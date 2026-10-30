using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace DrangedAddons;

public sealed record CoopPlayer(string Id, string Name);

/// <summary>
/// Keeps this manager listed as online on the co-op rendezvous (/presence) while it runs, so friends can see it and
/// invite it to co-op; reconnects on its own when the connection drops. An invite carries a lobby code, which the
/// invited side joins exactly like a typed one (<see cref="CoopJoin"/>).
/// </summary>
public sealed class CoopPresence : IDisposable
{
    readonly CancellationTokenSource _stop = new();
    readonly string _id;
    readonly Dictionary<string, CoopPlayer> _players = new();
    readonly SemaphoreSlim _send = new(1, 1);
    ClientWebSocket? _ws;

    public string Name { get; }

    /// <summary>The others online, by name. Events below fire on thread-pool threads.</summary>
    public List<CoopPlayer> Players
    {
        get { lock (_players) return _players.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public bool Connected => _ws?.State == WebSocketState.Open;

    public event Action? Changed;
    public event Action<CoopPlayer, string>? Invited;
    public event Action<CoopPlayer>? Declined;

    public CoopPresence(string id, string name)
    {
        _id = id;
        Name = name;
        _ = RunAsync();
    }

    async Task RunAsync()
    {
        var wait = TimeSpan.FromSeconds(5);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                var url = $"{CoopTunnel.RendezvousUrl.TrimEnd('/')}/presence?id={Uri.EscapeDataString(_id)}&name={Uri.EscapeDataString(Name)}";
                await ws.ConnectAsync(new Uri(url), _stop.Token);
                _ws = ws;
                wait = TimeSpan.FromSeconds(5);
                await ReceiveAsync(ws);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException) { }
            _ws = null;
            lock (_players) _players.Clear();
            Changed?.Invoke();
            try { await Task.Delay(wait, _stop.Token); }
            catch (OperationCanceledException) { return; }
            wait = TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds * 2, 120));
        }
    }

    async Task ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[16384];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, _stop.Token);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            JsonNode? json = null;
            try { json = JsonNode.Parse(message.ToArray()); }
            catch (System.Text.Json.JsonException) { }
            message.SetLength(0);
            if (json is not null) Handle(json);
        }
    }

    static CoopPlayer? Player(JsonNode? node) =>
        (string?)node?["id"] is { } id && (string?)node?["name"] is { } name ? new CoopPlayer(id, name) : null;

    void Handle(JsonNode message)
    {
        switch ((string?)message["t"])
        {
            case "online":
                lock (_players)
                {
                    _players.Clear();
                    foreach (var p in message["players"]?.AsArray().Select(Player).OfType<CoopPlayer>() ?? []) _players[p.Id] = p;
                }
                Changed?.Invoke();
                break;
            case "here" when Player(message["player"]) is { } here:
                lock (_players) _players[here.Id] = here;
                Changed?.Invoke();
                break;
            case "gone" when (string?)message["id"] is { } id:
                lock (_players) _players.Remove(id);
                Changed?.Invoke();
                break;
            case "invite" when Player(message["from"]) is { } from && (string?)message["code"] is { } code:
                Invited?.Invoke(from, code);
                break;
            case "declined" when Player(message["from"]) is { } from:
                Declined?.Invoke(from);
                break;
        }
    }

    public Task InviteAsync(CoopPlayer to, string code) => SendAsync(new JsonObject { ["t"] = "invite", ["to"] = to.Id, ["code"] = code });

    public Task DeclineAsync(CoopPlayer to) => SendAsync(new JsonObject { ["t"] = "decline", ["to"] = to.Id });

    async Task SendAsync(JsonObject message)
    {
        if (_ws is not { State: WebSocketState.Open } ws) throw new CoopException("not connected to the co-op service");
        await _send.WaitAsync();
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, _stop.Token); }
        finally { _send.Release(); }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _ws?.Abort(); } catch (ObjectDisposedException) { }
    }
}
