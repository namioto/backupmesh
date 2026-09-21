package controlapi

import (
	"crypto/rand"
	"crypto/subtle"
	"encoding/hex"
	"fmt"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"strings"
	"time"
)

// restic does not expose a TLS server-name/address override. A short-lived authenticated
// loopback bridge lets it use the same verified, DHCP-aware transport as the control API.
// The LAN hop remains HTTPS; no credentials or backup data are sent over plaintext LAN.
func RepositoryBridge(client *http.Client, repository string) (string, func(), error) {
	if client == nil {
		return repository, func() {}, nil
	}
	transport, ok := client.Transport.(*http.Transport)
	if !ok || transport.DialTLSContext == nil || !strings.HasPrefix(repository, "rest:https://") {
		return repository, func() {}, nil
	}
	target, err := url.Parse(strings.TrimPrefix(repository, "rest:"))
	if err != nil || target.Hostname() == "" {
		return "", nil, fmt.Errorf("invalid Storage repository endpoint")
	}
	secret := make([]byte, 32)
	if _, err := rand.Read(secret); err != nil {
		return "", nil, err
	}
	password := hex.EncodeToString(secret)
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return "", nil, err
	}
	proxy := &httputil.ReverseProxy{
		Transport: transport,
		Rewrite: func(request *httputil.ProxyRequest) {
			request.SetURL(target)
			request.Out.Header.Del("Authorization")
			if target.User != nil {
				password, _ := target.User.Password()
				request.Out.SetBasicAuth(target.User.Username(), password)
			}
		},
		ErrorHandler: func(w http.ResponseWriter, r *http.Request, err error) {
			http.Error(w, "Storage connection unavailable", http.StatusBadGateway)
		},
	}
	server := &http.Server{ReadHeaderTimeout: 10 * time.Second, MaxHeaderBytes: 32 << 10,
		Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			user, supplied, ok := r.BasicAuth()
			if !ok || user != "backupmesh" || subtle.ConstantTimeCompare([]byte(supplied), []byte(password)) != 1 {
				http.Error(w, "Unauthorized", http.StatusUnauthorized)
				return
			}
			proxy.ServeHTTP(w, r)
		}),
	}
	go server.Serve(listener)
	local := &url.URL{Scheme: "http", Host: listener.Addr().String(), Path: "/", User: url.UserPassword("backupmesh", password)}
	return "rest:" + local.String(), func() { server.Close() }, nil
}
