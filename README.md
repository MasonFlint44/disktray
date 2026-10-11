# DiskTray

A Windows tray app that shows how much of drive C: your WSL and Docker virtual disks
take up, warns when space runs low, and helps reclaim it (prune Docker, compact
the VHDX files).

See [docs/design.md](docs/design.md) for the design.

## Layout

| Project | What it is |
|---|---|
| `src/DiskTray.Core` | UI-free logic (`net10.0`) |
| `src/DiskTray.App` | WPF tray app (`net10.0-windows`) |
| `src/DiskTray.Compactor` | Elevated helper that compacts VHDX files |
| `tests/DiskTray.Core.Tests` | Unit tests for Core (run anywhere, including WSL) |
| `installer` | WiX project for the MSI |
| `scripts/deploy.sh` | Publishes a development copy from WSL to Windows and restarts it |
| `scripts/build-msi.sh` | Builds the MSI from WSL |

## Build

Needs the .NET 10 SDK. Builds from Windows or WSL, but runs only on Windows. The
development copy needs the .NET 10 Desktop Runtime; the installer brings its own.

```sh
dotnet build DiskTray.slnx
dotnet test
```

## Install

`scripts/build-msi.sh` builds `artifacts/msi/DiskTray-<version>-x64.msi` (it also needs
a .NET 6 or later SDK on Windows, since WiX only runs there). GitHub Actions builds the
same MSI on every push and attaches it to a release for each `v<version>` tag. It
installs to `C:\Program Files\DiskTray` and asks for admin once.

For trying changes, `scripts/deploy.sh` publishes a development copy to
`%LOCALAPPDATA%\Programs\DiskTray` and starts it, closing a running copy first. That copy
can't compact disks: the Compactor only runs as admin from a folder that needs admin
to change. `scripts/uninstall.sh` removes it.

New tray icons start out in the taskbar's overflow (`^`). To keep them visible, turn
them on under **Settings → Personalization → Taskbar → Other system tray icons**, or
drag them onto the taskbar.

Errors are logged to `%LOCALAPPDATA%\DiskTray\logs\disktray.log`.
