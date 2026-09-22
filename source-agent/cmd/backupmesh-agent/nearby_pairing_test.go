package main

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/namioto/backupmesh/source-agent/internal/controlapi"
)

func TestClaimNearbyRequestPinsTLSDecryptsCodeAndDerivesComparison(t *testing.T) {
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	requestID := "7d750726-97ab-4f81-9f09-f06c34f524d1"
	identity := strings.Repeat("ab", 32)
	pairingCode := "secret-one-time-pairing-code"
	encrypted, err := rsa.EncryptOAEP(sha256.New(), rand.Reader, &key.PublicKey, []byte(pairingCode), nil)
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/v1/pairing/requests/"+requestID+"/claim" {
			t.Errorf("path = %s", r.URL.Path)
		}
		json.NewEncoder(w).Encode(nearbyClaim{RequestID: requestID, EncryptedCode: base64.StdEncoding.EncodeToString(encrypted), ExpiresAt: time.Now().Add(5 * time.Minute)})
	}))
	defer server.Close()
	parsed, _ := url.Parse(server.URL)
	fingerprint := sha256.Sum256(server.Certificate().Raw)
	storage := controlapi.NearbyStorage{Host: parsed.Hostname(), Port: mustPort(t, parsed.Port()), StorageIdentity: hex.EncodeToString(fingerprint[:])}
	pending, code, err := claimNearbyRequest(context.Background(), storage, requestID, "f91436ac-0ca9-4bcb-b0d0-42bc7181f611", identity, key)
	if err != nil {
		t.Fatal(err)
	}
	if code != pairingCode || pending.ComparisonCode != nearbyComparisonCode(pairingCode, requestID, identity, storage.StorageIdentity) {
		t.Fatalf("claim = %#v, code %q", pending, code)
	}
}

func TestDeniedNearbyRequestCannotBeApproved(t *testing.T) {
	configPath := filepath.Join(t.TempDir(), "backupmesh.json")
	requestID := "7d750726-97ab-4f81-9f09-f06c34f524d1"
	if err := savePendingNearby(configPath, pendingNearbyRequest{RequestID: requestID, ExpiresAt: time.Now().Add(time.Minute)}); err != nil {
		t.Fatal(err)
	}
	item := pendingNearbyRequest{RequestID: requestID, ExpiresAt: time.Now().Add(time.Minute), Denied: true}
	if err := savePendingNearby(configPath, item); err != nil {
		t.Fatal(err)
	}
	if !pendingNearbyDenied(configPath, requestID) {
		t.Fatal("denied request became eligible for another prompt")
	}
	if err := approvePendingNearby(configPath, "", requestID); err == nil {
		t.Fatal("denied request was approved")
	}
}

func TestApprovalMarkerSurvivesLaterClaimSave(t *testing.T) {
	configPath := filepath.Join(t.TempDir(), "backupmesh.json")
	requestID := "7d750726-97ab-4f81-9f09-f06c34f524d1"
	pending := pendingNearbyRequest{RequestID: requestID, Endpoint: "https://storage.test", StorageFingerprint: strings.Repeat("ab", 32), EncryptedCode: "encrypted", ComparisonCode: "123456", ExpiresAt: time.Now().Add(time.Minute)}
	if err := savePendingNearby(configPath, pending); err != nil {
		t.Fatal(err)
	}
	if err := approveNearbyMarker(configPath, requestID); err != nil {
		t.Fatal(err)
	}
	if err := savePendingNearby(configPath, pending); err != nil {
		t.Fatal(err)
	}
	if !pendingNearbyApproved(configPath, requestID) {
		t.Fatal("later claim save lost approval")
	}
}

func mustPort(t *testing.T, value string) int {
	t.Helper()
	var port int
	if _, err := fmt.Sscan(value, &port); err != nil {
		t.Fatal(err)
	}
	return port
}
