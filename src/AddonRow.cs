using System.ComponentModel;
using System.Windows.Media;

namespace DrangedAddons;

/// <summary>A game version in the drop-down, labelled with how many DrangedAddons addons it has installed.</summary>
public sealed class FlavorItem(GameFlavor flavor) : INotifyPropertyChanged
{
    int _installed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public GameFlavor Flavor { get; } = flavor;

    /// <summary>The version's small icon in the picker (the game program's); null for none.</summary>
    public ImageSource? Icon { get; init; }

    public int Installed
    {
        get => _installed;
        set
        {
            if (_installed == value) return;
            _installed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    public string Label => _installed > 0 ? $"{Flavor.DisplayName} ({_installed})" : Flavor.DisplayName;
}

public enum AddonState { NotInstalled, UpdateAvailable, UpToDate, DevCopy }

public sealed class AddonRow : INotifyPropertyChanged
{
    static readonly Brush WarnBrush = Frozen(0xE0, 0xA3, 0x3A);
    static readonly Brush GoodBrush = Frozen(0x4C, 0xAF, 0x7D);
    static readonly Brush InfoBrush = Frozen(0x6A, 0x9F, 0xE0);
    static readonly Brush IdleBrush = Frozen(0x8B, 0x93, 0xA1);

    AddonState _state;
    string? _installedVersion;
    string? _busyText;
    GitStatus? _git;
    string? _gitText;

    public AddonRow(RemoteAddon remote, AddonVersion latest)
    {
        Remote = remote;
        Latest = latest;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RemoteAddon Remote { get; }
    public AddonVersion Latest { get; }
    public string Name => Remote.Name;
    public string Initial => Name[..1].ToUpperInvariant();

    ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            RaiseAll();
        }
    }
    public bool HasIcon => _icon is not null;
    public bool NoIcon => _icon is null;

    public string Description => string.IsNullOrWhiteSpace(Remote.Description) ? "No description." : Remote.Description;
    public string LatestVersion => Latest.Version;
    public string? Author => Remote.Author;
    public string AuthorText => Remote.Author is { } a ? $"by {a}" : "";

    /// <summary>Compact version for the list row: the installed one (→ the newer one when there's an update), else the latest.</summary>
    public string RowVersionText => _installedVersion is null
        ? $"v{Latest.Version}"
        : _state == AddonState.UpdateAvailable
            ? $"v{_installedVersion}  →  {Latest.Version}"
            : $"v{_installedVersion}";

    bool _installedIsBeta;

    /// <summary>
    /// Whether the version shown is an untagged beta build: the installed one, or (not installed / an update on
    /// offer) the version being offered.
    /// </summary>
    public bool IsBeta => _installedIsBeta || OfferedIsBeta;

    bool OfferedIsBeta => (_installedVersion is null || _state == AddonState.UpdateAvailable) && !Latest.IsRelease;

    public string BetaTip => (_installedIsBeta, OfferedIsBeta) switch
    {
        _ when _state == AddonState.DevCopy && _installedIsBeta =>
            "Your working copy isn't a published release: its .toc version hasn't been released yet, or it has unpushed commits or uncommitted changes.",
        (true, true) => "Both the installed version and the update are betas: untagged builds from the addon's main branch, not full tagged releases.",
        (true, false) => "The installed version is a beta: an untagged build from the addon's main branch, not a full tagged release.",
        _ when _installedVersion is null => "This version is a beta: an untagged build from the addon's main branch, not a full tagged release.",
        _ => "The update is a beta: an untagged build from the addon's main branch, not a full tagged release.",
    };

    public string RowSubText => Remote.Author is { } a ? $"{RowVersionText}  ·  by {a}" : RowVersionText;

    /// <summary>Text for an optional list column (see ListColumn keys in MainWindow).</summary>
    public string CellText(string key) => key switch
    {
        "author" => Remote.Author ?? "",
        "installed" => _installedVersion is null ? "—" : $"v{_installedVersion}",
        "latest" => $"v{Latest.Label}",
        "released" => Latest.Date.ToLocalTime().ToString("d MMM yyyy"),
        _ => "",
    };

    public AddonState State => _state;
    public string? InstalledVersion => _installedVersion;
    public bool IsBusy => _busyText is not null;

    /// <summary>A Git working copy that is behind GitHub and can be fast-forwarded.</summary>
    public bool CanPull => _state == AddonState.DevCopy && _git is { Behind: > 0 };

    public string VersionText => _installedVersion is null
        ? $"Latest {Latest.Label}"
        : _state == AddonState.DevCopy
            ? $"Local {_installedVersion}  ·  Latest {Latest.Label}" + (_gitText is null ? "" : $"  ·  {_gitText}")
            : $"Installed {_installedVersion}  ·  Latest {Latest.Label}";

    public string StatusText => _state switch
    {
        AddonState.UpdateAvailable => "Update available",
        AddonState.UpToDate => "Up to date",
        AddonState.DevCopy when CanPull => "Git · changes to pull",
        AddonState.DevCopy => "Git working copy",
        _ => "Not installed",
    };

    public Brush StatusBrush => _state switch
    {
        AddonState.UpdateAvailable => WarnBrush,
        AddonState.UpToDate => GoodBrush,
        AddonState.DevCopy when CanPull => WarnBrush,
        AddonState.DevCopy => InfoBrush,
        _ => IdleBrush,
    };

    public string ActionText => _busyText ?? _state switch
    {
        AddonState.NotInstalled => "Install",
        AddonState.UpdateAvailable => "Update",
        AddonState.UpToDate => "Up to date",
        _ when CanPull => "Pull",
        _ => "Managed by Git",
    };

    public bool IsPrimaryAction => _state is AddonState.NotInstalled or AddonState.UpdateAvailable || CanPull;
    // Stays "enabled" while busy so the progress fill isn't dimmed; clicks are ignored while the app is working.
    public bool ActionEnabled => IsPrimaryAction || IsBusy;
    public bool CanUninstall => !IsBusy && _state is AddonState.UpdateAvailable or AddonState.UpToDate;
    public bool CanBrowse => _state != AddonState.NotInstalled;
    /// <summary>The action button shows only while there's something to do (or it's doing it); otherwise a label.</summary>
    public bool ShowRowAction => IsPrimaryAction || IsBusy;
    public bool ShowRowLabel => !ShowRowAction;

    /// <summary>What's shown instead of a button when there's nothing to do.</summary>
    public string RowLabel => _state == AddonState.DevCopy ? "Git working copy" : "✓  Up to date";
    public Brush RowLabelBrush => _state == AddonState.DevCopy ? InfoBrush : GoodBrush;

    public void Update(AddonState state, string? installedVersion, bool installedIsBeta = false)
    {
        _state = state;
        _installedVersion = installedVersion;
        _installedIsBeta = installedIsBeta;
        RaiseAll();
    }

    /// <summary>Shows a Git working copy's state: <paramref name="text"/> is the status line ("checking…", an error, or the summary).</summary>
    public void SetGit(GitStatus? git, string? text)
    {
        _git = git;
        _gitText = text;
        // A release when HEAD is on a version tag, or when the .toc version is the latest release's and nothing is
        // local: commits after the tag that don't bump the version (screenshots, workflow) are still that release.
        if (git is not null)
            _installedIsBeta = !(git.IsRelease
                                 || git is { Changed: 0, Ahead: 0 } && _installedVersion is not null
                                    && Remote.LatestRelease is { } release && Versions.Compare(_installedVersion, release.Version) == 0);
        RaiseAll();
    }

    public void SetBusy(string? text)
    {
        _busyText = text;
        if (text is null) _progress = null;
        RaiseAll();
    }

    bool _isSelected;

    /// <summary>Ticked in the list for a bulk action. Raises SelectionChanged so the bulk bar can update.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SelectionChanged;

    double? _progress;

    /// <summary>0–1 while downloading (fills the action button), else null.</summary>
    public double? Progress => _progress;

    public void SetProgress(double? fraction, string text)
    {
        _progress = fraction;
        _busyText = text;
        RaiseAll();
    }

    void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
