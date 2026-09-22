package main

import (
	"bufio"
	"bytes"
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
