using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace DrangedAddons;

/// <summary>
/// Starts games: every launch from this app goes through here (Play, and co-op's host, join and solo restarts). Adds
/// the game profile's launch environment, and, when the overlay is on for the game (its profile) and for this player
/// (their settings), the in-game overlay: see docs/overlay.md.
///
/// The overlay DLL ships inside this exe. It's written to %LOCALAPPDATA%\DrangedAddons\overlay\&lt;hash&gt; and
/// registered for this Windows user as an implicit Vulkan layer that the Vulkan loader only loads into a process
/// started with DRANGED_OVERLAY_ENABLE=1, which only this class sets. Each game started with the overlay gets its own
/// named pipe, which this class serves until the game exits.
/// </summary>
public static class GameStarter
{
    /// <summary>This player's settings for a game (set by the main window at start).</summary>
    public static Func<string, GameState>? PlayerState { get; set; }

    const string LayerKey = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";
    static readonly string OverlayRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrangedAddons", "overlay");
    static readonly object InstallLock = new();
    static string? _layer;
    static bool _installTried;

    /// <summary>
    /// Starts <paramref name="exe"/> for <paramref name="game"/>. <paramref name="env"/> adds variables (co-op's).
    /// Returns the process, which the caller owns; null only if Windows didn't start it (it throws for that too).
    /// </summary>
    public static Process Start(GameDefinition game, string exe, string arguments, IReadOnlyDictionary<string, string>? env = null)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in GameProfiles.Find(game.Id)?.Install.LaunchEnv ?? [])
            if (pair.IndexOf('=') is > 0 and var i && IsAllowed(pair[..i])) variables[pair[..i]] = pair[(i + 1)..];
        foreach (var (name, value) in env ?? new Dictionary<string, string>()) variables[name] = value;

        OverlayConnection? session = null;
        var state = PlayerState?.Invoke(game.Id);
        if (game.HasOverlay && state?.Overlay != false && InstallLayer() is not null)
        {
            session = new OverlayConnection(game, state?.OverlayHotkey);
            session.Listen(); // before the game starts, so the overlay finds the pipe on its first try
            variables["DRANGED_OVERLAY_ENABLE"] = "1";
            variables["DRANGED_OVERLAY_PIPE"] = session.Name;
        }

        var info = new ProcessStartInfo(exe, arguments)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            // Variables need the process started directly; without any, start it the way Explorer would.
            UseShellExecute = variables.Count == 0,
            // A game started directly keeps what it writes to stderr (a Rust panic's message, say) in game-errors.log.
            RedirectStandardError = variables.Count > 0,
        };
        foreach (var (name, value) in variables) info.Environment[name] = value;
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new System.ComponentModel.Win32Exception("Windows didn't start it");
        }
        catch
        {
            session?.Dispose();
            throw;
        }
        if (info.RedirectStandardError)
        {
            var name = game.Name;
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) GameErrors.Write(name, e.Data);
            };
            process.BeginErrorReadLine(); // read as it comes, so the game never waits on a full pipe
        }
        session?.Follow(process.Id);
        return process;
    }

    /// <summary>A profile can't set the overlay's variables, the Vulkan loader's (they load code) or where programs are.</summary>
    static bool IsAllowed(string name) =>
        !name.StartsWith("DRANGED_", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("VK_", StringComparison.OrdinalIgnoreCase)
        && name.ToUpperInvariant() is not ("PATH" or "PATHEXT" or "COMSPEC" or "SYSTEMROOT" or "WINDIR");

    /// <summary>
    /// Writes the overlay DLL and its layer manifest out (once per version) and registers the layer for this Windows
    /// user, dropping registrations of older copies. Returns the manifest's path, or null if this build has no overlay
    /// or it couldn't be installed.
    /// </summary>
    public static string? InstallLayer()
    {
        lock (InstallLock)
        {
            if (_installTried) return _layer;
            _installTried = true;
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var dllStream = assembly.GetManifestResourceStream("DrangedOverlay64.dll");
                using var jsonStream = assembly.GetManifestResourceStream("DrangedOverlay64.json");
                if (dllStream is null || jsonStream is null) return null;
                var dll = new byte[dllStream.Length];
                dllStream.ReadExactly(dll);
                var hash = Convert.ToHexString(SHA256.HashData(dll))[..12].ToLowerInvariant();
                var dir = Path.Combine(OverlayRoot, hash);
                var dllPath = Path.Combine(dir, "DrangedOverlay64.dll");
                var jsonPath = Path.Combine(dir, "DrangedOverlay64.json");
                if (!File.Exists(dllPath) || !File.Exists(jsonPath))
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(dllPath + ".tmp", dll);
                    File.Move(dllPath + ".tmp", dllPath, overwrite: true);
                    using var json = File.Create(jsonPath);
                    jsonStream.CopyTo(json);
                }

                using (var key = Registry.CurrentUser.CreateSubKey(LayerKey))
                {
                    foreach (var name in key.GetValueNames())
                        if (name.StartsWith(OverlayRoot, StringComparison.OrdinalIgnoreCase) && !name.Equals(jsonPath, StringComparison.OrdinalIgnoreCase))
                            key.DeleteValue(name, throwOnMissingValue: false);
                    key.SetValue(jsonPath, 0, RegistryValueKind.DWord); // 0: enabled (still only with DRANGED_OVERLAY_ENABLE=1)
                }

                // Older copies: gone unless a game that's still running has one loaded.
                foreach (var old in Directory.EnumerateDirectories(OverlayRoot).Where(d => !d.Equals(dir, StringComparison.OrdinalIgnoreCase)))
                {
                    try { Directory.Delete(old, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                OverlayLog.Write($"overlay {hash} ready");
                return _layer = jsonPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                OverlayLog.Write("couldn't install the overlay: " + ex.Message);
                return null;
            }
        }
    }
}

/// <summary>What games started from the app write to stderr, in %LOCALAPPDATA%\DrangedAddons\game-errors.log.</summary>
static class GameErrors
{
    static readonly object Lock = new();

    public static void Write(string game, string line)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrangedAddons", "game-errors.log");
            lock (Lock)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{game}] {line}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>The manager's lines in %LOCALAPPDATA%\DrangedAddons\overlay.log, alongside the overlay DLL's own.</summary>
static class OverlayLog
{
    static readonly object Lock = new();

    public static void Write(string line)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrangedAddons", "overlay.log");
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(file);
                writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId} manager] {line}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
