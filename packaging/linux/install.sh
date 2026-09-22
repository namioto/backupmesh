#!/bin/sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
  echo "Run this installer as root." >&2
  exit 1
fi

PACKAGE_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
systemctl stop backupmesh-source-watch.service 2>/dev/null || true
install -d -m 0755 /opt/backupmesh /etc/backupmesh /var/cache/backupmesh
install -m 0755 "$PACKAGE_DIR/backupmesh-agent" /opt/backupmesh/backupmesh-agent
install -m 0755 "$PACKAGE_DIR/restic" /opt/backupmesh/restic
install -m 0644 "$PACKAGE_DIR/backupmesh-source-watch.service" /etc/systemd/system/backupmesh-source-watch.service
install -m 0644 "$PACKAGE_DIR/backupmesh-source@.service" /etc/systemd/system/backupmesh-source@.service
install -m 0644 "$PACKAGE_DIR/backupmesh-source@.timer" /etc/systemd/system/backupmesh-source@.timer

CONFIG_PATH=/etc/backupmesh/backupmesh.json
if [ -f /etc/backupmesh/backupmesh.json ]; then
  :
elif [ -f /etc/backupmesh/backupmesh.yaml ]; then
  CONFIG_PATH=/etc/backupmesh/backupmesh.yaml
elif [ -f /etc/backupmesh/backupmesh.yml ]; then
  CONFIG_PATH=/etc/backupmesh/backupmesh.yml
else
  AGENT_NAME=$(hostname 2>/dev/null | tr -cd 'A-Za-z0-9._-' || true)
  AGENT_NAME=${AGENT_NAME:-this-computer}
  umask 077
  printf '{"agent":{"name":"%s"},"storage":{"repositoryPasswordFile":"/etc/backupmesh/restic-password"},"backupSets":[]}\n' "$AGENT_NAME" > "$CONFIG_PATH"
  chmod 0600 "$CONFIG_PATH"
fi

if [ ! -f /etc/backupmesh/restic-password ]; then
  umask 077
  dd if=/dev/urandom bs=32 count=1 2>/dev/null | base64 | tr -d '\n' > /etc/backupmesh/restic-password
  printf '\n' >> /etc/backupmesh/restic-password
  chmod 0600 /etc/backupmesh/restic-password
fi
systemctl daemon-reload

DROPIN_DIR=/etc/systemd/system/backupmesh-source-watch.service.d
install -d -m 0755 "$DROPIN_DIR"
cat > "$DROPIN_DIR/config.conf" <<EOF
[Service]
ExecStart=
ExecStart=/opt/backupmesh/backupmesh-agent watch -config $CONFIG_PATH -restic /opt/backupmesh/restic
EOF
chmod 0644 "$DROPIN_DIR/config.conf"
systemctl daemon-reload

systemctl enable backupmesh-source-watch.service
systemctl restart backupmesh-source-watch.service

echo "Remote Agent installed and discoverable. Storage can request pairing; approve locally with:"
echo "  /opt/backupmesh/backupmesh-agent nearby pending -config $CONFIG_PATH"
echo "  /opt/backupmesh/backupmesh-agent nearby approve -config $CONFIG_PATH -request REQUEST_ID"
echo "Manual invitation pairing remains available:"
echo "  /opt/backupmesh/backupmesh-agent pair -config $CONFIG_PATH -output /etc/backupmesh/pairing"
echo "  /opt/backupmesh/backupmesh-agent validate -config $CONFIG_PATH"
echo "Back up /etc/backupmesh/restic-password securely. Losing it makes the encrypted backups unrecoverable."
echo "Optional scheduled fallback:"
echo "  systemctl enable --now backupmesh-source@BACKUP_SET_NAME.timer"
