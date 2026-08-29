package config

import (
	"crypto/rand"
	"encoding/json"
	"errors"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"strings"

	"gopkg.in/yaml.v3"
)

type Config struct {
	Agent          Agent       `json:"agent" yaml:"agent"`
	Storage        Storage     `json:"storage" yaml:"storage"`
	BackupSets     []BackupSet `json:"backupSets" yaml:"backupSets"`
	UploadLimitBPS int64       `json:"uploadLimitBps,omitempty" yaml:"uploadLimitBps,omitempty"`
}

type Agent struct {
	ID   string `json:"id,omitempty" yaml:"-"`
	Name string `json:"name,omitempty" yaml:"name,omitempty"`
}

type Storage struct {
	ControlEndpoint         string `json:"controlEndpoint" yaml:"controlEndpoint"`
	RepositoryPasswordFile  string `json:"repositoryPasswordFile" yaml:"repositoryPasswordFile"`
	ResticCacheDirectory    string `json:"resticCacheDirectory,omitempty" yaml:"resticCacheDirectory,omitempty"`
	AuthenticationTokenFile string `json:"authenticationTokenFile,omitempty" yaml:"authenticationTokenFile,omitempty"`
	TLSCAFile               string `json:"tlsCaFile,omitempty" yaml:"tlsCaFile,omitempty"`
	TLSCertificateFile      string `json:"tlsCertificateFile,omitempty" yaml:"tlsCertificateFile,omitempty"`
	TLSKeyFile              string `json:"tlsKeyFile,omitempty" yaml:"tlsKeyFile,omitempty"`
}

type BackupSet struct {
	ID      string   `json:"id,omitempty" yaml:"-"`
	Name    string   `json:"name" yaml:"name"`
	Paths   []string `json:"paths" yaml:"paths"`
	Include []string `json:"include,omitempty" yaml:"include,omitempty"`
	Exclude []string `json:"exclude,omitempty" yaml:"exclude,omitempty"`
	Hooks   Hooks    `json:"hooks,omitempty" yaml:"hooks,omitempty"`
}

type Hooks struct {
	Before [][]string `json:"before,omitempty" yaml:"before,omitempty"`
	After  [][]string `json:"after,omitempty" yaml:"after,omitempty"`
}

// Load reads and fully validates a configuration, including the Storage connection fields that only
// exist after pairing. Use it for sync/backup/watch/validate, which need a working connection.
func Load(path string) (Config, error) {
	c, err := load(path)
	if err != nil {
		return Config{}, err
	}
	if err := c.Validate(); err != nil {
		return Config{}, err
	}
	return c, nil
}

// LoadUserConfig reads a configuration without requiring the Storage connection fields (endpoint,
// repository password, credential/certificate paths) to be filled in yet. `pair` uses this so a freshly
// authored config containing only agent.name and backupSets can be paired for the first time; those
// connection fields do not exist until pairing writes them.
func LoadUserConfig(path string) (Config, error) {
	c, err := load(path)
	if err != nil {
		return Config{}, err
	}
	if err := c.ValidateUserAuthored(); err != nil {
		return Config{}, err
	}
	return c, nil
}

func load(path string) (Config, error) {
	b, err := os.ReadFile(filepath.Clean(path))
	if err != nil {
		return Config{}, fmt.Errorf("read config: %w", err)
	}
	c, err := decode(b, filepath.Ext(path))
	if err != nil {
		return Config{}, fmt.Errorf("decode config: %w", err)
	}
	if err := ResolveIdentityState(path, &c); err != nil {
		return Config{}, err
	}
	return c, nil
}

type identityState struct {
	AgentID    string                   `json:"agentId"`
	BackupSets []backupSetIdentityState `json:"backupSets"`
}

type backupSetIdentityState struct {
	ID    string   `json:"id"`
	Name  string   `json:"name"`
	Paths []string `json:"paths"`
}

