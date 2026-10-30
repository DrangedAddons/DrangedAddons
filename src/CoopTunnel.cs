using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DrangedAddons;

/// <summary>
/// Direct co-op between two players' PCs without opening ports on either router. Both managers learn their public UDP
/// address from a STUN server, swap it through the rendezvous Worker (coop-rendezvous/), and fire UDP packets at each
/// other at the same time so each router sees an outgoing packet and lets the other's in (hole punching). A QUIC
/// connection then runs over that path, and the game's TCP connection rides inside it: the joiner's game connects to
/// this app on 127.0.0.1, the host's app connects on to the host's game on 127.0.0.1.
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

    public static bool IsSupported => QuicListener.IsSupported && QuicConnection.IsSupported;

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

    /// <summary>One side's ways in: its address as the internet sees it, and on its own network.</summary>
    internal sealed record Candidates(IPEndPoint Public, IPEndPoint Local, string? Thumbprint)
    {
        public JsonObject ToJson() => new()
        {
            ["public"] = Public.ToString(), ["local"] = Local.ToString(), ["cert"] = Thumbprint,
        };

        public static Candidates? FromJson(JsonNode? data) =>
            data is null || !IPEndPoint.TryParse((string?)data["public"] ?? "", out var pub) || !IPEndPoint.TryParse((string?)data["local"] ?? "", out var local)
                ? null
                : new Candidates(pub, local, (string?)data["cert"]);

        /// <summary>Where to reach this side from <paramref name="me"/>: its own-network address when both share one router.</summary>
        public IPEndPoint ReachFrom(Candidates me) => Public.Address.Equals(me.Public.Address) ? Local : Public;
    }

    internal sealed class Rendezvous : IAsyncDisposable
    {
        readonly ClientWebSocket _ws = new();
        readonly SemaphoreSlim _send = new(1, 1);

        public static async Task<Rendezvous> ConnectAsync(string code, string role, CancellationToken cancel)
        {
            var r = new Rendezvous();
            r._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await r._ws.ConnectAsync(new Uri($"{RendezvousUrl.TrimEnd('/')}/lobby/{code}?role={role}"), cancel);
            return r;
        }

        public async Task SendAsync(JsonObject message, CancellationToken cancel)
        {
            await _send.WaitAsync(cancel);
            try { await _ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, cancel); }
            finally { _send.Release(); }
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
    internal static async Task<(Socket Socket, Candidates Me)> OpenAsync(string? thumbprint, CancellationToken cancel)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        var mapped = await Stun.MappedAddressAsync(socket, cancel)
                     ?? throw new CoopException("couldn't find this PC's internet address (a STUN server didn't answer)");
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

public sealed class CoopException(string message) : Exception(message);

/// <summary>
/// The host's side: keeps the lobby open on the rendezvous, and for each player who joins with the code, punches a path
/// to them on a fresh UDP port and accepts their QUIC connection there, forwarding each stream to the game's port.
/// </summary>
public sealed class CoopHost : IAsyncDisposable
{
    readonly CancellationTokenSource _stop = new();
    readonly X509Certificate2 _cert = CoopTunnel.NewCertificate();
    readonly int _gamePort;
    readonly Dictionary<string, (System.Net.Sockets.Socket Socket, CoopTunnel.Candidates Me)> _pending = new();
    readonly List<IAsyncDisposable> _open = new();
    CoopTunnel.Rendezvous? _rendezvous;

    public string Code { get; }

    /// <summary>Raised on a thread-pool thread with a line for the window ("Player 1 connected" …).</summary>
    public event Action<string>? Status;

    CoopHost(string code, int gamePort)
    {
        Code = code;
        _gamePort = gamePort;
    }

    /// <summary>Opens a lobby; the game must be started with WOW_ROGUE_COOP_HOST = <paramref name="gamePort"/>.</summary>
    public static async Task<CoopHost> StartAsync(int gamePort, CancellationToken cancel)
    {
        for (var attempt = 0; ; attempt++)
        {
            var host = new CoopHost(CoopTunnel.NewCode(), gamePort);
            host._rendezvous = await CoopTunnel.Rendezvous.ConnectAsync(host.Code, "host", cancel);
            var first = await host._rendezvous.ReceiveAsync(cancel);
            if ((string?)first?["t"] == "hosting")
            {
                _ = host.RunAsync();
                return host;
            }
            await host.DisposeAsync();
            if ((string?)first?["why"] != "taken" || attempt >= 3)
                throw new CoopException("the co-op rendezvous refused: " + ((string?)first?["why"] ?? "no answer"));
        }
    }

    async Task RunAsync()
    {
        var cancel = _stop.Token;
        var players = new Dictionary<string, int>();
        try
        {
            while (!cancel.IsCancellationRequested && await _rendezvous!.ReceiveAsync(cancel) is { } message)
            {
                var id = (string?)message["id"] ?? (string?)message["from"] ?? "";
                switch ((string?)message["t"])
                {
                    case "join":
                        players[id] = players.Count + 1;
                        Status?.Invoke($"Player {players[id]} is connecting…");
                        var opened = await CoopTunnel.OpenAsync(Convert.ToHexString(_cert.GetCertHash(HashAlgorithmName.SHA256)), cancel);
                        _pending[id] = opened;
                        await _rendezvous.SendAsync(new JsonObject { ["t"] = "signal", ["to"] = id, ["data"] = opened.Me.ToJson() }, cancel);
                        break;
                    case "signal" when _pending.Remove(id, out var mine) && CoopTunnel.Candidates.FromJson(message["data"]) is { } peer:
                        var n = players.GetValueOrDefault(id);
                        _ = AcceptAsync(mine.Socket, mine.Me, peer, n, cancel);
                        break;
                    // A joiner leaves the rendezvous once connected directly; only a leave before that is news.
                    case "leave" when _pending.Remove(id, out var gone):
                        gone.Socket.Dispose();
                        Status?.Invoke($"Player {players.GetValueOrDefault(id)} gave up connecting.");
                        break;
                }
            }
            if (!cancel.IsCancellationRequested) Status?.Invoke("Lost the co-op rendezvous: new players can't join (players already in stay connected).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is CoopException or SocketException or WebSocketException)
        {
            Status?.Invoke("Co-op stopped: " + ex.Message);
        }
    }

    async Task AcceptAsync(System.Net.Sockets.Socket socket, CoopTunnel.Candidates me, CoopTunnel.Candidates peer, int n, CancellationToken cancel)
    {
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        try
        {
            await CoopTunnel.PunchAsync(socket, peer.ReachFrom(me), cancel);
            var listener = await QuicListener.ListenAsync(new QuicListenerOptions
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
                    },
                })),
            }, cancel);
            lock (_open) _open.Add(listener);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var connection = await listener.AcceptConnectionAsync(timeout.Token);
            lock (_open) _open.Add(connection);
            Status?.Invoke($"Player {n} connected directly.");
            while (!cancel.IsCancellationRequested)
            {
                var stream = await connection.AcceptInboundStreamAsync(cancel);
                _ = ForwardToGameAsync(stream);
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            Status?.Invoke($"Player {n} couldn't connect directly: one of your networks blocks it. They can use the game's own co-op instead.");
        }
        catch (QuicException ex)
        {
            if (!cancel.IsCancellationRequested) Status?.Invoke($"Player {n} disconnected ({ex.QuicError}).");
        }
        catch (OperationCanceledException) { }
    }

    async Task ForwardToGameAsync(QuicStream stream)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, _gamePort);
        }
        catch (SocketException)
        {
            Status?.Invoke("A player reached you, but the game isn't hosting yet. They'll retry when it is.");
            tcp.Dispose();
            await stream.DisposeAsync();
            return;
        }
        await CoopTunnel.PumpAsync(stream, tcp.GetStream());
        tcp.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_rendezvous is not null) await _rendezvous.DisposeAsync();
        foreach (var (socket, _) in _pending.Values) socket.Dispose();
        List<IAsyncDisposable> open;
        lock (_open) open = [.. _open];
        foreach (var item in open)
        {
            try { await item.DisposeAsync(); } catch (QuicException) { }
        }
        _cert.Dispose();
    }
}

