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
        VersionText.Text = $"v{AppUpdater.CurrentVersion}";
        AddonList.ItemsSource = _rows;
        BetaCheck.IsChecked = _settings.ShowBeta;
        UninstallAppMenu.Visibility = AppInstaller.IsRunningInstalled ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) =>
        {
            LoadFlavors();
            _ = CheckForAppUpdateAsync();
            await RefreshAsync();
        };
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
        UpdateSummary();
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
            MessageBox.Show(this,
                $"Git didn't pull {row.Name}, so nothing in the folder was changed:\n\n{ex.Message}\n\n"
                + "This usually means you have local commits that aren't on GitHub yet, or uncommitted edits to the same "
                + "files. Commit, stash or push your work, then try again (or pull from your own Git tool).",
                "DrangedAddons", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var label = version is null ? "unknown" : record?.IsBeta == true ? $"{version} (beta)" : version;
        row.Update(upToDate ? AddonState.UpToDate : AddonState.UpdateAvailable, label);
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
        OpenFolderButton.IsEnabled = OpenFolderMenu.IsEnabled = CurrentFlavor is not null;
        UpdateFlavorCounts();

        EmptyText.Text = _root is null ? "Pick your World of Warcraft folder to get started."
            : _loadError is not null ? _loadError
            : "No addons with a packaged release were found in the DrangedAddons organisation.";
        EmptyText.Visibility = _rows.Count == 0 && !_working ? Visibility.Visible : Visibility.Collapsed;

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
            MessageBox.Show(owner ?? this, $"Couldn't install {row.Name} {version.Label}:\n\n{ex.Message}", "DrangedAddons",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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

        var installed = _manifest.For(flavor.AddOnsPath);
        var folders = installed.TryGetValue(row.Name, out var record) ? record.Folders : new List<string> { row.Name };
        var answer = MessageBox.Show(this,
            $"Uninstall {row.Name} from {flavor.DisplayName}?\n\nThese folders will be moved to the Recycle Bin:\n"
            + string.Join("\n", folders.Select(f => "  • " + f))
            + "\n\nYour addon settings (in the WTF folder) are kept.",
            "DrangedAddons", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        try
        {
            AddonInstaller.Uninstall(flavor.AddOnsPath, folders);
            installed.Remove(row.Name);
            Storage.Save(ManifestFile, _manifest);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't uninstall {row.Name}:\n\n{ex.Message}", "DrangedAddons",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Evaluate(row, flavor);
        UpdateSummary();
    }

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _ = CheckForAppUpdateAsync();
        await RefreshAsync();
    }

    AppRelease? _appUpdate;

    /// <summary>
    /// Shows the update bar when a newer app release exists. When <paramref name="interactive"/> (Help menu),
    /// always reports the outcome and offers to update straight away.
    /// </summary>
    async Task CheckForAppUpdateAsync(bool interactive = false)
    {
        await AppUpdater.CleanUpAsync();
        if (!AppUpdater.CanSelfUpdate && !interactive) return;
        try
        {
            _appUpdate = await AppUpdater.CheckAsync(_github);
        }
        catch (Exception ex)
        {
            // Not being able to check for an app update shouldn't get in the way of addons, unless asked.
            if (interactive) ShowMessage($"Couldn't check for app updates:\n\n{ex.Message}", MessageBoxImage.Warning);
            return;
        }

        if (_appUpdate is null)
        {
            if (interactive) ShowMessage($"You're on the latest version of DrangedAddons ({AppUpdater.CurrentVersion}).");
            return;
        }

        UpdateBannerText.Text = $"DrangedAddons {_appUpdate.Version} is available (you have {AppUpdater.CurrentVersion}).";
        UpdateBanner.Visibility = Visibility.Visible;
        if (!interactive) return;

        if (!AppUpdater.CanSelfUpdate)
        {
            ShowMessage($"DrangedAddons {_appUpdate.Version} is available, but this copy can't update itself. " +
                        "Download it from the releases page (Help → DrangedAddons on GitHub).");
            return;
        }
        var answer = MessageBox.Show(this,
            $"DrangedAddons {_appUpdate.Version} is available (you have {AppUpdater.CurrentVersion}).\n\n" +
            "Update now? DrangedAddons will restart.",
            "DrangedAddons", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes)
            await ApplySelfUpdateAsync();
    }

    void ShowMessage(string text, MessageBoxImage image = MessageBoxImage.Information) =>
        MessageBox.Show(this, text, "DrangedAddons", MessageBoxButton.OK, image);

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
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't update DrangedAddons:\n\n{ex.Message}", "DrangedAddons",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(this,
                "That doesn't look like a World of Warcraft folder. Pick the folder that contains _retail_, _classic_ and so on.",
                "DrangedAddons", MessageBoxButton.OK, MessageBoxImage.Information);
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

    async void About_Click(object sender, RoutedEventArgs e)
    {
        var access = await GitHubClient.IsSignedInAsync()
            ? "Signed in to GitHub through the GitHub CLI (5,000 checks an hour)."
            : "Using GitHub without signing in (60 checks an hour per internet connection).";
        ShowMessage($"DrangedAddons {AppUpdater.CurrentVersion}\n\n" +
                    "Installs and updates the World of Warcraft addons from github.com/DrangedAddons.\n\n" +
                    $"{access}\n\n" +
                    $"Installed in {(AppInstaller.IsRunningInstalled ? AppInstaller.InstallDir : Path.GetDirectoryName(Environment.ProcessPath))}\n" +
                    $"App settings are kept in {Storage.Dir}");
    }

    void Exit_Click(object sender, RoutedEventArgs e) => Close();

    void UninstallApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppInstaller.Uninstall())
            Application.Current.Shutdown();
    }

    void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkContentElement)?.DataContext is AddonRow row)
            Process.Start(new ProcessStartInfo(row.Latest.Url) { UseShellExecute = true });
    }
}
