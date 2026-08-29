package controlapi

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestNewMTLSHTTPClientRejectsInvalidCA(t *testing.T) {
	dir := t.TempDir()
	ca := filepath.Join(dir, "ca.pem")
	if err := os.WriteFile(ca, []byte("not a certificate"), 0o600); err != nil {
		t.Fatal(err)
	}
	_, err := NewMTLSHTTPClient(ca, filepath.Join(dir, "source.crt"), filepath.Join(dir, "source.key"))
	if err == nil || !strings.Contains(err.Error(), "no valid certificates") {
		t.Fatalf("error = %v", err)
	}
}