/// <summary>
/// The joiner's side: finds the host through the code, punches a path, connects with QUIC (checking the host's
/// certificate against the one it announced), and listens on 127.0.0.1 for the game to connect to.
/// </summary>
public sealed class CoopJoin : IAsyncDisposable
{
    readonly CancellationTokenSource _stop = new();
    QuicConnection? _connection;
    TcpListener? _local;

    /// <summary>The 127.0.0.1 port to give the game as WOW_ROGUE_COOP_JOIN.</summary>
    public int LocalPort { get; private set; }

    public event Action<string>? Status;

    public static async Task<CoopJoin> ConnectAsync(string code, Action<string> progress, CancellationToken cancel)
    {
        var join = new CoopJoin();
        try
        {
            await join.ConnectCoreAsync(code, progress, cancel);
            return join;
        }
        catch
        {
            await join.DisposeAsync();
            throw;
        }
    }

    async Task ConnectCoreAsync(string code, Action<string> progress, CancellationToken cancel)
    {
        progress("Finding the lobby…");
        await using var rendezvous = await CoopTunnel.Rendezvous.ConnectAsync(code, "join", cancel);
        var first = await rendezvous.ReceiveAsync(cancel);
        if ((string?)first?["t"] != "joined")
            throw new CoopException((string?)first?["why"] switch
            {
                "no lobby" => "no lobby with that code (the host may have closed it)",
                "full" => "the party is full",
                var why => "the co-op rendezvous refused: " + (why ?? "no answer"),
            });

        var (socket, me) = await CoopTunnel.OpenAsync(null, cancel);
        var localPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        await rendezvous.SendAsync(new JsonObject { ["t"] = "signal", ["data"] = me.ToJson() }, cancel);

        progress("Waiting for the host…");
        CoopTunnel.Candidates? host = null;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            while (host is null)
            {
                var message = await rendezvous.ReceiveAsync(wait.Token) ?? throw new CoopException("the host left");
                if ((string?)message["t"] == "closed") throw new CoopException((string?)message["why"] ?? "the host left");
                if ((string?)message["t"] == "signal") host = CoopTunnel.Candidates.FromJson(message["data"]);
            }
        }

