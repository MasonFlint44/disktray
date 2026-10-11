using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DiskTray.Core;
using DiskTray.Core.Settings;
using DiskTray.Core.Tray;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace DiskTray.App;

/// <summary>Settings › Tray icons: the icon shape, and up to 3 pinned items, each with a name, color and what its icon shows.</summary>
internal partial class SettingsWindow
{
    private readonly List<TrayPin> _pins = [];

    private void LoadTrayIcons(AppSettings settings)
    {
        ShapePie.IsChecked = settings.IconShape == IconShape.Pie;
        ShapeBar.IsChecked = settings.IconShape == IconShape.Bar;
        var sample = new PieChart([new(0.3, Palette.Docker.Main), new(0.35, Palette.Docker.Reclaimable), new(0.35, Palette.Free)], AlertLevel.Normal);
        PiePreview.Source = PieIconRenderer.Render(sample, 20, IconShape.Pie);
        BarPreview.Source = PieIconRenderer.Render(sample, 20, IconShape.Bar);
        ShapePie.Checked += (_, _) => BuildPinRows();
        ShapeBar.Checked += (_, _) => BuildPinRows();
        _pins.AddRange(settings.Pins);
        BuildPinRows();
    }

    private IconShape ChosenShape => ShapeBar.IsChecked == true ? IconShape.Bar : IconShape.Pie;

    /// <summary>What the pin points at, as it's detected now, ignoring any name or color picked for it.</summary>
    private TrayItem? Detected(TrayPin pin) =>
        _snapshot is null ? null : TrayLayout.Resolve(_snapshot, [pin with { Name = null, Color = null }])[0];

    /// <summary>The pins without any picked names or colors, so the usual colors come out in the same turn as the tray's.</summary>
    private static List<TrayPin> Usual(IEnumerable<TrayPin> pins) => pins.Select(p => p with { Name = null, Color = null }).ToList();

    private ItemColor UsualColor(TrayPin pin)
    {
        if (_snapshot is not null)
        {
            var items = TrayLayout.Resolve(_snapshot, Usual(_pins));
            if (items.FirstOrDefault(i => i.Pin.SameItem(pin)) is { } item)
            {
                return item.Color;
            }
        }

        return pin.Kind switch
        {
            PinKind.Docker => Palette.Docker,
            PinKind.Distro => Palette.Wsl,
            PinKind.Drive => new ItemColor(Palette.Other),
            _ => Palette.Extras[0],
        };
    }

