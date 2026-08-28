package controlapi

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestGetStorageStatusUsesSingularPathAndRequestID(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/v1/storage/status" {
			t.Errorf("path = %q", r.URL.Path)
		}
		assertUUIDv4(t, r.Header.Get("X-Request-ID"))
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"agent_id":"storage-1","state":"ready","observed_at":"2026-08-28T00:00:00Z","storage":null,"active_job_id":null}`))
	}))
	defer srv.Close()
	status, err := (Client{BaseURL: srv.URL + "/api/v1"}).GetStorageStatus(context.Background())
	if err != nil || status.State != "ready" {
		t.Fatalf("status = %#v, err = %v", status, err)
	}
}

func TestClientSendsAuthenticationTokenOutsideTheRequestBody(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if got := r.Header.Get("Authorization"); got != "Bearer test-token-with-at-least-32-characters" {
			t.Errorf("authorization = %q", got)
		}
		if got := r.Header.Get("X-BackupMesh-Agent-ID"); got != "f91436ac-0ca9-4bcb-b0d0-42bc7181f611" {
			t.Errorf("agent identity = %q", got)
		}
		_, _ = w.Write([]byte(`{"agent_id":"storage-1","state":"ready","observed_at":"2026-08-28T00:00:00Z","storage":[],"active_job_id":null}`))
	}))
	defer srv.Close()
	_, err := (Client{BaseURL: srv.URL, AuthToken: "test-token-with-at-least-32-characters", AgentID: "f91436ac-0ca9-4bcb-b0d0-42bc7181f611"}).GetStorageStatus(context.Background())
	if err != nil {
		t.Fatal(err)
	}
}

func TestRequestBackupHeadersAndSnakeCaseBody(t *testing.T) {
	now := time.Date(2026, 8, 28, 1, 2, 3, 0, time.UTC)
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/backup/request" {
			t.Errorf("path = %q", r.URL.Path)
		}
		assertUUIDv4(t, r.Header.Get("X-Request-ID"))
		if got := r.Header.Get("Idempotency-Key"); got != "0123456789abcdef" {
			t.Errorf("idempotency = %q", got)
		}
		if got := r.Header.Get("X-BackupMesh-Sent-At"); got != now.Format(time.RFC3339Nano) {
			t.Errorf("sent at = %q", got)
		}
		var raw map[string]any
		if err := json.NewDecoder(r.Body).Decode(&raw); err != nil {
			t.Fatal(err)
		}
		if raw["source_agent_id"] != "source-1" {
			t.Errorf("body = %#v", raw)
		}
		if _, ok := raw["sourceAgentId"]; ok {
			t.Error("camelCase field was sent")
		}
		w.WriteHeader(http.StatusAccepted)
		_, _ = w.Write([]byte(`{"job_id":"job-1","target_mapping_id":"mapping-1","device_id":"device-1","state":"ACCEPTED","accepted_at":"2026-08-28T01:02:03Z","repository_endpoint":"rest:http://storage/repo"}`))
	}))
	defer srv.Close()
	admission, err := (Client{BaseURL: srv.URL, Now: func() time.Time { return now }}).RequestBackup(context.Background(), "0123456789abcdef", BackupRequest{JobID: "job-1", SourceAgentID: "source-1", BackupSetID: "set-1", TargetMappingID: "mapping-1", RequestedAt: now})
	if err != nil || admission.State != "ACCEPTED" {
		t.Fatalf("admission = %#v, err = %v", admission, err)
	}
}

func TestListBackupTargets(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/backup/targets/source-1/set-1" {
			t.Errorf("path = %q", r.URL.Path)
		}
		_, _ = w.Write([]byte(`[{"mappingId":"mapping-1","deviceId":"device-1","backupSetId":"set-1","deviceName":"USB","destinationFolder":"D:\\backup","state":"READY"}]`))
	}))
	defer srv.Close()
	targets, err := (Client{BaseURL: srv.URL}).ListBackupTargets(context.Background(), "source-1", "set-1")
	if err != nil || len(targets) != 1 || targets[0].State != "READY" {
		t.Fatalf("targets = %#v, err = %v", targets, err)
	}
}

func TestGetBackupStatusUsesEscapedJobPath(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/backup/status/job-1" {
			t.Errorf("path = %q", r.URL.Path)
		}
		_, _ = w.Write([]byte(`{"job_id":"job-1","state":"CANCEL_REQUESTED"}`))
	}))
	defer srv.Close()
	status, err := (Client{BaseURL: srv.URL}).GetBackupStatus(context.Background(), "job-1")
	if err != nil || status.State != "CANCEL_REQUESTED" {
		t.Fatalf("status = %#v, err = %v", status, err)
	}
}

func TestReportEndpoints(t *testing.T) {
	paths := []string{}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		paths = append(paths, r.URL.Path)
		if r.Header.Get("Idempotency-Key") == "" || r.Header.Get("X-BackupMesh-Sent-At") == "" {
			t.Error("required headers missing")
		}
		w.WriteHeader(http.StatusNoContent)
	}))
	defer srv.Close()
	c := Client{BaseURL: srv.URL}
	if err := c.ReportProgress(context.Background(), "progress-key-0001", BackupProgress{}); err != nil {
		t.Fatal(err)
	}
	if err := c.ReportResult(context.Background(), "result-key-000001", BackupResult{}); err != nil {
		t.Fatal(err)
	}
	if strings.Join(paths, ",") != "/backup/progress,/backup/result" {
		t.Fatalf("paths = %#v", paths)
	}
}

func TestPublishSourceCatalog(t *testing.T) {
	var received SourceCatalog
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/source/catalog" {
			t.Errorf("path = %q", r.URL.Path)
		}
		if err := json.NewDecoder(r.Body).Decode(&received); err != nil {
			t.Fatal(err)
		}
		w.WriteHeader(http.StatusNoContent)
	}))
	defer srv.Close()
	catalog := SourceCatalog{SourceAgentID: "f91436ac-0ca9-4bcb-b0d0-42bc7181f611", SourceAgentName: "Home server", UpdatedAt: time.Now(), BackupSets: []SourceCatalogBackupSet{{BackupSetID: "7d750726-97ab-4f81-9f09-f06c34f524d1", Name: "photos", SourcePaths: []string{"/srv/photos"}}}}
	if err := (Client{BaseURL: srv.URL}).PublishSourceCatalog(context.Background(), "catalog-key-0001", catalog); err != nil {
		t.Fatal(err)
	}
	if received.SourceAgentName != catalog.SourceAgentName || len(received.BackupSets) != 1 {
		t.Fatalf("received = %#v", received)
	}
}

func TestResponseBodyIsBounded(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		_, _ = w.Write([]byte(strings.Repeat("x", maxResponseBytes+1)))
	}))
	defer srv.Close()
	_, err := (Client{BaseURL: srv.URL}).GetStorageStatus(context.Background())
	if err == nil || !strings.Contains(err.Error(), "size limit") {
		t.Fatalf("error = %v", err)
	}
}

func TestContextCancellation(t *testing.T) {
	started := make(chan struct{})
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		close(started)
		<-r.Context().Done()
	}))
	defer srv.Close()
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { _, err := (Client{BaseURL: srv.URL}).GetStorageStatus(ctx); done <- err }()
	<-started
	cancel()
	if err := <-done; err == nil {
		t.Fatal("expected cancellation error")
	}
}

func assertUUIDv4(t *testing.T, value string) {
	t.Helper()
	if len(value) != 36 || value[14] != '4' || (value[19] != '8' && value[19] != '9' && value[19] != 'a' && value[19] != 'b') {
		t.Errorf("not a UUID v4: %q", value)
	}
}
