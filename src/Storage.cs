using System.Text.Json;

namespace DrangedAddons;

/// <summary>JSON files under %AppData%\DrangedAddons.</summary>
public static class Storage
{
    // Test builds (DRANGEDADDONS_DEV=1) keep their own settings so they never touch the real app's.
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetEnvironmentVariable("DRANGEDADDONS_DEV") == "1" ? "DrangedAddons-dev" : "DrangedAddons");

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static T Load<T>(string file) where T : new()
    {
        try
        {
            var path = Path.Combine(Dir, file);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch
        {
            // Corrupt or unreadable file: start fresh rather than refusing to open.
        }
        return new T();
    }

    public static void Save<T>(string file, T value)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, file);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
