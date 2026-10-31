using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DrangedAddons;

/// <summary>
/// Direct co-op between players' PCs without opening ports on any router. Each manager learns its public UDP address
/// from a STUN server, swaps it through the rendezvous Worker (coop-rendezvous/), and fires UDP packets at the other
/// at the same time so each router sees an outgoing packet and lets the other's in (hole punching). A QUIC connection
/// then runs over that path, and the game's TCP connection rides inside it: the joiner's game connects to this app on
/// 127.0.0.1, the host's app connects on to the host's game on 127.0.0.1.
///
/// Both ends make a throwaway certificate per party and swap its fingerprint through the rendezvous; each QUIC side
/// only accepts the certificate it was told about, so nobody else can connect in, or pose as the host, even if they
/// learn the address. The rendezvous never carries game traffic.
/// </summary>
public static class CoopTunnel
{
    /// <summary>The rendezvous Worker; DRANGEDADDONS_COOP_URL overrides it for testing.</summary>
    public static string RendezvousUrl =>
        Environment.GetEnvironmentVariable("DRANGEDADDONS_COOP_URL") is { Length: > 0 } url ? url : "wss://drangedaddons-coop.coop-rendezvous.workers.dev";

    internal static readonly SslApplicationProtocol Alpn = new("azs-coop");

    /// <summary>No 0/O, 1/I: codes get read out over voice chat.</summary>
    const string CodeLetters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string NewCode() => string.Concat(Enumerable.Range(0, 6).Select(_ => CodeLetters[RandomNumberGenerator.GetInt32(CodeLetters.Length)]));

    public static string? NormalizeCode(string text)
    {
        var code = new string(text.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return code.Length == 6 && code.All(CodeLetters.Contains) ? code : null;
    }

    public static bool IsSupported
    {
        get
        {
            UseEmbeddedMsQuic();
            return QuicListener.IsSupported && QuicConnection.IsSupported;
        }
    }

    static int _msQuicHooked;

    /// <summary>
    /// The single-file exe carries msquic.dll as a resource (see EmbedMsQuic in the .csproj), because .NET leaves it
    /// out of single-file bundles and looks for it next to the exe. Writes it to %LocalAppData%\DrangedAddons\msquic,
    /// checks the copy on disk is byte-for-byte the embedded one (that folder is writable by other programs), loads it,
    /// and answers .NET's lookup with it. Must run before anything touches System.Net.Quic.
    /// </summary>
    static void UseEmbeddedMsQuic()
    {
        if (Interlocked.Exchange(ref _msQuicHooked, 1) == 1) return;
        using var embedded = typeof(CoopTunnel).Assembly.GetManifestResourceStream("msquic.dll");
        if (embedded is null) return; // Debug build: the shared runtime has it
        IntPtr handle;
        try
        {
            var bytes = new byte[embedded.Length];
            embedded.ReadExactly(bytes);
            var hash = SHA256.HashData(bytes);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrangedAddons", "msquic",
                Convert.ToHexString(hash)[..12]);
            var path = Path.Combine(dir, "msquic.dll");
            bool Intact() => File.Exists(path) && SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(hash);
            if (!Intact())
            {
                Directory.CreateDirectory(dir);
                var temp = path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(temp, bytes);
                try { File.Move(temp, path, overwrite: true); }
                catch (IOException) { File.Delete(temp); } // another copy of the app has it loaded
                if (!Intact()) return; // IsSupported then says QUIC isn't available
            }
            // Load now, straight after the check, so the file can't be swapped between checking and loading.
            handle = System.Runtime.InteropServices.NativeLibrary.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException)
        {
            return;
        }
        System.Runtime.Loader.AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
            Path.GetFileName(name).StartsWith("msquic", StringComparison.OrdinalIgnoreCase) ? handle : IntPtr.Zero;
    }

    /// <summary>A free TCP port on 127.0.0.1 (for the host's game to listen on).</summary>
    public static int FreeLocalPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // ---- Signalling ----

    /// <summary>
    /// One side's ways in: its address as the internet sees it, on its own network (the rendezvous only passes that
    /// on to someone behind the same router), and its certificate's fingerprint.
    /// </summary>
    internal sealed record Candidates(IPEndPoint Public, IPEndPoint? Local, string Thumbprint)
    {
        public JsonObject ToJson() => new()
        {
            ["public"] = Public.ToString(), ["local"] = Local?.ToString(), ["cert"] = Thumbprint,
        };

        public static Candidates? FromJson(JsonNode? data) =>
            data is null || !IPEndPoint.TryParse((string?)data["public"] ?? "", out var pub) || (string?)data["cert"] is not { Length: 64 } cert
                ? null
                : new Candidates(pub, IPEndPoint.TryParse((string?)data["local"] ?? "", out var local) ? local : null, cert);

