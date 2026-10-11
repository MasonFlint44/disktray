using DiskTray.Core.Install;

namespace DiskTray.Core.Tests;

public class AppInstallTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Programs\DiskTray\DiskTray.exe";

    [Fact]
    public void Knows_the_installed_copy_from_a_build_elsewhere()
    {
        Assert.True(AppInstall.IsInstalledCopy(Exe, Machine.LocalAppData));
        Assert.True(AppInstall.IsInstalledCopy(Exe.ToLowerInvariant(), Machine.LocalAppData));
        Assert.False(AppInstall.IsInstalledCopy(@"C:\code\disktray\bin\Release\DiskTray.exe", Machine.LocalAppData));
        Assert.False(AppInstall.IsInstalledCopy(@"C:\Users\me\AppData\Local\Programs\DiskTray\DiskTray.Compactor.exe", Machine.LocalAppData));
    }

    [Fact]
    public void Lists_itself_with_an_uninstall_command()
    {
        var entry = AppInstall.Entry(Exe, "0.1.0", 150L << 20, new DateOnly(2026, 10, 11));

        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\DiskTray", entry.InstallLocation);
        Assert.Equal($"\"{Exe}\" --uninstall", entry.UninstallString);
        Assert.Equal($"\"{Exe}\" --uninstall --quiet", entry.QuietUninstallString);
        Assert.Equal($"\"{Exe}\",0", entry.DisplayIcon);
        Assert.Equal(150 * 1024, entry.EstimatedSizeKiB);
        Assert.Equal("20261011", entry.InstallDate);
        Assert.Equal(1, entry.Values["NoModify"]);
    }

    [Fact]
    public void Keeps_data_and_the_app_in_separate_folders()
    {
        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\DiskTray", AppInstall.Folder(Machine.LocalAppData));
        Assert.Equal(@"C:\Users\me\AppData\Local\DiskTray", AppInstall.DataFolder(Machine.LocalAppData));
    }

    [Fact]
    public void Deletes_the_folder_once_disktray_has_exited() =>
        Assert.Equal(
            ["/d", "/c", "ping -n 4 127.0.0.1 >nul & rmdir /s /q \"C:\\Users\\me\\AppData\\Local\\Programs\\DiskTray\""],
            AppInstall.RemoveFolderCommand(AppInstall.Folder(Machine.LocalAppData)));

    [Theory]
    [InlineData(@"C:\Users\me")]
    [InlineData(@"C:\")]
    [InlineData("C:\\Programs\\DiskTray\" & del C:\\x & \"\\DiskTray")]
    public void Wont_delete_any_other_folder(string folder) =>
        Assert.Throws<ArgumentException>(() => AppInstall.RemoveFolderCommand(folder));
}
