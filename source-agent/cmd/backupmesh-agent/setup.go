package main

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/namioto/backupmesh/source-agent/internal/config"
)

const setupPairingWait = 2 * time.Minute

func runSetup(ctx context.Context, input io.Reader, output io.Writer, configPath, pairingOutput string) error {
	cfg, err := config.LoadUserConfig(configPath)
	if err != nil {
		return err
	}
	fmt.Fprintf(output, "Computer name: %s\nBackup folders:\n", cfg.Agent.Name)
	for _, set := range cfg.BackupSets {
		for _, path := range set.Paths {
			fmt.Fprintf(output, "  %s\n", path)
		}
	}

	reader := bufio.NewReader(input)
	var additions []string
	for {
		fmt.Fprint(output, "Add backup folder (absolute existing path, for example /home/you/Documents; Enter to continue): ")
		line, readErr := reader.ReadString('\n')
		path := strings.TrimSpace(line)
		if readErr != nil && path == "" {
			if errors.Is(readErr, io.EOF) {
				fmt.Fprintln(output, "Setup cancelled; configuration was not changed.")
				return nil
			}
			return readErr
		}
		if path == "" {
			break
		}
		path, err = validateBackupFolder(path)
		if err != nil {
			fmt.Fprintf(output, "Cannot add folder: %v\n", err)
			continue
		}
		additions = append(additions, path)
	}
	if len(additions) > 0 {
		if err := appendSetupFolders(configPath, additions); err != nil {
			return err
		}
		fmt.Fprintln(output, "Backup folders saved.")
	}
	if _, err := config.Load(configPath); err == nil {
		fmt.Fprintln(output, "Storage pairing is already configured. Setup complete.")
		return nil
	}
	fmt.Fprint(output, "Connect to Storage now? [Y/n]: ")
	line, readErr := reader.ReadString('\n')
	if readErr != nil && len(line) == 0 {
		fmt.Fprintln(output, "Setup finished without pairing. Run setup again when Storage is ready.")
		return nil
	}
	if answer := strings.ToLower(strings.TrimSpace(line)); answer == "n" || answer == "no" {
		fmt.Fprintln(output, "Setup finished without pairing. Run setup again when Storage is ready.")
		return nil
	}

	fmt.Fprintln(output, "Waiting up to 2 minutes for a Storage pairing request. Start pairing in the Storage app.")
	started := time.Now()
	deadline := started.Add(setupPairingWait)
	nextFeedback := started.Add(5 * time.Second)
	for time.Now().Before(deadline) {
		if _, err := config.Load(configPath); err == nil {
			fmt.Fprintln(output, "Storage pairing settings saved. Setup complete.")
			return nil
		}
		pending, err := loadPendingNearby(configPath)
		if err != nil {
			return err
		}
		active := pending[:0]
		for _, item := range pending {
			if !item.Denied && !item.Approved && time.Until(item.ExpiresAt) > 0 {
				active = append(active, item)
			}
		}
		pending = active
		if len(pending) > 0 {
			selected, approved, rejected, err := promptPairingDecision(reader, output, pending)
			if err != nil {
				return err
			}
			if rejected {
				if err := denyPendingNearby(configPath, pairingOutput, selected.RequestID); err != nil {
					return err
				}
				fmt.Fprintln(output, "Pairing request denied. Run setup again when ready.")
				return nil
			}
			if !approved {
				fmt.Fprintln(output, "Pairing was not approved. Run setup again when ready.")
				return nil
			}
			if err := approvePendingNearby(configPath, pairingOutput, selected.RequestID); err != nil {
				return err
			}
			fmt.Fprintln(output, "Approved. Waiting for the service to finish pairing.")
			for time.Now().Before(deadline) {
				if _, err := config.Load(configPath); err == nil {
					fmt.Fprintln(output, "Storage pairing settings saved. Setup complete.")
					return nil
				}
				if !sleepContext(ctx, 500*time.Millisecond) {
					return nil
				}
			}
			break
		}
		if time.Now().After(nextFeedback) {
			fmt.Fprintf(output, "Still waiting for Storage (%s elapsed).\n", time.Since(started).Round(time.Second))
			nextFeedback = time.Now().Add(5 * time.Second)
		}
		if !sleepContext(ctx, 500*time.Millisecond) {
			return nil
		}
	}

	fmt.Fprintln(output, "Nearby pairing did not complete. Check that both computers are on the same LAN. On Ubuntu, rerun the latest installer so it can allow LAN discovery through active UFW (UDP 7445-7446).")
	fmt.Fprintln(output, "You can rerun setup after fixing LAN access, or bypass discovery with a connection invitation.")
	fmt.Fprint(output, "Connection invitation (Enter to finish): ")
	line, readErr = reader.ReadString('\n')
	invitationText := strings.TrimSpace(line)
	if readErr != nil && !errors.Is(readErr, io.EOF) {
		return readErr
	}
	if invitationText == "" {
		fmt.Fprintln(output, "Setup finished without pairing. Run setup again when Storage is ready.")
		return nil
	}
	invitation, err := parsePairingInvitation(invitationText)
	if err != nil {
		return err
	}
	return pairWithCode(configPath, invitation.Endpoint, invitation.Code, invitation.Fingerprint, pairingOutput)
}