        /// <summary>Where to reach this side from <paramref name="me"/>: its own-network address when both share one router.</summary>
        public IPEndPoint ReachFrom(Candidates me) => Local is { } local && Public.Address.Equals(me.Public.Address) ? local : Public;
    }

    internal sealed class Rendezvous : IAsyncDisposable
    {
        readonly ClientWebSocket _ws = new();
        readonly SemaphoreSlim _send = new(1, 1);

        public static async Task<Rendezvous> ConnectAsync(string query, CancellationToken cancel)
        {
            var r = new Rendezvous();
            r._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await r._ws.ConnectAsync(new Uri($"{RendezvousUrl.TrimEnd('/')}/{query}"), cancel);
            return r;
        }

        public async Task SendAsync(JsonObject message, CancellationToken cancel)
        {
            await _send.WaitAsync(cancel);
            try { await _ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, cancel); }
            finally { _send.Release(); }
        }

        /// <summary>Sends if still connected; a party update that doesn't arrive isn't worth failing over.</summary>
        public async Task TrySendAsync(JsonObject message)
        {
            try { await SendAsync(message, CancellationToken.None); }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException) { }
        }

        /// <summary>The next message; null when the rendezvous closed.</summary>
        public async Task<JsonNode?> ReceiveAsync(CancellationToken cancel)
        {
            var buffer = new byte[8192];
            using var message = new MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result;
                try { result = await _ws.ReceiveAsync(buffer, cancel); }
                catch (WebSocketException) { return null; }
                if (result.MessageType == WebSocketMessageType.Close) return null;
                message.Write(buffer, 0, result.Count);
                if (message.Length > 65536) return null;
                if (!result.EndOfMessage) continue;
                try { return JsonNode.Parse(message.ToArray()); }
                catch (JsonException) { message.SetLength(0); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", new CancellationTokenSource(1000).Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
            _ws.Dispose();
        }
    }

    // ---- Hole punching ----

    /// <summary>A UDP socket on a fresh port and how it can be reached; the port is handed to QUIC after punching.</summary>
    internal static async Task<(Socket Socket, Candidates Me)> OpenAsync(string thumbprint, CancellationToken cancel)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var mapped = await Stun.MappedAddressAsync(socket, cancel);
        if (mapped is null)
        {
            socket.Dispose();
            throw new CoopException("Couldn't find this PC's internet address.");
        }
        return (socket, new Candidates(mapped, new IPEndPoint(LanAddress(), port), thumbprint));
    }

    /// <summary>Sends a burst of small packets so this side's router lets the peer's packets in, then frees the port.</summary>
    internal static async Task PunchAsync(Socket socket, IPEndPoint peer, CancellationToken cancel)
    {
        var ping = "azs-punch"u8.ToArray();
        for (var i = 0; i < 15; i++)
        {
            try { await socket.SendToAsync(ping, SocketFlags.None, peer, cancel); }
            catch (SocketException) { }
            await Task.Delay(100, cancel);
        }
        socket.Dispose();
    }

    static IPAddress LanAddress()
    {
        // The address Windows would use to reach the internet; nothing is sent.
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            probe.Connect("1.1.1.1", 53);
            return ((IPEndPoint)probe.LocalEndPoint!).Address;
        }
        catch (SocketException) { return IPAddress.Loopback; }
    }

    // ---- QUIC ----

    internal static T ConnectionDefaults<T>(T options) where T : QuicConnectionOptions
    {
        options.DefaultStreamErrorCode = 1;
        options.DefaultCloseErrorCode = 0;
        options.IdleTimeout = TimeSpan.FromSeconds(30);
        options.KeepAliveInterval = TimeSpan.FromSeconds(5); // also keeps both routers' UDP mappings open
        return options;
    }

    internal static X509Certificate2 NewCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=DrangedAddons co-op", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
        // Schannel needs the key in a form it can load, which an ephemeral key isn't: round-trip through PFX.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    /// <summary>What a lobby password is checked by: salted with the code, so it isn't the same hash in every lobby.</summary>
    internal static string PasswordHash(string code, string password) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"drangedaddons-lobby:{code}:{password}")));

    internal static string Thumbprint(X509Certificate cert) => Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));

    /// <summary>True only for the certificate whose fingerprint came through the rendezvous.</summary>
    internal static bool IsExpected(X509Certificate? cert, string expected) =>
        cert is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Thumbprint(cert)), Encoding.ASCII.GetBytes(expected.ToUpperInvariant()));

    /// <summary>Copies both ways until either side closes.</summary>
    internal static async Task PumpAsync(Stream a, Stream b)
    {
        var aToB = a.CopyToAsync(b);
        var bToA = b.CopyToAsync(a);
        try { await Task.WhenAny(aToB, bToA); }
        catch { }
        await a.DisposeAsync();
        await b.DisposeAsync();
        try { await Task.WhenAll(aToB, bToA); } catch { /* the other copy fails once its stream is gone */ }
    }
}

