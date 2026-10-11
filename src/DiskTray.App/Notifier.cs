using System.Windows;
using DiskTray.Core.Alerts;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DiskTray.App;

/// <summary>
/// Shows low-space notifications as Windows toasts, which also respect Do Not Disturb.
/// Each one replaces the last, so the notification center holds at most one.
/// </summary>
internal static class Notifier
{
    private const string Tag = "low-space";

    /// <summary>Raised on the UI thread when the user clicks a notification.</summary>
    public static event EventHandler? Clicked;

    /// <summary>
    /// Listens for clicks. Called once at startup, since a click on an old notification can
    /// also start the app.
    /// </summary>
    public static void Start() =>
        ToastNotificationManagerCompat.OnActivated += _ =>
            Application.Current?.Dispatcher.BeginInvoke(() => Clicked?.Invoke(null, EventArgs.Empty));

    public static void Show(AlertToast toast)
    {
        try
        {
            var builder = new ToastContentBuilder().AddArgument("action", "open").AddText(toast.Title);
            foreach (string line in toast.Body.Split('\n'))
            {
                builder.AddText(line);
            }

            builder.Show(t => t.Tag = Tag);
            Log.Info($"Notified: {toast.Title}");
        }
        catch (Exception e)
        {
            // Notifications can be turned off or unavailable; the tray still shows the level.
            Log.Error("Couldn't show a notification", e);
        }
    }
}
