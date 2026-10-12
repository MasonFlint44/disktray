using System.Buffers.Binary;

namespace DiskTray.Core.Startup;

/// <summary>
/// Reads and writes the switches in Task Manager's Startup apps, which Windows keeps under
/// <c>Explorer\StartupApproved\Run</c> as one binary value per Run entry: a 4-byte flag,
/// then (when switched off) the time it was switched off.
/// </summary>
public static class StartupApproval
{
    /// <summary>
    /// True when the entry was switched off. The first byte is odd when it's off
    /// (02 on, 03 off on most PCs, 06 and 07 on some). No value means it's never been
    /// changed, which counts as on.
    /// </summary>
    public static bool IsDisabled(byte[]? value) => value is [var first, ..] && (first & 1) == 1;

    /// <summary>The value Task Manager writes when it switches an entry on.</summary>
    public static byte[] Enabled() => [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>The value Task Manager writes when it switches an entry off at <paramref name="at"/>.</summary>
    public static byte[] Disabled(DateTimeOffset at)
    {
        var value = new byte[12];
        value[0] = 3;
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(4), at.ToFileTime());
        return value;
    }
}

/// <summary>Where the Run entry should point.</summary>
public static class StartupEntry
{
    /// <summary>
    /// Whether the Run entry belongs to another copy of DiskTray and should follow this one:
    /// it's there (so starting with Windows was set up) but names a different exe, as after
    /// moving from a development copy to the installed one. Its on/off switch is kept.
    /// </summary>
    public static bool ShouldFollow(string? current, string command) =>
        !string.IsNullOrWhiteSpace(current) && !string.Equals(current, command, StringComparison.OrdinalIgnoreCase);
}
