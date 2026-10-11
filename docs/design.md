# DiskTray design

Mockup: https://claude.ai/artifact/PyZbd4d2EFp8ysLhyV8GHt

DiskTray is a Windows tray app that shows how much of drive C: the WSL and Docker
virtual disks (VHDX files) take up compared with Windows, and helps get that space
back. VHDX files grow but never shrink on their own, so the app tracks two numbers
for each disk file: its size on C:, and how much of it is actually in use. The
difference is *reclaimable* and is shown as a lighter tint of the item's color.

## Tray icons

- Up to 3 pinned icons. Tray slots are fixed-size squares, so each icon is redrawn
  in code on every refresh.
- **Shape** (one setting for all icons): Pie, or Bar (vertical bar).
- **Disk-file icons (Docker, Ubuntu, any pinned path).** The whole circle (or bar)
  is that disk file plus the free space on C:. It shows used (item color),
  reclaimable (lighter tint) and free (dark gray). Today: Docker ≈ 73% (18% used,
  55% reclaimable), Ubuntu ≈ 56%.
  - "Icon shows" option: **Disk + free space** (default) or **Share of drive**.
- **Drive icon.** The whole drive broken down into Docker / Ubuntu / Windows & other /
  free.
  - "Icon shows" option: **Drive breakdown** (default) or **Used vs free**.
- **Warning indicator.** An amber ring or outline when free space is below the
  warning level, red below the critical level.
- **Tooltip.** For example: `Docker data · 129 GiB (75 GiB reclaimable)` /
  `C: 50 GiB free (11%) – below 15% warning`. The drive icon adds a breakdown line
  (`Docker 129 GiB · Ubuntu 60 GiB · Other 233 GiB`). Windows cuts tooltips off at 127
  characters, so a long one drops its middle lines first.
- **Sizes** are 1024-based and labelled GiB (also KiB, MiB, TiB), with one decimal below
  10 and whole numbers from 10 up. The numbers match Explorer, which counts the same
  way but labels them GB.
