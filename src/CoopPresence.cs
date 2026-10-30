using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace DrangedAddons;

/// <summary>A member's open lobby as listed to others; Code is null while it's locked.</summary>
public sealed record CoopLobby(string? Code, bool Locked, bool Password, int Count);

public sealed record CoopPlayer(string Id, string Name, CoopLobby? Lobby = null);

/// <summary>
/// Keeps this manager listed as online on the co-op rendezvous (/presence) while it runs, so members can see it,
/// invite it, and see the lobby it's hosting; reconnects on its own when the connection drops. An invite carries a
/// lobby code, which the invited side joins exactly like a typed one (<see cref="CoopJoin"/>).
/// </summary>
public sealed class CoopPresence : IDisposable
{
    readonly CancellationTokenSource _stop = new();
    /// <summary>This install's secret; others only ever see a hash of it as this player's id.</summary>
    readonly string _key;
    readonly Dictionary<string, CoopPlayer> _players = new();
    readonly SemaphoreSlim _send = new(1, 1);
    ClientWebSocket? _ws;
    CoopLobby? _lobby;

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

    public CoopPresence(string key, string name)
    {
        _key = key;
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
                var url = $"{CoopTunnel.RendezvousUrl.TrimEnd('/')}/presence?key={Uri.EscapeDataString(_key)}&name={Uri.EscapeDataString(Name)}";
                await ws.ConnectAsync(new Uri(url), _stop.Token);
                _ws = ws;
                wait = TimeSpan.FromSeconds(5);
                if (_lobby is not null) await SendStatusAsync();
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
        (string?)node?["id"] is { } id && (string?)node?["name"] is { } name ? new CoopPlayer(id, name, Lobby(node["lobby"])) : null;

    static CoopLobby? Lobby(JsonNode? node) =>
        node is JsonObject ? new CoopLobby((string?)node["code"], (bool?)node["locked"] == true, (bool?)node["password"] == true, (int?)node["count"] ?? 1) : null;

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

    /// <summary>Lists (or, with null, unlists) the lobby this member is hosting; kept across reconnects.</summary>
    public async Task SetLobbyAsync(CoopLobby? lobby)
    {
        _lobby = lobby;
        try { await SendStatusAsync(); }
        catch (Exception ex) when (ex is CoopException or WebSocketException or OperationCanceledException) { } // sent on reconnect
    }

    Task SendStatusAsync() => SendAsync(new JsonObject
    {
        ["t"] = "status",
        ["lobby"] = _lobby is { } l
            ? new JsonObject { ["code"] = l.Code, ["locked"] = l.Locked, ["password"] = l.Password, ["count"] = l.Count }
            : null,
    });

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
