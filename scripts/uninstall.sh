#!/usr/bin/env bash
# Removes the development copy that scripts/deploy.sh puts in %LOCALAPPDATA%\Programs\DiskTray:
# closes it, removes it from Startup apps, removes its notification registration and
# %LOCALAPPDATA%\DiskTray, then deletes the folder. (A copy from the installer is removed
# from Settings › Apps instead.)
set -euo pipefail

# Windows tools misbehave when started from a \\wsl.localhost working directory.
win() { (cd /mnt/c && "$@"); }

win_local_app_data=$(win cmd.exe /c 'echo %LOCALAPPDATA%' | tr -d '\r')
target=$(wslpath "$win_local_app_data\\Programs\\DiskTray")

if [[ ! -f "$target/DiskTray.exe" ]]; then
  echo "DiskTray isn't in $win_local_app_data\\Programs\\DiskTray"
  exit 1
fi

# Returns once the running copy has exited, so its files can go.
win "$target/DiskTray.exe" --remove-user-data
rm -rf "$target"
echo "Removed DiskTray from $win_local_app_data\\Programs\\DiskTray"
