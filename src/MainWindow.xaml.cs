using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DrangedAddons;

public partial class MainWindow : Window
{
    const string SettingsFile = "settings.json";
    const string ManifestFile = "installed.json";

    readonly GitHubClient _github = new();
    readonly ObservableCollection<AddonRow> _rows = new();
    readonly AppSettings _settings = Storage.Load<AppSettings>(SettingsFile);
    readonly InstallManifest _manifest = Storage.Load<InstallManifest>(ManifestFile);

    List<RemoteAddon> _remote = new();
    readonly Dictionary<string, System.Windows.Media.ImageSource?> _icons = new();
    /// <summary>The game shown; <see cref="_root"/> is its folder.</summary>
    GameDefinition _game = GameDefinition.Wow;
    string? _root;
    string? _loadError;
    DateTime? _lastChecked;
    bool _working;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _updateBannerBackground = UpdateBanner.Background;
        VersionText.Text = $"v{AppUpdater.CurrentVersion}";
        _settings.Migrate();
        _game = GameDefinition.Find(_settings.Game) ?? GameDefinition.Wow;
        LoadRail();
        LoadColumns();
        AddonList.ItemsSource = _rows;
        _view = System.Windows.Data.CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = item => item is AddonRow row && ShowsInList(row);
        if (_settings.Tab == "browse") BrowseTab.IsChecked = true;
        BetaMenu.IsChecked = _settings.ShowBeta;
        UninstallAppMenu.Visibility = AppInstaller.IsRunningInstalled ? Visibility.Visible : Visibility.Collapsed;
        CloseToTrayMenu.IsChecked = _settings.CloseToTray;
        NotificationsMenu.IsChecked = _settings.UpdateNotifications;
        CoopOnlineMenu.IsChecked = _settings.CoopOnline;

        GitHubClient.UseSavedToken(TokenStore.Load());
        _tray = new TrayIcon(ShowFromTray, CheckFromTray, App.ExitApp);
        _backgroundCheck.Tick += async (_, _) => await BackgroundCheckAsync();
        _ = UpdateCheckIntervalAsync();
        Closing += OnClosing;
        Closed += (_, _) => _tray?.Dispose();

