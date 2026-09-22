package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"time"
	"unicode"

	"github.com/namioto/backupmesh/source-agent/internal/config"
	"github.com/namioto/backupmesh/source-agent/internal/controlapi"
)

type pendingNearbyRequest struct {
	RequestID          string    `json:"request_id"`
	Endpoint           string    `json:"endpoint"`
	StorageFingerprint string    `json:"storage_fingerprint"`
	EncryptedCode      string    `json:"encrypted_code"`
	ComparisonCode     string    `json:"comparison_code"`
	ExpiresAt          time.Time `json:"expires_at"`
	Denied             bool      `json:"denied,omitempty"`
	Approved           bool      `json:"approved,omitempty"`
}

type nearbyClaim struct {
	RequestID     string    `json:"request_id"`
	EncryptedCode string    `json:"encrypted_code"`
	ExpiresAt     time.Time `json:"expires_at"`
}

func runNearbyWatch(ctx context.Context, configPath, outputDirectory string, cfg config.Config) error {
	promptContext, cancelPrompts := context.WithCancel(ctx)
	defer cancelPrompts()
	privateKey, identity, publicKey, err := loadNearbyIdentity(nearbyDirectory(configPath, outputDirectory))
	if err != nil {
		return err
	}
	announcement := controlapi.NearbyAnnouncement{AgentID: cfg.Agent.ID, AgentName: safeDisplayName(cfg.Agent.Name), Identity: identity, PublicKey: publicKey}
	type approvalResult struct {
		pending  pendingNearbyRequest
		approved bool
	}
	approvals := make(chan approvalResult, 1)
	prompted := map[string]bool{}
	claimFailureReported := map[string]bool{}
	promptActive := false
	for {
		_ = retryDeniedNearby(ctx, configPath, outputDirectory)
		if paired, err := pairApprovedNearby(configPath, outputDirectory); err != nil {
			fmt.Fprintf(os.Stderr, "approved pairing retry failed: %v\n", err)
		} else if paired {
			return nil
		}
		select {
		case result := <-approvals:
			promptActive = false
			delete(prompted, result.pending.RequestID)
			if result.approved && time.Until(result.pending.ExpiresAt) > 0 {
				if _, err := config.Load(configPath); err == nil {
					return errors.New("Remote Agent was paired by another request; refusing to replace active pairing")
				}
				parsed, _ := url.Parse(result.pending.Endpoint)
				port, _ := strconv.Atoi(parsed.Port())
				storage := controlapi.NearbyStorage{Host: parsed.Hostname(), Port: port, StorageIdentity: result.pending.StorageFingerprint}
				fresh, code, err := claimNearbyRequest(ctx, storage, result.pending.RequestID, cfg.Agent.ID, identity, privateKey)
				if err != nil {
					if !claimFailureReported[result.pending.RequestID] {
						fmt.Fprintf(os.Stderr, "nearby pairing request could not be claimed: %v; check TCP access to Storage and retry\n", err)
						claimFailureReported[result.pending.RequestID] = true
					}
					_ = denyPendingNearby(configPath, outputDirectory, result.pending.RequestID)
					continue
				}
				delete(claimFailureReported, result.pending.RequestID)
				if fresh.ComparisonCode != result.pending.ComparisonCode {
					_ = denyPendingNearby(configPath, outputDirectory, result.pending.RequestID)
					continue
				}
				_ = code
				if err := savePendingNearby(configPath, fresh); err != nil {
					return err
				}
				if err := approveNearbyMarker(configPath, fresh.RequestID); err != nil {
					return err
				}
				continue
			}
			_ = denyPendingNearby(configPath, outputDirectory, result.pending.RequestID)
		default:
		}
		storages, err := controlapi.AnnounceNearby(ctx, announcement)
		if err != nil && ctx.Err() == nil {
			fmt.Fprintf(os.Stderr, "nearby discovery failed: %v\n", err)
		}
		for _, storage := range storages {
			for _, requestID := range storage.RequestIDs {
				if pendingNearbyDenied(configPath, requestID) {
					continue
				}
				pending, code, err := claimNearbyRequest(ctx, storage, requestID, cfg.Agent.ID, identity, privateKey)
				if err != nil {
					if !claimFailureReported[requestID] {
						fmt.Fprintf(os.Stderr, "nearby pairing request could not be claimed: %v; check TCP access to Storage and retry\n", err)
						claimFailureReported[requestID] = true
					}
					continue
				}
				delete(claimFailureReported, requestID)
				if err := savePendingNearby(configPath, pending); err != nil {
					return err
				}
				_ = code
				if runtime.GOOS == "windows" && !promptActive && !pendingNearbyApproved(configPath, pending.RequestID) && !prompted[pending.RequestID] {
					promptActive = true
					prompted[pending.RequestID] = true
					go func() { approvals <- approvalResult{pending, approveNearbyWindows(promptContext, pending)} }()
				}
			}
		}
		if !sleepContext(ctx, time.Second) {
			return nil
		}
		if paired, _ := config.Load(configPath); paired.Agent.ID != "" {
			return nil
		}
	}
}

