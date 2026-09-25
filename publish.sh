#!/usr/bin/env bash
# Builds a self-contained, single-file Linux executable and packs it as a .tar.gz for sharing.
#
#   ./publish.sh [runtime]        (default: linux-x64, e.g. linux-arm64 also works)
#
# Result:  dist/app-<runtime>/osu_collection_manager   (+ the wwwroot folder and appsettings.json next to it)
#          dist/osu_collection_manager-<runtime>.tar.gz
#
# Self-contained means the person running it does NOT need .NET installed.
# Build this on Linux (or WSL): a tarball made on Windows can lose the "executable" permission.
set -euo pipefail
cd "$(dirname "$0")"

RUNTIME="${1:-linux-x64}"
APP_DIR="dist/app-$RUNTIME"
ARCHIVE="dist/osu_collection_manager-$RUNTIME.tar.gz"

rm -rf "$APP_DIR" "$ARCHIVE"
mkdir -p dist

dotnet publish src -c Release -r "$RUNTIME" --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None \
  -o "$APP_DIR"

# The Development settings only matter when running from source.
rm -f "$APP_DIR/appsettings.Development.json"
chmod +x "$APP_DIR/osu_collection_manager"

tar -czf "$ARCHIVE" -C "$APP_DIR" .

echo
echo "Done."
echo "  Run:   $APP_DIR/osu_collection_manager"
echo "  Share: $ARCHIVE"