func ResolveIdentityState(configPath string, c *Config) error {
	statePath := configPath + ".state.json"
	var state identityState
	if contents, err := os.ReadFile(filepath.Clean(statePath)); err == nil {
		if err := json.Unmarshal(contents, &state); err != nil {
			return fmt.Errorf("decode identity state: %w", err)
		}
	} else if !errors.Is(err, os.ErrNotExist) {
		return fmt.Errorf("read identity state: %w", err)
	}
	if strings.TrimSpace(c.Agent.ID) == "" {
		c.Agent.ID = state.AgentID
	}
	if !isUUID(c.Agent.ID) {
		c.Agent.ID = newUUID()
	}
	for i := range c.BackupSets {
		if isUUID(c.BackupSets[i].ID) {
			continue
		}
		matchedIDs := map[string]bool{}
		for _, saved := range state.BackupSets {
			if saved.Name == c.BackupSets[i].Name || slicesEqual(saved.Paths, c.BackupSets[i].Paths) {
				matchedIDs[saved.ID] = true
			}
		}
		switch len(matchedIDs) {
		case 0:
			c.BackupSets[i].ID = newUUID()
		case 1:
			for id := range matchedIDs {
				c.BackupSets[i].ID = id
			}
		default:
			// A simultaneous rename and path change matches one previous backup set by name and a
			// different one by paths. Guessing here risks silently merging or splitting backup
			// history, so this must be resolved by hand instead.
			return fmt.Errorf("backupSets[%d] (name %q) matches more than one previously known backup set by name or paths; rename or move it back to match exactly one, or edit %s to resolve which existing backup set this is", i, c.BackupSets[i].Name, statePath)
		}
	}
	return SaveIdentityState(configPath, *c)
}

func SaveIdentityState(configPath string, c Config) error {
	state := identityState{AgentID: c.Agent.ID, BackupSets: make([]backupSetIdentityState, 0, len(c.BackupSets))}
	for _, set := range c.BackupSets {
		state.BackupSets = append(state.BackupSets, backupSetIdentityState{ID: set.ID, Name: set.Name, Paths: append([]string(nil), set.Paths...)})
	}
	contents, err := json.MarshalIndent(state, "", "  ")
	if err != nil {
		return fmt.Errorf("encode identity state: %w", err)
	}
	statePath := configPath + ".state.json"
	if err := os.MkdirAll(filepath.Dir(statePath), 0700); err != nil {
		return fmt.Errorf("create identity state directory: %w", err)
	}
	temporary := statePath + ".tmp"
	if err := os.WriteFile(temporary, append(contents, '\n'), 0600); err != nil {
		return fmt.Errorf("write identity state: %w", err)
	}
	if err := os.Rename(temporary, statePath); err != nil {
		if removeErr := os.Remove(statePath); removeErr != nil && !errors.Is(removeErr, os.ErrNotExist) {
			_ = os.Remove(temporary)
			return fmt.Errorf("replace identity state: %w", removeErr)
		}
		if retryErr := os.Rename(temporary, statePath); retryErr != nil {
			_ = os.Remove(temporary)
			return fmt.Errorf("replace identity state: %w", retryErr)
		}
	}
	return os.Chmod(statePath, 0600)
}

func newUUID() string {
	bytes := make([]byte, 16)
	if _, err := rand.Read(bytes); err != nil {
		panic(fmt.Sprintf("generate UUID: %v", err))
	}
	bytes[6] = (bytes[6] & 0x0f) | 0x40
	bytes[8] = (bytes[8] & 0x3f) | 0x80
	return fmt.Sprintf("%08x-%04x-%04x-%04x-%012x", bytes[0:4], bytes[4:6], bytes[6:8], bytes[8:10], bytes[10:16])
}

func slicesEqual(left, right []string) bool {
	if len(left) != len(right) {
		return false
	}
	for i := range left {
		if left[i] != right[i] {
			return false
		}
	}
	return true
}

func decode(contents []byte, extension string) (Config, error) {
	var c Config
	if strings.EqualFold(extension, ".yaml") || strings.EqualFold(extension, ".yml") {
		d := yaml.NewDecoder(strings.NewReader(string(contents)))
		d.KnownFields(true)
		if err := d.Decode(&c); err != nil {
			return Config{}, err
		}
		return c, nil
	}
	d := json.NewDecoder(strings.NewReader(string(contents)))
	d.DisallowUnknownFields()
	if err := d.Decode(&c); err != nil {
		return Config{}, err
	}
	return c, nil
}

func Marshal(c Config, path string) ([]byte, error) {
	if strings.EqualFold(filepath.Ext(path), ".yaml") || strings.EqualFold(filepath.Ext(path), ".yml") {
		return yaml.Marshal(c)
	}
	return json.MarshalIndent(c, "", "  ")
}