func claimNearbyRequest(ctx context.Context, storage controlapi.NearbyStorage, requestID, agentID, identity string, key *rsa.PrivateKey) (pendingNearbyRequest, string, error) {
	if !isUUIDText(requestID) {
		return pendingNearbyRequest{}, "", errors.New("invalid request ID")
	}
	endpoint := "https://" + net.JoinHostPort(storage.Host, strconv.Itoa(storage.Port))
	body, _ := json.Marshal(map[string]string{"agent_id": agentID, "identity": identity})
	client := pinnedPairingClient(storage.StorageIdentity)
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, endpoint+"/api/v1/pairing/requests/"+requestID+"/claim", bytes.NewReader(body))
	if err != nil {
		return pendingNearbyRequest{}, "", err
	}
	request.Header.Set("Content-Type", "application/json")
	response, err := client.Do(request)
	if err != nil {
		return pendingNearbyRequest{}, "", err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return pendingNearbyRequest{}, "", fmt.Errorf("claim returned HTTP %d", response.StatusCode)
	}
	var claim nearbyClaim
	decoder := json.NewDecoder(io.LimitReader(response.Body, 64*1024))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&claim); err != nil {
		return pendingNearbyRequest{}, "", errors.New("invalid pairing claim")
	}
	remaining := time.Until(claim.ExpiresAt)
	if claim.RequestID != requestID || remaining <= 0 || remaining > 11*time.Minute {
		return pendingNearbyRequest{}, "", errors.New("invalid or expired pairing claim")
	}
	encrypted, err := base64.StdEncoding.DecodeString(claim.EncryptedCode)
	if err != nil {
		return pendingNearbyRequest{}, "", errors.New("invalid encrypted pairing code")
	}
	plaintext, err := rsa.DecryptOAEP(sha256.New(), rand.Reader, key, encrypted, nil)
	if err != nil {
		return pendingNearbyRequest{}, "", errors.New("pairing claim was not encrypted to this Remote Agent")
	}
	code := string(plaintext)
	pending := pendingNearbyRequest{RequestID: requestID, Endpoint: endpoint, StorageFingerprint: storage.StorageIdentity, EncryptedCode: claim.EncryptedCode, ComparisonCode: nearbyComparisonCode(code, requestID, identity, storage.StorageIdentity), ExpiresAt: claim.ExpiresAt}
	return pending, code, nil
}

func nearbyComparisonCode(pairingCode, requestID, remoteIdentity, storageIdentity string) string {
	compactID := strings.ReplaceAll(strings.ToLower(requestID), "-", "")
	sum := sha256.Sum256([]byte(pairingCode + "|" + compactID + "|" + strings.ToLower(remoteIdentity) + "|" + strings.ToLower(storageIdentity)))
	return fmt.Sprintf("%06d", binary.LittleEndian.Uint32(sum[:4])%1_000_000)
}

func pinnedPairingClient(fingerprint string) *http.Client {
	transport := http.DefaultTransport.(*http.Transport).Clone()
	transport.TLSClientConfig = &tls.Config{MinVersion: tls.VersionTLS12, InsecureSkipVerify: true, VerifyConnection: func(state tls.ConnectionState) error {
		if len(state.PeerCertificates) == 0 {
			return errors.New("storage did not present a certificate")
		}
		actual := sha256.Sum256(state.PeerCertificates[0].Raw)
		if !strings.EqualFold(hex.EncodeToString(actual[:]), fingerprint) {
			return errors.New("storage certificate fingerprint does not match discovery")
		}
		return nil
	}}
	return &http.Client{Transport: transport, Timeout: 15 * time.Second}
}

