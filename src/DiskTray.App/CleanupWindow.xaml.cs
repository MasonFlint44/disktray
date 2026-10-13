using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DiskTray.Core;
using DiskTray.Core.Cleanup;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DiskTray.App;

/// <summary>
/// The Clean up dialog: prune Docker, compact the disks, or both. Opens straight away with
/// the compact step, and fills in the prune step once Docker has been asked. Nothing runs
/// until Clean up is clicked.
/// </summary>
internal partial class CleanupWindow : FluentWindow
{
    private readonly bool _dryRun;
    private readonly CleanupDefaults _defaults;
    private CleanupState _state;
    private readonly HashSet<string> _volumes = new(StringComparer.Ordinal);
    private bool _ready;
    private bool _scanned;

    /// <summary>Whether "Start Docker Desktop afterwards" was clicked, after which it's left as the user set it.</summary>
    private bool _startDockerSet;

    /// <param name="defaults">What to start with, from Settings › Clean up.</param>
    public CleanupWindow(DiskSnapshot snapshot, CleanupDefaults defaults, bool dryRun)
    {
        _dryRun = dryRun;
        _defaults = defaults;
        _state = new CleanupState(snapshot, null, [], new HashSet<string>(), null);
        InitializeComponent();
        Themes.Track(this);

        if (dryRun)
        {
            TitleBar.Title = Title = "Clean up (dry run)";
            DryRunBar.Visibility = Visibility.Visible;
        }

        var choices = CleanupChoices.Defaults(_state, _defaults);
        // Docker hasn't been asked yet; ticked for now so the step doesn't flicker off and on.
        Prune.IsChecked = true;
        BuildCache.IsChecked = choices.BuildCache;
        DanglingImages.IsChecked = choices.DanglingImages;
        Compact.IsChecked = choices.Compact;
        WhenIdle.IsChecked = choices.When == CompactWhen.WhenIdle;
        RightAway.IsChecked = choices.When == CompactWhen.RightAway;
        IdleNote.Text = CleanupLayout.IdleNote;
        _ready = true;
        Update();
    }

    /// <summary>
    /// Raised when Clean up is clicked, with what to do. The window closes after, or, when
    /// there's something to compact, stays (disabled) until the owner closes it once the
    /// admin prompt is answered.
    /// </summary>
    public event Action<CleanupRequest>? Started;

    /// <summary>The window's handle, for the admin prompt to belong to.</summary>
    public nint Handle => new WindowInteropHelper(this).Handle;

    private bool _closed;

    /// <summary>Closes the window unless it's closed already.</summary>
    public void CloseIfOpen()
    {
        if (!_closed)
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    /// <summary>
    /// Raised when Clean up is clicked on a dialog that has been open so long that what it
    /// found may have changed (containers started, distros opened, volumes back in use). The
    /// owner scans again and calls <see cref="ShowScan"/>, and the user looks it over first.
    /// </summary>
    public event Func<Task>? ScanAgain;

    /// <summary>How long a scan is trusted for when Clean up is clicked.</summary>
    private static readonly TimeSpan ScanLasts = TimeSpan.FromMinutes(10);

    private DateTime _scannedAt;

    /// <summary>
    /// Fills in what Docker can prune, once it's been asked. <paramref name="again"/> for a
    /// fresh scan of an open dialog: what's ticked stays as the user left it, less volumes
    /// that have gone.
    /// </summary>
    public void ShowScan(CleanupState state, bool again = false)
    {
        _state = state;
        _scanned = true;
        _scannedAt = DateTime.UtcNow;
        var defaults = CleanupChoices.Defaults(state, _defaults);
        _ready = false;
        if (again)
        {
            _volumes.IntersectWith(state.Docker?.UnusedVolumes.Select(v => v.Name) ?? []);
            Body.IsEnabled = true;
            _ready = true;
            BuildVolumeList();
            Update();
            Plan.Text = "Checked again, since this was open a while. Look it over, then click Clean up.";
            return;
        }

        Prune.IsChecked = defaults.Prune;
        if (!_startDockerSet)
        {
            // For whichever "when" is picked now, which may have changed while Docker was asked.
            StartDocker.IsChecked = CleanupChoices.StartDockerDefault(state, _defaults, RightAway.IsChecked == true ? CompactWhen.RightAway : CompactWhen.WhenIdle);
        }
        _volumes.Clear();
        _volumes.UnionWith(defaults.Volumes);
        // Volumes picked for removal are always on show, never only a count.
        if (defaults.Prune && defaults.Volumes.Count > 0)
        {
            Details.Visibility = Visibility.Visible;
        }

        _ready = true;
        BuildVolumeList();
        Update();
    }

    private CleanupChoices Choices() => new(
        Prune.IsChecked == true,
        BuildCache.IsChecked == true,
        DanglingImages.IsChecked == true,
        _volumes,
        Compact.IsChecked == true,
        RightAway.IsChecked == true ? CompactWhen.RightAway : CompactWhen.WhenIdle,
        StartDocker.IsChecked == true);

    private void Update()
    {
        if (!_ready)
        {
            return;
        }

        var choices = Choices();
        var model = CleanupLayout.Build(_state, choices);
        bool dockerKnown = _state.CanPrune;

        Checking.Visibility = _scanned ? Visibility.Collapsed : Visibility.Visible;
        Prune.IsEnabled = dockerKnown;
        PruneDetail.Text = !_scanned ? "Checking Docker…" : model.PruneUnavailable ?? model.PruneDetail;
        PruneTotal.Text = choices.Prune ? model.PruneTotal : "";
        DetailsButton.Visibility = dockerKnown ? Visibility.Visible : Visibility.Collapsed;
        if (!dockerKnown)
        {
            Details.Visibility = Visibility.Collapsed;
        }

        DetailsButton.Content = Details.Visibility == Visibility.Visible ? "Hide details" : "Choose what to remove";
        Details.IsEnabled = choices.Prune;
        BuildCacheSize.Text = model.BuildCacheSize;
        DanglingImagesSize.Text = model.DanglingImagesSize;

        CompactDetail.Text = model.CompactDetail;
        CompactGain.Text = model.CompactGain;
        CompactOptions.IsEnabled = choices.Compact;
        CompactOptions.Opacity = choices.Compact ? 1 : 0.5;
        IdleNote.Visibility = choices.When == CompactWhen.WhenIdle ? Visibility.Visible : Visibility.Collapsed;
        Warning.Visibility = model.Warning.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarningLines.Children.Clear();
        for (int i = 0; i < model.Warning.Count; i++)
        {
            WarningLines.Children.Add(new TextBlock
            {
                Text = model.Warning[i],
                FontSize = 12,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = i == 0 && model.Warning.Count > 1 ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, i == 0 ? 0 : 4, 0, 0),
            });
        }

        Summary.Text = model.Summary;
        Plan.Text = model.Plan;
        Plan.ToolTip = model.Plan;
        // Don't start before Docker has been asked, or the prune step would quietly be skipped.
        Start.IsEnabled = model.CanStart && _scanned;
    }

