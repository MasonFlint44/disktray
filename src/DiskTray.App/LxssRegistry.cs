using DiskTray.Core.Detection;
using Microsoft.Win32;

namespace DiskTray.App;

/// <summary>Reads WSL's distro list from <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss</c>.</summary>
internal sealed class LxssRegistry : IWslRegistry
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public WslRegistryData Read()
    {
        using var lxss = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (lxss is null)
        {
            return WslRegistryData.Empty;
        }

        var entries = new List<WslRegistryEntry>();
        foreach (string id in lxss.GetSubKeyNames())
        {
            using var key = lxss.OpenSubKey(id);
            if (key is null)
            {
                continue;
            }

            entries.Add(new WslRegistryEntry(
                id,
                key.GetValue("DistributionName") as string,
                key.GetValue("BasePath") as string,
                key.GetValue("VhdFileName") as string,
                key.GetValue("Flags") as int?));
        }

        return new WslRegistryData(entries, lxss.GetValue("DefaultDistribution") as string);
    }
}
