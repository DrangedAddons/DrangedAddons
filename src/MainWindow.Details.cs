using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DrangedAddons;

/// <summary>A version on the details page's Versions tab; click it to show what changed.</summary>
public sealed class VersionItem(AddonVersion version, bool isInstalled, bool canInstall) : INotifyPropertyChanged
{
    static readonly Brush ReleaseBrush = Frozen(0x4C, 0xAF, 0x7D);
    static readonly Brush BetaBrush = Frozen(0x6A, 0x9F, 0xE0);

    public AddonVersion Version { get; } = version;
    public bool IsInstalled { get; } = isInstalled;
    public bool CanInstall { get; } = canInstall;
    public string KindText => Version.IsRelease ? "Release" : "Beta";
    public Brush KindBrush => Version.IsRelease ? ReleaseBrush : BetaBrush;
    public string ActionText => IsInstalled ? "Reinstall" : "Install";
    public string NotesText => string.IsNullOrWhiteSpace(Version.Notes) ? "No notes for this version." : Version.Notes.Trim();

    public string DateText => Version.IsRelease
        ? $"Released {Version.Date.ToLocalTime():d MMM yyyy}"
        : $"Untagged, from {Version.Date.ToLocalTime():d MMM yyyy}  ·  commit {Version.Commit?[..7]}";