func loadNearbyIdentity(directory string) (*rsa.PrivateKey, string, string, error) {
	path := filepath.Join(directory, "nearby-identity.pem")
	if raw, err := os.ReadFile(filepath.Clean(path)); err == nil {
		block, _ := pem.Decode(raw)
		if block == nil {
			return nil, "", "", errors.New("invalid nearby identity")
		}
		key, err := x509.ParsePKCS8PrivateKey(block.Bytes)
		if rsaKey, ok := key.(*rsa.PrivateKey); err == nil && ok && rsaKey.N.BitLen() >= 2048 {
			return encodeNearbyIdentity(rsaKey)
		}
		return nil, "", "", errors.New("invalid nearby identity")
	} else if !errors.Is(err, os.ErrNotExist) {
		return nil, "", "", err
	}
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		return nil, "", "", err
	}
	encoded, _ := x509.MarshalPKCS8PrivateKey(key)
	if err := os.MkdirAll(directory, 0700); err != nil {
		return nil, "", "", err
	}
	if err := writePrivateFile(path, pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: encoded})); err != nil {
		return nil, "", "", err
	}
	return encodeNearbyIdentity(key)
}

func encodeNearbyIdentity(key *rsa.PrivateKey) (*rsa.PrivateKey, string, string, error) {
	publicKey, err := x509.MarshalPKIXPublicKey(&key.PublicKey)
	if err != nil {
		return nil, "", "", err
	}
	sum := sha256.Sum256(publicKey)
	return key, hex.EncodeToString(sum[:]), base64.StdEncoding.EncodeToString(publicKey), nil
}

func safeDisplayName(value string) string {
	var out []rune
	for _, r := range strings.TrimSpace(value) {
		if unicode.IsControl(r) || r == '\u202a' || r == '\u202b' || r == '\u202c' || r == '\u202d' || r == '\u202e' || r == '\u2066' || r == '\u2067' || r == '\u2068' || r == '\u2069' {
			continue
		}
		out = append(out, r)
		if len(out) == 128 {
			break
		}
	}
	if len(out) == 0 {
		return "Remote Agent"
	}
	return string(out)
}

func nearbyDirectory(configPath, outputDirectory string) string {
	if outputDirectory != "" {
		return outputDirectory
	}
	return filepath.Join(filepath.Dir(configPath), "pairing")
}

func pendingNearbyDirectory(configPath string) string {
	return filepath.Join(filepath.Dir(configPath), "pairing", "pending")
}

func savePendingNearby(configPath string, pending pendingNearbyRequest) error {
	directory := pendingNearbyDirectory(configPath)
	if err := os.MkdirAll(directory, 0700); err != nil {
		return err
	}
	path := filepath.Join(directory, pending.RequestID+".json")
	if raw, err := os.ReadFile(path); err == nil {
		var existing pendingNearbyRequest
		if json.Unmarshal(raw, &existing) != nil || existing.RequestID != pending.RequestID || existing.Endpoint != pending.Endpoint || existing.StorageFingerprint != pending.StorageFingerprint || existing.EncryptedCode != pending.EncryptedCode || existing.ComparisonCode != pending.ComparisonCode {
			return errors.New("pairing request identity changed")
		}
		pending.Approved = pending.Approved || existing.Approved
		pending.Denied = pending.Denied || existing.Denied
	} else if !errors.Is(err, os.ErrNotExist) {
		return err
	}
	raw, _ := json.MarshalIndent(pending, "", "  ")
	return writePrivateFile(path, append(raw, '\n'))
}

func loadPendingNearby(configPath string) ([]pendingNearbyRequest, error) {
	entries, err := os.ReadDir(pendingNearbyDirectory(configPath))
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var pending []pendingNearbyRequest
	for _, entry := range entries {
		if entry.IsDir() || filepath.Ext(entry.Name()) != ".json" {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(pendingNearbyDirectory(configPath), entry.Name()))
		if err != nil {
			return nil, err
		}
		var item pendingNearbyRequest
		if json.Unmarshal(raw, &item) == nil && time.Until(item.ExpiresAt) > 0 {
			_, approvedMarkerErr := os.Stat(filepath.Join(pendingNearbyDirectory(configPath), item.RequestID+".approved"))
			item.Approved = item.Approved || approvedMarkerErr == nil
			_, deniedMarkerErr := os.Stat(filepath.Join(pendingNearbyDirectory(configPath), item.RequestID+".denied"))
			item.Denied = item.Denied || deniedMarkerErr == nil
			pending = append(pending, item)
		}
	}
	sort.Slice(pending, func(i, j int) bool { return pending[i].ExpiresAt.Before(pending[j].ExpiresAt) })
	return pending, nil
}

