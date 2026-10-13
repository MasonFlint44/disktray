#!/usr/bin/env bash
# Removes the development copy that scripts/deploy.sh puts in %LOCALAPPDATA%\Programs\DiskTray:
# closes it and deletes the folder. Settings, the Startup apps entry and the notification
# registration are shared with a copy from the installer, so with one installed they're left
# for it, and Startup apps is pointed back at it; otherwise they're removed too. (A copy from
# the installer is removed from Settings › Apps instead.)
set -euo pipefail

# Windows tools misbehave when started from a \\wsl.localhost working directory.
win() { (cd /mnt/c && "$@"); }

win_local_app_data=$(win cmd.exe /c 'echo %LOCALAPPDATA%' | tr -d '\r')
win_program_files=$(win cmd.exe /c 'echo %ProgramFiles%' | tr -d '\r')
target=$(wslpath "$win_local_app_data\\Programs\\DiskTray")
installed="$win_program_files\\DiskTray\\DiskTray.exe"
run_key='HKCU\Software\Microsoft\Windows\CurrentVersion\Run'

if [[ ! -f "$target/DiskTray.exe" ]]; then
  echo "DiskTray isn't in $win_local_app_data\\Programs\\DiskTray"
  exit 1
fi

if [[ -f "$(wslpath "$installed")" ]]; then
  # Returns once the running copy (whichever it is) has exited, so the files can go.
  win "$target/DiskTray.exe" --exit || true
  if win reg.exe query "$run_key" /v DiskTray 2>/dev/null | tr -d '\r' | grep -qi 'Programs\\DiskTray\\DiskTray.exe'; then
    win reg.exe add "$run_key" /v DiskTray /d "\"$installed\"" /f > /dev/null
    echo "Pointed Start with Windows back at $installed"
  fi
  rm -rf "$target"
  echo "Removed DiskTray from $win_local_app_data\\Programs\\DiskTray; kept the settings, which $installed uses too"
else
  # Returns once the running copy has exited, so its files can go.
  win "$target/DiskTray.exe" --remove-user-data
  rm -rf "$target"
  echo "Removed DiskTray and its settings from $win_local_app_data\\Programs\\DiskTray"
fi
