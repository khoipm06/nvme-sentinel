#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT_DIR"

DOTNET_CMD=(dotnet)
if command -v mise >/dev/null 2>&1; then
    DOTNET_CMD=(mise exec -- dotnet)
fi

RIDS=("linux-x64" "win-x64" "osx-arm64")

for RID in "${RIDS[@]}"; do
    OUT_DIR="$ROOT_DIR/publish/$RID"
    echo "==> Publishing self-contained bundle for $RID -> $OUT_DIR"
    "${DOTNET_CMD[@]}" publish src/NvmeSentinel.Daemon/NvmeSentinel.Daemon.csproj \
        -c Release -r "$RID" --self-contained true \
        -p:PublishSingleFile=true \
        -o "$OUT_DIR"

    "${DOTNET_CMD[@]}" publish src/NvmeSentinel.App/NvmeSentinel.App.csproj \
        -c Release -r "$RID" --self-contained true \
        -p:PublishSingleFile=true \
        -o "$OUT_DIR"
done

echo "All cross-platform bundles published under $ROOT_DIR/publish/"