    /// <summary>One heading per project, then a check box per volume.</summary>
    private void BuildVolumeList()
    {
        VolumeGroups.Children.Clear();
        NoVolumes.Visibility = _state.VolumeGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var group in _state.VolumeGroups)
        {
            var line = CleanupLayout.GroupLine(group);
            var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var heading = new TextBlock { Text = line.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            heading.SetResourceReference(TextBlock.ForegroundProperty, line.Warning ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
            panel.Children.Add(heading);

            foreach (var volume in line.Volumes)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                var box = new CheckBox
                {
                    Content = new TextBlock { Text = volume.Label, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 },
                    IsChecked = _volumes.Contains(volume.Name),
                    ToolTip = volume.Name,
                    Tag = volume.Name,
                };
                System.Windows.Automation.AutomationProperties.SetName(box, $"{volume.Name}, {volume.Size}");
                box.Checked += OnVolumeChanged;
                box.Unchecked += OnVolumeChanged;
                row.Children.Add(box);
                row.Children.Add(new TextBlock { Text = volume.Size, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
                panel.Children.Add(row);
            }

            VolumeGroups.Children.Add(panel);
        }
    }

    private void OnVolumeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string name } box)
        {
            if (box.IsChecked == true)
            {
                _volumes.Add(name);
            }
            else
            {
                _volumes.Remove(name);
            }

            Update();
        }
    }

    private void OnChanged(object sender, RoutedEventArgs e) => Update();

    /// <summary>Follows the default for the new choice, until the user has set the box themselves.</summary>
    private void OnWhenChanged(object sender, RoutedEventArgs e)
    {
        if (_ready && !_startDockerSet)
        {
            _ready = false;
            StartDocker.IsChecked = CleanupChoices.StartDockerDefault(_state, _defaults, RightAway.IsChecked == true ? CompactWhen.RightAway : CompactWhen.WhenIdle);
            _ready = true;
        }

        Update();
    }

    private void OnStartDockerClick(object sender, RoutedEventArgs e) => _startDockerSet = true;

    private void OnDetailsClick(object sender, RoutedEventArgs e)
    {
        Details.Visibility = Details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        Update();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow - _scannedAt > ScanLasts && ScanAgain is { } scanAgain)
        {
            Body.IsEnabled = false;
            Start.IsEnabled = false;
            Plan.Text = "Checking again, since this was open a while…";
            await scanAgain();
            return;
        }

        var choices = Choices();
        var model = CleanupLayout.Build(_state, choices);
        if (!model.CanStart)
        {
            return;
        }

        var request = CleanupRequest.From(_state, choices, model, _dryRun);
        if (request.Disks.Count == 0)
        {
            Started?.Invoke(request);
            Close();
            return;
        }

        // Open until the admin prompt is answered, so the prompt comes up over it, in front.
        Body.IsEnabled = false;
        Start.IsEnabled = false;
        CancelButton.IsEnabled = false;
        Plan.Text = "Waiting for admin permission…";
        Started?.Invoke(request);
    }
}
