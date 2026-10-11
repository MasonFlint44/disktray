using System.Windows;
using System.Windows.Controls;
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

    /// <param name="defaults">What to start with, from Settings › Clean up.</param>
    public CleanupWindow(DiskSnapshot snapshot, CleanupDefaults defaults, bool dryRun)
    {
        _dryRun = dryRun;
        _defaults = defaults;
        _state = new CleanupState(snapshot, null, [], new HashSet<string>(), 0);
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

    /// <summary>Raised when Clean up is clicked, with what to do. The window closes after.</summary>
    public event Action<CleanupRequest>? Started;

    /// <summary>Fills in what Docker can prune, once it's been asked.</summary>
    public void ShowScan(CleanupState state)
    {
        _state = state;
        _scanned = true;
        var defaults = CleanupChoices.Defaults(state, _defaults);
        _ready = false;
        Prune.IsChecked = defaults.Prune;
        StartDocker.IsChecked = defaults.StartDockerAfter;
        _volumes.Clear();
        _volumes.UnionWith(defaults.Volumes);
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
        bool dockerKnown = _state.DockerRunning;

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

    private void OnDetailsClick(object sender, RoutedEventArgs e)
    {
        Details.Visibility = Details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        Update();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        var choices = Choices();
        var model = CleanupLayout.Build(_state, choices);
        if (!model.CanStart)
        {
            return;
        }

        Started?.Invoke(CleanupRequest.From(_state, choices, model, _dryRun));
        Close();
    }
}
