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
        AddonList.ItemsSource = _rows;
        _view = System.Windows.Data.CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = item => item is AddonRow row && ShowsInList(row);
        if (_settings.Tab == "browse") BrowseTab.IsChecked = true;
        BetaMenu.IsChecked = _settings.ShowBeta;
        UninstallAppMenu.Visibility = AppInstaller.IsRunningInstalled ? Visibility.Visible : Visibility.Collapsed;
        CloseToTrayMenu.IsChecked = _settings.CloseToTray;
        NotificationsMenu.IsChecked = _settings.UpdateNotifications;

        GitHubClient.UseSavedToken(TokenStore.Load());
        _tray = new TrayIcon(ShowFromTray, CheckFromTray, App.ExitApp);
        _backgroundCheck.Tick += async (_, _) => await BackgroundCheckAsync();
        _ = UpdateCheckIntervalAsync();
        Closing += OnClosing;
        Closed += (_, _) => _tray?.Dispose();

        AutoUpdateAddonsMenu.IsChecked = _settings.AutoUpdateAddons;
        AutoUpdateAppMenu.IsChecked = _settings.AutoUpdateApp;
        Loaded += async (_, _) => await InitializeAsync();
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
    /// Installs available updates for installed addons in every game version. Skips Git working copies and
    /// versions the user picked on purpose (Versions…).
    /// </summary>
    async Task AutoUpdateAddonsAsync()
    {
        if (!_settings.AutoUpdateAddons || _working || _remote.Count == 0) return;
        if (FlavorList.ItemsSource is not IEnumerable<FlavorItem> flavors) return;

        var due = new List<(AddonRow Row, WowFlavor Flavor)>();
        foreach (var flavor in flavors.Select(f => f.Flavor))
        {
            foreach (var remote in _remote)
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
                summary + "\nIf WoW is running, /reload to load the new version.");
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
    /// Background checks run every minute when signed in to GitHub (unchanged results don't count towards the
    /// 5,000 an hour), but only every 15 minutes without a token: each check is ~8 requests and the anonymous
    /// allowance is 60 an hour.
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

    WowFlavor? CurrentFlavor => (FlavorList.SelectedItem as FlavorItem)?.Flavor;

    /// <summary>Refreshes the "N installed" counts in the game-version drop-down.</summary>
    void UpdateFlavorCounts()
    {
        if (FlavorList.ItemsSource is not IEnumerable<FlavorItem> items) return;
        foreach (var item in items)
        {
            var installed = _manifest.For(item.Flavor.AddOnsPath);
            item.Installed = _remote.Count(addon => Directory.Exists(Path.Combine(item.Flavor.AddOnsPath,
                installed.GetValueOrDefault(addon.Name)?.Folders.FirstOrDefault() ?? addon.Name)));
        }
    }

    void LoadFlavors()
    {
        _root = (_settings.WowRoot is { } saved ? WowLocator.NormalizeRoot(saved) : null) ?? WowLocator.FindRoot();
        if (_root is null)
        {
            FlavorList.ItemsSource = null;
            RebuildRows();
            return;
        }

        var flavors = WowLocator.GetFlavors(_root).Select(f => new FlavorItem(f)).ToList();
        FlavorList.ItemsSource = flavors;
        UpdateFlavorCounts();
        var wanted = _settings.Flavor ?? WowLocator.RegistryFlavor() ?? "_retail_";
        FlavorList.SelectedItem = flavors.FirstOrDefault(f => f.Flavor.Folder.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                                  ?? flavors.FirstOrDefault();
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
            RebuildRows();
        else
            UpdateSummary(); // just the "checked at" time
        await UpdateUsageAsync();
        if (_loadError is null) await AutoUpdateAddonsAsync();
    }

    /// <summary>Everything about the addon list that would change what's shown.</summary>
    static string Signature(List<RemoteAddon> remote) => string.Join("|", remote.Select(a =>
        $"{a.Name}:{a.LatestRelease?.Version}:{a.LatestBeta?.Commit}:{a.Author}:{a.Description}:{string.Join(',', a.Games)}"));

    void RebuildRows()
    {
        _rows.Clear();
        if (CurrentFlavor is { } flavor)
        {
            foreach (var remote in _remote)
            {
                // Without betas, an addon that has never been tagged has nothing to offer yet.
                if (remote.LatestFor(_settings.ShowBeta) is not { } latest) continue;
                // Only in the games it's for - unless it's already in this one (so it can still be managed).
                if (!remote.IsFor(flavor.Folder) && !IsInstalledIn(remote.Name, flavor)) continue;
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
            if (_rows.FirstOrDefault(r => r.Name == name) is { } same) DetailsPanel.DataContext = same;
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

    void ShowDetails(AddonRow row)
    {
        _detailsFor = row.Name;
        DetailsPanel.DataContext = row;
        DetailsPanel.Visibility = Visibility.Visible;
        ListPanel.Visibility = Visibility.Collapsed;
        UpdateBulkBar(); // hidden while on a details page
    }

    void ShowList()
    {
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
        if (e.Key == System.Windows.Input.Key.Escape && _detailsFor is not null)
        {
            ShowList();
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

    bool IsInstalledIn(string name, WowFlavor flavor) =>
        _manifest.For(flavor.AddOnsPath).ContainsKey(name)
        || Directory.Exists(Path.Combine(flavor.AddOnsPath, name));

    string MainFolder(AddonRow row, WowFlavor flavor) =>
        Path.Combine(flavor.AddOnsPath,
            _manifest.For(flavor.AddOnsPath).GetValueOrDefault(row.Name)?.Folders.FirstOrDefault() ?? row.Name);

    async Task CheckGitAsync(AddonRow row, WowFlavor flavor, bool fetch)
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

    async Task PullAsync(AddonRow row, WowFlavor flavor)
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

    void Evaluate(AddonRow row, WowFlavor flavor)
    {
        var installed = _manifest.For(flavor.AddOnsPath);
        installed.TryGetValue(row.Name, out var record);
        var mainFolder = Path.Combine(flavor.AddOnsPath, record?.Folders.FirstOrDefault() ?? row.Name);

        if (!Directory.Exists(mainFolder))
        {
            // Deleted outside the app: forget it.
            if (record is not null && installed.Remove(row.Name))
                Storage.Save(ManifestFile, _manifest);
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
        }
        row.Update(upToDate ? AddonState.UpToDate : AddonState.UpdateAvailable, version ?? "unknown", record?.IsBeta == true);
    }

    /// <summary>The installed version in this game folder, or null if it isn't installed.</summary>
    string? InstalledVersion(AddonRow row, WowFlavor flavor) =>
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
        EmptyText.Text = _root is null ? "Pick your World of Warcraft folder to get started."
            : _loadError is not null ? _loadError
            : _rows.Count == 0 ? "No addons with a packaged release were found in the DrangedAddons organisation."
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
    async Task<bool> InstallAsync(AddonRow row, WowFlavor flavor, AddonVersion? version = null, Window? owner = null,
        string? verb = null, bool pinned = false, bool quiet = false)
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
            return true;
        }
        catch (Exception ex)
        {
            if (quiet) StatusText.Text = $"Couldn't update {row.Name} automatically: {ex.Message}";
            else DarkDialog.Show(owner ?? this, $"Couldn't install {row.Name} {version.Label}", ex.Message);
            return false;
        }
        finally
        {
            row.SetBusy(null);
            Evaluate(row, flavor);
        }
    }

    void Versions_Click(object sender, RoutedEventArgs e)
    {
        if (_working || CurrentFlavor is not { } flavor || (sender as FrameworkElement)?.DataContext is not AddonRow row)
            return;

        var window = new VersionsWindow(_github, row.Remote, flavor, _settings.ShowBeta, row.Icon,
            () => InstalledVersion(row, flavor),
            // A Git working copy is view-only: never install over it.
            row.State == AddonState.DevCopy ? null : async (version, owner) =>
            {
                if (_working) return false;
                SetWorking(true);
                // Choosing anything other than the latest is deliberate: auto-update shouldn't undo it.
                var ok = await InstallAsync(row, flavor, version, owner, pinned: version.Version != row.Latest.Version
                                                                                  || version.IsRelease != row.Latest.IsRelease);
                SetWorking(false);
                UpdateSummary();
                return ok;
            })
        {
            Owner = this,
        };
        window.ShowDialog();
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

    void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_working || CurrentFlavor is not { } flavor || (sender as FrameworkElement)?.DataContext is not AddonRow row)
            return;

        DeleteAddon(row, flavor);
    }

    /// <summary>
    /// Deletes an addon from this game: asks to confirm, then whether to delete its saved settings too
    /// (default no). Everything goes to the Recycle Bin.
    /// </summary>
    void DeleteAddon(AddonRow row, WowFlavor flavor) => DeleteAddons([row], flavor);

    /// <summary>
    /// Deletes one or more addons from this game: one confirmation, then one "delete their saved settings too?"
    /// question (default no). Everything goes to the Recycle Bin. Git working copies are skipped.
    /// </summary>
    void DeleteAddons(IReadOnlyList<AddonRow> rows, WowFlavor flavor)
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

        var settingsFiles = AddonInstaller.FindSavedVariables(flavor.AddOnsPath, folders);
        var whose = rows.Count == 1 ? $"{rows[0].Name}'s" : "their";
        var deleteSettings = settingsFiles.Count > 0 && DarkDialog.Show(this, $"Also delete {whose} saved settings?",
            $"{settingsFiles.Count} settings file{(settingsFiles.Count == 1 ? "" : "s")} (account-wide and per character) "
            + "would be moved to the Recycle Bin. Keep them if you might reinstall later.\n\n"
            + "If World of Warcraft is running, close it first, or it will write them back when you log out.",
            primary: "Delete settings", secondary: "Keep settings", danger: true, defaultSecondary: true);

        foreach (var row in rows)
        {
            try
            {
                AddonInstaller.Uninstall(flavor.AddOnsPath, foldersOf[row]);
                installed.Remove(row.Name);
                Storage.Save(ManifestFile, _manifest);
                if (deleteSettings)
                    AddonInstaller.RecycleFiles(AddonInstaller.FindSavedVariables(flavor.AddOnsPath, foldersOf[row]));
            }
            catch (Exception ex)
            {
                DarkDialog.Show(this, $"Couldn't delete {row.Name}", ex.Message);
            }
            Evaluate(row, flavor);
        }
        UpdateSummary();
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
    async Task BulkAsync(Func<AddonRow, bool> applies, Func<AddonRow, WowFlavor, Task> action, string verb)
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

    async void BulkReinstall_Click(object sender, RoutedEventArgs e) =>
        await BulkAsync(r => r.CanUninstall, (r, f) => ReinstallAsync(r, f), "Reinstalling");

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
    async Task<bool> ReinstallAsync(AddonRow row, WowFlavor flavor, bool quiet = false)
    {
        try
        {
            var record = _manifest.For(flavor.AddOnsPath).GetValueOrDefault(row.Name);
            var installed = record?.Version ?? row.InstalledVersion;
            AddonVersion target = row.Latest;
            var installedIsRelease = record?.IsBeta != true;
            var sameAsLatest = installed is not null && Versions.Compare(installed, row.Latest.Version) == 0
                               && row.Latest.IsRelease == installedIsRelease;
            if (installed is not null && !sameAsLatest)
            {
                var history = await _github.GetVersionHistoryAsync(row.Remote, includeBeta: record?.IsBeta == true || _settings.ShowBeta);
                target = history.FirstOrDefault(v => record?.Commit is { } sha ? v.Commit == sha
                                                     : Versions.Compare(v.Version, installed) == 0 && v.IsRelease)
                         ?? history.FirstOrDefault(v => Versions.Compare(v.Version, installed) == 0)
                         ?? row.Latest;
            }
            return await InstallAsync(row, flavor, target, verb: "Reinstalling", quiet: quiet);
        }
        catch (Exception ex)
        {
            if (quiet) StatusText.Text = $"Couldn't reinstall {row.Name}: {ex.Message}";
            else DarkDialog.Show(this, $"Couldn't reinstall {row.Name}", ex.Message);
            return false;
        }
    }

    void RowBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || CurrentFlavor is not { } flavor) return;
        var folder = MainFolder(row, flavor);
        if (Directory.Exists(folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    async void RowChangelog_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        var repo = $"https://github.com/{GitHubClient.Org}/{row.Name}";
        var url = await _github.HasFileAsync(row.Name, row.Remote.DefaultBranch, "CHANGELOG.md")
            ? $"{repo}/blob/{row.Remote.DefaultBranch}/CHANGELOG.md"
            : $"{repo}/releases";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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
        _settings.WowRoot = _root;
        _settings.Flavor = flavor.Folder;
        Storage.Save(SettingsFile, _settings);
        RebuildRows();
    }

    void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select your World of Warcraft folder" };
        if (_root is not null) dialog.InitialDirectory = _root;
        if (dialog.ShowDialog(this) != true) return;

        var root = WowLocator.NormalizeRoot(dialog.FolderName);
        if (root is null)
        {
            DarkDialog.Show(this, "That isn't a World of Warcraft folder",
                "Pick the folder that contains _retail_, _classic_ and so on.");
            return;
        }
        _settings.WowRoot = root;
        Storage.Save(SettingsFile, _settings);
        LoadFlavors();
    }

    /// <summary>
    /// Re-reads the game versions in the current WoW folder (e.g. after installing another one in Battle.net),
    /// keeping the selected game, and says what changed in the bar.
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
        var message = _root is null ? "No World of Warcraft folder found. Use “Change WoW folder…” to pick it."
            : added.Count > 0 ? $"Found {added.Count} new game version{(added.Count == 1 ? "" : "s")}: {Names(added)}."
            : removed.Count > 0 ? $"{removed.Count} game version{(removed.Count == 1 ? " is" : "s are")} no longer in the WoW folder: {string.Join(", ", removed.Select(r => r.Trim('_')))}."
            : $"No new game versions found ({after.Count} in {_root}).";
        ShowBanner(message, BannerKind.Info, TimeSpan.FromSeconds(6));
    }

    /// <summary>The WoW folder isn't shown in the bar (no room); hovering these buttons shows the paths instead.</summary>
    void UpdateFolderTooltips()
    {
        ChangeFolderItem.ToolTip = _root is null
            ? "No World of Warcraft folder found yet: pick it here."
            : $"Current WoW folder:\n{_root}";
        ScanGamesItem.ToolTip = _root is null
            ? "Pick your World of Warcraft folder first (Change WoW folder…)."
            : $"Look in {_root} again for game versions\n(e.g. one you've just installed).";
        OpenFolderItem.ToolTip = CurrentFlavor is { } flavor ? flavor.AddOnsPath : "Pick a game version first.";
    }

    void GameMenu_Click(object sender, RoutedEventArgs e) => OpenMenuUnder(GameMenuButton);

    /// <summary>Opens a button's menu under it, right edges lined up.</summary>
    static void OpenMenuUnder(Button button)
    {
        var menu = button.ContextMenu;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popup, target, _) =>
            [new(new System.Windows.Point(target.Width - popup.Width, target.Height + 4), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)];
        menu.IsOpen = true;
    }

    /// <summary>
    /// Top-left logo for the selected game version: the official logo from logos\ (PTR/Beta reuse their game's logo
    /// with a tag; Classic Era uses Hardcore's, its main use now), else a drawn wordmark in that version's colour.
    /// </summary>
    void UpdateGameLogo()
    {
        var folder = CurrentFlavor?.Folder ?? "";
        var (image, tag) = folder switch
        {
            "_retail_" or "" => ("retail", ""),
            "_ptr_" => ("retail", "PTR"),
            "_xptr_" => ("retail", "XPTR"),
            "_beta_" => ("retail", "BETA"),
            "_classic_" => ("classic", ""),
            "_classic_ptr_" => ("classic", "PTR"),
            "_classic_beta_" => ("forever", "BETA"), // the WoW Forever beta ran in this folder
            "_classic_era_" => ("era", ""),
            "_classic_era_ptr_" => ("era", "PTR"),
            "_anniversary_" => ("anniversary", ""),
            "_forever_" => ("forever", ""),
            _ => (null, ""),
        };
        GameLogoImage.Source = image is null ? null
            : new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/logos/{image}.png"));
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

    void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkContentElement)?.DataContext is AddonRow row)
            Process.Start(new ProcessStartInfo(row.Latest.Url) { UseShellExecute = true });
    }
}
