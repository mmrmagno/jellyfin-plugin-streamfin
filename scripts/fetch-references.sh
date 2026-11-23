#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
image="${JELLYFIN_IMAGE:-$(sed -n 's/^FROM \([^ ]*\).*/\1/p' scripts/images/jellyfin/Dockerfile)}"
mkdir -p .jellyfin
container=$(docker create "$image")
trap 'docker rm "$container" >/dev/null' EXIT
for assembly in MediaBrowser.Common MediaBrowser.Controller MediaBrowser.Model Jellyfin.Data Jellyfin.Database.Implementations Jellyfin.Extensions Emby.Naming; do
  docker cp "$container:/jellyfin/$assembly.dll" ".jellyfin/$assembly.dll"
done
