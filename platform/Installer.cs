using System.Diagnostics;
using System.Runtime.InteropServices;
using DrangedAddons;
using Microsoft.Win32;

namespace DrangedPlatform;

/// <summary>
/// Installs per-user the way DrangedAddons does: %LocalAppData%\Programs\DrangedPlatform, Start menu and desktop
/// shortcuts, and an entry in Settings → Apps. Running a newer DrangedPlatform.exe from anywhere upgrades the
/// installed copy. No self-update: it's an admin tool, updated by running a new build.
/// </summary>
public static class Installer
{
    const string AppName = "DrangedAddons Platform";
    const string ExeName = "DrangedPlatform.exe";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DrangedPlatform";

    public static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DrangedPlatform");

    static string InstalledExe => Path.Combine(InstallDir, ExeName);
    static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    /// <summary>This process's .exe; null when running from `dotnet run` or a build folder (a .dll beside it).</summary>
    static string? ExePath =>
        Environment.ProcessPath is { } p && p.EndsWith(ExeName, StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.ChangeExtension(p, ".dll"))
            ? p : null;

    public static string Version =>
        typeof(Installer).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public static bool IsRunningInstalled =>
        ExePath is { } exe && Path.GetFullPath(exe).Equals(InstalledExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns true to carry on starting this process, false when it handed over to the installed copy.</summary>
    public static bool HandleStartup(string[] args)
    {
        DeleteSetAside();
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Uninstall();
            return false;
        }
        // Build folders, and DRANGEDPLATFORM_DEV=1 for a test build, run in place.
        if (ExePath is not { } exe || Environment.GetEnvironmentVariable("DRANGEDPLATFORM_DEV") == "1") return true;

        if (IsRunningInstalled)
        {
            Register();
            return true;
        }

        if (File.Exists(InstalledExe))
        {
            var installed = FileVersionInfo.GetVersionInfo(InstalledExe).FileVersion ?? "0";
            if (Compare(Version, installed) > 0)
            {
                CopyIn(exe);
                Register();
                Launch("--replace");
            }
            else Launch("");
            return false;
        }

        if (!DarkDialog.Show(null, $"Install {AppName}?",
                $"It will be installed for your Windows account in\n{InstallDir}\n" +
                "with Start menu and desktop shortcuts, and can be removed from Settings → Apps.\n\n" +
                "Choose \"Just run it\" to use it this once without installing.",
                primary: "Install", secondary: "Just run it", showLogo: true))
            return true;

        CopyIn(exe);
        Register();
        CreateShortcut(StartMenuShortcut);
        CreateShortcut(DesktopShortcut);
        DarkDialog.Show(null, $"{AppName} is installed",
            "Open it from the Start menu or the desktop shortcut from now on.\n\n" +
            $"You can delete the file you ran ({exe}).",
            primary: "Open it", showLogo: true);
        Launch("");
        return false;
    }

    /// <summary>"0.2.0" vs "0.10.0" by number, not text.</summary>
    static int Compare(string a, string b)
    {
        static int[] Parts(string v) => v.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).Concat([0, 0, 0]).Take(3).ToArray();
        var (x, y) = (Parts(a), Parts(b));
        for (var i = 0; i < 3; i++)
            if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return 0;
    }

    /// <summary>A running installed copy can't be overwritten but can be renamed: set it aside, then copy in.</summary>
    static void CopyIn(string sourceExe)
    {
        Directory.CreateDirectory(InstallDir);
        if (File.Exists(InstalledExe))
            File.Move(InstalledExe, InstalledExe + ".old", overwrite: true);
        File.Copy(sourceExe, InstalledExe);
    }

    static void DeleteSetAside()
    {
        try { File.Delete(InstalledExe + ".old"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static void Launch(string args) =>
        Process.Start(new ProcessStartInfo(InstalledExe, args) { UseShellExecute = true, WorkingDirectory = InstallDir });

    static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "DrangedAddons");
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
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
            link.Description = "Status of the DrangedAddons co-op platform: games, connections, Cloudflare and security";
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    public static void Uninstall()
    {
        if (!DarkDialog.Show(null, $"Uninstall {AppName}?", "The stats token in %AppData%\\DrangedAddons is kept.",
                primary: "Uninstall", secondary: "Cancel", showLogo: true))
            return;
        foreach (var shortcut in new[] { StartMenuShortcut, DesktopShortcut })
        {
            try { File.Delete(shortcut); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
        DarkDialog.Show(null, $"{AppName} has been uninstalled", "");
    }
}
