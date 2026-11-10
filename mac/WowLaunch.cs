using System.Diagnostics;

namespace DrangedAddons.Mac;

/// <summary>Starts a WoW version on a Mac.</summary>
static class WowLaunch
{
    /// <summary>
    /// Through the WoW launcher with the version's Battle.net product (as the Windows app does), when the install has
    /// one; otherwise the version's own app (World of Warcraft.app, World of Warcraft Classic.app …).
    /// </summary>
    public static void Start(string root, GameFlavor flavor)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Play starts WoW on a Mac. (This is the Mac app running on another system.)");
        var folder = Path.Combine(root, flavor.Folder);
        var launcher = Path.Combine(root, "World of Warcraft Launcher.app");
        if (Directory.Exists(launcher) && WowLocator.ProductCode(folder) is { } product)
            Open("-a", launcher, "--args", $"--productcode={product}");
        else if (Directory.EnumerateDirectories(folder, "World of Warcraft*.app").FirstOrDefault() is { } app)
            Open(app);
        else
            throw new FileNotFoundException($"There's no World of Warcraft app in {folder}.");
    }

    static void Open(params string[] args)
    {
        var info = new ProcessStartInfo("open") { UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        Process.Start(info)?.Dispose();
    }
}
