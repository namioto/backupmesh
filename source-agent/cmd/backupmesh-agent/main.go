package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"os/signal"
	"strings"
	"time"

	"github.com/namioto/backupmesh/source-agent/internal/config"
	"github.com/namioto/backupmesh/source-agent/internal/controlapi"
	"github.com/namioto/backupmesh/source-agent/internal/engine"
	"github.com/namioto/backupmesh/source-agent/internal/restic"
)

const version = "0.1.0-dev"

func main() {
	if err := run(os.Args[1:]); err != nil {
		fmt.Fprintln(os.Stderr, "error:", err)
		os.Exit(1)
	}
}

func run(args []string) error {
	if len(args) == 0 {
		return fmt.Errorf("usage: backupmesh-agent <validate|backup|version>")
	}
	if args[0] == "version" {
		fmt.Println(version)
		return nil
	}
	fs := flag.NewFlagSet(args[0], flag.ContinueOnError)
	configPath := fs.String("config", "backupmesh.json", "path to configuration")
	setName := fs.String("set", "", "backup set name")
	resticBinary := fs.String("restic", "restic", "path to bundled restic binary")
	if err := fs.Parse(args[1:]); err != nil {
		return err
	}
	cfg, err := config.Load(*configPath)
	if err != nil {
		return err
	}
	switch args[0] {
	case "validate":
		fmt.Println("configuration is valid")
		return nil
	case "backup":
		set, ok := cfg.FindBackupSet(*setName)
		if !ok {
			return fmt.Errorf("backup set %q not found", *setName)
		}
		ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
		defer stop()
		api := controlapi.Client{BaseURL: strings.TrimRight(cfg.Storage.ControlEndpoint, "/") + "/api/v1"}
		status, err := api.GetStorageStatus(ctx)
		if err != nil {
			return fmt.Errorf("check storage status: %w", err)
		}
		if status.State != "ready" {
			return fmt.Errorf("storage is not ready (state %q)", status.State)
		}
		jobID, err := controlapi.UUIDv4()
		if err != nil {
			return fmt.Errorf("create job ID: %w", err)
		}
		requestKey, err := controlapi.UUIDv4()
		if err != nil {
			return fmt.Errorf("create idempotency key: %w", err)
		}
		admission, err := api.RequestBackup(ctx, requestKey, controlapi.BackupRequest{JobID: jobID, SourceAgentID: cfg.Agent.ID, RequestedAt: time.Now().UTC(), Repository: set.Name})
		if err != nil {
			return fmt.Errorf("request backup admission: %w", err)
		}
		if admission.State != "ACCEPTED" || admission.JobID != jobID {
			return fmt.Errorf("storage returned an invalid backup admission")
		}
		if err := runHooks(ctx, set.Hooks.Before); err != nil {
			eventID, idErr := controlapi.UUIDv4()
			if idErr != nil {
				return fmt.Errorf("before hook failed and result event ID creation failed: %w", idErr)
			}
			report := controlapi.BackupResult{EventID: eventID, JobID: jobID, Sequence: 1, CompletedAt: time.Now().UTC(), Outcome: "FAILED", ErrorCode: "BEFORE_HOOK_FAILED", Message: "before hook failed"}
			if reportErr := api.ReportResult(ctx, eventID, report); reportErr != nil {
				return fmt.Errorf("before hook failed and result reporting failed: %w", reportErr)
			}
			return fmt.Errorf("before hook: %w", err)
		}
		backupCtx, cancelBackup := context.WithCancel(ctx)
		defer cancelBackup()
		var sequence int64
		var reportErr error
		result, backupErr := (restic.Adapter{Binary: *resticBinary}).Backup(backupCtx, engine.BackupRequest{Repository: admission.RepositoryEndpoint, PasswordFile: cfg.Storage.RepositoryPasswordFile, Paths: set.Paths, Includes: set.Include, Excludes: set.Exclude, UploadLimitBPS: cfg.UploadLimitBPS}, func(p engine.Progress) {
			sequence++
			eventID, idErr := controlapi.UUIDv4()
			if idErr != nil {
				reportErr = idErr
				cancelBackup()
				return
			}
			reportErr = api.ReportProgress(backupCtx, eventID, controlapi.BackupProgress{EventID: eventID, JobID: jobID, Sequence: sequence, ReportedAt: time.Now().UTC(), Phase: "UPLOADING", BytesDone: p.BytesDone, BytesTotal: p.TotalBytes, FilesDone: p.FilesDone, FilesTotal: p.TotalFiles})
			if reportErr != nil {
				cancelBackup()
				return
			}
			fmt.Printf("progress %.1f%% (%d/%d bytes)\n", p.Percent*100, p.BytesDone, p.TotalBytes)
		})
		if reportErr != nil {
			backupErr = fmt.Errorf("report backup progress: %w", reportErr)
		}
		sequence++
		resultEventID, idErr := controlapi.UUIDv4()
		if idErr != nil {
			return fmt.Errorf("create result event ID: %w", idErr)
		}
		apiResult := controlapi.BackupResult{EventID: resultEventID, JobID: jobID, Sequence: sequence, CompletedAt: time.Now().UTC()}
		if backupErr == nil {
			apiResult.Outcome, apiResult.SnapshotID, apiResult.BytesAdded = "SUCCEEDED", result.SnapshotID, result.DataAdded
		} else if errors.Is(backupErr, context.Canceled) {
			apiResult.Outcome, apiResult.ErrorCode, apiResult.Message = "CANCELLED", "CANCELLED", "backup was cancelled"
		} else {
			apiResult.Outcome, apiResult.ErrorCode, apiResult.Message = "FAILED", "BACKUP_ENGINE_FAILED", "backup engine failed"
		}
		reportCtx := ctx
		var stopReport context.CancelFunc = func() {}
		if ctx.Err() != nil {
			reportCtx, stopReport = context.WithTimeout(context.Background(), 5*time.Second)
		}
		defer stopReport()
		if err := api.ReportResult(reportCtx, resultEventID, apiResult); err != nil {
			return fmt.Errorf("report backup result: %w", err)
		}
		if backupErr != nil {
			return backupErr
		}
		if err := runHooks(ctx, set.Hooks.After); err != nil {
			return fmt.Errorf("after hook: %w", err)
		}
		fmt.Printf("backup complete: snapshot %s\n", result.SnapshotID)
		return nil
	default:
		return fmt.Errorf("unknown command %q", args[0])
	}
}

func runHooks(ctx context.Context, hooks [][]string) error {
	for _, hook := range hooks {
		cmd := exec.CommandContext(ctx, hook[0], hook[1:]...)
		cmd.Stdout, cmd.Stderr = os.Stdout, os.Stderr
		if err := cmd.Run(); err != nil {
			return err
		}
	}
	return nil
}
