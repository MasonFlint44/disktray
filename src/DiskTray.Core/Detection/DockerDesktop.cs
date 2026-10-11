using System.Text.Json;

namespace DiskTray.Core.Detection;

/// <summary>Finds Docker Desktop's data disk (<c>docker_data.vhdx</c>).</summary>
public static class DockerDesktop
{
    public const string DataDiskFileName = "docker_data.vhdx";

    /// <summary>The distro older Docker Desktop versions kept their data in, before docker_data.vhdx.</summary>
    public const string LegacyDataDistroName = "docker-desktop-data";

    /// <summary>
    /// The data disk lives in <c>disk\</c> under the "CustomWslDistroDir" setting, which
    /// defaults to <c>%LOCALAPPDATA%\Docker\wsl</c>. Newer versions store settings in
    /// settings-store.json (PascalCase keys), older ones in settings.json (camelCase).
    /// </summary>
    public static string DataDiskPath(IDiskProbe probe, string localAppData, string appData)
    {
        string? distroDir =
            ReadDistroDir(probe.ReadAllText(WindowsPath.Join(appData, "Docker", "settings-store.json")))
            ?? ReadDistroDir(probe.ReadAllText(WindowsPath.Join(appData, "Docker", "settings.json")));

        return WindowsPath.Join(distroDir ?? WindowsPath.Join(localAppData, "Docker", "wsl"), "disk", DataDiskFileName);
    }

    internal static string? ReadDistroDir(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.NameEquals("CustomWslDistroDir") || property.NameEquals("customWslDistroDir"))
                {
                    return property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } dir
                        ? dir
                        : null;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
