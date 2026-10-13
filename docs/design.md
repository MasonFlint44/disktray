# DiskTray design

DiskTray is a Windows tray app that shows how much of the Windows drive (`%SystemDrive%`,
usually C:, written as C: below) the WSL and Docker virtual disks (VHDX files) take up compared with Windows, and helps get that space
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
- **What gets compacted**, whatever is pinned: Docker's data disk, the primary distro's,
  and any other distro once it's been measured holding space it doesn't use. The
  flyout's "Reclaimable by compacting" total and the "reclaimable" line in notifications
  count the same disks, pinned or not, that are on the drive they're about (C:); a disk
  on another drive is compacted too, but not counted there. Docker Desktop's own small
  distros are left out.
- **Pinned paths** must be on one of the PC's own drive letters. A path in
  `\\wsl.localhost` would start WSL on every refresh (and keep it from going idle for a
  compact), and a network share could hold up every refresh, so neither can be picked,
  and settings holding a `\\` path drop it. A mapped network drive letter looks like a
  local path in settings, so only the pickers refuse it.
- **Two pins for the same item** (the default distro pinned both as "the default" and by
  name) get one icon.
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
  (often empty, meaning `ext4.vhdx`). WSL 1 distros have no disk file and are skipped:
  they're the ones without the VM flag (`0x8`) in the key's `Flags` value.
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
    on its own once idle. While a clean up waits for WSL to go idle, stops it or
    compacts, refreshes don't run `df` at all and show the last readings, so they can't
    start a distro that was just stopped.
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
- "Reclaimable by compacting" total: every disk on this drive that Clean up compacts,
  pinned or not.
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
- Each alert replaces the last alert, and each Clean up notification the last Clean up
  one, so the notification center holds at most two. Clicking one opens the flyout.
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
   - Only offered when Docker Desktop is running and has said what can be pruned;
     otherwise the step says to start it (or, if it's running but didn't answer, to try
     again). DiskTray never starts it on its own.
   - Whatever is picked to remove is always listed: the volume list opens on its own
     when any are picked.
   - Prunes are counted by what Docker says it freed ("Total reclaimed space"), falling
     back to the estimate. The build cache estimate is an upper bound, since
     `builder prune` without `--all` leaves cache that's still referenced.
   - The dialog opens straight away; the prune step fills in once Docker has answered
     (`docker system df -v` can take a few seconds), and **Clean up** waits for that.
2. **Compact disks** (see "What gets compacted" above).
   - Shows each disk's size now and roughly after ("Docker 129 → ~19 GiB"): what's in
     use, less what the prune takes out of Docker's.
   - When: **When WSL is idle** (default) or **Right away**.
     - Idle: starts once Docker Desktop has quit, `wsl --list --running` says no distros
       run, and none of the WSL or Docker disk files is still open (checked every 30 s),
       Docker Desktop's own system disk (`docker-desktop`'s) included, though it isn't
       shown or compacted.
       Anything it can't find out counts as still running: a `wsl.exe` that times out
       or fails with a WSL error code, a `docker desktop status` it can't read (then
       Docker's own distros running means Docker is), or a disk it can't check. So a
       hung WSL is never taken for an idle one. Once everything looks idle, WSL is asked
       once more just before it's shut down, since a check takes a while, and Cancel
       clicked during a check still cancels. Nothing gets interrupted.
     - Right away: shows an amber warning listing what gets closed (each running distro's
       terminals, VS Code and Claude Code sessions, and Docker Desktop's running
       containers), or saying it couldn't tell what's running. Docker Desktop has to be
       stopped first: if DiskTray can't tell whether it's running, or it's still running
       after `docker desktop stop`, nothing gets shut down or compacted.
   - Takes about 10–20 min and asks for admin. **Start Docker Desktop afterwards** is
     ticked when compacting right away and Docker Desktop is running as the dialog opens.
     When compacting waits for WSL to go idle, it starts unticked: the user is the one who
     quits Docker then, maybe for the day, and it shouldn't come back on its own hours
     later. Switching between the two follows that until the box is clicked.
- **Footer.** The **Clean up** button has a fixed width and label and is disabled
  when nothing is selected. The left side shows a summary ("C: gets back ~152 GiB")
  and a plan ("Prunes now, compacts when WSL is idle").
