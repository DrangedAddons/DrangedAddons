using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DrangedAddons;

/// <summary>
/// A game as the platform describes it (the co-op Worker's /profiles, edited in DrangedAddons Platform). Live profiles
/// list under Add a game even before the game has addons; one the app has no code for becomes a
/// <see cref="ProfileGame"/>. The Worker checks every field (cleanProfile in coop-rendezvous/worker.js).
/// </summary>
public sealed class GameProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Live";
    /// <summary>This app has its own code for the game (WoW): only the name, description and icon are used.</summary>
    public bool Builtin { get; set; }
    public int Order { get; set; } = 100;
    public ProfileInstall Install { get; set; } = new();
    public string Connectivity { get; set; } = "";
    public int? PartySize { get; set; }
    /// <summary>The in-game overlay for this game; null (a profile from before the setting) for the default.</summary>
    public bool? Overlay { get; set; }
    /// <summary>A short hash of the profile's icon on the Worker; null for none.</summary>
    public string? Icon { get; set; }

    /// <summary>The overlay is on for the game: as set on the platform, else on unless the game is presence only.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool OverlayOn => Overlay ?? Connectivity != "presence-only";
}

/// <summary>Where the game is and how it starts. Folders are inside the game folder.</summary>
public sealed class ProfileInstall
{
    public string? Exe { get; set; }
    public string LaunchArgs { get; set; } = "";
    public string? Addons { get; set; }
    public string? Saved { get; set; }
    /// <summary>Folders relative to each drive, or starting %LOCALAPPDATA% and the like.</summary>
    public List<string> Search { get; set; } = [];
    public string? Typical { get; set; }
    /// <summary>NAME=value set whenever the app starts the game (the Worker allows only safe names).</summary>
    public List<string> LaunchEnv { get; set; } = [];
}

/// <summary>
/// The platform's game profiles: the last list read (kept on disk, so it's there at start and offline), every profile
/// ever seen (so a game already in the side bar keeps working after its profile is withdrawn), and their icons.
/// </summary>
public static class GameProfiles
{
    const string CacheFile = "game-profiles.json";
    static readonly string IconDir = Path.Combine(Storage.Dir, "profile-icons");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };

    sealed class Cache
    {
        /// <summary>The ids on the last list read: what Add a game offers.</summary>
        public List<string> Listed { get; set; } = [];
        /// <summary>Every profile seen, newest copy of each.</summary>
        public Dictionary<string, GameProfile> Known { get; set; } = [];
    }

    sealed record Reply(List<GameProfile> profiles);

    static Cache _cache = Load();
    static readonly Dictionary<string, ImageSource?> Icons = new();

    /// <summary>The co-op Worker's web address (its WebSocket address with http in place of ws).</summary>
    static string WorkerUrl => CoopTunnel.RendezvousUrl.Replace("wss://", "https://").Replace("ws://", "http://").TrimEnd('/');

    public static IEnumerable<GameProfile> Listed => _cache.Listed.Select(id => _cache.Known.GetValueOrDefault(id)).OfType<GameProfile>();
    public static IEnumerable<GameProfile> Known => _cache.Known.Values;
    public static GameProfile? Find(string id) => _cache.Known.GetValueOrDefault(id);
    public static bool IsListed(string id) => _cache.Listed.Contains(id);

    /// <summary>Goes up whenever the profiles change (so anything worked out from them can be redone).</summary>
    public static int Version { get; private set; }

    static Cache Load()
    {
        try
        {
            var path = Path.Combine(Storage.Dir, CacheFile);
            if (File.Exists(path)) return JsonSerializer.Deserialize<Cache>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Read again from the Worker.
        }
        return new();
    }

    /// <summary>Reads the list from the Worker and fetches new icons; true if anything changed. Quietly keeps the last list when it can't.</summary>
    public static async Task<bool> RefreshAsync()
    {
        List<GameProfile> profiles;
        try
        {
            var reply = JsonSerializer.Deserialize<Reply>(await Http.GetStringAsync(WorkerUrl + "/profiles"), Json);
            if (reply?.profiles is not { } list) return false;
            profiles = list.Where(p => p.Id.Length > 0 && p.Status == "Live").ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }

        var before = JsonSerializer.Serialize(_cache, Json);
        var next = new Cache { Listed = profiles.Select(p => p.Id).ToList(), Known = new(_cache.Known) };
        foreach (var p in profiles) next.Known[p.Id] = p;
        foreach (var p in profiles.Where(p => p.Icon is not null && !File.Exists(IconPath(p)))) await FetchIconAsync(p);
        if (JsonSerializer.Serialize(next, Json) == before) return false;
        _cache = next;
        Version++;
        try
        {
            Storage.WriteAtomic(CacheFile, JsonSerializer.Serialize(next, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept for this run.
        }
        Icons.Clear();
        return true;
    }

    /// <summary>The profile's icon from the Worker (kept on disk); null if it has none or it isn't here yet.</summary>
    public static ImageSource? IconFor(string id)
    {
        if (Icons.TryGetValue(id, out var cached)) return cached;
        if (Find(id) is not { Icon: not null } profile || !File.Exists(IconPath(profile))) return Icons[id] = null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(IconPath(profile));
            image.EndInit();
            image.Freeze();
            return Icons[id] = image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return Icons[id] = null;
        }
    }

    static string IconPath(GameProfile p) => Path.Combine(IconDir, $"{p.Id}-{p.Icon}.img");

    static async Task FetchIconAsync(GameProfile p)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync($"{WorkerUrl}/profiles/{p.Id}/icon");
            Directory.CreateDirectory(IconDir);
            foreach (var old in Directory.EnumerateFiles(IconDir, p.Id + "-*.img")) File.Delete(old);
            await File.WriteAllBytesAsync(IconPath(p), bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            // No icon this time; the exe's shows instead.
        }
    }
}

