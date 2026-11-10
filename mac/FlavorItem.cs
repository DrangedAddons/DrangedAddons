using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DrangedAddons.Mac;

/// <summary>A WoW version in the picker: its Battle.net icon, labelled with how many DrangedAddons addons it has installed.</summary>
public sealed class FlavorItem(GameFlavor flavor) : INotifyPropertyChanged
{
    int _installed;

    public GameFlavor Flavor { get; } = flavor;
    public Bitmap? Icon { get; } = Images.VersionIcon(flavor.Folder);

    public int Installed
    {
        get => _installed;
        set
        {
            if (_installed == value) return;
            _installed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    public string Label => _installed > 0 ? $"{Flavor.DisplayName} ({_installed})" : Flavor.DisplayName;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>The Windows app's images (src/logos, versionicons, gameicons), bundled into the Mac app.</summary>
static class Images
{
    static readonly Dictionary<string, Bitmap?> Cache = new();

    public static Bitmap? Bundled(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        Bitmap? image = null;
        try
        {
            var uri = new Uri($"avares://DrangedAddons/Assets/{path}");
            if (AssetLoader.Exists(uri)) image = new Bitmap(AssetLoader.Open(uri));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // Unreadable: the caller falls back.
        }
        return Cache[path] = image;
    }

    /// <summary>Battle.net's icon for the version, chosen like the logos (PTRs and betas use their live version's).</summary>
    public static Bitmap? VersionIcon(string folder) =>
        WowLocator.ArtKey(folder) is { } key ? Bundled($"versionicons/{key}.png") : null;

    public static Bitmap? Logo(string folder) =>
        WowLocator.ArtKey(folder) is { } key ? Bundled($"logos/{key}.png") : null;
}