- **Order of a run.** Ask for admin (starts the Compactor, which waits) → prune → wait for
  idle, or stop Docker Desktop (`docker desktop stop`) → `wsl --shutdown` → the Compactor
  compacts → `docker desktop start` if ticked, even when compacting failed. The admin
  prompt comes first, off the UI thread, while DiskTray still has the foreground from the
  click, so it shows in front rather than flashing in the taskbar after a long prune. The
  dialog stays open (greyed out) until the prompt is answered, and the prompt belongs to
  it. Turning it down still prunes, and leaves WSL and Docker running. Notifications say
  when compacting starts and how it all went ("C: got back 135 GiB", each drive with its
  own total).
  - If `docker desktop stop` fails, DiskTray asks Docker again, since the command can fail
    late, after Docker has stopped. Stopped, the run goes on. Still running, nothing is
    shut down or compacted and Docker is left running. Can't tell, nothing is shut down
    or compacted, and Docker is started again if ticked, in case it had stopped (starting
    a running Docker does nothing). If `wsl --shutdown` fails, nothing is compacted (the
    disks would still be open), and Docker is started again if ticked.
  - A disk whose path the Compactor wouldn't take (say a Docker folder written with
    forward slashes that can't be read plainly) is left out and named in the result; the
    others are still compacted.
  - Before it says it's ready, the Compactor checks every disk (see "Only what the user
    could change already" and "Pins each disk's path" below). A disk it would refuse is
    named in the result and left out; if it would refuse them all, it says so as its
    first word, and DiskTray stops nothing: Docker and WSL keep running.
  - The Compactor reports at least once a second while it works (cancelling a compact
    Windows stopped reporting on can take up to a minute). If it goes 2 minutes without
    a word, DiskTray stops following it and says it can't tell whether compacting
    finished. The run isn't over, though, since the Compactor may still be compacting:
    Clean up stays locked, as after a restart (below), until it ends, and only then is
    Docker started again if ticked, so the two never fight over its disk. The same goes
    for a run that ends with an error part way. The lock is held from the run's start
    to the Compactor's end with no gap between, and a Clean up dialog opened before it
    was taken can't start a second run.
- **Docker Desktop 4.39 or later**, with the WSL 2 backend: stopping, starting and asking
  about it use its `docker desktop` commands (added in 4.37, generally available in 4.39).
- **Exiting during a run.** Exit asks first while the run prunes (nothing gets
  compacted then), stops Docker Desktop or shuts down WSL (nothing gets compacted, and
  the next start starts Docker again if the run was going to), or compacts (it carries
  on without DiskTray). Waiting for admin or for WSL to go idle, Exit just cancels. From
  stopping Docker on, the run is recorded in `cleanup-running.json`, along with whether
  the Compactor had been told to go. If DiskTray goes away before the end (Exit, an
  upgrade, a crash), its next start waits for the Compactor in this Windows session to
  end (Clean up waits too), starts Docker Desktop if the run was going to, and says what
  happened: compacting carried on, or nothing was compacted. It waits 2 hours at most,
  far longer than compacting takes, so a stuck Compactor can't keep Clean up locked;
  then it says so, and to start Docker once the Compactor has ended.
- **Closed from outside.** Closed by the installer or `--exit` (which doesn't ask) while a
  run prunes or waits (for admin, or for WSL to go idle), DiskTray says in a notification
  that nothing was compacted; from stopping Docker on, the next start finishes up as
  above. `--exit` also waits a few seconds for a Compactor that was only waiting to
  quit, so the installer doesn't take it for one that's compacting.
- **Dry run.** `DiskTray.exe --dry-run` runs the same steps but only logs the ones that
  change something; the Compactor still asks for admin, makes the same checks of each
  disk (its path and the account that asked; only holding the disk is left out, since WSL
  is still running) and reports each without compacting it. The dialog says so at the
  top. It needs a released, installed copy to reach the Compactor: a development copy
  (`scripts/deploy.sh --dry-run`) or the dev MSI can't start it, dry run or not (see
  "Only from an admin-only folder" and "Native, not .NET").

