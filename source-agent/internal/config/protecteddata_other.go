//go:build !windows

package config

// Protect is a no-op on non-Windows platforms: protection there comes from the 0600 file mode
// already applied when the secret is written (a Linux root-only file), not from encrypting the
// content itself.
func Protect(data []byte) ([]byte, error) { return data, nil }

// TryUnprotect always reports the data as not DPAPI-protected on non-Windows platforms, so callers
// use it as-is.
func TryUnprotect(data []byte) ([]byte, bool) { return data, false }
