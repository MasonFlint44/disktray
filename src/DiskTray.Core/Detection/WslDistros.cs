namespace DiskTray.Core.Detection;

/// <summary>
/// One distro key under <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss</c>.
/// <paramref name="Id"/> is the key name, a GUID in braces.
/// </summary>
/// <param name="Flags">The key's Flags value, null if it has none. <see cref="VmModeFlag"/> marks WSL 2.</param>
public sealed record WslRegistryEntry(string Id, string? DistributionName, string? BasePath, string? VhdFileName, int? Flags)
{
    /// <summary>Set in Flags for a WSL 2 distro, which runs in the VM and has a disk file.</summary>
    public const int VmModeFlag = 0x8;

    public bool IsWsl2 => Flags is not { } flags || (flags & VmModeFlag) != 0;
}

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
            .Where(e => e.IsWsl2
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
