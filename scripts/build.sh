#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
if [[ ! -f .jellyfin/MediaBrowser.Controller.dll ]]; then
  echo 'Missing API assemblies. Run scripts/fetch-references.sh first.' >&2
  exit 1
fi
docker run --rm --user "$(id -u):$(id -g)" -e DOTNET_CLI_HOME=/tmp/dotnet \
  -v "$PWD:/work" -w /work "$SDK_IMAGE" dotnet build src/AniFin.csproj -c Release -o artifacts/plugin
docker run --rm --user "$(id -u):$(id -g)" -e DOTNET_CLI_HOME=/tmp/dotnet \
  -v "$PWD:/work" -w /work "$SDK_IMAGE" dotnet run --project tests/AniFin.Tests.csproj -c Release
python3 scripts/package.py
