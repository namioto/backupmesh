package controlapi

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

const maxResponseBytes = 1 << 20

type Client struct {
	BaseURL    string
	HTTPClient *http.Client
	Now        func() time.Time
}

type StorageStatus struct {
	AgentID     string             `json:"agent_id"`
	State       string             `json:"state"`
	ObservedAt  time.Time          `json:"observed_at"`
	Storage     *StorageDescriptor `json:"storage"`
	ActiveJobID *string            `json:"active_job_id"`
	Message     string             `json:"message,omitempty"`
}

type StorageDescriptor struct {
	StorageID          string `json:"storage_id"`
	Label              string `json:"label"`
	CapacityBytes      int64  `json:"capacity_bytes"`
	AvailableBytes     int64  `json:"available_bytes"`
	Filesystem         string `json:"filesystem"`
	RepositoryEndpoint string `json:"repository_endpoint"`
}

type BackupRequest struct {
	JobID         string    `json:"job_id"`
	SourceAgentID string    `json:"source_agent_id"`
	RequestedAt   time.Time `json:"requested_at"`
	Repository    string    `json:"repository"`
	SnapshotTags  []string  `json:"snapshot_tags,omitempty"`
}

type BackupAdmission struct {
	JobID              string    `json:"job_id"`
	State              string    `json:"state"`
	AcceptedAt         time.Time `json:"accepted_at"`
	RepositoryEndpoint string    `json:"repository_endpoint"`
}

type BackupProgress struct {
	EventID    string    `json:"event_id"`
	JobID      string    `json:"job_id"`
	Sequence   int64     `json:"sequence"`
	ReportedAt time.Time `json:"reported_at"`
	Phase      string    `json:"phase"`
	BytesDone  uint64    `json:"bytes_done"`
	BytesTotal uint64    `json:"bytes_total"`
	FilesDone  uint64    `json:"files_done"`
	FilesTotal uint64    `json:"files_total"`
}

type BackupResult struct {
	EventID     string    `json:"event_id"`
	JobID       string    `json:"job_id"`
	Sequence    int64     `json:"sequence"`
	CompletedAt time.Time `json:"completed_at"`
	Outcome     string    `json:"outcome"`
	SnapshotID  string    `json:"snapshot_id,omitempty"`
	BytesAdded  uint64    `json:"bytes_added,omitempty"`
	ErrorCode   string    `json:"error_code,omitempty"`
	Message     string    `json:"message,omitempty"`
}

type SourceCatalogBackupSet struct {
	BackupSetID string   `json:"backup_set_id"`
	Name        string   `json:"name"`
	SourcePaths []string `json:"source_paths"`
}

type SourceCatalog struct {
	SourceAgentID   string                   `json:"source_agent_id"`
	SourceAgentName string                   `json:"source_agent_name"`
	UpdatedAt       time.Time                `json:"updated_at"`
	BackupSets      []SourceCatalogBackupSet `json:"backup_sets"`
}

type problem struct {
	Code   string `json:"code"`
	Detail string `json:"detail"`
}

func (c Client) GetStorageStatus(ctx context.Context) (StorageStatus, error) {
	var out StorageStatus
	err := c.do(ctx, http.MethodGet, "/storage/status", "", nil, http.StatusOK, &out)
	return out, err
}

func (c Client) RequestBackup(ctx context.Context, key string, in BackupRequest) (BackupAdmission, error) {
	var out BackupAdmission
	err := c.do(ctx, http.MethodPost, "/backup/request", key, in, http.StatusAccepted, &out)
	return out, err
}

func (c Client) ReportProgress(ctx context.Context, key string, in BackupProgress) error {
	return c.do(ctx, http.MethodPost, "/backup/progress", key, in, http.StatusNoContent, nil)
}

func (c Client) ReportResult(ctx context.Context, key string, in BackupResult) error {
	return c.do(ctx, http.MethodPost, "/backup/result", key, in, http.StatusNoContent, nil)
}

func (c Client) PublishSourceCatalog(ctx context.Context, key string, in SourceCatalog) error {
	return c.do(ctx, http.MethodPost, "/source/catalog", key, in, http.StatusNoContent, nil)
}

func (c Client) do(ctx context.Context, method, path, key string, body any, expected int, out any) error {
	base, err := url.Parse(strings.TrimRight(c.BaseURL, "/"))
	if err != nil || base.Scheme == "" || base.Host == "" {
		return errors.New("invalid Control API base URL")
	}
	base.Path = strings.TrimRight(base.Path, "/") + path
	var reader io.Reader
	if body != nil {
		encoded, err := json.Marshal(body)
		if err != nil {
			return fmt.Errorf("encode request: %w", err)
		}
		reader = bytes.NewReader(encoded)
	}
	req, err := http.NewRequestWithContext(ctx, method, base.String(), reader)
	if err != nil {
		return fmt.Errorf("create request: %w", err)
	}
	requestID, err := UUIDv4()
	if err != nil {
		return fmt.Errorf("create request ID: %w", err)
	}
	req.Header.Set("X-Request-ID", requestID)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if key != "" {
		req.Header.Set("Idempotency-Key", key)
		req.Header.Set("X-BackupMesh-Sent-At", c.now().UTC().Format(time.RFC3339Nano))
	}
	client := c.HTTPClient
	if client == nil {
		client = http.DefaultClient
	}
	resp, err := client.Do(req)
	if err != nil {
		return fmt.Errorf("Control API request: %w", err)
	}
	defer resp.Body.Close()
	limited := io.LimitReader(resp.Body, maxResponseBytes+1)
	b, err := io.ReadAll(limited)
	if err != nil {
		return fmt.Errorf("read Control API response: %w", err)
	}
	if len(b) > maxResponseBytes {
		return errors.New("Control API response exceeds size limit")
	}
	if resp.StatusCode != expected {
		var p problem
		if json.Unmarshal(b, &p) == nil && p.Detail != "" {
			return fmt.Errorf("Control API returned %d %s: %s", resp.StatusCode, p.Code, p.Detail)
		}
		return fmt.Errorf("Control API returned HTTP %d", resp.StatusCode)
	}
	if out != nil {
		if len(b) == 0 {
			return errors.New("Control API returned an empty response")
		}
		if err := json.Unmarshal(b, out); err != nil {
			return fmt.Errorf("decode Control API response: %w", err)
		}
	}
	return nil
}

func (c Client) now() time.Time {
	if c.Now != nil {
		return c.Now()
	}
	return time.Now()
}

func UUIDv4() (string, error) {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", err
	}
	b[6] = (b[6] & 0x0f) | 0x40
	b[8] = (b[8] & 0x3f) | 0x80
	var out [36]byte
	hex.Encode(out[0:8], b[0:4])
	out[8] = '-'
	hex.Encode(out[9:13], b[4:6])
	out[13] = '-'
	hex.Encode(out[14:18], b[6:8])
	out[18] = '-'
	hex.Encode(out[19:23], b[8:10])
	out[23] = '-'
	hex.Encode(out[24:36], b[10:16])
	return string(out[:]), nil
}
