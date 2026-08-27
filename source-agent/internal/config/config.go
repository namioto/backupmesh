package config

import (
	"encoding/json"
	"errors"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"strings"
)

type Config struct {
	Agent          Agent       `json:"agent"`
	Storage        Storage     `json:"storage"`
	BackupSets     []BackupSet `json:"backupSets"`
	UploadLimitBPS int64       `json:"uploadLimitBps,omitempty"`
}

type Agent struct {
	ID   string `json:"id"`
	Name string `json:"name,omitempty"`
}

type Storage struct {
	ControlEndpoint        string `json:"controlEndpoint"`
	RepositoryPasswordFile string `json:"repositoryPasswordFile"`
}

type BackupSet struct {
	ID      string   `json:"id"`
	Name    string   `json:"name"`
	Paths   []string `json:"paths"`
	Include []string `json:"include,omitempty"`
	Exclude []string `json:"exclude,omitempty"`
	Hooks   Hooks    `json:"hooks,omitempty"`
}

type Hooks struct {
	Before [][]string `json:"before,omitempty"`
	After  [][]string `json:"after,omitempty"`
}

func Load(path string) (Config, error) {
	b, err := os.ReadFile(filepath.Clean(path))
	if err != nil {
		return Config{}, fmt.Errorf("read config: %w", err)
	}
	var c Config
	d := json.NewDecoder(strings.NewReader(string(b)))
	d.DisallowUnknownFields()
	if err := d.Decode(&c); err != nil {
		return Config{}, fmt.Errorf("decode config: %w", err)
	}
	if err := c.Validate(); err != nil {
		return Config{}, err
	}
	return c, nil
}

func (c Config) Validate() error {
	var problems []error
	if strings.TrimSpace(c.Agent.ID) == "" {
		problems = append(problems, errors.New("agent.id is required"))
	} else if !isUUID(c.Agent.ID) {
		problems = append(problems, errors.New("agent.id must be a UUID"))
	}
	if strings.TrimSpace(c.Agent.Name) == "" {
		problems = append(problems, errors.New("agent.name is required"))
	}
	u, err := url.Parse(c.Storage.ControlEndpoint)
	if err != nil || u.Scheme == "" || u.Host == "" || (u.Scheme != "https" && u.Scheme != "http") {
		problems = append(problems, errors.New("storage.controlEndpoint must be an absolute HTTP(S) URL"))
	}
	if strings.TrimSpace(c.Storage.RepositoryPasswordFile) == "" {
		problems = append(problems, errors.New("storage.repositoryPasswordFile is required"))
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
	return errors.Join(problems...)
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
