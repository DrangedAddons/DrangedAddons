using System.Text.RegularExpressions;

namespace DrangedAddons;

public sealed class AppSettings
{
    /// <summary>Before games: WoW's folder and version. Moved into <see cref="GameStates"/>["wow"] on load.</summary>
    public string? WowRoot { get; set; }
    public string? Flavor { get; set; }

    /// <summary>The game shown (a <see cref="GameDefinition.Id"/>).</summary>
    public string Game { get; set; } = "wow";

    /// <summary>The games in the side bar, in order. Empty on a new install: the user adds them.</summary>
    public List<string> Games { get; set; } = [];

    /// <summary>Each game's folder and selected version.</summary>
    public Dictionary<string, GameState> GameStates { get; set; } = new();

    public GameState StateFor(string game) =>
        GameStates.TryGetValue(game, out var state) ? state : GameStates[game] = new GameState();

    /// <summary>Moves settings saved before games existed into WoW's entry, with WoW in the side bar.</summary>
    public void Migrate()
    {
        // Before accounts, "Show me online for co-op" off meant hidden: that's Appear offline now.
        Account ??= new AccountSettings { Status = CoopOnline ? AccountStatus.Online : AccountStatus.AppearOffline };
        if (WowRoot is null && Flavor is null) return;
        var wow = StateFor("wow");
        wow.Root ??= WowRoot;
        wow.Flavor ??= Flavor;
        WowRoot = Flavor = null;
        if (!Games.Contains("wow")) Games.Insert(0, "wow");
    }
    public bool ShowBeta { get; set; }
    public bool CloseToTray { get; set; } = true;
    /// <summary>"my" or "browse"; null until chosen (then: My Addons if anything's installed, else Browse).</summary>
    public string? Tab { get; set; }
    public bool UpdateNotifications { get; set; } = true;
    public bool AutoUpdateAddons { get; set; }
    public bool AutoUpdateApp { get; set; }
    public bool TrayTipShown { get; set; }
    /// <summary>Optional list columns shown, in order (ListColumn keys); null = the default set.</summary>
    public List<string>? Columns { get; set; }
    /// <summary>Before accounts: shown as online for co-op. Only read once, to set <see cref="Account"/>'s status.</summary>
    public bool CoopOnline { get; set; } = true;
    /// <summary>Name, picture and status members see; set by <see cref="Migrate"/> when missing.</summary>
    public AccountSettings? Account { get; set; }
    /// <summary>This install's secret key on the co-op service (others see only a hash of it); made on first use.</summary>
    public string? CoopId { get; set; }
}

public sealed class GameState
{
    public string? Root { get; set; }
    public string? Flavor { get; set; }
    /// <summary>This player shows the in-game overlay in this game (when the platform has it on for the game).</summary>
    public bool Overlay { get; set; } = true;
    /// <summary>The key that opens the overlay, as "Shift+Tab"; null for the default (OverlayHotkey.Default).</summary>
    public string? OverlayHotkey { get; set; }
}

public sealed class InstalledRecord
{
    public string Version { get; set; } = "";
    public bool IsBeta { get; set; }
    public string? Commit { get; set; }
    public List<string> Folders { get; set; } = new();
    /// <summary>Chosen on purpose from Versions… (not the latest): auto-update leaves it alone.</summary>
    public bool Pinned { get; set; }
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
    /// <summary>"## Author:" from the addon's .toc, when it has one.</summary>
    public string? Author { get; init; }

    /// <summary>The game it's for (a game id like "wow", from the repo's GitHub topics).</summary>
    public string Game { get; init; } = "wow";

    /// <summary>HEAD of the default branch, when the branch request succeeded. Addon icons are stamped with this.</summary>
    public string? HeadCommit { get; init; }

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
