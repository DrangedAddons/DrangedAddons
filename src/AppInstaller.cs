using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace DrangedAddons;

/// <summary>
/// Installs the app per-user like Discord or VS Code: %LocalAppData%\Programs\DrangedAddons, Start menu and desktop
/// shortcuts, and an entry in Settings → Apps. Per-user (not Program Files) so the self-updater can replace the
/// .exe without an administrator prompt.
/// </summary>
public static class AppInstaller
{
    const string ExeName = "DrangedAddons.exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DrangedAddons";

    public static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DrangedAddons");

    static string InstalledExe => Path.Combine(InstallDir, ExeName);
    static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "DrangedAddons.lnk");
    static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DrangedAddons.lnk");

    public static bool IsRunningInstalled =>
        AppUpdater.ExePath is { } exe && Path.GetFullPath(exe).Equals(InstalledExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true to carry on starting this process, false when it handed over to the installed copy.</summary>
    public static bool HandleStartup(string[] args)
    {
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Uninstall(); // from Settings → Apps; this process exits either way
            return false;
        }

        // Dev builds (with their .dll alongside) never install, and DRANGEDADDONS_DEV=1 makes a published test
        // build run in place without offering to install or touching an installed copy.
        if (AppUpdater.ExePath is not { } exe || Environment.GetEnvironmentVariable("DRANGEDADDONS_DEV") == "1")
            return true;

        if (IsRunningInstalled)
        {
            Register(); // keeps the version shown in Settings → Apps current after self-updates
            return true;
        }

        if (File.Exists(InstalledExe))
        {
            // Already installed: a newer download upgrades it, then the installed copy opens either way.
            var installed = FileVersionInfo.GetVersionInfo(InstalledExe).FileVersion ?? "0";
            var upgrade = Versions.Compare(AppUpdater.CurrentVersion, installed) > 0;
            if (upgrade)
            {
                CopyIn(exe);
                Register();
            }
            // After an upgrade the new installed copy replaces one that may be running in the tray; otherwise a
            // running copy just shows its window.
            Launch(replace: upgrade);
            return false;
        }

        if (!DarkDialog.Show(null, "Install DrangedAddons?",
                $"It will be installed for your Windows account in\n{InstallDir}\n" +
                "with Start menu and desktop shortcuts, and can be removed from Settings → Apps.\n\n" +
                "Choose \"Just run it\" to use it this once without installing.",
                primary: "Install", secondary: "Just run it", showLogo: true))
            return true;

        CopyIn(exe);
        Register();
        CreateShortcut(StartMenuShortcut);
        CreateShortcut(DesktopShortcut);
        DarkDialog.Show(null, "DrangedAddons is installed",
            "Open it from the Start menu or the desktop shortcut from now on.\n\n" +
            $"You can delete the file you downloaded ({exe}).",
            primary: "Open DrangedAddons", showLogo: true);
        Launch();
        return false;
    }

    /// <summary>Copies this .exe into the install folder. A running installed copy is renamed aside (.old) first,
    /// the same way self-updates work; the new copy deletes it when it starts.</summary>
    static void CopyIn(string sourceExe)
    {
        Directory.CreateDirectory(InstallDir);
        if (File.Exists(InstalledExe))
            AppUpdater.SetAside(InstalledExe);
        File.Copy(sourceExe, InstalledExe);
    }

    static void Launch(bool replace = false) =>
        Process.Start(new ProcessStartInfo(InstalledExe, replace ? "--replace" : "")
        {
            UseShellExecute = true,
            WorkingDirectory = InstallDir,
        });

    /// <summary>The Settings → Apps entry (per-user, no admin needed).</summary>
    static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        var previousVersion = key.GetValue("DisplayVersion") as string;
        key.SetValue("DisplayName", "DrangedAddons");
        key.SetValue("DisplayVersion", AppUpdater.CurrentVersion);
        key.SetValue("Publisher", "DrangedAddons");
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("URLInfoAbout", $"https://github.com/{GitHubClient.Org}/{GitHubClient.Org}");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        if (File.Exists(InstalledExe))
            key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);

        // First run of a new version: Windows caches shortcut icons (Start, taskbar pins, desktop) and won't notice
        // that the .exe behind them changed, so tell it to refresh them.
        if (previousVersion != AppUpdater.CurrentVersion)
            RefreshShellIcons();
    }

    const int SHCNE_UPDATEITEM = 0x00002000;
    const int SHCNE_ASSOCCHANGED = 0x08000000;
    const uint SHCNF_IDLIST = 0x0000;
    const uint SHCNF_PATHW = 0x0005;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string? item1, IntPtr item2);

    static void RefreshShellIcons()
    {
        foreach (var path in new[] { InstalledExe, StartMenuShortcut, DesktopShortcut })
        {
            if (File.Exists(path)) SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, path, IntPtr.Zero);
        }
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, null, IntPtr.Zero);
    }

    static void CreateShortcut(string path)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host isn't available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = InstalledExe;
            link.WorkingDirectory = InstallDir;
            link.IconLocation = InstalledExe + ",0";
            link.Description = "Install and update the DrangedAddons World of Warcraft addons";
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>
    /// Removes the app (from Settings → Apps or File → Uninstall). Installed addons and their settings in the
    /// WoW folder are not touched.
    /// </summary>
    public static bool Uninstall(Window? owner = null)
    {
        if (!DarkDialog.Show(owner, "Uninstall DrangedAddons?",
                "Your installed addons and their settings in the World of Warcraft folder are not touched.",
                primary: "Uninstall", secondary: "Cancel", showLogo: true))
            return false;

        foreach (var shortcut in new[] { StartMenuShortcut, DesktopShortcut })
        {
            try { File.Delete(shortcut); } catch { /* already gone or in use: harmless */ }
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        // App settings in %AppData%\DrangedAddons are kept, so reinstalling picks up where it left off.

        // This .exe is probably the one being removed, so delete the folder just after this process exits.
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
        DarkDialog.Show(owner, "DrangedAddons has been uninstalled", "");
        return true;
    }
}
