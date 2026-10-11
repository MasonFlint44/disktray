using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DiskTray.Core.Tray;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;

namespace DiskTray.App;

/// <summary>
/// Picks a pinned item's color: a shade square and hue strip, a hex box, suggested colors,
/// and a preview of the tray icon. Warns when the color would blend into the free slice.
/// </summary>
internal partial class ColorPickerWindow : FluentWindow
{
    private static readonly string[] Suggested =
        ["#2496ED", "#E95420", "#A371F7", "#3FB950", "#1ABC9C", "#F778BA", "#DB61A2", "#58A6FF", "#D29922", "#D0D0D0"];

    private readonly Rgb _usual;
    private readonly IconShape _shape;
    private double _hue;
    private double _saturation;
    private double _value;
    private bool _updating;

    /// <param name="current">The color now, or null when it's the usual one.</param>
    /// <param name="usual">The item's usual color, for "Use the usual color".</param>
    public ColorPickerWindow(string itemName, Rgb? current, Rgb usual, IconShape shape)
    {
        _usual = usual;
        _shape = shape;
        InitializeComponent();
        Themes.Track(this);
        TitleBar.Title = Title = $"Color for {itemName}";

        foreach (string hex in Suggested)
        {
            var color = Rgb.FromHex(hex);
            var swatch = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Content = new System.Windows.Shapes.Ellipse { Width = 22, Height = 22, Fill = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)) },
                ToolTip = hex,
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, hex);
            swatch.Click += (_, _) => Set(color);
            Swatches.Children.Add(swatch);
        }

        Set(current ?? usual);
    }

    /// <summary>The color picked, or null for the usual one. Set when the window closes with OK.</summary>
    public string? Picked { get; private set; }

    private void Set(Rgb color)
    {
        (_hue, _saturation, _value) = color.ToHsv();
        Update(updateHex: true);
    }

    private Rgb Current => Rgb.FromHsv(_hue, _saturation, _value);

    private void Update(bool updateHex)
    {
        var hue = Rgb.FromHsv(_hue, 1, 1);
        HueFill.Fill = new SolidColorBrush(Color.FromRgb(hue.R, hue.G, hue.B));
        Canvas.SetLeft(ShadeThumb, (_saturation * ShadeArea.Width) - (ShadeThumb.Width / 2));
        Canvas.SetTop(ShadeThumb, ((1 - _value) * ShadeArea.Height) - (ShadeThumb.Height / 2));
        Canvas.SetTop(HueThumb, (_hue / 360 * HueArea.Height) - (HueThumb.Height / 2));

        var color = Current;
        if (updateHex)
        {
            _updating = true;
            Hex.Text = color.ToString();
            _updating = false;
        }

        var item = new ItemColor(color);
        var chart = new PieChart([new PieSlice(0.35, item.Main), new PieSlice(0.3, item.Reclaimable), new PieSlice(0.35, Palette.Free)], AlertLevel.Normal);
        PreviewSmall.Source = PieIconRenderer.Render(chart, 24, _shape);
        PreviewLarge.Source = PieIconRenderer.Render(chart, 64, _shape);
        BlendWarning.Visibility = Palette.BlendsWithFree(color) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShadeDown(object sender, MouseButtonEventArgs e)
    {
        ShadeArea.CaptureMouse();
        PickShade(e.GetPosition(ShadeArea));
    }

    private void OnShadeMove(object sender, MouseEventArgs e)
    {
        if (ShadeArea.IsMouseCaptured)
        {
            PickShade(e.GetPosition(ShadeArea));
        }
    }

    private void OnHueDown(object sender, MouseButtonEventArgs e)
    {
        HueArea.CaptureMouse();
        PickHue(e.GetPosition(HueArea));
    }

    private void OnHueMove(object sender, MouseEventArgs e)
    {
        if (HueArea.IsMouseCaptured)
        {
            PickHue(e.GetPosition(HueArea));
        }
    }

    private void OnAreaUp(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    private void PickShade(Point point)
    {
        _saturation = Math.Clamp(point.X / ShadeArea.Width, 0, 1);
        _value = 1 - Math.Clamp(point.Y / ShadeArea.Height, 0, 1);
        Update(updateHex: true);
    }

    private void PickHue(Point point)
    {
        // Stop just short of the bottom, which is red again.
        _hue = Math.Clamp(point.Y / HueArea.Height, 0, 0.9999) * 360;
        Update(updateHex: true);
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        string text = Hex.Text.Trim();
        text = text.StartsWith('#') ? text : "#" + text;
        bool valid = TrayPin.IsColor(text);
        Ok.IsEnabled = valid;
        if (valid)
        {
            (_hue, _saturation, _value) = Rgb.FromHex(text).ToHsv();
            Update(updateHex: false);
        }
    }

    private void OnUseUsual(object sender, RoutedEventArgs e)
    {
        Picked = null;
        DialogResult = true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var color = Current;
        Picked = color == _usual ? null : color.ToString();
        DialogResult = true;
    }
}
