#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
image='jellyfin/jellyfin:12.1.20260915-010956@sha256:78d3ea1207d1322471fcac39a614f004f2ccf7e878f95ab2977d752f07e4dd7e'
mkdir -p .jellyfin
container=$(docker create "$image")
trap 'docker rm "$container" >/dev/null' EXIT
for assembly in MediaBrowser.Common MediaBrowser.Controller MediaBrowser.Model Jellyfin.Data Jellyfin.Database.Implementations Jellyfin.Extensions Emby.Naming; do
  docker cp "$container:/jellyfin/$assembly.dll" ".jellyfin/$assembly.dll"
done
