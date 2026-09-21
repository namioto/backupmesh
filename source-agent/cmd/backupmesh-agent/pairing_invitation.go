package main

import (
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net/url"
	"strings"
)

type pairingInvitation struct {
	Endpoint    string `json:"endpoint"`
	Code        string `json:"code"`
	Fingerprint string `json:"fingerprint"`
}

func parsePairingInvitation(value string) (pairingInvitation, error) {
	var invitation pairingInvitation
	invalid := errors.New("invalid connection invitation; copy it again from the Storage app")
	value = strings.TrimSpace(value)
	if len(value) > 4096 || !strings.HasPrefix(value, "backupmesh:v1:") {
		return invitation, invalid
	}
	data, err := base64.RawURLEncoding.DecodeString(strings.TrimPrefix(value, "backupmesh:v1:"))
	if err != nil {
		return invitation, invalid
	}
	decoder := json.NewDecoder(strings.NewReader(string(data)))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&invitation) != nil {
		return invitation, invalid
	}
	var extra any
	if decoder.Decode(&extra) != io.EOF {
		return invitation, invalid
	}
	endpoint, err := url.Parse(invitation.Endpoint)
	if err != nil || endpoint.Scheme != "https" || endpoint.Hostname() == "" || endpoint.User != nil ||
		(endpoint.Path != "" && endpoint.Path != "/") || endpoint.RawQuery != "" || endpoint.Fragment != "" {
		return invitation, invalid
	}
	fingerprint, err := hex.DecodeString(invitation.Fingerprint)
	if err != nil || len(fingerprint) != 32 || strings.TrimSpace(invitation.Code) == "" || len(invitation.Code) > 128 {
		return invitation, invalid
	}
	return invitation, nil
}
