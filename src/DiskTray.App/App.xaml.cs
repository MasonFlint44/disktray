using System.Windows;

namespace DiskTray.App;

/// <summary>
/// Tray-only app: there is no main window, so the app runs until the user picks Exit from the tray menu.
/// </summary>
public partial class App : Application
{
}
