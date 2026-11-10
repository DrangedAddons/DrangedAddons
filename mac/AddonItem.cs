using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DrangedAddons.Mac;

public enum AddonState { NotInstalled, UpdateAvailable, UpToDate, DevCopy }

/// <summary>One addon in the list for the picked WoW version. Wording and rules follow the Windows app's AddonRow.</summary>
public sealed class AddonItem(RemoteAddon remote, AddonVersion latest) : INotifyPropertyChanged
{
    static readonly IBrush WarnBrush = Brush.Parse("#E0A33A");
    static readonly IBrush GoodBrush = Brush.Parse("#4CAF7D");
    static readonly IBrush InfoBrush = Brush.Parse("#6A9FE0");
    static readonly IBrush IdleBrush = Brush.Parse("#8B93A1");

    /// <summary>Width of the action button, for the download fill.</summary>
    public const double ActionWidth = 140;

    public RemoteAddon Remote { get; } = remote;
    public AddonVersion Latest { get; } = latest;
    public string Name => Remote.Name;
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    AddonState _state;
    string? _installedVersion;
    bool _installedIsBeta;
    string? _busyText;
    double _progress;
    bool _isSelected;
    Bitmap? _icon;

    public AddonState State => _state;
    public string? InstalledVersion => _installedVersion;

    public Bitmap? Icon
    {
        get => _icon;
        set { _icon = value; Raise(); Raise(nameof(HasIcon)); }
    }

    public bool HasIcon => _icon is not null;

    /// <summary>Ticked for a bulk action.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Raise();
            SelectionChanged?.Invoke();
        }
    }

    public event Action? SelectionChanged;

    /// <summary>Compact version for the row: the installed one (→ the newer one when there's an update), else the latest.</summary>
    public string RowVersionText => _installedVersion is null
        ? $"v{Latest.Version}"
        : _state == AddonState.UpdateAvailable
            ? $"v{_installedVersion}  →  {Latest.Version}"
            : $"v{_installedVersion}";

    public string RowSubText => Remote.Author is { } a ? $"{RowVersionText}  ·  by {a}" : RowVersionText;

    bool OfferedIsBeta => (_installedVersion is null || _state == AddonState.UpdateAvailable) && !Latest.IsRelease;
    public bool IsBeta => _installedIsBeta || OfferedIsBeta;

    public string BetaTip => (_installedIsBeta, OfferedIsBeta) switch
    {
        _ when _state == AddonState.DevCopy && _installedIsBeta =>
            "Your working copy isn't a published release: its .toc version hasn't been released yet.",
        (true, true) => "Both the installed version and the update are betas: untagged builds from the addon's main branch, not full tagged releases.",
        (true, false) => "The installed version is a beta: an untagged build from the addon's main branch, not a full tagged release.",
        _ when _installedVersion is null => "This version is a beta: an untagged build from the addon's main branch, not a full tagged release.",
        _ => "The update is a beta: an untagged build from the addon's main branch, not a full tagged release.",
    };

    // The optional columns' cells (Installed, Latest, Author, Release date).
    public string InstalledCell => _installedVersion is null ? "—" : $"v{_installedVersion}";
    public string LatestCell => $"v{Latest.Label}";
    public string AuthorCell => Remote.Author ?? "";
    public string ReleasedCell => Latest.Date.ToLocalTime().ToString("d MMM yyyy");

    public string StatusText => _state switch
    {
        AddonState.UpdateAvailable => "Update available",
        AddonState.UpToDate => "Up to date",
        AddonState.DevCopy => "Git working copy",
        _ => "Not installed",
    };

    public IBrush StatusBrush => _state switch
    {
        AddonState.UpdateAvailable => WarnBrush,
        AddonState.UpToDate => GoodBrush,
        AddonState.DevCopy => InfoBrush,
        _ => IdleBrush,
    };

    public string ActionText => _busyText ?? _state switch
    {
        AddonState.NotInstalled => "Install",
        AddonState.UpdateAvailable => "Update",
        AddonState.UpToDate => "Up to date",
        _ => "Managed by Git",
    };

    public bool IsBusy => _busyText is not null;
    public bool IsPrimaryAction => _state is AddonState.NotInstalled or AddonState.UpdateAvailable;
    public bool CanUninstall => !IsBusy && _state is AddonState.UpdateAvailable or AddonState.UpToDate;
    public bool CanBrowse => _state != AddonState.NotInstalled;

    /// <summary>The action button shows only while there's something to do (or it's doing it); otherwise a label.</summary>
    public bool ShowRowAction => IsPrimaryAction || IsBusy;
    public bool ShowRowLabel => !ShowRowAction;
    public string RowLabel => _state == AddonState.DevCopy ? "Git working copy" : "✓  Up to date";
    public IBrush RowLabelBrush => _state == AddonState.DevCopy ? InfoBrush : GoodBrush;

    /// <summary>How much of the action button the download has filled.</summary>
    public double ProgressWidth => IsBusy ? Math.Clamp(_progress, 0, 1) * ActionWidth : 0;

    public void Update(AddonState state, string? installedVersion, bool installedIsBeta = false)
    {
        _state = state;
        _installedVersion = installedVersion;
        _installedIsBeta = installedIsBeta;
        RaiseAll();
    }

    /// <summary>"Downloading…" etc. on the button while working; null when done.</summary>
    public void SetBusy(string? text, double progress = 0)
    {
        _busyText = text;
        _progress = progress;
        RaiseAll();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(State), nameof(InstalledVersion), nameof(RowVersionText), nameof(RowSubText), nameof(IsBeta),
                     nameof(BetaTip), nameof(InstalledCell), nameof(LatestCell), nameof(StatusText), nameof(StatusBrush),
                     nameof(ActionText), nameof(IsBusy), nameof(IsPrimaryAction), nameof(CanUninstall), nameof(CanBrowse),
                     nameof(ShowRowAction), nameof(ShowRowLabel), nameof(RowLabel), nameof(RowLabelBrush), nameof(ProgressWidth),
                 })
            Raise(name);
    }
}
