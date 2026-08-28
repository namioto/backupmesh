package main

import (
	"context"
	"crypto/x509"
	"encoding/json"
	"encoding/pem"
	"errors"
	"flag"
	"fmt"
	"net/http"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
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
		return fmt.Errorf("usage: backupmesh-agent <apply-pairing|validate|sync|backup|version>")
	}
	if args[0] == "version" {
		fmt.Println(version)
		return nil
	}
	fs := flag.NewFlagSet(args[0], flag.ContinueOnError)
	configPath := fs.String("config", "backupmesh.json", "path to configuration")
	setName := fs.String("set", "", "backup set name")
	resticBinary := fs.String("restic", "restic", "path to bundled restic binary")
	pairingBundle := fs.String("bundle", "backupmesh-pairing.json", "path to pairing bundle")
	pairingOutput := fs.String("output", "", "directory for protected pairing files")
	if err := fs.Parse(args[1:]); err != nil {
		return err
	}
	if args[0] == "apply-pairing" {
		return applyPairing(*configPath, *pairingBundle, *pairingOutput)
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
	httpClient, err := loadMTLSClient(cfg.Storage)
	if err != nil {
		return err
	}
	switch args[0] {
	case "sync":
		ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
		defer stop()
		api := controlapi.Client{BaseURL: strings.TrimRight(cfg.Storage.ControlEndpoint, "/") + "/api/v1", AuthToken: authToken, AgentID: cfg.Agent.ID, HTTPClient: httpClient}
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
		api := controlapi.Client{BaseURL: strings.TrimRight(cfg.Storage.ControlEndpoint, "/") + "/api/v1", AuthToken: authToken, AgentID: cfg.Agent.ID, HTTPClient: httpClient}
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
		backupErr := runBackupTargets(readyTargets, func(target controlapi.BackupTargetAvailability) error {
			return runBackupTarget(ctx, api, cfg, set, target, *resticBinary)
		})
		if err := runHooks(ctx, set.Hooks.After); err != nil {
			backupErr = errors.Join(backupErr, fmt.Errorf("after hook: %w", err))
		}
		if backupErr != nil {
			return backupErr
		}
		fmt.Printf("backup complete on %d target(s)\n", len(readyTargets))
		return nil
	default:
		return fmt.Errorf("unknown command %q", args[0])
	}
}

type pairingBundleFile struct {
	AgentID         string `json:"agent_id"`
	ControlEndpoint string `json:"control_endpoint"`
	Credential      string `json:"credential"`
	CertificatePEM  string `json:"certificate_pem"`
	PrivateKeyPEM   string `json:"private_key_pem"`
	AuthorityPEM    string `json:"authority_pem"`
	ExpiresAt       string `json:"expires_at"`
	IssuedAt        string `json:"issued_at"`
}

func applyPairing(configPath, bundlePath, outputDirectory string) error {
	cfg, err := config.Load(configPath)
	if err != nil {
		return err
	}
	b, err := os.ReadFile(filepath.Clean(bundlePath))
	if err != nil {
		return fmt.Errorf("read pairing bundle: %w", err)
	}
	var bundle pairingBundleFile
	decoder := json.NewDecoder(strings.NewReader(string(b)))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&bundle); err != nil {
		return fmt.Errorf("decode pairing bundle: %w", err)
	}
	certBlock, _ := pem.Decode([]byte(bundle.CertificatePEM))
	caBlock, _ := pem.Decode([]byte(bundle.AuthorityPEM))
	keyBlock, _ := pem.Decode([]byte(bundle.PrivateKeyPEM))
	if certBlock == nil || caBlock == nil || keyBlock == nil || len(strings.TrimSpace(bundle.Credential)) < 32 {
		return errors.New("pairing bundle is incomplete")
	}
	certificate, err := x509.ParseCertificate(certBlock.Bytes)
	if err != nil {
		return fmt.Errorf("parse pairing certificate: %w", err)
	}
	if certificate.Subject.CommonName != bundle.AgentID {
		return errors.New("pairing certificate identity does not match agent_id")
	}
	if outputDirectory == "" {
		outputDirectory = filepath.Join(filepath.Dir(configPath), "pairing")
	}
	outputDirectory, err = filepath.Abs(outputDirectory)
	if err != nil {
		return fmt.Errorf("resolve pairing output: %w", err)
	}
	if err := os.MkdirAll(outputDirectory, 0700); err != nil {
		return fmt.Errorf("create pairing output: %w", err)
	}
	files := []struct{ name, content string }{
		{"control.token", strings.TrimSpace(bundle.Credential) + "\n"},
		{"source.crt", bundle.CertificatePEM}, {"source.key", bundle.PrivateKeyPEM}, {"storage-ca.pem", bundle.AuthorityPEM},
	}
	for _, file := range files {
		if err := writePrivateFile(filepath.Join(outputDirectory, file.name), []byte(file.content)); err != nil {
			return err
		}
	}
	cfg.Agent.ID = bundle.AgentID
	cfg.Storage.ControlEndpoint = bundle.ControlEndpoint
	cfg.Storage.AuthenticationTokenFile = filepath.Join(outputDirectory, "control.token")
	cfg.Storage.TLSCertificateFile = filepath.Join(outputDirectory, "source.crt")
	cfg.Storage.TLSKeyFile = filepath.Join(outputDirectory, "source.key")
	cfg.Storage.TLSCAFile = filepath.Join(outputDirectory, "storage-ca.pem")
	if err := cfg.Validate(); err != nil {
		return fmt.Errorf("paired configuration: %w", err)
	}
	encoded, err := json.MarshalIndent(cfg, "", "  ")
	if err != nil {
		return fmt.Errorf("encode paired configuration: %w", err)
	}
	if err := writePrivateFile(configPath, append(encoded, '\n')); err != nil {
		return err
	}
	fmt.Printf("pairing applied for Source Agent %s\n", bundle.AgentID)
	return nil
}

func writePrivateFile(path string, contents []byte) error {
	temporary := path + ".tmp"
	if err := os.WriteFile(temporary, contents, 0600); err != nil {
		return fmt.Errorf("write %s: %w", filepath.Base(path), err)
	}
	if err := os.Rename(temporary, path); err != nil {
		if removeErr := os.Remove(path); removeErr != nil && !errors.Is(removeErr, os.ErrNotExist) {
			_ = os.Remove(temporary)
			return fmt.Errorf("replace %s: %w", filepath.Base(path), err)
		}
		if retryErr := os.Rename(temporary, path); retryErr != nil {
			_ = os.Remove(temporary)
			return fmt.Errorf("replace %s: %w", filepath.Base(path), retryErr)
		}
	}
	if err := os.Chmod(path, 0600); err != nil {
		return fmt.Errorf("protect %s: %w", filepath.Base(path), err)
	}
	return nil
}

func loadMTLSClient(storage config.Storage) (*http.Client, error) {
	if strings.TrimSpace(storage.TLSCAFile) == "" {
		return nil, nil
	}
	return controlapi.NewMTLSHTTPClient(storage.TLSCAFile, storage.TLSCertificateFile, storage.TLSKeyFile)
}

func runBackupTargets(targets []controlapi.BackupTargetAvailability, runTarget func(controlapi.BackupTargetAvailability) error) error {
	var failures []error
	for _, target := range targets {
		if err := runTarget(target); err != nil {
			failures = append(failures, fmt.Errorf("backup target %s (%s): %w", target.DeviceName, target.DestinationFolder, err))
		}
	}
	return errors.Join(failures...)
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
