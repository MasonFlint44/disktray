using System.Windows;
using System.Windows.Controls;

namespace DiskTray.App;

/// <summary>The flyout's content, bound to a <see cref="Core.Flyout.FlyoutModel"/>. Kept apart from the window so previews can render it.</summary>
public partial class FlyoutView : UserControl
{
    public FlyoutView()
    {
        InitializeComponent();
    }

    public event EventHandler? SettingsClicked;

    public event EventHandler? CleanupClicked;

    public event EventHandler? CancelCleanupClicked;

    /// <summary>Raised with a large file's path when its pin button is clicked.</summary>
    public event EventHandler<string>? PinClicked;

    /// <summary>Shows how a clean up is going, or hides it when <paramref name="status"/> is null.</summary>
    public void ShowCleanup(string? status, bool canCancel)
    {
        CleanupStatus.Visibility = status is null ? Visibility.Collapsed : Visibility.Visible;
        CleanupText.Text = status;
        CancelCleanup.Visibility = canCancel ? Visibility.Visible : Visibility.Collapsed;
        CleanupButton.IsEnabled = status is null;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsClicked?.Invoke(this, EventArgs.Empty);

    private void OnCleanupClick(object sender, RoutedEventArgs e) => CleanupClicked?.Invoke(this, EventArgs.Empty);

    private void OnCancelCleanupClick(object sender, RoutedEventArgs e) => CancelCleanupClicked?.Invoke(this, EventArgs.Empty);

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
        {
            PinClicked?.Invoke(this, path);
        }
    }
}
