package main

import (
	"bufio"
	"bytes"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/namioto/backupmesh/source-agent/internal/config"
)

func TestAppendSetupFoldersPreservesConfiguration(t *testing.T) {
	directory := t.TempDir()
	configPath := filepath.Join(directory, "backupmesh.json")
	newFolder := filepath.Join(directory, "photos")
	secret := func(name string) string { return filepath.Join(directory, name) }
	original := config.Config{
		Agent:          config.Agent{ID: "11111111-1111-4111-8111-111111111111", Name: "laptop"},
		Storage:        config.Storage{ControlEndpoint: "https://storage.test:7443", RepositoryPasswordFile: secret("repository"), AuthenticationTokenFile: secret("token"), TLSCAFile: secret("ca"), TLSCertificateFile: secret("cert"), TLSKeyFile: secret("key")},
		BackupSets:     []config.BackupSet{{ID: "22222222-2222-4222-8222-222222222222", Name: "docs", Paths: []string{filepath.Join(directory, "docs")}, Exclude: []string{"*.tmp"}}},
		UploadLimitBPS: 1234,
	}
	encoded, err := config.Marshal(original, configPath)
	if err != nil {
		t.Fatal(err)
	}
	if err := writePrivateFile(configPath, encoded); err != nil {
		t.Fatal(err)
	}
	if err := appendSetupFolders(configPath, []string{newFolder}); err != nil {
		t.Fatal(err)
	}
	cfg, err := config.Load(configPath)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Agent != original.Agent || cfg.Storage != original.Storage || cfg.UploadLimitBPS != original.UploadLimitBPS {
		t.Fatalf("existing config changed: %#v", cfg)
	}
	if len(cfg.BackupSets) != 2 || cfg.BackupSets[0].ID != "22222222-2222-4222-8222-222222222222" || cfg.BackupSets[0].Exclude[0] != "*.tmp" || cfg.BackupSets[1].Paths[0] != newFolder {
		t.Fatalf("backup sets = %#v", cfg.BackupSets)
	}
}

func TestPromptPairingDecisionRequiresExplicitYes(t *testing.T) {
	pending := []pendingNearbyRequest{
		{RequestID: "11111111-1111-4111-8111-111111111111", ComparisonCode: "123456", ExpiresAt: time.Now().Add(time.Minute)},
		{RequestID: "22222222-2222-4222-8222-222222222222", ComparisonCode: "654321", ExpiresAt: time.Now().Add(time.Minute)},
	}
	for _, test := range []struct {
		input    string
		approved bool
		rejected bool
	}{
		{"2\nyes\n", true, false},
		{"2\n\n", false, true},
		{"2\nno\n", false, true},
		{"2\n", false, false},
	} {
		selected, approved, rejected, err := promptPairingDecision(bufio.NewReader(strings.NewReader(test.input)), &bytes.Buffer{}, pending)
		if err != nil {
			t.Fatal(err)
		}
		if selected.RequestID != pending[1].RequestID || approved != test.approved || rejected != test.rejected {
			t.Fatalf("input %q selected %q, approved %v, rejected %v", test.input, selected.RequestID, approved, rejected)
		}
	}
}

