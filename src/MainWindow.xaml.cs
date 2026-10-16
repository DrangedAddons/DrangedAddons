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
        BetaCheck.IsChecked = _settings.ShowBeta;
        UninstallAppMenu.Visibility = AppInstaller.IsRunningInstalled ? Visibility.Visible : Visibility.Collapsed;
        CloseToTrayMenu.IsChecked = _settings.CloseToTray;
        NotificationsMenu.IsChecked = _settings.UpdateNotifications;

        _tray = new TrayIcon(ShowFromTray, CheckFromTray, App.ExitApp);
        _backgroundCheck.Tick += async (_, _) => await BackgroundCheckAsync();
        _backgroundCheck.Start();
        Closing += OnClosing;
        Closed += (_, _) => _tray?.Dispose();

        Loaded += async (_, _) =>
        {
            LoadFlavors();
            _ = CheckForAppUpdateAsync();
            await RefreshAsync();
            NotifyNewUpdates();
        };
    }

    // ---- Tray -------------------------------------------------------------------------------------------------

    TrayIcon? _tray;
    readonly System.Windows.Threading.DispatcherTimer _backgroundCheck = new() { Interval = TimeSpan.FromHours(1) };
    readonly HashSet<string> _notified = new();

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

    /// <summary>Hourly: check addons and the app; tell the user about anything new if the window is hidden.</summary>
    async Task BackgroundCheckAsync()
    {
        if (_working) return;
        await RefreshAsync();
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
            RootText.Text = "World of Warcraft folder not found. Use “Change WoW folder…” to pick it.";
            FlavorList.ItemsSource = null;
            RebuildRows();
            return;
        }

        RootText.Text = _root;
        var flavors = WowLocator.GetFlavors(_root).Select(f => new FlavorItem(f)).ToList();
        FlavorList.ItemsSource = flavors;
        UpdateFlavorCounts();
        var wanted = _settings.Flavor ?? WowLocator.RegistryFlavor() ?? "_retail_";
        FlavorList.SelectedItem = flavors.FirstOrDefault(f => f.Flavor.Folder.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                                  ?? flavors.FirstOrDefault();
    }

    async Task RefreshAsync()
    {
        if (_working) return;
        SetWorking(true, "Checking GitHub for updates…");
        try
        {
            _remote = await _github.GetAddonsAsync();
            _loadError = null;
            _lastChecked = DateTime.Now;
            foreach (var addon in _remote)
                _ = RefreshIconAsync(addon);
        }
        catch (RateLimitException ex)
        {
            _loadError = ex.Message;
        }
        catch (Exception ex)
        {
            _loadError = "Couldn't reach GitHub: " + ex.Message;
        }
        finally
        {
            SetWorking(false);
        }
        RebuildRows();
    }

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
                var row = new AddonRow(remote, latest) { Icon = icon };
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
        ListScroll.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
    }

    void ShowList()
    {
        _detailsFor = null;
        DetailsPanel.Visibility = Visibility.Collapsed;
        DetailsPanel.DataContext = null;
        ListScroll.Visibility = Visibility.Visible;
        UpdateSummary(); // restores the "no addons" message if the list is empty
    }

    void Back_Click(object sender, RoutedEventArgs e) => ShowList();

    // Esc or the mouse's back button also go back to the list.
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && _detailsFor is not null)
        {
            ShowList();
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
            row.Update(AddonState.DevCopy, AddonInstaller.ReadTocVersion(mainFolder));
            return;
        }

        // Trust our own record first; fall back to the .toc for copies installed by hand.
        var version = record?.Version ?? AddonInstaller.ReadTocVersion(mainFolder);
        var upToDate = version is not null && Versions.Compare(version, row.LatestVersion) >= 0;
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
        OpenFolderButton.IsEnabled = OpenFolderMenu.IsEnabled = CurrentFlavor is not null;
        UpdateFlavorCounts();

        EmptyText.Text = _root is null ? "Pick your World of Warcraft folder to get started."
            : _loadError is not null ? _loadError
            : "No addons with a packaged release were found in the DrangedAddons organisation.";
        EmptyText.Visibility = _rows.Count == 0 && !_working && _detailsFor is null ? Visibility.Visible : Visibility.Collapsed;

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
        }
    }

    void SetWorking(bool working, string? status = null)
    {
        _working = working;
        RefreshButton.IsEnabled = !working;
        ChangeFolderButton.IsEnabled = !working;
        ChangeFolderMenu.IsEnabled = !working;
        FlavorList.IsEnabled = !working;
        if (working) UpdateAllButton.IsEnabled = false;
        if (status is not null) StatusText.Text = status;
    }

    /// <summary>Installs <paramref name="version"/> (default: the row's latest). Returns false if it failed.</summary>
    async Task<bool> InstallAsync(AddonRow row, WowFlavor flavor, AddonVersion? version = null, Window? owner = null)
    {
        version ??= row.Latest;
        var verb = row.State == AddonState.NotInstalled ? "Installing" : "Updating";
        StatusText.Text = $"{verb} {row.Name} {version.Label}…";
        row.SetBusy($"{verb}…");
        try
        {
            var folders = await AddonInstaller.InstallAsync(_github, row.Remote, version, flavor.AddOnsPath);
            _manifest.For(flavor.AddOnsPath)[row.Name] = new InstalledRecord
            {
                Version = version.Version,
                IsBeta = !version.IsRelease,
                Commit = version.Commit,
                Folders = folders,
                InstalledAt = DateTime.Now,
            };
            Storage.Save(ManifestFile, _manifest);
            return true;
        }
        catch (Exception ex)
        {
            DarkDialog.Show(owner ?? this, $"Couldn't install {row.Name} {version.Label}", ex.Message);
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
            async (version, owner) =>
            {
                if (_working) return false;
                SetWorking(true);
                var ok = await InstallAsync(row, flavor, version, owner);
                SetWorking(false);
                UpdateSummary();
                return ok;
            })
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    void Beta_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.ShowBeta = BetaCheck.IsChecked == true;
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
    void DeleteAddon(AddonRow row, WowFlavor flavor)
    {
        if (!row.CanUninstall) return;
        var installed = _manifest.For(flavor.AddOnsPath);
        var folders = installed.TryGetValue(row.Name, out var record) ? record.Folders : new List<string> { row.Name };
        if (!DarkDialog.Show(this, $"Delete {row.Name} from {flavor.DisplayName}?",
                "These folders will be moved to the Recycle Bin:\n"
                + string.Join("\n", folders.Select(f => "  • " + f)),
                primary: "Delete", secondary: "Cancel", danger: true))
            return;

        var settingsFiles = AddonInstaller.FindSavedVariables(flavor.AddOnsPath, folders);
        var deleteSettings = settingsFiles.Count > 0 && DarkDialog.Show(this, $"Also delete {row.Name}'s saved settings?",
            $"{settingsFiles.Count} settings file{(settingsFiles.Count == 1 ? "" : "s")} (account-wide and per character) "
            + "would be moved to the Recycle Bin. Keep them if you might reinstall it later.\n\n"
            + "If World of Warcraft is running, close it first, or it will write them back when you log out.",
            primary: "Delete settings", secondary: "Keep settings", danger: true, defaultSecondary: true);

        try
        {
            AddonInstaller.Uninstall(flavor.AddOnsPath, folders);
            installed.Remove(row.Name);
            Storage.Save(ManifestFile, _manifest);
            if (deleteSettings) AddonInstaller.RecycleFiles(settingsFiles);
        }
        catch (Exception ex)
        {
            DarkDialog.Show(this, $"Couldn't delete {row.Name}", ex.Message);
        }
        Evaluate(row, flavor);
        UpdateSummary();
    }

    // ---- Row right-click menu -----------------------------------------------------------------------------------

    static AddonRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as AddonRow;

    void RowDetails_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) ShowDetails(row);
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
    void ShowBanner(string text, bool isUpdate, TimeSpan? hideAfter = null)
    {
        var token = ++_bannerToken;
        UpdateBannerText.Text = text;
        UpdateBannerButtons.Visibility = isUpdate ? Visibility.Visible : Visibility.Collapsed;
        SelfUpdateButton.Visibility = isUpdate && AppUpdater.CanSelfUpdate ? Visibility.Visible : Visibility.Collapsed;
        UpdateBanner.Background = isUpdate ? _updateBannerBackground : (System.Windows.Media.Brush)FindResource("PanelBrush");
        UpdateBanner.BorderBrush = (System.Windows.Media.Brush)FindResource(isUpdate ? "AccentBrush" : "LineBrush");
        UpdateBannerText.FontWeight = isUpdate ? FontWeights.SemiBold : FontWeights.Normal;
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
    }

    async void SelfUpdate_Click(object sender, RoutedEventArgs e) => await ApplySelfUpdateAsync();

    async Task ApplySelfUpdateAsync()
    {
        if (_appUpdate is null || _working) return;
        SetWorking(true, $"Downloading DrangedAddons {_appUpdate.Version}…");
        SelfUpdateButton.IsEnabled = false;
        SelfUpdateButton.Content = "Downloading…";
        try
        {
            await AppUpdater.ApplyAsync(_github, _appUpdate);
            App.ExitApp();
        }
        catch (Exception ex)
        {
            DarkDialog.Show(this, "Couldn't update DrangedAddons", ex.Message);
            SelfUpdateButton.IsEnabled = true;
            SelfUpdateButton.Content = "Restart to update";
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