/// <summary>A co-op step failed; <see cref="Reason"/> is the co-op service's refusal (password, locked …) when there was one.</summary>
/// <summary>
/// Co-op failures with their full details, for when "Couldn't connect" needs explaining: %LocalAppData%\DrangedAddons\coop.log,
/// kept under 256 KB. Holds no keys or passwords (exception text and the lobby code only).
/// </summary>
public static class CoopLog
{
    static readonly object Lock = new();

    public static void Write(string what, Exception ex)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrangedAddons", "coop.log");
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {what}: {ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class CoopException(string message, string? reason = null) : Exception(message)
{
    public string? Reason { get; } = reason;
}

public enum MemberState { Connecting, Connected, Failed }

/// <summary>A lobby chat message. From is the name the co-op service has for the sender, not one the sender chose per message.</summary>
public sealed record ChatLine(string From, bool FromHost, string Text);

/// <summary>A player in a lobby: Id within this lobby; Member their hashed member id, as checked by the co-op service.</summary>
/// <param name="Ping">Round trip to the host over the direct link, in ms (the last 10 s on average); null until measured.</param>
public sealed record PartyMember(string Id, string Name, MemberState State, string? Member = null, int? Ping = null);

/// <summary>What everyone in a party sees: the host's name, who's in it, and whether the host has started the game.</summary>
public sealed record PartyInfo(string Host, IReadOnlyList<PartyMember> Members, bool Started);

/// <summary>
/// The host's side: keeps the party open on the rendezvous, punches a path to each player who joins, accepts their
/// QUIC connection (and only theirs), and forwards each stream to the game's port. Keeps everyone's party list
/// current, starts everyone's game together, and can remove a player.
/// </summary>
public sealed class CoopHost : IAsyncDisposable
{
    readonly CancellationTokenSource _stop = new();
    readonly X509Certificate2 _cert = CoopTunnel.NewCertificate();
    readonly int _gamePort;
    readonly string _name;
    readonly object _lock = new();
    readonly Dictionary<string, (Socket Socket, CoopTunnel.Candidates Me)> _pending = new();
    readonly Dictionary<string, PartyMember> _members = new();
    readonly Dictionary<string, QuicConnection> _connections = new();
    /// <summary>Round trips measured to each player since the last telemetry report, in ms.</summary>
    readonly Dictionary<string, List<double>> _pings = new();
    CoopTunnel.Rendezvous? _rendezvous;

    public string Code { get; }
    public bool Started { get; private set; }
    /// <summary>Locked: nobody new gets in except members invited from then on.</summary>
    public bool Locked { get; private set; }
    public bool HasPassword { get; private set; }

    /// <summary>A line for the window ("Lexible connected" …). Raised on a thread-pool thread.</summary>
    public event Action<string>? Status;
    /// <summary>The party changed. Raised on a thread-pool thread.</summary>
    public event Action<PartyInfo>? PartyChanged;
    public event Action<ChatLine>? Chat;

    CoopHost(string code, int gamePort, string name)
    {
        Code = code;
        _gamePort = gamePort;
        _name = name;
    }

    public PartyInfo Party
    {
        get { lock (_lock) return new PartyInfo(_name, [.. _members.Values], Started); }
    }

    /// <summary>Opens a party; the game must be started with WOW_ROGUE_COOP_HOST = <paramref name="gamePort"/>.</summary>
    /// <param name="game">The game's id, so the platform dashboard knows which game the session is.</param>
    public static async Task<CoopHost> StartAsync(int gamePort, string name, CancellationToken cancel, string game = "azeroth-survivors")
    {
        for (var attempt = 0; ; attempt++)
        {
            var host = new CoopHost(CoopTunnel.NewCode(), gamePort, name);
            host._rendezvous = await CoopTunnel.Rendezvous.ConnectAsync(
                $"lobby/{host.Code}?role=host&name={Uri.EscapeDataString(name)}&game={Uri.EscapeDataString(game)}", cancel);
            var first = await host._rendezvous.ReceiveAsync(cancel);
            if ((string?)first?["t"] == "hosting")
            {
                _ = host.RunAsync();
                _ = host.ReportAsync();
                return host;
            }
            await host.DisposeAsync();
            if ((string?)first?["why"] != "taken" || attempt >= 3)
                throw new CoopException("Couldn't open a lobby.");
        }
    }

    /// <summary>
    /// Lock and password, enforced by the co-op service. Only a hash of the password (salted with the code) is sent;
    /// joiners send the same hash, so the password itself never leaves the players' PCs. Invited members get in
    /// regardless (<see cref="AllowAsync"/>).
    /// </summary>
    public async Task SetSettingsAsync(bool locked, string? password)
    {
        Locked = locked;
        HasPassword = !string.IsNullOrEmpty(password);
        await _rendezvous!.TrySendAsync(new JsonObject
        {
            ["t"] = "settings", ["locked"] = locked, ["pw"] = HasPassword ? CoopTunnel.PasswordHash(Code, password!) : null,
        });
    }

    /// <summary>Lobby chat: goes to everyone through the co-op service, which echoes it back (so order matches everyone's).</summary>
    public Task SendChatAsync(string text) => _rendezvous!.TrySendAsync(new JsonObject { ["t"] = "chat", ["text"] = text });

    /// <summary>Lets an invited member in even while the lobby is locked or has a password.</summary>
    public Task AllowAsync(string member) => _rendezvous!.TrySendAsync(new JsonObject { ["t"] = "allow", ["member"] = member });

    /// <summary>Tells everyone's manager to start their game (the host's own is started by the window).</summary>
    public async Task StartGameAsync()
    {
        Started = true;
        await _rendezvous!.TrySendAsync(new JsonObject { ["t"] = "start" });
        await SendPartyAsync();
    }

    /// <summary>
    /// Sends a player to another lobby this host has opened (<paramref name="code"/>): their manager leaves this one
    /// and joins that one. They drop out of this party once they've left.
    /// </summary>
    public Task MoveAsync(string id, string code) => _rendezvous!.TrySendAsync(new JsonObject { ["t"] = "move", ["to"] = id, ["code"] = code });

    /// <summary>Removes a player: closes their connection to this game and tells their manager they were removed.</summary>
    public async Task KickAsync(string id)
    {
        QuicConnection? connection;
        PartyMember? member;
        lock (_lock)
        {
            _connections.Remove(id, out connection);
            _members.Remove(id, out member);
            if (_pending.Remove(id, out var pending)) pending.Socket.Dispose();
        }
        await _rendezvous!.TrySendAsync(new JsonObject { ["t"] = "kick", ["to"] = id });
        if (connection is not null)
        {
            try { await connection.CloseAsync(0); await connection.DisposeAsync(); }
            catch (QuicException) { }
        }
                await SendPartyAsync();
    }

    void SetMember(string id, Func<PartyMember?, PartyMember?> change)
    {
        lock (_lock)
        {
            var next = change(_members.GetValueOrDefault(id));
            if (next is null) _members.Remove(id);
            else _members[id] = next;
        }
        _ = SendPartyAsync();
    }

    async Task SendPartyAsync()
    {
        var party = Party;
        PartyChanged?.Invoke(party);
        var members = new JsonArray();
        foreach (var m in party.Members)
            members.Add(new JsonObject { ["id"] = m.Id, ["name"] = m.Name, ["state"] = m.State.ToString(), ["member"] = m.Member, ["ping"] = m.Ping });
        await _rendezvous!.TrySendAsync(new JsonObject
        {
            ["t"] = "party",
            ["data"] = new JsonObject { ["host"] = party.Host, ["started"] = party.Started, ["members"] = members },
        });
    }

    async Task RunAsync()
    {
        var cancel = _stop.Token;
        try
        {
            while (!cancel.IsCancellationRequested && await _rendezvous!.ReceiveAsync(cancel) is { } message)
            {
                var id = (string?)message["id"] ?? (string?)message["from"] ?? "";
                switch ((string?)message["t"])
                {
                    case "join":
                        var name = (string?)message["name"] ?? "A player";
                        var member = (string?)message["member"];
                        SetMember(id, _ => new PartyMember(id, name, MemberState.Connecting, member));
                                                var opened = await CoopTunnel.OpenAsync(CoopTunnel.Thumbprint(_cert), cancel);
                        lock (_lock) _pending[id] = opened;
                        await _rendezvous.SendAsync(new JsonObject { ["t"] = "signal", ["to"] = id, ["data"] = opened.Me.ToJson() }, cancel);
                        break;
                    case "signal":
                        (Socket Socket, CoopTunnel.Candidates Me) mine;
                        lock (_lock)
                            if (!_pending.Remove(id, out mine)) break;
                        if (CoopTunnel.Candidates.FromJson(message["data"]) is { } peer) _ = AcceptAsync(id, mine.Socket, mine.Me, peer, cancel);
                        else mine.Socket.Dispose();
                        break;
                    case "chat" when (string?)message["text"] is { } text:
                        Chat?.Invoke(new ChatLine((string?)message["from"] ?? "?", (bool?)message["host"] == true, text));
                        break;
                    case "leave":
                        QuicConnection? gone;
                        PartyMember? who;
                        lock (_lock)
                        {
                            if (_pending.Remove(id, out var p)) p.Socket.Dispose();
                            _connections.Remove(id, out gone);
                            _members.Remove(id, out who);
                        }
                        if (gone is not null) _ = gone.DisposeAsync().AsTask();
                                                await SendPartyAsync();
                        break;
                }
            }
            if (!cancel.IsCancellationRequested) Status?.Invoke("Lost the co-op service. Players already in stay connected.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is CoopException or SocketException or WebSocketException)
        {
            Status?.Invoke("Co-op stopped: " + ex.Message);
        }
    }

    async Task AcceptAsync(string id, Socket socket, CoopTunnel.Candidates me, CoopTunnel.Candidates peer, CancellationToken cancel)
    {
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var name = Party.Members.FirstOrDefault(m => m.Id == id)?.Name ?? "A player";
        try
        {
            await CoopTunnel.PunchAsync(socket, peer.ReachFrom(me), cancel);
            QuicConnection connection;
            // One listener per player, on the port punched for them, closed again once they're in: it only ever
            // accepts the certificate that player announced, so nobody else gets a connection through it.
            await using (var listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Any, port),
                ApplicationProtocols = [CoopTunnel.Alpn],
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(CoopTunnel.ConnectionDefaults(new QuicServerConnectionOptions
                {
                    MaxInboundBidirectionalStreams = 16,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ApplicationProtocols = [CoopTunnel.Alpn],
                        ServerCertificate = _cert,
                        ClientCertificateRequired = true,
                        RemoteCertificateValidationCallback = (_, cert, _, _) => CoopTunnel.IsExpected(cert, peer.Thumbprint),
                    },
                })),
            }, cancel))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                connection = await listener.AcceptConnectionAsync(timeout.Token);
            }
            lock (_lock)
            {
                if (!_members.ContainsKey(id)) // kicked or left while connecting
                {
                    _ = connection.DisposeAsync().AsTask();
                    return;
                }
                _connections[id] = connection;
            }
            SetMember(id, m => m is null ? null : m with { State = MemberState.Connected });
            _ = PingAsync(id, connection, cancel);
                        if (Started) await _rendezvous!.TrySendAsync(new JsonObject { ["t"] = "start" });
            while (!cancel.IsCancellationRequested)
            {
                var stream = await connection.AcceptInboundStreamAsync(cancel);
                _ = ForwardToGameAsync(stream);
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            SetMember(id, m => m is null ? null : m with { State = MemberState.Failed });
            Status?.Invoke($"{name} couldn't connect: one of your networks blocks direct connections.");
        }
        catch (Exception ex) when (ex is QuicException or AuthenticationException or ObjectDisposedException)
        {
            bool stillHere;
            lock (_lock) stillHere = _connections.Remove(id) | _members.ContainsKey(id);
            // Their connection ended: they left (or dropped). Free the slot; the lobby chat logs who left.
            if (!cancel.IsCancellationRequested && stillHere) SetMember(id, _ => null);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Measures the round trip to a player every 2 s over its own small stream on the player's link: 8 bytes out,
    /// the player's manager sends them straight back. A player's manager from before this doesn't accept the stream,
    /// so it just has no ping.
    /// </summary>
    async Task PingAsync(string id, QuicConnection connection, CancellationToken cancel)
    {
        try
        {
            QuicStream stream;
            using (var open = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                open.CancelAfter(TimeSpan.FromSeconds(5));
                stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, open.Token);
            }
            await using (stream)
            {
                var sent = new byte[8];
                var back = new byte[8];
                while (!cancel.IsCancellationRequested)
                {
                    var start = System.Diagnostics.Stopwatch.GetTimestamp();
                    BitConverter.TryWriteBytes(sent, start);
                    await stream.WriteAsync(sent, cancel);
                    await stream.ReadExactlyAsync(back, cancel);
                    if (BitConverter.ToInt64(back) == start)
                    {
                        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        lock (_lock)
                        {
                            if (!_members.ContainsKey(id)) return; // left or removed
                            (_pings.TryGetValue(id, out var list) ? list : _pings[id] = []).Add(ms);
                        }
                    }
                    await Task.Delay(2000, cancel);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or QuicException or ObjectDisposedException or EndOfStreamException)
        {
            // An older player's manager, or the player left: no ping.
        }
    }

    /// <summary>
    /// Every 10 s: each player's ping over that time (average, best, worst, jitter) to the lobby on the co-op service
    /// (for the platform dashboard), and the average into the party list so every player sees it.
    /// </summary>
    async Task ReportAsync()
    {
        var cancel = _stop.Token;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancel);
                Dictionary<string, List<double>> window;
                lock (_lock)
                {
                    window = _pings.Where(p => p.Value.Count > 0 && _members.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value.ToList());
                    _pings.Clear();
                }
                var pings = new JsonObject();
                foreach (var (id, ms) in window)
                {
                    var jitter = ms.Count > 1 ? ms.Zip(ms.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
                    pings[id] = new JsonObject { ["avg"] = Math.Round(ms.Average()), ["min"] = Math.Round(ms.Min()), ["max"] = Math.Round(ms.Max()), ["jitter"] = Math.Round(jitter) };
                }
                await _rendezvous!.TrySendAsync(new JsonObject { ["t"] = "telemetry", ["pings"] = pings });
                var changed = false;
                lock (_lock)
                {
                    foreach (var (id, ms) in window)
                    {
                        var avg = (int)Math.Round(ms.Average());
                        if (_members.TryGetValue(id, out var m) && m.Ping != avg)
                        {
                            _members[id] = m with { Ping = avg };
                            changed = true;
                        }
                    }
                }
                if (changed) await SendPartyAsync();
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task ForwardToGameAsync(QuicStream stream)
    {
        // A player can connect while the host's game is still loading: wait for it to start listening.
        TcpClient? tcp = null;
        for (var waited = 0; tcp is null; waited++)
        {
            var attempt = new TcpClient { NoDelay = true };
            try
            {
                await attempt.ConnectAsync(IPAddress.Loopback, _gamePort, _stop.Token);
                tcp = attempt;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                attempt.Dispose();
                if (_stop.IsCancellationRequested || waited >= 90)
                {
                    if (!_stop.IsCancellationRequested) Status?.Invoke("A player reached you, but your game never started hosting.");
                    await stream.DisposeAsync();
                    return;
                }
                if (waited == 0 && Started) Status?.Invoke("A player is waiting for your game to finish loading.");
                await Task.Delay(1000);
            }
        }
        await CoopTunnel.PumpAsync(stream, tcp.GetStream());
        tcp.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_rendezvous is not null) await _rendezvous.DisposeAsync();
        List<QuicConnection> connections;
        lock (_lock)
        {
            foreach (var (socket, _) in _pending.Values) socket.Dispose();
            _pending.Clear();
            connections = [.. _connections.Values];
            _connections.Clear();
        }
        foreach (var connection in connections)
        {
            try { await connection.DisposeAsync(); } catch (QuicException) { }
        }
        _cert.Dispose();
    }
}

/// <summary>
/// The joiner's side: finds the host through the code, punches a path, connects with QUIC (each side checking the
/// other's certificate against the fingerprint it announced), and listens on 127.0.0.1 for the game to connect to.
/// Stays on the rendezvous for the party's life, for the host's party list, start and remove.
/// </summary>
public sealed class CoopJoin : IAsyncDisposable
{
    readonly CancellationTokenSource _stop = new();
    readonly X509Certificate2 _cert = CoopTunnel.NewCertificate();
    CoopTunnel.Rendezvous? _rendezvous;
    QuicConnection? _connection;
    TcpListener? _local;

    /// <summary>The 127.0.0.1 port to give the game as WOW_ROGUE_COOP_JOIN.</summary>
    public int LocalPort { get; private set; }

    /// <summary>This player's id in the party (to mark "you" in the list).</summary>
    public string Id { get; private set; } = "";

    public PartyInfo? Party { get; private set; }

    /// <summary>The host has started (or had already started) the game.</summary>
    public bool Started => Party?.Started == true;

    public event Action<string>? Status;
    public event Action<PartyInfo>? PartyChanged;
    /// <summary>The host started the game: start ours.</summary>
    public event Action? StartRequested;
    /// <summary>The party is over for us (removed, the host left, or the connection dropped), with why.</summary>
    public event Action<string>? Ended;
    /// <summary>The host opened a new lobby with us in it, by its code: leave this one and join that.</summary>
    public event Action<string>? Moved;
    public event Action<ChatLine>? Chat;

    /// <summary>Lobby chat: goes to everyone through the co-op service, which echoes it back.</summary>
    public Task SendChatAsync(string text) => _rendezvous!.TrySendAsync(new JsonObject { ["t"] = "chat", ["text"] = text });

    /// <param name="key">This install's secret key: the co-op service hashes it so the host sees which member joined.</param>
    public static async Task<CoopJoin> ConnectAsync(string code, string name, string key, string? password, Action<string> progress, CancellationToken cancel)
    {
        var join = new CoopJoin();
        try
        {
            await join.ConnectCoreAsync(code, name, key, password, progress, cancel);
            return join;
        }
        catch
        {
            await join.DisposeAsync();
            throw;
        }
    }

    async Task ConnectCoreAsync(string code, string name, string key, string? password, Action<string> progress, CancellationToken cancel)
    {
        progress("Finding the lobby…");
        var query = $"lobby/{code}?role=join&name={Uri.EscapeDataString(name)}&key={Uri.EscapeDataString(key)}";
        if (!string.IsNullOrEmpty(password)) query += "&pw=" + CoopTunnel.PasswordHash(code, password);
        _rendezvous = await CoopTunnel.Rendezvous.ConnectAsync(query, cancel);
        var first = await _rendezvous.ReceiveAsync(cancel);
        if ((string?)first?["t"] != "joined")
        {
            var why = (string?)first?["why"];
            throw new CoopException(why switch
            {
                "no lobby" => "No lobby with that code.",
                "full" => "The lobby is full.",
                "locked" => "The lobby is locked.",
                "password" => "This lobby needs a password.",
                "wrong password" => "Wrong password.",
                "removed" => "The host removed you from this lobby.",
                "slow down" => "Too many wrong passwords. Try again in a minute.",
                "update" => "Update DrangedAddons to join this lobby.",
                _ => "The co-op service refused: " + (why ?? "no answer") + ".",
            }, why);
        }
        Id = (string?)first["id"] ?? "";

        var (socket, me) = await CoopTunnel.OpenAsync(CoopTunnel.Thumbprint(_cert), cancel);
        var localPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        await _rendezvous.SendAsync(new JsonObject { ["t"] = "signal", ["data"] = me.ToJson() }, cancel);

        progress("Waiting for the host…");
        CoopTunnel.Candidates? host = null;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            while (host is null)
            {
                var message = await _rendezvous.ReceiveAsync(wait.Token) ?? throw new CoopException("The host closed the lobby.");
                switch ((string?)message["t"])
                {
                    case "closed": throw new CoopException("The host closed the lobby.");
                    case "kicked": throw new CoopException("The host removed you from this lobby.");
                    case "party": Party = ParseParty(message["data"]); break;
                    case "signal": host = CoopTunnel.Candidates.FromJson(message["data"]); break;
                }
            }
        }

        progress("Connecting directly to the host…");
        var target = host.ReachFrom(me);
        await CoopTunnel.PunchAsync(socket, target, cancel);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                attempt.CancelAfter(TimeSpan.FromSeconds(5));
                _connection = await QuicConnection.ConnectAsync(CoopTunnel.ConnectionDefaults(new QuicClientConnectionOptions
                {
                    RemoteEndPoint = target,
                    LocalEndPoint = new IPEndPoint(IPAddress.Any, localPort),
                    MaxInboundBidirectionalStreams = 1, // the host's ping stream (see CoopHost.PingAsync)
                    ClientAuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        ApplicationProtocols = [CoopTunnel.Alpn],
                        TargetHost = "drangedaddons-coop",
                        ClientCertificates = [_cert],
                        // Self-signed: trust it only if it's the one the host announced through the rendezvous.
                        RemoteCertificateValidationCallback = (_, cert, _, _) => CoopTunnel.IsExpected(cert, host.Thumbprint),
                    },
                }), attempt.Token);
                break;
            }
            catch (AuthenticationException)
            {
                // The certificate wasn't the one the host announced: not the host, so don't keep trying it.
                throw new CoopException("The host's connection failed its security check.");
            }
            // SocketException: an early attempt can reach the host's port a moment before its listener is up, and
            // Windows answers "unreachable". Keep trying until the deadline.
            catch (Exception ex) when (ex is QuicException or SocketException or OperationCanceledException && !cancel.IsCancellationRequested)
            {
                if (DateTime.UtcNow > deadline)
                    throw new CoopException("Couldn't connect to the host: one of your networks blocks direct connections.");
                await Task.Delay(300, cancel);
            }
        }