// Validate checks the full configuration, including the Storage connection fields a Source Agent needs
// to actually run a backup. It fails on a pre-pairing config; see ValidateUserAuthored for that case.
func (c Config) Validate() error {
	var problems []error
	problems = append(problems, c.validateUserAuthored()...)
	u, err := url.Parse(c.Storage.ControlEndpoint)
	if err != nil || u.Scheme == "" || u.Host == "" || (u.Scheme != "https" && u.Scheme != "http") {
		problems = append(problems, errors.New("storage.controlEndpoint must be an absolute HTTP(S) URL"))
	}
	if strings.TrimSpace(c.Storage.RepositoryPasswordFile) == "" {
		problems = append(problems, errors.New("storage.repositoryPasswordFile is required"))
	}
	if strings.TrimSpace(c.Storage.ResticCacheDirectory) != "" && !filepath.IsAbs(c.Storage.ResticCacheDirectory) {
		problems = append(problems, errors.New("storage.resticCacheDirectory must be an absolute path"))
	}
	if strings.TrimSpace(c.Storage.AuthenticationTokenFile) != "" && !filepath.IsAbs(c.Storage.AuthenticationTokenFile) {
		problems = append(problems, errors.New("storage.authenticationTokenFile must be an absolute path"))
	}
	tlsFiles := []string{c.Storage.TLSCAFile, c.Storage.TLSCertificateFile, c.Storage.TLSKeyFile}
	tlsCount := 0
	for _, path := range tlsFiles {
		if strings.TrimSpace(path) != "" {
			tlsCount++
			if !filepath.IsAbs(path) {
				problems = append(problems, errors.New("storage TLS file paths must be absolute"))
			}
		}
	}
	if tlsCount != 0 && tlsCount != len(tlsFiles) {
		problems = append(problems, errors.New("storage tlsCaFile, tlsCertificateFile, and tlsKeyFile must be configured together"))
	}
	return errors.Join(problems...)
}

// ValidateUserAuthored checks only what a user writes by hand: agent identity and backup sets. It
// deliberately does not require the Storage connection fields, which do not exist until `pair` runs.
func (c Config) ValidateUserAuthored() error {
	return errors.Join(c.validateUserAuthored()...)
}

func (c Config) validateUserAuthored() []error {
	var problems []error
	if strings.TrimSpace(c.Agent.ID) == "" {
		problems = append(problems, errors.New("agent.id is required"))
	} else if !isUUID(c.Agent.ID) {
		problems = append(problems, errors.New("agent.id must be a UUID"))
	}
	if strings.TrimSpace(c.Agent.Name) == "" {
		problems = append(problems, errors.New("agent.name is required"))
	}
	if c.UploadLimitBPS < 0 {
		problems = append(problems, errors.New("uploadLimitBps cannot be negative"))
	}
	if len(c.BackupSets) == 0 {
		problems = append(problems, errors.New("at least one backupSet is required"))
	}
	seen := map[string]bool{}
	for i, set := range c.BackupSets {
		prefix := fmt.Sprintf("backupSets[%d]", i)
		if !isUUID(set.ID) {
			problems = append(problems, fmt.Errorf("%s.id must be a UUID", prefix))
		}
		if strings.TrimSpace(set.Name) == "" {
			problems = append(problems, fmt.Errorf("%s.name is required", prefix))
		} else if seen[set.Name] {
			problems = append(problems, fmt.Errorf("%s.name %q is duplicated", prefix, set.Name))
		}
		seen[set.Name] = true
		if len(set.Paths) == 0 {
			problems = append(problems, fmt.Errorf("%s.paths cannot be empty", prefix))
		}
		for _, commands := range append(set.Hooks.Before, set.Hooks.After...) {
			if len(commands) == 0 || strings.TrimSpace(commands[0]) == "" {
				problems = append(problems, fmt.Errorf("%s hook command cannot be empty", prefix))
			}
		}
	}
	return problems
}

func isUUID(s string) bool {
	if len(s) != 36 || s[8] != '-' || s[13] != '-' || s[18] != '-' || s[23] != '-' {
		return false
	}
	for i, r := range s {
		if i == 8 || i == 13 || i == 18 || i == 23 {
			continue
		}
		if !((r >= '0' && r <= '9') || (r >= 'a' && r <= 'f') || (r >= 'A' && r <= 'F')) {
			return false
		}
	}
	return true
}

func (c Config) FindBackupSet(name string) (BackupSet, bool) {
	for _, set := range c.BackupSets {
		if set.Name == name {
			return set, true
		}
	}
	return BackupSet{}, false
}

func (c Config) FindBackupSetByID(id string) (BackupSet, bool) {
	for _, set := range c.BackupSets {
		if set.ID == id {
			return set, true
		}
	}
	return BackupSet{}, false
}
