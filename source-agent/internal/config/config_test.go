package config

import (
	"strings"
	"testing"
)

func validConfig() Config {
	return Config{Agent: Agent{ID: "f91436ac-0ca9-4bcb-b0d0-42bc7181f611"}, Storage: Storage{ControlEndpoint: "https://storage.local:7443", RepositoryPasswordFile: "/run/secrets/restic-password"}, BackupSets: []BackupSet{{Name: "home", Paths: []string{"/home/user"}}}}
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
	for _, want := range []string{"agent.id", "controlEndpoint", "repositoryPasswordFile", "negative", "paths", "duplicated", "hook"} {
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