func TestResetSetupPairingKeepsBackupConfigurationAndCreatesFreshIdentity(t *testing.T) {
	directory := t.TempDir()
	configPath := filepath.Join(directory, "backupmesh.json")
	pairingDirectory := filepath.Join(directory, "pairing")
	secret := func(name string) string { return filepath.Join(pairingDirectory, name) }
	repositoryPassword := filepath.Join(directory, "repository-password")
	externalCA := filepath.Join(directory, "shared-storage-ca.pem")
	original := config.Config{
		Agent:          config.Agent{ID: "11111111-1111-4111-8111-111111111111", Name: "laptop"},
		Storage:        config.Storage{ControlEndpoint: "https://storage.test:7443", RepositoryPasswordFile: repositoryPassword, AuthenticationTokenFile: secret("control.token"), TLSCAFile: externalCA, TLSCertificateFile: secret("source.crt"), TLSKeyFile: secret("source.key")},
		BackupSets:     []config.BackupSet{{ID: "22222222-2222-4222-8222-222222222222", Name: "docs", Paths: []string{"/data/docs"}, Exclude: []string{"*.tmp"}}},
		UploadLimitBPS: 1234,
	}
	encoded, err := config.Marshal(original, configPath)
	if err != nil {
		t.Fatal(err)
	}
	if err := writePrivateFile(configPath, encoded); err != nil {
		t.Fatal(err)
	}
	if err := config.SaveIdentityState(configPath, original); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(pairingDirectory, 0700); err != nil {
		t.Fatal(err)
	}
	for _, path := range []string{repositoryPassword, externalCA, secret("control.token"), secret("storage-ca.pem"), secret("source.crt"), secret("source.key"), secret("nearby-identity.pem")} {
		if err := writePrivateFile(path, []byte("secret")); err != nil {
			t.Fatal(err)
		}
	}

	if err := resetSetupPairing(configPath, pairingDirectory); err != nil {
		t.Fatal(err)
	}
	cfg, err := config.LoadUserConfig(configPath)
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Agent.ID == original.Agent.ID || cfg.Agent.ID == "" {
		t.Fatalf("agent identity was not replaced: %q", cfg.Agent.ID)
	}
	if cfg.Agent.Name != original.Agent.Name || cfg.Storage.RepositoryPasswordFile != repositoryPassword || cfg.UploadLimitBPS != original.UploadLimitBPS || len(cfg.BackupSets) != 1 || cfg.BackupSets[0].ID != original.BackupSets[0].ID || cfg.BackupSets[0].Exclude[0] != "*.tmp" {
		t.Fatalf("backup configuration changed: %#v", cfg)
	}
	if cfg.Storage.ControlEndpoint != "" || cfg.Storage.AuthenticationTokenFile != "" || cfg.Storage.TLSCAFile != "" || cfg.Storage.TLSCertificateFile != "" || cfg.Storage.TLSKeyFile != "" {
		t.Fatalf("old connection remains: %#v", cfg.Storage)
	}
	if _, err := os.Stat(repositoryPassword); err != nil {
		t.Fatalf("repository password was removed: %v", err)
	}
	if _, err := os.Stat(externalCA); err != nil {
		t.Fatalf("external CA was removed: %v", err)
	}
	for _, path := range []string{secret("control.token"), secret("storage-ca.pem"), secret("source.crt"), secret("source.key"), secret("nearby-identity.pem")} {
		if _, err := os.Stat(path); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("old pairing file remains: %s", path)
		}
	}
}

func TestResetSetupPairingRejectsRepositoryPasswordCleanupOverlap(t *testing.T) {
	directory := t.TempDir()
	configPath := filepath.Join(directory, "backupmesh.json")
	pairingDirectory := filepath.Join(directory, "pairing")
	password := filepath.Join(pairingDirectory, "control.token")
	original := config.Config{
		Agent:      config.Agent{ID: "11111111-1111-4111-8111-111111111111", Name: "laptop"},
		Storage:    config.Storage{ControlEndpoint: "https://storage.test:7443", RepositoryPasswordFile: password, AuthenticationTokenFile: password},
		BackupSets: []config.BackupSet{{ID: "22222222-2222-4222-8222-222222222222", Name: "docs", Paths: []string{"/data/docs"}}},
	}
	encoded, err := config.Marshal(original, configPath)
	if err != nil {
		t.Fatal(err)
	}
	if err := writePrivateFile(configPath, encoded); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(pairingDirectory, 0700); err != nil {
		t.Fatal(err)
	}
	if err := writePrivateFile(password, []byte("keep-me")); err != nil {
		t.Fatal(err)
	}

	if err := resetSetupPairing(configPath, pairingDirectory); err == nil || !strings.Contains(err.Error(), "overlaps") {
		t.Fatalf("reset error = %v, want overlap rejection", err)
	}
	contents, err := os.ReadFile(password)
	if err != nil || string(contents) != "keep-me" {
		t.Fatalf("repository password changed: %q, %v", contents, err)
	}
	cfg, err := config.LoadUserConfig(configPath)
	if err != nil || cfg.Agent.ID != original.Agent.ID || cfg.Storage.ControlEndpoint != original.Storage.ControlEndpoint {
		t.Fatalf("configuration changed before rejection: %#v, %v", cfg, err)
	}
}
