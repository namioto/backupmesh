package restic

import (
	"context"
	"errors"
	"os/exec"
	"reflect"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/namioto/backupmesh/source-agent/internal/engine"
)

func TestEnsureRepositoryPreservesCancellation(t *testing.T) {
	binary := "sh"
	if runtime.GOOS == "windows" {
		binary = "cmd"
	}
	path, err := exec.LookPath(binary)
	if err != nil {
		t.Skipf("%s is unavailable: %v", binary, err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	err = (Adapter{Binary: path}).EnsureRepository(ctx, engine.BackupRequest{Repository: "rest:http://localhost/repo"})
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("error = %v, want context cancellation", err)
	}
}

func TestBuildBackupArgs(t *testing.T) {
	req := engine.BackupRequest{Repository: "rest:https://host/repo", Paths: []string{"/home/a", "/srv/b"}, Includes: []string{"*.db"}, Excludes: []string{"cache/**"}, UploadLimitBPS: 1025}
	want := []string{"backup", "--json", "--include", "*.db", "--exclude", "cache/**", "--limit-upload", "2", "/home/a", "/srv/b"}
	if got := BuildBackupArgs(req); !reflect.DeepEqual(got, want) {
		t.Fatalf("BuildBackupArgs() = %#v, want %#v", got, want)
	}
}

func TestBuildBackupArgsOmitsRepository(t *testing.T) {
	secretURL := "rest:https://user:secret@host/repo"
	got := strings.Join(BuildBackupArgs(engine.BackupRequest{Repository: secretURL, Paths: []string{"/data"}}), " ")
	if strings.Contains(got, secretURL) || strings.Contains(got, "secret") {
		t.Fatalf("repository leaked into process args: %q", got)
	}
}

func TestBuildEnvCarriesRepositorySecretsOutsideArguments(t *testing.T) {
	req := engine.BackupRequest{Repository: "rest:https://user:secret@host/repo", PasswordFile: "/run/secrets/restic-password", CacheDirectory: "/var/cache/backupmesh/restic", CACertificateFile: "/etc/backupmesh/pairing/storage-ca.pem"}
	env := BuildEnv([]string{"PATH=/bin"}, nil, req)
	joined := strings.Join(env, "\n")
	for _, want := range []string{"RESTIC_REPOSITORY=" + req.Repository, "RESTIC_PASSWORD_FILE=" + req.PasswordFile, "RESTIC_CACHE_DIR=" + req.CacheDirectory, "RESTIC_CACERT=" + req.CACertificateFile} {
		if !strings.Contains(joined, want) {
			t.Fatalf("environment does not contain %q", want)
		}
	}
}

func TestBuildBackupArgsDoesNotUseShellSyntax(t *testing.T) {
	path := `/data/name; echo unsafe`
	got := BuildBackupArgs(engine.BackupRequest{Repository: "repo", Paths: []string{path}})
	if got[len(got)-1] != path {
		t.Fatalf("path changed: %q", got[len(got)-1])
	}
}

func TestParseJSONStream(t *testing.T) {
	input := strings.Join([]string{
		`{"message_type":"status","files_done":2,"total_files":4,"bytes_done":100,"total_bytes":400,"percent_done":0.25}`,
		`{"message_type":"future_message","value":1}`,
		`{"message_type":"summary","files_new":1,"files_changed":2,"files_unmodified":3,"data_added":99,"total_duration":1.5,"snapshot_id":"abc123"}`,
	}, "\n")
	var seen engine.Progress
	result, err := ParseJSONStream(strings.NewReader(input), func(p engine.Progress) { seen = p })
	if err != nil {
		t.Fatalf("ParseJSONStream() error = %v", err)
	}
	if seen.Percent != .25 || seen.BytesDone != 100 {
		t.Errorf("progress = %#v", seen)
	}
	if result.SnapshotID != "abc123" || result.FilesUnchanged != 3 || result.Duration != 1500*time.Millisecond {
		t.Errorf("result = %#v", result)
	}
}

func TestParseJSONStreamRejectsMalformedJSON(t *testing.T) {
	_, err := ParseJSONStream(strings.NewReader("not json\n"), nil)
	if err == nil || !strings.Contains(err.Error(), "decode restic JSON") {
		t.Fatalf("error = %v", err)
	}
}

func TestParseJSONStreamRequiresSummary(t *testing.T) {
	_, err := ParseJSONStream(strings.NewReader(`{"message_type":"status"}`), nil)
	if err == nil || !strings.Contains(err.Error(), "without summary") {
		t.Fatalf("error = %v", err)
	}
}
