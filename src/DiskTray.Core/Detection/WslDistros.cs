namespace DiskTray.Core.Detection;

/// <summary>
/// One distro key under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss</c>.
/// <paramref name="Id"/> is the key name, a GUID in braces.
/// </summary>
public sealed record WslRegistryEntry(string Id, string? DistributionName, string? BasePath, string? VhdFileName, int Version);

public sealed record WslRegistryData(IReadOnlyList<WslRegistryEntry> Entries, string? DefaultDistributionId)
{
    public static WslRegistryData Empty { get; } = new([], null);
}

/// <summary>Reads the Lxss registry key. Implemented in the app, since the registry is Windows-only.</summary>
public interface IWslRegistry
{
    WslRegistryData Read();
}

/// <summary>A WSL 2 distro and the path of its virtual disk.</summary>
public sealed record WslDistro(string Id, string Name, string VhdxPath)
{
    /// <summary>Docker Desktop's own distros (docker-desktop, docker-desktop-data).</summary>
    public bool IsDockerOwned => IsDockerOwnedName(Name);

    public static bool IsDockerOwnedName(string name) => name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase);
}

public static class WslDistros
{
    public const string DefaultVhdFileName = "ext4.vhdx";

    public static IReadOnlyList<WslDistro> FromRegistry(WslRegistryData data) =>
        data.Entries
            // WSL 1 distros live in a plain folder, not a disk file.
            .Where(e => e.Version != 1
                && !string.IsNullOrWhiteSpace(e.DistributionName)
                && !string.IsNullOrWhiteSpace(e.BasePath))
            .Select(e => new WslDistro(
                e.Id,
                e.DistributionName!,
                WindowsPath.Join(
                    WindowsPath.StripLongPathPrefix(e.BasePath!),
                    string.IsNullOrWhiteSpace(e.VhdFileName) ? DefaultVhdFileName : e.VhdFileName)))
            .ToList();
}
