#!/bin/sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
  echo "Run this installer as root." >&2
  exit 1
fi

PACKAGE_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
install -d -m 0755 /opt/backupmesh /etc/backupmesh /var/cache/backupmesh
install -m 0755 "$PACKAGE_DIR/backupmesh-agent" /opt/backupmesh/backupmesh-agent
install -m 0755 "$PACKAGE_DIR/restic" /opt/backupmesh/restic
install -m 0644 "$PACKAGE_DIR/backupmesh-source@.service" /etc/systemd/system/backupmesh-source@.service
install -m 0644 "$PACKAGE_DIR/backupmesh-source@.timer" /etc/systemd/system/backupmesh-source@.timer
if [ ! -f /etc/backupmesh/backupmesh.json ]; then
  install -m 0600 "$PACKAGE_DIR/backupmesh.json.example" /etc/backupmesh/backupmesh.json
fi
systemctl daemon-reload
echo "Edit /etc/backupmesh/backupmesh.json, validate it, then enable a timer:"
echo "  /opt/backupmesh/backupmesh-agent validate -config /etc/backupmesh/backupmesh.json"
echo "  systemctl enable --now backupmesh-source@BACKUP_SET_NAME.timer"
