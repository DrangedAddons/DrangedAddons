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
            if (Versions.Compare(AppUpdater.CurrentVersion, installed) > 0)
            {
                CopyIn(exe);
                Register();
            }
            Launch();
            return false;
        }

        var answer = MessageBox.Show(
            "Install DrangedAddons?\n\n" +
            $"It will be installed for your Windows account in\n{InstallDir}\n" +
            "with Start menu and desktop shortcuts, and can be removed from Settings → Apps.\n\n" +
            "Choose No to just run it this once without installing.",
            "DrangedAddons", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return true;

        CopyIn(exe);
        Register();
        CreateShortcut(StartMenuShortcut);
        CreateShortcut(DesktopShortcut);
        MessageBox.Show(
            "DrangedAddons is installed. Open it from the Start menu or the desktop shortcut from now on.\n\n" +
            $"You can delete the file you downloaded ({exe}).",
            "DrangedAddons", MessageBoxButton.OK, MessageBoxImage.Information);
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

    static void Launch() =>
        Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir });

    /// <summary>The Settings → Apps entry (per-user, no admin needed).</summary>
    static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
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
    public static bool Uninstall()
    {
        var answer = MessageBox.Show(
            "Uninstall DrangedAddons?\n\nYour installed addons and their settings in the World of Warcraft folder are not touched.",
            "DrangedAddons", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;

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
        MessageBox.Show("DrangedAddons has been uninstalled.", "DrangedAddons", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }
}
