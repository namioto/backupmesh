package engine

import (
	"context"
	"time"
)

type BackupRequest struct {
	Repository     string
	PasswordFile   string
	CacheDirectory string
	Paths          []string
	Includes       []string
	Excludes       []string
	UploadLimitBPS int64
}

type Progress struct {
	FilesDone  uint64
	TotalFiles uint64
	BytesDone  uint64
	TotalBytes uint64
	Percent    float64
}

type Result struct {
	SnapshotID     string
	FilesNew       uint64
	FilesChanged   uint64
	FilesUnchanged uint64
	DataAdded      uint64
	Duration       time.Duration
}

type BackupEngine interface {
	Backup(context.Context, BackupRequest, func(Progress)) (Result, error)
}
