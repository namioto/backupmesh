package config

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestLoadYAMLWithMultipleSourcePaths(t *testing.T) {
	path := filepath.Join(t.TempDir(), "backupmesh.yaml")
	contents := `agent:
  name: Home server
storage:
  controlEndpoint: https://storage.local:7443
  repositoryPasswordFile: /run/secrets/restic-password
backupSets:
  - name: home
    paths:
      - /home/user/Documents
      - /home/user/Pictures
`
	if err := os.WriteFile(path, []byte(contents), 0600); err != nil {
		t.Fatal(err)
	}
	c, err := Load(path)
	if err != nil {
		t.Fatalf("Load() error = %v", err)
	}
	if got := c.BackupSets[0].Paths; len(got) != 2 || got[1] != "/home/user/Pictures" {
		t.Fatalf("paths = %#v", got)
	}
	if !isUUID(c.Agent.ID) || !isUUID(c.BackupSets[0].ID) {
		t.Fatalf("identities were not generated: agent=%q set=%q", c.Agent.ID, c.BackupSets[0].ID)
	}
	reloaded, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	if reloaded.Agent.ID != c.Agent.ID || reloaded.BackupSets[0].ID != c.BackupSets[0].ID {
		t.Fatalf("generated identities were not stable: %#v, %#v", c, reloaded)
	}
}

func TestLoadUserConfigAcceptsAConfigWithNoStorageSectionYet(t *testing.T) {
	path := filepath.Join(t.TempDir(), "backupmesh.yaml")
	contents := `agent:
  name: New laptop
backupSets:
  - name: home
    paths:
      - /home/user/Documents
`
	if err := os.WriteFile(path, []byte(contents), 0600); err != nil {
		t.Fatal(err)
	}
	c, err := LoadUserConfig(path)
	if err != nil {
		t.Fatalf("LoadUserConfig() error = %v, want nil so `pair` can run before a Storage endpoint is known", err)
	}
	if !isUUID(c.Agent.ID) || !isUUID(c.BackupSets[0].ID) {
		t.Fatalf("identities were not generated: agent=%q set=%q", c.Agent.ID, c.BackupSets[0].ID)
	}
	if _, err := Load(path); err == nil {
		t.Fatal("Load() expected an error for a config missing storage.controlEndpoint, since pair has not run yet")
	}
}

func TestLoadYAMLRejectsUnknownFields(t *testing.T) {
	path := filepath.Join(t.TempDir(), "backupmesh.yml")
	if err := os.WriteFile(path, []byte("unknown: true\n"), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := Load(path); err == nil || !strings.Contains(err.Error(), "field unknown") {
		t.Fatalf("Load() error = %v", err)
	}
}

func validConfig() Config {
	return Config{Agent: Agent{ID: "f91436ac-0ca9-4bcb-b0d0-42bc7181f611", Name: "Home server"}, Storage: Storage{ControlEndpoint: "https://storage.local:7443", RepositoryPasswordFile: "/run/secrets/restic-password"}, BackupSets: []BackupSet{{ID: "7d750726-97ab-4f81-9f09-f06c34f524d1", Name: "home", Paths: []string{"/home/user"}}}}
}

func TestValidateValid(t *testing.T) {
	if err := validConfig().Validate(); err != nil {
		t.Fatalf("Validate() error = %v", err)
	}
}

func TestValidateReportsMultipleProblems(t *testing.T) {
	c := Config{UploadLimitBPS: -1, BackupSets: []BackupSet{{Name: "x"}, {Name: "x", Paths: []string{"/tmp"}, Hooks: Hooks{Before: [][]string{{}}}}}}
	err := c.Validate()
	if err == nil {
		t.Fatal("Validate() expected error")
	}
	for _, want := range []string{"agent.id", "agent.name", "controlEndpoint", "repositoryPasswordFile", "negative", "paths", "duplicated", "hook"} {
		if !strings.Contains(err.Error(), want) {
			t.Errorf("error %q does not contain %q", err, want)
		}
	}
}

func TestFindBackupSet(t *testing.T) {
	c := validConfig()
	set, ok := c.FindBackupSet("home")
	if !ok || set.Name != "home" {
		t.Fatalf("FindBackupSet() = %#v, %v", set, ok)
	}
}

func TestValidateRejectsRelativeResticCacheDirectory(t *testing.T) {
	c := validConfig()
	c.Storage.ResticCacheDirectory = "relative/cache"
	if err := c.Validate(); err == nil || !strings.Contains(err.Error(), "resticCacheDirectory") {
		t.Fatalf("Validate() error = %v", err)
	}
}

func TestValidateRejectsRelativeAuthenticationTokenFile(t *testing.T) {
	c := validConfig()
	c.Storage.AuthenticationTokenFile = "relative/token"
	if err := c.Validate(); err == nil || !strings.Contains(err.Error(), "authenticationTokenFile") {
		t.Fatalf("Validate() error = %v", err)
	}
}

func TestValidateRequiresCompleteAbsoluteMTLSConfiguration(t *testing.T) {
	c := validConfig()
	c.Storage.TLSCAFile = "/etc/backupmesh/ca.pem"
	if err := c.Validate(); err == nil || !strings.Contains(err.Error(), "configured together") {
		t.Fatalf("Validate() partial mTLS error = %v", err)
	}
	c.Storage.TLSCertificateFile = "/etc/backupmesh/source.crt"
	c.Storage.TLSKeyFile = "relative.key"
	if err := c.Validate(); err == nil || !strings.Contains(err.Error(), "absolute") {
		t.Fatalf("Validate() relative mTLS error = %v", err)
	}
}
