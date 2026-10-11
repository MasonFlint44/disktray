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

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsClicked?.Invoke(this, EventArgs.Empty);
}
