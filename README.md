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

## Build

Needs the .NET 10 SDK. Builds from Windows or WSL, but runs only on Windows.

```sh
dotnet build DiskTray.slnx
```
