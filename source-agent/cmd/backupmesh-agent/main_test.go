package main

import (
	"errors"
	"strings"
	"testing"

	"github.com/namioto/backupmesh/source-agent/internal/controlapi"
)

func TestRunBackupTargetsContinuesAfterFailure(t *testing.T) {
	targets := []controlapi.BackupTargetAvailability{
		{DeviceName: "First", DestinationFolder: "one"},
		{DeviceName: "Second", DestinationFolder: "two"},
	}
	var attempted []string
	err := runBackupTargets(targets, func(target controlapi.BackupTargetAvailability) error {
		attempted = append(attempted, target.DeviceName)
		if target.DeviceName == "First" {
			return errors.New("offline")
		}
		return nil
	})

	if strings.Join(attempted, ",") != "First,Second" {
		t.Fatalf("attempted targets = %v, want both targets in order", attempted)
	}
	if err == nil || !strings.Contains(err.Error(), "First (one): offline") {
		t.Fatalf("error = %v, want contextual first-target failure", err)
	}
}

func TestRunBackupTargetsAggregatesFailures(t *testing.T) {
	targets := []controlapi.BackupTargetAvailability{
		{DeviceName: "First", DestinationFolder: "one"},
		{DeviceName: "Second", DestinationFolder: "two"},
	}
	err := runBackupTargets(targets, func(target controlapi.BackupTargetAvailability) error {
		return errors.New("failed " + target.DeviceName)
	})

	if err == nil || !strings.Contains(err.Error(), "failed First") || !strings.Contains(err.Error(), "failed Second") {
		t.Fatalf("error = %v, want both target failures", err)
	}
}
