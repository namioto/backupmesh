package config

import (
	"runtime"
	"testing"
)

func TestProtectRoundTripsOnWindows(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("Protect/TryUnprotect only wrap DPAPI on Windows; elsewhere they are no-ops")
	}
	plaintext := []byte("a-generated-repository-password")
	protected, err := Protect(plaintext)
	if err != nil {
		t.Fatalf("Protect() error = %v", err)
	}
	if string(protected) == string(plaintext) {
		t.Fatal("Protect() returned the plaintext unchanged; expected a DPAPI blob")
	}
	recovered, wasProtected := TryUnprotect(protected)
	if !wasProtected {
		t.Fatal("TryUnprotect() reported the DPAPI blob it just decrypted as not protected")
	}
	if string(recovered) != string(plaintext) {
		t.Fatalf("TryUnprotect() = %q, want %q", recovered, plaintext)
	}
}

func TestTryUnprotectFallsBackOnPlaintextInput(t *testing.T) {
	plaintext := []byte("a-user-chosen-password")
	recovered, wasProtected := TryUnprotect(plaintext)
	if wasProtected {
		t.Fatal("TryUnprotect() claimed plain, non-DPAPI bytes were protected")
	}
	if string(recovered) != string(plaintext) {
		t.Fatalf("TryUnprotect() = %q, want the input unchanged: %q", recovered, plaintext)
	}
}
