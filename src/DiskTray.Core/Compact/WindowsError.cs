namespace DiskTray.Core.Compact;

/// <summary>The Windows error codes the file and account checks tell apart, in one place.</summary>
public static class WindowsError
{
    public const int FileNotFound = 2;
    public const int PathNotFound = 3;
    public const int AccessDenied = 5;

    /// <summary>Another handle on the file doesn't share what this one asks for.</summary>
    public const int SharingViolation = 32;

    /// <summary>Another handle has part of the file locked.</summary>
    public const int LockViolation = 33;

    /// <summary>A sharing or lock violation: something else has the file open. Windows only gets that far once access was allowed.</summary>
    public static bool IsSharing(int error) => error is SharingViolation or LockViolation;
}