    UIElement? _notesView;
    /// <summary>The notes rendered as Markdown; built the first time the version is opened.</summary>
    public UIElement? NotesView
    {
        get => _notesView;
        set
        {
            _notesView = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotesView)));
        }
    }

    bool _showNotes;
    public bool ShowNotes
    {
        get => _showNotes;
        set
        {
            _showNotes = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowNotes)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>The details page: header stats and the Overview / Changelog / Gallery / Versions tabs.</summary>
public partial class MainWindow
{
    /// <summary>What's been fetched for an addon's details page this session; each piece once, when first needed.</summary>
    sealed class DetailsData
    {
        public Task<string?>? Readme;
        public Task<string?>? Changelog;
        public Task<long?>? Downloads;
        public Task<List<string>>? GalleryPaths;
        public Task<List<AddonVersion>>? Versions;
        public bool VersionsWithBeta;
    }

    readonly Dictionary<string, DetailsData> _details = new();
    readonly Dictionary<string, Task<ImageSource?>> _images = new();
    readonly HashSet<string> _loadedTabs = new();
    readonly Dictionary<string, string?> _tabMessages = new();
    AddonRow? _detailsRow;
    (AddonState, string?) _detailsRowState;
    int _detailsLoad; // bumped for each addon shown, so late results for another addon are dropped

    DetailsData DataFor(AddonRow row) =>
        _details.TryGetValue(row.Name, out var data) ? data : _details[row.Name] = new DetailsData();

    /// <summary>Points the details page at a row: a new addon resets the tabs; the same one (rows rebuilt) refreshes.</summary>
    void BindDetails(AddonRow row, bool newAddon, string? tab)
    {
        if (_detailsRow is not null) _detailsRow.PropertyChanged -= DetailsRow_PropertyChanged;
        _detailsRow = row;
        row.PropertyChanged += DetailsRow_PropertyChanged;
        _detailsRowState = (row.State, row.InstalledVersion);

        if (newAddon)
        {
            _detailsLoad++;
            CloseLightbox();
            DetailsScroll.ScrollToTop();
            OverviewContent.Children.Clear();
            ChangelogContent.Children.Clear();
            GalleryContent.Children.Clear();
            VersionList.ItemsSource = null;
            _loadedTabs.Clear();
            _tabMessages.Clear();
            GalleryTab.Content = "Gallery";
            VersionsTab.Content = "Versions";
            _ = LoadDownloadsAsync(row, _detailsLoad);
            _ = CountGalleryAsync(row, _detailsLoad);
        }
        else
        {
            _loadedTabs.Remove("versions"); // installed badge may have moved
        }
        UpdateDetailStats(row);
        SelectDetailTab(tab ?? (newAddon ? "overview" : CurrentDetailTab));
    }

    void UnbindDetails()
    {
        if (_detailsRow is not null) _detailsRow.PropertyChanged -= DetailsRow_PropertyChanged;
        _detailsRow = null;
        CloseLightbox();
    }

    /// <summary>Install / update / uninstall from the page: refresh size and About (not on every progress tick).</summary>
    void DetailsRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AddonRow row || (row.State, row.InstalledVersion) == _detailsRowState) return;
        _detailsRowState = (row.State, row.InstalledVersion);
        UpdateDetailStats(row);
        _loadedTabs.Remove("versions");
        if (CurrentDetailTab == "versions") ShowDetailTab("versions");
    }

    // ---- Tabs -----------------------------------------------------------------------------------------------------

    string CurrentDetailTab =>
        ChangelogTab.IsChecked == true ? "changelog"
        : GalleryTab.IsChecked == true ? "gallery"
        : VersionsTab.IsChecked == true ? "versions"
        : "overview";

    void SelectDetailTab(string tab)
    {
        var button = tab switch { "changelog" => ChangelogTab, "gallery" => GalleryTab, "versions" => VersionsTab, _ => OverviewTab };
        if (button.IsChecked == true) ShowDetailTab(tab);
        else button.IsChecked = true; // → DetailTab_Checked
    }

    void DetailTab_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && _detailsRow is not null) ShowDetailTab(CurrentDetailTab);
    }

    void ShowDetailTab(string tab)
    {
        OverviewPage.Visibility = tab == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ChangelogPage.Visibility = tab == "changelog" ? Visibility.Visible : Visibility.Collapsed;
        GalleryPage.Visibility = tab == "gallery" ? Visibility.Visible : Visibility.Collapsed;
        VersionsPage.Visibility = tab == "versions" ? Visibility.Visible : Visibility.Collapsed;
        ShowTabMessage(tab);
        if (_detailsRow is not { } row || !_loadedTabs.Add(tab)) return;
        var load = _detailsLoad;
        _ = tab switch
        {
            "changelog" => LoadChangelogAsync(row, load),
            "gallery" => LoadGalleryAsync(row, load),
            "versions" => LoadVersionsAsync(row, load),
            _ => LoadOverviewAsync(row, load),
        };
    }

    /// <summary>A centred note for a tab (loading, empty, error); null clears it.</summary>
    void SetTabMessage(string tab, string? message)
    {
        _tabMessages[tab] = message;
        if (CurrentDetailTab == tab) ShowTabMessage(tab);
    }

    void ShowTabMessage(string tab)
    {
        var message = _tabMessages.GetValueOrDefault(tab);
        DetailsMessage.Text = message ?? "";
        DetailsMessage.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    static string LoadError(Exception ex) => ex is RateLimitException
        ? "GitHub is limiting requests right now. Try again in a few minutes, or add a GitHub token (File → GitHub token…)."
        : $"Couldn't load this from GitHub: {ex.Message}";

    // ---- Header stats and About -----------------------------------------------------------------------------------

    void UpdateDetailStats(AddonRow row)
    {
        UpdatedText.Text = Relative(row.Latest.Date) + (row.Latest.IsRelease ? "" : " (beta)");
        GamesText.Text = row.Remote.Games.Count == 0
            ? "All game versions"
            : string.Join(", ", row.Remote.Games.Select(WowLocator.DisplayName));
        SizeText.Text = row.State == AddonState.NotInstalled ? "Not installed" : "…";
        if (row.State != AddonState.NotInstalled && CurrentFlavor is { } flavor)
            _ = ShowSizeAsync(row, flavor, _detailsLoad);
        FillAbout(row);
    }

    async Task ShowSizeAsync(AddonRow row, WowFlavor flavor, int load)
    {
        var folders = InstalledFolders(row, flavor).Select(f => Path.Combine(flavor.AddOnsPath, f)).ToList();
        var bytes = await Task.Run(() => folders.Sum(FolderSize));
        if (load == _detailsLoad && _detailsRow?.Name == row.Name) SizeText.Text = FormatSize(bytes);
    }

    List<string> InstalledFolders(AddonRow row, WowFlavor flavor) =>
        _manifest.For(flavor.AddOnsPath).GetValueOrDefault(row.Name)?.Folders is { Count: > 0 } folders ? folders : [row.Name];

    /// <summary>Bytes in a folder, not counting a Git working copy's .git history.</summary>
    static long FolderSize(string folder)
    {
        try
        {
            var git = Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar;
            return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => !f.FullName.Contains(git, StringComparison.OrdinalIgnoreCase))
                .Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.##} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    static string Relative(DateTimeOffset date)
    {
        var days = (DateTime.Today - date.LocalDateTime.Date).Days;
        return days switch
        {
            <= 0 => "Today",
            1 => "Yesterday",
            < 30 => $"{days} days ago",
            _ => date.ToLocalTime().ToString("d MMM yyyy"),
        };
    }

    async Task LoadDownloadsAsync(AddonRow row, int load)
    {
        DownloadsText.Text = "…";
        try
        {
            var data = DataFor(row);
            var count = await (data.Downloads ??= _github.GetDownloadCountAsync(row.Name));
            if (load != _detailsLoad) return;
            DownloadsText.Text = count is { } n ? $"{n:N0} download{(n == 1 ? "" : "s")}" : "No releases";
        }
        catch (Exception ex) when (ex is HttpRequestException or RateLimitException or InvalidOperationException)
        {
            DataFor(row).Downloads = null; // try again next time
            if (load == _detailsLoad) DownloadsText.Text = "—";
        }
    }

    void FillAbout(AddonRow row)
    {
        AboutPanel.Children.Clear();
        var flavor = CurrentFlavor;
        var record = flavor is null ? null : _manifest.For(flavor.AddOnsPath).GetValueOrDefault(row.Name);

        AboutRow("Installed", row.InstalledVersion is { } v
            ? $"v{v}" + (row.State == AddonState.DevCopy ? " (Git working copy)" : "")
            : "Not installed");
        AboutRow("Latest", $"v{row.Latest.Label}");
        if (record is not null && record.InstalledAt != default)
            AboutRow("Installed on", record.InstalledAt.ToString("d MMM yyyy, HH:mm"));
        if (row.State != AddonState.NotInstalled && flavor is not null)
            AboutRow("Folders", string.Join(", ", InstalledFolders(row, flavor)));
        if (record?.Pinned == true)
            AboutRow("Auto-update", "Off for this addon (a version was picked in Versions)");

        var elsewhere = (FlavorList.ItemsSource as IEnumerable<FlavorItem>)?
            .Select(f => f.Flavor)
            .Where(f => f.Folder != flavor?.Folder
                        && (_manifest.For(f.AddOnsPath).ContainsKey(row.Name) || Directory.Exists(Path.Combine(f.AddOnsPath, row.Name))))
            .Select(f => f.DisplayName)
            .ToList() ?? [];
        if (elsewhere.Count > 0) AboutRow("Also installed in", string.Join(", ", elsewhere));

        var repo = $"https://github.com/{GitHubClient.Org}/{row.Name}";
        var link = new Hyperlink(new Run($"github.com/{GitHubClient.Org}/{row.Name}"))
        {
            Foreground = (Brush)FindResource("AccentBrush"),
            TextDecorations = null,
            ToolTip = repo,
        };
        link.Click += (_, _) => Process.Start(new ProcessStartInfo(repo) { UseShellExecute = true });
        var value = new TextBlock { TextWrapping = TextWrapping.Wrap };
        value.Inlines.Add(link);
        AboutRow("Repository", value);
    }

    void AboutRow(string label, object value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = (Brush)FindResource("MutedBrush") });
        panel.Children.Add(value as UIElement ?? new TextBlock { Text = value.ToString(), TextWrapping = TextWrapping.Wrap });
        AboutPanel.Children.Add(panel);
    }

    void DetailsMenu_Click(object sender, RoutedEventArgs e) => OpenMenuUnder(DetailsMenuButton);

    void ViewOnGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
            Process.Start(new ProcessStartInfo($"https://github.com/{GitHubClient.Org}/{row.Name}") { UseShellExecute = true });
    }

    // ---- Overview and changelog (Markdown) ------------------------------------------------------------------------

    MarkdownRenderer RendererFor(AddonRow row) =>
        new(link => ResolveLink(row, link), src => ResolveImage(row, src), url => LoadImageAsync(url));

    async Task LoadOverviewAsync(AddonRow row, int load)
    {
        SetTabMessage("overview", "Loading…");
        string? readme;
        try
        {
            readme = await (DataFor(row).Readme ??= _github.GetReadmeAsync(row.Remote));
        }
        catch (Exception ex) when (ex is HttpRequestException or RateLimitException)
        {
            DataFor(row).Readme = null;
            if (load != _detailsLoad) return;
            _loadedTabs.Remove("overview"); // try again next time the tab is opened
            SetTabMessage("overview", LoadError(ex));
            return;
        }
        if (load != _detailsLoad) return;
        SetTabMessage("overview", null);
        if (readme is null)
        {
            OverviewContent.Children.Add(new TextBlock { Text = row.Description, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 });
            OverviewContent.Children.Add(Note("This addon doesn't have a README yet, so this is its short description from GitHub."));
            return;
        }
        RendererFor(row).Render(OverviewContent, readme);
    }

    async Task LoadChangelogAsync(AddonRow row, int load)
    {
        SetTabMessage("changelog", "Loading…");
        try
        {
            var text = await (DataFor(row).Changelog ??= _github.GetChangelogTextAsync(row.Remote));
            if (text is null)
            {
                // No CHANGELOG.md: build one from the release notes.
                var releases = (await VersionsAsync(row)).Where(v => v.IsRelease).ToList();
                text = releases.Count == 0
                    ? null
                    : string.Join("\n\n", releases.Select(v =>
                        $"## {v.Version}\n*Released {v.Date.ToLocalTime():d MMM yyyy}*\n\n{(string.IsNullOrWhiteSpace(v.Notes) ? "No notes for this version." : v.Notes)}"));
            }
            if (load != _detailsLoad) return;
            SetTabMessage("changelog", text is null ? "No changelog or release notes yet." : null);
            if (text is not null) RendererFor(row).Render(ChangelogContent, text);
        }
        catch (Exception ex) when (ex is HttpRequestException or RateLimitException or InvalidOperationException)
        {
            DataFor(row).Changelog = null;
            _loadedTabs.Remove("changelog");
            if (load == _detailsLoad) SetTabMessage("changelog", LoadError(ex));
        }
    }

    static TextBlock Note(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.Resources["MutedBrush"],
        Margin = new Thickness(0, 10, 0, 0),
    };

    /// <summary>Relative README links go to the file on GitHub; "#section" links go to the repo page.</summary>
    static string ResolveLink(AddonRow row, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto") return href;
        var repo = $"https://github.com/{GitHubClient.Org}/{row.Name}";
        if (href.StartsWith('#')) return repo + href;
        return $"{repo}/blob/{row.Remote.DefaultBranch}/{RelativePath(href)}";
    }

    /// <summary>Relative README images and github.com/…/blob/… image links become raw file URLs.</summary>
    static string ResolveImage(AddonRow row, string src)
    {
        var blob = GitHubBlob().Match(src);
        if (blob.Success) return $"https://raw.githubusercontent.com/{blob.Groups[1].Value}/{blob.Groups[2].Value}";
        if (Uri.TryCreate(src, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return src;
        return GitHubClient.RawUrl(row.Name, row.Remote.DefaultBranch, RelativePath(src));
    }

    static string RelativePath(string path) => path.StartsWith("./") ? path[2..] : path.TrimStart('/');

    [GeneratedRegex(@"^https://github\.com/([^/]+/[^/]+)/(?:blob|raw)/([^?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubBlob();

    // ---- Images ---------------------------------------------------------------------------------------------------

    /// <summary>An image by URL, decoded (to <paramref name="decodeWidth"/> pixels wide for thumbnails); cached.</summary>
    Task<ImageSource?> LoadImageAsync(string url, int decodeWidth = 0)
    {
        var key = $"{decodeWidth}|{url}";
        if (!_images.TryGetValue(key, out var task))
            _images[key] = task = FetchImageAsync(url, decodeWidth);
        return task;
    }

    async Task<ImageSource?> FetchImageAsync(string url, int decodeWidth)
    {
        if (!IsPicture(url)) return null;
        var bytes = await _github.GetImageBytesAsync(url);
        return bytes is null ? null : await Task.Run(() => Decode(bytes, decodeWidth));
    }

    /// <summary>WPF can't draw SVG, which is what badges (shields.io) are.</summary>
    static bool IsPicture(string url) =>
        !url.Split('?')[0].EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
        && !url.Contains("shields.io", StringComparison.OrdinalIgnoreCase)
        && !url.Contains("/badge", StringComparison.OrdinalIgnoreCase);

    static ImageSource? Decode(byte[] bytes, int decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    // ---- Gallery --------------------------------------------------------------------------------------------------

    List<string> _galleryUrls = [];
    int _lightboxIndex;

    /// <summary>README pictures (not badges) then images from screenshots / gallery / media folders.</summary>
    async Task<List<string>> GalleryUrlsAsync(AddonRow row)
    {
        var data = DataFor(row);
        var urls = new List<string>();
        try
        {
            if (await (data.Readme ??= _github.GetReadmeAsync(row.Remote)) is { } readme)
                urls.AddRange(MarkdownRenderer.ImageSources(readme).Select(src => ResolveImage(row, src)).Where(IsPicture));
        }
        catch (Exception ex) when (ex is HttpRequestException or RateLimitException)
        {
            data.Readme = null;
        }
        try
        {
            var paths = await (data.GalleryPaths ??= _github.GetGalleryPathsAsync(row.Remote));
            urls.AddRange(paths.Select(p => GitHubClient.RawUrl(row.Name, row.Remote.DefaultBranch, p)));
        }
        catch (Exception ex) when (ex is HttpRequestException or RateLimitException or InvalidOperationException)
        {
            data.GalleryPaths = null;
        }
        urls.AddRange(GitHubClient.TestGalleryUrls);
        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    async Task CountGalleryAsync(AddonRow row, int load)
    {
        var urls = await GalleryUrlsAsync(row);
        if (load == _detailsLoad && urls.Count > 0) GalleryTab.Content = $"Gallery ({urls.Count})";
    }

    async Task LoadGalleryAsync(AddonRow row, int load)
    {
        SetTabMessage("gallery", "Loading…");
        var urls = await GalleryUrlsAsync(row);
        if (load != _detailsLoad) return;
        _galleryUrls = urls;
        SetTabMessage("gallery", urls.Count == 0
            ? "No screenshots yet.\nPut images in a “screenshots” folder in the addon's repo, or in its README, and they'll show here."
            : null);
        for (var i = 0; i < urls.Count; i++)
        {
            var index = i;
            var image = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var tile = new Border
            {
                Width = 256,
                Height = 144,
                Margin = new Thickness(0, 0, 12, 12),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("LineBrush"),
                Background = (Brush)FindResource("PanelBrush"),
                Cursor = System.Windows.Input.Cursors.Hand,
                ClipToBounds = true,
                Child = image,
                ToolTip = Path.GetFileName(new Uri(urls[i]).AbsolutePath),
            };
            tile.MouseEnter += (_, _) => tile.BorderBrush = (Brush)FindResource("AccentBrush");
            tile.MouseLeave += (_, _) => tile.BorderBrush = (Brush)FindResource("LineBrush");
            tile.MouseLeftButtonUp += (_, _) => ShowLightbox(index);
            GalleryContent.Children.Add(tile);
            _ = SetThumbnailAsync(image, tile, urls[i]);
        }
    }

    async Task SetThumbnailAsync(Image image, Border tile, string url)
    {
        if (await LoadImageAsync(url, decodeWidth: 512) is { } source) image.Source = source;
        else tile.Child = new TextBlock
        {
            Text = "Couldn't load this image",
            Foreground = (Brush)FindResource("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    async void ShowLightbox(int index)
    {
        if (_galleryUrls.Count == 0) return;
        _lightboxIndex = (index % _galleryUrls.Count + _galleryUrls.Count) % _galleryUrls.Count;
        Lightbox.Visibility = Visibility.Visible;
        LightboxCounter.Text = $"{_lightboxIndex + 1} / {_galleryUrls.Count}";
        LightboxImage.Source = null;
        var shown = _lightboxIndex;
        var source = await LoadImageAsync(_galleryUrls[shown]);
        if (shown == _lightboxIndex && Lightbox.Visibility == Visibility.Visible) LightboxImage.Source = source;
    }

    void CloseLightbox()
    {
        Lightbox.Visibility = Visibility.Collapsed;
        LightboxImage.Source = null;
    }

    bool LightboxOpen => Lightbox.Visibility == Visibility.Visible;

    void LightboxPrev_Click(object sender, RoutedEventArgs e) => ShowLightbox(_lightboxIndex - 1);
    void LightboxNext_Click(object sender, RoutedEventArgs e) => ShowLightbox(_lightboxIndex + 1);
    void LightboxClose_Click(object sender, RoutedEventArgs e) => CloseLightbox();
    void LightboxImage_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;

    void Lightbox_BackdropClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource == Lightbox) CloseLightbox();
    }

    // ---- Versions -------------------------------------------------------------------------------------------------

    /// <summary>The version history (with betas when they're shown), fetched once per setting.</summary>
    Task<List<AddonVersion>> VersionsAsync(AddonRow row)
    {
        var data = DataFor(row);
        if (data.Versions is null || data.VersionsWithBeta != _settings.ShowBeta || data.Versions.IsFaulted)
        {
            data.VersionsWithBeta = _settings.ShowBeta;
            data.Versions = _github.GetVersionHistoryAsync(row.Remote, _settings.ShowBeta);
        }
        return data.Versions;
    }

    async Task LoadVersionsAsync(AddonRow row, int load)
    {
        VersionsNote.Text = row.State == AddonState.DevCopy
            ? "This is a Git working copy, so versions can be browsed here but not installed over it. Use Git to move between versions."
            : $"{CurrentFlavor?.DisplayName}  ·  "
              + (_settings.ShowBeta ? "tagged releases and beta versions" : "tagged releases only (File → Show beta versions for untagged ones)")
              + ". Installing a version replaces the one in this game folder; your settings are kept. Click a version to see what changed.";
        if (VersionList.ItemsSource is null) SetTabMessage("versions", "Loading versions…");
        try
        {
            var versions = await VersionsAsync(row);
            if (load != _detailsLoad) return;
            ShowVersionItems(row, versions);
        }
        catch (Exception ex) when (ex is HttpRequestException or RateLimitException or InvalidOperationException)
        {
            _loadedTabs.Remove("versions");
            if (load == _detailsLoad) SetTabMessage("versions", LoadError(ex));
        }
    }

    void ShowVersionItems(AddonRow row, List<AddonVersion> versions)
    {
        var installed = CurrentFlavor is { } flavor ? InstalledVersion(row, flavor) : null;
        VersionList.ItemsSource = versions
            .Select(v => new VersionItem(v, installed is not null && Versions.Compare(v.Version, installed) == 0,
                row.State != AddonState.DevCopy))
            .ToList();
        VersionsTab.Content = $"Versions ({versions.Count})";
        SetTabMessage("versions", versions.Count == 0 ? "No installable versions were found." : null);
    }

    void VersionRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VersionItem item || _detailsRow is not { } row) return;
        if (item.NotesView is null)
        {
            var notes = new StackPanel();
            RendererFor(row).Render(notes, item.NotesText);
            item.NotesView = notes;
        }
        item.ShowNotes = !item.ShowNotes;
    }

    async void VersionInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_working || CurrentFlavor is not { } flavor || _detailsRow is not { } row || row.State == AddonState.DevCopy
            || (sender as FrameworkElement)?.DataContext is not VersionItem item)
            return;
        var version = item.Version;
        SetWorking(true);
        // Choosing anything other than the latest is deliberate: auto-update shouldn't undo it.
        await InstallAsync(row, flavor, version, this,
            pinned: version.Version != row.Latest.Version || version.IsRelease != row.Latest.IsRelease);
        SetWorking(false);
        UpdateSummary();
        UpdateDetailStats(row);
        if (DataFor(row).Versions is { IsCompletedSuccessfully: true } done) ShowVersionItems(row, done.Result);
    }
}
