#!/usr/bin/env bash
set -euo pipefail
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_root"
dotnet restore
dotnet tool restore
(cd web && npm ci && npm run build)
assets_backup="$(mktemp -d "${TMPDIR:-/tmp}/simple-mock-assets.XXXXXX")"
if [[ -d src/MockServer.Api/wwwroot ]]; then
  mv src/MockServer.Api/wwwroot "$assets_backup/source"
fi
if [[ -d dist/server/wwwroot ]]; then
  mv dist/server/wwwroot "$assets_backup/published"
fi
mkdir -p src/MockServer.Api/wwwroot
cp -R web/dist/web/browser/. src/MockServer.Api/wwwroot/
dotnet publish src/MockServer.Api -c Release -o dist/server --no-restore
printf '\nRun: cd "%s/dist/server" && dotnet MockServer.Api.dll\n' "$project_root"
