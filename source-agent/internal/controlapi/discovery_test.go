package controlapi

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"encoding/pem"
	"io"
	"math/big"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func pairedTLSFixture(t *testing.T, handler http.Handler) (*httptest.Server, *http.Client) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(1), DNSNames: []string{"storage.internal"}, NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth, x509.ExtKeyUsageClientAuth}}
	raw, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	keyBytes, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}
	certPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: raw})
	keyPEM := pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyBytes})
	pair, err := tls.X509KeyPair(certPEM, keyPEM)
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewUnstartedServer(handler)
	server.TLS = &tls.Config{Certificates: []tls.Certificate{pair}, ClientAuth: tls.RequireAnyClientCert}
	server.StartTLS()
	t.Cleanup(server.Close)
	dir := t.TempDir()
	certPath, keyPath := filepath.Join(dir, "cert.pem"), filepath.Join(dir, "key.pem")
	if err := os.WriteFile(certPath, certPEM, 0600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(keyPath, keyPEM, 0600); err != nil {
		t.Fatal(err)
	}
	client, err := NewMTLSHTTPClient(certPath, certPath, keyPath, server.URL)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(client.CloseIdleConnections)
	return server, client
}

func TestLANDiscoveryAcceptsOnlyMatchingReplyAndAuthenticatedStorage(t *testing.T) {
	server, client := pairedTLSFixture(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(204) }))
	address, _ := url.Parse(server.URL)
	transport := client.Transport.(*http.Transport)
	pinned := server.TLS.Certificates[0].Certificate[0]
	dialer := &lanDialer{host: "192.0.2.200", controlPort: address.Port(), fingerprint: certificateFingerprint(pinned), tlsConfig: transport.TLSClientConfig}
	responder, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer responder.Close()
	go func() {
		buffer := make([]byte, 512)
		n, sender, err := responder.ReadFromUDP(buffer)
		if err != nil {
			return
		}
		var query discoveryMessage
		if json.Unmarshal(buffer[:n], &query) != nil {
			return
		}
		query.Port = responder.LocalAddr().(*net.UDPAddr).Port // Wrong control port must not be accepted.
		packet, _ := json.Marshal(query)
		responder.WriteToUDP(packet, sender)
		query.Port = server.Listener.Addr().(*net.TCPAddr).Port
		nonce := query.Nonce
		query.Nonce = strings.Repeat("0", 32)
		packet, _ = json.Marshal(query)
		responder.WriteToUDP(packet, sender)
		query.Nonce = nonce
		packet, _ = json.Marshal(query)
		responder.WriteToUDP(packet, sender)
	}()
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	attempts := 0
	conn, err := discoverStorage(ctx, dialer.fingerprint, dialer.controlPort, []net.UDPAddr{*responder.LocalAddr().(*net.UDPAddr)}, func(host string) (net.Conn, error) {
		attempts++
		return dialer.connect(ctx, host, address.Port())
	})
	if err != nil {
		t.Fatal(err)
	}
	conn.Close()
	if attempts != 1 {
		t.Fatalf("accepted %d discovery replies, want 1", attempts)
	}
	// A different server cannot impersonate this one, even with the same DNS name.
	var leaked atomic.Int32
	other, _ := pairedTLSFixture(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { leaked.Add(1) }))
	otherURL, _ := url.Parse(other.URL)
	if conn, err := dialer.connect(ctx, "127.0.0.1", otherURL.Port()); err == nil {
		conn.Close()
		t.Fatal("accepted an unpaired Storage identity")
	}
	if leaked.Load() != 0 {
		t.Fatal("request reached an unpaired Storage")
	}
}

func TestRepositoryBridgeKeepsTLSIdentityAndRequiresLocalCredentials(t *testing.T) {
	server, client := pairedTLSFixture(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		user, password, _ := r.BasicAuth()
		if user != "repository" || password != "secret" || r.URL.Path != "/repo/config" {
			t.Errorf("incorrect upstream request: %s", r.URL.Path)
		}
		w.Write([]byte("verified repository"))
	}))
	target, _ := url.Parse(server.URL + "/repo/")
	target.User = url.UserPassword("repository", "secret")
	local, closeBridge, err := RepositoryBridge(client, "rest:"+target.String())
	if err != nil {
		t.Fatal(err)
	}
	defer closeBridge()
	response, err := http.Get(strings.TrimPrefix(local, "rest:") + "config")
	if err != nil {
		t.Fatal(err)
	}
	body, _ := io.ReadAll(response.Body)
	response.Body.Close()
	if response.StatusCode != 200 || string(body) != "verified repository" {
		t.Fatalf("bridge failed: %d %s", response.StatusCode, body)
	}
	anonymous, _ := url.Parse(strings.TrimPrefix(local, "rest:"))
	anonymous.User = nil
	response, err = http.Get(anonymous.String())
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if response.StatusCode != 401 {
		t.Fatalf("anonymous bridge access returned %d", response.StatusCode)
	}
}
