using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace DrangedAddons.Mac;

public partial class MainWindow : Window
{
    const string SettingsFile = "settings.json";
    const string ManifestFile = "installed.json";
    const string Game = "wow";
    const string AppRepoUrl = "https://github.com/DrangedAddons/DrangedAddons";

    // Repos tagged for these games aren't WoW addons. (The Windows app learns its games from the platform.)
    static readonly string[] OtherGames = ["azeroth-survivors"];

    readonly GitHubClient _github;
    readonly AppSettings _settings = Storage.Load<AppSettings>(SettingsFile);
    readonly InstallManifest _manifest = Storage.Load<InstallManifest>(ManifestFile);
    readonly List<AddonItem> _all = [];
    readonly ObservableCollection<AddonItem> _shown = [];
    readonly Dictionary<string, Bitmap?> _icons = new();
    readonly HashSet<string> _filters = [];
    readonly DispatcherTimer _check = new() { Interval = TimeSpan.FromMinutes(15) };
    List<RemoteAddon> _remote = [];
    List<FlavorItem> _flavors = [];
    string? _root;
    bool _working;
    DateTime? _checked;
    string? _error;

    public MainWindow()
    {
        GitHubClient.GameForTopics = topics =>
            topics.FirstOrDefault(t => OtherGames.Contains(t, StringComparer.OrdinalIgnoreCase)) ?? Game;
        _github = new GitHubClient();
        InitializeComponent();
        _settings.Migrate();
        AddonList.ItemsSource = _shown;
        BetaMenu.IsChecked = _settings.ShowBeta;
        if (_settings.Tab == "browse")
        {
            _browse = true;
            BrowseTab.IsChecked = true;
        }
        VersionText.Text = $"v{AppVersion}";
        RailIcon.Source = Images.Bundled("gameicons/wow.png");
        _check.Tick += async (_, _) => await RefreshAsync(quiet: true);
        KeyDown += OnKeyDown;
        Opened += async (_, _) =>
        {
            LoadRoot();
            await RefreshAsync();
            _check.Start();
        };
    }

    static string AppVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    GameState State => _settings.StateFor(Game);

    /// <summary>File → Show beta versions (native menu items have no generated field).</summary>
    NativeMenuItem BetaMenu => NativeMenu.GetMenu(this)!.Items.OfType<NativeMenuItem>()
        .SelectMany(m => m.Menu?.Items.OfType<NativeMenuItem>() ?? [])
        .First(i => i.Header == "Show beta versions");

    GameFlavor? CurrentFlavor => (FlavorList.SelectedItem as FlavorItem)?.Flavor;

    void SaveSettings() => Storage.Save(SettingsFile, _settings);

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // ⌘F on a Mac (Ctrl+F elsewhere) searches, as Ctrl+F does in the Windows app.
        if (e.Key == Key.F && (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _all.Any(i => i.IsSelected))
        {
            ClearSelection();
            e.Handled = true;
        }
    }

    // ---- WoW folder, version, logo -----------------------------------------------------------------------------