        _local = new TcpListener(IPAddress.Loopback, 0);
        _local.Start();
        LocalPort = ((IPEndPoint)_local.LocalEndpoint).Port;
        _ = ServeGameAsync();
        _ = FollowPartyAsync();
        _ = EchoPingsAsync();
    }

    /// <summary>Sends the host's ping bytes straight back, so it can time the round trip.</summary>
    async Task EchoPingsAsync()
    {
        var cancel = _stop.Token;
        try
        {
            await using var stream = await _connection!.AcceptInboundStreamAsync(cancel);
            var buffer = new byte[8];
            while (!cancel.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(buffer, cancel);
                await stream.WriteAsync(buffer, cancel);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or QuicException or ObjectDisposedException or EndOfStreamException)
        {
            // The link closed, or a host from before pings: nothing to echo.
        }
    }

    static PartyInfo? ParseParty(JsonNode? data)
    {
        if (data is null) return null;
        var members = data["members"]?.AsArray()
            .Select(m => (string?)m?["id"] is { } id && (string?)m?["name"] is { } name
                ? new PartyMember(id, name, Enum.TryParse<MemberState>((string?)m["state"], out var s) ? s : MemberState.Connecting, (string?)m["member"], (int?)m["ping"])
                : null)
            .OfType<PartyMember>().ToList() ?? [];
        return new PartyInfo((string?)data["host"] ?? "Host", members, (bool?)data["started"] == true);
    }

    async Task FollowPartyAsync()
    {
        var cancel = _stop.Token;
        if (Party is { } early) PartyChanged?.Invoke(early);
        try
        {
            while (!cancel.IsCancellationRequested && await _rendezvous!.ReceiveAsync(cancel) is { } message)
            {
                switch ((string?)message["t"])
                {
                    case "party" when ParseParty(message["data"]) is { } party:
                        var startedNow = party.Started && Party?.Started != true;
                        Party = party;
                        PartyChanged?.Invoke(party);
                        if (startedNow) StartRequested?.Invoke();
                        break;
                    case "chat" when (string?)message["text"] is { } text:
                        Chat?.Invoke(new ChatLine((string?)message["from"] ?? "?", (bool?)message["host"] == true, text));
                        break;
                    case "start":
                        if (Party is { Started: false } p) Party = p with { Started = true };
                        StartRequested?.Invoke();
                        break;
                    case "kicked":
                        Ended?.Invoke("The host removed you from the lobby.");
                        return;
                    case "closed":
                        Ended?.Invoke("The host closed the lobby.");
                        return;
                    case "move" when CoopTunnel.NormalizeCode((string?)message["code"] ?? "") is { } code:
                        Moved?.Invoke(code);
                        return;
                }
            }
            if (!cancel.IsCancellationRequested)
                Status?.Invoke("Lost the co-op service. You're still connected to the host.");
        }
        catch (OperationCanceledException) { }
    }

    async Task ServeGameAsync()
    {
        var cancel = _stop.Token;
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var game = await _local!.AcceptTcpClientAsync(cancel);
                game.NoDelay = true;
                var stream = await _connection!.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancel);
                _ = Task.Run(async () =>
                {
                    await CoopTunnel.PumpAsync(game.GetStream(), stream);
                    game.Dispose();
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (QuicException)
        {
            if (!cancel.IsCancellationRequested) Ended?.Invoke("Lost the connection to the host.");
        }
        catch (SocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _local?.Stop();
        if (_rendezvous is not null) await _rendezvous.DisposeAsync();
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); } catch (QuicException) { }
        }
        _cert.Dispose();
    }
}

