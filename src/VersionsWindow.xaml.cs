using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DrangedAddons;

public sealed class VersionItem(AddonVersion version, bool isInstalled)
{
    static readonly Brush ReleaseBrush = Frozen(0x4C, 0xAF, 0x7D);
    static readonly Brush BetaBrush = Frozen(0x6A, 0x9F, 0xE0);

    public AddonVersion Version { get; } = version;
    public bool IsInstalled { get; } = isInstalled;
    public string KindText => Version.IsRelease ? "Release" : "Beta";
    public Brush KindBrush => Version.IsRelease ? ReleaseBrush : BetaBrush;
    public string ActionText => IsInstalled ? "Reinstall" : "Install";

    public string DateText => Version.IsRelease
        ? $"Released {Version.Date.ToLocalTime():d MMM yyyy}"
        : $"Untagged, from {Version.Date.ToLocalTime():d MMM yyyy}  ·  commit {Version.Commit?[..7]}";

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public partial class VersionsWindow : Window
{
    readonly GitHubClient _github;
    readonly RemoteAddon _addon;
    readonly bool _includeBeta;
    readonly Func<string?> _installedVersion;
    readonly Func<AddonVersion, Window, Task<bool>> _install;
    List<AddonVersion> _versions = new();

    public VersionsWindow(GitHubClient github, RemoteAddon addon, WowFlavor flavor, bool includeBeta, ImageSource? icon,
        Func<string?> installedVersion, Func<AddonVersion, Window, Task<bool>> install)
    {
        InitializeComponent();
        if (icon is not null)
        {
            HeaderIcon.Source = icon;
            HeaderIcon.Visibility = Visibility.Visible;
        }
        _github = github;
        _addon = addon;
        _includeBeta = includeBeta;
        _installedVersion = installedVersion;
        _install = install;

        Title = $"{addon.Name} versions";
        HeaderText.Text = $"{addon.Name} versions";
        SubText.Text = $"{flavor.DisplayName}  ·  "
                       + (includeBeta ? "tagged releases and beta versions" : "tagged releases only (tick “Show beta versions” to see untagged ones)");
        Loaded += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        LoadingText.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<string>(message => LoadingText.Text = message);
            _versions = await _github.GetVersionHistoryAsync(_addon, _includeBeta, progress);
            ShowVersions();
            LoadingText.Visibility = _versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadingText.Text = "No installable versions were found.";
        }
        catch (Exception ex)
        {
            LoadingText.Text = "Couldn't load versions: " + ex.Message;
        }
    }

    void ShowVersions()
    {
        var installed = _installedVersion();
        var items = _versions
            .Select(v => new VersionItem(v, installed is not null && Versions.Compare(v.Version, installed) == 0))
            .ToList();
        VersionList.ItemsSource = items;
        VersionList.SelectedItem = items.FirstOrDefault(i => i.IsInstalled) ?? items.FirstOrDefault();
        if (VersionList.SelectedItem is not null) VersionList.ScrollIntoView(VersionList.SelectedItem);
    }

    void VersionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VersionList.SelectedItem is not VersionItem item) return;
        NotesHeader.Text = $"Changes in {item.Version.Label}";
        NotesText.Text = string.IsNullOrWhiteSpace(item.Version.Notes) ? "No notes for this version." : item.Version.Notes;
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VersionItem item) return;
        VersionList.SelectedItem = item;
        IsEnabled = false;
        LoadingText.Text = $"Installing {item.Version.Label}…";
        LoadingText.Visibility = Visibility.Visible;
        try
        {
            await _install(item.Version, this);
        }
        finally
        {
            IsEnabled = true;
            LoadingText.Visibility = Visibility.Collapsed;
            ShowVersions();
        }
    }
}
