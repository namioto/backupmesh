package main

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/json"
	"encoding/pem"
	"math/big"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/namioto/backupmesh/source-agent/internal/config"
	"github.com/namioto/backupmesh/source-agent/internal/controlapi"
)

func testCertificateExpiringAt(t *testing.T, commonName string, notAfter time.Time) (string, string) {
	t.Helper()
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		t.Fatal(err)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: commonName}, NotBefore: time.Now().Add(-time.Hour), NotAfter: notAfter, KeyUsage: x509.KeyUsageDigitalSignature}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	return string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})), string(pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: x509.MarshalPKCS1PrivateKey(key)}))
}

func renewalTestConfig(t *testing.T, agentID string, currentExpiry time.Time) (config.Config, string, string, string) {
	t.Helper()
	directory := t.TempDir()
	certPath := filepath.Join(directory, "source.crt")
	keyPath := filepath.Join(directory, "source.key")
	caPath := filepath.Join(directory, "storage-ca.pem")
	certPEM, keyPEM := testCertificateExpiringAt(t, agentID, currentExpiry)
	if err := os.WriteFile(certPath, []byte(certPEM), 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(keyPath, []byte(keyPEM), 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(caPath, []byte(certPEM), 0600); err != nil {
		t.Fatal(err)
	}
	cfg := config.Config{Agent: config.Agent{ID: agentID}, Storage: config.Storage{TLSCertificateFile: certPath, TLSKeyFile: keyPath, TLSCAFile: caPath}}
	return cfg, certPath, keyPath, caPath
}

func TestRenewCertificateIfNeededDoesNothingWhenFarFromExpiry(t *testing.T) {
	agentID := "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"
	cfg, _, _, _ := renewalTestConfig(t, agentID, time.Now().Add(60*24*time.Hour))
	server := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {
		t.Fatal("renewal must not contact the Storage Service when the certificate is not close to expiry")
	}))
	defer server.Close()
	api := controlapi.Client{BaseURL: server.URL + "/api/v1", HTTPClient: server.Client()}

	client, err := renewCertificateIfNeeded(context.Background(), api, cfg)

	if err != nil {
		t.Fatal(err)
	}
	if client != nil {
		t.Fatal("expected no replacement client when renewal was not needed")
	}
}

func TestRenewCertificateIfNeededReplacesFilesWhenCloseToExpiry(t *testing.T) {
	agentID := "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"
	cfg, certPath, keyPath, caPath := renewalTestConfig(t, agentID, time.Now().Add(5*24*time.Hour))
	renewedExpiry := time.Now().Add(365 * 24 * time.Hour)
	renewedCertPEM, renewedKeyPEM := testCertificateExpiringAt(t, agentID, renewedExpiry)
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/v1/certificate/renew" || r.Method != http.MethodPost {
			t.Fatalf("unexpected request: %s %s", r.Method, r.URL.Path)
		}
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(controlapi.RenewCertificateResponse{
			CertificatePEM: renewedCertPEM, PrivateKeyPEM: renewedKeyPEM, AuthorityPEM: renewedCertPEM, ExpiresAt: renewedExpiry,
		})
	}))
	defer server.Close()
	api := controlapi.Client{BaseURL: server.URL + "/api/v1", HTTPClient: server.Client()}

	client, err := renewCertificateIfNeeded(context.Background(), api, cfg)

	if err != nil {
		t.Fatal(err)
	}
	if client == nil {
		t.Fatal("expected a replacement mTLS client after renewal")
	}
	onDiskCert, err := os.ReadFile(certPath)
	if err != nil {
		t.Fatal(err)
	}
	if string(onDiskCert) != renewedCertPEM {
		t.Fatal("renewed certificate was not written to the configured certificate file")
	}
	onDiskKey, err := os.ReadFile(keyPath)
	if err != nil {
		t.Fatal(err)
	}
	if string(onDiskKey) != renewedKeyPEM {
		t.Fatal("renewed private key was not written to the configured key file")
	}
	onDiskCA, err := os.ReadFile(caPath)
	if err != nil {
		t.Fatal(err)
	}
	if string(onDiskCA) != renewedCertPEM {
		t.Fatal("renewed authority PEM was not written to the configured CA file")
	}
}

func TestRenewCertificateIfNeededRejectsAMismatchedIdentityAndLeavesFilesAlone(t *testing.T) {
	agentID := "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"
	cfg, certPath, _, _ := renewalTestConfig(t, agentID, time.Now().Add(5*24*time.Hour))
	originalCert, err := os.ReadFile(certPath)
	if err != nil {
		t.Fatal(err)
	}
	wrongCertPEM, wrongKeyPEM := testCertificateExpiringAt(t, "wrong-agent-id", time.Now().Add(365*24*time.Hour))
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(controlapi.RenewCertificateResponse{CertificatePEM: wrongCertPEM, PrivateKeyPEM: wrongKeyPEM, AuthorityPEM: wrongCertPEM, ExpiresAt: time.Now().Add(365 * 24 * time.Hour)})
	}))
	defer server.Close()
	api := controlapi.Client{BaseURL: server.URL + "/api/v1", HTTPClient: server.Client()}

	_, err = renewCertificateIfNeeded(context.Background(), api, cfg)

	if err == nil {
		t.Fatal("expected an error when the renewed certificate identity does not match agent_id")
	}
	afterCert, readErr := os.ReadFile(certPath)
	if readErr != nil {
		t.Fatal(readErr)
	}
	if string(afterCert) != string(originalCert) {
		t.Fatal("the original certificate file must not be replaced when renewal is rejected")
	}
}
