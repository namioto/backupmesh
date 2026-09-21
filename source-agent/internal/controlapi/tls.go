package controlapi

import (
	"crypto/tls"
	"crypto/x509"
	"encoding/pem"
	"fmt"
	"net/http"
	"net/url"
	"os"
	"time"
)

func NewMTLSHTTPClient(caFile, certificateFile, keyFile string, endpoint ...string) (*http.Client, error) {
	caPEM, err := os.ReadFile(caFile)
	if err != nil {
		return nil, fmt.Errorf("read storage agent CA: %w", err)
	}
	roots := x509.NewCertPool()
	if !roots.AppendCertsFromPEM(caPEM) {
		return nil, fmt.Errorf("storage agent CA file contains no valid certificates")
	}
	certificate, err := tls.LoadX509KeyPair(certificateFile, keyFile)
	if err != nil {
		return nil, fmt.Errorf("load source agent client certificate: %w", err)
	}
	transport := http.DefaultTransport.(*http.Transport).Clone()
	transport.TLSClientConfig = &tls.Config{MinVersion: tls.VersionTLS13, RootCAs: roots, Certificates: []tls.Certificate{certificate}}
	// Pairing stores the self-signed server certificate, not a public CA. Its authenticated
	// name and identity remain valid when DHCP assigns a different network address.
	block, _ := pem.Decode(caPEM)
	if len(endpoint) > 0 && block != nil {
		pinned, parseErr := x509.ParseCertificate(block.Bytes)
		address, urlErr := url.Parse(endpoint[0])
		if parseErr == nil && urlErr == nil && address.Scheme == "https" && address.Hostname() != "" &&
			!pinned.IsCA && pinned.CheckSignature(pinned.SignatureAlgorithm, pinned.RawTBSCertificate, pinned.Signature) == nil {
			name := ""
			if len(pinned.DNSNames) > 0 {
				name = pinned.DNSNames[0]
			} else if len(pinned.IPAddresses) > 0 {
				name = pinned.IPAddresses[0].String()
			}
			if name != "" {
				transport.Proxy = nil // Discovery is restricted to direct LAN connections.
				transport.TLSClientConfig.ServerName = name
				transport.TLSClientConfig.VerifyConnection = func(state tls.ConnectionState) error {
					if !state.PeerCertificates[0].Equal(pinned) {
						return fmt.Errorf("Storage identity does not match the paired certificate")
					}
					return nil
				}
				port := address.Port()
				if port == "" {
					port = "443"
				}
				dialer := &lanDialer{host: address.Hostname(), controlPort: port, fingerprint: certificateFingerprint(pinned.Raw), tlsConfig: transport.TLSClientConfig}
				transport.DialTLSContext = dialer.DialTLSContext
			}
		}
	}
	return &http.Client{Transport: transport, Timeout: 2 * time.Minute}, nil
}
