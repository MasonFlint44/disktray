using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DiskTray.App;
using DiskTray.Compactor;
using DiskTray.Core.Compact;
using Microsoft.Win32.SafeHandles;

namespace DiskTray.Windows.Tests;

/// <summary>
/// <see cref="PathHold"/>: what the Compactor refuses to compact, and that a held path can't
/// be changed. All on files made here, in a temp folder.
/// </summary>
public partial class PathHoldTests
{
    [Fact]
    public void Holds_a_plain_file_and_every_folder_above_it_until_let_go()
    {
        using var temp = new TestFolder();
        string disk = temp.File(@"wsl\disk\ext4.vhdx");
        string folder = Path.GetDirectoryName(disk)!;

        using (var hold = PathHold.Take(disk, out string? problem, out _))
        {
            Assert.True(hold is not null, problem);
            Assert.ThrowsAny<IOException>(() => File.Move(disk, disk + ".moved"));
            Assert.ThrowsAny<IOException>(() => File.Delete(disk));
            Assert.ThrowsAny<IOException>(() => Directory.Move(folder, folder + "-moved"));
            Assert.ThrowsAny<IOException>(() => Directory.Move(Path.Combine(temp.Path, "wsl"), Path.Combine(temp.Path, "wsl-moved")));
            // Still open to read and write, as compacting needs.
            using var stream = new FileStream(disk, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }

        File.Move(disk, disk + ".moved");
    }

    [Fact]
    public void Refuses_a_file_with_another_name()
    {
        using var temp = new TestFolder();
        string disk = temp.File("ext4.vhdx");
        Assert.True(CreateHardLink(Path.Combine(temp.Path, "other.vhdx"), disk, 0), $"CreateHardLink failed ({Marshal.GetLastPInvokeError()})");

        Assert.Null(PathHold.Take(disk, out string? problem, out _));
        Assert.Contains("hard link", problem);
    }

    [Fact]
    public void Refuses_a_path_through_a_junction()
    {
        using var temp = new TestFolder();
        string disk = temp.File(@"real\ext4.vhdx");
        string junction = Junction.Create(Path.Combine(temp.Path, "moved"), Path.Combine(temp.Path, "real"));

        Assert.Null(PathHold.Take(Path.Combine(junction, "ext4.vhdx"), out string? problem, out _));
        Assert.Contains("is a link", problem);
        using var direct = PathHold.Take(disk, out string? directProblem, out _);
        Assert.True(direct is not null, directProblem);
    }

    [Fact]
    public void Refuses_a_file_that_is_a_link()
    {
        using var temp = new TestFolder();
        string target = temp.File("target.vhdx");
        string link = Path.Combine(temp.Path, "ext4.vhdx");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Admin.SkipForWantOfRights($"Making a file symlink needs admin rights or Developer Mode ({e.Message}).");
        }

        Assert.Null(PathHold.Take(link, out string? problem, out _));
        Assert.Contains("link", problem);
    }

    [Fact]
    public void Refuses_a_short_name()
    {
        using var temp = new TestFolder();
        string disk = temp.File(@"A Long Folder Name\ext4.vhdx");
        string folder = Path.GetDirectoryName(disk)!;
        string shortFolder = ShortPath(folder);
        Assert.SkipWhen(shortFolder == folder, "8.3 names are off on this drive.");

        Assert.Null(PathHold.Take(Path.Combine(shortFolder, "ext4.vhdx"), out string? problem, out _));
        Assert.Contains("isn't where its path says", problem);
    }

    [Fact]
    public void Says_why_when_the_file_isnt_there()
    {
        using var temp = new TestFolder();

        Assert.Null(PathHold.Take(Path.Combine(temp.Path, "ext4.vhdx"), out string? problem, out _));
        Assert.Equal("it wasn't found", problem);
        Assert.Null(PathHold.Take(Path.Combine(temp.Path, "gone", "ext4.vhdx"), out problem, out _));
        Assert.Equal($"{Path.Combine(temp.Path, "gone")} wasn't found", problem);
    }