/// <summary>Asks a public STUN server what address and port a UDP socket appears as on the internet (RFC 5389).</summary>
static class Stun
{
    static readonly (string Host, int Port)[] Servers = [("stun.cloudflare.com", 3478), ("stun.l.google.com", 19302), ("stun1.l.google.com", 19302)];

    public static async Task<IPEndPoint?> MappedAddressAsync(Socket socket, CancellationToken cancel)
    {
        foreach (var (host, port) in Servers)
        {
            IPAddress? server;
            try { server = (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancel)).FirstOrDefault(); }
            catch (SocketException) { continue; }
            if (server is null) continue;

            var request = new byte[20];
            request[1] = 1; // Binding Request
            BitConverter.GetBytes(IPAddress.HostToNetworkOrder(0x2112A442)).CopyTo(request, 4);
            RandomNumberGenerator.Fill(request.AsSpan(8, 12));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await socket.SendToAsync(request, SocketFlags.None, new IPEndPoint(server, port), cancel);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(1.5));
                var buffer = new byte[1024];
                try
                {
                    while (true)
                    {
                        var got = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
                        // Only the server we asked, answering the transaction we sent.
                        if (got.RemoteEndPoint is IPEndPoint from && from.Address.Equals(server) && got.ReceivedBytes >= 20
                            && buffer.AsSpan(8, 12).SequenceEqual(request.AsSpan(8, 12))
                            && Parse(buffer.AsSpan(0, got.ReceivedBytes)) is { } mapped)
                            return mapped;
                    }
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
                catch (SocketException) { break; } // Windows reports an ICMP "port unreachable" this way
            }
        }
        return null;
    }

    static IPEndPoint? Parse(ReadOnlySpan<byte> reply)
    {
        for (var i = 20; i + 4 <= reply.Length;)
        {
            var type = (reply[i] << 8) | reply[i + 1];
            var length = (reply[i + 2] << 8) | reply[i + 3];
            var value = reply.Slice(i + 4, Math.Min(length, reply.Length - i - 4));
            if (value.Length >= 8 && value[1] == 1 && type is 0x0020 or 0x0001)
            {
                var xor = type == 0x0020;
                var port = ((value[2] << 8) | value[3]) ^ (xor ? 0x2112 : 0);
                byte[] ip = [value[4], value[5], value[6], value[7]];
                if (xor) { ip[0] ^= 0x21; ip[1] ^= 0x12; ip[2] ^= 0xA4; ip[3] ^= 0x42; }
                return new IPEndPoint(new IPAddress(ip), port);
            }
            i += 4 + ((length + 3) & ~3);
        }
        return null;
    }
}
