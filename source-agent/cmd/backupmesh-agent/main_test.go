package main

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json"
	"encoding/pem"
	"errors"
	"math/big"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/namioto/backupmesh/source-agent/internal/config"
	"github.com/namioto/backupmesh/source-agent/internal/controlapi"
)

func TestApplyPairingWritesIdentityAndProtectedFiles(t *testing.T) {
	directory := t.TempDir()
	configPath := filepath.Join(directory, "backupmesh.json")
	bundlePath := filepath.Join(directory, "bundle.json")
	outputPath := filepath.Join(directory, "secrets")
	agentID := "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"
	configuration := `{"agent":{"id":"11111111-1111-4111-8111-111111111111","name":"source"},"storage":{"controlEndpoint":"https://storage:7444","repositoryPasswordFile":"/var/lib/backupmesh/password"},"backupSets":[{"id":"22222222-2222-4222-8222-222222222222","name":"docs","paths":["/data"]}]}`
	if err := os.WriteFile(configPath, []byte(configuration), 0600); err != nil {
		t.Fatal(err)
	}
	certPEM, keyPEM := testCertificate(t, agentID)
	bundle, _ := json.Marshal(pairingBundleFile{AgentID: agentID, ControlEndpoint: "https://storage.example:7443", Credential: strings.Repeat("x", 43), CertificatePEM: certPEM, PrivateKeyPEM: keyPEM, AuthorityPEM: certPEM, ExpiresAt: time.Now().Add(time.Hour).Format(time.RFC3339), IssuedAt: time.Now().Format(time.RFC3339)})
	if err := os.WriteFile(bundlePath, bundle, 0600); err != nil {
		t.Fatal(err)
	}
	if err := applyPairing(configPath, bundlePath, outputPath); err != nil {
		t.Fatal(err)
	}
	cfg, err := config.Load(configPath)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Agent.ID != agentID || cfg.Storage.ControlEndpoint != "https://storage.example:7443" || cfg.Storage.TLSKeyFile != filepath.Join(outputPath, "source.key") {
		t.Fatalf("pairing was not applied: %+v", cfg)
	}
	for _, name := range []string{"control.token", "source.crt", "source.key", "storage-ca.pem"} {
		info, err := os.Stat(filepath.Join(outputPath, name))
		if err != nil {
			t.Fatal(err)
		}
		if runtime.GOOS != "windows" && info.Mode().Perm()&0077 != 0 {
			t.Fatalf("%s permissions = %o", name, info.Mode().Perm())
		}
	}
}

func testCertificate(t *testing.T, commonName string) (string, string) {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: commonName}, NotBefore: time.Now().Add(-time.Minute), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageDigitalSignature}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	return string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})), string(pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: x509.MarshalPKCS1PrivateKey(key)}))
}

func TestRunBackupTargetsContinuesAfterFailure(t *testing.T) {
	targets := []controlapi.BackupTargetAvailability{
		{DeviceName: "First", DestinationFolder: "one"},
		{DeviceName: "Second", DestinationFolder: "two"},
	}
	var attempted []string
	var attemptedGate sync.Mutex
	err := runBackupTargets(targets, func(target controlapi.BackupTargetAvailability) error {
		attemptedGate.Lock()
		attempted = append(attempted, target.DeviceName)
		attemptedGate.Unlock()
		if target.DeviceName == "First" {
			return errors.New("offline")
		}
		return nil
	})

	if len(attempted) != 2 {
		t.Fatalf("attempted targets = %v, want both targets", attempted)
	}
	if err == nil || !strings.Contains(err.Error(), "First (one): offline") {
		t.Fatalf("error = %v, want contextual first-target failure", err)
	}
}

func TestRunBackupTargetsStartsAllTargetsConcurrently(t *testing.T) {
	targets := []controlapi.BackupTargetAvailability{{DeviceName: "First"}, {DeviceName: "Second"}}
	started := make(chan string, len(targets))
	release := make(chan struct{})
	done := make(chan error, 1)
	go func() {
		done <- runBackupTargets(targets, func(target controlapi.BackupTargetAvailability) error {
			started <- target.DeviceName
			<-release
			return nil
		})
	}()
	for range targets {
		select {
		case <-started:
		case <-time.After(2 * time.Second):
			t.Fatal("backup targets did not start concurrently")
		}
	}
	close(release)
	if err := <-done; err != nil {
		t.Fatal(err)
	}
}

