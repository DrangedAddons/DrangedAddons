using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DrangedAddons;

public sealed record GameFlavor(string Folder, string DisplayName, string AddOnsPath)
{
    public override string ToString() => DisplayName;
}

/// <summary>Finds the World of Warcraft root folder and the game versions (_retail_, _classic_era_, ...) inside it.</summary>
public static class WowLocator
{
    static readonly Regex FlavorFolder = new(@"^_[a-z0-9_]+_$", RegexOptions.IgnoreCase);

    static readonly Dictionary<string, string> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_retail_"] = "Retail",
        ["_ptr_"] = "Retail PTR",
        ["_xptr_"] = "Retail XPTR",
        ["_beta_"] = "Retail Beta",
        ["_classic_"] = "Classic",
        ["_classic_ptr_"] = "Classic PTR",
        ["_classic_beta_"] = "Classic Beta",
        ["_classic_era_"] = "Classic Era",
        ["_classic_era_ptr_"] = "Classic Era PTR",
        ["_anniversary_"] = "Anniversary",
    };

    public static string? RegistryInstallPath()
    {
        foreach (var key in new[]
                 {
                     @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft",
                     @"SOFTWARE\Blizzard Entertainment\World of Warcraft",
                 })
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(key);
                if (k?.GetValue("InstallPath") is string path && path.Length > 0)
                    return path;
            }
            catch
            {
                // Unreadable key: try the next one.
            }
        }
        return null;
    }

    /// <summary>The game-version folder the Battle.net launcher last registered, e.g. "_retail_".</summary>
    public static string? RegistryFlavor() =>
        RegistryInstallPath() is { } path ? Path.GetFileName(path.TrimEnd('\\', '/')) : null;

    public static string? FindRoot()
    {
        var candidates = new List<string>();
        if (RegistryInstallPath() is { } registered)
            candidates.Add(registered);
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var r = drive.RootDirectory.FullName;
            candidates.Add(Path.Combine(r, "Program Files (x86)", "World of Warcraft"));
            candidates.Add(Path.Combine(r, "Program Files", "World of Warcraft"));
            candidates.Add(Path.Combine(r, "Games", "World of Warcraft"));
            candidates.Add(Path.Combine(r, "World of Warcraft"));
        }
        return candidates.Select(NormalizeRoot).FirstOrDefault(r => r is not null);
    }

    /// <summary>Accepts the WoW root or any game-version folder inside it; returns the root if it holds at least one game version.</summary>
    public static string? NormalizeRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim()).TrimEnd('\\', '/');
            if (FlavorFolder.IsMatch(Path.GetFileName(full)))
                full = Path.GetDirectoryName(full)!;
            return Directory.Exists(full) && GetFlavors(full).Count > 0 ? full : null;
        }
        catch
        {
            return null;
        }
    }

    public static List<GameFlavor> GetFlavors(string root)
    {
        var flavors = new List<GameFlavor>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (!FlavorFolder.IsMatch(name)) continue;
                var isGame = Directory.Exists(Path.Combine(dir, "Interface"))
                             || Directory.EnumerateFiles(dir, "Wow*.exe").Any();
                if (!isGame) continue;
                flavors.Add(new GameFlavor(name, DisplayName(name), Path.Combine(dir, "Interface", "AddOns")));
            }
        }
        catch
        {
            // Inaccessible folder: report whatever was found.
        }
        return flavors.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The version whose art (logo, icon) a version uses: PTRs and betas look like their live version; Classic Beta
    /// looks like Forever, whose beta ran in that folder.
    /// </summary>
    public static string LookAlike(string folder) => folder.ToLowerInvariant() switch
    {
        "_ptr_" or "_xptr_" or "_beta_" => "_retail_",
        "_classic_ptr_" => "_classic_",
        "_classic_beta_" => "_forever_",
        "_classic_era_ptr_" => "_classic_era_",
        _ => folder,
    };

    /// <summary>
    /// The art a version uses, named as in logos\ and versionicons\ ("retail", "classic", "era", "anniversary",
    /// "forever"); null for versions without any.
    /// </summary>
    public static string? ArtKey(string folder) => LookAlike(folder) switch
    {
        "_retail_" or "" => "retail",
        "_classic_" => "classic",
        "_classic_era_" => "era",
        "_anniversary_" => "anniversary",
        "_forever_" => "forever",
        _ => null,
    };

    /// <summary>Battle.net product codes for installs without a .flavor.info.</summary>
    static readonly Dictionary<string, string> KnownProducts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_retail_"] = "wow",
        ["_ptr_"] = "wowt",
        ["_xptr_"] = "wowxptr",
        ["_beta_"] = "wow_beta",
        ["_classic_"] = "wow_classic",
        ["_classic_ptr_"] = "wow_classic_ptr",
        ["_classic_beta_"] = "wow_classic_beta",
        ["_classic_era_"] = "wow_classic_era",
        ["_classic_era_ptr_"] = "wow_classic_era_ptr",
        ["_anniversary_"] = "wow_anniversary",
        ["_forever_"] = "wowf",
    };

    /// <summary>
    /// The Battle.net product a game-version folder belongs to ("wow", "wow_classic_era", "wowf" …): the second line
    /// of its .flavor.info, else the usual one for the folder name.
    /// </summary>
    public static string? ProductCode(string flavorFolder)
    {
        try
        {
            var info = Path.Combine(flavorFolder, ".flavor.info");
            if (File.Exists(info)
                && File.ReadLines(info).Skip(1).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } product
                && Regex.IsMatch(product, "^[a-z0-9_]+$", RegexOptions.IgnoreCase))
                return product;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: fall back to the folder name.
        }
        return KnownProducts.GetValueOrDefault(Path.GetFileName(flavorFolder.TrimEnd('\\', '/')));
    }

    /// <summary>"Classic Era" for "_classic_era_"; unknown folders are title-cased.</summary>
    public static string DisplayName(string folder) =>
        KnownNames.TryGetValue(folder, out var name)
            ? name
            : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(folder.Trim('_').Replace('_', ' '));
}
