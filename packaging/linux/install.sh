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
install -m 0644 "$PACKAGE_DIR/backupmesh-source-watch.service" /etc/systemd/system/backupmesh-source-watch.service
install -m 0644 "$PACKAGE_DIR/backupmesh-source@.service" /etc/systemd/system/backupmesh-source@.service
install -m 0644 "$PACKAGE_DIR/backupmesh-source@.timer" /etc/systemd/system/backupmesh-source@.timer

CONFIG_PATH=/etc/backupmesh/backupmesh.json
JUST_GENERATED=0
if [ ! -f /etc/backupmesh/backupmesh.json ] && [ ! -f /etc/backupmesh/backupmesh.yaml ] && [ ! -f /etc/backupmesh/backupmesh.yml ]; then
  if [ -t 0 ] && [ -t 1 ]; then
    echo "No existing configuration found. Answer a few questions to create a minimal one (Ctrl+C to skip)."
    DEFAULT_NAME=$(hostname 2>/dev/null || echo "this-computer")
    printf 'Name for this Source Agent [%s]: ' "$DEFAULT_NAME"
    read -r AGENT_NAME
    AGENT_NAME=${AGENT_NAME:-$DEFAULT_NAME}
    printf 'Name for the first Backup Set [documents]: '
    read -r SET_NAME
    SET_NAME=${SET_NAME:-documents}
    SET_PATH=""
    while [ -z "$SET_PATH" ] || [ "${SET_PATH#/}" = "$SET_PATH" ]; do
      printf 'Absolute path to back up (e.g. /home/you/Documents): '
      read -r SET_PATH
    done
    CONFIG_PATH=/etc/backupmesh/backupmesh.yaml
    umask 077
    cat > "$CONFIG_PATH" <<EOF
agent:
  name: $AGENT_NAME
storage:
  repositoryPasswordFile: /etc/backupmesh/restic-password
backupSets:
  - name: $SET_NAME
    paths:
      - $SET_PATH
EOF
    chmod 0600 "$CONFIG_PATH"
    JUST_GENERATED=1
    echo "Wrote $CONFIG_PATH. Add more backupSets entries by hand any time; no ID or Storage connection field is required until you pair."
  else
    install -m 0600 "$PACKAGE_DIR/backupmesh.json.example" /etc/backupmesh/backupmesh.json
  fi
elif [ -f /etc/backupmesh/backupmesh.yaml ]; then
  CONFIG_PATH=/etc/backupmesh/backupmesh.yaml
elif [ -f /etc/backupmesh/backupmesh.yml ]; then
  CONFIG_PATH=/etc/backupmesh/backupmesh.yml
fi

if [ ! -f /etc/backupmesh/restic-password ]; then
  umask 077
  dd if=/dev/urandom bs=32 count=1 2>/dev/null | base64 | tr -d '\n' > /etc/backupmesh/restic-password
  printf '\n' >> /etc/backupmesh/restic-password
  chmod 0600 /etc/backupmesh/restic-password
fi
systemctl daemon-reload

if [ "$JUST_GENERATED" -eq 1 ] && [ -t 0 ] && [ -t 1 ]; then
  printf 'Pair with the Storage Agent now? The Storage tray shows a one-time code. [y/N]: '
  read -r DO_PAIR
  if [ "$DO_PAIR" = "y" ] || [ "$DO_PAIR" = "Y" ]; then
    printf 'Storage HTTPS endpoint (e.g. https://storage-pc:7443): '
    read -r STORAGE_ENDPOINT
    printf 'One-time pairing code from the tray: '
    read -r PAIRING_CODE
    printf 'Certificate SHA-256 fingerprint from the tray: '
    read -r FINGERPRINT
    if /opt/backupmesh/backupmesh-agent pair -config "$CONFIG_PATH" -storage "$STORAGE_ENDPOINT" -code "$PAIRING_CODE" -fingerprint "$FINGERPRINT" -output /etc/backupmesh/pairing; then
      systemctl enable --now backupmesh-source-watch.service
      echo "Paired and watching for Storage commands. Back up /etc/backupmesh/restic-password securely - losing it makes the encrypted backups unrecoverable."
      echo "Check status any time with: systemctl status backupmesh-source-watch.service"
      exit 0
    else
      echo "Pairing failed. You can retry later with the command below." >&2
    fi
  fi
fi

echo "Edit $CONFIG_PATH (agent name and backup sets), then pair with the Storage tray app's one-time code and enable the command watcher:"
echo "  /opt/backupmesh/backupmesh-agent pair -config $CONFIG_PATH -storage https://STORAGE-PC:7443 -code CODE-FROM-TRAY -fingerprint FINGERPRINT-FROM-TRAY -output /etc/backupmesh/pairing"
echo "  /opt/backupmesh/backupmesh-agent validate -config $CONFIG_PATH"
echo "  systemctl enable --now backupmesh-source-watch.service"
echo "Back up /etc/backupmesh/restic-password securely. Losing it makes the encrypted backups unrecoverable."
echo "Optional scheduled fallback:"
echo "  systemctl enable --now backupmesh-source@BACKUP_SET_NAME.timer"
