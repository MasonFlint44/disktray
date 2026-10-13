#!/usr/bin/env bash
# Builds a development installer, artifacts/msi/DiskTray-<version>-x64-dev.msi, from WSL, for
# trying out installing, upgrading and uninstalling. Releases come from GitHub Actions
# instead: their Compactor is compiled ahead of time to a native exe, which can only be
# built on Windows with the C++ build tools; here it's a plain .NET one.
# Publishes both apps self-contained (so nobody needs .NET installed), then runs WiX with
# the Windows dotnet, since WiX only runs on Windows. That happens in a copy under
# %TEMP%\disktray-msi, because Windows tools misbehave on \\wsl.localhost paths.
# Needs a .NET 6 or later SDK on Windows.
set -euo pipefail
cd "$(dirname "$0")/.."

win() { (cd /mnt/c && "$@"); }

win_temp=$(win cmd.exe /c 'echo %TEMP%' | tr -d '\r')
win_stage="$win_temp\\disktray-msi"
stage=$(wslpath "$win_stage")

rm -rf "$stage"
mkdir -p "$stage/src/DiskTray.App" "$stage/artifacts"
cp -r installer "$stage/"
cp Directory.Build.props "$stage/"
cp src/DiskTray.App/app.ico "$stage/src/DiskTray.App/"

dotnet publish src/DiskTray.App -c Release -r win-x64 --self-contained -o "$stage/artifacts/publish" -v quiet -nologo
dotnet publish src/DiskTray.Compactor -c Release -r win-x64 --self-contained -p:PublishAot=false -o "$stage/artifacts/publish" -v quiet -nologo

win dotnet.exe build "$win_stage\\installer" -c Release -nologo -v quiet -p:DevBuild=true
mkdir -p artifacts/msi
# Only the latest dev MSI is kept; each is over 50 MB.
rm -f artifacts/msi/*-dev.msi
cp "$stage"/artifacts/msi/*.msi artifacts/msi/
ls -l artifacts/msi/*.msi