/// <summary>
/// A game this app has no code for, described by its platform profile: the exe that marks its folder (and starts
/// it), where addons and their saved settings go, and where it's usually installed. No versions.
/// </summary>
public sealed class ProfileGame(GameProfile profile) : GameDefinition
{
    public GameProfile Profile { get; set; } = profile;
    ProfileInstall Install => Profile.Install;
    string Exe => Install.Exe ?? "";

    public override string Id => Profile.Id;
    protected override string DefaultName => Profile.Name;
    protected override string DefaultDescription => Profile.Description;
    public override string TypicalRoot => Install.Typical ?? $@"C:\Games\{Profile.Name}";
    public override string FolderHint => $"Pick the folder with {Exe} in it.";

    /// <summary>The profile's places: each relative one on every drive, the %…% ones as they expand here.</summary>
    public override string? FindRoot() =>
        Install.Search
            .SelectMany(s => s.StartsWith('%') ? [Environment.ExpandEnvironmentVariables(s)] : OnEveryDrive(s))
            .Select(NormalizeRoot)
            .FirstOrDefault(r => r is not null);

    /// <summary>The folder with the game's exe in it: the one picked or one above it.</summary>
    public override string? NormalizeRoot(string path)
    {
        if (Exe.Length == 0) return null;
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
        Install.Addons is { } addons ? [new GameFlavor(Id, Profile.Name, Path.Combine(root, addons))] : [];

    /// <summary>Files named after the addon's folders (.lua, as WoW-style games keep them) in the saved-settings folder.</summary>
    public override List<string> FindSavedVariables(string addOnsPath, IEnumerable<string> folders)
    {
        if (Install.Saved is not { } saved || Install.Addons is not { } addons) return [];
        var full = Path.GetFullPath(addOnsPath).TrimEnd('\\', '/');
        if (!full.EndsWith(addons.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return [];
        var folder = Path.Combine(full[..^addons.TrimEnd('\\').Length], saved);
        if (!Directory.Exists(folder)) return [];
        var names = folders.SelectMany(f => new[] { f + ".lua", f + ".lua.bak" }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => names.Contains(Path.GetFileName(f))).ToList();
    }

    protected override string? IconExe(string root) => Exe.Length > 0 ? Path.Combine(root, Exe) : null;

    public override GameLaunch? GetLaunch(string root, GameFlavor flavor) =>
        Exe.Length > 0 && File.Exists(Path.Combine(root, Exe)) ? new GameLaunch(Path.Combine(root, Exe), Install.LaunchArgs, "with " + Exe) : null;
}
