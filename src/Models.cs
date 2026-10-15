using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace DrangedAddons;

public sealed class AppSettings
{
    public string? WowRoot { get; set; }
    public string? Flavor { get; set; }
    public bool ShowBeta { get; set; }
}

public sealed class InstalledRecord
{
    public string Version { get; set; } = "";
    public bool IsBeta { get; set; }
    public string? Commit { get; set; }
    public List<string> Folders { get; set; } = new();
    public DateTime InstalledAt { get; set; }
}

/// <summary>What this app has installed, per AddOns folder, so updates and uninstalls know which folders belong to which repo.</summary>
public sealed class InstallManifest
{
    public Dictionary<string, Dictionary<string, InstalledRecord>> Installs { get; set; } = new();

    public Dictionary<string, InstalledRecord> For(string addOnsPath)
    {
        var key = Path.GetFullPath(addOnsPath).TrimEnd('\\', '/').ToLowerInvariant();
        if (!Installs.TryGetValue(key, out var map))
            Installs[key] = map = new Dictionary<string, InstalledRecord>();
        return map;
    }
}

/// <summary>
/// One installable version. A release has a packaged <see cref="ZipUrl"/>; a beta (untagged) version is
/// built from the source at <see cref="Commit"/>.
/// </summary>
public sealed record AddonVersion(
    string Version,
    bool IsRelease,
    string? Tag,
    string? ZipUrl,
    string? Commit,
    DateTimeOffset Date,
    string Url,
    string? Notes)
{
    public string Label => IsRelease ? Version : $"{Version} (beta)";
}

/// <summary>An addon repo in the org: its newest tagged release and the version currently on its default branch.</summary>
public sealed record RemoteAddon(
    string Name,
    string Description,
    string DefaultBranch,
    string? TocPath,
    AddonVersion? LatestRelease,
    AddonVersion? LatestBeta,
    IReadOnlyList<string> Games)
{
    /// <summary>Whether it's for this game-version folder: listed in its .toc, or no games listed at all.</summary>
    public bool IsFor(string flavorFolder) =>
        Games.Count == 0 || Games.Contains(flavorFolder, StringComparer.OrdinalIgnoreCase);

    /// <summary>The version to offer: the branch head only when betas are shown and it's newer than the latest release.</summary>
    public AddonVersion? LatestFor(bool includeBeta) =>
        includeBeta && LatestBeta is { } beta
                    && (LatestRelease is null || Versions.Compare(beta.Version, LatestRelease.Version) > 0)
            ? beta
            : LatestRelease;
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
    public string ReleasedText => Latest.IsRelease
        ? $"Released {Latest.Date.ToLocalTime():d MMM yyyy}"
        : $"Beta build, committed {Latest.Date.ToLocalTime():d MMM yyyy}";
    public string LinkText => Latest.IsRelease ? "Release notes" : "View commit";

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
    public bool ActionEnabled => !IsBusy && IsPrimaryAction;
    public bool CanUninstall => !IsBusy && _state is AddonState.UpdateAvailable or AddonState.UpToDate;
    public bool CanPickVersion => !IsBusy && _state != AddonState.DevCopy;

    public string UninstallTip => _state switch
    {
        AddonState.NotInstalled => "Not installed in this game version.",
        AddonState.DevCopy => "This is a Git working copy, so it's left alone.",
        _ => "Move this addon's folders to the Recycle Bin. Your settings are kept.",
    };

    public void Update(AddonState state, string? installedVersion)
    {
        _state = state;
        _installedVersion = installedVersion;
        RaiseAll();
    }

    /// <summary>Shows a Git working copy's state: <paramref name="text"/> is the status line ("checking…", an error, or the summary).</summary>
    public void SetGit(GitStatus? git, string? text)
    {
        _git = git;
        _gitText = text;
        RaiseAll();
    }

    public void SetBusy(string? text)
    {
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

public static class Versions
{
    /// <summary>Compares dotted numeric versions ("v0.50.11" vs "0.53.2"), ignoring any prefix.</summary>
    public static int Compare(string a, string b)
    {
        var x = Parts(a);
        var y = Parts(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    static long[] Parts(string v) =>
        Regex.Matches(v, @"\d+").Select(m => long.TryParse(m.Value, out var n) ? n : 0).ToArray();
}
