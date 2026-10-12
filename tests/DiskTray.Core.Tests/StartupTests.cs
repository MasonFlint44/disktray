using DiskTray.Core.Startup;

namespace DiskTray.Core.Tests;

public class StartupApprovalTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(new byte[0], false)]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 3, 0, 0, 0, 0x10, 0x2C, 0x5E, 0x8B, 0x2A, 0x3B, 0xDB, 0x01 }, true)]
    [InlineData(new byte[] { 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    public void Reads_the_task_manager_switch(byte[]? value, bool disabled) =>
        Assert.Equal(disabled, StartupApproval.IsDisabled(value));

    [Fact]
    public void Writes_values_task_manager_reads_back()
    {
        var at = new DateTimeOffset(2026, 10, 11, 18, 30, 0, TimeSpan.Zero);
        byte[] disabled = StartupApproval.Disabled(at);

        Assert.False(StartupApproval.IsDisabled(StartupApproval.Enabled()));
        Assert.True(StartupApproval.IsDisabled(disabled));
        Assert.Equal(12, disabled.Length);
        Assert.Equal(at, DateTimeOffset.FromFileTime(BitConverter.ToInt64(disabled, 4)));
    }
}

public class StartupEntryTests
{
    private const string Installed = "\"C:\\Program Files\\DiskTray\\DiskTray.exe\"";

    [Theory]
    [InlineData("\"C:\\Users\\me\\AppData\\Local\\Programs\\DiskTray\\DiskTray.exe\"", true)]
    [InlineData("\"c:\\program files\\disktray\\disktray.exe\"", false)]
    [InlineData(Installed, false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Follows_this_copy_only_when_the_entry_names_another(string? current, bool follow) =>
        Assert.Equal(follow, StartupEntry.ShouldFollow(current, Installed));
}
