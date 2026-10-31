using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DrangedAddons;

/// <summary>How to start a game: the program, its arguments, and how it starts (shown in the Play tooltip).</summary>
public sealed record GameLaunch(string Exe, string Arguments, string How);

/// <summary>
/// A game the app manages addons for: where it's installed, its versions (WoW's _retail_, _classic_era_ …; one for
/// games without any), where addons go and where their saved settings live. Repos in the org belong to a game by
/// GitHub topic (<see cref="Id"/>); repos without a game topic are WoW addons.
/// </summary>
public abstract class GameDefinition
{
    /// <summary>"wow", "azeroth-survivors": also the GitHub topic that marks a repo as this game's.</summary>
    public abstract string Id { get; }

    /// <summary>The platform profile's name when it has one, else the game's own.</summary>
    public string Name => GameProfiles.Find(Id)?.Name is { Length: > 0 } name ? name : DefaultName;
    protected abstract string DefaultName { get; }

    /// <summary>A line or two about the game and its addons, for the "Add a game" window (the profile's when it has one).</summary>
    public string Description => GameProfiles.Find(Id)?.Description is { Length: > 0 } text ? text : DefaultDescription;
    protected abstract string DefaultDescription { get; }

    /// <summary>Whether it has versions to pick between (WoW); otherwise the version picker is hidden.</summary>
    public virtual bool HasVersions => false;

    /// <summary>Looks in the usual places; null if it isn't found.</summary>
    public abstract string? FindRoot();

    /// <summary>Where it's usually installed: suggested when it isn't found.</summary>
    public abstract string TypicalRoot { get; }

    /// <summary>What a folder needs to be this game's: shown when the folder picked isn't.</summary>
    public abstract string FolderHint { get; }

    /// <summary>The game folder for a folder the user picked (the game folder or one inside it), or null if it isn't one.</summary>
    public abstract string? NormalizeRoot(string path);

    public abstract List<GameFlavor> GetFlavors(string root);

    /// <summary>The version to start on when none was picked before.</summary>
    public virtual string? DefaultFlavor => null;

    /// <summary>An addon's saved-settings files (to offer deleting them with it).</summary>
    public abstract List<string> FindSavedVariables(string addOnsPath, IEnumerable<string> folders);

    /// <summary>The program whose icon stands for the game in the side bar.</summary>
    protected abstract string? IconExe(string root);

    /// <summary>How to start <paramref name="flavor"/> of the game (Play); null if its program isn't there.</summary>
    public abstract GameLaunch? GetLaunch(string root, GameFlavor flavor);

    /// <summary>Whether the game can host or join co-op directly, without its relay (the Direct co-op window).</summary>
    public virtual bool HasDirectCoop => false;

    /// <summary>Whether the platform turns the in-game overlay on for this game (its profile; off without one).</summary>
    public bool HasOverlay => GameProfiles.Find(Id)?.OverlayOn == true;

    /// <summary>Whether the game is running now (any version), for "In game" on the account.</summary>
    public virtual bool IsRunning(string root) =>
        GetFlavors(root).Select(f => GetLaunch(root, f)?.Exe).OfType<string>().Any(Account.IsRunning);

    /// <summary>A small icon for <paramref name="flavor"/> in the version picker; null for none.</summary>
    public virtual ImageSource? FlavorIcon(string root, GameFlavor flavor) => null;

    public static readonly GameDefinition Wow = new WowGame();

    /// <summary>The games this app has code for.</summary>
    static readonly GameDefinition[] Coded = [Wow, new AzerothSurvivorsGame()];

    /// <summary>
    /// A game for every platform profile the app has no code for, kept across refreshes so the side bar's references
    /// stay the same objects. Includes withdrawn profiles, so a game already added keeps working.
    /// </summary>
    static readonly Dictionary<string, ProfileGame> FromProfiles = new();

    static IEnumerable<GameDefinition> Everything()
    {
        foreach (var profile in GameProfiles.Known.Where(p => !p.Builtin && Coded.All(c => c.Id != p.Id)
                                                            && p.Install.Exe is not null && p.Install.Addons is not null))
        {
            if (FromProfiles.TryGetValue(profile.Id, out var game)) game.Profile = profile;
            else FromProfiles[profile.Id] = new ProfileGame(profile);
        }
        return Coded.Concat(FromProfiles.Values);
    }

    /// <summary>What Add a game offers: the games with code, then the platform's Live profiles in its order.</summary>
    public static IReadOnlyList<GameDefinition> All =>
        Coded.Concat(Everything().OfType<ProfileGame>().Where(g => GameProfiles.IsListed(g.Id)).OrderBy(g => g.Profile.Order).ThenBy(g => g.Name))
            .ToList();

    public static GameDefinition? Find(string? id) => Everything().FirstOrDefault(g => g.Id == id);

    /// <summary>The game a repo is for, from its GitHub topics; WoW when it has none of the games' topics.</summary>
    public static string ForTopics(IEnumerable<string> topics) =>
        Everything().FirstOrDefault(g => topics.Contains(g.Id, StringComparer.OrdinalIgnoreCase))?.Id ?? Wow.Id;

