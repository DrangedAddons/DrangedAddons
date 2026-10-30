using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace DrangedAddons;

/// <summary>A member's open lobby as listed to others; Code is null while it's locked. Game: the game id it's for.</summary>
public sealed record CoopLobby(string? Code, bool Locked, bool Password, int Count, string? Game = null);

/// <param name="State">"online", "away" or "ingame" (members appearing offline aren't listed).</param>
/// <param name="Avatar">Hash of the member's picture (see <see cref="Avatars"/>), or null for none.</param>
/// <param name="Game">While in game: which one (a <see cref="GameDefinition"/> id).</param>
public sealed record CoopPlayer(string Id, string Name, CoopLobby? Lobby = null, string State = "online", string? Avatar = null,
    string? Game = null);

/// <summary>This member's friends and friend requests (both ways), as the co-op service keeps them.</summary>
public sealed record CoopFriends(List<CoopPlayer> Friends, List<CoopPlayer> Incoming, List<CoopPlayer> Outgoing)
{
    public static readonly CoopFriends None = new([], [], []);
}

/// <param name="Relation">"friend", "incoming" (they asked you), "outgoing" (you asked them), or null.</param>
public sealed record CoopFound(CoopPlayer Player, string? Relation);

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
    string _state;
    string? _game;
    CoopFriends _friends = CoopFriends.None;
    TaskCompletionSource<List<CoopFound>>? _search;
    string? _searchFor;

    public string Name { get; }

    /// <summary>What others see of this member: "online", "away", "ingame", or "offline" (appear offline).</summary>
    public string State => _state;

    /// <summary>While in game: which game (a <see cref="GameDefinition"/> id).</summary>
    public string? Game => _game;

    /// <summary>The others online, by name. Events below fire on thread-pool threads.</summary>
    public List<CoopPlayer> Players
    {
        get { lock (_players) return _players.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public bool Connected => _ws?.State == WebSocketState.Open;

    /// <summary>Friends and requests; online friends' status and lobby are in <see cref="Players"/>.</summary>
    public CoopFriends Friends => _friends;

    public bool IsFriend(string id) => _friends.Friends.Any(f => f.Id == id);

    /// <summary>Fires when <see cref="Players"/> or <see cref="Friends"/> change.</summary>
    public event Action? Changed;
    public event Action<CoopPlayer, string>? Invited;
    public event Action<CoopPlayer>? Declined;

    public CoopPresence(string key, string name, string state, string? game)
    {
        _key = key;
        Name = name;
        _state = state;
        _game = game;
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
                // The state goes with the connection, so appearing offline never shows for a moment.
                var url = $"{CoopTunnel.RendezvousUrl.TrimEnd('/')}/presence?key={Uri.EscapeDataString(_key)}"
                          + $"&name={Uri.EscapeDataString(Name)}&state={_state}"
                          + (_game is null ? "" : $"&game={Uri.EscapeDataString(_game)}");
                await ws.ConnectAsync(new Uri(url), _stop.Token);
                _ws = ws;
                wait = TimeSpan.FromSeconds(5);
                if (_lobby is not null) await SendStatusAsync();
                await ReceiveAsync(ws);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException) { }
            _ws = null;
            lock (_players) _players.Clear();
            _search?.TrySetResult([]);
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
        (string?)node?["id"] is { } id && (string?)node?["name"] is { } name
            ? new CoopPlayer(id, name, Lobby(node["lobby"]), (string?)node["state"] ?? "online", (string?)node["avatar"],
                (string?)node["game"])
            : null;

    static CoopLobby? Lobby(JsonNode? node) =>
        node is JsonObject
            ? new CoopLobby((string?)node["code"], (bool?)node["locked"] == true, (bool?)node["password"] == true,
                (int?)node["count"] ?? 1, (string?)node["game"])
            : null;

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
            case "friends":
                _friends = new CoopFriends(Profiles(message["friends"]), Profiles(message["incoming"]), Profiles(message["outgoing"]));
                Changed?.Invoke();
                break;
            case "found" when (string?)message["q"] == _searchFor:
                _search?.TrySetResult(message["players"]?.AsArray()
                    .Select(n => Profile(n) is { } p ? new CoopFound(p, (string?)n?["relation"]) : null)
                    .OfType<CoopFound>().ToList() ?? []);
                break;
        }
    }

    /// <summary>A friend or search result: name and picture only; whether they're online comes from <see cref="Players"/>.</summary>
    static CoopPlayer? Profile(JsonNode? node) =>
        (string?)node?["id"] is { } id && (string?)node?["name"] is { } name
            ? new CoopPlayer(id, name, State: "offline", Avatar: (string?)node["avatar"])
            : null;

    static List<CoopPlayer> Profiles(JsonNode? list) =>
        list?.AsArray().Select(Profile).OfType<CoopPlayer>().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? [];

    /// <summary>Members whose name contains <paramref name="query"/> (2+ characters); empty if offline or no answer in 10 s.</summary>
    public async Task<List<CoopFound>> SearchAsync(string query)
    {
        var search = new TaskCompletionSource<List<CoopFound>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _search?.TrySetResult([]); // an older search is no longer wanted
        _search = search;
        _searchFor = query;
        try { await SendAsync(new JsonObject { ["t"] = "search", ["q"] = query }); }
        catch (Exception ex) when (ex is CoopException or WebSocketException or OperationCanceledException) { return []; }
        var done = await Task.WhenAny(search.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        return done == search.Task ? search.Task.Result : [];
    }

    /// <summary>Asks to be friends, or accepts their request.</summary>
    public Task AddFriendAsync(string id) => SendAsync(new JsonObject { ["t"] = "friend", ["to"] = id });

    /// <summary>Removes a friend, or declines or cancels a request.</summary>
    public Task RemoveFriendAsync(string id) => SendAsync(new JsonObject { ["t"] = "unfriend", ["to"] = id });

    /// <summary>Lists (or, with null, unlists) the lobby this member is hosting; kept across reconnects.</summary>
    public async Task SetLobbyAsync(CoopLobby? lobby)
    {
        _lobby = lobby;
        try { await SendStatusAsync(); }
        catch (Exception ex) when (ex is CoopException or WebSocketException or OperationCanceledException) { } // sent on reconnect
    }

    /// <summary>Changes what others see (online, away, in game and which, appear offline); kept across reconnects.</summary>
    public async Task SetStateAsync(string state, string? game)
    {
        if (_state == state && _game == game) return;
        _state = state;
        _game = game;
        try { await SendStatusAsync(); }
        catch (Exception ex) when (ex is CoopException or WebSocketException or OperationCanceledException) { } // sent on reconnect
    }

    Task SendStatusAsync() => SendAsync(new JsonObject
    {
        ["t"] = "status",
        ["state"] = _state,
        ["game"] = _game,
        ["lobby"] = _lobby is { } l
            ? new JsonObject { ["code"] = l.Code, ["locked"] = l.Locked, ["password"] = l.Password, ["count"] = l.Count, ["game"] = l.Game }
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
