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
	AuthToken  string
	AgentID    string
}

type StorageStatus struct {
	AgentID     string            `json:"agent_id"`
	State       string            `json:"state"`
	ObservedAt  time.Time         `json:"observed_at"`
	Storage     []StoragePresence `json:"storage"`
	ActiveJobID *string           `json:"active_job_id"`
	Message     string            `json:"message,omitempty"`
}

type StoragePresence struct {
	DeviceID    string `json:"deviceId"`
	DisplayName string `json:"displayName"`
	Connected   bool   `json:"connected"`
	Ready       bool   `json:"ready"`
	CurrentRoot string `json:"currentRoot"`
}

type BackupRequest struct {
	JobID           string    `json:"job_id"`
	SourceAgentID   string    `json:"source_agent_id"`
	BackupSetID     string    `json:"backup_set_id"`
	TargetMappingID string    `json:"target_mapping_id"`
	RequestedAt     time.Time `json:"requested_at"`
	SnapshotTags    []string  `json:"snapshot_tags,omitempty"`
}

type BackupAdmission struct {
	JobID              string    `json:"job_id"`
	TargetMappingID    string    `json:"target_mapping_id"`
	DeviceID           string    `json:"device_id"`
	State              string    `json:"state"`
	AcceptedAt         time.Time `json:"accepted_at"`
	RepositoryEndpoint string    `json:"repository_endpoint"`
}

type BackupTargetAvailability struct {
	MappingID         string `json:"mappingId"`
	DeviceID          string `json:"deviceId"`
	BackupSetID       string `json:"backupSetId"`
	DeviceName        string `json:"deviceName"`
	DestinationFolder string `json:"destinationFolder"`
	State             string `json:"state"`
	Reason            string `json:"reason,omitempty"`
}

type BackupCommand struct {
	CommandID       string    `json:"command_id"`
	SourceAgentID   string    `json:"source_agent_id"`
	BackupSetID     string    `json:"backup_set_id"`
	TargetMappingID string    `json:"target_mapping_id"`
	Reason          string    `json:"reason"`
	RequestedAt     time.Time `json:"requested_at"`
	State           string    `json:"state"`
	JobID           string    `json:"job_id,omitempty"`
}

type BackupCommandClaimResponse struct {
	Command *BackupCommand `json:"command"`
}

type BackupCommandResult struct {
	CommandID     string    `json:"command_id"`
	SourceAgentID string    `json:"source_agent_id"`
	CompletedAt   time.Time `json:"completed_at"`
	Outcome       string    `json:"outcome"`
	JobID         string    `json:"job_id,omitempty"`
	Message       string    `json:"message,omitempty"`
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

type JobStatus struct {
	JobID  string        `json:"job_id"`
	State  string        `json:"state"`
	Result *BackupResult `json:"result,omitempty"`
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

type RenewCertificateResponse struct {
	CertificatePEM string    `json:"certificate_pem"`
	PrivateKeyPEM  string    `json:"private_key_pem"`
	AuthorityPEM   string    `json:"authority_pem"`
	ExpiresAt      time.Time `json:"expires_at"`
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

func (c Client) ListBackupTargets(ctx context.Context, sourceAgentID, backupSetID string) ([]BackupTargetAvailability, error) {
	var out []BackupTargetAvailability
	path := "/backup/targets/" + url.PathEscape(sourceAgentID) + "/" + url.PathEscape(backupSetID)
	err := c.do(ctx, http.MethodGet, path, "", nil, http.StatusOK, &out)
	return out, err
}

func (c Client) ClaimBackupCommand(ctx context.Context, sourceAgentID string) (*BackupCommand, error) {
	var out BackupCommandClaimResponse
	err := c.do(ctx, http.MethodPost, "/backup/commands/claim/"+url.PathEscape(sourceAgentID), "", nil, http.StatusOK, &out)
	return out.Command, err
}

func (c Client) CompleteBackupCommand(ctx context.Context, key string, in BackupCommandResult) error {
	return c.do(ctx, http.MethodPost, "/backup/commands/result", key, in, http.StatusNoContent, nil)
}

func (c Client) GetBackupStatus(ctx context.Context, jobID string) (JobStatus, error) {
	var out JobStatus
	err := c.do(ctx, http.MethodGet, "/backup/status/"+url.PathEscape(jobID), "", nil, http.StatusOK, &out)
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

func (c Client) RenewCertificate(ctx context.Context) (RenewCertificateResponse, error) {
	var out RenewCertificateResponse
	err := c.do(ctx, http.MethodPost, "/certificate/renew", "", nil, http.StatusOK, &out)
	return out, err
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
	if c.AuthToken != "" {
		req.Header.Set("Authorization", "Bearer "+c.AuthToken)
	}
	if c.AgentID != "" {
		req.Header.Set("X-BackupMesh-Agent-ID", c.AgentID)
	}
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
		return fmt.Errorf("control API request: %w", err)
	}
	defer resp.Body.Close()
	limited := io.LimitReader(resp.Body, maxResponseBytes+1)
	b, err := io.ReadAll(limited)
	if err != nil {
		return fmt.Errorf("read control API response: %w", err)
	}
	if len(b) > maxResponseBytes {
		return errors.New("control API response exceeds size limit")
	}
	if resp.StatusCode != expected {
		var p problem
		if json.Unmarshal(b, &p) == nil && p.Detail != "" {
			return fmt.Errorf("control API returned %d %s: %s", resp.StatusCode, p.Code, p.Detail)
		}
		return fmt.Errorf("control API returned HTTP %d", resp.StatusCode)
	}
	if out != nil {
		if len(b) == 0 {
			return errors.New("control API returned an empty response")
		}
		if err := json.Unmarshal(b, out); err != nil {
			return fmt.Errorf("decode control API response: %w", err)
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
