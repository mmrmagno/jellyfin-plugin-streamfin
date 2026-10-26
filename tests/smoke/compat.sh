#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
image=${1:-jellyfin/jellyfin:latest}
zip=$(ls -v artifacts/StreamFin-*.zip | tail -n 1)
config=$(mktemp -d)
trap 'docker rm -f streamfin-compat >/dev/null 2>&1; rm -rf "$config"' EXIT
mkdir -p "$config/plugins/StreamFin"
python3 -c 'import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])' "$zip" "$config/plugins/StreamFin"
docker run -d --name streamfin-compat --network none --user "$(id -u):$(id -g)" -v "$config:/config" "$image" >/dev/null
for _ in $(seq 120); do
  logs=$(docker logs streamfin-compat 2>&1)
  if grep -q 'Loaded plugin: StreamFin' <<<"$logs"; then
    grep -m1 -o 'Jellyfin version: [0-9.]*' <<<"$logs" || true
    echo "Loaded $(basename "$zip")"
    exit 0
  fi
  if grep -qiE 'Startup complete|Error .*StreamFin|Failed to .*StreamFin' <<<"$logs"; then
    break
  fi
  sleep 1
done
grep -iE 'StreamFin|plugin' <<<"$logs" >&2 || true
echo "StreamFin did not load on $image" >&2
exit 1
