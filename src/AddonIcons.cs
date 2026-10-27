using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DrangedAddons;

/// <summary>
/// Addon icons: an icon.ico (or icon.png) at the root of each repo's default branch, kept in
/// %AppData%\DrangedAddons\icons so they show straight away on the next launch, even offline.
/// </summary>
public static class AddonIcons
{
    static readonly string[] Candidates = ["icon.ico", "icon.png"];
    static readonly string Dir = Path.Combine(Storage.Dir, "icons");

    /// <summary>The icon saved from a previous run, if any.</summary>
    public static ImageSource? LoadCached(string addon) =>
        Candidates.Select(c => CachePath(addon, c)).Where(File.Exists).Select(Load).FirstOrDefault(i => i is not null);

    /// <summary>
    /// True when the icon on disk was saved for <paramref name="commit"/> (including "this repo has no icon").
    /// <paramref name="icon"/> is the cached image, or null when the repo has none.
    /// </summary>
    public static bool TryUnchanged(string addon, string commit, out ImageSource? icon)
    {
        icon = null;
        try
        {
            var stamp = StampPath(addon);
            if (!File.Exists(stamp) || File.ReadAllText(stamp) != commit) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        icon = LoadCached(addon);
        return true;
    }

    /// <summary>Downloads the repo's current icon, saves it, and returns it. Keeps the cached one if GitHub can't be reached.</summary>
    public static async Task<ImageSource?> RefreshAsync(GitHubClient github, RemoteAddon addon)
    {
        // The icon lives at the branch head. The same commit still has the same file, so a refresh
        // (manual or because some other addon changed) doesn't download it again.
        if (addon.HeadCommit is { } commit && TryUnchanged(addon.Name, commit, out var cached))
            return cached;
        try
        {
            foreach (var candidate in Candidates)
            {
                if (await github.GetRawBytesAsync(addon.Name, addon.DefaultBranch, candidate) is not { Length: > 0 } bytes)
                    continue;

                Directory.CreateDirectory(Dir);
                var path = CachePath(addon.Name, candidate);
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes);
                File.Move(tmp, path, overwrite: true);
                // Only one icon per addon: drop a stale one of the other type.
                foreach (var other in Candidates.Where(c => c != candidate))
                    File.Delete(CachePath(addon.Name, other));
                WriteStamp(addon);
                return Load(path);
            }

            // The repo has no icon (any more).
            foreach (var candidate in Candidates)
                File.Delete(CachePath(addon.Name, candidate));
            WriteStamp(addon);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or RateLimitException)
        {
            return LoadCached(addon.Name);
        }
    }

    static void WriteStamp(RemoteAddon addon)
    {
        if (addon.HeadCommit is not { } commit) return;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(StampPath(addon.Name), commit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Next refresh will try the download again.
        }
    }

    static string CachePath(string addon, string candidate) =>
        Path.Combine(Dir, addon + Path.GetExtension(candidate));

    static string StampPath(string addon) => Path.Combine(Dir, addon + ".commit");

    /// <summary>Decodes an .ico or .png, taking the largest image an .ico holds.</summary>
    static ImageSource? Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null; // unreadable image: fall back to the letter tile
        }
    }
}
