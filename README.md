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
| `scripts/deploy.sh` | Publishes from WSL to Windows and restarts the app |

## Build

Needs the .NET 10 SDK. Builds from Windows or WSL, but runs only on Windows, where it
needs the .NET 10 Desktop Runtime.

```sh
dotnet build DiskTray.slnx
dotnet test
```

## Run

From WSL, `scripts/deploy.sh` publishes to `%LOCALAPPDATA%\Programs\DiskTray` and starts
it, closing a running copy first.

New tray icons start out in the taskbar's overflow (`^`). To keep them visible, turn
them on under **Settings → Personalization → Taskbar → Other system tray icons**, or
drag them onto the taskbar.

Errors are logged to `%LOCALAPPDATA%\DiskTray\logs\disktray.log`.
