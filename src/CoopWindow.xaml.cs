using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DrangedAddons;

/// <summary>
/// Starts Azeroth Survivors hosting or joining co-op over a direct TCP connection instead of the game's relay.
/// The relay sits overseas, so two players in the same country can see 500+ ms; directly they're tens of ms apart.
/// Hosting asks the router to forward the port (UPnP), so nobody has to install anything or change router settings.
/// </summary>
public partial class CoopWindow : Window
{
    /// <summary>Our pick: the game's switch has no default we know of.</summary>
    public const int DefaultPort = 27420;

    /// <summary>How long the router keeps the port forwarded; long enough for an evening of runs.</summary>
    const int LeaseHours = 8;

    readonly AppSettings _settings;
    readonly string _exe;

    Task<Upnp?>? _router;
    IPAddress? _external;
    /// <summary>Set after the router couldn't forward the port: Start then starts the game anyway.</summary>
    bool _startAnyway;

    /// <summary>A status line once the game was started (the caller then saves the settings); null if it wasn't.</summary>
    public string? Started { get; private set; }

    public CoopWindow(AppSettings settings, string exe)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _settings = settings;
        _exe = exe;
        PortBox.Text = settings.CoopPort.ToString();
        AddressBox.Text = settings.CoopJoinAddress ?? "";
        if (!string.IsNullOrEmpty(settings.CoopJoinAddress)) JoinTab.IsChecked = true;
        UpdateMode();
    }

    bool Hosting => HostTab.IsChecked == true;

    int Port => int.TryParse(PortBox.Text.Trim(), out var p) ? p : _settings.CoopPort;

    void Mode_Checked(object sender, RoutedEventArgs e) => UpdateMode();

    void UpdateMode()
    {
        if (HostPanel is null || JoinPanel is null || StartButton is null) return; // still in InitializeComponent
        HostPanel.Visibility = Hosting ? Visibility.Visible : Visibility.Collapsed;
        JoinPanel.Visibility = Hosting ? Visibility.Collapsed : Visibility.Visible;
        _startAnyway = false;
        StartButton.Content = Hosting ? "Host and start" : "Join and start";
        MessageText.Visibility = Visibility.Collapsed;
        if (Hosting && _router is null) _ = CheckRouterAsync();
    }

    void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (MessageText is not null) MessageText.Visibility = Visibility.Collapsed;
        if (sender == PortBox && AddressList is not null) ShowAddresses();
    }

    /// <summary>Finds the router and its internet address, and says whether the others can reach this PC through it.</summary>
    async Task CheckRouterAsync()
    {
        _router = FindRouterAsync();
        var router = await _router;
        _external = router is null ? null : await router.GetExternalAddressAsync();
        RouterText.Text = router is null
            ? "Your router didn't answer (UPnP may be off). You'll need to forward the port in its settings, or let someone else host."
            : _external is null
                ? "Your router answered but wouldn't give its internet address. Starting will still try to open the port."
                : Upnp.IsShared(_external)
                    ? $"Your internet provider shares one address between customers (yours is {_external}), so nobody outside can connect in. Let someone else host, or ask your provider for a public IP address."
                    : "Your router can forward the port. Send the others the address below once you've started.";
        RouterText.Foreground = (Brush)FindResource(router is not null && _external is { } ip && !Upnp.IsShared(ip) ? "MutedBrush" : "DangerBrush");
        ShowAddresses();
    }

    static async Task<Upnp?> FindRouterAsync()
    {
        try { return await Upnp.FindAsync(); }
        catch (SocketException) { return null; }
    }

    /// <summary>The internet address with the port first (what the others need), then this PC's own addresses.</summary>
    void ShowAddresses()
    {
        AddressList.Children.Clear();
        if (_external is { } external && !Upnp.IsShared(external)) AddAddress(external, "internet");
        foreach (var (address, kind) in LocalAddresses()) AddAddress(address, kind);
    }

    void AddAddress(IPAddress address, string kind)
    {
        var text = $"{address}:{Port}";
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var copy = new Button { Content = "Copy", Style = (Style)FindResource("GhostBtn"), Padding = new Thickness(10, 2, 10, 2) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(text); copy.Content = "Copied"; }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
        };
        DockPanel.SetDock(copy, Dock.Right);
        row.Children.Add(copy);
        row.Children.Add(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Inlines =
            {
                new System.Windows.Documents.Run(text) { FontWeight = FontWeights.SemiBold },
                new System.Windows.Documents.Run("  " + kind) { Foreground = (Brush)FindResource("MutedBrush") },
            },
        });
        AddressList.Children.Add(row);
    }

    /// <summary>This PC's IPv4 addresses: for players on the same home network, or a VPN someone already uses.</summary>
    static IEnumerable<(IPAddress Address, string Kind)> LocalAddresses()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }
        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = unicast.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ip.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue; // link-local: no DHCP answer, unreachable
                yield return (ip, Upnp.IsShared(ip) ? "same home network only" : nic.Name);
            }
        }
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        int? port = null;
        string? join = null;
        if (Hosting)
        {
            if (!int.TryParse(PortBox.Text.Trim(), out var p) || p is < 1024 or > 65535)
            {
                ShowMessage("The port needs to be a number from 1024 to 65535.");
                return;
            }
            port = p;
            if (!_startAnyway && await OpenPortAsync(p) is { } problem)
            {
                ShowMessage($"Couldn't open port {p} on your router: {problem}. Players outside your home network won't be able to join unless you forward TCP {p} to this PC in the router's settings.");
                _startAnyway = true;
                StartButton.Content = "Start anyway";
                return;
            }
        }
        else
        {
            join = AddressBox.Text.Trim();
            var colon = join.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(join[(colon + 1)..], out var jp) || jp is < 1 or > 65535)
            {
                ShowMessage("Enter the host's address with the port, like 203.0.113.7:" + DefaultPort + ".");
                return;
            }
        }

        var info = new ProcessStartInfo(_exe)
        {
            WorkingDirectory = Path.GetDirectoryName(_exe)!,
            UseShellExecute = false, // needed to hand the game its own environment variables
        };
        foreach (var (name, value) in AzerothSurvivorsGame.DirectCoopEnvironment(port, join, PasswordBox.Password))
            info.Environment[name] = value;
        try
        {
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowMessage("Couldn't start the game: " + ex.Message);
            return;
        }

        if (port is { } hosted) _settings.CoopPort = hosted;
        if (join is not null) _settings.CoopJoinAddress = join;
        Started = port is { } h
            ? _external is { } ip && !Upnp.IsShared(ip)
                ? $"Hosting direct co-op: send the others {ip}:{h}"
                : $"Starting Azeroth Survivors hosting direct co-op on port {h}…"
            : $"Starting Azeroth Survivors joining {join} directly…";
        Close();
    }

    /// <summary>Asks the router to forward the port here; null when it did, else why not.</summary>
    async Task<string?> OpenPortAsync(int port)
    {
        StartButton.IsEnabled = false;
        StartButton.Content = "Opening port…";
        try
        {
            if (_router is null) _ = CheckRouterAsync();
            var router = await _router!;
            if (router is null) return "the router didn't answer (UPnP may be off)";
            if (_external is { } ip && Upnp.IsShared(ip)) return "your internet provider shares your address with other customers";
            return await router.AddTcpMappingAsync(port, "Azeroth Survivors co-op", LeaseHours);
        }
        finally
        {
            StartButton.IsEnabled = true;
            StartButton.Content = "Host and start";
        }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.Visibility = Visibility.Visible;
    }
}
