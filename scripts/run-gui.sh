#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT_DIR"

if command -v mise >/dev/null 2>&1; then
    exec mise exec -- dotnet run -c Release --project src/NvmeSentinel.App/NvmeSentinel.App.csproj -- "$@"
else
    exec dotnet run -c Release --project src/NvmeSentinel.App/NvmeSentinel.App.csproj -- "$@"
fi
