namespace DiskTray.Core.Compact;

/// <summary>
/// Whether a disk file is open, which for a WSL or Docker disk means WSL is using it. The
/// app's idle check uses this; the Compactor reads the same errors through
/// <see cref="WindowsError.IsSharing"/>.
/// </summary>
public static class DiskOpenCheck
{
    private const int Win32Facility = unchecked((int)0x8007_0000);

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

    /// <summary>A Windows sharing or lock violation, as an exception's HRESULT: another handle on the file doesn't allow this one.</summary>
    public static bool IsSharingError(int hresult) =>
        (hresult & unchecked((int)0xFFFF_0000)) == Win32Facility && WindowsError.IsSharing(hresult & 0xFFFF);
}