    /// <summary>
    /// The game's icon: the one bundled with the app (gameicons\&lt;Id&gt;.png, taken from the game's program), else
    /// its platform profile's, else read from the game's program file; null if none is there.
    /// </summary>
    public ImageSource? LoadIcon(string? root)
    {
        if (Bundled($"gameicons/{Id}.png") is { } bundled) return bundled;
        if (GameProfiles.IconFor(Id) is { } profileIcon) return profileIcon;
        return root is not null && IconExe(root) is { } exe ? ExeIcon(exe, 64) : null;
    }

    /// <summary>An image bundled with the app (e.g. "gameicons/wow.png"); null if there isn't one.</summary>
    protected static ImageSource? Bundled(string path)
    {
        if (BundledImages.TryGetValue(path, out var cached)) return cached;
        try
        {
            var image = new BitmapImage(new Uri($"pack://application:,,,/{path}"));
            image.Freeze();
            return BundledImages[path] = image;
        }
        catch (IOException)
        {
            return BundledImages[path] = null;
        }
    }

    static readonly Dictionary<string, ImageSource?> BundledImages = new();
    static readonly Dictionary<(string, int), ImageSource?> ExeIcons = new();

    /// <summary>The icon in <paramref name="exe"/>, at the size nearest <paramref name="size"/>; null if there isn't one.</summary>
    protected static ImageSource? ExeIcon(string exe, int size)
    {
        if (ExeIcons.TryGetValue((exe, size), out var cached)) return cached;
        return ExeIcons[(exe, size)] = ReadExeIcon(exe, size);
    }