    /// <summary>The saved WoW folder, else /Applications/World of Warcraft; its versions into the picker.</summary>
    void LoadRoot()
    {
        _root = State.Root is { } saved ? WowLocator.NormalizeRoot(saved) : null;
        _root ??= WowLocator.FindRoot();
        NoWowPanel.IsVisible = _root is null;
        ListPanel.IsVisible = _root is not null;
        PlayButton.IsEnabled = _root is not null;
        if (_root is null)
        {
            _flavors = [];
            FlavorList.ItemsSource = null;
            UpdateGameLogo();
            SetStatus("World of Warcraft wasn't found.");
            return;
        }
        _flavors = WowLocator.GetFlavors(_root).Select(f => new FlavorItem(f)).ToList();
        FlavorList.ItemsSource = _flavors;
        var wanted = State.Flavor ?? "_retail_";
        FlavorList.SelectedItem = _flavors.FirstOrDefault(f => f.Flavor.Folder.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                                  ?? _flavors.FirstOrDefault();
        UpdateFolderTips();
    }

    void Flavor_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CurrentFlavor is not { } flavor) return;
        if (e.RemovedItems.Count > 0) ClearSelection(); // a selection belongs to one game version
        State.Root = _root;
        State.Flavor = flavor.Folder;
        SaveSettings();
        UpdateGameLogo();
        UpdateFolderTips();
        Rebuild();
    }

    /// <summary>The version's official logo (logos\), else a drawn wordmark; PTRs and betas get a tag. The side bar icon follows.</summary>
    void UpdateGameLogo()
    {
        var folder = CurrentFlavor?.Folder ?? "";
        RailIcon.Source = Images.VersionIcon(folder) ?? Images.Bundled("gameicons/wow.png");
        var image = Images.Logo(folder);
        var tag = folder switch
        {
            "_ptr_" or "_classic_ptr_" or "_classic_era_ptr_" => "PTR",
            "_xptr_" => "XPTR",
            "_beta_" or "_classic_beta_" => "BETA",
            _ => "",
        };
        GameLogoImage.Source = image;
        GameLogoImage.IsVisible = image is not null;
        GameLogoText.IsVisible = image is null;
        GameLogoTagText.Text = tag;
        GameLogoTag.IsVisible = tag.Length > 0;
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
            _ => (CurrentFlavor?.DisplayName.ToUpperInvariant() ?? "", "#F2B33D"),
        };
        var color = Color.Parse(hex);
        GameLogoTitle.Foreground = new SolidColorBrush(color);
        GameLogoEdition.Foreground = new SolidColorBrush(color);
        GameLogoEdition.Text = edition;
        GameLogoEditionBox.BorderBrush = new SolidColorBrush(color);
        GameLogoEditionBox.Background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B));
        GameLogoEditionBox.IsVisible = edition.Length > 0;
    }

    void UpdateFolderTips()
    {
        ToolTip.SetTip(OpenFolderItem, CurrentFlavor?.AddOnsPath);
        ToolTip.SetTip(PlayButton, CurrentFlavor is { } f ? $"Play {f.DisplayName}" : "Play");
    }

    void ChooseFolder_Click(object? sender, RoutedEventArgs e) => ChooseFolder();

    void ChooseFolderMenu_Click(object? sender, EventArgs e) => ChooseFolder();

    async void ChooseFolder()
    {
        var start = await StorageProvider.TryGetFolderFromPathAsync(_root ?? "/Applications");
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the World of Warcraft folder",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        if (WowLocator.NormalizeRoot(path) is not { } root)
        {
            await Dialog.ShowAsync(this, "That isn't a World of Warcraft folder",
                "Pick the folder that holds _retail_, _classic_ and the other game versions.");
            return;
        }
        State.Root = root;
        State.Flavor = null;
        SaveSettings();
        LoadRoot();
        Rebuild();
    }

    void OpenFolder_Click(object? sender, RoutedEventArgs e) => OpenFolder();

    void OpenFolderMenu_Click(object? sender, EventArgs e) => OpenFolder();

    void OpenFolder()
    {
        if (CurrentFlavor is not { } flavor) return;
        Directory.CreateDirectory(flavor.AddOnsPath);
        OpenPath(flavor.AddOnsPath);
    }

    static void OpenPath(string pathOrUrl)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "explorer") { UseShellExecute = false };
        info.ArgumentList.Add(pathOrUrl);
        try
        {
            Process.Start(info)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Nothing to open it with.
        }
    }

    void GameMenu_Click(object? sender, RoutedEventArgs e) => GameMenuButton.ContextMenu?.Open(GameMenuButton);

    async void Play_Click(object? sender, RoutedEventArgs e)
    {
        if (_root is null || CurrentFlavor is not { } flavor) return;
        try
        {
            WowLaunch.Start(_root, flavor);
            SetStatus($"Starting {flavor.DisplayName}…");
        }
        catch (Exception ex) when (ex is FileNotFoundException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            await Dialog.ShowAsync(this, $"Couldn't start {flavor.DisplayName}", ex.Message);
        }
    }

    void Beta_Click(object? sender, EventArgs e)
    {
        _settings.ShowBeta = !_settings.ShowBeta;
        BetaMenu.IsChecked = _settings.ShowBeta;
        SaveSettings();
        Rebuild();
    }

    void OpenAppRepo_Click(object? sender, EventArgs e) => OpenPath(AppRepoUrl);

    async void About_Click(object? sender, EventArgs e) =>
        await Dialog.ShowAsync(this, $"DrangedAddons {AppVersion}",
            "Installs and updates the World of Warcraft addons from github.com/DrangedAddons.\n\n" +
            $"Settings are kept in {Storage.Dir}");

    // ---- The addon list ----------------------------------------------------------------------------------------

    async void Refresh_Click(object? sender, RoutedEventArgs e) => await RefreshAsync();

    /// <summary>Reads the addon list from GitHub. A quiet (timed) check only redraws.</summary>
    async Task RefreshAsync(bool quiet = false)
    {
        if (_working) return;
        if (!quiet) SetStatus("Checking GitHub for updates…");
        RefreshButton.IsEnabled = false;
        try
        {
            _remote = (await _github.GetAddonsAsync()).Where(a => a.Game == Game).ToList();
            _error = null;
            _checked = DateTime.Now;
            foreach (var addon in _remote) _ = LoadIconAsync(addon);
        }
        catch (RateLimitException ex)
        {
            _error = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException
                                       or System.Text.Json.JsonException)
        {
            _error = "Couldn't reach GitHub: " + ex.Message;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
        Rebuild();
        await UpdateUsageAsync();
    }

    /// <summary>How much of this hour's GitHub allowance is used (60 an hour without signing in).</summary>
    async Task UpdateUsageAsync()
    {
        if (await GitHubClient.GetAccessAsync() is not { } access || access.Limit <= 0) return;
        var used = access.Limit - access.Remaining;
        var share = (double)used / access.Limit;
        UsagePanel.IsVisible = true;
        UsageFill.Width = 90 * Math.Clamp(share, 0, 1);
        UsageFill.Background = share >= 0.9 ? (IBrush)this.FindResource("DangerBrush")! : (IBrush)this.FindResource("AccentBrush")!;
        UsageText.Text = $"{used:N0} / {access.Limit:N0}";
        ToolTip.SetTip(UsagePanel, $"{access.Source}. GitHub allows {access.Limit:N0} requests an hour; this hour's resets at {access.Reset.ToLocalTime():t}.");
    }

    /// <summary>The addon's icon.png or icon.ico from its repo; rows without one show their first letter.</summary>
    async Task LoadIconAsync(RemoteAddon addon)
    {
        if (_icons.ContainsKey(addon.Name)) return;
        _icons[addon.Name] = null;
        foreach (var file in new[] { "icon.png", "icon.ico" })
        {
            try
            {
                if (await _github.GetRawBytesAsync(addon.Name, addon.DefaultBranch, file) is not { Length: > 0 } bytes) continue;
                var icon = new Bitmap(new MemoryStream(bytes));
                _icons[addon.Name] = icon;
                foreach (var item in _all.Where(i => i.Name == addon.Name)) item.Icon = icon;
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RateLimitException
                                           or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // No icon, or one this can't read: the letter stays.
            }
        }
    }

    /// <summary>Every addon for the picked WoW version (or already installed in it), with what's installed.</summary>
    void Rebuild()
    {
        foreach (var item in _all) item.SelectionChanged -= UpdateBulkBar;
        var selected = _all.Where(i => i.IsSelected).Select(i => i.Name).ToHashSet();
        _all.Clear();
        if (CurrentFlavor is { } flavor)
        {
            foreach (var remote in _remote)
            {
                // Without betas, an addon that has never been tagged has nothing to offer yet.
                if (remote.LatestFor(_settings.ShowBeta) is not { } latest) continue;
                // Only in the game versions it's for, unless it's already in this one (so it can still be managed).
                if (!remote.IsFor(flavor.Folder) && !IsInstalledIn(remote.Name, flavor)) continue;
                var item = new AddonItem(remote, latest) { Icon = _icons.GetValueOrDefault(remote.Name), IsSelected = selected.Contains(remote.Name) };
                item.SelectionChanged += UpdateBulkBar;
                Evaluate(item, flavor);
                _all.Add(item);
            }
        }
        UpdateFlavorCounts();
        ShowList();
    }

    bool IsInstalledIn(string name, GameFlavor flavor) =>
        _manifest.For(flavor.AddOnsPath).ContainsKey(name) || Directory.Exists(Path.Combine(flavor.AddOnsPath, name));

    /// <summary>The "(N)" installed counts in the version picker.</summary>
    void UpdateFlavorCounts()
    {
        foreach (var item in _flavors)
        {
            var installed = _manifest.For(item.Flavor.AddOnsPath);
            item.Installed = _remote.Count(addon => Directory.Exists(Path.Combine(item.Flavor.AddOnsPath,
                installed.GetValueOrDefault(addon.Name)?.Folders.FirstOrDefault() ?? addon.Name)));
        }
    }

    /// <summary>What's installed, as the Windows app works it out (same installed.json format).</summary>
    void Evaluate(AddonItem item, GameFlavor flavor)
    {
        var installed = _manifest.For(flavor.AddOnsPath);
        installed.TryGetValue(item.Name, out var record);
        var mainFolder = Path.Combine(flavor.AddOnsPath, record?.Folders.FirstOrDefault() ?? item.Name);
        if (!Directory.Exists(mainFolder))
        {
            // Deleted outside the app: forget it.
            if (record is not null && installed.Remove(item.Name)) Storage.Save(ManifestFile, _manifest);
            item.Update(AddonState.NotInstalled, null);
            return;
        }
        if (AddonInstaller.IsDevCopy(mainFolder))
        {
            var local = AddonInstaller.ReadTocVersion(mainFolder);
            var untagged = local is not null
                           && (item.Remote.LatestRelease is not { } release || Versions.Compare(local, release.Version) > 0);
            item.Update(AddonState.DevCopy, local, installedIsBeta: untagged);
            return;
        }
        // Our own record first; the .toc for copies installed by hand.
        var version = record?.Version ?? AddonInstaller.ReadTocVersion(mainFolder);
        var upToDate = version is not null && Versions.Compare(version, item.Latest.Version) >= 0;
        item.Update(upToDate ? AddonState.UpToDate : AddonState.UpdateAvailable, version ?? "unknown", record?.IsBeta == true);
    }

    // Which tab is picked. Kept here because a tab's IsCheckedChanged runs before the other tab is unticked.
    bool _browse;

    void Tab_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } || _settings is null) return;
        _browse = sender == BrowseTab;
        _settings.Tab = _browse ? "browse" : "my";
        SaveSettings();
        ShowList();
    }

    void Search_TextChanged(object? sender, TextChangedEventArgs e) => ShowList();

    bool ShowsInList(AddonItem item)
    {
        if (!_browse && item.State == AddonState.NotInstalled) return false;
        var search = SearchBox.Text?.Trim() ?? "";
        if (search.Length > 0
            && !item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            && !item.Remote.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
            && !(item.Remote.Author ?? "").Contains(search, StringComparison.OrdinalIgnoreCase))
            return false;
        return _filters.Count == 0 || _filters.Any(f => f switch
        {
            "updates" => item.State == AddonState.UpdateAvailable,
            "uptodate" => item.State == AddonState.UpToDate,
            "notinstalled" => item.State == AddonState.NotInstalled,
            "beta" => item.IsBeta,
            "git" => item.State == AddonState.DevCopy,
            _ => true,
        });
    }

    void ShowList()
    {
        if (AddonList is null) return; // during InitializeComponent
        _shown.Clear();
        foreach (var item in _all.Where(ShowsInList)) _shown.Add(item);
        MyAddonsTab.Content = $"My Addons ({_all.Count(i => i.State != AddonState.NotInstalled)})";
        BrowseTab.Content = $"Browse ({_all.Count})";

        var updates = _all.Count(i => i.State == AddonState.UpdateAvailable);
        UpdateAllButton.IsEnabled = !_working && updates > 0;
        UpdateAllButton.Content = updates > 0 ? $"Update all ({updates})" : "Update all";

        var browse = _browse;
        var searching = (SearchBox.Text?.Trim().Length ?? 0) > 0 || _filters.Count > 0;
        EmptyText.IsVisible = _shown.Count == 0;
        EmptyText.Text = _error is not null && _remote.Count == 0 ? _error
            : _remote.Count == 0 ? "Loading the addon list…"
            : searching ? "No addons match."
            : browse ? $"None of the DrangedAddons addons support {CurrentFlavor?.DisplayName ?? "this version"} yet."
            : "Nothing installed here yet. Find addons on the Browse tab.";
        UpdateBulkBar();
        if (!_working) SetStatus(Summary(updates));
    }

    string Summary(int updates)
    {
        var parts = new List<string> { $"{_all.Count} addons", updates == 1 ? "1 update available" : $"{updates} updates available" };
        if (_error is not null) parts.Add(_error);
        else if (_checked is { } at) parts.Add($"checked {at:t}");
        return string.Join("  ·  ", parts);
    }

    void SetStatus(string text) => StatusText.Text = text;

    // ---- Filter ------------------------------------------------------------------------------------------------

    void Filter_Click(object? sender, RoutedEventArgs e) => FilterMenu.Open(FilterButton);

    void FilterItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        if (item == FilterAll) _filters.Clear();
        else if (item.Tag is string key && !_filters.Remove(key)) _filters.Add(key);
        foreach (var menuItem in FilterMenu.Items.OfType<MenuItem>())
            menuItem.IsChecked = menuItem == FilterAll ? _filters.Count == 0 : menuItem.Tag is string k && _filters.Contains(k);
        FilterButton.Foreground = _filters.Count > 0 ? (IBrush)this.FindResource("AccentBrush")! : (IBrush)this.FindResource("MutedBrush")!;
        ShowList();
    }

    // ---- Selection and bulk actions ----------------------------------------------------------------------------

    void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        var tick = SelectAllCheck.IsChecked == true;
        foreach (var item in _shown) item.IsSelected = tick;
        UpdateBulkBar();
    }

    void ClearSelection()
    {
        foreach (var item in _all) item.IsSelected = false;
        UpdateBulkBar();
    }

    void BulkClear_Click(object? sender, RoutedEventArgs e) => ClearSelection();

    List<AddonItem> Selected => _all.Where(i => i.IsSelected).ToList();

    void UpdateBulkBar()
    {
        var selected = Selected;
        BulkBar.IsVisible = selected.Count > 0 && !_working;
        SelectAllCheck.IsChecked = _shown.Count > 0 && _shown.All(i => i.IsSelected) ? true
            : _shown.Any(i => i.IsSelected) ? null : false;
        if (selected.Count == 0) return;
        BulkCountText.Text = selected.Count.ToString();
        SetBulk(BulkUpdateButton, "Update", selected.Count(i => i.State == AddonState.UpdateAvailable));
        SetBulk(BulkInstallButton, "Install", selected.Count(i => i.State == AddonState.NotInstalled));
        SetBulk(BulkReinstallButton, "Reinstall", selected.Count(i => i.CanUninstall));
        SetBulk(BulkDeleteButton, "Delete", selected.Count(i => i.CanUninstall));
    }

    static void SetBulk(Button button, string verb, int count)
    {
        button.IsVisible = count > 0;
        button.Content = $"{verb} {count}";
    }

    async void BulkUpdate_Click(object? sender, RoutedEventArgs e) =>
        await InstallManyAsync(Selected.Where(i => i.State == AddonState.UpdateAvailable));

    async void BulkInstall_Click(object? sender, RoutedEventArgs e) =>
        await InstallManyAsync(Selected.Where(i => i.State == AddonState.NotInstalled));

    async void BulkReinstall_Click(object? sender, RoutedEventArgs e) =>
        await InstallManyAsync(Selected.Where(i => i.CanUninstall));

    async void BulkDelete_Click(object? sender, RoutedEventArgs e) => await DeleteAsync(Selected.Where(i => i.CanUninstall).ToList());

    async void UpdateAll_Click(object? sender, RoutedEventArgs e) =>
        await InstallManyAsync(_all.Where(i => i.State == AddonState.UpdateAvailable));

    async Task InstallManyAsync(IEnumerable<AddonItem> items)
    {
        foreach (var item in items.ToList())
            if (!await InstallAsync(item)) break;
        ClearSelection();
    }

    // ---- Row actions -------------------------------------------------------------------------------------------

    static AddonItem? RowOf(object? sender) => (sender as Control)?.DataContext as AddonItem;

    async void Action_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { IsPrimaryAction: true } item) await InstallAsync(item);
    }

    async void RowReinstall_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { CanUninstall: true } item) await InstallAsync(item);
    }

    void RowBrowse_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } item || CurrentFlavor is not { } flavor) return;
        var folder = _manifest.For(flavor.AddOnsPath).GetValueOrDefault(item.Name)?.Folders.FirstOrDefault() ?? item.Name;
        OpenPath(Path.Combine(flavor.AddOnsPath, folder));
    }

    void RowGitHub_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } item) OpenPath($"https://github.com/{GitHubClient.Org}/{item.Name}");
    }

    async void RowDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { CanUninstall: true } item) await DeleteAsync([item]);
    }

    /// <summary>Downloads the latest version and swaps it into AddOns (the old folders come back if anything fails).</summary>
    async Task<bool> InstallAsync(AddonItem item)
    {
        if (_working || CurrentFlavor is not { } flavor) return false;
        var verb = item.State == AddonState.NotInstalled ? "Installed" : "Updated";
        _working = true;
        BulkBar.IsVisible = false;
        UpdateAllButton.IsEnabled = false;
        SetStatus($"Downloading {item.Name} {item.Latest.Label}…");
        item.SetBusy("Downloading…");
        var progress = new Progress<DownloadProgress>(p =>
            item.SetBusy("Downloading…", p.Total is > 0 ? (double)p.Bytes / p.Total.Value : 0));
        var done = false;
        try
        {
            var folders = await AddonInstaller.InstallAsync(_github, item.Remote, item.Latest, flavor.AddOnsPath, progress);
            item.SetBusy("Installing…", 1);
            _manifest.For(flavor.AddOnsPath)[item.Name] = new InstalledRecord
            {
                Version = item.Latest.Version,
                IsBeta = !item.Latest.IsRelease,
                Commit = item.Latest.Commit,
                Folders = folders,
                InstalledAt = DateTime.Now,
            };
            Storage.Save(ManifestFile, _manifest);
            done = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RateLimitException or IOException
                                       or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            await Dialog.ShowAsync(this, $"Couldn't install {item.Name}", ex.Message);
        }
        finally
        {
            _working = false;
            item.SetBusy(null);
            Evaluate(item, flavor);
            UpdateFlavorCounts();
            ShowList();
        }
        if (done) SetStatus($"{verb} {item.Name} {item.Latest.Label}. Type /reload in WoW to load it.");
        return done;
    }

    /// <summary>Moves the addons' folders to the Trash, after asking. Their saved settings stay.</summary>
    async Task DeleteAsync(List<AddonItem> items)
    {
        if (items.Count == 0 || CurrentFlavor is not { } flavor || _working) return;
        var installed = _manifest.For(flavor.AddOnsPath);
        var title = items.Count == 1 ? $"Delete {items[0].Name}?" : $"Delete {items.Count} addons?";
        var what = items.Count == 1 ? items[0].Name : string.Join(", ", items.Select(i => i.Name));
        if (!await Dialog.ConfirmAsync(this, title,
                $"Moves {what} from {flavor.DisplayName}'s AddOns folder to the Trash. Saved settings stay.", "Delete"))
            return;
        foreach (var item in items)
        {
            var folders = installed.GetValueOrDefault(item.Name)?.Folders is { Count: > 0 } known ? known : [item.Name];
            try
            {
                AddonInstaller.Uninstall(flavor.AddOnsPath, folders);
                installed.Remove(item.Name);
                Storage.Save(ManifestFile, _manifest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await Dialog.ShowAsync(this, $"Couldn't delete {item.Name}", ex.Message);
            }
            Evaluate(item, flavor);
        }
        ClearSelection();
        UpdateFlavorCounts();
        ShowList();
        SetStatus(items.Count == 1 ? $"Deleted {items[0].Name}." : $"Deleted {items.Count} addons.");
    }
}
