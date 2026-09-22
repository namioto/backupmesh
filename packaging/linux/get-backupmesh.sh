#!/bin/sh
set -eu

REPOSITORY=https://github.com/namioto/backupmesh

if [ "$(uname -s)" != Linux ]; then
  echo "BackupMesh Remote Agent requires Linux." >&2
  exit 1
fi
case "$(uname -m)" in
  x86_64|amd64) ;;
  *) echo "BackupMesh Remote Agent requires a 64-bit x86 computer." >&2; exit 1 ;;
esac
for tool in curl sha256sum tar mktemp grep tr; do
  command -v "$tool" >/dev/null 2>&1 || { echo "Required tool not found: $tool" >&2; exit 1; }
done

latest=$(curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --output /dev/null --write-out '%{url_effective}' "$REPOSITORY/releases/latest")
tag=${latest##*/}
printf '%s\n' "$tag" | grep -Eq '^v[0-9]+\.[0-9]+\.[0-9]+$' || { echo "GitHub returned an invalid release tag." >&2; exit 1; }
[ "$latest" = "$REPOSITORY/releases/tag/$tag" ] || { echo "GitHub returned an invalid release URL." >&2; exit 1; }
version=${tag#v}
asset="BackupMesh-Source-$version-linux-x64.tar.gz"
release="$REPOSITORY/releases/download/$tag"

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT HUP INT TERM
if ! curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --output "$work/$asset" "$release/$asset"; then
  echo "Latest release $tag does not include the Linux package. Publish $asset and its checksum, then try again." >&2
  exit 1
fi
if ! curl --fail --silent --show-error --location --proto '=https' --proto-redir '=https' --output "$work/$asset.sha256" "$release/$asset.sha256"; then
  echo "Latest release $tag does not include the Linux package checksum. Publish $asset.sha256, then try again." >&2
  exit 1
fi

checksum=$(cat "$work/$asset.sha256")
expected=${checksum%% *}
if [ "$checksum" != "$expected *$asset" ] || ! printf '%s\n' "$expected" | grep -Eq '^[0-9A-Fa-f]{64}$'; then
  echo "Release checksum file is invalid." >&2
  exit 1
fi
expected=$(printf '%s' "$expected" | tr 'A-F' 'a-f')
actual=$(sha256sum "$work/$asset")
actual=${actual%% *}
if [ "$actual" != "$expected" ]; then
  echo "Release checksum verification failed." >&2
  exit 1
fi

mkdir "$work/package"
tar -xzf "$work/$asset" -C "$work/package"
installer="$work/package/BackupMesh-Source-linux-x64/install.sh"
if [ ! -f "$installer" ]; then
  echo "Release package does not contain the installer." >&2
  exit 1
fi

if (: </dev/tty) 2>/dev/null; then
  if [ "$(id -u)" -eq 0 ]; then
    sh "$installer" </dev/tty
  else
    command -v sudo >/dev/null 2>&1 || { echo "sudo is required to install BackupMesh." >&2; exit 1; }
    sudo sh "$installer" </dev/tty
  fi
elif [ "$(id -u)" -eq 0 ]; then
  sh "$installer" </dev/null
else
  command -v sudo >/dev/null 2>&1 || { echo "sudo is required to install BackupMesh." >&2; exit 1; }
  sudo -n sh "$installer" </dev/null
fi