func TestRunBackupTargetsAggregatesFailures(t *testing.T) {
	targets := []controlapi.BackupTargetAvailability{
		{DeviceName: "First", DestinationFolder: "one"},
		{DeviceName: "Second", DestinationFolder: "two"},
	}
	err := runBackupTargets(targets, func(target controlapi.BackupTargetAvailability) error {
		return errors.New("failed " + target.DeviceName)
	})

	if err == nil || !strings.Contains(err.Error(), "failed First") || !strings.Contains(err.Error(), "failed Second") {
		t.Fatalf("error = %v, want both target failures", err)
	}
}

func TestExecuteSourceCommandReportsFailure(t *testing.T) {
	var completed controlapi.BackupCommandResult
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/backup/commands/result":
			if err := json.NewDecoder(r.Body).Decode(&completed); err != nil {
				t.Fatal(err)
			}
			w.WriteHeader(http.StatusNoContent)
		default:
			t.Fatalf("unexpected path %q", r.URL.Path)
		}
	}))
	defer srv.Close()

	cfg := config.Config{Agent: config.Agent{ID: "source-1"}, BackupSets: []config.BackupSet{{ID: "set-1", Name: "docs", Paths: []string{"/data"}}}}
	api := controlapi.Client{BaseURL: srv.URL}
	err := executeSourceCommand(context.Background(), api, cfg, controlapi.BackupCommand{CommandID: "command-1", BackupSetID: "missing-set"}, "restic")

	if err == nil || !strings.Contains(err.Error(), `backup set "missing-set" not found`) {
		t.Fatalf("error = %v, want missing-set failure", err)
	}
	if completed.Outcome != "FAILED" || !strings.Contains(completed.Message, "not found") {
		t.Fatalf("completed = %#v", completed)
	}
}

func TestRunSourceCommandRequiresReadyMapping(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/backup/targets/source-1/set-1" {
			t.Fatalf("unexpected path %q", r.URL.Path)
		}
		_, _ = w.Write([]byte(`[{"mappingId":"mapping-other","deviceId":"device-1","backupSetId":"set-1","deviceName":"Local C","destinationFolder":"C:\\BackupMesh","state":"READY"}]`))
	}))
	defer srv.Close()

	cfg := config.Config{Agent: config.Agent{ID: "source-1"}, BackupSets: []config.BackupSet{{ID: "set-1", Name: "docs", Paths: []string{"/data"}}}}
	command := controlapi.BackupCommand{CommandID: "command-1", BackupSetID: "set-1", TargetMappingID: "mapping-1", JobID: "job-1"}
	_, err := runSourceCommand(context.Background(), controlapi.Client{BaseURL: srv.URL}, cfg, command, "restic")

	if err == nil || !strings.Contains(err.Error(), `mapped backup target "mapping-1" is not ready`) {
		t.Fatalf("error = %v, want not-ready mapping failure", err)
	}
}

func TestPollCancellationCancelsRunningBackup(t *testing.T) {
	requested := make(chan struct{}, 1)
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/backup/status/job-1" {
			t.Fatalf("unexpected path %q", r.URL.Path)
		}
		requested <- struct{}{}
		_, _ = w.Write([]byte(`{"jobId":"job-1","state":"CANCEL_REQUESTED"}`))
	}))
	defer srv.Close()

	pollCtx, stop := context.WithCancel(context.Background())
	defer stop()
	backupCtx, cancelBackup := context.WithCancel(context.Background())
	go pollCancellation(pollCtx, controlapi.Client{BaseURL: srv.URL}, "job-1", cancelBackup)

	select {
	case <-backupCtx.Done():
	case <-time.After(2 * time.Second):
		t.Fatal("backup was not cancelled after Storage requested cancellation")
	}
	select {
	case <-requested:
	default:
		t.Fatal("Storage status was not polled")
	}
}