    private void BuildPinRows()
    {
        PinRows.Children.Clear();
        for (int i = 0; i < _pins.Count; i++)
        {
            PinRows.Children.Add(PinRow(i));
        }

        AddPin.IsEnabled = _pins.Count < TrayPin.MaxPins;
        AddHint.Visibility = AddPin.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private Border PinRow(int index)
    {
        var pin = _pins[index];
        var detected = Detected(pin);
        string usualName = detected?.Name ?? Fallback(pin);

        var grid = new Grid();
        foreach (var width in new[] { 44.0, 10, 130, 10, double.NaN, 10, 160, 6, 36 })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(width) ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
        }

        // Color
        var color = pin.Color is { } hex ? Rgb.FromHex(hex) : UsualColor(pin).Main;
        var colorButton = new Button
        {
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Content = new System.Windows.Shapes.Ellipse { Width = 20, Height = 20, Fill = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)) },
            ToolTip = pin.Kind == PinKind.Drive ? "Color for everything else on the drive" : "Change color",
        };
        System.Windows.Automation.AutomationProperties.SetName(colorButton, $"Color for {pin.Name ?? usualName}");
        colorButton.Click += (_, _) => PickColor(index, pin.Name ?? usualName);
        grid.Children.Add(colorButton);

        // Name
        var name = new TextBox { Text = pin.Name ?? "", PlaceholderText = usualName };
        name.TextChanged += (_, _) => _pins[index] = _pins[index] with { Name = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text };
        grid.Children.Add(Labeled("Name", name, 2));

        // What it points at
        string where = pin.Kind switch
        {
            PinKind.Drive => detected?.Drive?.Root ?? pin.Target ?? _snapshot?.SystemDrive.Root ?? "",
            PinKind.Path => pin.Target ?? "",
            _ => detected?.File?.Path ?? "Not found",
        };
        var path = new TextBox
        {
            Text = where,
            IsReadOnly = true,
            ClearButtonEnabled = false,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            ToolTip = where,
        };
        string pathLabel = pin.Kind switch
        {
            PinKind.Drive => "Drive",
            PinKind.Path when _snapshot?.Files.FirstOrDefault(f => f.Id == Core.Detection.DiskScanner.PathId(where))?.Kind == DiskKind.Folder => "Folder",
            PinKind.Path => "Path",
            _ => "Path · auto-detected",
        };
        grid.Children.Add(Labeled(pathLabel, path, 4));

        // Icon shows
        var shows = new ComboBox { ItemsSource = pin.ShowsChoices.Select(c => Describe(c, pin.Kind)).ToList() };
        shows.SelectedIndex = Math.Max(0, pin.ShowsChoices.ToList().IndexOf(pin.Shows));
        shows.SelectionChanged += (_, _) =>
            _pins[index] = _pins[index] with { Shows = _pins[index].ShowsChoices[Math.Max(0, shows.SelectedIndex)] };
        grid.Children.Add(Labeled("Icon shows", shows, 6));

        // Remove
        var remove = new Button
        {
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Appearance = ControlAppearance.Transparent,
            BorderThickness = new Thickness(0),
            Icon = new SymbolIcon(SymbolRegular.Dismiss24),
            VerticalAlignment = VerticalAlignment.Bottom,
            IsEnabled = _pins.Count > 1,
            ToolTip = _pins.Count > 1 ? "Remove" : "At least one item stays pinned",
        };
        System.Windows.Automation.AutomationProperties.SetName(remove, $"Remove {pin.Name ?? usualName}");
        remove.Click += (_, _) =>
        {
            _pins.RemoveAt(index);
            BuildPinRows();
        };
        Grid.SetColumn(remove, 8);
        grid.Children.Add(remove);

        return new Border { Style = (Style)FindResource("Card"), Child = grid };
    }

    private static StackPanel Labeled(string label, FrameworkElement control, int column)
    {
        var panel = new StackPanel();
        var text = new Label { Content = label, Target = control, Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 4), FontSize = 12 };
        text.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
        panel.Children.Add(text);
        panel.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        Grid.SetColumn(panel, column);
        return panel;
    }

    private static string Describe(IconShows shows, PinKind kind) => (shows, kind) switch
    {
        (IconShows.Default, PinKind.Drive) => "Drive breakdown",
        (IconShows.Default, _) => kind == PinKind.Path ? "Size + free space" : "Disk + free space",
        (IconShows.ShareOfDrive, _) => "Share of drive",
        _ => "Used vs free",
    };

    private static string Fallback(TrayPin pin) => pin.Kind switch
    {
        PinKind.Docker => "Docker data",
        PinKind.Distro => "WSL distro",
        PinKind.Drive => $"Drive {(pin.Target ?? "C:").TrimEnd('\\')}",
        _ => Core.Detection.WindowsPath.GetName(pin.Target ?? ""),
    };

    private void PickColor(int index, string name)
    {
        var pin = _pins[index];
        var picker = new ColorPickerWindow(name, pin.Color is { } hex ? Rgb.FromHex(hex) : null, UsualColor(pin).Main, ChosenShape) { Owner = this };
        if (picker.ShowDialog() == true)
        {
            _pins[index] = pin with { Color = picker.Picked };
            BuildPinRows();
        }
    }

    private void OnAddPin(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddPin, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (label, pin) in Addable())
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => Add(pin);
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        var file = new MenuItem { Header = "File…" };
        file.Click += (_, _) => AddFile();
        var folder = new MenuItem { Header = "Folder…" };
        folder.Click += (_, _) => AddFolder();
        menu.Items.Add(file);
        menu.Items.Add(folder);
        menu.IsOpen = true;
    }

    /// <summary>What was detected and isn't pinned yet: Docker's data disk, each distro and each fixed drive.</summary>
    private IEnumerable<(string Label, TrayPin Pin)> Addable()
    {
        var candidates = new List<(string, TrayPin)>();
        if (_snapshot?.Docker is not null)
        {
            candidates.Add(("Docker data", new TrayPin(PinKind.Docker)));
        }

        foreach (var distro in _snapshot?.Files.Where(f => f.Kind == DiskKind.WslDistro) ?? [])
        {
            // The primary distro is pinned without a target, so it follows WSL's default.
            var pin = distro.Id == _snapshot!.PrimaryDistroId ? new TrayPin(PinKind.Distro) : new TrayPin(PinKind.Distro, distro.Id);
            candidates.Add(($"{distro.Name} (WSL)", pin));
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            bool system = string.Equals(drive.Name, _snapshot?.SystemDrive.Root, StringComparison.OrdinalIgnoreCase);
            candidates.Add(($"Drive {drive.Name.TrimEnd('\\')}", system ? new TrayPin(PinKind.Drive) : new TrayPin(PinKind.Drive, drive.Name)));
        }

        return candidates.Where(c => !_pins.Any(p => p.SameItem(c.Item2) || SameDrive(p, c.Item2)));
    }

    /// <summary>The system drive can be pinned as the default drive or by its letter; both are the same icon.</summary>
    private bool SameDrive(TrayPin a, TrayPin b) =>
        a.Kind == PinKind.Drive && b.Kind == PinKind.Drive
        && string.Equals(a.Target ?? _snapshot?.SystemDrive.Root, b.Target ?? _snapshot?.SystemDrive.Root, StringComparison.OrdinalIgnoreCase);

    private void AddFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Pin a file",
            Filter = "Virtual disks (*.vhdx;*.vhd;*.vmdk;*.qcow2)|*.vhdx;*.vhd;*.vmdk;*.qcow2|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            Add(new TrayPin(PinKind.Path, dialog.FileName));
        }
    }

    private void AddFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Pin a folder" };
        if (dialog.ShowDialog(this) == true)
        {
            Add(new TrayPin(PinKind.Path, dialog.FolderName));
        }
    }

    private void Add(TrayPin pin)
    {
        if (_pins.Count < TrayPin.MaxPins && !_pins.Any(p => p.SameItem(pin) || SameDrive(p, pin)))
        {
            _pins.Add(pin);
            BuildPinRows();
        }
    }
}
