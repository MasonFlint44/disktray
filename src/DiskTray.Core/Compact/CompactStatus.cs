using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskTray.Core.Compact;

public enum CompactState
{
    /// <summary>Started with admin rights, waiting for the app to say WSL is shut down.</summary>
    Waiting,

    Compacting,

    Done,

    /// <summary>Stopped part way. <see cref="CompactStatus.Message"/> says why.</summary>
    Failed,

    /// <summary>Stopped before compacting anything, because the app asked or went away.</summary>
    Cancelled,
}

/// <summary>How one disk went.</summary>
/// <param name="Problem">Why it couldn't be compacted, or null when it was.</param>
public sealed record DiskResult(string Path, long? BeforeBytes, long? AfterBytes, string? Problem = null)
{
    [JsonIgnore]
    public long GainBytes => BeforeBytes is { } before && AfterBytes is { } after ? Math.Max(0, before - after) : 0;
}

/// <summary>What the Compactor is doing. It sends this to the app over the pipe each time it changes.</summary>
/// <param name="Disk">The index of the disk being compacted.</param>
/// <param name="Percent">How far through that disk it is.</param>
public sealed record CompactStatus(
    CompactState State,
    int Disk = 0,
    int Percent = 0,
    string? Message = null,
    IReadOnlyList<DiskResult>? Results = null,
    bool DryRun = false)
{
    [JsonIgnore]
    public bool Finished => State is CompactState.Done or CompactState.Failed or CompactState.Cancelled;

    public string ToJson() => JsonSerializer.Serialize(this, CompactJson.Default.CompactStatus);

    /// <summary>Reads a status, or null if it isn't one.</summary>
    public static CompactStatus? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, CompactJson.Default.CompactStatus);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Writes and reads <see cref="CompactStatus"/> with generated code rather than reflection,
/// since the Compactor is compiled ahead of time.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, Converters = [typeof(CompactStateConverter)])]
[JsonSerializable(typeof(CompactStatus))]
internal sealed partial class CompactJson : JsonSerializerContext;

/// <summary>States as "waiting", "compacting" and so on.</summary>
internal sealed class CompactStateConverter() : JsonStringEnumConverter<CompactState>(JsonNamingPolicy.CamelCase);

/// <summary>What the app tells the Compactor.</summary>
public enum AppSignal
{
    /// <summary>Nothing yet.</summary>
    None,

    /// <summary>WSL is shut down; compact now.</summary>
    Go,

    /// <summary>Stop without compacting.</summary>
    Cancel,

    /// <summary>The app closed the pipe or went away.</summary>
    Gone,
}

/// <summary>
/// The lines sent over the pipe. The Compactor sends "status {json}" and "log {text}"; the
/// app sends "go" or "cancel".
/// </summary>
public static class CompactorMessages
{
    public const string Go = "go";
    public const string Cancel = "cancel";

    public static string Status(CompactStatus status) => "status " + status.ToJson();

    public static string Log(string text) => "log " + text.Replace("\r", " ").Replace("\n", " ");

    /// <summary>Reads a line from the Compactor: a status, or a line for the app's log.</summary>
    public static (CompactStatus? Status, string? Log) Parse(string line) =>
        line.StartsWith("status ", StringComparison.Ordinal) ? (CompactStatus.FromJson(line["status ".Length..]), null)
        : line.StartsWith("log ", StringComparison.Ordinal) ? (null, line["log ".Length..])
        : (null, null);

    /// <summary>Reads a line from the app.</summary>
    public static AppSignal ParseSignal(string? line) => line?.Trim() switch
    {
        null => AppSignal.Gone,
        Go => AppSignal.Go,
        Cancel => AppSignal.Cancel,
        _ => AppSignal.None,
    };

    /// <summary>
    /// The first thing the app says is what counts: "go" followed straight away by the app
    /// going (it exited once WSL was down) is still "go".
    /// </summary>
    public static AppSignal Latch(AppSignal current, AppSignal next) => current == AppSignal.None ? next : current;
}
