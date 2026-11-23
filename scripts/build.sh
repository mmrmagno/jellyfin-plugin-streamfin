#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
SDK_IMAGE=$(sed -n 's/^FROM \([^ ]*\).*/\1/p' scripts/images/sdk/Dockerfile)
if [[ ! -f .jellyfin/MediaBrowser.Controller.dll ]]; then
  echo 'Missing API assemblies. Run scripts/fetch-references.sh first.' >&2
  exit 1
fi
docker run --rm --user "$(id -u):$(id -g)" -e DOTNET_CLI_HOME=/tmp/dotnet \
  -v "$PWD:/work" -w /work "$SDK_IMAGE" dotnet build src/StreamFin.csproj -c Release -o artifacts/plugin
docker run --rm --user "$(id -u):$(id -g)" -e DOTNET_CLI_HOME=/tmp/dotnet \
  -v "$PWD:/work" -w /work "$SDK_IMAGE" dotnet run --project tests/StreamFin.Tests.csproj -c Release
python3 scripts/package.py
