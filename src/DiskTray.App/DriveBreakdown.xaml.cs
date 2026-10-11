using System.Windows.Controls;

namespace DiskTray.App;

/// <summary>The drive's donut with free space in the middle, and the legend beside it. Shown in the flyout and in notifications.</summary>
public partial class DriveBreakdown : UserControl
{
    public DriveBreakdown()
    {
        InitializeComponent();
    }
}
