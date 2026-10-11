# DiskTray design

Mockup: https://claude.ai/artifact/PyZbd4d2EFp8ysLhyV8GHt

DiskTray is a Windows tray app that shows how much of drive C: the WSL and Docker
virtual disks (VHDX files) take up compared with Windows, and helps get that space
back. VHDX files grow but never shrink on their own, so the app tracks two numbers
for each disk file: its size on C:, and how much of it is actually in use. The
difference is *reclaimable* and is shown as a darker shade of the item's color.

## Tray icons

- Up to 3 pinned icons. Tray slots are fixed-size squares, so each icon is redrawn
  in code on every refresh.
- **Shape** (one setting for all icons): Pie, or Bar (vertical bar).
- **Disk-file icons (Docker, Ubuntu, any pinned path).** The whole circle (or bar)
  is that disk file plus the free space on C:. It shows used (item color),
  reclaimable (darker shade) and free (dark gray). Today: Docker ≈ 73% (18% used,
  55% reclaimable), Ubuntu ≈ 56%.
  - "Icon shows" option: **Disk + free space** (default) or **Share of drive**.
- **Drive icon.** The whole drive broken down into Docker / Ubuntu / Windows & other /
  free.
  - "Icon shows" option: **Drive breakdown** (default) or **Used vs free**.
- **Warning indicator.** An amber ring or outline when free space is below the
  warning level, red below the critical level.
- **Tooltip.** For example: `Docker data · 138 GB (104 reclaimable)` /
  `C: 51 GB free (11%) – below 15% warning`.

### Colors

| Item | Color | Reclaimable shade |
|---|---|---|
| Docker | `#2496ED` | `#14548A` |
| Ubuntu / WSL | `#E95420` | `#7A2C11` |
| Windows & other | `#D0D0D0` | — |
| Free | `#3A3A3A` | — |
| Warning | `#F0B040` | — |
| Critical | `#E5534B` | — |
| Accent | `#4CC2FF` | — |

Each pinned item has a color picker, and its reclaimable shade is derived from the
chosen color.

## Detection

- **WSL distros.** Read from the registry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss`
  (each distro's `BasePath` holds its `ext4.vhdx`).
- **Docker Desktop.** Find `docker_data.vhdx` from Docker Desktop's settings file
  (default: `%LOCALAPPDATA%\Docker\wsl\disk\docker_data.vhdx`).
- **Large-file scan.** Daily scan of the user folder for files over a threshold
  (default 2 GB) such as `.vhdx`, `.vhd`, `.vmdk` and `.qcow2`. Large files that
  aren't pinned show up in the flyout's "Other large files" section, which can be
  toggled off, and each has a pin button.
- **"Used inside" numbers.** Queried from WSL/Docker (e.g. `df`, `docker system df`)
  only when they're already running. Otherwise the last known value is shown with
  an "as of" time. The app never starts WSL or Docker just to measure.
- **Add a path…** pins any file or folder by hand.

## Flyout (left-click a tray icon)

- Header: "Disk usage · Drive C: · 472 GB" and a settings gear.
- A warning banner when below a level, e.g. "51 GB free (11%) – below your 15% warning".
- A donut of the whole drive with free space in the center, and a legend listing
  each item's size plus "N used · N reclaimable".
- "Darker shade = space a disk file is holding but not using."
- "Other large files" (over 2 GB, not pinned), each with a pin button. Can be toggled.
- "Reclaimable by compacting" total, and one **Clean up…** button.
- Footer: "Updated 1:41 PM · checks every 5 min".

## Alerts

- **Warning** at 15% free (≈71 GB on a 472 GB drive). **Critical** at 5% (≈24 GB).
- A toast fires as soon as free space drops below a level.
- While still low, a daily reminder shows the first time the user is active
  (activity, unlock or login) after a set time (default 9:00 AM).
- An alert re-arms only after free space climbs a few GB above its level, so it
  doesn't flap.
- Do Not Disturb is respected (Windows toasts handle this).

## Clean up dialog

One dialog with two steps. Both are on by default, and either can be unchecked.
Nothing runs until the user clicks **Clean up**.

1. **Prune Docker** (runs now, while Docker is running).
   - Build cache and dangling images.
   - Unused volumes, grouped by Compose project, behind "Choose what to remove".
     Each group has a confidence tag (see below).
2. **Compact disks.**
   - When: **When WSL is idle** (default) or **Right away**.
     - Idle: starts once Docker Desktop has quit and no WSL distros are running.
       Nothing gets interrupted.
     - Right away: shows an amber warning listing what gets closed (open terminals,
       VS Code and Claude Code sessions, running containers).
   - Takes about 10–20 min, asks for admin, and restarts Docker Desktop afterwards
     (optional).
- **Footer.** The **Clean up** button has a fixed width and label and is disabled
  when nothing is selected. The left side shows a summary ("C: gets back ~152 GB")
  and a plan ("Prunes now, compacts when WSL is idle").

### Volume confidence

Volume labels only carry `com.docker.compose.project` (dots stripped, e.g.
`acme.web-app` → `acmeweb-app`) and
`com.docker.compose.volume`. Folder paths come from container labels
(`devcontainer.local_folder`, `com.docker.compose.project.working_dir`). In order:

1. The path from a container label (exact).
2. A project → folder cache the app records over time from containers it sees (exact).
3. A name match inside the "Code folders to search" list (best guess).

| Tag | Pre-selected |
|---|---|
| Folder deleted | Yes |
| No matching folder in ~/git | No |
| Folder exists | Never |
| No project (unnamed volume) | Never |

## Settings (tabs)

- **Tray icons:** shape (Pie/Bar); pinned rows with color, name, path
  ("auto-detected" or the chosen path) and "Icon shows"; Add a path….
- **Detection:** WSL distros, Docker Desktop data disk, daily large-file scan and
  its threshold, show "Other large files" in the flyout, and the "Code folders to
  search" list.
- **Alerts:** warning and critical levels, notify when free space drops below a
  level, and the daily reminder time.
- **Clean up:** defaults for prune build cache, remove dangling images,
  pre-select volumes whose folder is gone, compact afterwards (when idle / right
  away), and restart Docker Desktop after compacting.
- **General:** refresh interval (default 5 min), Start with Windows.

## Architecture

- **.NET 10, WPF**, with WPF-UI (Fluent controls) and H.NotifyIcon (tray icons).
- `src/DiskTray.Core` (`net10.0`): detection, size and usage queries, volume
  confidence rules, settings model, alert state machine. No UI, so it's unit-tested
  and the tests run in WSL.
- `src/DiskTray.App` (`net10.0-windows`, WinExe): tray icons, flyout, Settings,
  Clean up, toasts.
- `src/DiskTray.Compactor` (`net10.0-windows`, elevated): the only part that
  needs admin. It shuts down WSL and compacts the VHDX paths it's given (diskpart
  `compact vdisk`, or `Optimize-VHD` when Hyper-V tools are present), then reports
  back.
- **Settings** are stored as JSON under `%LOCALAPPDATA%\DiskTray\`.
- **Install location.** The app runs from `%LOCALAPPDATA%\DiskTray\`, not from
  `\\wsl.localhost\…`, because it has to keep running while WSL is shut down for a
  compact. Start with Windows uses the Run key or Task Scheduler.
- **Build.** `EnableWindowsTargeting` lets everything build from WSL. The app only
  runs on Windows.
