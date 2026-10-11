#!/usr/bin/env bash
# Builds the installer, artifacts/msi/DiskTray-<version>-x64.msi, from WSL.
# Publishes both apps self-contained (so nobody needs .NET installed), then runs WiX with
# the Windows dotnet, since WiX only runs on Windows. That happens in a copy under
# %TEMP%\disktray-msi, because Windows tools misbehave on \\wsl.localhost paths.
# Needs a .NET 6 or later SDK on Windows. GitHub Actions builds the same thing.
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

for project in src/DiskTray.App src/DiskTray.Compactor; do
  dotnet publish "$project" -c Release -r win-x64 --self-contained -o "$stage/artifacts/publish" -v quiet -nologo
done

win dotnet.exe build "$win_stage\\installer" -c Release -nologo -v quiet
mkdir -p artifacts/msi
cp "$stage"/artifacts/msi/*.msi artifacts/msi/
ls -l artifacts/msi/*.msi