    [Fact]
    public void Looks_at_a_disk_another_program_has_open_for_itself_but_cant_hold_it()
    {
        using var temp = new TestFolder();
        string disk = temp.File(@"wsl\ext4.vhdx");
        using var wsl = new FileStream(disk, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        using (var look = PathHold.Look(disk, out string? problem))
        {
            Assert.True(look is not null, problem);
        }

        Assert.Null(PathHold.Take(disk, out string? holdProblem, out bool inUse));
        Assert.Equal("it couldn't be opened (error 32)", holdProblem);
        Assert.True(inUse);
    }

    [Fact]
    public void Holds_a_path_longer_than_windows_usual_limit_and_reads_its_size()
    {
        using var temp = new TestFolder();
        string deep = Path.Combine(temp.Path, string.Join('\\', Enumerable.Repeat(new string('d', 60), 5)));
        string disk = Path.Combine(deep, "ext4.vhdx");
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(disk, new byte[1234]);
        Assert.True(disk.Length > 260);

        using var hold = PathHold.Take(disk, out string? problem, out bool inUse);

        Assert.True(hold is not null, problem);
        Assert.False(inUse);
        Assert.Equal(1234, hold.Size);
        Assert.Null(hold.Changed());
    }

    /// <summary>
    /// Anything done to the file while it's held, such as a link put on it and taken off
    /// again while the virtual disk service opened it: an attribute set and cleared stands in.
    /// </summary>
    [Fact]
    public void Sees_a_held_file_changed_and_changed_back()
    {
        using var temp = new TestFolder();
        string disk = temp.File("ext4.vhdx");
        using var hold = PathHold.Take(disk, out string? problem, out _);
        Assert.True(hold is not null, problem);
        Assert.Null(hold.Changed());

        var attributes = File.GetAttributes(disk);
        File.SetAttributes(disk, attributes | FileAttributes.Hidden);
        File.SetAttributes(disk, attributes);

        Assert.Equal("it changed while it was being opened to compact", hold.Changed());
    }

    [Fact]
    public void Refuses_a_path_through_a_drive_letter_made_for_a_folder()
    {
        using var temp = new TestFolder();
        temp.File("ext4.vhdx");
        char letter = "ZYXWVUTSRQPONMLKJIHGFED".First(l => !Directory.Exists($"{l}:\\"));
        string drive = $"{letter}:";
        Assert.True(DefineDosDevice(0, drive, temp.Path), $"DefineDosDevice failed ({Marshal.GetLastPInvokeError()})");
        try
        {
            Assert.Null(PathHold.Take($@"{drive}\ext4.vhdx", out string? problem, out _));
            Assert.Equal("it's a link, or isn't where its path says", problem);
        }
        finally
        {
            DefineDosDevice(RemoveDefinition | ExactMatchOnRemove, drive, temp.Path);
        }
    }

    private const uint RemoveDefinition = 0x2;
    private const uint ExactMatchOnRemove = 0x4;

    [LibraryImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DefineDosDevice(uint flags, string device, string target);

    private static string ShortPath(string path)
    {
        var buffer = new char[1024];
        uint length = GetShortPathName(path, buffer, (uint)buffer.Length);
        return length is > 0 and < 1024 ? new string(buffer, 0, (int)length) : path;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string link, string existing, nint security);

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathName(string longPath, [Out] char[] shortPath, uint length);
}

/// <summary><see cref="UserAccess"/>: only disks the account that asked could change get compacted.</summary>
public class UserAccessTests
{
    private static UserAccess OfThisProcess() =>
        UserAccess.OfProcess((uint)Environment.ProcessId, out string? problem) ?? throw new InvalidOperationException(problem);

    /// <summary>Checks the file as the Compactor does: through a handle on it, not its path.</summary>
    private static bool? CanWrite(UserAccess user, string path)
    {
        using var look = PathHold.Look(path, out string? problem) ?? throw new InvalidOperationException(problem);
        return user.CanWrite(look.File);
    }

    /// <summary>Denies this account writing to <paramref name="path"/> until disposed.</summary>
    internal static IDisposable DenyWriting(string path)
    {
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.WriteData, AccessControlType.Deny);
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        return new Undo(() =>
        {
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        });
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    [Fact]
    public void Says_yes_for_a_file_the_account_can_change()
    {
        using var temp = new TestFolder();
        using var user = OfThisProcess();

        Assert.True(CanWrite(user, temp.File("ext4.vhdx")));
    }

    [Fact]
    public void Says_no_for_a_file_the_account_cant_change()
    {
        using var temp = new TestFolder();
        string disk = temp.File("ext4.vhdx");
        using var denied = DenyWriting(disk);
        using var user = OfThisProcess();

        Assert.False(CanWrite(user, disk));
    }

    [Fact]
    public void Says_yes_for_a_file_the_account_can_change_while_wsl_has_it_open()
    {
        using var temp = new TestFolder();
        string disk = temp.File("ext4.vhdx");
        using var wsl = new FileStream(disk, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var user = OfThisProcess();

        Assert.True(CanWrite(user, disk));
    }

    [Fact]
    public void Refuses_a_file_only_admins_can_change_for_an_admin_running_without_admin_rights()
    {
        // The file is held as the Compactor holds it, with admin rights.
        Admin.SkipUnlessElevated();
        using var temp = new TestFolder();
        string adminOnly = temp.File("admin-only.vhdx");
        string own = temp.File("own.vhdx");
        var info = new FileInfo(adminOnly);
        var original = info.GetAccessControl();
        var locked = new FileSecurity();
        locked.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        locked.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        locked.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(locked);
        try
        {
            using var token = NonAdminToken.Create();
            using var user = UserAccess.OfToken(token, out string? problem) ?? throw new InvalidOperationException(problem);

            Assert.False(CanWrite(user, adminOnly));
            Assert.True(CanWrite(user, own));
        }
        finally
        {
            info.SetAccessControl(original);
        }
    }

    [Fact]
    public void Says_so_when_it_cant_tell()
    {
        using var user = OfThisProcess();
        using var nothing = new SafeFileHandle(0, ownsHandle: false);

        Assert.Null(user.CanWrite(nothing));
    }

    /// <summary>A connected pipe owned by <paramref name="owner"/>, as DiskTray makes it owned by itself.</summary>
    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> PipeOwnedBy(SecurityIdentifier owner)
    {
        string name = "DiskTray-test-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetOwner(owner);
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        var connected = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(TimeSpan.FromSeconds(15));
        await connected;
        return (server, client);
    }

    [Fact]
    public async Task Finds_the_account_of_whatever_made_the_pipe()
    {
        var (server, client) = await PipeOwnedBy(WindowsIdentity.GetCurrent().User!);
        using (server)
        using (client)
        {
            using var temp = new TestFolder();

            using var user = UserAccess.OfPipeServer(client.SafePipeHandle, Environment.ProcessPath!, out string? problem);

            Assert.Null(problem);
            Assert.True(CanWrite(user!, temp.File("ext4.vhdx")));
        }
    }

    /// <summary>
    /// The DiskTray.exe here is the test helper's relay, the stand-in the Compactor exe's tests
    /// use, so this also shows that it passes the check and passes everything through.
    /// </summary>
    [Fact]
    public void Takes_requests_from_the_program_it_should_and_the_stand_in_passes_for_it()
    {
        using var temp = new TestFolder();
        string app = TestHelper.CopyInto(temp.Path, "DiskTray.exe");
        string testPipe = CompactorArgs.NewPipeName(), compactorPipe = CompactorArgs.NewPipeName();
        using var testEnd = new NamedPipeServerStream(testPipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var relay = Process.Start(new ProcessStartInfo(app, ["relay", testPipe, compactorPipe]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            testEnd.WaitForConnection();
            Assert.Equal("ready", relay.StandardOutput.ReadLine());
            using var compactorEnd = PipeHost.Connect(compactorPipe, TimeSpan.FromSeconds(15))!;

            using (var user = UserAccess.OfPipeServer(compactorEnd.SafePipeHandle, app, out string? problem))
            {
                Assert.True(user is not null, problem);
            }

            compactorEnd.Write([1, 2]);
            testEnd.Write([3]);
            Assert.Equal(1, testEnd.ReadByte());
            Assert.Equal(2, testEnd.ReadByte());
            Assert.Equal(3, compactorEnd.ReadByte());

            testEnd.Dispose();
            Assert.Equal(-1, compactorEnd.ReadByte());
            Assert.True(relay.WaitForExit(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            if (!relay.HasExited)
            {
                relay.Kill();
                relay.WaitForExit();
            }
        }
    }

    [Fact]
    public async Task Refuses_a_pipe_made_by_a_program_other_than_the_one_it_takes_requests_from()
    {
        var (server, client) = await PipeOwnedBy(WindowsIdentity.GetCurrent().User!);
        using (server)
        using (client)
        {
            string app = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "DiskTray.exe");

            Assert.Null(UserAccess.OfPipeServer(client.SafePipeHandle, app, out string? problem));
            Assert.Equal($"found the program on the other end of the pipe ({Environment.ProcessPath}) isn't the DiskTray.exe beside it", problem);
        }
    }

    /// <summary>
    /// What a process ID passed on to another process (a service, say) would look like: the
    /// account found isn't the pipe's owner. An admin can give a pipe to Administrators, which
    /// stands in for that here.
    /// </summary>
    [Fact]
    public async Task Refuses_an_account_that_doesnt_own_the_pipe()
    {
        Admin.SkipUnlessElevated();
        var (server, client) = await PipeOwnedBy(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        using (server)
        using (client)
        {
            Assert.Null(UserAccess.OfPipeServer(client.SafePipeHandle, Environment.ProcessPath!, out string? problem));
            Assert.Equal("found the pipe wasn't made by the account on its other end", problem);
        }
    }

    [Fact]
    public void Turns_the_debug_privilege_off_again_after_reading_a_process_only_it_can_open()
    {
        Admin.SkipUnlessElevated();
        using var other = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), ["-n", "30", "127.0.0.1"]) { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            // Permissions that let no one in, so only the debug privilege opens it; and the
            // privilege off to begin with (the runner can start with it on), so it's needed.
            Privileges.LockOut(other);
            Privileges.DebugOff();

            using var user = UserAccess.OfProcess((uint)other.Id, out string? problem);

            Assert.True(user is not null, problem);
            Assert.False(Privileges.DebugIsOn(), "SeDebugPrivilege was left on.");
        }
        finally
        {
            other.Kill();
        }
    }
}

/// <summary>Holding a disk to compact: each refusal comes before anything is compacted, and the held file is what gets measured.</summary>
public class DiskHoldTests
{
    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> ConnectedPipe()
    {
        string name = "DiskTray-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        client.Connect(TimeSpan.FromSeconds(15));
        await connected;
        return (server, client);
    }

    [Fact]
    public async Task Refuses_a_disk_the_account_that_asked_cant_change_before_and_while_holding_it()
    {
        using var temp = new TestFolder();
        string disk = temp.File("ext4.vhdx");
        using var denied = UserAccessTests.DenyWriting(disk);
        var (server, client) = await ConnectedPipe();
        using (server)
        using (var user = UserAccess.OfProcess((uint)Environment.ProcessId, out _))
        using (var host = new PipeHost(client, user, null))
        {
            const string refusal = "the account that asked can't change it, so it isn't DiskTray's to compact";

            Assert.Equal(refusal, host.Check(disk));
            Assert.Equal(new DiskHold(null, refusal), host.Hold(disk, dryRun: false));
            Assert.Equal(new DiskHold(null, refusal), host.Hold(disk, dryRun: true));
        }
    }

    [Fact]
    public async Task Refuses_every_disk_when_it_couldnt_tell_which_account_asked()
    {
        using var temp = new TestFolder();
        string disk = temp.File("ext4.vhdx");
        var (server, client) = await ConnectedPipe();
        using (server)
        using (var host = new PipeHost(client, null, "couldn't read which account asked (error 5)"))
        {
            Assert.Equal("the Compactor couldn't read which account asked (error 5)", host.Check(disk));
            Assert.Equal(new DiskHold(null, "the Compactor couldn't read which account asked (error 5)"), host.Hold(disk, dryRun: false));
        }
    }

    [Fact]
    public async Task Checks_the_path_before_the_account()
    {
        using var temp = new TestFolder();
        var (server, client) = await ConnectedPipe();
        using (server)
        using (var user = UserAccess.OfProcess((uint)Environment.ProcessId, out _))
        using (var host = new PipeHost(client, user, null))
        {
            string missing = Path.Combine(temp.Path, "ext4.vhdx");

            Assert.Equal("it wasn't found", host.Check(missing));
            Assert.Equal(new DiskHold(null, "it wasn't found"), host.Hold(missing, dryRun: false));
        }
    }

    [Fact]
    public async Task Passes_a_disk_the_account_can_change_even_while_wsl_has_it_open()
    {
        using var temp = new TestFolder();
        string disk = temp.File("ext4.vhdx");
        var (server, client) = await ConnectedPipe();
        using (server)
        using (var user = UserAccess.OfProcess((uint)Environment.ProcessId, out _))
        using (var host = new PipeHost(client, user, null))
        using (new FileStream(disk, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(host.Check(disk));

            // Held for compacting, it's in use until WSL lets go; a dry run only looks at it.
            var hold = host.Hold(disk, dryRun: false);
            Assert.Null(hold.Disk);
            Assert.True(hold.InUse);
            using var look = host.Hold(disk, dryRun: true).Disk;
            Assert.NotNull(look);
        }
    }

    [Fact]
    public async Task Measures_the_disk_it_holds()
    {
        using var temp = new TestFolder();
        string disk = Path.Combine(temp.Path, "ext4.vhdx");
        File.WriteAllBytes(disk, new byte[4321]);
        var (server, client) = await ConnectedPipe();
        using (server)
        using (var user = UserAccess.OfProcess((uint)Environment.ProcessId, out _))
        using (var host = new PipeHost(client, user, null))
        {
            using var held = host.Hold(disk, dryRun: false).Disk;

            Assert.Equal(4321, held!.Size);
            Assert.ThrowsAny<IOException>(() => File.Move(disk, disk + ".moved"));
        }
    }
}

/// <summary>The shared in-use check, on Windows, where sharing rules are enforced.</summary>
public class DiskOpenCheckTests
{
    [Theory]
    [InlineData(FileShare.ReadWrite)]
    [InlineData(FileShare.Read)]
    [InlineData(FileShare.None)]
    public void Counts_a_file_open_elsewhere_as_in_use(FileShare share)
    {
        using var temp = new TestFolder();
        string disk = temp.File("ext4.vhdx");
        using (new FileStream(disk, FileMode.Open, FileAccess.ReadWrite, share))
        {
            Assert.True(DiskOpenCheck.InUse(disk, out _));
        }

        Assert.False(DiskOpenCheck.InUse(disk, out _));
    }
}

/// <summary>Compacting a real VHDX, as the Compactor does. Needs admin, as it does.</summary>
public class CompactTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public void Compacts_a_disk_while_its_path_is_held()
    {
        Admin.SkipUnlessElevated();
        using var temp = new TestFolder();
        string disk = Path.Combine(temp.Path, @"wsl\disk\ext4.vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
        TestDisk.Create(disk);
        var progress = new List<int>();

        using var hold = PathHold.Take(disk, out string? problem, out _);
        Assert.Null(problem);

        Assert.Null(VirtualDisk.Compact(disk, hold!, progress.Add));
        Assert.NotEmpty(progress);
    }

    [Fact]
    public void Compacts_a_disk_whose_path_is_longer_than_windows_usual_limit()
    {
        Admin.SkipUnlessElevated();
        using var temp = new TestFolder();
        string deep = Path.Combine(temp.Path, string.Join('\\', Enumerable.Repeat(new string('d', 60), 5)));
        Directory.CreateDirectory(deep);
        string disk = Path.Combine(deep, "ext4.vhdx");
        Assert.True(disk.Length > 260);
        TestDisk.Create(@"\\?\" + disk);

        using var hold = PathHold.Take(disk, out string? problem, out _);
        Assert.True(hold is not null, problem);

        Assert.Null(VirtualDisk.Compact(disk, hold, _ => { }));
    }

    /// <summary>
    /// What Clean up is for: a disk that grew, then had its data cleared inside, gets that
    /// space back on the drive. WSL clears freed space for a VHDX the same way (as zeros or
    /// trimmed blocks), and compacting a detached disk gives back blocks holding nothing.
    /// </summary>
    [Fact]
    public void Gives_back_the_space_a_disk_holds_but_no_longer_uses()
    {
        Admin.SkipUnlessElevated();
        const long diskBytes = 256L << 20;
        const long dataBytes = 96L << 20;
        using var temp = new TestFolder();
        string disk = Path.Combine(temp.Path, "ext4.vhdx");
        TestDisk.Create(disk, diskBytes);

        using (var attached = new AttachedTestDisk(disk, diskBytes))
        {
            // Past the first block, where Windows may keep its own bits; then cleared again.
            attached.Fill(32L << 20, dataBytes, 0xA5);
            attached.Fill(32L << 20, dataBytes, 0);
        }

        long before = new FileInfo(disk).Length;
        Assert.True(before > dataBytes, $"The disk file should have grown with the data, but is {before} bytes.");

        using (var hold = PathHold.Take(disk, out string? problem, out _))
        {
            Assert.True(hold is not null, problem);
            Assert.Null(VirtualDisk.Compact(disk, hold, _ => { }));
        }

        long after = new FileInfo(disk).Length;
        Assert.True(before - after >= dataBytes * 3 / 4, $"Compacting took {before} bytes to {after}.");
    }

    /// <summary>
    /// The Compactor exe these run: the one DISKTRAY_COMPACTOR_EXE names (the GitHub Actions
    /// build points it at the NativeAOT publish that ships, which must then be native), or else
    /// where the build put it, framework-dependent as the tests are.
    /// </summary>
    private static string CompactorExe()
    {
        if (Environment.GetEnvironmentVariable("DISKTRAY_COMPACTOR_EXE") is { Length: > 0 } named)
        {
            Assert.True(File.Exists(named), $"DISKTRAY_COMPACTOR_EXE is {named}, which isn't there.");
            Assert.Null(CompactorLauncher.NotNative(named));
            return named;
        }

        string beside = Path.Combine(AppContext.BaseDirectory, "DiskTray.Compactor.exe");
        if (File.Exists(beside) && File.Exists(Path.ChangeExtension(beside, ".runtimeconfig.json")))
        {
            return beside;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var bin = new DirectoryInfo(Path.Combine(dir.FullName, "src", "DiskTray.Compactor", "bin"));
            if (bin.Exists)
            {
                return bin.EnumerateFiles("DiskTray.Compactor.exe", SearchOption.AllDirectories)
                    .Where(f => File.Exists(Path.ChangeExtension(f.FullName, ".runtimeconfig.json")))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .First().FullName;
            }
        }

        throw new FileNotFoundException("Couldn't find DiskTray.Compactor.exe.");
    }

    /// <summary>Starts the Compactor exe as the app does, but without asking for admin, which these tests already have.</summary>
    private static Process StartCompactor(IEnumerable<string> args) => StartCompactor(CompactorExe(), args);

    private static Process StartCompactor(string exe, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return Process.Start(start)!;
    }

    /// <summary>
    /// A Compactor run as Clean up starts one, from a copy of the Compactor beside a
    /// stand-in for DiskTray.exe (the test helper's relay), since it only takes requests
    /// from the DiskTray.exe beside it. The stand-in opens the Compactor's pipe and passes
    /// everything through to the app's end here, so what the Compactor sends and is sent is
    /// as it would be. Ends whatever is still running, and removes the copies, on dispose.
    /// </summary>
    private sealed class Run : IDisposable
    {
        private readonly TestFolder _folder = new();
        private readonly Process _app;

        public Run(CompactorArgs args)
        {
            string compactor = CopyCompactor(_folder.Path);
            string app = TestHelper.CopyInto(_folder.Path, "DiskTray.exe");
            string compactorPipe = CompactorArgs.NewPipeName();

            Launcher = new CompactorLauncher();
            Launcher.OpenPipe(args.Pipe);
            _app = Process.Start(new ProcessStartInfo(app, ["relay", args.Pipe, compactorPipe]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            Launcher.Listen(_app);
            Assert.Equal("ready", _app.StandardOutput.ReadLine());
            Compactor = StartCompactor(compactor, (args with { Pipe = compactorPipe }).ToArgs());
        }

        public CompactorLauncher Launcher { get; }

        public Process Compactor { get; }

        public void Dispose()
        {
            Launcher.Dispose();
            foreach (var process in new[] { Compactor, _app })
            {
                if (!process.WaitForExit(Timeout))
                {
                    process.Kill();
                    process.WaitForExit(Timeout);
                }

                process.Dispose();
            }

            _folder.Dispose();
        }

        /// <summary>The Compactor's files (just the exe, for the native one) copied into <paramref name="folder"/>.</summary>
        private static string CopyCompactor(string folder)
        {
            string exe = CompactorExe();
            var from = new DirectoryInfo(Path.GetDirectoryName(exe)!);
            foreach (var file in from.EnumerateFiles("DiskTray.Compactor.*").Concat(from.EnumerateFiles("DiskTray.Core.*")))
            {
                file.CopyTo(Path.Combine(folder, file.Name));
            }

            return Path.Combine(folder, Path.GetFileName(exe));
        }
    }

    private static string TestDiskIn(TestFolder temp, string relative)
    {
        string disk = Path.Combine(temp.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
        TestDisk.Create(disk);
        return disk;
    }

    [Fact]
    [Trait("Compactor", "exe")]
    public void The_compactor_compacts_what_the_app_asks_once_told_to_go()
    {
        Admin.SkipUnlessElevated();
        using var temp = new TestFolder();
        string one = TestDiskIn(temp, @"docker\disk\docker_data.vhdx");
        string two = TestDiskIn(temp, @"Ubuntu\ext4.vhdx");

        using var run = new Run(new CompactorArgs(CompactorArgs.NewPipeName(), [one, two]));
        Statuses.WaitFor(run.Launcher, s => s.State == CompactState.Waiting, Timeout);
        run.Launcher.Signal(CompactorMessages.Go);
        var final = Statuses.WaitFor(run.Launcher, s => s.Finished, Timeout);

        Assert.True(final.State == CompactState.Done, final.Message);
        Assert.Equal([one, two], final.Results!.Select(r => r.Path));
        Assert.All(final.Results!, r => Assert.Null(r.Problem));
        Assert.True(run.Compactor.WaitForExit(Timeout));
        Assert.Equal(0, run.Compactor.ExitCode);
    }

    [Fact]
    [Trait("Compactor", "exe")]
    public void The_compactor_changes_nothing_on_a_dry_run()
    {
        Admin.SkipUnlessElevated();
        using var temp = new TestFolder();
        string disk = TestDiskIn(temp, @"Ubuntu\ext4.vhdx");
        var written = File.GetLastWriteTimeUtc(disk);
        long size = new FileInfo(disk).Length;

        using var run = new Run(new CompactorArgs(CompactorArgs.NewPipeName(), [disk], DryRun: true));
        Statuses.WaitFor(run.Launcher, s => s.State == CompactState.Waiting, Timeout);
        run.Launcher.Signal(CompactorMessages.Go);
        var final = Statuses.WaitFor(run.Launcher, s => s.Finished, Timeout);

        Assert.Equal(CompactState.Done, final.State);
        Assert.True(final.DryRun);
        Assert.Equal(new DiskResult(disk, size, size), Assert.Single(final.Results!));
        Assert.True(run.Compactor.WaitForExit(Timeout));
        Assert.Equal(0, run.Compactor.ExitCode);
        Assert.Equal(written, File.GetLastWriteTimeUtc(disk));
    }

    [Fact]
    [Trait("Compactor", "exe")]
    public void The_compactor_refuses_before_it_says_its_ready_when_it_would_refuse_every_disk()
    {
        Admin.SkipUnlessElevated();
        using var temp = new TestFolder();
        string disk = TestDiskIn(temp, @"Ubuntu\ext4.vhdx");
        using var denied = UserAccessTests.DenyWriting(disk);

        using var run = new Run(new CompactorArgs(CompactorArgs.NewPipeName(), [disk]));
        var first = Statuses.WaitFor(run.Launcher, _ => true, Timeout);

        // Its first word, so the app never stops Docker or WSL for it.
        Assert.Equal(CompactState.Failed, first.State);
        Assert.StartsWith("Nothing was shut down or compacted.", first.Message);
        Assert.Contains("the account that asked can't change it", first.Message);
        Assert.True(run.Compactor.WaitForExit(Timeout));
        Assert.Equal(3, run.Compactor.ExitCode);
    }

    /// <summary>
    /// Started by something other than the DiskTray.exe beside it (this test, with the app's
    /// own pipe code), it refuses every disk before it says it's ready.
    /// </summary>
    [Fact]
    [Trait("Compactor", "exe")]
    public void The_compactor_refuses_every_disk_for_a_program_other_than_the_disktray_beside_it()
    {
        Admin.SkipUnlessElevated();
        using var temp = new TestFolder();
        string disk = TestDiskIn(temp, @"Ubuntu\ext4.vhdx");
        var args = new CompactorArgs(CompactorArgs.NewPipeName(), [disk]);

        using var launcher = new CompactorLauncher();
        launcher.OpenPipe(args.Pipe);
        using var compactor = StartCompactor(args.ToArgs());
        launcher.Listen(compactor);
        var first = Statuses.WaitFor(launcher, _ => true, Timeout);

        Assert.Equal(CompactState.Failed, first.State);
        Assert.StartsWith("Nothing was shut down or compacted.", first.Message);
        Assert.Contains($"found the program on the other end of the pipe ({Environment.ProcessPath}) isn't the DiskTray.exe beside it", first.Message);
        Assert.True(compactor.WaitForExit(Timeout));
        Assert.Equal(3, compactor.ExitCode);
    }

    [Fact]
    [Trait("Compactor", "exe")]
    public void The_compactor_stops_without_compacting_when_the_app_goes_away()
    {
        Admin.SkipUnlessElevated();
        using var temp = new TestFolder();
        string disk = TestDiskIn(temp, @"Ubuntu\ext4.vhdx");
        var written = File.GetLastWriteTimeUtc(disk);

        using var run = new Run(new CompactorArgs(CompactorArgs.NewPipeName(), [disk]));
        Statuses.WaitFor(run.Launcher, s => s.State == CompactState.Waiting, Timeout);
        run.Launcher.Dispose();

        Assert.True(run.Compactor.WaitForExit(Timeout));
        Assert.Equal(2, run.Compactor.ExitCode);
        Assert.Equal(written, File.GetLastWriteTimeUtc(disk));
    }

    /// <summary>Once told to go, it finishes even if the app exits straight away, as it may once WSL is down.</summary>
    [Fact]
    [Trait("Compactor", "exe")]
    public void The_compactor_carries_on_when_the_app_goes_away_after_saying_go()
    {
        Admin.SkipUnlessElevated();
        using var temp = new TestFolder();
        string disk = TestDiskIn(temp, @"Ubuntu\ext4.vhdx");

        using var run = new Run(new CompactorArgs(CompactorArgs.NewPipeName(), [disk]));
        Statuses.WaitFor(run.Launcher, s => s.State == CompactState.Waiting, Timeout);
        run.Launcher.Signal(CompactorMessages.Go);
        run.Launcher.Dispose();

        Assert.True(run.Compactor.WaitForExit(Timeout));
        Assert.Equal(0, run.Compactor.ExitCode);
    }

    [Fact]
    [Trait("Compactor", "exe")]
    public void The_compactor_quits_on_arguments_it_doesnt_take()
    {
        Admin.SkipUnlessElevated();

        using var compactor = StartCompactor(["--pipe", CompactorArgs.NewPipeName(), "--disk", @"C:\Windows\..\x.vhdx"]);

        Assert.True(compactor.WaitForExit(Timeout));
        Assert.Equal(64, compactor.ExitCode);
    }

    [Fact]
    [Trait("Compactor", "exe")]
    public void The_compactor_quits_when_there_is_no_app_to_talk_to()
    {
        Admin.SkipUnlessElevated();
        using var temp = new TestFolder();

        using var compactor = StartCompactor(["--pipe", CompactorArgs.NewPipeName(), "--disk", temp.File("ext4.vhdx")]);

        Assert.True(compactor.WaitForExit(Timeout));
        Assert.Equal(65, compactor.ExitCode);
    }
}

/// <summary><see cref="ProtectedFolder"/>: the app won't start a Compactor that could have been swapped.</summary>
public class ProtectedFolderTests
{
    /// <summary>
    /// A folder like the installer's: only admins and Windows can change it, under one only
    /// they can rename. Removed on dispose. Needs admin to make.
    /// </summary>
    private sealed class AdminOnlyFolder : IDisposable
    {
        public AdminOnlyFolder()
        {
            Admin.SkipUnlessElevated();
            var folder = Directory.CreateDirectory(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DiskTray.Tests-" + Guid.NewGuid().ToString("N")));
            Path = folder.FullName;
            var locked = new DirectorySecurity();
            locked.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            locked.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            locked.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            locked.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            // As in Program Files: being the owner gives no extra rights.
            locked.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            folder.SetAccessControl(locked);
            Exe = System.IO.Path.Combine(Path, "DiskTray.Compactor.exe");
            File.WriteAllText(Exe, "not really an exe");
            File.WriteAllText(System.IO.Path.Combine(Path, "DiskTray.dll"), "nor a dll");
        }

        public string Path { get; }

        public string Exe { get; }

        /// <summary>The folder's problem for the Compactor at <paramref name="exe"/>, checked as an admin's everyday token, as DiskTray runs.</summary>
        public static string? ProblemAsEveryday(string exe)
        {
            using var token = NonAdminToken.Create();
            return WindowsIdentity.RunImpersonated(token, () => ProtectedFolder.FolderProblem(exe));
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public void Refuses_a_compactor_in_a_folder_anyone_can_change()
    {
        using var temp = new TestFolder();
        string exe = temp.File("DiskTray.Compactor.exe");

        Assert.Contains("can be changed without admin permission", ProtectedFolder.FolderProblem(exe));
    }

    [Fact]
    public void Lets_through_a_compactor_only_admins_can_change()
    {
        using var folder = new AdminOnlyFolder();

        string? problem = AdminOnlyFolder.ProblemAsEveryday(folder.Exe);

        Assert.True(problem is null, TestLog.Explain(problem));
    }

    /// <summary>
    /// A junction the user made, pointing at the protected folder: every permission read
    /// through it is the folder's, but the user could point it elsewhere while the admin
    /// prompt is up.
    /// </summary>
    [Fact]
    public void Refuses_a_compactor_reached_through_a_junction_even_to_a_protected_folder()
    {
        using var folder = new AdminOnlyFolder();
        using var temp = new TestFolder();
        string junction = Junction.Create(Path.Combine(temp.Path, "DiskTray"), folder.Path);

        Assert.Contains("goes through a link", AdminOnlyFolder.ProblemAsEveryday(Path.Combine(junction, "DiskTray.Compactor.exe")));
    }

    [Fact]
    public void Refuses_a_compactor_reached_through_a_junction()
    {
        using var temp = new TestFolder();
        string exe = temp.File(@"real\DiskTray.Compactor.exe");
        string junction = Junction.Create(Path.Combine(temp.Path, "linked"), Path.GetDirectoryName(exe)!);

        Assert.Contains("goes through a link", ProtectedFolder.FolderProblem(Path.Combine(junction, "DiskTray.Compactor.exe")));
    }

    [Fact]
    public void Refuses_a_link_in_the_compactors_folder()
    {
        using var folder = new AdminOnlyFolder();
        using var temp = new TestFolder();
        string link = Junction.Create(Path.Combine(folder.Path, "runtimes"), temp.Path);
        try
        {
            Assert.Contains("there's a link in the Compactor's folder", AdminOnlyFolder.ProblemAsEveryday(folder.Exe));
        }
        finally
        {
            // The junction itself, before what it points to goes.
            Directory.Delete(link);
        }
    }

    /// <summary>Write access given to some other everyday account, which opening the file as this one can't show.</summary>
    [Fact]
    public void Refuses_a_compactor_another_account_could_change()
    {
        using var folder = new AdminOnlyFolder();
        var exe = new FileInfo(folder.Exe);
        var security = exe.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-21-1-2-3-1001"), FileSystemRights.Modify, AccessControlType.Allow));
        exe.SetAccessControl(security);

        Assert.Contains("can be changed without admin permission", AdminOnlyFolder.ProblemAsEveryday(folder.Exe));
    }

    [Fact]
    public void Knows_trustedinstaller_by_the_id_windows_gives_it()
    {
        var named = new NTAccount(@"NT SERVICE\TrustedInstaller").Translate(typeof(SecurityIdentifier));

        Assert.Equal(ProtectedFolder.TrustedInstaller, named.Value);
    }

    [Theory]
    [InlineData(null, true, "DiskTray couldn't check whether it's running as admin")]
    [InlineData(true, true, "DiskTray is running as admin.")]
    [InlineData(true, false, null)]
    public void Goes_by_how_diskTray_itself_runs(bool? elevated, bool uacOn, string? starts)
    {
        string? problem = ProtectedFolder.Problem(@"C:
owhere\DiskTray.Compactor.exe", () => elevated, () => uacOn);

        if (starts is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.StartsWith(starts, problem);
        }
    }

    [Fact]
    public void Checks_the_folder_when_diskTray_runs_without_admin_rights()
    {
        using var temp = new TestFolder();
        string exe = temp.File("DiskTray.Compactor.exe");

        Assert.Contains("can be changed without admin permission", ProtectedFolder.Problem(exe, () => false, () => true));
    }

    [Fact]
    public void Tells_a_development_compactor_from_the_native_one()
    {
        using var temp = new TestFolder();
        // The .NET build the tests are built with sits beside them; a copy of its exe alone is
        // what an upgrade could leave behind.
        string dotnet = Path.Combine(AppContext.BaseDirectory, "DiskTray.Compactor.exe");
        string alone = Path.Combine(temp.Path, "DiskTray.Compactor.exe");
        File.Copy(dotnet, alone);
        string text = temp.File(@"text\DiskTray.Compactor.exe");

        Assert.Contains("isn't the native build", CompactorLauncher.NotNative(dotnet));
        Assert.Contains("isn't the native build", CompactorLauncher.NotNative(alone));
        Assert.Contains("isn't the native build", CompactorLauncher.NotNative(text));
    }

    /// <summary>
    /// The Compactor the MSI installed, on the GitHub Actions runner, which sets
    /// DISKTRAY_INSTALLED_COMPACTOR to it: what DiskTray checks before running it as admin.
    /// </summary>
    [Fact]
    [Trait("Compactor", "installed")]
    public void Lets_through_the_compactor_the_installer_put_in_place()
    {
        string? exe = Environment.GetEnvironmentVariable("DISKTRAY_INSTALLED_COMPACTOR");
        Assert.SkipWhen(string.IsNullOrEmpty(exe), "Runs once the MSI is installed (DISKTRAY_INSTALLED_COMPACTOR).");
        Admin.SkipUnlessElevated();

        Assert.Null(CompactorLauncher.NotNative(exe!));
        string? problem = AdminOnlyFolder.ProblemAsEveryday(exe!);
        Assert.True(problem is null, TestLog.Explain(problem));
    }
}
