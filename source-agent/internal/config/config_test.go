package config

import (
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
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

func TestResolveIdentityStateReusesIDAcrossARenameAlone(t *testing.T) {
	path := filepath.Join(t.TempDir(), "backupmesh.json")
	original := `{"agent":{"name":"Home server"},"storage":{"controlEndpoint":"https://storage.local:7443","repositoryPasswordFile":"/run/secrets/restic-password"},"backupSets":[{"name":"alpha","paths":["/data/alpha"]}]}`
	if err := os.WriteFile(path, []byte(original), 0600); err != nil {
		t.Fatal(err)
	}
	first, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	renamed := `{"agent":{"name":"Home server"},"storage":{"controlEndpoint":"https://storage.local:7443","repositoryPasswordFile":"/run/secrets/restic-password"},"backupSets":[{"name":"alpha-renamed","paths":["/data/alpha"]}]}`
	if err := os.WriteFile(path, []byte(renamed), 0600); err != nil {
		t.Fatal(err)
	}
	second, err := Load(path)
	if err != nil {
		t.Fatal(err)
	}
	if second.BackupSets[0].ID != first.BackupSets[0].ID {
		t.Fatalf("a rename alone should keep the same backup set ID: %q != %q", second.BackupSets[0].ID, first.BackupSets[0].ID)
	}
}

func TestResolveIdentityStateRejectsAnAmbiguousSimultaneousRenameAndPathChange(t *testing.T) {
	path := filepath.Join(t.TempDir(), "backupmesh.json")
	original := `{"agent":{"name":"Home server"},"storage":{"controlEndpoint":"https://storage.local:7443","repositoryPasswordFile":"/run/secrets/restic-password"},"backupSets":[{"name":"alpha","paths":["/data/alpha"]},{"name":"beta","paths":["/data/beta"]}]}`
	if err := os.WriteFile(path, []byte(original), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := Load(path); err != nil {
		t.Fatal(err)
	}
	// "alpha" now points at what used to be beta's paths: the name matches one saved backup set and
	// the paths match a different one, so which existing history this is cannot be inferred safely.
	ambiguous := `{"agent":{"name":"Home server"},"storage":{"controlEndpoint":"https://storage.local:7443","repositoryPasswordFile":"/run/secrets/restic-password"},"backupSets":[{"name":"alpha","paths":["/data/beta"]},{"name":"beta","paths":["/data/beta-new"]}]}`
	if err := os.WriteFile(path, []byte(ambiguous), 0600); err != nil {
		t.Fatal(err)
	}
	_, err := Load(path)
	if err == nil {
		t.Fatal("Load() expected an error for an ambiguous simultaneous rename and path change")
	}
	if !strings.Contains(err.Error(), "matches more than one previously known backup set") {
		t.Fatalf("Load() error = %v, want an ambiguous-match error", err)
	}
}

func TestSaveIdentityStateIsOwnerOnlyAndHasNoLeftoverTemporaryFile(t *testing.T) {
	directory := t.TempDir()
	path := filepath.Join(directory, "backupmesh.json")
	statePath := path + ".state.json"
	c := Config{Agent: Agent{ID: newUUID(), Name: "Home server"}, BackupSets: []BackupSet{{ID: newUUID(), Name: "alpha", Paths: []string{"/data/alpha"}}}}
	if err := SaveIdentityState(path, c); err != nil {
		t.Fatal(err)
	}
	// Overwrite it to exercise the rename-over-an-existing-file path, not just initial creation.
	c.BackupSets[0].Name = "alpha-renamed"
	if err := SaveIdentityState(path, c); err != nil {
		t.Fatal(err)
	}
	info, err := os.Stat(statePath)
	if err != nil {
		t.Fatal(err)
	}
	if runtime.GOOS != "windows" && info.Mode().Perm() != 0600 {
		t.Fatalf("%s permissions = %o, want 0600", statePath, info.Mode().Perm())
	}
	reloaded, err := os.ReadFile(statePath)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(reloaded), "alpha-renamed") {
		t.Fatalf("state file was not fully replaced by the second save: %s", reloaded)
	}
	entries, err := os.ReadDir(directory)
	if err != nil {
		t.Fatal(err)
	}
	for _, entry := range entries {
		if strings.HasSuffix(entry.Name(), ".tmp") {
			t.Fatalf("leftover temporary file after atomic replacement: %s", entry.Name())
		}
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

const unchangedIdentityConfig = `{"agent":{"name":"Home server"},"storage":{"controlEndpoint":"https://storage.local:7443","repositoryPasswordFile":"/run/secrets/restic-password"},"backupSets":[{"name":"alpha","paths":["/data/alpha"]}]}`

// The Linux units run with ProtectSystem=strict and ReadWritePaths=/var/cache/backupmesh, so the
// directory holding the config is read-only. Rewriting the state file on every load made watch and
// backup fail to start with EROFS even though nothing about the identity had changed.
func TestResolveIdentityStateLeavesAnUnchangedStateFileAlone(t *testing.T) {
	path := filepath.Join(t.TempDir(), "backupmesh.json")
	if err := os.WriteFile(path, []byte(unchangedIdentityConfig), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := Load(path); err != nil {
		t.Fatalf("first Load() = %v", err)
	}
	statePath := path + ".state.json"
	before, err := os.Stat(statePath)
	if err != nil {
		t.Fatalf("state file was not created: %v", err)
	}
	time.Sleep(20 * time.Millisecond)
	if _, err := Load(path); err != nil {
		t.Fatalf("second Load() = %v", err)
	}
	after, err := os.Stat(statePath)
	if err != nil {
		t.Fatal(err)
	}
	if !before.ModTime().Equal(after.ModTime()) {
		t.Fatalf("state file was rewritten even though the identity did not change (%v -> %v)", before.ModTime(), after.ModTime())
	}
}

func TestResolveIdentityStateStillWritesWhenTheIdentityChanges(t *testing.T) {
	path := filepath.Join(t.TempDir(), "backupmesh.json")
	if err := os.WriteFile(path, []byte(unchangedIdentityConfig), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := Load(path); err != nil {
		t.Fatal(err)
	}
	statePath := path + ".state.json"
	before, err := os.Stat(statePath)
	if err != nil {
		t.Fatal(err)
	}
	added := `{"agent":{"name":"Home server"},"storage":{"controlEndpoint":"https://storage.local:7443","repositoryPasswordFile":"/run/secrets/restic-password"},"backupSets":[{"name":"alpha","paths":["/data/alpha"]},{"name":"beta","paths":["/data/beta"]}]}`
	if err := os.WriteFile(path, []byte(added), 0600); err != nil {
		t.Fatal(err)
	}
	time.Sleep(20 * time.Millisecond)
	if _, err := Load(path); err != nil {
		t.Fatal(err)
	}
	after, err := os.Stat(statePath)
	if err != nil {
		t.Fatal(err)
	}
	if before.ModTime().Equal(after.ModTime()) {
		t.Fatal("a new backup set did not update the identity state file")
	}
	contents, err := os.ReadFile(statePath)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(contents), `"beta"`) {
		t.Fatalf("identity state does not contain the new backup set: %s", contents)
	}
}

func TestLoadSucceedsWhenTheConfigDirectoryIsReadOnlyAndNothingChanged(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("directory write permissions are not enforced the same way on Windows")
	}
	dir := t.TempDir()
	path := filepath.Join(dir, "backupmesh.json")
	if err := os.WriteFile(path, []byte(unchangedIdentityConfig), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := Load(path); err != nil {
		t.Fatalf("warmup Load() = %v", err)
	}
	if err := os.Chmod(dir, 0500); err != nil {
		t.Skip("cannot make the directory read-only")
	}
	t.Cleanup(func() { _ = os.Chmod(dir, 0700) })
	if _, err := Load(path); err != nil {
		t.Fatalf("Load() with a read-only config directory = %v; watch and backup cannot start", err)
	}
}
