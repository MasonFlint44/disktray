#!/usr/bin/env bash
# Publishes DiskTray from WSL to %LOCALAPPDATA%\Programs\DiskTray on Windows and starts it.
# Closes a running copy first so its files can be replaced.
#   scripts/deploy.sh             publish and start
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

if [[ "${1:-}" != "--no-start" ]]; then
  # Started through Explorer, like a normal launch. "cmd /c start" would hand it this
  # script's interop pipes and keep the script waiting until DiskTray exits.
  win explorer.exe "$win_target\\DiskTray.exe" || true
  echo "Started $win_target\\DiskTray.exe"
fi
