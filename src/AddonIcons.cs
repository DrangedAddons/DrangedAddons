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

    /// <summary>Downloads the repo's current icon, saves it, and returns it. Keeps the cached one if GitHub can't be reached.</summary>
    public static async Task<ImageSource?> RefreshAsync(GitHubClient github, RemoteAddon addon)
    {
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
                return Load(path);
            }

            // The repo has no icon (any more).
            foreach (var candidate in Candidates)
                File.Delete(CachePath(addon.Name, candidate));
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or RateLimitException)
        {
            return LoadCached(addon.Name);
        }
    }

    static string CachePath(string addon, string candidate) =>
        Path.Combine(Dir, addon + Path.GetExtension(candidate));

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
