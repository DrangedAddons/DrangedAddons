using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;

namespace DrangedAddons;

/// <summary>
/// Starts Azeroth Survivors hosting or joining co-op over a direct TCP connection instead of the game's relay.
/// The relay sits overseas, so two players in the same country can see 500+ ms; directly they're tens of ms apart.
/// </summary>
public partial class CoopWindow : Window
{
    /// <summary>Our pick: the game's switch has no default we know of.</summary>
    public const int DefaultPort = 27420;

    readonly AppSettings _settings;
    readonly string _exe;

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
        ShowAddresses();
        UpdateMode();
    }

    bool Hosting => HostTab.IsChecked == true;

    void Mode_Checked(object sender, RoutedEventArgs e) => UpdateMode();

    void UpdateMode()
    {
        if (HostPanel is null || JoinPanel is null || StartButton is null) return; // still in InitializeComponent
        HostPanel.Visibility = Hosting ? Visibility.Visible : Visibility.Collapsed;
        JoinPanel.Visibility = Hosting ? Visibility.Collapsed : Visibility.Visible;
        StartButton.Content = Hosting ? "Host and start" : "Join and start";
        ShowAddresses();
        MessageText.Visibility = Visibility.Collapsed;
    }

    void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (MessageText is not null) MessageText.Visibility = Visibility.Collapsed;
        if (sender == PortBox) ShowAddresses();
    }

    /// <summary>This PC's IPv4 addresses with the port, Tailscale's (100.64.0.0/10) first.</summary>
    void ShowAddresses()
    {
        if (AddressList is null) return;
        AddressList.Children.Clear();
        var port = int.TryParse(PortBox.Text.Trim(), out var p) ? p : _settings.CoopPort;
        var found = LocalAddresses().OrderBy(a => a.Kind == "Tailscale" ? 0 : 1).ToList();
        if (found.Count == 0)
        {
            AddressList.Children.Add(new TextBlock { Text = "No network addresses found.", Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush") });
            return;
        }
        foreach (var (address, kind) in found)
        {
            var text = $"{address}:{port}";
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
                    new System.Windows.Documents.Run("  " + kind) { Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush") },
                },
            });
            AddressList.Children.Add(row);
        }
    }

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
                var kind = b[0] == 100 && b[1] is >= 64 and <= 127 ? "Tailscale"
                    : b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ? "same home network only"
                    : nic.Name;
                yield return (ip, kind);
            }
        }
    }

    void Start_Click(object sender, RoutedEventArgs e)
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
        }
        else
        {
            join = AddressBox.Text.Trim();
            var colon = join.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(join[(colon + 1)..], out var jp) || jp is < 1 or > 65535)
            {
                ShowMessage("Enter the host's address with the port, like 100.101.102.103:" + DefaultPort + ".");
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
        Started = port is { } h ? $"Starting Azeroth Survivors hosting direct co-op on port {h}…"
            : $"Starting Azeroth Survivors joining {join} directly…";
        Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.Visibility = Visibility.Visible;
    }
}
