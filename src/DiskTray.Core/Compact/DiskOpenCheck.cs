namespace DiskTray.Core.Compact;

/// <summary>
/// Whether a disk file is open, which for a WSL or Docker disk means WSL is using it. The
/// app's idle check and the Compactor both use this, so they read the same errors the same
/// way.
/// </summary>
public static class DiskOpenCheck
{
    private const int Win32Facility = unchecked((int)0x8007_0000);
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>
    /// Tries opening the file for itself alone, then closes it. True if something else has it
    /// open, false if it's free or not there, null (with why) if that can't be told.
    /// </summary>
    public static bool? InUse(string path, out string? problem)
    {
        problem = null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException e) when (IsSharingError(e.HResult))
        {
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = e.Message;
            return null;
        }
    }

    /// <summary>A Windows sharing or lock violation: another handle on the file doesn't allow this one.</summary>
    public static bool IsSharingError(int hresult) =>
        (hresult & unchecked((int)0xFFFF_0000)) == Win32Facility && (hresult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;
}