### Volume confidence

Volume labels only carry `com.docker.compose.project` (dots stripped, e.g.
`acme.web-app` → `acmeweb-app`) and
`com.docker.compose.volume`. Folder paths come from container labels
(`devcontainer.local_folder`, `com.docker.compose.project.working_dir`). In order:

1. The path from a container label (exact). VS Code's `devcontainer.local_folder`
   (`\\wsl.localhost\Ubuntu\…`) wins; Compose's working folder doesn't say which distro
   it's in, so that comes from a bind mount's source, or else is assumed to be the main
   distro. An assumed distro is marked: its folder can be found there (folder exists),
   but not being there doesn't make it deleted, since it may be in another distro.
2. A project → folder cache the app records from containers it sees each time the
   dialog opens, saved as `projects.json` (exact, or marked assumed as above; an assumed
   folder never replaces a recorded one, and a recorded one wins over an assumed one from
   the containers now). Entries saved before this was marked count as assumed.
3. A name match inside the "Code folders to search" list from Settings › Detection
   (best guess), plus the folders that recorded projects live in.

Folders inside a distro are only looked at (through `\\wsl.localhost`) when the distro is
already running, since looking would start it (a distro that stops in the moment between
the two can still get started that way). A folder only counts as deleted when its
parent can be read and has nothing by that name at all (not a folder, file or link, as
`\\wsl.localhost` can show a Linux symlink as either), so a hiccup reading it can't
pre-select anything. Each look has 5 seconds: one that takes longer counts as can't tell,
and nothing more is asked of that distro (or of Windows, for a Windows folder), so a
stuck `\\wsl.localhost` can't leave the dialog checking for good.

A project's known folder being gone still isn't enough on its own. It's only called
deleted when nothing else says the project lives on: the other folder known for it (from
a container now, or remembered) isn't there either, or is in a distro that was recorded,
and no folder by its name turns up in the code folders. So a project deleted and cloned
again somewhere new, or moved, keeps its volumes unpicked.

