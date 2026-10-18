using System.Diagnostics;
using System.Text.Json;

namespace DrangedAddons;

public sealed record AppRelease(string Version, string ExeUrl, string Url);

/// <summary>
/// Keeps DrangedAddons.exe itself up to date from the DrangedAddons/DrangedAddons releases. Windows lets a running
/// .exe be renamed but not overwritten, so an update renames the running copy to .old, moves the new one into its
/// place, starts it, and exits; the new copy deletes the .old file when it starts.
/// </summary>
public static class AppUpdater
{
    const string Repo = GitHubClient.Org; // the app's repo is named after the org
    const string ExeName = "DrangedAddons.exe";

    public static string CurrentVersion => typeof(AppUpdater).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// The running .exe if it's a release build (one self-contained file), whatever it's called: browsers save
    /// repeat downloads as "DrangedAddons (1).exe" and that copy must still update itself. Null for dev builds,
    /// which run from a .dll beside the .exe.
    /// </summary>
    internal static string? ExePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (path is null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            // A single-file release has no assembly file on disk, so Location is empty.
            var singleFile = string.IsNullOrEmpty(typeof(AppUpdater).Assembly.Location);
            return singleFile ? path : null;
        }
    }

    public static bool CanSelfUpdate => ExePath is not null;

    /// <summary>The latest release if it's newer than this copy and carries DrangedAddons.exe.</summary>
    public static async Task<AppRelease?> CheckAsync(GitHubClient github)
    {
        var json = await github.GetJsonAsync($"https://api.github.com/repos/{GitHubClient.Org}/{Repo}/releases/latest", default);
        github.SaveCaches();
        if (json is null) return null;

        using var doc = JsonDocument.Parse(json);
        var release = doc.RootElement;
        var version = (release.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (Versions.Compare(version, CurrentVersion) <= 0) return null;

        var exeUrl = release.GetProperty("assets").EnumerateArray()
            .Where(a => (a.GetProperty("name").GetString() ?? "").Equals(ExeName, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.GetProperty("browser_download_url").GetString())
            .FirstOrDefault();
        return exeUrl is null ? null : new AppRelease(version, exeUrl, release.GetProperty("html_url").GetString() ?? "");
    }

    /// <summary>Downloads and swaps in the new .exe, then starts it. The caller should shut down after this returns.</summary>
    public static async Task ApplyAsync(GitHubClient github, AppRelease release, bool startInTray = false,
        IProgress<DownloadProgress>? progress = null)
    {
        var exe = ExePath ?? throw new InvalidOperationException("This copy of DrangedAddons can't update itself.");
        var newPath = exe + ".new";

        try
        {
            await github.DownloadAsync(release.ExeUrl, newPath, progress);
        }
        catch (UnauthorizedAccessException)
        {
            // Can't write beside this copy (e.g. it's in Program Files): let the new version install itself instead.
            await HandOverToInstallerAsync(github, release, startInTray, progress);
            return;
        }
        if (new FileInfo(newPath).Length < 1_000_000)
        {
            File.Delete(newPath);
            throw new InvalidDataException("The downloaded update looks incomplete. Try again later.");
        }

        string oldPath;
        try
        {
            oldPath = SetAside(exe);
        }
        catch (UnauthorizedAccessException)
        {
            File.Delete(newPath);
            await HandOverToInstallerAsync(github, release, startInTray, progress);
            return;
        }
        try
        {
            File.Move(newPath, exe);
        }
        catch
        {
            File.Move(oldPath, exe); // put the running copy back
            throw;
        }
        // --replace: the new copy waits for this one to finish closing instead of just showing its window.
        // --tray: an automatic update restarts straight back into the tray.
        Process.Start(new ProcessStartInfo(exe, startInTray ? "--replace --tray" : "--replace") { UseShellExecute = true });
    }

    /// <summary>
    /// For a copy that can't replace itself where it is: download the new version to a temporary folder and start
    /// it. Its normal start-up takes over: it upgrades the installed copy (or offers to install), and --replace
    /// makes this old copy close.
    /// </summary>
    static async Task HandOverToInstallerAsync(GitHubClient github, AppRelease release, bool startInTray,
        IProgress<DownloadProgress>? progress)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DrangedAddons-update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ExeName);
        await github.DownloadAsync(release.ExeUrl, path, progress);
        if (new FileInfo(path).Length < 1_000_000)
            throw new InvalidDataException("The downloaded update looks incomplete. Try again later.");
        Process.Start(new ProcessStartInfo(path, startInTray ? "--replace --tray" : "--replace") { UseShellExecute = true });
    }

    /// <summary>
    /// Renames <paramref name="exe"/> out of the way (it may be running) and returns where it went. Normally
    /// "DrangedAddons.exe.old"; if an older copy there is still running and can't be removed, a uniquely named one.
    /// </summary>
    internal static string SetAside(string exe)
    {
        var oldPath = exe + ".old";
        try
        {
            if (File.Exists(oldPath)) File.Delete(oldPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            oldPath = $"{exe}.{DateTime.Now:yyyyMMddHHmmss}.old";
        }
        File.Move(exe, oldPath);
        return oldPath;
    }

    /// <summary>Removes previous copies left by updates. One may still be closing, so retry for a few seconds.</summary>
    public static async Task CleanUpAsync()
    {
        if (ExePath is not { } exe) return;
        var dir = Path.GetDirectoryName(exe)!;
        var pattern = Path.GetFileName(exe) + "*.old"; // DrangedAddons.exe.old and DrangedAddons.exe.<time>.old
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var leftovers = Directory.GetFiles(dir, pattern);
            if (leftovers.Length == 0) break;
            var locked = false;
            foreach (var file in leftovers)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    locked = true; // still running; try again shortly, or next launch
                }
            }
            if (!locked) break;
            await Task.Delay(500);
        }
        try
        {
            File.Delete(exe + ".new"); // leftover from an interrupted download
        }
        catch
        {
            // Harmless.
        }
    }
}
