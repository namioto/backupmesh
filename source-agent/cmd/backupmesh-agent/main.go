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
		return fmt.Errorf("usage: backupmesh-agent <validate|sync|backup|version>")
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
	if args[0] == "validate" {
		fmt.Println("configuration is valid")
		return nil
	}
	authToken, err := loadAuthenticationToken(cfg.Storage.AuthenticationTokenFile)
	if err != nil {
		return err
	}
	switch args[0] {
	case "sync":
		ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
		defer stop()
		api := controlapi.Client{BaseURL: strings.TrimRight(cfg.Storage.ControlEndpoint, "/") + "/api/v1", AuthToken: authToken}
		if err := publishCatalog(ctx, api, cfg); err != nil {
			return fmt.Errorf("publish Source catalog: %w", err)
		}
		fmt.Println("Source catalog synchronized")
		return nil
	case "backup":
		set, ok := cfg.FindBackupSet(*setName)
		if !ok {
			return fmt.Errorf("backup set %q not found", *setName)
		}
		ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
		defer stop()
		api := controlapi.Client{BaseURL: strings.TrimRight(cfg.Storage.ControlEndpoint, "/") + "/api/v1", AuthToken: authToken}
		if err := publishCatalog(ctx, api, cfg); err != nil {
			return fmt.Errorf("publish Source catalog: %w", err)
		}
		status, err := api.GetStorageStatus(ctx)
		if err != nil {
			return fmt.Errorf("check storage status: %w", err)
		}
		if status.State != "ready" {
			return fmt.Errorf("storage is not ready (state %q)", status.State)
		}
		targets, err := api.ListBackupTargets(ctx, cfg.Agent.ID, set.ID)
		if err != nil {
			return fmt.Errorf("list backup targets: %w", err)
		}
		readyTargets := make([]controlapi.BackupTargetAvailability, 0, len(targets))
		for _, target := range targets {
			if target.State == "READY" {
				readyTargets = append(readyTargets, target)
			}
		}
		if len(readyTargets) == 0 {
			return fmt.Errorf("no mapped backup target is ready")
		}
		if err := runHooks(ctx, set.Hooks.Before); err != nil {
			return fmt.Errorf("before hook: %w", err)
		}
		for _, target := range readyTargets {
			if err := runBackupTarget(ctx, api, cfg, set, target, *resticBinary); err != nil {
				return fmt.Errorf("backup target %s (%s): %w", target.DeviceName, target.DestinationFolder, err)
			}
		}
		if err := runHooks(ctx, set.Hooks.After); err != nil {
			return fmt.Errorf("after hook: %w", err)
		}
		fmt.Printf("backup complete on %d target(s)\n", len(readyTargets))
		return nil
	default:
		return fmt.Errorf("unknown command %q", args[0])
	}
}

func loadAuthenticationToken(path string) (string, error) {
	if strings.TrimSpace(path) == "" {
		return "", nil
	}
	b, err := os.ReadFile(path)
	if err != nil {
		return "", fmt.Errorf("read Control API authentication token: %w", err)
	}
	token := strings.TrimSpace(string(b))
	if len(token) < 32 {
		return "", fmt.Errorf("Control API authentication token must contain at least 32 characters")
	}
	return token, nil
}

func runBackupTarget(ctx context.Context, api controlapi.Client, cfg config.Config, set config.BackupSet, target controlapi.BackupTargetAvailability, resticBinary string) error {
	jobID, err := controlapi.UUIDv4()
	if err != nil {
		return fmt.Errorf("create job ID: %w", err)
	}
	requestKey, err := controlapi.UUIDv4()
	if err != nil {
		return fmt.Errorf("create idempotency key: %w", err)
	}
	admission, err := api.RequestBackup(ctx, requestKey, controlapi.BackupRequest{JobID: jobID, SourceAgentID: cfg.Agent.ID, BackupSetID: set.ID, TargetMappingID: target.MappingID, RequestedAt: time.Now().UTC()})
	if err != nil {
		return fmt.Errorf("request backup admission: %w", err)
	}
	if admission.State != "ACCEPTED" || admission.JobID != jobID || admission.TargetMappingID != target.MappingID {
		return fmt.Errorf("storage returned an invalid backup admission")
	}

	backupCtx, cancelBackup := context.WithCancel(ctx)
	defer cancelBackup()
	pollCtx, cancelPoll := context.WithCancel(ctx)
	defer cancelPoll()
	go pollCancellation(pollCtx, api, jobID, cancelBackup)
	adapter := restic.Adapter{Binary: resticBinary}
	backupRequest := engine.BackupRequest{Repository: admission.RepositoryEndpoint, PasswordFile: cfg.Storage.RepositoryPasswordFile, CacheDirectory: cfg.Storage.ResticCacheDirectory, Paths: set.Paths, Includes: set.Include, Excludes: set.Exclude, UploadLimitBPS: cfg.UploadLimitBPS}
	var sequence int64
	var reportErr error
	var result engine.Result
	backupErr := adapter.EnsureRepository(backupCtx, backupRequest)
	if backupErr == nil {
		result, backupErr = adapter.Backup(backupCtx, backupRequest, func(p engine.Progress) {
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
			fmt.Printf("%s progress %.1f%% (%d/%d bytes)\n", target.DeviceName, p.Percent*100, p.BytesDone, p.TotalBytes)
		})
	}
	cancelPoll()
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
	fmt.Printf("%s backup complete: snapshot %s\n", target.DeviceName, result.SnapshotID)
	return nil
}

func pollCancellation(ctx context.Context, api controlapi.Client, jobID string, cancel context.CancelFunc) {
	ticker := time.NewTicker(250 * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			status, err := api.GetBackupStatus(ctx, jobID)
			if err == nil && status.State == "CANCEL_REQUESTED" {
				cancel()
				return
			}
		}
	}
}

func publishCatalog(ctx context.Context, api controlapi.Client, cfg config.Config) error {
	key, err := controlapi.UUIDv4()
	if err != nil {
		return fmt.Errorf("create catalog idempotency key: %w", err)
	}
	sets := make([]controlapi.SourceCatalogBackupSet, 0, len(cfg.BackupSets))
	for _, set := range cfg.BackupSets {
		sets = append(sets, controlapi.SourceCatalogBackupSet{BackupSetID: set.ID, Name: set.Name, SourcePaths: append([]string(nil), set.Paths...)})
	}
	return api.PublishSourceCatalog(ctx, key, controlapi.SourceCatalog{SourceAgentID: cfg.Agent.ID, SourceAgentName: cfg.Agent.Name, UpdatedAt: time.Now().UTC(), BackupSets: sets})
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
