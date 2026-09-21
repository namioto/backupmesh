package main

import (
	"encoding/base64"
	"encoding/json"
	"strings"
	"testing"
)

func TestPairingInvitation(t *testing.T) {
	expected := pairingInvitation{"https://192.168.1.20:7443", "one-time-test-code", strings.Repeat("ab", 32)}
	encode := func(value pairingInvitation) string {
		data, _ := json.Marshal(value)
		return "backupmesh:v1:" + base64.RawURLEncoding.EncodeToString(data)
	}
	actual, err := parsePairingInvitation(" \n" + encode(expected) + "\n")
	if err != nil || actual != expected {
		t.Fatalf("invitation round trip failed: %v", err)
	}
	for _, endpoint := range []string{"http://192.168.1.20:7443", "https://user:secret@host", "https://host/path", "https://host?query=1", "https://host#fragment", "https://"} {
		invalid := expected
		invalid.Endpoint = endpoint
		if _, err := parsePairingInvitation(encode(invalid)); err == nil {
			t.Errorf("accepted endpoint %q", endpoint)
		}
	}
	for _, fingerprint := range []string{"", strings.Repeat("z", 64), "abcd"} {
		invalid := expected
		invalid.Fingerprint = fingerprint
		if _, err := parsePairingInvitation(encode(invalid)); err == nil {
			t.Error("accepted invalid fingerprint")
		}
	}
	for _, value := range []string{"", "backupmesh:v2:abc", "backupmesh:v1:!", strings.Repeat("x", 4097), "backupmesh:v1:" + base64.RawURLEncoding.EncodeToString([]byte(`{"endpoint":"https://host","unknown":1}`))} {
		if _, err := parsePairingInvitation(value); err == nil {
			t.Error("accepted malformed invitation")
		}
	}
}
