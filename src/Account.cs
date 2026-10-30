using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DrangedAddons;

/// <summary>The status a member picks. In game and idle-away are worked out on top of Online (<see cref="Account.Effective"/>).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AccountStatus>))]
public enum AccountStatus { Online, Away, AppearOffline }

/// <summary>
/// The member's local account: a display name, a picture and a status. Nothing is registered anywhere; the install's
/// secret co-op key (settings CoopId) is the identity, and others only see a hash of it.
/// </summary>
public sealed class AccountSettings
{
    /// <summary>Null: the hero last played in Azeroth Survivors, else the Windows user name.</summary>
    public string? Name { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Online;

    /// <summary>Hash of the picture in avatar.jpg (null: no picture), and of the one last uploaded to the co-op service.</summary>
    public string? Picture { get; set; }
    public string? PictureUploaded { get; set; }

    /// <summary>The first-run welcome (pick a name and picture) has been shown.</summary>
    public bool Welcomed { get; set; }
}

/// <summary>What others see: "online", "away", "ingame" or "offline" (the wire values on the co-op service).</summary>
public static class Account
{
    public const int NameLength = 24;
    const int PictureSize = 128;
    static readonly TimeSpan IdleAway = TimeSpan.FromMinutes(10);

    public static string PictureFile => Path.Combine(Storage.Dir, "avatar.jpg");

    /// <summary>
    /// The state to show: appear offline wins, then playing (<paramref name="game"/>: which game, or null), then away
    /// (picked, or idle for 10 minutes).
    /// </summary>
    public static string Effective(AccountStatus status, string? game) =>
        status == AccountStatus.AppearOffline ? "offline"
        : game is not null ? "ingame"
        : status == AccountStatus.Away || IdleFor() >= IdleAway ? "away"
        : "online";

    /// <param name="game">For in game: the game's id, to name it ("In game · World of Warcraft").</param>
    public static string Label(string? state, string? game = null) => state switch
    {
        "online" => "Online",
        "away" => "Away",
        "ingame" => GameDefinition.Find(game) is { } g ? $"In game · {g.Name}" : "In game",
        _ => "Offline",
    };

    public static Brush StateBrush(string? state) => state switch
    {
        "online" => Frozen("#3BA55D"),
        "away" => Frozen("#E8A33A"),
        "ingame" => Frozen("#4C9BF0"),
        _ => Frozen("#747F8D"),
    };

    static readonly Dictionary<string, Brush> Brushes = new();

    static Brush Frozen(string hex)
    {
        if (Brushes.TryGetValue(hex, out var brush)) return brush;
        var made = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        made.Freeze();
        return Brushes[hex] = made;
    }

    /// <summary>Whether the game is running (any copy of its program).</summary>
    public static bool IsRunning(string exe)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Time since the last keyboard or mouse input on this PC.</summary>
    static TimeSpan IdleFor()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) : TimeSpan.Zero;
    }

    /// <summary>
    /// A picture for the account from an image file: the middle square, 128 px, as a JPEG (a few KB, well under the
    /// co-op service's 64 KB). Throws if the file isn't an image WPF can read.
    /// </summary>
    public static byte[] MakePicture(string file)
    {
        var source = new BitmapImage();
        source.BeginInit();
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        source.UriSource = new Uri(file);
        source.EndInit();
        var side = Math.Min(source.PixelWidth, source.PixelHeight);
        BitmapSource square = new CroppedBitmap(source,
            new Int32Rect((source.PixelWidth - side) / 2, (source.PixelHeight - side) / 2, side, side));
        if (side != PictureSize)
            square = new TransformedBitmap(square, new ScaleTransform((double)PictureSize / side, (double)PictureSize / side));
        // JPEG has no transparency: flatten onto the app's panel colour so transparent PNGs don't turn black.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x28, 0x2D, 0x37)), null, new Rect(0, 0, PictureSize, PictureSize));
            dc.DrawImage(square, new Rect(0, 0, PictureSize, PictureSize));
        }
        var flat = new RenderTargetBitmap(PictureSize, PictureSize, 96, 96, PixelFormats.Pbgra32);
        flat.Render(visual);
        var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(flat));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Short hash naming a picture (the same one the co-op service uses).</summary>
    public static string Hash(byte[] picture) => Convert.ToHexStringLower(SHA256.HashData(picture)[..6]);

    public static ImageSource? Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public static ImageSource? LoadOwnPicture()
    {
        try
        {
            return File.Exists(PictureFile) ? Decode(File.ReadAllBytes(PictureFile)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Initials for a picture-less avatar: "Dranged" → "D", "Big Bob" → "BB".</summary>
    public static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
}

/// <summary>
/// Other members' pictures, by member id and picture hash: from memory, else from disk (avatars\&lt;id&gt;-&lt;hash&gt;.jpg),
/// else downloaded from the co-op service in the background (<see cref="Loaded"/> fires when one arrives).
/// </summary>
public static class Avatars
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static readonly Dictionary<string, ImageSource?> Memory = new();
    static readonly HashSet<string> Fetching = new();

    static string Dir => Path.Combine(Storage.Dir, "avatars");

    /// <summary>Fires on the UI thread when a picture finished downloading.</summary>
    public static event Action? Loaded;

    /// <summary>The picture if it's here already; otherwise null now and <see cref="Loaded"/> later.</summary>
    public static ImageSource? Get(string id, string? hash)
    {
        if (hash is null || !IsHex(id) || !IsHex(hash)) return null;
        var key = $"{id}-{hash}";
        if (Memory.TryGetValue(key, out var known)) return known;
        var file = Path.Combine(Dir, key + ".jpg");
        try
        {
            if (File.Exists(file)) return Memory[key] = Account.Decode(File.ReadAllBytes(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (Fetching.Add(key)) _ = FetchAsync(id, key, file);
        return null;
    }

    static async Task FetchAsync(string id, string key, string file)
    {
        byte[]? bytes = null;
        try
        {
            using var response = await Http.GetAsync($"{HttpBase}/avatar/{id}");
            if (response.IsSuccessStatusCode) bytes = await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Fetching.Remove(key);
            if (bytes is null || bytes.Length > 64 * 1024 || Account.Decode(bytes) is not { } image) return; // tried again next time it's asked for
            Memory[key] = image;
            try
            {
                Directory.CreateDirectory(Dir);
                // Older pictures of this member are no longer shown anywhere.
                foreach (var old in Directory.EnumerateFiles(Dir, id + "-*.jpg")) File.Delete(old);
                File.WriteAllBytes(file, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            Loaded?.Invoke();
        });
    }

    /// <summary>Sets (or, with null, removes) this member's picture on the co-op service. Returns its hash; throws on failure.</summary>
    public static async Task<string?> UploadAsync(string key, byte[]? picture)
    {
        using var request = new HttpRequestMessage(picture is null ? HttpMethod.Delete : HttpMethod.Put,
            $"{HttpBase}/avatar?key={Uri.EscapeDataString(key)}");
        if (picture is not null)
            request.Content = new ByteArrayContent(picture) { Headers = { ContentType = new("image/jpeg") } };
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return picture is null ? null : Account.Hash(picture);
    }

    /// <summary>The co-op service over HTTPS (it's configured as a wss:// URL for the sockets).</summary>
    static string HttpBase => CoopTunnel.RendezvousUrl.TrimEnd('/').Replace("wss://", "https://").Replace("ws://", "http://");

    static bool IsHex(string s) => s.Length is > 0 and <= 64 && s.All(Uri.IsHexDigit);
}