func removePendingNearby(configPath, requestID string) error {
	if !isUUIDText(requestID) {
		return errors.New("invalid request ID")
	}
	_ = os.Remove(filepath.Join(pendingNearbyDirectory(configPath), requestID+".approved"))
	_ = os.Remove(filepath.Join(pendingNearbyDirectory(configPath), requestID+".denied"))
	err := os.Remove(filepath.Join(pendingNearbyDirectory(configPath), requestID+".json"))
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	return err
}

func denyPendingNearby(configPath, outputDirectory, requestID string) error {
	pending, err := loadPendingNearby(configPath)
	if err != nil {
		return err
	}
	for _, item := range pending {
		if item.RequestID == requestID {
			if err := writePrivateFile(filepath.Join(pendingNearbyDirectory(configPath), requestID+".denied"), nil); err != nil {
				return err
			}
			item.Denied = true
			if err := savePendingNearby(configPath, item); err != nil {
				return err
			}
			ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
			defer cancel()
			if err := rejectNearbyRequest(ctx, configPath, outputDirectory, item); err != nil {
				return fmt.Errorf("denial saved locally; Storage notification will retry: %w", err)
			}
			return removePendingNearby(configPath, requestID)
		}
	}
	return errors.New("pending pairing request not found or expired")
}

func retryDeniedNearby(ctx context.Context, configPath, outputDirectory string) error {
	pending, err := loadPendingNearby(configPath)
	if err != nil {
		return err
	}
	for _, item := range pending {
		if !item.Denied {
			continue
		}
		if err := rejectNearbyRequest(ctx, configPath, outputDirectory, item); err != nil {
			return err
		}
		_ = removePendingNearby(configPath, item.RequestID)
	}
	return nil
}

func rejectNearbyRequest(ctx context.Context, configPath, outputDirectory string, item pendingNearbyRequest) error {
	cfg, err := config.LoadUserConfig(configPath)
	if err != nil {
		return err
	}
	key, identity, _, err := loadNearbyIdentity(nearbyDirectory(configPath, outputDirectory))
	if err != nil {
		return err
	}
	encrypted, err := base64.StdEncoding.DecodeString(item.EncryptedCode)
	if err != nil {
		return err
	}
	code, err := rsa.DecryptOAEP(sha256.New(), rand.Reader, key, encrypted, nil)
	if err != nil {
		return err
	}
	body, _ := json.Marshal(map[string]string{"agent_id": cfg.Agent.ID, "identity": identity, "code": string(code)})
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, item.Endpoint+"/api/v1/pairing/requests/"+item.RequestID+"/reject", bytes.NewReader(body))
	if err != nil {
		return err
	}
	request.Header.Set("Content-Type", "application/json")
	response, err := pinnedPairingClient(item.StorageFingerprint).Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusNoContent && response.StatusCode != http.StatusNotFound {
		return fmt.Errorf("reject returned HTTP %d", response.StatusCode)
	}
	return nil
}

func pendingNearbyDenied(configPath, requestID string) bool {
	pending, _ := loadPendingNearby(configPath)
	for _, item := range pending {
		if item.RequestID == requestID {
			return item.Denied
		}
	}
	return false
}

func approvePendingNearby(configPath, outputDirectory, requestID string) error {
	if _, err := config.Load(configPath); err == nil {
		return errors.New("Remote Agent is already paired; refusing to replace active pairing")
	}
	pending, err := loadPendingNearby(configPath)
	if err != nil {
		return err
	}
	for _, item := range pending {
		if item.Denied {
			continue
		}
		if item.RequestID != requestID {
			continue
		}
		if err := approveNearbyMarker(configPath, item.RequestID); err != nil {
			return err
		}
		fmt.Println("pairing approved; watcher will apply it")
		return nil
	}
	return errors.New("pending pairing request not found or expired")
}