- **Default icons** (until Settings exists): the Docker data disk, the primary WSL
  distro (WSL's default distro, or else the largest), and drive C:.
- Icons are drawn at the taskbar's DPI (16 px at 100%, 24 at 150%, 32 at 200%). Each
  icon's GUID is derived from the exe path, because Windows ties tray GUIDs to the
  path and remembers pinning by GUID.

### Colors

| Item | Color | Reclaimable tint |
|---|---|---|
| Docker | `#2496ED` | `#92CAF6` |
| Ubuntu / WSL | `#E95420` | `#F4AA90` |
| Windows & other | `#D0D0D0` | — |
| Free | `#3A3A3A` | — |
| Warning | `#F0B040` | — |
| Critical | `#E5534B` | — |
| Accent | `#4CC2FF` | — |

The tint is the item color mixed halfway with white. The free slice next to it is dark
gray, so a darker shade blended into it (about 1.3 : 1) on both dark and light taskbars;
the tint stands out (about 6 : 1). The icons look the same on either taskbar theme.

The flyout follows the app theme. On a light background, Windows & other is `#8C8C8C`
and free is `#E0E0E0`, so free stays the lightest slice.

Each pinned item has a color picker, and its tint is derived from the chosen color.

## Detection

- **WSL distros.** Read from the registry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss`.
  Each distro's disk is `BasePath` (which may start with `\\?\`) plus `VhdFileName`
  (often empty, meaning `ext4.vhdx`). WSL 1 distros have no disk file and are skipped.
  Docker Desktop's own distros (`docker-desktop*`) aren't shown as distros.
- **Docker Desktop.** The data disk is `disk\docker_data.vhdx` under the
  `CustomWslDistroDir` setting, read from `%APPDATA%\Docker\settings-store.json`
  (newer versions) or `settings.json` (older), defaulting to `%LOCALAPPDATA%\Docker\wsl`.
  Older Docker versions kept data in the `docker-desktop-data` distro instead, which is
  used when `docker_data.vhdx` doesn't exist.
- **Other drives.** A disk file on another drive is measured against that drive's free
  space, and isn't part of the C: breakdown.
- **Large-file scan.** Daily scan of the user folder for files over a threshold
  (default 2 GiB) such as `.vhdx`, `.vhd`, `.vmdk` and `.qcow2`. Large files that
  aren't pinned show up in the flyout's "Other large files" section, which can be
  toggled off, and each has a pin button.
- **"Used inside" numbers.** `df -P -B1 <mount>` run through `wsl.exe --exec`, only in
  distros listed by `wsl.exe --list --running --quiet`, so the app never starts WSL or
  Docker just to measure. A distro's disk is its `/`. Docker's data disk is mounted at
  `/mnt/docker-desktop-disk` in the `docker-desktop` distro (the legacy
  `docker-desktop-data` distro uses its `/`). The df line has to be for that exact mount
  point, because df on a plain folder reports the disk the folder is on.
  - When a distro isn't running, the last reading is used and the tooltip says when it
    was taken (`94 GiB reclaimable as of 2:14 PM`, or a date). Growth of the disk file
    since then counts as used, so the reclaimable number is never overstated.
  - There's no way to run a command only if the distro is already up. If a distro stops
    in the moment between the list and the `df`, WSL starts it again, and it shuts down
    on its own once idle.
- **Add a path…** pins any file or folder by hand.

## Flyout (left-click a tray icon)

- Header: "Disk usage · Drive C: · 472 GiB" and a settings gear.
- A warning banner when below a level, e.g. "51 GiB free (11%) – below your 15% warning".
- A donut of the whole drive with free space in the center, and a legend listing
  each item's size plus "N used · N reclaimable". Disk files go largest first;
  Windows & other and Free are always last.
- "Lighter shade = space a disk file is holding but not using."
- "Other large files" (over 2 GiB, not pinned), each with a pin button. Can be toggled.
- "Reclaimable by compacting" total, and one **Clean up…** button.
- Footer: "Updated 1:41 PM · checks every 5 min".

## Alerts

- **Warning** at 15% free (≈71 GiB on a 472 GiB drive). **Critical** at 5% (≈24 GiB).
- A toast fires as soon as free space drops below a level, e.g. "Drive C: is getting
  full · 49 GiB free (10%) – below your 15% warning level · 118 GiB is reclaimable by
  compacting."
- While still low, a daily reminder shows the first time the user is active
  (keyboard or mouse in the last minute or so, unlock or login) after a set time
  (default 9:00 AM). A drop toast counts as that day's reminder.
- An alert re-arms only after free space climbs 3 GiB above its level, so it doesn't
  flap.
- Each toast includes a picture of the flyout's donut and legend, drawn for the
  Windows mode (notifications don't follow the app theme).
- Each toast replaces the last, so the notification center holds at most one.
  Clicking it opens the flyout.
- What has fired is kept in `alerts.json`, so restarting doesn't notify again.
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
  when nothing is selected. The left side shows a summary ("C: gets back ~152 GiB")
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

Opened from the flyout's gear or the tray menu. Nothing changes until **Save**; **Cancel**
throws the changes away. Tabs arrive with the features they control, so for now there
are only Alerts (the two levels) and General.

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
- **General:** app theme (System, Light or Dark; default System) for the flyout,
  Settings and Clean up, refresh interval (default 5 min), Start with Windows (on by
  default; the same switch as Task Manager's Startup apps).

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
- **Settings** are stored as JSON under `%LOCALAPPDATA%\DiskTray\`, the last usage
  readings in `usage.json` and what alerts have fired in `alerts.json` there, and logs go to `%LOCALAPPDATA%\DiskTray\logs\disktray.log`.
- **Install location.** The app runs from `%LOCALAPPDATA%\Programs\DiskTray\` (the usual
  place for per-user installs), not from `\\wsl.localhost\…`, because it has to keep
  running while WSL is shut down for a compact.
- **Start with Windows.** On first launch the app adds itself to the current user's Run
  key, turned on, so it's listed in Task Manager's Startup apps. After that, Settings
  never removes the entry; it flips the same switch Task Manager does
  (`Explorer\StartupApproved\Run`), so either place can turn it on or off and both
  agree. It counts as on only when the entry points at this copy. An uninstaller should
  remove both values, and the toast registration that Windows notifications need for
  an unpackaged app.
- **One copy at a time.** A second launch exits right away. `DiskTray.exe --exit`
  closes the running copy cleanly, so its icons leave the tray (used by
  `scripts/deploy.sh`, and later by updates).
- **`DiskTray.exe --render-icons <folder>`** saves the current icons as PNGs at several
  sizes plus their tooltips, the flyout and the notification picture in light and dark,
  and the app icon, for checking the drawing without the tray.
- **App icon.** `src/DiskTray.App/app.ico` is the flyout's donut (Docker and WSL with
  their tints, and Windows & other), drawn by `AppIcon`. `--render-icons` writes a fresh
  `app.ico` to copy over it after changing the drawing.
- **Build.** `EnableWindowsTargeting` lets everything build from WSL. The app only
  runs on Windows. Tests use xUnit v3 on Microsoft.Testing.Platform (opted in through
  `global.json`).
