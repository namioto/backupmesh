# Mutual TLS configuration

BackupMesh can expose a remote TLS 1.3 Control API that requires a client certificate. Keep the loopback endpoint for the Windows tray app and use a private CA to issue separate server and Source Agent certificates.

On the Storage Agent, set `MutualTls:Enabled`, `Port`, `ServerCertificatePath` (PKCS#12/PFX), optional `ServerCertificatePassword`, and `ClientCertificateAuthorityPath` (PEM). The service rejects a remote TLS connection unless its certificate chains to that CA and has the TLS client-auth extended key usage.

On each Source Agent, use an `https` `controlEndpoint` and set all three absolute paths: `tlsCaFile`, `tlsCertificateFile`, and `tlsKeyFile`. The CA validates the Storage Agent and the certificate/key identify that Source. BackupMesh permits TLS 1.3 only on this endpoint. The bearer token remains available as a migration fallback and should be removed after every Source has its own certificate.
