using System.Windows.Controls;

namespace DiskTray.App;

/// <summary>The flyout's content, bound to a <see cref="Core.Flyout.FlyoutModel"/>. Kept apart from the window so previews can render it.</summary>
public partial class FlyoutView : UserControl
{
    public FlyoutView()
    {
        InitializeComponent();
    }
}