    static ImageSource? ReadExeIcon(string exe, int size)
    {
        if (!File.Exists(exe)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractIcon(exe, 0, size);
            if (icon is null) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Candidate folders on every fixed drive: X:\&lt;each of <paramref name="relative"/>&gt;.</summary>
    protected static IEnumerable<string> OnEveryDrive(params string[] relative) =>
        DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .SelectMany(d => relative.Select(r => Path.Combine(d.RootDirectory.FullName, r)));
}

/// <summary>World of Warcraft: one install folder holding a folder per game version (_retail_, _classic_era_, _forever_ …).</summary>
sealed class WowGame : GameDefinition
{
    public override string Id => "wow";
    protected override string DefaultName => "World of Warcraft";
    protected override string DefaultDescription =>
        "Retail, Classic, Anniversary, Forever and the rest. Each game version has its own addons, picked at the top.";
    public override bool HasVersions => true;
    public override string? FindRoot() => WowLocator.FindRoot();
    public override string TypicalRoot => @"C:\Program Files (x86)\World of Warcraft";
    public override string FolderHint => "Pick the World of Warcraft folder: the one with _retail_, _classic_ and so on in it.";
    public override string? NormalizeRoot(string path) => WowLocator.NormalizeRoot(path);
    public override List<GameFlavor> GetFlavors(string root) => WowLocator.GetFlavors(root);
    public override string? DefaultFlavor => WowLocator.RegistryFlavor() ?? "_retail_";

    public override List<string> FindSavedVariables(string addOnsPath, IEnumerable<string> folders) =>
        AddonInstaller.FindSavedVariables(addOnsPath, folders);

    /// <summary>
    /// Through Battle.net, the way its own desktop shortcuts do: World of Warcraft Launcher.exe
    /// --productcode=&lt;the version's product&gt; signs in and starts that version. Without the launcher or a product
    /// code, the version's own Wow*.exe (which hands over to Battle.net to sign in).
    /// </summary>
    public override GameLaunch? GetLaunch(string root, GameFlavor flavor)
    {
        var folder = Path.Combine(root, flavor.Folder);
        var launcher = Path.Combine(root, "World of Warcraft Launcher.exe");
        if (File.Exists(launcher) && WowLocator.ProductCode(folder) is { } product)
            return new GameLaunch(launcher, $"--productcode={product}", "through Battle.net");
        return VersionExe(folder) is { } exe ? new GameLaunch(exe, "", "with " + Path.GetFileName(exe)) : null;
    }

    /// <summary>
    /// Battle.net's icon for the version (versionicons\, chosen like the big logos: PTRs and betas show their live
    /// version's, Classic Beta shows Forever's). Versions without one show their game program's icon.
    /// </summary>
    public override ImageSource? FlavorIcon(string root, GameFlavor flavor) =>
        WowLocator.ArtKey(flavor.Folder) is { } key && Bundled($"versionicons/{key}.png") is { } icon ? icon
        : VersionExe(Path.Combine(root, flavor.Folder)) is { } exe ? ExeIcon(exe, 32)
        : null;

    /// <summary>Any version's game program, not the launcher (which <see cref="GetLaunch"/> starts).</summary>
    public override bool IsRunning(string root) =>
        GetFlavors(root).Select(f => VersionExe(Path.Combine(root, f.Folder))).OfType<string>().Any(Account.IsRunning);

    /// <summary>A version folder's game program (Wow.exe, WowClassic.exe, WowB.exe …); null if there isn't one.</summary>
    static string? VersionExe(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "Wow*.exe")
                    .Where(f => !Path.GetFileName(f).Contains("Voice", StringComparison.OrdinalIgnoreCase)
                                && !Path.GetFileName(f).Contains("Error", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => Path.GetFileName(f).Length)
                    .FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    protected override string? IconExe(string root)
    {
        var launcher = Path.Combine(root, "World of Warcraft Launcher.exe");
        if (File.Exists(launcher)) return launcher;
        try
        {
            return WowLocator.GetFlavors(root)
                .Select(f => Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetDirectoryName(f.AddOnsPath))!, "Wow*.exe").FirstOrDefault())
                .FirstOrDefault(e => e is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Azeroth Survivors: AzerothSurvivors.exe with its addons in benilla-config\AddOns (WoW-style folders with a .toc)
/// and their saved settings in benilla-config\saved\&lt;Addon&gt;.lua. No game versions.
/// </summary>
sealed class AzerothSurvivorsGame : GameDefinition
{
    const string Exe = "AzerothSurvivors.exe";
    const string ConfigFolder = "benilla-config";

    public override string Id => "azeroth-survivors";
    protected override string DefaultName => "Azeroth Survivors";
    protected override string DefaultDescription =>
        "Its addons work like WoW's: folders with a .toc, in benilla-config\\AddOns inside the game folder.";

    public override string TypicalRoot => @"C:\Games\AzerothSurvivors";
    public override string FolderHint => $"Pick the folder with {Exe} in it.";

    public override string? FindRoot() =>
        OnEveryDrive(@"Games\AzerothSurvivors", @"Games\Azeroth Survivors", "AzerothSurvivors", "Azeroth Survivors",
                @"Program Files\AzerothSurvivors", @"Program Files (x86)\AzerothSurvivors")
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AzerothSurvivors"))
            .Select(NormalizeRoot)
            .FirstOrDefault(r => r is not null);

    /// <summary>The folder with AzerothSurvivors.exe in it: the one picked or one of the (up to three) above it.</summary>
    public override string? NormalizeRoot(string path)
    {
        try
        {
            for (var dir = Path.GetFullPath(path.Trim()).TrimEnd('\\', '/'); dir is not null; dir = Path.GetDirectoryName(dir))
            {
                if (File.Exists(Path.Combine(dir, Exe))) return dir;
                if (dir.Count(c => c == '\\') <= 1) break; // don't climb to the drive root
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            // Not a usable path.
        }
        return null;
    }

    public override List<GameFlavor> GetFlavors(string root) =>
        [new GameFlavor(Id, Name, Path.Combine(root, ConfigFolder, "AddOns"))];

    public override List<string> FindSavedVariables(string addOnsPath, IEnumerable<string> folders)
    {
        var saved = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(addOnsPath).TrimEnd('\\', '/'))!, "saved");
        if (!Directory.Exists(saved)) return [];
        var names = folders.SelectMany(f => new[] { f + ".lua", f + ".lua.bak" }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(saved, "*", SearchOption.AllDirectories)
            .Where(f => names.Contains(Path.GetFileName(f)))
            .ToList();
    }

    protected override string? IconExe(string root) => Path.Combine(root, Exe);

    public override GameLaunch? GetLaunch(string root, GameFlavor flavor) =>
        File.Exists(Path.Combine(root, Exe)) ? new GameLaunch(Path.Combine(root, Exe), "", "with " + Exe) : null;

    public override bool HasDirectCoop => true;

    /// <summary>
    /// The hero last played: the game keeps per-hero files named "&lt;Realm&gt;-&lt;Hero&gt;.txt" in benilla-config\chat,
    /// layout and camera. Null when it hasn't been played yet.
    /// </summary>
    public static string? LastHero(string root)
    {
        try
        {
            return new[] { "chat", "layout", "camera" }
                .Select(d => Path.Combine(root, ConfigFolder, d))
                .Where(Directory.Exists)
                .SelectMany(d => new DirectoryInfo(d).EnumerateFiles("*-*.txt"))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => Path.GetFileNameWithoutExtension(f.Name))
                .Select(n => n[(n.LastIndexOf('-') + 1)..])
                .FirstOrDefault(n => n.Length > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Environment variables the exe reads at start for co-op without its relay (wss://wow.aethus.pl/coop/lobby/):
    /// the host's game listens on a TCP port ("rogue: hosting a party on port …"), a joiner's connects to host:port
    /// ("rogue: joining the party at …"). Found in the exe's strings, not documented by the game. Direct co-op points
    /// both at this app on 127.0.0.1, which carries the connection between the PCs (<see cref="CoopTunnel"/>).
    /// </summary>
    public static Dictionary<string, string> DirectCoopEnvironment(int? hostPort, string? joinAddress, string? password)
    {
        var env = new Dictionary<string, string>();
        if (hostPort is { } port) env["WOW_ROGUE_COOP_HOST"] = port.ToString();
        if (!string.IsNullOrWhiteSpace(joinAddress)) env["WOW_ROGUE_COOP_JOIN"] = joinAddress.Trim();
        if (!string.IsNullOrEmpty(password)) env["WOW_ROGUE_COOP_PASSWORD"] = password;
        return env;
    }
}