        AutoUpdateAddonsMenu.IsChecked = _settings.AutoUpdateAddons;
        AutoUpdateAppMenu.IsChecked = _settings.AutoUpdateApp;
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => _coopPresence?.Dispose();
    }

    bool _initialized;

    /// <summary>First load: game versions, GitHub check, app update check. Runs on show, or straight away when
    /// started hidden in the tray (after an automatic app update).</summary>
    async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        LoadFlavors();
        _ = CheckForAppUpdateAsync();
        await RefreshAsync();
        // First run: start on My Addons if anything is installed here, otherwise on Browse.
        if (_settings.Tab is null && !_rows.Any(r => r.State != AddonState.NotInstalled))
            BrowseTab.IsChecked = true;
        NotifyNewUpdates();
    }

    /// <summary>Starts without showing the window (it lives in the tray until opened).</summary>
    public void StartInTray() => _ = InitializeAsync();

    // ---- Auto-update ----------------------------------------------------------------------------------------------

    void AutoUpdateAddons_Click(object sender, RoutedEventArgs e) => SetAutoUpdateAddons(AutoUpdateAddonsMenu.IsChecked);

    void SetAutoUpdateAddons(bool on)
    {
        if (_settings.AutoUpdateAddons == on) return;
        _settings.AutoUpdateAddons = on;
        Storage.Save(SettingsFile, _settings);
        AutoUpdateAddonsMenu.IsChecked = on;
        if (on) _ = AutoUpdateAddonsAsync();
    }

    void AutoUpdateApp_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoUpdateApp = AutoUpdateAppMenu.IsChecked;
        Storage.Save(SettingsFile, _settings);
    }

    /// <summary>
    /// Installs available updates for installed addons in every version of every game in the side bar. Skips Git
    /// working copies and versions the user picked on purpose (Versions).
    /// </summary>
    async Task AutoUpdateAddonsAsync()
    {
        if (!_settings.AutoUpdateAddons || _working || _remote.Count == 0) return;

        var due = new List<(AddonRow Row, GameFlavor Flavor)>();
        foreach (var (game, flavor) in AllFlavors())
        {
            foreach (var remote in _remote.Where(r => r.Game == game.Id))
            {
                if (remote.LatestFor(_settings.ShowBeta) is not { } latest) continue;
                if (_manifest.For(flavor.AddOnsPath).GetValueOrDefault(remote.Name)?.Pinned == true) continue;
                var row = new AddonRow(remote, latest);
                Evaluate(row, flavor); // DevCopy / NotInstalled / UpToDate are left alone
                if (row.State == AddonState.UpdateAvailable) due.Add((row, flavor));
            }
        }
        if (due.Count == 0) return;

        SetWorking(true);
        var done = new List<string>();
        foreach (var (row, flavor) in due)
        {
            if (await InstallAsync(row, flavor, verb: "Auto-updating", quiet: true))
            {
                done.Add($"{row.Name} {row.Latest.Label} ({flavor.DisplayName})");
                _notified.Add($"{row.Name} {row.Latest.Label}"); // don't also announce it as "available"
            }
        }
        SetWorking(false);
        RebuildRows();
        if (done.Count == 0) return;

        var summary = string.Join("\n", done);
        if (IsVisible && WindowState != WindowState.Minimized)
            ShowBanner($"Updated automatically: {string.Join(", ", done)}", BannerKind.Info, TimeSpan.FromSeconds(12));
        else if (_settings.UpdateNotifications)
            _tray?.Notify(done.Count == 1 ? "Updated automatically" : $"{done.Count} addons updated automatically",
                summary + "\nIf the game is running, /reload to load the new version.");
    }

    // ---- Tabs and search ----------------------------------------------------------------------------------------

    readonly System.ComponentModel.ICollectionView _view;

    bool OnBrowseTab => BrowseTab.IsChecked == true;

    /// <summary>My Addons shows what's installed in this game; Browse shows everything. Both apply the search.</summary>
    bool ShowsInList(AddonRow row)
    {
        if (!OnBrowseTab && row.State == AddonState.NotInstalled) return false;
        if (!PassesFilter(row)) return false;
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;
        var haystack = $"{row.Name} {row.Description} {row.Author}";
        return words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The filter menu's choice (its item's Tag); null shows all. Applies on top of the tab and search.</summary>
    string? _filter;
    string _filterLabel = "";

    bool PassesFilter(AddonRow row) => _filter switch
    {
        "updates" => row.State == AddonState.UpdateAvailable,
        "uptodate" => row.State == AddonState.UpToDate,
        "notinstalled" => row.State == AddonState.NotInstalled,
        "beta" => row.IsBeta,
        "git" => row.State == AddonState.DevCopy,
        _ => true,
    };

    void Filter_Click(object sender, RoutedEventArgs e) => OpenMenuUnder(FilterButton);

    void FilterItem_Click(object sender, RoutedEventArgs e)
    {
        var picked = (MenuItem)sender;
        _filter = picked.Tag as string;
        _filterLabel = picked.Header as string ?? "";
        foreach (var item in FilterButton.ContextMenu.Items.OfType<MenuItem>())
            item.IsChecked = item == picked;
        // Gold while a filter is on, so a shorter list never looks like missing addons.
        if (_filter is null) FilterButton.ClearValue(ForegroundProperty);
        else FilterButton.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        FilterButton.ToolTip = _filter is null ? "Filter" : $"Filter: {_filterLabel}";
        UpdateSummary();
    }

    // ---- Columns ------------------------------------------------------------------------------------------------

    /// <summary>Every optional column, in default order. Name and Action are always shown.</summary>
    readonly ListColumn[] _allColumns =
    [
        new("installed", "Installed", 110),
        new("latest", "Latest", 110),
        new("author", "Author", 170),
        new("released", "Release date", 115),
    ];

    static readonly string[] DefaultColumns = ["installed", "latest", "author"];

    /// <summary>Optional columns shown, in order (the header and every row bind to this).</summary>
    public ObservableCollection<ListColumn> Columns { get; } = new();

    /// <summary>Optional columns not shown: offered in column edit mode.</summary>
    public ObservableCollection<ListColumn> HiddenColumns { get; } = new();

    public static readonly DependencyProperty ColumnEditModeProperty =
        DependencyProperty.Register(nameof(ColumnEditMode), typeof(bool), typeof(MainWindow));

    /// <summary>Column headers show move / hide buttons, and a bar offers hidden columns.</summary>
    public bool ColumnEditMode
    {
        get => (bool)GetValue(ColumnEditModeProperty);
        set
        {
            SetValue(ColumnEditModeProperty, value);
            if (value) ColumnsButton.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
            else ColumnsButton.ClearValue(ForegroundProperty);
        }
    }

    void LoadColumns()
    {
        Columns.Clear();
        HiddenColumns.Clear();
        foreach (var key in _settings.Columns ?? [.. DefaultColumns])
            if (_allColumns.FirstOrDefault(c => c.Key == key) is { } column && !Columns.Contains(column))
                Columns.Add(column);
        foreach (var column in _allColumns.Where(c => !Columns.Contains(c)))
            HiddenColumns.Add(column);
        UpdateMinWidth();
    }

    void SaveColumns()
    {
        _settings.Columns = Columns.Select(c => c.Key).ToList();
        Storage.Save(SettingsFile, _settings);
        UpdateMinWidth();
    }

    /// <summary>
    /// The game side bar, window edges and margins, a row's border, padding, tickbox and icon, the Action column and
    /// the list's scrollbar: everything across a row except the name and the optional columns.
    /// </summary>
    const double RowFixedWidth = 64 + 315;

    /// <summary>Room for the name column: about 24 characters plus a BETA bubble before it's cut short.</summary>
    const double NameMinWidth = 230;

    /// <summary>The window can't get narrower than the list needs to show every chosen column and a fair-length name.</summary>
    void UpdateMinWidth() => MinWidth = Math.Max(720, RowFixedWidth + NameMinWidth + Columns.Sum(c => c.Width));

    static ListColumn ColumnOf(object sender) => (ListColumn)((FrameworkElement)sender).DataContext;

    void Columns_Click(object sender, RoutedEventArgs e) => ColumnEditMode = !ColumnEditMode;
    void ColumnDone_Click(object sender, RoutedEventArgs e) => ColumnEditMode = false;

    // Dragging a header (column edit mode only): a ghost of the header follows the cursor, the columns reorder live
    // to preview where it will land, and the order is saved on release. Esc puts it back.
    ListColumn? _dragColumn;
    List<ListColumn> _dragOrder = [];
    double _dragStartX, _dragGrabOffset;
    bool _dragging, _dropped;

    void HeaderCell_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!ColumnEditMode) return;
        _dragColumn = ColumnOf(sender);
        _dragOrder = Columns.ToList();
        _dragStartX = e.GetPosition(HeaderColumns).X;
        _dragGrabOffset = e.GetPosition((IInputElement)sender).X;
        _dragging = _dropped = false;
        HeaderColumns.CaptureMouse();
        e.Handled = true;
    }

    void HeaderColumns_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragColumn is null) return;
        var x = e.GetPosition(HeaderColumns).X;
        if (!_dragging)
        {
            if (Math.Abs(x - _dragStartX) < SystemParameters.MinimumHorizontalDragDistance) return;
            _dragging = true;
            _dragColumn.IsDragging = true;
            DragGhostText.Text = _dragColumn.Header;
            DragGhost.Width = _dragColumn.Width;
            DragGhost.Height = HeaderColumns.ActualHeight + 14; // the header bar's full height (7px padding each side)
            DragGhost.Visibility = Visibility.Visible;
        }
        Canvas.SetLeft(DragGhost, x - _dragGrabOffset);

        // Where the dragged header's left edge is now decides its slot: it goes before the first other column
        // whose middle it hasn't passed. Measured against the others alone, so it doesn't flip back and forth
        // between columns of different widths.
        var left = x - _dragGrabOffset;
        var others = Columns.Where(c => c != _dragColumn).ToList();
        var slot = 0;
        double at = 0;
        foreach (var other in others)
        {
            if (left < at + other.Width / 2) break;
            at += other.Width;
            slot++;
        }
        var from = Columns.IndexOf(_dragColumn);
        if (slot != from) Columns.Move(from, slot);
    }

    void HeaderColumns_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dropped = true;
            SaveColumns();
        }
        HeaderColumns.ReleaseMouseCapture();
    }

    /// <summary>End of a drag: dropped (already saved), or cancelled (Esc, focus lost), which restores the order.</summary>
    void HeaderColumns_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragging && !_dropped)
            for (var i = 0; i < _dragOrder.Count; i++)
                if (Columns.IndexOf(_dragOrder[i]) is var at && at != i && at >= 0)
                    Columns.Move(at, i);
        if (_dragColumn is not null) _dragColumn.IsDragging = false;
        DragGhost.Visibility = Visibility.Collapsed;
        _dragColumn = null;
        _dragging = false;
    }

    void ColumnHide_Click(object sender, RoutedEventArgs e)
    {
        var column = ColumnOf(sender);
        Columns.Remove(column);
        // Keep the hidden list in default order.
        var at = HiddenColumns.Count(c => Array.IndexOf(_allColumns, c) < Array.IndexOf(_allColumns, column));
        HiddenColumns.Insert(at, column);
        SaveColumns();
    }

    void ColumnShow_Click(object sender, RoutedEventArgs e)
    {
        var column = ColumnOf(sender);
        HiddenColumns.Remove(column);
        Columns.Add(column);
        SaveColumns();
    }

    void ColumnReset_Click(object sender, RoutedEventArgs e)
    {
        _settings.Columns = null;
        Storage.Save(SettingsFile, _settings);
        LoadColumns();
    }

    /// <summary>Keeps the column header's Action label over the row buttons when the list's scrollbar appears.</summary>
    void ListScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The scrollbar's real width (the app's own slim style, not Windows' default).
        var bar = ListScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? ListScroll.ActualWidth - ListScroll.ViewportWidth : 0;
        ListHeader.Margin = new Thickness(0, 0, bar, 4);
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.Tab = OnBrowseTab ? "browse" : "my";
        Storage.Save(SettingsFile, _settings);
        UpdateSummary();
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) UpdateSummary();
    }

    // ---- Tray -------------------------------------------------------------------------------------------------

    TrayIcon? _tray;
    readonly System.Windows.Threading.DispatcherTimer _backgroundCheck = new() { Interval = TimeSpan.FromMinutes(15) };
    readonly HashSet<string> _notified = new();

    /// <summary>
    /// Background checks run every minute when signed in, and every 15 minutes when not. A check that finds the
    /// org repo list unchanged stops there (one request; anonymous 304s still count). The first check of a run,
    /// and any check where a repo actually changed, still looks at each addon.
    /// </summary>
    async Task UpdateCheckIntervalAsync()
    {
        _backgroundCheck.Interval = await GitHubClient.IsSignedInAsync() ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(15);
        _backgroundCheck.Stop();
        _backgroundCheck.Start();
    }

    void GitHubToken_Click(object sender, RoutedEventArgs e) => ShowTokenWindow();

    void Usage_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowTokenWindow();

    /// <summary>
    /// The footer meter: how much of this hour's GitHub allowance is used (gold over 75%, red over 90%).
    /// Reading it doesn't use any of the allowance.
    /// </summary>
    async Task UpdateUsageAsync()
    {
        GitHubAccess? access;
        try
        {
            access = await GitHubClient.GetAccessAsync();
        }
        catch (UnauthorizedAccessException)
        {
            access = null;
        }
        if (access is null || access.Limit <= 0) return;

        var used = Math.Clamp(access.Limit - access.Remaining, 0, access.Limit);
        var share = (double)used / access.Limit;
        UsageFill.Width = 90 * share;
        UsageFill.Background = (System.Windows.Media.Brush)FindResource(share > 0.9 ? "DangerBrush" : share > 0.75 ? "AccentBrush" : "MutedBrush");
        UsageText.Text = $"{used:N0} / {access.Limit:N0}";
        UsagePanel.ToolTip = $"{access.Source}\n{used:N0} of {access.Limit:N0} GitHub checks used this hour; resets at {access.Reset.ToLocalTime():t}.\n"
                             + (access.SignedIn ? "Click to manage your GitHub token." : "Click to add a free GitHub token (5,000 an hour).");
        UsagePanel.Visibility = Visibility.Visible;
    }

    async void ShowTokenWindow()
    {
        var window = new GitHubTokenWindow { Owner = this };
        window.ShowDialog();
        if (!window.Changed) return;
        await UpdateCheckIntervalAsync();
        HideRateLimitBanner();
        await RefreshAsync();
    }

    /// <summary>When GitHub's limit is hit, say so in the bar and offer a token (unless already signed in).</summary>
    async Task ShowRateLimitBannerAsync(RateLimitException ex)
    {
        var resetText = ex.Reset is { } reset ? $" It resets at {reset.ToLocalTime():t}." : "";
        if (await GitHubClient.IsSignedInAsync())
            ShowBanner($"GitHub's hourly limit was reached.{resetText}", BannerKind.Info);
        else
            ShowBanner($"GitHub's limit of 60 checks an hour was reached (you're not signed in).{resetText} " +
                       "A free GitHub token raises it to 5,000.", BannerKind.Token);
        _rateLimitBanner = _bannerToken;
    }

    int _rateLimitBanner = -1;

    void HideRateLimitBanner()
    {
        if (_rateLimitBanner == _bannerToken) UpdateBanner.Visibility = Visibility.Collapsed;
        _rateLimitBanner = -1;
    }

    /// <summary>Closing the window hides it to the tray (unless that's turned off, or the app is really exiting).</summary>
    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.IsExiting || !_settings.CloseToTray) return;
        e.Cancel = true;
        Hide();
        if (!_settings.TrayTipShown && _settings.UpdateNotifications)
        {
            _settings.TrayTipShown = true;
            Storage.Save(SettingsFile, _settings);
            _tray?.Notify("DrangedAddons is still running",
                "It keeps checking for updates in the background. Right-click the tray icon to exit.");
        }
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; // bring it in front of whatever has focus
        Topmost = false;
    }

    async void CheckFromTray()
    {
        ShowFromTray();
        _ = CheckForAppUpdateAsync(interactive: true);
        await RefreshAsync();
        NotifyNewUpdates();
    }

    /// <summary>Periodic: check addons and the app; tell the user about anything new if the window is hidden.</summary>
    async Task BackgroundCheckAsync()
    {
        if (_working) return;
        await RefreshAsync(quiet: true);
        await CheckForAppUpdateAsync();
        NotifyNewUpdates();
    }

    /// <summary>
    /// Shows a Windows notification for updates not mentioned before, but only while the window is hidden;
    /// when it's open they're already on screen.
    /// </summary>
    void NotifyNewUpdates()
    {
        var fresh = _rows.Where(r => r.State == AddonState.UpdateAvailable)
            .Select(r => $"{r.Name} {r.Latest.Label}")
            .Where(_notified.Add)
            .ToList();
        if (_appUpdate is { } app && _notified.Add($"app {app.Version}"))
            fresh.Add($"DrangedAddons {app.Version} (the app itself)");
        if (fresh.Count == 0 || !_settings.UpdateNotifications || (IsVisible && WindowState != WindowState.Minimized))
            return;
        _tray?.Notify(fresh.Count == 1 ? "Update available" : $"{fresh.Count} updates available",
            string.Join("\n", fresh) + "\nClick to open DrangedAddons.");
    }

    void CloseToTray_Click(object sender, RoutedEventArgs e)
    {
        _settings.CloseToTray = CloseToTrayMenu.IsChecked;
        Storage.Save(SettingsFile, _settings);
    }

    void Notifications_Click(object sender, RoutedEventArgs e)
    {
        _settings.UpdateNotifications = NotificationsMenu.IsChecked;
        Storage.Save(SettingsFile, _settings);
    }

    GameFlavor? CurrentFlavor => (FlavorList.SelectedItem as FlavorItem)?.Flavor;

    // Bumped when an install record or the addon list changes. Searching doesn't, so the counts aren't
    // recomputed (a Directory.Exists per addon per version) on every keystroke.
    int _installStamp;
    int _flavorCountStamp = -1;
    string? _flavorCountKey;

    void NoteInstallChange() => _installStamp++;

    string FlavorCountKey() =>
        FlavorList.ItemsSource is IEnumerable<FlavorItem> items
            ? string.Join("|", items.Select(i => i.Flavor.AddOnsPath))
            : "";

    /// <summary>Refreshes the "N installed" counts in the game-version drop-down.</summary>
    void UpdateFlavorCounts()
    {
        var key = FlavorCountKey();
        if (key == _flavorCountKey && _installStamp == _flavorCountStamp) return;
        _flavorCountKey = key;
        _flavorCountStamp = _installStamp;
        if (FlavorList.ItemsSource is not IEnumerable<FlavorItem> items) return;
        foreach (var item in items)
        {
            var installed = _manifest.For(item.Flavor.AddOnsPath);
            item.Installed = _remote.Where(a => a.Game == _game.Id).Count(addon => Directory.Exists(Path.Combine(item.Flavor.AddOnsPath,
                installed.GetValueOrDefault(addon.Name)?.Folders.FirstOrDefault() ?? addon.Name)));
        }
    }

    /// <summary>The current game's folder (saved, else found) and its versions into the picker.</summary>
    void LoadFlavors()
    {
        if (NoGames)
        {
            _root = null;
            FlavorList.ItemsSource = null;
            RebuildRows();
            return;
        }
        var state = _settings.StateFor(_game.Id);
        _root = RootOf(_game);
        // Only WoW has versions to pick; for other games the picker would only ever hold the game itself.
        FlavorList.Visibility = _game.HasVersions ? Visibility.Visible : Visibility.Collapsed;
        ScanGamesItem.Visibility = ScanGamesMenu.Visibility = _game.HasVersions ? Visibility.Visible : Visibility.Collapsed;
        UpdateRailIcon(_game);
        if (_root is null)
        {
            FlavorList.ItemsSource = null;
            RebuildRows();
            UpdateGameLogo();
            return;
        }

        var root = _root;
        var flavors = _game.GetFlavors(root).Select(f => new FlavorItem(f) { Icon = _game.FlavorIcon(root, f) }).ToList();
        FlavorList.ItemsSource = flavors;
        UpdateFlavorCounts();
        var wanted = state.Flavor ?? _game.DefaultFlavor ?? "";
        FlavorList.SelectedItem = flavors.FirstOrDefault(f => f.Flavor.Folder.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                                  ?? flavors.FirstOrDefault();
        if (flavors.Count == 0) RebuildRows();
    }

    /// <summary>
    /// Fetches the addon list from GitHub. A <paramref name="quiet"/> (background) check shows nothing while it
    /// runs and only redraws the list if something actually changed, so a check every minute goes unnoticed.
    /// </summary>
    async Task RefreshAsync(bool quiet = false)
    {
        if (_working) return;
        if (!quiet) SetWorking(true, "Checking GitHub for updates…");
        var before = Signature(_remote);
        var hadError = _loadError is not null;
        try
        {
            _remote = await _github.GetAddonsAsync();
            _loadError = null;
            _lastChecked = DateTime.Now;
            if (!quiet || Signature(_remote) != before)
            {
                foreach (var addon in _remote)
                    _ = RefreshIconAsync(addon);
            }
            HideRateLimitBanner(); // the limit message no longer applies
        }
        catch (RateLimitException ex)
        {
            _loadError = ex.Message;
            await ShowRateLimitBannerAsync(ex);
        }
        catch (Exception ex)
        {
            _loadError = "Couldn't reach GitHub: " + ex.Message;
        }
        finally
        {
            if (!quiet) SetWorking(false);
        }
        if (!quiet || Signature(_remote) != before || hadError != (_loadError is not null))
        {
            NoteInstallChange();
            RebuildRows();
        }
        else
            UpdateSummary(); // just the "checked at" time
        await UpdateUsageAsync();
        if (_loadError is null) await AutoUpdateAddonsAsync();
    }

    /// <summary>Everything about the addon list that would change what's shown.</summary>
    static string Signature(List<RemoteAddon> remote) => string.Join("|", remote.Select(a =>
        $"{a.Name}:{a.LatestRelease?.Version}:{a.LatestBeta?.Commit}:{a.HeadCommit}:{a.Author}:{a.Description}:{string.Join(',', a.Games)}"));

    void RebuildRows()
    {
        UpdatePlayButton();
        _rows.Clear();
        if (CurrentFlavor is { } flavor)
        {
            foreach (var remote in _remote.Where(r => r.Game == _game.Id))
            {
                // Without betas, an addon that has never been tagged has nothing to offer yet.
                if (remote.LatestFor(_settings.ShowBeta) is not { } latest) continue;
                // Only in the game versions it's for - unless it's already in this one (so it can still be managed).
                // Games without versions have nothing to match its .toc's list against.
                if (_game.HasVersions && !remote.IsFor(flavor.Folder) && !IsInstalledIn(remote.Name, flavor)) continue;
                if (!_icons.TryGetValue(remote.Name, out var icon))
                    _icons[remote.Name] = icon = AddonIcons.LoadCached(remote.Name);
                var row = new AddonRow(remote, latest) { Icon = icon, IsSelected = _selected.Contains(remote.Name) };
                row.SelectionChanged += Row_SelectionChanged;
                Evaluate(row, flavor);
                _rows.Add(row);
            }
            foreach (var row in _rows.Where(r => r.State == AddonState.DevCopy))
                _ = CheckGitAsync(row, flavor, fetch: true);
        }

        // Rows are rebuilt on refresh / game change: keep an open details page on the same addon if it's still listed.
        if (_detailsFor is { } name)
        {
            if (_rows.FirstOrDefault(r => r.Name == name) is { } same)
            {
                DetailsPanel.DataContext = same;
                BindDetails(same, newAddon: false, tab: null);
            }
            else ShowList();
        }
        UpdateSummary();
    }

    // ---- List / details navigation ------------------------------------------------------------------------------

    string? _detailsFor;

    void Row_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AddonRow row) ShowDetails(row);
    }

    /// <param name="tab">"overview", "changelog", "gallery" or "versions".</param>
    void ShowDetails(AddonRow row, string? tab = null)
    {
        var newAddon = _detailsFor != row.Name;
        _detailsFor = row.Name;
        DetailsPanel.DataContext = row;
        DetailsPanel.Visibility = Visibility.Visible;
        ListPanel.Visibility = Visibility.Collapsed;
        UpdateBulkBar(); // hidden while on a details page
        BindDetails(row, newAddon, tab);
    }

    void ShowList()
    {
        UnbindDetails();
        _detailsFor = null;
        DetailsPanel.Visibility = Visibility.Collapsed;
        DetailsPanel.DataContext = null;
        ListPanel.Visibility = Visibility.Visible;
        UpdateSummary(); // restores the "no addons" message if the list is empty
    }

    void Back_Click(object sender, RoutedEventArgs e) => ShowList();

    // Esc: back to the list from details, else clear the search. Ctrl+F: search. Mouse back button: back.
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (LightboxOpen && e.Key is System.Windows.Input.Key.Escape or System.Windows.Input.Key.Left or System.Windows.Input.Key.Right)
        {
            if (e.Key == System.Windows.Input.Key.Escape) CloseLightbox();
            else ShowLightbox(_lightboxIndex + (e.Key == System.Windows.Input.Key.Left ? -1 : 1));
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && _detailsFor is not null)
        {
            ShowList();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && _dragColumn is not null)
        {
            HeaderColumns.ReleaseMouseCapture(); // cancels the column drag
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && ColumnEditMode)
        {
            ColumnEditMode = false;
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && _selected.Count > 0)
        {
            ClearSelection();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.F
                 && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            if (_detailsFor is not null) ShowList();
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewMouseDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.XButton1 && _detailsFor is not null)
        {
            ShowList();
            e.Handled = true;
        }
        base.OnPreviewMouseDown(e);
    }

    async Task RefreshIconAsync(RemoteAddon addon)
    {
        var icon = await AddonIcons.RefreshAsync(_github, addon);
        _icons[addon.Name] = icon;
        foreach (var row in _rows.Where(r => r.Name == addon.Name))
            row.Icon = icon;
    }

    bool IsInstalledIn(string name, GameFlavor flavor) =>
        _manifest.For(flavor.AddOnsPath).ContainsKey(name)
        || Directory.Exists(Path.Combine(flavor.AddOnsPath, name));

    string MainFolder(AddonRow row, GameFlavor flavor) =>
        Path.Combine(flavor.AddOnsPath,
            _manifest.For(flavor.AddOnsPath).GetValueOrDefault(row.Name)?.Folders.FirstOrDefault() ?? row.Name);

    async Task CheckGitAsync(AddonRow row, GameFlavor flavor, bool fetch)
    {
        row.SetGit(null, "checking GitHub…");
        try
        {
            var status = await GitRepo.GetStatusAsync(MainFolder(row, flavor), fetch);
            row.SetGit(status, status.Summary);
        }
        catch (GitException ex)
        {
            row.SetGit(null, ex.Message);
        }
        UpdateSummary();
    }

    async Task PullAsync(AddonRow row, GameFlavor flavor)
    {
        StatusText.Text = $"Pulling {row.Name}…";
        row.SetBusy("Pulling…");
        try
        {
            await GitRepo.PullAsync(MainFolder(row, flavor));
        }
        catch (GitException ex)
        {
            DarkDialog.Show(this, $"Git didn't pull {row.Name}, so nothing in the folder was changed",
                $"{ex.Message}\n\n"
                + "This usually means you have local commits that aren't on GitHub yet, or uncommitted edits to the same "
                + "files. Commit, stash or push your work, then try again (or pull from your own Git tool).");
        }
        finally
        {
            row.SetBusy(null);
            Evaluate(row, flavor);
        }
        await CheckGitAsync(row, flavor, fetch: false);
    }

    void Evaluate(AddonRow row, GameFlavor flavor)
    {
        var installed = _manifest.For(flavor.AddOnsPath);
        installed.TryGetValue(row.Name, out var record);
        var mainFolder = Path.Combine(flavor.AddOnsPath, record?.Folders.FirstOrDefault() ?? row.Name);

        if (!Directory.Exists(mainFolder))
        {
            // Deleted outside the app: forget it.
            if (record is not null && installed.Remove(row.Name))
            {
                Storage.Save(ManifestFile, _manifest);
                NoteInstallChange();
            }
            row.Update(AddonState.NotInstalled, null);
            return;
        }

        if (AddonInstaller.IsDevCopy(mainFolder))
        {
            // BETA guess until Git answers (CheckGitAsync): a local version with no release yet is untagged code.
            var local = AddonInstaller.ReadTocVersion(mainFolder);
            var untagged = local is not null
                           && (row.Remote.LatestRelease is not { } latestRelease || Versions.Compare(local, latestRelease.Version) > 0);
            row.Update(AddonState.DevCopy, local, installedIsBeta: untagged);
            return;
        }

        // Trust our own record first; fall back to the .toc for copies installed by hand.
        var version = record?.Version ?? AddonInstaller.ReadTocVersion(mainFolder);
        var upToDate = version is not null && Versions.Compare(version, row.LatestVersion) >= 0;

        // A beta whose version has since been published as a release is that release now: stop calling it a beta.
        if (record is { IsBeta: true } && version is not null
            && row.Remote.LatestRelease is { } release && Versions.Compare(release.Version, version) == 0)
        {
            record.IsBeta = false;
            record.Commit = null;
            Storage.Save(ManifestFile, _manifest);
            NoteInstallChange();
        }
        row.Update(upToDate ? AddonState.UpToDate : AddonState.UpdateAvailable, version ?? "unknown", record?.IsBeta == true);
    }

    /// <summary>The installed version in this game folder, or null if it isn't installed.</summary>
    string? InstalledVersion(AddonRow row, GameFlavor flavor) =>
        row.State == AddonState.NotInstalled
            ? null
            : _manifest.For(flavor.AddOnsPath).GetValueOrDefault(row.Name)?.Version
              ?? AddonInstaller.ReadTocVersion(Path.Combine(flavor.AddOnsPath, row.Name));

    void UpdateSummary()
    {
        var updates = _rows.Count(r => r.State == AddonState.UpdateAvailable);
        UpdateAllButton.IsEnabled = !_working && updates > 0;
        UpdateAllButton.Content = updates > 0 ? $"Update all ({updates})" : "Update all";
        if (_tray is not null)
            _tray.Tooltip = updates > 0 ? $"DrangedAddons — {updates} update{(updates == 1 ? "" : "s")} available" : "DrangedAddons";
        OpenFolderItem.IsEnabled = OpenFolderMenu.IsEnabled = CurrentFlavor is not null;
        UpdateFolderTooltips();
        UpdateGameLogo();
        UpdateFlavorCounts();

        // Tabs and search: re-filter (installs/uninstalls move rows between tabs) and show the counts.
        _view.Refresh();
        var shown = _rows.Count(ShowsInList);
        MyAddonsTab.Content = $"My Addons ({_rows.Count(r => r.State != AddonState.NotInstalled)})";
        BrowseTab.Content = $"Browse ({_rows.Count})";
        UpdateBulkBar();

        var query = SearchBox.Text.Trim();
        EmptyText.Text = _root is null ? $"Pick your {_game.Name} folder to get started (⋯ → Change game folder…)."
            : _loadError is not null ? _loadError
            : _rows.Count == 0 && _remote.Count > 0 && !_remote.Any(r => r.Game == _game.Id)
                ? $"No {_game.Name} addons have been published yet."
            : _rows.Count == 0 && _remote.Any(r => r.Game == _game.Id)
                ? $"None of the {_game.Name} addons support {CurrentFlavor?.DisplayName ?? "this version"} yet."
            : _rows.Count == 0 ?"No addons with a packaged release were found in the DrangedAddons organisation."
            : query.Length > 0 ? $"No addons match “{query}”."
            : _filter is not null ? $"No addons match the filter “{_filterLabel}”."
            : $"Nothing installed for {CurrentFlavor?.DisplayName ?? "this game"} yet. Find addons in Browse.";
        EmptyText.Visibility = shown == 0 && !_working && _detailsFor is null ? Visibility.Visible : Visibility.Collapsed;

        if (_working) return;
        if (_loadError is not null)
            StatusText.Text = _loadError;
        else if (_lastChecked is { } checkedAt)
        {
            var pulls = _rows.Count(r => r.CanPull);
            StatusText.Text = $"{_rows.Count} addon{(_rows.Count == 1 ? "" : "s")}  ·  "
                              + $"{updates} update{(updates == 1 ? "" : "s")} available  ·  "
                              + (pulls > 0 ? $"{pulls} to pull  ·  " : "")
                              + $"checked {checkedAt:t}";
            RefreshButton.ToolTip = $"Check for updates (last checked {checkedAt:t})";
        }
    }

    void SetWorking(bool working, string? status = null)
    {
        _working = working;
        RefreshButton.IsEnabled = !working;
        ChangeFolderItem.IsEnabled = !working;
        ChangeFolderMenu.IsEnabled = !working;
        ScanGamesItem.IsEnabled = ScanGamesMenu.IsEnabled = !working;
        FlavorList.IsEnabled = !working;
        if (working) UpdateAllButton.IsEnabled = false;
        if (status is not null) StatusText.Text = status;
    }

    /// <summary>"Downloading 45%" (or "Downloading 1.2 MB" when the size isn't known), then "Installing…".</summary>
    static string DownloadLabel(double shown, DownloadProgress? real) =>
        shown >= 1 ? "Installing…"
        : real is { Total: null or 0, Bytes: > 0 } r ? $"Downloading {r.Text}"
        : $"Downloading {(int)(shown * 100)}%";

    /// <summary>
    /// Installs <paramref name="version"/> (default: the row's latest). Returns false if it failed.
    /// <paramref name="pinned"/>: the user chose this version on purpose (Versions…), so auto-update leaves it alone.
    /// <paramref name="quiet"/>: no error dialog (auto-update reports in the status line instead).
    /// </summary>
    async Task<bool> InstallAsync(AddonRow row, GameFlavor flavor, AddonVersion? version = null, Window? owner = null,
        string? verb = null, bool pinned = false, bool quiet = false, List<string>? errors = null)
    {
        version ??= row.Latest;
        verb ??= row.State == AddonState.NotInstalled ? "Installing" : "Updating";
        StatusText.Text = $"{verb} {row.Name} {version.Label}…";
        row.SetBusy($"{verb}…");
        try
        {
            // The action button fills as the download goes ("Downloading 45%"): quickly, but never ahead of the
            // real download, and an instant one still visibly fills before the row moves on.
            var pacer = new ProgressPacer((shown, real) => row.SetProgress(shown, DownloadLabel(shown, real)));
            List<string> folders;
            try
            {
                folders = await AddonInstaller.InstallAsync(_github, row.Remote, version, flavor.AddOnsPath,
                    new Progress<DownloadProgress>(pacer.Report));
            }
            catch
            {
                pacer.Stop();
                throw;
            }
            pacer.Complete();
            await pacer.WhenFullAsync();
            _manifest.For(flavor.AddOnsPath)[row.Name] = new InstalledRecord
            {
                Version = version.Version,
                IsBeta = !version.IsRelease,
                Commit = version.Commit,
                Folders = folders,
                InstalledAt = DateTime.Now,
                Pinned = pinned,
            };
            Storage.Save(ManifestFile, _manifest);
            NoteInstallChange();
            return true;
        }
        catch (Exception ex)
        {
            if (quiet)
            {
                errors?.Add($"{row.Name}: {ex.Message}");
                StatusText.Text = $"{verb} {row.Name} failed: {ex.Message}";
            }
            else DarkDialog.Show(owner ?? this, $"Couldn't install {row.Name} {version.Label}", ex.Message);
            return false;
        }
        finally
        {
            row.SetBusy(null);
            Evaluate(row, flavor);
        }
    }

    void Beta_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowBeta = BetaMenu.IsChecked;
        Storage.Save(SettingsFile, _settings);
        RebuildRows();
    }

    async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_working || CurrentFlavor is not { } flavor || (sender as FrameworkElement)?.DataContext is not AddonRow row)
            return;
        SetWorking(true);
        if (row.CanPull)
            await PullAsync(row, flavor);
        else
            await InstallAsync(row, flavor);
        SetWorking(false);
        UpdateSummary();
    }

    async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_working || CurrentFlavor is not { } flavor) return;
        SetWorking(true);
        foreach (var row in _rows.Where(r => r.State == AddonState.UpdateAvailable).ToList())
            await InstallAsync(row, flavor);
        SetWorking(false);
        UpdateSummary();
    }

    /// <summary>
    /// Deletes an addon from this game: asks to confirm, then whether to delete its saved settings too
    /// (default no). Everything goes to the Recycle Bin.
    /// </summary>
    void DeleteAddon(AddonRow row, GameFlavor flavor) => DeleteAddons([row], flavor);

    /// <summary>
    /// Deletes one or more addons from this game: one confirmation, then one "delete their saved settings too?"
    /// question (default no). Everything goes to the Recycle Bin. Git working copies are skipped.
    /// </summary>
    void DeleteAddons(IReadOnlyList<AddonRow> rows, GameFlavor flavor)
    {
        rows = rows.Where(r => r.CanUninstall).ToList();
        if (rows.Count == 0) return;
        var installed = _manifest.For(flavor.AddOnsPath);
        var foldersOf = rows.ToDictionary(r => r,
            r => installed.TryGetValue(r.Name, out var record) ? record.Folders : new List<string> { r.Name });
        var folders = foldersOf.Values.SelectMany(f => f).ToList();
        var what = rows.Count == 1 ? rows[0].Name : $"{rows.Count} addons";
        if (!DarkDialog.Show(this, $"Delete {what} from {flavor.DisplayName}?",
                "These folders will be moved to the Recycle Bin:\n"
                + string.Join("\n", folders.Select(f => "  • " + f)),
                primary: "Delete", secondary: "Cancel", danger: true))
            return;

        var settingsFiles = _game.FindSavedVariables(flavor.AddOnsPath, folders);
        var whose = rows.Count == 1 ? $"{rows[0].Name}'s" : "their";
        var deleteSettings = settingsFiles.Count > 0 && DarkDialog.Show(this, $"Also delete {whose} saved settings?",
            $"{settingsFiles.Count} settings file{(settingsFiles.Count == 1 ? "" : "s")} "
            + "would be moved to the Recycle Bin. Keep them if you might reinstall later.\n\n"
            + $"If {_game.Name} is running, close it first, or it will write them back when you log out.",
            primary: "Delete settings", secondary: "Keep settings", danger: true, defaultSecondary: true);

        foreach (var row in rows)
        {
            try
            {
                AddonInstaller.Uninstall(flavor.AddOnsPath, foldersOf[row]);
                installed.Remove(row.Name);
                Storage.Save(ManifestFile, _manifest);
                if (deleteSettings)
                    AddonInstaller.RecycleFiles(SettingsFor(settingsFiles, foldersOf[row]));
            }
            catch (Exception ex)
            {
                DarkDialog.Show(this, $"Couldn't delete {row.Name}", ex.Message);
            }
            Evaluate(row, flavor);
        }
        NoteInstallChange();
        UpdateSummary();

        // Names the saved-settings file belongs to: "Addon.lua" and "Addon.lua.bak" are both Addon.
        static string SavedSettingsName(string path)
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".lua.bak", StringComparison.OrdinalIgnoreCase)) return name[..^".lua.bak".Length];
            if (name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) return name[..^".lua".Length];
            return Path.GetFileNameWithoutExtension(name);
        }

        static IEnumerable<string> SettingsFor(List<string> files, IEnumerable<string> addonFolders)
        {
            var names = addonFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return files.Where(f => names.Contains(SavedSettingsName(f)));
        }
    }

    // ---- Multi-select and bulk actions ----------------------------------------------------------------------------

    // Names of ticked addons; kept across list rebuilds (background checks), cleared when the game version changes.
    readonly HashSet<string> _selected = new();

    void Row_SelectionChanged(object? sender, EventArgs e)
    {
        if (sender is not AddonRow row) return;
        if (row.IsSelected) _selected.Add(row.Name);
        else _selected.Remove(row.Name);
        UpdateBulkBar();
    }

    IEnumerable<AddonRow> SelectedRows => _rows.Where(r => r.IsSelected);

    /// <summary>The select-all box ticks or clears the rows currently shown (this tab and search).</summary>
    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var select = SelectAllCheck.IsChecked == true;
        foreach (var row in _rows.Where(ShowsInList).ToList())
            row.IsSelected = select;
        UpdateBulkBar();
    }

    void ClearSelection()
    {
        foreach (var row in _rows.Where(r => r.IsSelected).ToList())
            row.IsSelected = false;
        _selected.Clear();
        UpdateBulkBar();
    }

    void BulkClear_Click(object sender, RoutedEventArgs e) => ClearSelection();

    /// <summary>Counts per action, the select-all box state, and showing / hiding the bar.</summary>
    void UpdateBulkBar()
    {
        var selected = SelectedRows.ToList();
        var shown = _rows.Where(ShowsInList).ToList();
        var shownSelected = shown.Count(r => r.IsSelected);
        SelectAllCheck.IsChecked = shownSelected == 0 ? false : shownSelected == shown.Count ? true : null;
        SelectAllCheck.IsEnabled = shown.Count > 0;

        void Set(Button button, string label, int count)
        {
            button.Content = $"{label} ({count})";
            button.IsEnabled = count > 0 && !_working;
        }
        BulkCountText.Text = selected.Count.ToString();
        Set(BulkUpdateButton, "Update", selected.Count(r => r.State == AddonState.UpdateAvailable));
        Set(BulkInstallButton, "Install", selected.Count(r => r.State == AddonState.NotInstalled));
        Set(BulkReinstallButton, "Reinstall", selected.Count(r => r.CanUninstall));
        Set(BulkDeleteButton, "Delete", selected.Count(r => r.CanUninstall));

        var show = selected.Count > 0 && _detailsFor is null;
        // Room under the last row so the bar never covers it.
        AddonList.Margin = new Thickness(0, 0, 0, show ? 64 : 0);
        if (show && BulkBar.Visibility != Visibility.Visible)
        {
            BulkBar.Visibility = Visibility.Visible;
            var ease = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            BulkBarShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            BulkBar.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        else if (!show)
        {
            BulkBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Runs <paramref name="action"/> on each ticked addon it applies to, one at a time, then clears the selection.</summary>
    async Task BulkAsync(Func<AddonRow, bool> applies, Func<AddonRow, GameFlavor, Task> action, string verb)
    {
        if (_working || CurrentFlavor is not { } flavor) return;
        var rows = SelectedRows.Where(applies).ToList();
        if (rows.Count == 0) return;
        SetWorking(true, $"{verb} {rows.Count} addon{(rows.Count == 1 ? "" : "s")}…");
        try
        {
            foreach (var row in rows)
                await action(row, flavor);
        }
        finally
        {
            SetWorking(false);
            ClearSelection();
            UpdateSummary();
        }
    }

    async void BulkUpdate_Click(object sender, RoutedEventArgs e) =>
        await BulkAsync(r => r.State == AddonState.UpdateAvailable, (r, f) => InstallAsync(r, f), "Updating");

    async void BulkInstall_Click(object sender, RoutedEventArgs e) =>
        await BulkAsync(r => r.State == AddonState.NotInstalled, (r, f) => InstallAsync(r, f), "Installing");

    async void BulkReinstall_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        await BulkAsync(r => r.CanUninstall, (r, f) => ReinstallAsync(r, f, quiet: true, errors: errors), "Reinstalling");
        if (errors.Count == 0) return;
        DarkDialog.Show(this, errors.Count == 1 ? "Couldn't reinstall an addon" : $"Couldn't reinstall {errors.Count} addons",
            string.Join("\n\n", errors));
    }

    void BulkDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_working || CurrentFlavor is not { } flavor) return;
        DeleteAddons(SelectedRows.Where(r => r.CanUninstall).ToList(), flavor);
        ClearSelection();
    }

    // ---- Row right-click menu -----------------------------------------------------------------------------------

    static AddonRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as AddonRow;

    void RowDetails_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) ShowDetails(row);
    }

    /// <summary>
    /// Puts a fresh copy of the installed version back (same release, or the same beta commit), keeping settings.
    /// If the installed version can't be found among the published ones, the latest is installed instead.
    /// </summary>
    async void RowReinstall_Click(object sender, RoutedEventArgs e)
    {
        if (_working || RowOf(sender) is not { } row || CurrentFlavor is not { } flavor || !row.CanUninstall) return;
        SetWorking(true, $"Reinstalling {row.Name}…");
        try
        {
            await ReinstallAsync(row, flavor);
        }
        finally
        {
            SetWorking(false);
            UpdateSummary();
        }
    }

    /// <summary>
    /// Puts a fresh copy of the installed version back (same release, or the same beta commit), keeping settings.
    /// If the installed version can't be found among the published ones, the latest is installed instead.
    /// </summary>
    async Task<bool> ReinstallAsync(AddonRow row, GameFlavor flavor, bool quiet = false, List<string>? errors = null)
    {
        try
        {
            var record = _manifest.For(flavor.AddOnsPath).GetValueOrDefault(row.Name);
            var installed = record?.Version ?? row.InstalledVersion;
            var target = installed is null ? row.Latest : await VersionForReinstallAsync(row, record, installed);
            return await InstallAsync(row, flavor, target, verb: "Reinstalling", quiet: quiet, errors: errors);
        }
        catch (Exception ex)
        {
            if (quiet)
            {
                errors?.Add($"{row.Name}: {ex.Message}");
                StatusText.Text = $"Reinstalling {row.Name} failed: {ex.Message}";
            }
            else DarkDialog.Show(this, $"Couldn't reinstall {row.Name}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The published build matching what's installed. A tagged release is looked up in the release list.
    /// A beta walks .toc commits newest-first and stops when its commit (or version) shows up.
    /// </summary>
    async Task<AddonVersion> VersionForReinstallAsync(AddonRow row, InstalledRecord? record, string installed)
    {
        var beta = record?.IsBeta == true || record?.Commit is not null;
        var sameAsLatest = Versions.Compare(installed, row.Latest.Version) == 0 && row.Latest.IsRelease == !beta;
        if (sameAsLatest) return row.Latest;
        if (!beta)
            return await _github.FindReleaseAsync(row.Remote, installed) ?? row.Latest;

        var history = await _github.StartVersionHistoryAsync(row.Remote, includeBeta: true);
        while (true)
        {
            if (MatchReinstall(history, record, installed, allowFallback: history.Complete) is { } match)
            {
                await Task.Run(_github.SaveTocCache);
                return match;
            }
            if (history.Complete) break;
            await _github.ReadMoreVersionsAsync(history, 20);
        }
        await Task.Run(_github.SaveTocCache);
        return row.Latest;
    }

    static AddonVersion? MatchReinstall(VersionHistory history, InstalledRecord? record, string installed, bool allowFallback)
    {
        if (record?.Commit is { } sha && history.Versions.FirstOrDefault(v => v.Commit == sha) is { } byCommit)
            return byCommit;
        if (record?.Commit is not null && !allowFallback) return null;
        return history.Versions.FirstOrDefault(v => Versions.Compare(v.Version, installed) == 0 && v.IsRelease)
               ?? history.Versions.FirstOrDefault(v => Versions.Compare(v.Version, installed) == 0);
    }

    void RowBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || CurrentFlavor is not { } flavor) return;
        var folder = MainFolder(row, flavor);
        if (Directory.Exists(folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    void RowChangelog_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) ShowDetails(row, "changelog");
    }

    void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!_working && RowOf(sender) is { } row && CurrentFlavor is { } flavor) DeleteAddon(row, flavor);
    }

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _ = CheckForAppUpdateAsync(interactive: true);
        await RefreshAsync();
    }

    AppRelease? _appUpdate;
    int _bannerToken;
    readonly System.Windows.Media.Brush _updateBannerBackground;

    /// <summary>
    /// Shows the bar under the header: gold with "Restart to update" for an available update, or a plain status
    /// ("checking…", "you're on the latest version") that can hide itself after a while.
    /// </summary>
    enum BannerKind { Info, Update, Token }

    void ShowBanner(string text, bool isUpdate, TimeSpan? hideAfter = null) =>
        ShowBanner(text, isUpdate ? BannerKind.Update : BannerKind.Info, hideAfter);

    void ShowBanner(string text, BannerKind kind, TimeSpan? hideAfter = null)
    {
        var token = ++_bannerToken;
        var isUpdate = kind == BannerKind.Update;
        var standsOut = kind != BannerKind.Info;
        UpdateBannerText.Text = text;
        UpdateBannerButtons.Visibility = standsOut ? Visibility.Visible : Visibility.Collapsed;
        WhatsNewButton.Visibility = isUpdate ? Visibility.Visible : Visibility.Collapsed;
        SelfUpdateButton.Visibility = isUpdate && AppUpdater.CanSelfUpdate ? Visibility.Visible : Visibility.Collapsed;
        TokenButton.Visibility = kind == BannerKind.Token ? Visibility.Visible : Visibility.Collapsed;
        // Gold for an update waiting, red for a problem (GitHub's limit), plain for status messages.
        UpdateBanner.Background = kind switch
        {
            BannerKind.Update => _updateBannerBackground,
            BannerKind.Token => (System.Windows.Media.Brush)FindResource("AlertBackgroundBrush"),
            _ => (System.Windows.Media.Brush)FindResource("PanelBrush"),
        };
        UpdateBanner.BorderBrush = (System.Windows.Media.Brush)FindResource(kind switch
        {
            BannerKind.Update => "AccentBrush",
            BannerKind.Token => "DangerBrush",
            _ => "LineBrush",
        });
        UpdateBannerText.FontWeight = standsOut ? FontWeights.SemiBold : FontWeights.Normal;
        UpdateBanner.Visibility = Visibility.Visible;
        if (hideAfter is { } delay)
            _ = HideBannerLaterAsync(token, delay);
    }

    async Task HideBannerLaterAsync(int token, TimeSpan delay)
    {
        await Task.Delay(delay);
        if (token == _bannerToken) UpdateBanner.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the update bar when a newer app release exists. When <paramref name="interactive"/> (Help menu or
    /// "Check for updates"), the bar also says it's checking and what it found, then hides again if there's nothing.
    /// </summary>
    async Task CheckForAppUpdateAsync(bool interactive = false)
    {
        await AppUpdater.CleanUpAsync();
        if (!AppUpdater.CanSelfUpdate && !interactive) return;
        if (interactive && _appUpdate is null)
            ShowBanner("Checking for app updates…", isUpdate: false);
        try
        {
            _appUpdate = await AppUpdater.CheckAsync(_github);
        }
        catch (Exception ex)
        {
            // Not being able to check for an app update shouldn't get in the way of addons, unless asked.
            if (interactive) ShowBanner($"Couldn't check for app updates: {ex.Message}", isUpdate: false, TimeSpan.FromSeconds(8));
            return;
        }

        if (_appUpdate is null)
        {
            if (interactive)
                ShowBanner($"You're on the latest version of DrangedAddons ({AppUpdater.CurrentVersion}).", isUpdate: false,
                    TimeSpan.FromSeconds(4));
            return;
        }

        ShowBanner(AppUpdater.CanSelfUpdate
            ? $"DrangedAddons {_appUpdate.Version} is available (you have {AppUpdater.CurrentVersion})."
            : $"DrangedAddons {_appUpdate.Version} is available. This copy can't update itself: download it from the releases page.",
            isUpdate: true);

        // Auto-update the app whether it's open or in the tray (it reopens the same way), but not in the middle of
        // something: an install running or another window (Versions…, GitHub token, a dialog) open. The next check
        // tries again.
        if (_settings.AutoUpdateApp && AppUpdater.CanSelfUpdate && !_working && OwnedWindows.Count == 0)
            await ApplySelfUpdateAsync(backToTray: !IsVisible);
    }

    async void SelfUpdate_Click(object sender, RoutedEventArgs e) => await ApplySelfUpdateAsync();

    bool _selfUpdating;

    async Task ApplySelfUpdateAsync(bool backToTray = false)
    {
        if (_appUpdate is null || _selfUpdating) return; // a second click while updating is ignored
        _selfUpdating = true;
        // Let a check or install that's already running finish first (clicking during the start-up check used to
        // do nothing), then update.
        if (_working)
        {
            SelfUpdateButton.Content = "Waiting…";
            while (_working) await Task.Delay(200);
        }
        SetWorking(true, $"Downloading DrangedAddons {_appUpdate.Version}…");
        // Stays enabled (a disabled button would dim the progress fill); a second click is ignored while working.
        SelfUpdateButton.Background = (System.Windows.Media.Brush)FindResource("Panel2Brush");
        SelfUpdateButton.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
        var pacer = new ProgressPacer((shown, real) =>
        {
            ProgressFill.SetValue(SelfUpdateButton, shown);
            SelfUpdateButton.Content = shown >= 1 ? "Restarting…" : DownloadLabel(shown, real);
        });
        try
        {
            await AppUpdater.ApplyAsync(_github, _appUpdate, startInTray: backToTray,
                progress: new Progress<DownloadProgress>(pacer.Report));
            pacer.Complete();
            await pacer.WhenFullAsync();
            App.ExitApp();
        }
        catch (Exception ex)
        {
            pacer.Stop();
            if (backToTray) StatusText.Text = $"Couldn't update DrangedAddons automatically: {ex.Message}";
            else DarkDialog.Show(this, "Couldn't update DrangedAddons", ex.Message);
            ProgressFill.SetValue(SelfUpdateButton, null);
            SelfUpdateButton.ClearValue(BackgroundProperty);
            SelfUpdateButton.ClearValue(ForegroundProperty);
            SelfUpdateButton.Content = "Restart to update";
            _selfUpdating = false;
            SetWorking(false);
            UpdateSummary();
        }
    }

    void WhatsNew_Click(object sender, RoutedEventArgs e)
    {
        if (_appUpdate is not null)
            Process.Start(new ProcessStartInfo(_appUpdate.Url) { UseShellExecute = true });
    }

    void Flavor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CurrentFlavor is not { } flavor) return;
        if (e.RemovedItems.Count > 0) _selected.Clear(); // a selection belongs to one game version
        var state = _settings.StateFor(_game.Id);
        state.Root = _root;
        state.Flavor = flavor.Folder;
        Storage.Save(SettingsFile, _settings);
        UpdateRailIcon(_game);
        RebuildRows();
    }

    /// <summary>How Play would start the picked game version; null if there's nothing to start.</summary>
    GameLaunch? CurrentLaunch => !NoGames && _root is { } root && CurrentFlavor is { } flavor ? _game.GetLaunch(root, flavor) : null;

    void UpdatePlayButton()
    {
        var launch = CurrentLaunch;
        var what = _game.HasVersions && CurrentFlavor is { } flavor ? $"{_game.Name} {flavor.DisplayName}" : _game.Name;
        PlayButton.Visibility = NoGames ? Visibility.Collapsed : Visibility.Visible;
        PlayButton.IsEnabled = launch is not null;
        PlayButton.ToolTip = launch is null ? $"{what} isn't installed in the game folder"
            : _game.HasDirectCoop ? $"Play {what}, or start direct co-op"
            : $"Start {what} {launch.How}";
        ContextMenuService.SetIsEnabled(PlayButton, _game.HasDirectCoop); // no right-click menu for plain Play
    }

    void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_game.HasDirectCoop) OpenMenuUnder(PlayButton);
        else PlayGame();
    }

    void PlayItem_Click(object sender, RoutedEventArgs e) => PlayGame();

    void PlayGame()
    {
        if (CurrentLaunch is not { } launch) return;
        try
        {
            Process.Start(new ProcessStartInfo(launch.Exe, launch.Arguments)
            {
                WorkingDirectory = Path.GetDirectoryName(launch.Exe)!,
                UseShellExecute = true,
            })?.Dispose();
            StatusText.Text = $"Starting {(_game.HasVersions ? CurrentFlavor?.DisplayName : _game.Name)} {launch.How}…";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DarkDialog.Show(this, $"Couldn't start {_game.Name}", ex.Message);
        }
    }

    void ChangeFolder_Click(object sender, RoutedEventArgs e) => PickGameFolder();

    /// <summary>Asks for the current game's folder; false if cancelled or it isn't one.</summary>
    bool PickGameFolder()
    {
        if (NoGames) return false;
        var dialog = new OpenFolderDialog { Title = $"Select your {_game.Name} folder" };
        if (_root is not null) dialog.InitialDirectory = _root;
        if (dialog.ShowDialog(this) != true) return false;

        var root = _game.NormalizeRoot(dialog.FolderName);
        if (root is null)
        {
            DarkDialog.Show(this, $"That isn't a {_game.Name} folder", _game.HasVersions
                ? "Pick the folder that contains _retail_, _classic_ and so on."
                : $"Pick the folder that {_game.Name} is installed in.");
            return false;
        }
        _settings.StateFor(_game.Id).Root = root;
        Storage.Save(SettingsFile, _settings);
        LoadFlavors();
        return true;
    }

    /// <summary>
    /// Re-reads the game versions in the current game's folder (e.g. after installing another one in Battle.net),
    /// keeping the selected one, and says what changed in the bar.
    /// </summary>
    void ScanGames_Click(object sender, RoutedEventArgs e)
    {
        if (_working) return;
        var before = Folders(FlavorList.ItemsSource);
        LoadFlavors();
        var after = Folders(FlavorList.ItemsSource);

        string Names(IEnumerable<string> folders) =>
            string.Join(", ", folders.Select(f => (FlavorList.ItemsSource as IEnumerable<FlavorItem>)?
                .FirstOrDefault(i => i.Flavor.Folder == f)?.Flavor.DisplayName ?? f.Trim('_')));
        var added = after.Except(before).ToList();
        var removed = before.Except(after).ToList();
        var message = _root is null ? $"No {_game.Name} folder found. Use “Change game folder…” to pick it."
            : added.Count > 0 ? $"Found {added.Count} new game version{(added.Count == 1 ? "" : "s")}: {Names(added)}."
            : removed.Count > 0 ? $"{removed.Count} game version{(removed.Count == 1 ? " is" : "s are")} no longer in the {_game.Name} folder: {string.Join(", ", removed.Select(r => r.Trim('_')))}."
            : $"No new game versions found ({after.Count} in {_root}).";
        ShowBanner(message, BannerKind.Info, TimeSpan.FromSeconds(6));
    }

    /// <summary>The game folder isn't shown in the bar (no room); hovering these menu items shows the paths instead.</summary>
    void UpdateFolderTooltips()
    {
        ChangeFolderItem.ToolTip = _root is null
            ? $"No {_game.Name} folder found yet: pick it here."
            : $"Current {_game.Name} folder:\n{_root}";
        ScanGamesItem.ToolTip = _root is null
            ? $"Pick your {_game.Name} folder first (Change game folder…)."
            : $"Look in {_root} again for game versions\n(e.g. one you've just installed).";
        OpenFolderItem.ToolTip = CurrentFlavor is { } flavor ? flavor.AddOnsPath : "Pick a game version first.";
    }

    void GameMenu_Click(object sender, RoutedEventArgs e) => OpenMenuUnder(GameMenuButton);

    /// <summary>Opens a button's menu under it, right edges lined up.</summary>
    static void OpenMenuUnder(Button button)
    {
        var menu = button.ContextMenu;
        // While the menu is open the button ignores clicks, so clicking it just closes the menu (a toggle) instead of
        // closing it and opening it again.
        button.IsHitTestVisible = false;
        menu.Closed -= MenuClosed;
        menu.Closed += MenuClosed;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popup, target, _) =>
            [new(new System.Windows.Point(target.Width - popup.Width, target.Height + 4), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)];
        menu.IsOpen = true;
    }

    static void MenuClosed(object sender, RoutedEventArgs e)
    {
        if (((ContextMenu)sender).PlacementTarget is UIElement button) button.IsHitTestVisible = true;
    }

    string? _logoKey;
    readonly Dictionary<string, System.Windows.Media.ImageSource> _logoImages = new();

    System.Windows.Media.ImageSource LogoImage(string name)
    {
        if (_logoImages.TryGetValue(name, out var image)) return image;
        var bitmap = new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/logos/{name}.png"));
        bitmap.Freeze();
        return _logoImages[name] = bitmap;
    }

    /// <summary>
    /// Top-left logo for the selected game version: the official logo from logos\ (PTR/Beta reuse their game's logo
    /// with a tag; Classic Era uses Hardcore's, its main use now), else a drawn wordmark in that version's colour.
    /// </summary>
    void UpdateGameLogo()
    {
        var logoKey = $"{_game.Id}|{CurrentFlavor?.Folder ?? ""}";
        if (logoKey == _logoKey) return;
        _logoKey = logoKey;
        if (_game != GameDefinition.Wow)
        {
            ShowWordmark(_game.Name, "#F08A3C");
            return;
        }
        GameLogoLine1.Text = "WORLD of";
        GameLogoLine1.Visibility = Visibility.Visible;
        GameLogoTitle.Text = "WARCRAFT";
        var folder = CurrentFlavor?.Folder ?? "";
        var image = WowLocator.ArtKey(folder);
        var tag = folder switch
        {
            "_ptr_" or "_classic_ptr_" or "_classic_era_ptr_" => "PTR",
            "_xptr_" => "XPTR",
            "_beta_" or "_classic_beta_" => "BETA",
            _ => "",
        };
        GameLogoImage.Source = image is null ? null : LogoImage(image);
        GameLogoImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        GameLogoText.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        GameLogoTagText.Text = tag;
        GameLogoTag.Visibility = tag.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (image is not null) return;

        var (edition, hex) = folder switch
        {
            "_retail_" or "" => ("", "#F2B33D"),
            "_ptr_" => ("PTR", "#F2B33D"),
            "_xptr_" => ("XPTR", "#F2B33D"),
            "_beta_" => ("BETA", "#F2B33D"),
            "_classic_" => ("CLASSIC", "#E3B867"),
            "_classic_ptr_" => ("CLASSIC PTR", "#E3B867"),
            "_classic_beta_" => ("CLASSIC BETA", "#E3B867"),
            "_classic_era_" => ("CLASSIC ERA", "#D4A15A"),
            "_classic_era_ptr_" => ("CLASSIC ERA PTR", "#D4A15A"),
            "_anniversary_" => ("ANNIVERSARY", "#A9C9EE"),
            "_forever_" => ("FOREVER", "#C29BFF"),
            _ => (CurrentFlavor!.DisplayName.ToUpperInvariant(), "#F2B33D"),
        };
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var brush = new System.Windows.Media.SolidColorBrush(color);
        GameLogoTitle.Foreground = brush;
        GameLogoEdition.Foreground = brush;
        GameLogoEdition.Text = edition;
        GameLogoEditionBox.BorderBrush = brush;
        GameLogoEditionBox.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x30, color.R, color.G, color.B));
        GameLogoEditionBox.Visibility = edition.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A drawn logo for a game without logo art: its name, last word large ("AZEROTH" over "SURVIVORS").</summary>
    void ShowWordmark(string name, string hex)
    {
        var words = name.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        GameLogoLine1.Text = string.Join(' ', words[..^1]);
        GameLogoLine1.Visibility = words.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        GameLogoTitle.Text = words[^1];
        GameLogoTitle.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        GameLogoImage.Visibility = Visibility.Collapsed;
        GameLogoText.Visibility = Visibility.Visible;
        GameLogoEditionBox.Visibility = Visibility.Collapsed;
        GameLogoTag.Visibility = Visibility.Collapsed;
    }

    static HashSet<string> Folders(System.Collections.IEnumerable? items) =>
        items?.OfType<FlavorItem>().Select(i => i.Flavor.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentFlavor is not { } flavor) return;
        Directory.CreateDirectory(flavor.AddOnsPath);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{flavor.AddOnsPath}\"") { UseShellExecute = true });
    }

    async void CheckAppUpdates_Click(object sender, RoutedEventArgs e) => await CheckForAppUpdateAsync(interactive: true);

    void OpenAppRepo_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo($"https://github.com/{GitHubClient.Org}/{GitHubClient.Org}/releases")
            { UseShellExecute = true });

    void About_Click(object sender, RoutedEventArgs e)
    {
        DarkDialog.Show(this, $"DrangedAddons {AppUpdater.CurrentVersion}",
            "Installs and updates the World of Warcraft addons from github.com/DrangedAddons.\n\n" +
            $"Installed in {(AppInstaller.IsRunningInstalled ? AppInstaller.InstallDir : Path.GetDirectoryName(Environment.ProcessPath))}\n" +
            $"App settings are kept in {Storage.Dir}",
            primary: "Close", showLogo: true);
    }

    void Exit_Click(object sender, RoutedEventArgs e) => App.ExitApp();

    void UninstallApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppInstaller.Uninstall(this))
            App.ExitApp();
    }
}
