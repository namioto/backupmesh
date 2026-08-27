package restic

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os/exec"
	"strconv"
	"strings"
	"time"

	"github.com/namioto/backupmesh/source-agent/internal/engine"
)

type Adapter struct {
	Binary string
	Env    []string
}

func (a Adapter) Backup(ctx context.Context, req engine.BackupRequest, progress func(engine.Progress)) (engine.Result, error) {
	binary := a.Binary
	if binary == "" {
		binary = "restic"
	}
	cmd := exec.CommandContext(ctx, binary, BuildBackupArgs(req)...)
	cmd.Env = BuildEnv(cmd.Environ(), a.Env, req)
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return engine.Result{}, fmt.Errorf("restic stdout: %w", err)
	}
	stderr, err := cmd.StderrPipe()
	if err != nil {
		return engine.Result{}, fmt.Errorf("restic stderr: %w", err)
	}
	if err := cmd.Start(); err != nil {
		return engine.Result{}, fmt.Errorf("start restic: %w", err)
	}
	errText := make(chan string, 1)
	go func() { b, _ := io.ReadAll(io.LimitReader(stderr, 64*1024)); errText <- strings.TrimSpace(string(b)) }()
	result, parseErr := ParseJSONStream(stdout, progress)
	waitErr := cmd.Wait()
	message := <-errText
	if ctx.Err() != nil {
		return engine.Result{}, ctx.Err()
	}
	if parseErr != nil {
		return engine.Result{}, parseErr
	}
	if waitErr != nil {
		if message != "" {
			message = strings.ReplaceAll(message, req.Repository, "[repository]")
			return engine.Result{}, fmt.Errorf("restic failed: %w: %s", waitErr, message)
		}
		return engine.Result{}, fmt.Errorf("restic failed: %w", waitErr)
	}
	return result, nil
}

func BuildEnv(base, extra []string, req engine.BackupRequest) []string {
	env := append(append([]string{}, base...), extra...)
	env = append(env, "RESTIC_REPOSITORY="+req.Repository)
	if req.PasswordFile != "" {
		env = append(env, "RESTIC_PASSWORD_FILE="+req.PasswordFile)
	}
	if req.CacheDirectory != "" {
		env = append(env, "RESTIC_CACHE_DIR="+req.CacheDirectory)
	}
	return env
}

func BuildBackupArgs(req engine.BackupRequest) []string {
	args := []string{"backup", "--json"}
	for _, pattern := range req.Includes {
		args = append(args, "--include", pattern)
	}
	for _, pattern := range req.Excludes {
		args = append(args, "--exclude", pattern)
	}
	if req.UploadLimitBPS > 0 {
		// restic expects KiB/s and has no bytes/s flag.
		kib := (req.UploadLimitBPS + 1023) / 1024
		args = append(args, "--limit-upload", strconv.FormatInt(kib, 10))
	}
	return append(args, req.Paths...)
}

type message struct {
	MessageType    string  `json:"message_type"`
	FilesDone      uint64  `json:"files_done"`
	TotalFiles     uint64  `json:"total_files"`
	BytesDone      uint64  `json:"bytes_done"`
	TotalBytes     uint64  `json:"total_bytes"`
	PercentDone    float64 `json:"percent_done"`
	SnapshotID     string  `json:"snapshot_id"`
	FilesNew       uint64  `json:"files_new"`
	FilesChanged   uint64  `json:"files_changed"`
	FilesUnchanged uint64  `json:"files_unmodified"`
	DataAdded      uint64  `json:"data_added"`
	TotalDuration  float64 `json:"total_duration"`
}

func ParseJSONStream(r io.Reader, progress func(engine.Progress)) (engine.Result, error) {
	s := bufio.NewScanner(r)
	s.Buffer(make([]byte, 64*1024), 1024*1024)
	var result engine.Result
	var gotSummary bool
	for s.Scan() {
		line := strings.TrimSpace(s.Text())
		if line == "" {
			continue
		}
		var m message
		if err := json.Unmarshal([]byte(line), &m); err != nil {
			return engine.Result{}, fmt.Errorf("decode restic JSON: %w", err)
		}
		switch m.MessageType {
		case "status":
			if progress != nil {
				progress(engine.Progress{FilesDone: m.FilesDone, TotalFiles: m.TotalFiles, BytesDone: m.BytesDone, TotalBytes: m.TotalBytes, Percent: m.PercentDone})
			}
		case "summary":
			gotSummary = true
			result = engine.Result{SnapshotID: m.SnapshotID, FilesNew: m.FilesNew, FilesChanged: m.FilesChanged, FilesUnchanged: m.FilesUnchanged, DataAdded: m.DataAdded, Duration: time.Duration(m.TotalDuration * float64(time.Second))}
		case "verbose_status", "warning":
		default:
			// Forward compatibility: newer informational messages are ignored.
		}
	}
	if err := s.Err(); err != nil {
		return engine.Result{}, fmt.Errorf("read restic JSON: %w", err)
	}
	if !gotSummary {
		return engine.Result{}, errors.New("restic output ended without summary")
	}
	return result, nil
}
