//go:build windows

package main

import (
	"errors"
	"path/filepath"
	"syscall"
	"time"
)

const errorSharingViolation syscall.Errno = 32

func lockPairing(configPath string) (func(), error) {
	path, err := syscall.UTF16PtrFromString(filepath.Join(filepath.Dir(configPath), "pairing.lock"))
	if err != nil {
		return nil, err
	}
	deadline := time.Now().Add(20 * time.Second)
	for {
		handle, err := syscall.CreateFile(path, syscall.GENERIC_READ|syscall.GENERIC_WRITE, 0, nil, syscall.OPEN_ALWAYS, syscall.FILE_ATTRIBUTE_NORMAL, 0)
		if err == nil {
			return func() { syscall.CloseHandle(handle) }, nil
		}
		if err != errorSharingViolation {
			return nil, err
		}
		if time.Now().After(deadline) {
			return nil, errors.New("another pairing operation is still running")
		}
		time.Sleep(100 * time.Millisecond)
	}
}
