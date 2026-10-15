using System.IO.Compression;
using Microsoft.VisualBasic.FileIO;

namespace DrangedAddons;

public static class AddonInstaller
{
    /// <summary>A folder with .git is someone's development checkout; the app never overwrites or deletes it.</summary>
    public static bool IsDevCopy(string folder) =>
        Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git"));

    /// <summary>Reads "## Version:" from the folder's .toc, preferring the one named after the folder.</summary>
    public static string? ReadTocVersion(string folder)
    {
        try
        {
            var name = Path.GetFileName(folder);
            var tocs = Directory.EnumerateFiles(folder, "*.toc")
                .OrderBy(f => Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
            foreach (var toc in tocs)
            {
                if (ParseTocVersion(File.ReadLines(toc)) is { } version) return version;
            }
        }
        catch
        {
            // Unreadable .toc: version unknown.
        }
        return null;
    }

    public static string? ParseTocVersion(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (!line.StartsWith("## Version:", StringComparison.OrdinalIgnoreCase)) continue;
            var version = line["## Version:".Length..].Trim();
            if (version.Length > 0 && !version.StartsWith('@')) return version;
        }
        return null;
    }

    /// <summary>
    /// Downloads the version and swaps its addon folders into AddOns. Returns the installed folder names,
    /// the one matching the repo name first. On any failure the previous folders are put back.
    /// </summary>
    public static async Task<List<string>> InstallAsync(GitHubClient github, RemoteAddon addon, AddonVersion version,
        string addOnsPath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(addOnsPath);
        // Stage next to AddOns (same drive) so the swap is a quick rename, and outside it so WoW never sees half an addon.
        var stagingRoot = Path.Combine(Path.GetDirectoryName(addOnsPath)!, ".DrangedAddons-staging");
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var zipPath = Path.Combine(staging, "download.zip");
            var extracted = Path.Combine(staging, "new");
            if (version.ZipUrl is { } zipUrl)
            {
                await github.DownloadAsync(zipUrl, zipPath, ct);
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extracted), ct);
            }
            else if (version.Commit is { } sha)
            {
                await github.DownloadAsync(GitHubClient.SourceZipUrl(addon.Name, sha), zipPath, ct);
                var source = Path.Combine(staging, "source");
                await Task.Run(() =>
                {
                    ZipFile.ExtractToDirectory(zipPath, source);
                    // codeload zips hold a single "<repo>-<sha>" folder with the repo contents.
                    var dirs = Directory.GetDirectories(source);
                    PackageSource(dirs.Length == 1 ? dirs[0] : source, addon.Name, extracted);
                }, ct);
            }
            else
            {
                throw new InvalidOperationException($"{addon.Name} {version.Label} has nothing to download.");
            }

            var folders = Directory.GetDirectories(extracted)
                .Where(d => Directory.EnumerateFiles(d, "*.toc").Any())
                .Select(d => Path.GetFileName(d))
                .OrderBy(f => f.Equals(addon.Name, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (folders.Count == 0)
                throw new InvalidDataException($"{addon.Name} {version.Label} doesn't contain an addon folder (no .toc file found).");

            foreach (var folder in folders)
            {
                if (IsDevCopy(Path.Combine(addOnsPath, folder)))
                    throw new InvalidOperationException($"{folder} in this game folder is a Git working copy, so it was left untouched.");
            }

            Swap(extracted, Path.Combine(staging, "old"), addOnsPath, folders);
            return folders;
        }
        finally
        {
            TryDelete(staging);
            try
            {
                if (Directory.Exists(stagingRoot) && !Directory.EnumerateFileSystemEntries(stagingRoot).Any())
                    Directory.Delete(stagingRoot);
            }
            catch
            {
                // Leftover empty staging folder is harmless.
            }
        }
    }

    /// <summary>
    /// Turns a source checkout into addon folders the way the repos' release workflow does: a repo with a .toc at
    /// its root becomes one folder named after the repo; otherwise each sub-folder with a .toc is an addon.
    /// </summary>
    static void PackageSource(string root, string addonName, string outDir)
    {
        if (Directory.EnumerateFiles(root, "*.toc").Any())
        {
            CopyFiltered(root, Path.Combine(outDir, addonName));
            return;
        }
        foreach (var dir in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (!IsDevOnly(name) && Directory.EnumerateFiles(dir, "*.toc").Any())
                CopyFiltered(dir, Path.Combine(outDir, name));
        }
    }

    // Matches the release workflow's rsync excludes: .git*, .github, tools, build (at any depth).
    static bool IsDevOnly(string name) =>
        name.StartsWith(".git", StringComparison.Ordinal) || name is "tools" or "build";

    static void CopyFiltered(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            if (!IsDevOnly(Path.GetFileName(file)))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            if (!IsDevOnly(Path.GetFileName(dir)))
                CopyFiltered(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    static void Swap(string newDir, string backupDir, string addOnsPath, List<string> folders)
    {
        Directory.CreateDirectory(backupDir);
        var moved = new List<(string Folder, bool HadOld)>();
        try
        {
            foreach (var folder in folders)
            {
                var target = Path.Combine(addOnsPath, folder);
                var hadOld = Directory.Exists(target);
                if (hadOld)
                    Directory.Move(target, Path.Combine(backupDir, folder));
                moved.Add((folder, hadOld));
                Directory.Move(Path.Combine(newDir, folder), target);
            }
        }
        catch
        {
            foreach (var (folder, hadOld) in Enumerable.Reverse(moved))
            {
                var target = Path.Combine(addOnsPath, folder);
                try
                {
                    if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                    if (hadOld) Directory.Move(Path.Combine(backupDir, folder), target);
                }
                catch
                {
                    // Best effort; the backup stays in the staging folder if this fails.
                }
            }
            throw;
        }
    }

    /// <summary>Moves the addon's folders to the Recycle Bin.</summary>
    public static void Uninstall(string addOnsPath, IEnumerable<string> folders)
    {
        foreach (var folder in folders)
        {
            var dir = Path.Combine(addOnsPath, folder);
            if (!Directory.Exists(dir)) continue;
            if (IsDevCopy(dir))
                throw new InvalidOperationException($"{folder} is a Git working copy, so it was left untouched.");
            FileSystem.DeleteDirectory(dir, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
    }

    static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Temp leftovers are harmless.
        }
    }
}
