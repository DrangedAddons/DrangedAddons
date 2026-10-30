using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DrangedAddons;

/// <summary>
/// Hosts or joins Azeroth Survivors co-op over a direct, hole-punched connection (<see cref="CoopTunnel"/>) instead
/// of the game's relay, which sits overseas: two players in the same country saw 500+ ms through it. The host invites
/// friends who are online (<see cref="CoopPresence"/>) or shares a lobby code; an invited friend joins with one click.
/// The window stays open while the game runs, because the connection lives in this app; closing it, or the game,
/// ends co-op.
/// </summary>
public partial class CoopWindow : Window
{
    readonly string _exe;
    readonly Action<bool> _setOnline;
    CoopPresence? _presence;
    CoopHost? _host;
    CoopJoin? _join;
    Process? _game;
    bool _starting;
    readonly HashSet<string> _invited = new();

    /// <summary>Raised when the player joins someone's invite, so it stops being offered.</summary>
    public event Action<CoopPlayer>? Joined;

    public CoopWindow(string exe, bool online, Action<bool> setOnline)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _exe = exe;
        _setOnline = setOnline;
        OnlineCheck.IsChecked = online;
        UpdateMode();
        if (!CoopTunnel.IsSupported)
        {
            AddStatus("Direct co-op needs Windows 11 or Windows Server 2022 (for QUIC); this PC doesn't have it.", danger: true);
            StartButton.IsEnabled = false;
        }
    }

    bool Hosting => HostTab.IsChecked == true;
    bool Active => _host is not null || _join is not null;

    // ---- Friends online and invites ----

    /// <summary>Follows <paramref name="presence"/> (null when offline) for the friends list and declines.</summary>
    public void UsePresence(CoopPresence? presence)
    {
        if (_presence is not null)
        {
            _presence.Changed -= PresenceChanged;
            _presence.Declined -= PresenceDeclined;
        }
        _presence = presence;
        if (presence is not null)
        {
            presence.Changed += PresenceChanged;
            presence.Declined += PresenceDeclined;
        }
        OnlineCheckText.Text = presence is null
            ? "Show me as online so friends can invite me"
            : $"Show me as online so friends can invite me (as {presence.Name})";
        ShowOnline();
    }

    void PresenceChanged() => Dispatcher.BeginInvoke(ShowOnline);

    void PresenceDeclined(CoopPlayer from) => Dispatcher.BeginInvoke(() =>
    {
        _invited.Remove(from.Id);
        AddStatus($"{from.Name} declined.");
        ShowOnline();
    });

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowOnline();
    }

    void ShowOnline()
    {
        if (OnlineList is null) return;
        OnlineList.Children.Clear();
        var muted = (Brush)FindResource("MutedBrush");
        if (_presence is null)
        {
            OnlineList.Children.Add(Line("Turn on \"Show me as online\" below to see friends who are online.", muted));
            return;
        }
        if (!_presence.Connected)
        {
            OnlineList.Children.Add(Line("Connecting to the co-op service…", muted));
            return;
        }
        var all = _presence.Players;
        var query = SearchBox.Text.Trim();
        var shown = all.Where(p => query.Length == 0 || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        OnlineHeader.Text = all.Count == 0 ? "Friends online" : $"Friends online ({all.Count})";
        if (shown.Count == 0)
        {
            OnlineList.Children.Add(Line(all.Count == 0 ? "Nobody else is online with DrangedAddons right now." : $"Nobody online matches “{query}”.", muted));
            return;
        }
        foreach (var player in shown)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var invited = _invited.Contains(player.Id);
            var invite = new Button
            {
                Content = invited ? "Invited" : "Invite",
                Style = (Style)FindResource(invited ? "GhostBtn" : "Btn"),
                Padding = new Thickness(12, 3, 12, 3),
                IsEnabled = !invited && !_starting && _join is null && CoopTunnel.IsSupported,
            };
            invite.Click += async (_, _) => await InviteAsync(player);
            DockPanel.SetDock(invite, Dock.Right);
            row.Children.Add(invite);
            row.Children.Add(new TextBlock { Text = player.Name, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            OnlineList.Children.Add(row);
        }
    }

    static TextBlock Line(string text, Brush brush) => new() { Text = text, Foreground = brush, TextWrapping = TextWrapping.Wrap };

    /// <summary>Hosts first if not hosting yet (that starts the game), then sends the invite with the lobby's code.</summary>
    async Task InviteAsync(CoopPlayer player)
    {
        if (_presence is null) return;
        if (_host is null)
        {
            HostTab.IsChecked = true;
            if (!await StartAsync(hosting: true, code: null)) return;
        }
        try
        {
            await _presence.InviteAsync(player, _host!.Code);
            _invited.Add(player.Id);
            AddStatus($"Invited {player.Name}.");
        }
        catch (Exception ex) when (ex is CoopException or System.Net.WebSockets.WebSocketException)
        {
            AddStatus($"Couldn't invite {player.Name}: {ex.Message}. Send them the code instead.", danger: true);
        }
        ShowOnline();
    }

    /// <summary>Adds (or refreshes) an invite row: Join connects and starts the game, Decline tells the host.</summary>
    public void ShowInvite(CoopPlayer from, string code)
    {
        foreach (var old in InviteList.Children.OfType<FrameworkElement>().Where(e => e.Tag is string id && id == from.Id).ToList())
            InviteList.Children.Remove(old);
        var card = new Border
        {
            Tag = from.Id,
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x24, 0x10)),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(1),
        };
        var row = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var decline = new Button { Content = "Decline", Style = (Style)FindResource("GhostBtn"), Margin = new Thickness(0, 0, 8, 0) };
        var join = new Button { Content = "Join", Style = (Style)FindResource("AccentBtn"), MinWidth = 80, IsEnabled = CoopTunnel.IsSupported };
        decline.Click += async (_, _) =>
        {
            InviteList.Children.Remove(card);
            Joined?.Invoke(from);
            try { if (_presence is not null) await _presence.DeclineAsync(from); }
            catch (Exception ex) when (ex is CoopException or System.Net.WebSockets.WebSocketException) { }
        };
        join.Click += async (_, _) =>
        {
            if (Active || _starting) return;
            InviteList.Children.Remove(card);
            Joined?.Invoke(from);
            JoinTab.IsChecked = true;
            CodeBox.Text = code;
            await StartAsync(hosting: false, code);
        };
        buttons.Children.Add(decline);
        buttons.Children.Add(join);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(new TextBlock
        {
            Text = $"{from.Name} invites you to co-op",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        card.Child = row;
        InviteList.Children.Add(card);
    }

    void OnlineCheck_Click(object sender, RoutedEventArgs e) => _setOnline(OnlineCheck.IsChecked == true);

    // ---- Hosting and joining ----

    void Mode_Checked(object sender, RoutedEventArgs e) => UpdateMode();

    void UpdateMode()
    {
        if (HostPanel is null || JoinPanel is null || StartButton is null) return; // still in InitializeComponent
        HostPanel.Visibility = Hosting ? Visibility.Visible : Visibility.Collapsed;
        JoinPanel.Visibility = Hosting ? Visibility.Collapsed : Visibility.Visible;
        StartButton.Content = Hosting ? "Host and start" : "Join and start";
        if (!Active) StatusList.Children.Clear();
        if (!Hosting) CodeBox.Focus();
    }

    void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (StatusList is not null && !Active && !_starting) StatusList.Children.Clear();
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_starting || Active) return;
        string? code = null;
        if (!Hosting && (code = CoopTunnel.NormalizeCode(CodeBox.Text)) is null)
        {
            StatusList.Children.Clear();
            AddStatus("A lobby code is 6 letters and numbers, like K7MPQ2.", danger: true);
            return;
        }
        await StartAsync(Hosting, code);
    }

    /// <summary>Opens the lobby or connects to the host, then starts the game pointed at it; false if that failed.</summary>
    async Task<bool> StartAsync(bool hosting, string? code)
    {
        if (_starting || Active) return Active && hosting == (_host is not null);
        _starting = true;
        StartButton.IsEnabled = false;
        ModeTabs.IsEnabled = PasswordPanel.IsEnabled = CodeBox.IsEnabled = false;
        StatusList.Children.Clear();
        ShowOnline();
        AddStatus("If Windows asks whether DrangedAddons may use the network, click Allow: the connection to the other players goes through it.");
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Dictionary<string, string> env;
            if (hosting)
            {
                AddStatus("Opening a lobby…");
                var gamePort = CoopTunnel.FreeLocalPort();
                _host = await CoopHost.StartAsync(gamePort, cancel.Token);
                _host.Status += line => Dispatcher.BeginInvoke(() => AddStatus(line));
                CodeText.Text = _host.Code;
                CodePanel.Visibility = Visibility.Visible;
                HostHint.Text = "Invite friends above, or send the code to anyone not in the list.";
                env = AzerothSurvivorsGame.DirectCoopEnvironment(gamePort, null, PasswordBox.Password);
            }
            else
            {
                _join = await CoopJoin.ConnectAsync(code!, line => Dispatcher.BeginInvoke(() => AddStatus(line)), cancel.Token);
                _join.Status += line => Dispatcher.BeginInvoke(() => AddStatus(line, danger: true));
                env = AzerothSurvivorsGame.DirectCoopEnvironment(null, $"127.0.0.1:{_join.LocalPort}", PasswordBox.Password);
                AddStatus("Connected to the host directly.");
            }

            StartGame(env);
            AddStatus(hosting ? "Started the game. Enter the zone once everyone has joined." : "Started the game: it joins the host's party.");
            AddStatus("Keep this window open while you play: the connection runs through it.");
            CloseButton.Content = "End co-op";
            StartButton.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception ex) when (ex is CoopException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException
                                       or OperationCanceledException or System.ComponentModel.Win32Exception or System.Net.Quic.QuicException)
        {
            AddStatus(ex switch
            {
                OperationCanceledException => "Timed out.",
                System.Net.WebSockets.WebSocketException => "Couldn't reach the co-op service. Check your internet connection.",
                System.ComponentModel.Win32Exception => "Couldn't start the game: " + ex.Message,
                _ => char.ToUpper(ex.Message[0]) + ex.Message[1..] + ".",
            }, danger: true);
            await EndAsync();
            StartButton.IsEnabled = true;
            ModeTabs.IsEnabled = PasswordPanel.IsEnabled = CodeBox.IsEnabled = true;
            return false;
        }
        finally
        {
            _starting = false;
            ShowOnline();
        }
    }

    void StartGame(Dictionary<string, string> env)
    {
        var info = new ProcessStartInfo(_exe)
        {
            WorkingDirectory = Path.GetDirectoryName(_exe)!,
            UseShellExecute = false, // needed to hand the game its own environment variables
        };
        foreach (var (name, value) in env) info.Environment[name] = value;
        _game = Process.Start(info) ?? throw new System.ComponentModel.Win32Exception("Windows didn't start it");
        _game.EnableRaisingEvents = true;
        _game.Exited += (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            if (!Active) return;
            AddStatus("The game closed, so co-op ended.");
            await EndAsync();
            CloseButton.Content = "Close";
        });
    }

    async Task EndAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        if (_join is not null) await _join.DisposeAsync();
        _host = null;
        _join = null;
        _invited.Clear();
        _game?.Dispose();
        _game = null;
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CodeText.Text);
            CopyButton.Content = "Copied";
        }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (Active && _game is { HasExited: false } &&
            !DarkDialog.Show(this, "End co-op?",
                _host is not null ? "The others will lose their connection to your game." : "Your game will lose its connection to the host.",
                "End co-op", "Keep playing"))
        {
            e.Cancel = true;
            return;
        }
        UsePresence(null);
        await EndAsync();
    }

    void AddStatus(string text, bool danger = false) =>
        StatusList.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = (Brush)FindResource(danger ? "DangerBrush" : "MutedBrush"),
        });
}
