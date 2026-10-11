#!/usr/bin/env bash
# Publishes a development copy of DiskTray from WSL to %LOCALAPPDATA%\Programs\DiskTray on
# Windows and starts it. (It can't compact; the installer from scripts/build-msi.sh can.)
# Closes a running copy first so its files can be replaced.
#   scripts/deploy.sh             publish and start
#   scripts/deploy.sh --dry-run   publish and start with Clean up only logging what it would do
#   scripts/deploy.sh --no-start  publish only
set -euo pipefail
cd "$(dirname "$0")/.."

# Windows tools misbehave when started from a \\wsl.localhost working directory.
win() { (cd /mnt/c && "$@"); }

win_local_app_data=$(win cmd.exe /c 'echo %LOCALAPPDATA%' | tr -d '\r')
win_target="$win_local_app_data\\Programs\\DiskTray"
target=$(wslpath "$win_target")

if [[ -f "$target/DiskTray.exe" ]]; then
  win "$target/DiskTray.exe" --exit || true
  for _ in $(seq 1 50); do
    win tasklist.exe /fi "imagename eq DiskTray.exe" /nh | grep -qi 'DiskTray.exe' || break
    sleep 0.2
  done
fi

dotnet publish src/DiskTray.App -c Release -r win-x64 --self-contained false -o "$target" -v quiet -nologo
# The elevated helper that compacts the disks lives next to DiskTray.exe.
dotnet publish src/DiskTray.Compactor -c Release -r win-x64 --self-contained false -o "$target" -v quiet -nologo

case "${1:-}" in
  --no-start) ;;
  --dry-run)
    # Explorer can't pass arguments, so this one goes through PowerShell's Start-Process,
    # which doesn't keep this script waiting either.
    win powershell.exe -NoProfile -Command "Start-Process -FilePath '$win_target\\DiskTray.exe' -ArgumentList '--dry-run'"
    echo "Started $win_target\\DiskTray.exe --dry-run"
    ;;
  *)
    # Started through Explorer, like a normal launch. "cmd /c start" would hand it this
    # script's interop pipes and keep the script waiting until DiskTray exits.
    win explorer.exe "$win_target\\DiskTray.exe" || true
    echo "Started $win_target\\DiskTray.exe"
    ;;
esac