        progress("Connecting directly to the host…");
        var target = host.ReachFrom(me);
        await CoopTunnel.PunchAsync(socket, target, cancel);
        var expected = host.Thumbprint;
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
                    MaxInboundBidirectionalStreams = 0,
                    ClientAuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        ApplicationProtocols = [CoopTunnel.Alpn],
                        TargetHost = "drangedaddons-coop",
                        // Self-signed: trust it only if it's the one the host announced through the rendezvous.
                        RemoteCertificateValidationCallback = (_, cert, _, _) =>
                            cert is not null && expected is not null &&
                            string.Equals(Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)), expected, StringComparison.OrdinalIgnoreCase),
                    },
                }), attempt.Token);
                break;
            }
            catch (Exception ex) when (ex is QuicException or OperationCanceledException && !cancel.IsCancellationRequested)
            {
                if (DateTime.UtcNow > deadline)
                    throw new CoopException("couldn't connect directly to the host: one of your networks blocks it. Use the game's own co-op instead");
            }
        }

        _local = new TcpListener(IPAddress.Loopback, 0);
        _local.Start();
        LocalPort = ((IPEndPoint)_local.LocalEndpoint).Port;
        _ = ServeGameAsync();
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
        catch (QuicException ex)
        {
            Status?.Invoke($"Lost the direct connection to the host ({ex.QuicError}).");
        }
        catch (SocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _local?.Stop();
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); } catch (QuicException) { }
        }
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
                        if (got.ReceivedBytes >= 20 && buffer.AsSpan(8, 12).SequenceEqual(request.AsSpan(8, 12))
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