| Tag | Pre-selected |
|---|---|
| Folder deleted | Yes |
| Not checked (its distro isn't running or was assumed, or it may be somewhere else) | No |
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
  volumes are never picked), compact the disks and when (idle / right away), and, when
  compacting right away, start Docker Desktop again afterwards if it was running. All on,
  and when idle, by default.
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
- `src/DiskTray.Compactor` (`net10.0-windows`, elevated, compiled ahead of time to a
  native exe with NativeAOT): the only part that needs admin, and it does nothing else.
  Its manifest requires admin, so starting it shows the admin prompt. It gets the VHDX
  paths on its command line (fixed once the prompt shows; only full local `.vhdx` paths
  written plainly are accepted: no `.`/`..`, forward slashes, empty parts, parts ending
  in a dot or space, wildcards, `:stream` or device names such as `NUL.vhdx`, and the
  app leaves out any other before prompting), checks each disk, waits for the app to say
  "go", then compacts each detached disk with Windows' virtual disk API
  (`CompactVirtualDisk`, what `Optimize-VHD` and diskpart use), which works whatever
  file system is inside. A disk's parents (a differencing disk's, whose paths are written
  inside the file) are never opened. If Windows stops answering how a compact is going,
  it's cancelled rather than waited on blind. The app stops Docker Desktop and shuts down
  WSL itself, without admin, and starts Docker again afterwards, so Docker never runs
  elevated. Its exit code is 0 when every disk was compacted, 1 when some weren't, 2
  when cancelled, 3 when it refused every disk before anything was stopped, 64 for
  arguments it doesn't take, 65 when there's no app to talk to and 70 when something
  went wrong that it didn't expect (it reports that as a failure first).
  - **Talking to the app.** Over a named pipe with a fresh random name that the app
    creates before starting it (first instance only, so nothing else can take the
    name; only the user and Administrators can connect, never over the network), and
    the app checks that the process on the other end is the one it started. The app
    makes itself the pipe's owner. Once it has connected, the pipe closing (not the process ending) means it's gone, so its last
    status is always read.
    The Compactor sends status and log lines; the app sends "go" or "cancel". The
    Compactor's end is opened for overlapped I/O: on a handle opened without it, Windows
    runs one call at a time, so its reader thread, always waiting for the app's next
    line, would hold up every status it sends. It writes no files, since anything it
    wrote in a folder the user can change could be redirected elsewhere with admin
    rights. If the app goes away while it's waiting, it stops; once it's been told to go
    it finishes, even if the app goes straight after.
  - **Only what the user could change already.** The disk paths come from settings
    anything running as the user can change (the WSL registry key, Docker's settings
    file), so the Compactor only compacts a disk the account that asked could write to
    itself. It checks with the token of the DiskTray on the other end of the pipe, which
    runs without admin: acting as that account, it opens the file for writing, without
    changing it. Windows names that process by its ID, which another process can be
    given once it ends, so the account found must also be the pipe's owner; a process
    that took over the ID (a service, say) is refused. It opens the handle it already has on the file again, never the path,
    since a path can lead elsewhere for the user than for an admin process (each has its
    own drive letters). A disk only an admin can change (another account's, or a virtual
    machine's) is refused, and so is everything when that account can't be told. It's
    checked before the Compactor says it's ready, while WSL still has the disk open (that
    counts as a yes, since the permission is checked first), and again while the disk is
    held for compacting. Reading another account's DiskTray can need the debug privilege,
    which is turned on only for that and off straight after (off even if the
    Compactor started with it on).
  - **Only from an admin-only folder.** Before the admin prompt, the app checks that
    nothing without admin rights can change the Compactor itself: write, replace or
    re-permission the exe or anything in its folder, add a file there (a planted DLL
    would load into it), or move the folder or one above it. It tests what it can do
    itself by opening each with those rights and closing again, then reads each one's
    owner and permissions for what any other account could: only SYSTEM,
    Administrators and TrustedInstaller may be let change one, or own one (another
    owner can always re-permission it, unless an OWNER RIGHTS entry limits what the
    owner gets). First, the
    exe's real path must be the path DiskTray runs it by, so no folder on the way is a
    junction or other link, and no drive letter of the user's own (`subst`) or network
    share is in it; and nothing in its folder may be a link. Otherwise the checks would
    read where a link leads now, and it could be pointed elsewhere while the prompt is
    up. The installer's `Program Files\DiskTray` passes. A copy in
    `%LOCALAPPDATA%` doesn't, because anything running as the user could swap it before
    the prompt is answered, so Clean up still prunes there but won't compact. DiskTray
    itself running with admin rights in effect won't compact either, whether it was
    started as administrator or runs as the built-in Administrator account (which UAC
    doesn't split): the Compactor would start with no prompt, and the check can't see
    what a normal process could change. Nor will it when it can't tell whether it has
    them. With UAC off there's no line to protect, so it's skipped.
  - **Native, not .NET.** A .NET runtime in an admin process honours settings in the
    user's environment, which anything running as the user can change, such as ones
    that load a profiler or startup hook. The NativeAOT exe has neither, and loads
    `virtdisk.dll`, `kernel32.dll` and `advapi32.dll` only from System32. Windows still
    looks for some DLLs it loads at start next to the exe first, which is one reason
    the folder check above matters. The app reads the exe itself: it only starts one
    that exports `DotNetRuntimeDebugHeader`, as NativeAOT exes do and the hosts that start
    the .NET runtime (framework-dependent or single-file) don't, and that has no
    `DiskTray.Compactor.dll` beside it. So a development build never runs as admin, with
    its `.dll` or without. The WiX build makes the same check before it packs a release.
  - **Pins each disk's path while compacting it.** The disk and the folders above it can
    usually be changed by the user, so the disk and each folder are held open without
    delete sharing while that disk is checked and compacted (none can be renamed,
    removed or replaced), and the disk is refused if any of them is a link, the disk
    has another name (a hard link), or it isn't where its path says (an 8.3 short name,
    or a drive letter made for a folder with `subst`, is refused that way). A disk reached
    through a junction, or through a volume mounted in a folder (both reparse points, as
    when a disk is moved to another drive that way), isn't compacted. Its sizes before
    and after are read through the held file, never its path. Windows' virtual disk
    service opens the disk by its path, so once it has, the held file is checked again
    for having become a link or gained a name. Paths longer than 260 characters work.
  - It waits up to a minute for WSL to let go of each disk after the shutdown, trying
    to hold it once a second, and reports a disk that stays in use (something started
    WSL again) without compacting it.