func validateBackupFolder(path string) (string, error) {
	if !filepath.IsAbs(path) {
		return "", errors.New("path must be absolute")
	}
	path = filepath.Clean(path)
	directory, err := os.Open(path)
	if err != nil {
		return "", fmt.Errorf("open %q: %w", path, err)
	}
	defer directory.Close()
	info, err := directory.Stat()
	if err != nil {
		return "", fmt.Errorf("read %q: %w", path, err)
	}
	if !info.IsDir() {
		return "", fmt.Errorf("%q is not a directory", path)
	}
	if _, err := directory.Readdirnames(1); err != nil && !errors.Is(err, io.EOF) {
		return "", fmt.Errorf("read %q: %w", path, err)
	}
	return path, nil
}

func appendSetupFolders(configPath string, additions []string) error {
	unlock, err := lockPairing(configPath)
	if err != nil {
		return err
	}
	defer unlock()
	cfg, err := config.LoadUserConfig(configPath)
	if err != nil {
		return err
	}
	known := map[string]bool{}
	for _, set := range cfg.BackupSets {
		for _, path := range set.Paths {
			known[filepath.Clean(path)] = true
		}
	}
	for _, path := range additions {
		if known[path] {
			continue
		}
		name := filepath.Base(path)
		if name == "." || name == string(filepath.Separator) || name == "" {
			name = "root"
		}
		base := name
		for suffix := 2; cfgHasSetName(cfg, name); suffix++ {
			name = base + "-" + strconv.Itoa(suffix)
		}
		cfg.BackupSets = append(cfg.BackupSets, config.BackupSet{Name: name, Paths: []string{path}})
		known[path] = true
	}
	if err := config.ResolveIdentityState(configPath, &cfg); err != nil {
		return err
	}
	if err := cfg.ValidateUserAuthored(); err != nil {
		return err
	}
	encoded, err := config.Marshal(cfg, configPath)
	if err != nil {
		return fmt.Errorf("encode config: %w", err)
	}
	return writePrivateFile(configPath, append(encoded, '\n'))
}

func cfgHasSetName(cfg config.Config, name string) bool {
	_, found := cfg.FindBackupSet(name)
	return found
}

func promptPairingDecision(reader *bufio.Reader, output io.Writer, pending []pendingNearbyRequest) (pendingNearbyRequest, bool, bool, error) {
	selected := 0
	if len(pending) > 1 {
		fmt.Fprintln(output, "Pending Storage requests:")
		for i, item := range pending {
			fmt.Fprintf(output, "  %d. code %s (expires %s)\n", i+1, item.ComparisonCode, item.ExpiresAt.Local().Format(time.RFC3339))
		}
		fmt.Fprint(output, "Choose request number: ")
		line, err := reader.ReadString('\n')
		if err != nil && len(line) == 0 {
			return pendingNearbyRequest{}, false, false, nil
		}
		choice, err := strconv.Atoi(strings.TrimSpace(line))
		if err != nil || choice < 1 || choice > len(pending) {
			return pendingNearbyRequest{}, false, false, errors.New("invalid pairing request selection")
		}
		selected = choice - 1
	}
	item := pending[selected]
	fmt.Fprintf(output, "Compare this 6-digit code with Storage: %s\nApprove this Storage? [y/N]: ", item.ComparisonCode)
	line, err := reader.ReadString('\n')
	if err != nil && len(line) == 0 {
		return item, false, false, nil
	}
	answer := strings.ToLower(strings.TrimSpace(line))
	return item, answer == "y" || answer == "yes", answer == "" || answer == "n" || answer == "no", nil
}
