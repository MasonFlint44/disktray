#!/usr/bin/env bash
# Uninstalls DiskTray from WSL, the same as Uninstall in Settings › Apps but without asking:
# closes it, removes it from Startup apps and Settings › Apps, removes its notification
# registration and %LOCALAPPDATA%\DiskTray, then deletes %LOCALAPPDATA%\Programs\DiskTray.
set -euo pipefail

# Windows tools misbehave when started from a \\wsl.localhost working directory.
win() { (cd /mnt/c && "$@"); }

win_local_app_data=$(win cmd.exe /c 'echo %LOCALAPPDATA%' | tr -d '\r')
target=$(wslpath "$win_local_app_data\\Programs\\DiskTray")

if [[ ! -f "$target/DiskTray.exe" ]]; then
  echo "DiskTray isn't installed in $win_local_app_data\\Programs\\DiskTray"
  exit 1
fi

win "$target/DiskTray.exe" --uninstall --quiet
# The folder goes a few seconds after DiskTray exits.
for _ in $(seq 1 30); do
  [[ -d "$target" ]] || { echo "Uninstalled DiskTray"; exit 0; }
  sleep 0.5
done
echo "DiskTray's settings and registrations are gone, but $target is still there"
exit 1