func pairApprovedNearby(configPath, outputDirectory string) (bool, error) {
	pending, err := loadPendingNearby(configPath)
	if err != nil {
		return false, err
	}
	for _, item := range pending {
		if item.Denied || !pendingNearbyApproved(configPath, item.RequestID) {
			continue
		}
		if _, err := config.Load(configPath); err == nil {
			return false, errors.New("Remote Agent is already paired; refusing to replace active pairing")
		}
		key, _, _, err := loadNearbyIdentity(nearbyDirectory(configPath, outputDirectory))
		if err != nil {
			return false, err
		}
		encrypted, err := base64.StdEncoding.DecodeString(item.EncryptedCode)
		if err != nil {
			return false, err
		}
		code, err := rsa.DecryptOAEP(sha256.New(), rand.Reader, key, encrypted, nil)
		if err != nil {
			return false, err
		}
		if err := pairWithCode(configPath, item.Endpoint, string(code), item.StorageFingerprint, outputDirectory); err != nil {
			return false, err
		}
		return true, removePendingNearby(configPath, item.RequestID)
	}
	return false, nil
}

func approveNearbyMarker(configPath, requestID string) error {
	if !isUUIDText(requestID) {
		return errors.New("invalid request ID")
	}
	return writePrivateFile(filepath.Join(pendingNearbyDirectory(configPath), requestID+".approved"), nil)
}

func pendingNearbyApproved(configPath, requestID string) bool {
	if _, err := os.Stat(filepath.Join(pendingNearbyDirectory(configPath), requestID+".approved")); err == nil {
		return true
	}
	pending, _ := loadPendingNearby(configPath)
	for _, item := range pending {
		if item.RequestID == requestID {
			return item.Approved
		}
	}
	return false
}

func printPendingNearby(configPath string) error {
	pending, err := loadPendingNearby(configPath)
	if err != nil {
		return err
	}
	if len(pending) == 0 {
		fmt.Println("no pending pairing requests")
		return nil
	}
	for _, item := range pending {
		fmt.Printf("%s  comparison %s  expires %s\n", item.RequestID, item.ComparisonCode, item.ExpiresAt.Local().Format(time.RFC3339))
	}
	return nil
}

func approveNearbyWindows(ctx context.Context, pending pendingNearbyRequest) bool {
	if time.Until(pending.ExpiresAt) <= 0 {
		return false
	}
	script := `[void][Reflection.Assembly]::LoadWithPartialName('System.Windows.Forms'); $text=if([Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName -eq 'ko'){$env:BACKUPMESH_APPROVAL_TEXT_KO}else{$env:BACKUPMESH_APPROVAL_TEXT_EN}; $r=[Windows.Forms.MessageBox]::Show($text,'BackupMesh pairing request',[Windows.Forms.MessageBoxButtons]::YesNo,[Windows.Forms.MessageBoxIcon]::Question,[Windows.Forms.MessageBoxDefaultButton]::Button2); if($r -eq [Windows.Forms.DialogResult]::Yes){exit 0}; exit 1`
	// PowerShell -EncodedCommand uses UTF-16LE.
	utf16 := make([]byte, 0, len(script)*2)
	for _, r := range script {
		utf16 = append(utf16, byte(r), byte(r>>8))
	}
	encoded := base64.StdEncoding.EncodeToString(utf16)
	promptContext, cancel := context.WithDeadline(ctx, pending.ExpiresAt)
	defer cancel()
	command := exec.CommandContext(promptContext, "powershell.exe", "-NoProfile", "-STA", "-WindowStyle", "Hidden", "-EncodedCommand", encoded)
	english := "Storage requests access to this computer. Compare code " + pending.ComparisonCode + " in Storage, then approve. Request expires " + pending.ExpiresAt.Local().Format(time.RFC3339) + "."
	korean := "Storage에서 이 컴퓨터에 연결을 요청했습니다. Storage에 표시된 코드 " + pending.ComparisonCode + "을(를) 확인한 후 승인하세요. 요청 만료: " + pending.ExpiresAt.Local().Format(time.RFC3339)
	command.Env = append(os.Environ(), "BACKUPMESH_APPROVAL_TEXT_EN="+english, "BACKUPMESH_APPROVAL_TEXT_KO="+korean)
	return command.Run() == nil && time.Until(pending.ExpiresAt) > 0
}

func isUUIDText(value string) bool {
	if len(value) != 36 {
		return false
	}
	_, err := hex.DecodeString(strings.ReplaceAll(value, "-", ""))
	return err == nil
}