- **Settings** are stored as JSON under `%LOCALAPPDATA%\DiskTray\`, the last usage
  readings in `usage.json` (kept for 30 days after a disk was last detected, so
  switching detection off for a while doesn't lose them; only written when a reading
  changes, or once a day for when each disk was last seen), what alerts have fired in
  `alerts.json`, Compose projects' folders in `projects.json`, the last large-file scan
  in `largefiles.json` and a running clean up in `cleanup-running.json` there, with the
  pictures for notifications in `notifications\`. Logs go to
  `%LOCALAPPDATA%\DiskTray\logs\disktray.log` (the Compactor's lines included).
- **Installer.** A per-machine MSI built with WiX 5 (`installer/`), which installs to
  `C:\Program Files\DiskTray`, so only admins can change what Clean up runs as admin. It
  asks for admin once, at install. The app and Compactor are published self-contained, so
  nobody needs .NET installed (about 56 MB). It adds a Start menu shortcut and lists
  DiskTray in Settings › Apps, with Uninstall only. Unsigned, so a downloaded copy gets
  SmartScreen's "Windows protected your PC" until it's known. Running as the user doing
  the install, it:
  - closes the running copy before an upgrade or repair (`DiskTray.exe --exit`, which
    waits), and afterwards closes whatever copy still runs, wherever it's from (a
    development copy, say), so the new one doesn't find it and quit;
  - starts DiskTray afterwards through Explorer, so it never runs as admin even when the
    install did (not on silent installs; after a repair too);
  - starts DiskTray again after a silent upgrade a user started (winget, say), since it
    was closed for it, but not after a silent first install or anything run as SYSTEM;
  - on an uninstall (not an upgrade), runs `DiskTray.exe --remove-user-data`, which closes
    the running copy and removes what DiskTray added for that user: the Run and Startup
    apps values, the toast registration Windows notifications need for an unpackaged
    app, and `%LOCALAPPDATA%\DiskTray`. Other users' settings stay behind. It runs once
    the uninstall is under way, after the admin prompt, so turning the prompt down keeps
    them. It can't be undone, so an uninstall that fails after it has still removed them.

  As admin, on an upgrade or uninstall, it also closes DiskTray running for other
  signed-in users: it sends a close message and ends it if it hasn't gone after 15 s.
  Windows doesn't pass that message to other sessions, so in practice their copies are
  ended, and may leave a stale tray icon until hovered. While a Compactor is running in
  any user's session (a clean up is compacting a disk, or about to), the install stops
  with a message to try again once it's done, rather than end it or replace it part
  way. The user's data and other users' copies are left alone: those steps only run once
  the install is under way. An upgrade or repair that closed DiskTray and then doesn't
  finish (stopped that way, cancelled, or failed and rolled back) starts it again as it
  ends, so it can follow a compact and say when it's done. An upgrade puts the new files
  in place before it removes the old version, within the same install, so one that
  fails leaves the old version as it was. Files are replaced by version: each build's
  exes and DLLs get a fourth version number that only grows (the GitHub Actions run
  number, 0 for a local build), so a release replaces a dev MSI's files of the same
  version, and each build replaces the one before. A dev MSI over a release of the same
  version keeps the release's (newer) exes but adds the .NET Compactor's `.dll`, which
  the app then refuses, so uninstall a release before trying a dev MSI. A rebuilt MSI
  of the same version takes the installed one's place in Settings › Apps rather than
  installing beside it.

  Releases come from the GitHub Actions workflow, since NativeAOT for Windows needs
  Windows and the C++ build tools. It runs the tests (Core on Linux; everything,
  including the Windows tests below, on Windows) and builds the MSI on pushes to main,
  pull requests and tags, with a `SHA256SUMS.txt`; on a `v1.2.3` tag matching
  `Version` in `Directory.Build.props` it publishes a release with both attached. The
  WiX build refuses a Compactor that isn't the native one. Before building the MSI, the
  Compactor exe tests run again against the native exe that ships; then the runner
  installs the latest release's MSI; checks that a running Compactor (any program by
  that name) stops an upgrade and an uninstall without changing a file or the user's
  settings, and that DiskTray is started again; upgrades it to this build and checks
  every installed file is this build's and the settings stayed; checks the installed
  Compactor passes the app's checks as an everyday token; and, with DiskTray running,
  uninstalls it, checking DiskTray was closed and everything is gone, the settings too. Its actions are
  pinned to commits, not tags. `scripts/build-msi.sh` builds a `-dev` MSI from WSL for
  trying the installer, with a plain .NET Compactor that the app won't run (see
  "Native, not .NET"), so it can't compact (WiX only runs on Windows, so it uses the
  Windows dotnet, .NET 6 or later).
- **Development copy.** `scripts/deploy.sh` copies a framework-dependent build to
  `%LOCALAPPDATA%\Programs\DiskTray\` and starts it, for trying changes quickly; it isn't
  listed in Settings › Apps and can't compact (see above). `scripts/uninstall.sh` removes
  it. Either way the app runs from a Windows folder, not `\\wsl.localhost\…`, because it
  has to keep running while WSL is shut down for a compact. Builds before the installer
  listed themselves under `HKCU\…\Uninstall\DiskTray`; the app removes that entry.
- **Start with Windows.** On first launch the app adds itself to the current user's Run
  key, turned on, so it's listed in Task Manager's Startup apps. After that, Settings
  never removes the entry; it flips the same switch Task Manager does
  (`Explorer\StartupApproved\Run`), so either place can turn it on or off and both
  agree. It counts as on only when the entry points at this copy. When the entry names
  another copy (moving from a development copy to the installed one, say), the app points
  it here on start, keeping its on/off switch.
- **One copy at a time.** A second launch exits right away. `DiskTray.exe --exit`
  closes the running copy cleanly, so its icons leave the tray (used by
  `scripts/deploy.sh` and the installer), without asking first (see "Closed from
  outside").
- **`DiskTray.exe --render-icons <folder>`** saves the current icons as PNGs at several
  sizes plus their tooltips, the flyout and the notification picture in light and dark,
  and the app icon, for checking the drawing without the tray.
- **App icon.** `src/DiskTray.App/app.ico` is the flyout's donut (Docker and WSL with
  their tints, and Windows & other), drawn by `AppIcon`. `--render-icons` writes a fresh
  `app.ico` to copy over it after changing the drawing.
- **Build.** `EnableWindowsTargeting` lets everything build from WSL. The app only
  runs on Windows. Tests use xUnit v3 on Microsoft.Testing.Platform (opted in through
  `global.json`). `tests/DiskTray.Core.Tests` runs anywhere. `tests/DiskTray.Windows.Tests`
  runs only on Windows (`dotnet test` elsewhere leaves it out) and covers what runs as
  admin, on real files, pipes and processes in temp folders: the pipe between the app
  and the Compactor (and who may connect to it, and closing it under a waiting read),
  the path hold (links, junctions, hard links, short names, long paths, a disk in use),
  the account check (including an admin's everyday token, at Medium integrity, against
  a file only admins can change, a disk WSL has open, a pipe owned by another account,
  and the debug privilege being off again after use), the in-use check, the folder
  check (refusing a folder anyone can change, a junction to a protected folder, a link
  inside it and write access for another account; passing one only admins can; how
  DiskTray itself runs; telling a .NET Compactor from the native one), a compact that
  gives back the space a test VHDX held after its data was cleared, a long path, a
  compact Windows stops reporting on (with a stand-in for its virtual disk API),
  `HiddenProcess`'s timeouts, and the Compactor exe itself: compacting, a dry run,
  refusing every disk before it's ready, carrying on when the app goes after "go", and
  quitting on bad arguments, no app, or the app going away. Those run against the
  framework-dependent build, and in the GitHub Actions installer job again against the
  NativeAOT exe (`DISKTRAY_COMPACTOR_EXE`). The tests that need admin skip without it,
  and fail instead in the GitHub Actions build (`DISKTRAY_REQUIRE_ADMIN_TESTS`), whose
  runner has it. They write DiskTray's log to a temp file, never the real one.
