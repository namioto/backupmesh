package main

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"time"

	"github.com/namioto/backupmesh/source-agent/internal/config"
	"github.com/namioto/backupmesh/source-agent/internal/controlapi"
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
	var announceErrors <-chan error
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
		fmt.Fprint(output, "Storage connection is already configured. Reset it and connect again? Existing backup folders and settings will be kept. [y/N]: ")
		line, readErr := reader.ReadString('\n')
		answer := strings.ToLower(strings.TrimSpace(line))
		if readErr != nil && !errors.Is(readErr, io.EOF) {
			return readErr
		}
		if answer != "y" && answer != "yes" {
			fmt.Fprintln(output, "Existing Storage connection kept. Setup complete.")
			return nil
		}
		if err := resetSetupPairing(configPath, pairingOutput); err != nil {
			return err
		}
		fmt.Fprintln(output, "Old connection removed. This computer now has a fresh identity for connecting again.")
		resetConfig, err := config.LoadUserConfig(configPath)
		if err != nil {
			return err
		}
		announceContext, stopAnnouncing := context.WithCancel(ctx)
		defer stopAnnouncing()
		errors := make(chan error, 1)
		announceErrors = errors
		go func() { errors <- runNearbyWatch(announceContext, configPath, pairingOutput, resetConfig) }()
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
		select {
		case err := <-announceErrors:
			announceErrors = nil
			if err != nil {
				return fmt.Errorf("announce fresh identity: %w", err)
			}
		default:
		}
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

func resetSetupPairing(configPath, pairingOutput string) error {
	unlock, err := lockPairing(configPath)
	if err != nil {
		return err
	}
	defer unlock()
	cfg, err := config.LoadUserConfig(configPath)
	if err != nil {
		return err
	}
	nearby, err := filepath.Abs(nearbyDirectory(configPath, pairingOutput))
	if err != nil {
		return err
	}
	if cfg.Storage.RepositoryPasswordFile != "" {
		password, err := filepath.Abs(cfg.Storage.RepositoryPasswordFile)
		if err != nil {
			return err
		}
		for _, name := range []string{"control.token", "source.crt", "source.key", "storage-ca.pem", "nearby-identity.pem"} {
			if samePath(password, filepath.Join(nearby, name)) {
				return errors.New("repository password overlaps managed pairing credentials; move it before resetting the connection")
			}
		}
		if relative, err := filepath.Rel(filepath.Join(nearby, "pending"), password); err == nil && relative != ".." && !strings.HasPrefix(relative, ".."+string(filepath.Separator)) {
			return errors.New("repository password is inside pending pairing data; move it before resetting the connection")
		}
	}
	newID, err := controlapi.UUIDv4()
	if err != nil {
		return err
	}
	cfg.Agent.ID = newID
	cfg.Storage.ControlEndpoint = ""
	cfg.Storage.AuthenticationTokenFile = ""
	cfg.Storage.TLSCAFile = ""
	cfg.Storage.TLSCertificateFile = ""
	cfg.Storage.TLSKeyFile = ""
	if err := cfg.ValidateUserAuthored(); err != nil {
		return err
	}
	encoded, err := config.Marshal(cfg, configPath)
	if err != nil {
		return fmt.Errorf("encode config: %w", err)
	}
	if err := writePrivateFile(configPath, append(encoded, '\n')); err != nil {
		return err
	}
	if err := config.SaveIdentityState(configPath, cfg); err != nil {
		return err
	}
	for _, name := range []string{"control.token", "source.crt", "source.key", "storage-ca.pem", "nearby-identity.pem"} {
		if err := os.Remove(filepath.Join(nearby, name)); err != nil && !errors.Is(err, os.ErrNotExist) {
			return err
		}
	}
	if err := os.RemoveAll(filepath.Join(nearby, "pending")); err != nil {
		return err
	}
	return nil
}

func samePath(left, right string) bool {
	if runtime.GOOS == "windows" {
		return strings.EqualFold(filepath.Clean(left), filepath.Clean(right))
	}
	return filepath.Clean(left) == filepath.Clean(right)
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
