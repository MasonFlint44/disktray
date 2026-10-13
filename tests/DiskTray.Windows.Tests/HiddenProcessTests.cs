using System.Diagnostics;
using System.IO;
using DiskTray.App;

namespace DiskTray.Windows.Tests;

/// <summary><see cref="HiddenProcess"/>: what DiskTray runs wsl.exe and docker.exe through.</summary>
public class HiddenProcessTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "ping.exe");

    [Fact]
    public void Gives_what_a_program_prints()
    {
        var (output, failure, exitCode) = HiddenProcess.RunToEnd(Cmd, ["/c", "echo", "hello"], TimeSpan.FromSeconds(30));

        Assert.Null(failure);
        Assert.Equal(0, exitCode);
        Assert.Equal("hello", output?.Trim());
    }

    [Fact]
    public void Says_how_a_program_that_failed_ended()
    {
        var (output, failure) = HiddenProcess.Run(Cmd, ["/c", "exit", "3"], TimeSpan.FromSeconds(30));

        Assert.Null(output);
        Assert.StartsWith("exited with 3", failure);
    }

    [Fact]
    public void Ends_a_program_that_runs_past_its_timeout()
    {
        var clock = Stopwatch.StartNew();

        var (output, failure, exitCode) = HiddenProcess.RunToEnd(Ping, ["-n", "30", "127.0.0.1"], TimeSpan.FromSeconds(1));

        Assert.Null(output);
        Assert.Null(exitCode);
        Assert.Equal("timed out after 1 s", failure);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public void Leaves_what_a_program_started_running_when_asked_to()
    {
        using var linger = new Linger();

        // cmd waits on what it started, as docker desktop start might on Docker Desktop.
        var (_, failure, _) = HiddenProcess.RunToEnd(Cmd, ["/c", linger.Exe, "linger", "30"], TimeSpan.FromSeconds(2), killTree: false);

        Assert.Equal("timed out after 2 s", failure);
        Assert.NotEmpty(linger.Running());
    }

    /// <summary>
    /// The test helper's linger, under a name of its own, so what a test leaves running
    /// (started by what it ran, so the test never has it) can be found, and ended, without
    /// touching anything else. Ended and removed on dispose.
    /// </summary>
    private sealed class Linger : IDisposable
    {
        private readonly TestFolder _folder = new();
        private readonly string _name = "linger-" + Guid.NewGuid().ToString("N");

        public Linger() => Exe = TestHelper.CopyInto(_folder.Path, _name + ".exe");

        public string Exe { get; }

        public Process[] Running() => Process.GetProcessesByName(_name);

        public void Dispose()
        {
            foreach (var process in Running())
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Ended on its own just now.
                }

                process.Dispose();
            }

            _folder.Dispose();
        }
    }

    /// <summary>
    /// A program that has exited while something it started still holds its output open: the
    /// wait for the output counts against the same timeout, rather than lasting as long as that does.
    /// </summary>
    [Fact]
    public void Doesnt_wait_past_the_timeout_for_output_something_it_started_keeps_open()
    {
        using var linger = new Linger();
        var clock = Stopwatch.StartNew();

        var (_, failure, _) = HiddenProcess.RunToEnd(Cmd, ["/c", "start", "/b", linger.Exe, "linger", "8"], TimeSpan.FromSeconds(2));

        Assert.Equal("timed out after 2 s", failure);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public void Says_why_a_program_didnt_start()
    {
        var (output, failure) = HiddenProcess.Run(Path.Combine(Environment.SystemDirectory, "no-such-program.exe"), [], TimeSpan.FromSeconds(5));

        Assert.Null(output);
        Assert.NotNull(failure);
    }
}
