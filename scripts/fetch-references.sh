#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
image="${JELLYFIN_IMAGE:-jellyfin/jellyfin@sha256:2038fe038ab3876a80f6e7b7e0aef6607b032c7dc0c9b3ad3296d0b2eba3bbb5}" # 12.3.0
mkdir -p .jellyfin
container=$(docker create "$image")
trap 'docker rm "$container" >/dev/null' EXIT
for assembly in MediaBrowser.Common MediaBrowser.Controller MediaBrowser.Model Jellyfin.Data Jellyfin.Database.Implementations Jellyfin.Extensions Emby.Naming; do
  docker cp "$container:/jellyfin/$assembly.dll" ".jellyfin/$assembly.dll"
done
