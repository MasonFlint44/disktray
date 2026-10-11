using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskTray.Core.Settings;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DiskTray.App;

/// <summary>Settings › Detection: what to look for, the large-file scan, and the code folders Clean up searches.</summary>
internal partial class SettingsWindow
{
    private readonly List<string> _codeFolders = [];

    private void LoadDetection(AppSettings settings)
    {
        DetectDistros.IsChecked = settings.DetectDistros;
        DetectDocker.IsChecked = settings.DetectDocker;
        LargeFileScan.IsChecked = settings.LargeFileScan;
        LargeFileMin.Value = settings.LargeFileMinGiB;
        ShowLargeFiles.IsChecked = settings.ShowLargeFiles;
        _codeFolders.AddRange(settings.CodeFolders);
        BuildCodeFolderRows();
    }

    private void BuildCodeFolderRows()
    {
        CodeFolderRows.Children.Clear();
        NoCodeFolders.Visibility = _codeFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (string folder in _codeFolders)
        {
            var row = new DockPanel();
            var remove = new Button
            {
                Width = 36,
                Height = 36,
                Padding = new Thickness(0),
                Appearance = ControlAppearance.Transparent,
                BorderThickness = new Thickness(0),
                Icon = new SymbolIcon(SymbolRegular.Dismiss24),
                ToolTip = "Remove",
            };
            System.Windows.Automation.AutomationProperties.SetName(remove, $"Remove {folder}");
            remove.Click += (_, _) =>
            {
                _codeFolders.Remove(folder);
                BuildCodeFolderRows();
            };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(new TextBlock
            {
                Text = folder,
                ToolTip = folder,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var card = new Border { Style = (Style)FindResource("Card"), Child = row, Padding = new Thickness(12, 4, 4, 4) };
            CodeFolderRows.Children.Add(card);
        }
    }

    private void OnAddCodeFolder(object sender, RoutedEventArgs e)
    {
        // Starting in WSL makes the usual place, ~/git in a distro, a couple of clicks away.
        var dialog = new OpenFolderDialog { Title = "Add a code folder", InitialDirectory = @"\\wsl.localhost\" };
        if (dialog.ShowDialog(this) == true && !_codeFolders.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase))
        {
            _codeFolders.Add(dialog.FolderName);
            BuildCodeFolderRows();
        }
    }
}
