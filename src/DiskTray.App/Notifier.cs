using System.Windows;
using DiskTray.Core.Alerts;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DiskTray.App;

/// <summary>
/// Shows notifications as Windows toasts, which also respect Do Not Disturb. Each kind
/// (low space, clean up) replaces its own last one, so the notification center holds at
/// most one of each.
/// </summary>
internal static class Notifier
{
    private const string LowSpaceTag = "low-space";
    private const string CleanupTag = "cleanup";

    // A toast shows the title and at most two more lines.
    private const int MaxLines = 2;

    /// <summary>Raised on the UI thread when the user clicks a notification.</summary>
    public static event EventHandler? Clicked;

    /// <summary>
    /// Listens for clicks. Called once at startup, since a click on an old notification can
    /// also start the app.
    /// </summary>
    public static void Start() =>
        ToastNotificationManagerCompat.OnActivated += _ =>
            Application.Current?.Dispatcher.BeginInvoke(() => Clicked?.Invoke(null, EventArgs.Empty));

    /// <param name="image">A picture of the drive to show under the text, if there is one.</param>
    public static void Show(AlertToast toast, string? image) => Show(toast.Title, toast.Body, image, LowSpaceTag);

    /// <summary>How a clean up is going, or how it went.</summary>
    public static void ShowCleanup(string title, string body) => Show(title, body, null, CleanupTag);

    private static void Show(string title, string body, string? image, string tag)
    {
        try
        {
            var builder = new ToastContentBuilder().AddArgument("action", "open").AddText(title);
            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines.Take(MaxLines - 1).Append(string.Join(" ", lines.Skip(MaxLines - 1))).Where(l => l.Length > 0))
            {
                builder.AddText(line);
            }

            if (image is not null)
            {
                builder.AddInlineImage(new Uri(image), "Drive breakdown");
            }

            builder.Show(t => t.Tag = tag);
            Log.Info($"Notified: {title}");
        }
        catch (Exception e)
        {
            // Notifications can be turned off or unavailable; the tray still shows the level.
            Log.Error("Couldn't show a notification", e);
        }
    }
}
