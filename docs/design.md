# DiskTray design

Mockup: https://claude.ai/artifact/PyZbd4d2EFp8ysLhyV8GHt

DiskTray is a Windows tray app that shows how much of drive C: the WSL and Docker
virtual disks (VHDX files) take up compared with Windows, and helps get that space
back. VHDX files grow but never shrink on their own, so the app tracks two numbers
for each disk file: its size on C:, and how much of it is actually in use. The
difference is *reclaimable* and is shown as a lighter tint of the item's color.

## Tray icons

- Up to 3 pinned items, each with its own icon: Docker's data disk, a WSL distro, a
  drive, or any file or folder. At least one stays pinned. Tray slots are fixed-size
  squares, so each icon is redrawn in code on every refresh.
- **Shape** (one setting for all icons): Pie, or Bar (a vertical bar half the icon
  wide, filled from the bottom, so a disk icon reads like a gauge with free space on top).
- **Disk-file icons (Docker, Ubuntu, any pinned path).** The whole circle (or bar)
  is that disk file plus the free space on its drive. It shows used (item color),
  reclaimable (lighter tint) and free (dark gray). Today: Docker ≈ 73% (18% used,
  55% reclaimable), Ubuntu ≈ 56%. Pinned files and folders have no reclaimable part.
  - "Icon shows" option: **Disk + free space** (default; **Size + free space** for a
    path) or **Share of drive** (the item, the rest of what's used on the drive, and free).
- **Drive icon.** The whole drive broken down into the pinned items on it, largest
  first, then Windows & other and free. Items that aren't pinned count as Windows &
  other.
  - "Icon shows" option: **Drive breakdown** (default) or **Used vs free**.
- **Pinned folders** are added up in the background, when pinned and then hourly, since
  walking a big folder takes a while (links and junctions are skipped). Their tooltip and
  legend say when ("as of 2:14 PM"). Until the first count, the icon is a plain circle
  saying so.
- **Missing items** (Docker not installed, a deleted file, a drive that isn't there)
  keep their icon as a plain circle whose tooltip says what's missing.
- **What gets compacted** stays Docker's data disk and the primary distro's, whatever
  is pinned, and so does the "reclaimable" line in notifications.
- **Warning indicator.** An amber ring or outline when free space is below the
  warning level, red below the critical level.
- **Tooltip.** For example: `Docker data · 129 GiB (75 GiB reclaimable)` /
  `C: 50 GiB free (11%) – below 15% warning`. The drive icon adds a breakdown line
  (`Docker 129 GiB · Ubuntu 60 GiB · Other 233 GiB`). Windows cuts tooltips off at 127
  characters, so a long one drops its middle lines first.
- **Sizes** are 1024-based and labelled GiB (also KiB, MiB, TiB), with one decimal below
  10 and whole numbers from 10 up. The numbers match Explorer, which counts the same
  way but labels them GB.
- **Default icons:** the Docker data disk, the primary WSL distro (WSL's default
  distro, or else the largest), and the system drive.
- Icons are drawn at the taskbar's DPI (16 px at 100%, 24 at 150%, 32 at 200%). Each
  icon's GUID is derived from the exe path and the item (Docker, the distro's ID, the
  drive or the path), because Windows ties tray GUIDs to the path and remembers pinning
  by GUID. The default items keep the keys they had before pinning existed.

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

Each pinned item has a color: its usual one (Docker blue, WSL orange; pinned files and
folders take purple `#A371F7`, green `#3FB950` and teal `#1ABC9C` in turn), or one
picked in a color picker (shade square, hue strip, hex box, suggested colors and a
preview of the tray icon). The tint is derived from the color. The picker warns when a
color is below 1.5 : 1 against the free slice, where it would blend in. A drive's color
is its Windows & other slice.

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
- **Large-file scan.** Once a day (checked hourly, first a couple of minutes after
  startup), a low-priority look through the user folder for any file over a threshold
  (default 2 GiB), such as `.vhdx`, `.vhd`, `.vmdk`, `.qcow2` or `.iso`. Links and
  junctions are skipped. The result is kept in `largefiles.json`, so a restart doesn't
  look again; lowering the threshold does. Files that aren't already shown (Docker's and
  the distros' disks, pinned files, and anything inside a pinned folder) show up in the
  flyout's "Other large files" section, largest first, five at most, and each has a pin
  button (dimmed, with a tooltip, when all 3 pins are in use). The section can be turned
  off.
- **Turning detection off.** Settings › Detection can skip WSL distros or Docker's data
  disk; a pinned one then says "Turned off in Settings › Detection".
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
- **Add… › File… / Folder…** in Settings › Tray icons pins any file or folder by hand.

## Flyout (left-click a tray icon)

- Header: "Disk usage · Drive C: · 472 GiB" and a settings gear.
- A warning banner when below a level, e.g. "51 GiB free (11%) – below your 15% warning".
- A donut of the whole drive with free space in the center, and a legend listing
  each item's size plus "N used · N reclaimable". Disk files go largest first;
  Windows & other and Free are always last.
- "Lighter shade = space a disk file is holding but not using."
- "Other large files" (over 2 GiB, not pinned), each with its folder, size and a pin
  button, and when the scan ran. Can be turned off.
- "Reclaimable by compacting" total.
- Footer: "Updated 1:41 PM · checks every 5 min", and the **Clean up…** button.
- While a clean up runs, a line under the banners says where it is ("Compacts once you
  quit Docker Desktop and close Ubuntu", "Compacting Docker data (1 of 2) · 40%"), with
  **Cancel** while it's still waiting. The tray menu's **Clean up…** turns into
  **Cancel clean up** then.

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

One dialog with two steps, opened from the flyout's **Clean up…** button or the tray
menu. Both steps are on by default, and either can be unchecked. Nothing runs until the
user clicks **Clean up**.

1. **Prune Docker** (runs now, while Docker is running).
   - Build cache (`docker builder prune --force`, the part Docker counts as reclaimable)
     and dangling images (`docker image prune --force`).
   - Unused volumes (no container, running or stopped, uses them), grouped by Compose
     project, behind "Choose what to remove". Each group has a confidence tag (see
     below). Picked volumes are removed one at a time with `docker volume rm`, never
     `volume prune`, so one that's back in use doesn't stop the rest.
   - Only offered when `docker desktop status` says Docker Desktop is running; otherwise
     the step says to start it. DiskTray never starts it on its own.
   - The dialog opens straight away; the prune step fills in once Docker has answered
     (`docker system df -v` can take a few seconds), and **Clean up** waits for that.
2. **Compact disks** (Docker's data disk and the main distro's).
   - Shows each disk's size now and roughly after ("Docker 129 → ~19 GiB"): what's in
     use, less what the prune takes out of Docker's.
   - When: **When WSL is idle** (default) or **Right away**.
     - Idle: starts once Docker Desktop has quit and no WSL distros are running (checked
       every 30 s). Nothing gets interrupted.
     - Right away: shows an amber warning listing what gets closed (each running distro's
       terminals, VS Code and Claude Code sessions, and Docker Desktop's running
       containers).
   - Takes about 10–20 min and asks for admin. **Start Docker Desktop afterwards** is
     ticked when Docker Desktop is running as the dialog opens.
- **Footer.** The **Clean up** button has a fixed width and label and is disabled
  when nothing is selected. The left side shows a summary ("C: gets back ~152 GiB")
  and a plan ("Prunes now, compacts when WSL is idle").
- **Order of a run.** Prune → ask for admin (starts the Compactor) → wait for idle, or
  stop Docker Desktop (`docker desktop stop`) → `wsl --shutdown` → the Compactor
  compacts → `docker desktop start` if ticked, even when compacting failed. Turning down
  the admin prompt leaves WSL and Docker running. Notifications say when compacting
  starts and how it all went ("C: got back 135 GiB").
- **Dry run.** `DiskTray.exe --dry-run` (or `scripts/deploy.sh --dry-run`) runs the same
  steps but only logs the ones that change something; the Compactor still asks for
  admin and reports each disk without compacting it. The dialog says so at the top.

### Volume confidence

Volume labels only carry `com.docker.compose.project` (dots stripped, e.g.
`acme.web-app` → `acmeweb-app`) and
`com.docker.compose.volume`. Folder paths come from container labels
(`devcontainer.local_folder`, `com.docker.compose.project.working_dir`). In order:

1. The path from a container label (exact). VS Code's `devcontainer.local_folder`
   (`\\wsl.localhost\Ubuntu\…`) wins; Compose's working folder doesn't say which distro
   it's in, so that comes from a bind mount's source, or else is the main distro.
2. A project → folder cache the app records from containers it sees each time the
   dialog opens, saved as `projects.json` (exact).
3. A name match inside the "Code folders to search" list from Settings › Detection
   (best guess), plus the folders that recorded projects live in.

Folders inside a distro are only looked at (through `\\wsl.localhost`) when the distro is
already running, since looking would start it. A folder only counts as deleted when its
parent can be read and lacks it, so a hiccup reading `\\wsl.localhost` can't pre-select
anything.

| Tag | Pre-selected |
|---|---|
| Folder deleted | Yes |
| Not checked (its distro isn't running) | No |
| No matching folder in ~/git | No |
| Folder exists | Never |
| No project (unnamed volume) | Never |

## Settings (tabs)

Opened from the flyout's gear or the tray menu. Nothing changes until **Save**; **Cancel**
throws the changes away. The tabs are Tray icons, Detection, Alerts, Clean up and
General.

- **Tray icons:** shape (Pie/Bar); pinned rows with color, name (blank for the detected
  one), path ("auto-detected", the drive, or the chosen path) and "Icon shows", each with
  a remove button; **Add…** lists what was detected but isn't pinned (Docker data, each
  distro, each fixed drive), then File… and Folder….
- **Detection:** WSL distros, Docker Desktop data disk, daily large-file scan and
  its threshold, show "Other large files" in the flyout, and the "Code folders to
  search" list (Add folder… starts in `\\wsl.localhost`).
- **Alerts:** warning and critical levels, notify when free space drops below a
  level, and the daily reminder time.
- **Clean up:** what the Clean up dialog starts with: clear the build cache, remove
  dangling images, pick unused volumes from projects whose folder is gone (other unused
  volumes are never picked), compact the disks and when (idle / right away), and start
  Docker Desktop afterwards if it was running. All on, and when idle, by default.
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
  needs admin, and it does nothing else. Its manifest requires admin, so starting it
  shows the admin prompt. It gets the VHDX paths on its command line (fixed once the
  prompt shows; only full local `.vhdx` paths are accepted), waits for the app to say
  "go", then compacts each detached disk with Windows' virtual disk API
  (`CompactVirtualDisk`, what `Optimize-VHD` and diskpart use), which works whatever
  file system is inside. The app stops Docker Desktop and shuts down WSL itself, without
  admin, and starts Docker again afterwards, so Docker never runs elevated.
  - **Talking to the app.** Over a named pipe with a fresh random name that the app
    creates before starting it (first instance only, so nothing else can take the
    name), and the app checks that the process on the other end is the one it started.
    The Compactor sends status and log lines; the app sends "go" or "cancel". It writes
    no files, since anything it wrote in a folder the user can change could be
    redirected elsewhere with admin rights. If the app goes away while it's waiting, it
    stops; once compacting has started it finishes.
  - It waits up to a minute for WSL to let go of each disk after the shutdown, and
    reports a disk that stays in use (something started WSL again) without compacting
    it.
- **Settings** are stored as JSON under `%LOCALAPPDATA%\DiskTray\`, the last usage
  readings in `usage.json`, what alerts have fired in `alerts.json`, Compose
  projects' folders in `projects.json` and the last large-file scan in
  `largefiles.json` there, and logs go to
  `%LOCALAPPDATA%\DiskTray\logs\disktray.log` (the Compactor's lines included).
- **Install location.** The app runs from `%LOCALAPPDATA%\Programs\DiskTray\` (the usual
  place for per-user installs), not from `\\wsl.localhost\…`, because it has to keep
  running while WSL is shut down for a compact.
- **Start with Windows.** On first launch the app adds itself to the current user's Run
  key, turned on, so it's listed in Task Manager's Startup apps. After that, Settings
  never removes the entry; it flips the same switch Task Manager does
  (`Explorer\StartupApproved\Run`), so either place can turn it on or off and both
  agree. It counts as on only when the entry points at this copy.
- **Settings › Apps and uninstalling.** There's no installer: `scripts/deploy.sh` copies
  the app in. On each start, the installed copy (only that one, not a build elsewhere)
  writes its entry under `HKCU\…\Uninstall\DiskTray` (name, icon, version, size, folder,
  first install date) so it shows in Settings › Apps with an **Uninstall** button.
  `DiskTray.exe --uninstall` (what that button runs) asks first, closes the running copy,
  then removes the Run and Startup apps values, the toast registration Windows
  notifications need for an unpackaged app, the Settings › Apps entry and
  `%LOCALAPPDATA%\DiskTray`. A hidden `cmd` deletes `%LOCALAPPDATA%\Programs\DiskTray`
  a few seconds after DiskTray exits; it only ever deletes that folder.
  `--uninstall --quiet` (and `scripts/uninstall.sh`) skips the questions.
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
