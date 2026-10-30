using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace DrangedAddons;

/// <summary>
/// Asks the home router (UPnP IGD) to forward a TCP port to this PC, and for the router's internet address, so a
/// player can host direct co-op without setting up port forwarding by hand. Most home routers answer; some have UPnP
/// turned off, and behind an ISP's shared address (CGNAT) a forwarded port still can't be reached from outside.
/// </summary>
public sealed class Upnp
{
    static readonly string[] ServiceTypes =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    readonly Uri _control;
    readonly string _service;

    /// <summary>This PC's address on the router's network (where the router forwards to).</summary>
    public IPAddress LocalAddress { get; }

    Upnp(Uri control, string service, IPAddress local)
    {
        _control = control;
        _service = service;
        LocalAddress = local;
    }

    /// <summary>Finds the router's port-forwarding service; null if no router answers within a couple of seconds.</summary>
    public static async Task<Upnp?> FindAsync(CancellationToken cancel = default)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var search = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
        var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        await udp.SendAsync(search, target, cancel);
        await udp.SendAsync(search, target, cancel); // UDP: a second copy in case the first is dropped

        var tried = new HashSet<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        while (true)
        {
            UdpReceiveResult reply;
            try { reply = await udp.ReceiveAsync(timeout.Token); }
            catch (OperationCanceledException) { return null; }

            var location = Encoding.ASCII.GetString(reply.Buffer).Split("\r\n")
                .FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))?[9..].Trim();
            if (location is null || !tried.Add(location) || !Uri.TryCreate(location, UriKind.Absolute, out var description)) continue;
            if (await FromDescriptionAsync(description, cancel) is { } found) return found;
        }
    }

    static async Task<Upnp?> FromDescriptionAsync(Uri description, CancellationToken cancel)
    {
        try
        {
            var xml = XDocument.Parse(await Http.GetStringAsync(description, cancel));
            var services = xml.Descendants().Where(e => e.Name.LocalName == "service").ToList();
            foreach (var type in ServiceTypes)
            {
                var service = services.FirstOrDefault(s => s.Elements().Any(e => e.Name.LocalName == "serviceType" && e.Value.Trim() == type));
                var path = service?.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value.Trim();
                if (path is null) continue;
                // The address this PC uses to reach the router is the one the router should forward to.
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                probe.Connect(description.Host, description.Port);
                return new Upnp(new Uri(description, path), type, ((IPEndPoint)probe.LocalEndPoint!).Address);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException or SocketException)
        {
            // Not a router we can use; try the next one that answered.
        }
        return null;
    }

    /// <summary>The router's internet address; null if it won't say.</summary>
    public async Task<IPAddress?> GetExternalAddressAsync(CancellationToken cancel = default)
    {
        var reply = await SoapAsync("GetExternalIPAddress", "", cancel);
        return IPAddress.TryParse(reply?.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value.Trim(), out var ip)
            ? ip : null;
    }

    /// <summary>Forwards TCP <paramref name="port"/> to this PC for <paramref name="hours"/>; null when done, else why not.</summary>
    public async Task<string?> AddTcpMappingAsync(int port, string description, int hours, CancellationToken cancel = default)
    {
        string Args(int lease) =>
            $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{port}</NewExternalPort><NewProtocol>TCP</NewProtocol>" +
            $"<NewInternalPort>{port}</NewInternalPort><NewInternalClient>{LocalAddress}</NewInternalClient>" +
            $"<NewEnabled>1</NewEnabled><NewPortMappingDescription>{System.Security.SecurityElement.Escape(description)}</NewPortMappingDescription>" +
            $"<NewLeaseDuration>{lease}</NewLeaseDuration>";

        var error = await SoapErrorAsync("AddPortMapping", Args(hours * 3600), cancel);
        if (error == "725") error = await SoapErrorAsync("AddPortMapping", Args(0), cancel); // only permanent leases supported
        return error switch
        {
            null => null,
            "718" => $"port {port} is already forwarded to another device on your network",
            "606" or "401" => "the router refused (UPnP may be read-only on it)",
            _ => $"the router refused (UPnP error {error})",
        };
    }

    async Task<string?> SoapErrorAsync(string action, string args, CancellationToken cancel)
    {
        try
        {
            var reply = await SendAsync(action, args, cancel);
            if (reply.IsSuccessStatusCode) return null;
            var body = await reply.Content.ReadAsStringAsync(cancel);
            try
            {
                return XDocument.Parse(body).Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value.Trim()
                       ?? ((int)reply.StatusCode).ToString();
            }
            catch (System.Xml.XmlException) { return ((int)reply.StatusCode).ToString(); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "no answer";
        }
    }

    async Task<XDocument?> SoapAsync(string action, string args, CancellationToken cancel)
    {
        try
        {
            var reply = await SendAsync(action, args, cancel);
            return reply.IsSuccessStatusCode ? XDocument.Parse(await reply.Content.ReadAsStringAsync(cancel)) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            return null;
        }
    }

    Task<HttpResponseMessage> SendAsync(string action, string args, CancellationToken cancel)
    {
        var envelope =
            "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
            $"<u:{action} xmlns:u=\"{_service}\">{args}</u:{action}></s:Body></s:Envelope>";
        var request = new HttpRequestMessage(HttpMethod.Post, _control)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{_service}#{action}\"");
        return Http.SendAsync(request, cancel);
    }

    /// <summary>
    /// An address the internet can't reach in: private, or the ISP's shared range (CGNAT, 100.64.0.0/10). A router
    /// with one of these as its "internet" address is behind another NAT, so forwarding a port on it doesn't help.
    /// </summary>
    public static bool IsShared(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                                 || (b[0] == 100 && b[1] is >= 64 and <= 127));
    }
}
