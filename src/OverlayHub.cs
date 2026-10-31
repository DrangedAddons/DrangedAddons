using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DrangedAddons;

/// <summary>
/// The manager's side of every running overlay (docs/overlay.md). The main window says what to show
/// (<see cref="Provide"/>) and runs what overlays ask for (<see cref="Handle"/>); anything that changes what they'd
/// show calls <see cref="Changed"/>, and each overlay gets the new state (only what changed, pictures once each).
/// </summary>
public static class OverlayHub
{
    /// <summary>What an overlay in a game shows.</summary>
    /// <param name="You">{id, name, avatar}.</param>
    /// <param name="Friends">{friends, incoming, invites}; see the overlay's ui.cpp for each field.</param>
    /// <param name="Lobby">The lobby the player is in, or null.</param>
    /// <param name="Overview">{addons:[{name, version, update}]}.</param>
    /// <param name="Pictures">Members' pictures the overlay may draw: id, hash (the picture's version), and the picture.</param>
    public sealed record Snapshot(JsonObject You, JsonNode? Friends, JsonNode? Lobby, JsonNode? Overview,
        IReadOnlyList<(string Id, string Hash, ImageSource Picture)> Pictures);

    /// <summary>Builds a game's snapshot (on the UI thread), by game id.</summary>
    public static Func<string, Snapshot>? Provide { get; set; }

    /// <summary>Runs an overlay's action (on the UI thread): game id, then the message ({a, ...}).</summary>
    public static Action<string, JsonObject>? Handle { get; set; }

    static readonly List<OverlayConnection> Connections = [];
    static DispatcherTimer? _debounce;

    internal static void Add(OverlayConnection connection)
    {
        lock (Connections) Connections.Add(connection);
    }

    internal static void Remove(OverlayConnection connection)
    {
        lock (Connections) Connections.Remove(connection);
    }

    static List<OverlayConnection> All()
    {
        lock (Connections) return [.. Connections];
    }

    /// <summary>Something an overlay shows changed: they're all brought up to date shortly (changes come in bursts).</summary>
    public static void Changed()
    {
        if (Application.Current?.Dispatcher is not { } dispatcher) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Changed);
            return;
        }
        if (All().Count == 0) return;
        _debounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            foreach (var c in All()) c.Push();
        }, dispatcher);
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>A notification in the corner of every running overlay, open or not.</summary>
    public static void Toast(string title, string text)
    {
        foreach (var c in All()) c.Send(new JsonObject { ["t"] = "toast", ["title"] = title, ["text"] = text });
    }

    /// <summary>A picture as 64×64 RGBA (cropped to a square from the middle), base64: what the overlay uploads as a texture.</summary>
    internal static string? Rgba(ImageSource picture)
    {
        if (picture is not BitmapSource bitmap || bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) return null;
        const int size = 64;
        var side = Math.Min(bitmap.PixelWidth, bitmap.PixelHeight);
        var square = new CroppedBitmap(bitmap, new Int32Rect((bitmap.PixelWidth - side) / 2, (bitmap.PixelHeight - side) / 2, side, side));
        var scaled = new TransformedBitmap(square, new ScaleTransform((double)size / side, (double)size / side));
        var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        bgra.CopyPixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        return Convert.ToBase64String(pixels);
    }
}

/// <summary>
/// One game's overlay: a named pipe it connects to (it reconnects if the pipe drops). Sends hello (hotkey, game,
/// modules, you) and state; hears hello, problems and actions. Lives until the game exits.
/// </summary>
sealed class OverlayConnection : IDisposable
{
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _writing = new(1, 1);
    readonly GameDefinition _game;
    readonly string? _hotkey;
    readonly HashSet<string> _sentPictures = [];
    NamedPipeServerStream? _pipe;
    string? _lastState;

    public string Name { get; } = "DrangedOverlay-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public OverlayConnection(GameDefinition game, string? hotkey)
    {
        _game = game;
        _hotkey = hotkey;
        OverlayHub.Add(this);
    }

    public void Listen() => _ = ServeAsync();

