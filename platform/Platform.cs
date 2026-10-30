using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DrangedPlatform;

// The platform's fixed description (data\*.json, built into the app) and its live checks.

public sealed record Profile(string game_id, string game, string connectivity, int? party_size, string host_setup,
    string join_setup, string lobby, string in_game_detection, string profile_state);

public sealed record Connectivity(string id, string name, string status, string how, string needs, string latency);

public sealed record Component(int order, string layer, string component, string runs_on, string detail, string status);

public sealed record SecurityControl(int order, string area, string control, string enforced_by);

public static class PlatformData
{
    public static readonly List<Profile> Profiles = Load<Profile>("profiles.json");
    public static readonly List<Connectivity> Connectivity = Load<Connectivity>("connectivity.json");
    public static readonly List<Component> Components = Load<Component>("components.json").OrderBy(c => c.order).ToList();
    public static readonly List<SecurityControl> Security = Load<SecurityControl>("security.json").OrderBy(s => s.order).ToList();

    static List<T> Load<T>(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"{name} isn't built into the app.");
        return JsonSerializer.Deserialize<List<T>>(stream) ?? [];
    }
}

/// <summary>Live totals from the Worker's /stats (counts only).</summary>
public sealed record Stats(DateTimeOffset At, int Connected, int Visible, int Online, int Away, int InGame, int Hidden,
    Dictionary<string, int> InGameByGame, Dictionary<string, (int Lobbies, int Players, int Locked, int Password)> Lobbies,
    int Members, int SeenDay, int SeenWeek, int Pictures, int Friendships, int PendingRequests);

public enum StatsProblem { None, NoToken, NotDeployed, Refused, Unreachable }

public sealed record Check(bool Ok, int? Milliseconds, string Detail);

public static class Live
{
    public const string WorkerHost = "drangedaddons-coop.coop-rendezvous.workers.dev";
    public static readonly string WorkerUrl = $"https://{WorkerHost}";
    public const string StunHost = "stun.cloudflare.com";
    const int StunPort = 3478;

    /// <summary>The admin token for /stats; kept beside DrangedAddons' own settings, never in the repo.</summary>
    public static readonly string TokenFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DrangedAddons", "platform-stats-token.txt");

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>The Worker's front door answers, and how long it took.</summary>
    public static async Task<Check> PingWorkerAsync()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await Http.GetAsync(WorkerUrl + "/");
            return response.IsSuccessStatusCode
                ? new Check(true, (int)watch.ElapsedMilliseconds, "Answering")
                : new Check(false, (int)watch.ElapsedMilliseconds, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new Check(false, null, "Can't reach it");
        }
    }

    public static async Task<(Stats? Stats, StatsProblem Problem)> GetStatsAsync()
    {
        string token;
        try
        {
            token = File.Exists(TokenFile) ? (await File.ReadAllTextAsync(TokenFile)).Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            token = "";
        }
        if (token.Length == 0) return (null, StatsProblem.NoToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, WorkerUrl + "/stats");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Http.SendAsync(request);
            // A Worker from before /stats answers anything it doesn't know with 404.
            if (response.StatusCode == HttpStatusCode.NotFound) return (null, StatsProblem.NotDeployed);
            if (response.StatusCode == HttpStatusCode.Unauthorized) return (null, StatsProblem.Refused);
            if (!response.IsSuccessStatusCode) return (null, StatsProblem.Unreachable);
            return (Parse(JsonNode.Parse(await response.Content.ReadAsStringAsync())!), StatsProblem.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return (null, StatsProblem.Unreachable);
        }
    }

    static Stats Parse(JsonNode j)
    {
        int N(JsonNode? n) => n?.GetValue<double>() is { } d ? (int)d : 0;
        var states = j["byState"];
        var inGame = j["inGame"]?.AsObject().ToDictionary(kv => kv.Key, kv => N(kv.Value)) ?? [];
        var lobbies = j["lobbies"]?.AsObject().ToDictionary(kv => kv.Key,
            kv => (N(kv.Value?["lobbies"]), N(kv.Value?["players"]), N(kv.Value?["locked"]), N(kv.Value?["password"]))) ?? [];
        var members = j["members"];
        return new Stats(DateTimeOffset.TryParse((string?)j["at"], out var at) ? at : DateTimeOffset.Now,
            N(j["connected"]), N(j["visible"]), N(states?["online"]), N(states?["away"]), N(states?["ingame"]), N(states?["offline"]),
            inGame, lobbies, N(members?["known"]), N(members?["seenDay"]), N(members?["seenWeek"]), N(members?["pictures"]),
            N(j["friendships"]), N(j["pendingRequests"]));
    }

    /// <summary>
    /// A STUN binding request to Cloudflare, as the tunnel makes before punching: whether it answers and how fast.
    /// The address it reports back is not shown.
    /// </summary>
    public static async Task<Check> PingStunAsync()
    {
        try
        {
            var address = (await Dns.GetHostAddressesAsync(StunHost, AddressFamily.InterNetwork)).FirstOrDefault();
            if (address is null) return new Check(false, null, "No address for " + StunHost);
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var transaction = RandomNumberGenerator.GetBytes(12);
            var request = new byte[20];
            request[1] = 0x01; // binding request, no attributes
            request[4] = 0x21; request[5] = 0x12; request[6] = 0xA4; request[7] = 0x42; // magic cookie
            transaction.CopyTo(request, 8);
            var watch = Stopwatch.StartNew();
            await udp.SendAsync(request, new IPEndPoint(address, StunPort));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (true)
            {
                var reply = await udp.ReceiveAsync(timeout.Token);
                if (reply.Buffer.Length >= 20 && reply.Buffer[0] == 0x01 && reply.Buffer[1] == 0x01
                    && reply.Buffer.AsSpan(8, 12).SequenceEqual(transaction))
                    return new Check(true, (int)watch.ElapsedMilliseconds, "Answering");
            }
        }
        catch (OperationCanceledException)
        {
            return new Check(false, null, "No answer in 3 s (UDP blocked?)");
        }
        catch (SocketException ex)
        {
            return new Check(false, null, ex.Message);
        }
    }
}
