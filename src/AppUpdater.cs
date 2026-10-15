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

    /// <summary>The running .exe, or null when it can't replace itself (a dev build with its .dll beside it).</summary>
    internal static string? ExePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (path is null || !Path.GetFileName(path).Equals(ExeName, StringComparison.OrdinalIgnoreCase)) return null;
            return File.Exists(Path.ChangeExtension(path, ".dll")) ? null : path;
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
    public static async Task ApplyAsync(GitHubClient github, AppRelease release)
    {
        var exe = ExePath ?? throw new InvalidOperationException("This copy of DrangedAddons can't update itself.");
        var newPath = exe + ".new";
        var oldPath = exe + ".old";

        try
        {
            await github.DownloadAsync(release.ExeUrl, newPath);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Windows won't let DrangedAddons write to {Path.GetDirectoryName(exe)}. Move DrangedAddons.exe to a folder " +
                "you own (like Desktop or Documents) and try again.");
        }
        if (new FileInfo(newPath).Length < 1_000_000)
        {
            File.Delete(newPath);
            throw new InvalidDataException("The downloaded update looks incomplete. Try again later.");
        }

        if (File.Exists(oldPath)) File.Delete(oldPath);
        File.Move(exe, oldPath);
        try
        {
            File.Move(newPath, exe);
        }
        catch
        {
            File.Move(oldPath, exe); // put the running copy back
            throw;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    /// <summary>Removes the previous .exe left by an update. It may still be closing, so retry for a few seconds.</summary>
    public static async Task CleanUpAsync()
    {
        if (ExePath is not { } exe) return;
        var oldPath = exe + ".old";
        for (var attempt = 0; attempt < 20 && File.Exists(oldPath); attempt++)
        {
            try
            {
                File.Delete(oldPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(500);
            }
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