    /// <summary>Ends with the game's process.</summary>
    public void Follow(int processId) => _ = Task.Run(async () =>
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            await process.WaitForExitAsync(_stop.Token);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException) { }
        Dispose();
    });

    async Task ServeAsync()
    {
        var cancel = _stop.Token;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                // Buffered both ways: with none, each side's write waits for the other to read, and both write first.
                await using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 256 * 1024, 64 * 1024);
                await pipe.WaitForConnectionAsync(cancel);
                lock (_sentPictures) _sentPictures.Clear();
                _lastState = null;
                _pipe = pipe;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Send(Hello());
                    Push();
                });
                while (await ReadAsync(pipe, cancel) is { } message) Heard(message);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { }
            finally { _pipe = null; }
        }
    }

    JsonObject Hello()
    {
        var (modifiers, key) = OverlayHotkey.Parse(_hotkey) ?? OverlayHotkey.Parse(OverlayHotkey.Default)!.Value;
        return new JsonObject
        {
            ["t"] = "hello", ["game"] = _game.Name, ["gameId"] = _game.Id,
            ["lobbies"] = _game.HasDirectCoop, ["addons"] = true,
            ["you"] = OverlayHub.Provide?.Invoke(_game.Id).You.DeepClone(),
            ["vk"] = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key),
            ["ctrl"] = modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control),
            ["alt"] = modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt),
            ["shift"] = modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift),
        };
    }

    /// <summary>Sends the state if it changed, after any pictures it needs that haven't been sent. On the UI thread.</summary>
    public void Push()
    {
        if (_pipe is null || OverlayHub.Provide is not { } provide) return;
        var snapshot = provide(_game.Id);
        foreach (var (id, hash, picture) in snapshot.Pictures)
        {
            lock (_sentPictures)
                if (!_sentPictures.Add(id + ":" + hash)) continue;
            if (OverlayHub.Rgba(picture) is { } rgba)
                Send(new JsonObject { ["t"] = "avatar", ["id"] = id, ["hash"] = hash, ["w"] = 64, ["h"] = 64, ["rgba"] = rgba });
        }
        var state = new JsonObject
        {
            ["t"] = "state", ["friends"] = snapshot.Friends?.DeepClone(), ["lobby"] = snapshot.Lobby?.DeepClone(),
            ["overview"] = snapshot.Overview?.DeepClone(),
        };
        var text = state.ToJsonString();
        if (text == _lastState) return;
        _lastState = text;
        Send(state);
    }

    void Heard(JsonNode message)
    {
        switch ((string?)message["t"])
        {
            case "hello":
                OverlayLog.Write($"{_game.Name}: overlay {(string?)message["version"]} ({(string?)message["api"]}) connected");
                break;
            case "problem":
                OverlayLog.Write($"{_game.Name}: overlay problem: {(string?)message["text"]}");
                break;
            case "action" when message is JsonObject action:
                OverlayLog.Write($"{_game.Name}: overlay asks: {(string?)action["a"]}");
                Application.Current.Dispatcher.BeginInvoke(() => OverlayHub.Handle?.Invoke(_game.Id, action));
                break;
        }
    }

    /// <summary>Queues a message to the overlay; dropped if it isn't connected. Any thread.</summary>
    public void Send(JsonNode message)
    {
        var pipe = _pipe;
        if (pipe is null) return;
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        _ = Task.Run(async () =>
        {
            await _writing.WaitAsync();
            try
            {
                await pipe.WriteAsync(BitConverter.GetBytes(body.Length), _stop.Token);
                await pipe.WriteAsync(body, _stop.Token); // no flush: on a pipe it waits until the overlay has read it
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
            finally { _writing.Release(); }
        });
    }

    static async Task<JsonNode?> ReadAsync(Stream pipe, CancellationToken cancel)
    {
        var header = new byte[4];
        try { await pipe.ReadExactlyAsync(header, cancel); }
        catch (EndOfStreamException) { return null; }
        var length = BitConverter.ToInt32(header);
        if (length is < 0 or > 1024 * 1024) return null;
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, cancel);
        try { return JsonNode.Parse(body) ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { return new JsonObject(); }
    }

    public void Dispose()
    {
        OverlayHub.Remove(this);
        if (!_stop.IsCancellationRequested) _stop.Cancel();
    }
}
