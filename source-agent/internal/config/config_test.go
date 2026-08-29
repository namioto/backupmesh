package config

import (
	"strings"
	"testing"
)

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
