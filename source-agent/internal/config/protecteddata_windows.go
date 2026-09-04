//go:build windows

package config

import (
	"fmt"
	"syscall"
	"unsafe"
)

var (
	modcrypt32    = syscall.NewLazyDLL("crypt32.dll")
	modkernel32   = syscall.NewLazyDLL("kernel32.dll")
	procProtect   = modcrypt32.NewProc("CryptProtectData")
	procUnprotect = modcrypt32.NewProc("CryptUnprotectData")
	procLocalFree = modkernel32.NewProc("LocalFree")
)

const cryptProtectUIForbidden = 0x1

type dataBlob struct {
	cbData uint32
	pbData *byte
}

func newDataBlob(data []byte) *dataBlob {
	if len(data) == 0 {
		return &dataBlob{}
	}
	return &dataBlob{cbData: uint32(len(data)), pbData: &data[0]}
}

func (b *dataBlob) copyBytes() []byte {
	if b.cbData == 0 || b.pbData == nil {
		return nil
	}
	return append([]byte(nil), unsafe.Slice(b.pbData, b.cbData)...)
}

// Protect encrypts data for the current Windows user account via DPAPI, so only processes running
// as this user on this machine can decrypt it. Used to store the repository password and other
// generated secrets without ever asking the user to manage a key themselves.
func Protect(data []byte) ([]byte, error) {
	in := newDataBlob(data)
	var out dataBlob
	ret, _, callErr := procProtect.Call(uintptr(unsafe.Pointer(in)), 0, 0, 0, 0, cryptProtectUIForbidden, uintptr(unsafe.Pointer(&out)))
	if ret == 0 {
		return nil, fmt.Errorf("CryptProtectData: %w", callErr)
	}
	defer procLocalFree.Call(uintptr(unsafe.Pointer(out.pbData)))
	return out.copyBytes(), nil
}

// TryUnprotect decrypts DPAPI-protected data. If data is not a DPAPI blob — such as a plaintext
// password file a user created by hand — it returns the input unchanged and reports false, so
// callers can fall back to treating it as already-plaintext.
func TryUnprotect(data []byte) ([]byte, bool) {
	in := newDataBlob(data)
	var out dataBlob
	ret, _, _ := procUnprotect.Call(uintptr(unsafe.Pointer(in)), 0, 0, 0, 0, cryptProtectUIForbidden, uintptr(unsafe.Pointer(&out)))
	if ret == 0 {
		return data, false
	}
	defer procLocalFree.Call(uintptr(unsafe.Pointer(out.pbData)))
	return out.copyBytes(), true
}
