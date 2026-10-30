using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DrangedAddons;

/// <summary>
/// Direct co-op for Azeroth Survivors over a hole-punched connection (<see cref="CoopTunnel"/>) instead of the game's
/// relay, which sits overseas: two players in the same country saw 500+ ms through it.
///
/// The host opens a party by inviting friends who are online (<see cref="CoopPresence"/>) or creating one and sharing
/// its code. The party lobby shows who's in, who's invited and who's still connecting; the host can remove players,
/// and Start game starts everyone's game together. Players who join after that start straight away. The window stays
/// open while playing, because the connection lives in this app; closing it, or the game, ends co-op.
/// </summary>
public partial class CoopWindow : Window
{
    readonly string _exe;
    readonly string _name;
    readonly Action<bool> _setOnline;
    CoopPresence? _presence;
    CoopHost? _host;
    CoopJoin? _join;
    Process? _game;
    bool _busy;
    /// <summary>Friends invited to this party and not in it yet (or declined), by presence id.</summary>
    readonly Dictionary<string, CoopPlayer> _invited = new();

    /// <summary>Raised when the player answers someone's invite, so it stops being offered.</summary>
    public event Action<CoopPlayer>? Joined;

    public CoopWindow(string exe, string name, bool online, Action<bool> setOnline)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _exe = exe;
        _name = name;
        _setOnline = setOnline;
        OnlineCheck.IsChecked = online;
        UpdateView();
        if (!CoopTunnel.IsSupported)
        {
            AddStatus("Direct co-op needs Windows 11 or Windows Server 2022 (for QUIC); this PC doesn't have it.", danger: true);
            StartButton.IsEnabled = false;
        }
    }

    bool InParty => _host is not null || _join is not null;
    Brush Muted => (Brush)FindResource("MutedBrush");

    // ---- Layout per state: not in a party (Host / Join tabs), hosting a party, in someone's party ----

    void Mode_Checked(object sender, RoutedEventArgs e) => UpdateView();

    void UpdateView()
    {
        if (PartyPanel is null || FriendsPanel is null || StartButton is null) return; // still in InitializeComponent
        var hostTab = HostTab.IsChecked == true;
        ModeTabs.Visibility = InParty ? Visibility.Collapsed : Visibility.Visible;
        PartyPanel.Visibility = InParty ? Visibility.Visible : Visibility.Collapsed;
        FriendsPanel.Visibility = _join is null && (InParty || hostTab) ? Visibility.Visible : Visibility.Collapsed;
        JoinPanel.Visibility = !InParty && !hostTab ? Visibility.Visible : Visibility.Collapsed;
        CodePanel.Visibility = _host is not null ? Visibility.Visible : Visibility.Collapsed;
        IntroText.Visibility = InParty ? Visibility.Collapsed : Visibility.Visible;
        HostHint.Visibility = _host is null ? Visibility.Visible : Visibility.Collapsed;

        if (_host is not null)
        {
            StartButton.Visibility = Visibility.Visible;
            StartButton.Content = _host.Started ? "Game started" : "Start game";
            StartButton.IsEnabled = !_host.Started && !_busy;
            CloseButton.Content = "Disband party";
        }
        else if (_join is not null)
        {
            StartButton.Visibility = Visibility.Collapsed;
            CloseButton.Content = "Leave party";
        }
        else
        {
            StartButton.Visibility = Visibility.Visible;
            StartButton.Content = hostTab ? "Create party" : "Join party";
            StartButton.IsEnabled = !_busy && CoopTunnel.IsSupported;
            CloseButton.Content = "Close";
        }
        ShowParty();
        ShowOnline();
    }

    /// <summary>The party lobby: host first, then players (with Remove for the host), then friends invited but not in.</summary>
    void ShowParty()
    {
        if (MemberList is null) return;
        MemberList.Children.Clear();
        PartyInfo? party = _host?.Party ?? _join?.Party;
        if (party is null)
        {
            if (_join is not null) MemberList.Children.Add(Line("Waiting for the party list from the host…", Muted));
            return;
        }
        PartyHeader.Text = _host is not null ? $"Your party ({party.Members.Count + 1}/5)" : $"{party.Host}'s party ({party.Members.Count + 1}/5)";
        MemberList.Children.Add(MemberRow(party.Host + (_host is not null ? " (you)" : ""), "Host", null));
        foreach (var member in party.Members)
        {
            var you = _join is not null && member.Id == _join.Id;
            var state = member.State switch
            {
                MemberState.Connected => party.Started ? "In the game" : "Ready",
                MemberState.Failed => "Couldn't connect",
                _ => "Connecting…",
            };
            MemberList.Children.Add(MemberRow(member.Name + (you ? " (you)" : ""), state,
                _host is not null ? () => _ = KickAsync(member) : null, member.State == MemberState.Failed));
        }
        if (_host is not null)
            foreach (var invited in _invited.Values.Where(i => !party.Members.Any(m => m.Name == i.Name)))
                MemberList.Children.Add(MemberRow(invited.Name, "Invited", null));
        if (party.Members.Count == 0 && _invited.Count == 0 && _host is not null)
            MemberList.Children.Add(Line("Invite friends below, or send them the code.", Muted));
    }

    UIElement MemberRow(string name, string state, Action? remove, bool danger = false)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        if (remove is not null)
        {
            var kick = new Button { Content = "Remove", Style = (Style)FindResource("GhostBtn"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
            kick.Click += (_, _) => remove();
            DockPanel.SetDock(kick, Dock.Right);
            row.Children.Add(kick);
        }
        var stateText = new TextBlock { Text = state, Foreground = danger ? (Brush)FindResource("DangerBrush") : Muted, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(stateText, Dock.Right);
        row.Children.Add(stateText);
        row.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    async Task KickAsync(PartyMember member)
    {
        if (_host is null) return;
        if (!DarkDialog.Show(this, $"Remove {member.Name}?", $"{member.Name} loses their connection to your game and is told they were removed.",
                "Remove", "Cancel", danger: true))
            return;
        _invited.Remove(_invited.FirstOrDefault(i => i.Value.Name == member.Name).Key ?? "");
        await _host.KickAsync(member.Id);
    }

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
        if (!_invited.Remove(from.Id)) return;
        AddStatus($"{from.Name} declined.");
        ShowParty();
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
        if (_presence is null)
        {
            OnlineList.Children.Add(Line("Turn on \"Show me as online\" below to see friends who are online.", Muted));
            return;
        }
        if (!_presence.Connected)
        {
            OnlineList.Children.Add(Line("Connecting to the co-op service…", Muted));
            return;
        }
        var all = _presence.Players;
        var inParty = (_host?.Party.Members ?? []).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var query = SearchBox.Text.Trim();
        var shown = all.Where(p => query.Length == 0 || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        // Two players online with the same name: show part of each one's id so they can be told apart.
        var sameName = all.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        OnlineHeader.Text = all.Count == 0 ? "Friends online" : $"Friends online ({all.Count})";
        if (shown.Count == 0)
        {
            OnlineList.Children.Add(Line(all.Count == 0 ? "Nobody else is online with DrangedAddons right now." : $"Nobody online matches “{query}”.", Muted));
            return;
        }
        var full = (_host?.Party.Members.Count ?? 0) + 1 >= 5;
        foreach (var player in shown)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var invited = _invited.ContainsKey(player.Id);
            var joined = inParty.Contains(player.Name);
            var invite = new Button
            {
                Content = joined ? "In party" : invited ? "Invited" : "Invite",
                Style = (Style)FindResource(invited || joined ? "GhostBtn" : "Btn"),
                Padding = new Thickness(12, 3, 12, 3),
                IsEnabled = !invited && !joined && !full && !_busy && _join is null && CoopTunnel.IsSupported,
            };
            invite.Click += async (_, _) => await InviteAsync(player);
            DockPanel.SetDock(invite, Dock.Right);
            row.Children.Add(invite);
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            label.Inlines.Add(new System.Windows.Documents.Run(player.Name) { FontWeight = FontWeights.SemiBold });
            if (sameName.Contains(player.Name))
                label.Inlines.Add(new System.Windows.Documents.Run($"  #{player.Id[..Math.Min(4, player.Id.Length)]}") { Foreground = Muted });
            row.Children.Add(label);
            OnlineList.Children.Add(row);
        }
    }

    static TextBlock Line(string text, Brush brush) => new() { Text = text, Foreground = brush, TextWrapping = TextWrapping.Wrap };

    /// <summary>Opens the party first if there isn't one, then sends the invite with its code.</summary>
    async Task InviteAsync(CoopPlayer player)
    {
        if (_presence is null) return;
        if (_host is null && !await CreatePartyAsync()) return;
        try
        {
            await _presence.InviteAsync(player, _host!.Code);
            _invited[player.Id] = player;
            AddStatus($"Invited {player.Name}.");
        }
        catch (Exception ex) when (ex is CoopException or System.Net.WebSockets.WebSocketException)
        {
            AddStatus($"Couldn't invite {player.Name}: {ex.Message}. Send them the code instead.", danger: true);
        }
        ShowParty();
        ShowOnline();
    }

    /// <summary>Adds (or refreshes) an invite card: Join joins the party, Decline tells the host.</summary>
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
            if (InParty || _busy) return;
            InviteList.Children.Remove(card);
            Joined?.Invoke(from);
            await JoinPartyAsync(code);
        };
        buttons.Children.Add(decline);
        buttons.Children.Add(join);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(new TextBlock
        {
            Text = $"{from.Name} invites you to their party",
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        card.Child = row;
        InviteList.Children.Add(card);
    }

    void OnlineCheck_Click(object sender, RoutedEventArgs e) => _setOnline(OnlineCheck.IsChecked == true);

    // ---- Party: create, join, start, end ----

    void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (StatusList is not null && !InParty && !_busy) StatusList.Children.Clear();
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_host is not null)
        {
            StartHostGame();
            return;
        }
        if (InParty) return;
        if (HostTab.IsChecked == true)
        {
            await CreatePartyAsync();
            return;
        }
        if (CoopTunnel.NormalizeCode(CodeBox.Text) is not { } code)
        {
            StatusList.Children.Clear();
            AddStatus("A party code is 6 letters and numbers, like K7MPQ2.", danger: true);
            return;
        }
        await JoinPartyAsync(code);
    }

    const string FirewallHint =
        "If Windows asks whether DrangedAddons may use the network, click Allow: the connection to the other players goes through it. " +
        "If it asks about Azeroth Survivors, you can click Cancel: the game only talks to DrangedAddons on this PC.";

    async Task<bool> CreatePartyAsync()
    {
        if (_busy || InParty) return _host is not null;
        _busy = true;
        StatusList.Children.Clear();
        AddStatus(FirewallHint);
        UpdateView();
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var gamePort = CoopTunnel.FreeLocalPort();
            _host = await CoopHost.StartAsync(gamePort, _name, cancel.Token);
            _hostGamePort = gamePort;
            _host.Status += line => Dispatcher.BeginInvoke(() => AddStatus(line));
            _host.PartyChanged += _ => Dispatcher.BeginInvoke(() => { ShowParty(); ShowOnline(); });
            CodeText.Text = _host.Code;
            AddStatus("Party open. Start the game once everyone you invited is Ready.");
            return true;
        }
        catch (Exception ex) when (IsCoopFailure(ex))
        {
            AddStatus(Describe(ex), danger: true);
            await EndAsync();
            return false;
        }
        finally
        {
            _busy = false;
            UpdateView();
        }
    }

    int _hostGamePort;

    void StartHostGame()
    {
        if (_host is null || _host.Started) return;
        try
        {
            StartGame(AzerothSurvivorsGame.DirectCoopEnvironment(_hostGamePort, null, null));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AddStatus("Couldn't start the game: " + ex.Message, danger: true);
            return;
        }
        _ = _host.StartGameAsync();
        AddStatus("Started the game, and told everyone's DrangedAddons to start theirs. Enter the zone once everyone is in.");
        AddStatus("Keep this window open while you play: the connection runs through it.");
        UpdateView();
    }

    async Task JoinPartyAsync(string code)
    {
        if (_busy || InParty) return;
        _busy = true;
        JoinTab.IsChecked = true;
        CodeBox.Text = code;
        StatusList.Children.Clear();
        AddStatus(FirewallHint);
        UpdateView();
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _join = await CoopJoin.ConnectAsync(code, _name, line => Dispatcher.BeginInvoke(() => AddStatus(line)), cancel.Token);
            _join.Status += line => Dispatcher.BeginInvoke(() => AddStatus(line, danger: true));
            _join.PartyChanged += _ => Dispatcher.BeginInvoke(ShowParty);
            _join.StartRequested += () => Dispatcher.BeginInvoke(StartJoinGame);
            _join.Ended += why => Dispatcher.BeginInvoke(async () =>
            {
                AddStatus(why, danger: true);
                await EndAsync();
            });
            AddStatus("Connected to the host directly.");
            if (_join.Started) StartJoinGame();
            else AddStatus("Waiting for the host to start the game. Your game starts by itself when they do.");
        }
        catch (Exception ex) when (IsCoopFailure(ex))
        {
            AddStatus(Describe(ex), danger: true);
            await EndAsync();
        }
        finally
        {
            _busy = false;
            UpdateView();
        }
    }

    void StartJoinGame()
    {
        if (_join is null || _game is not null) return;
        try
        {
            StartGame(AzerothSurvivorsGame.DirectCoopEnvironment(null, $"127.0.0.1:{_join.LocalPort}", null));
            AddStatus("The host started: starting your game. It joins their party.");
            AddStatus("Keep this window open while you play: the connection runs through it.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AddStatus("Couldn't start the game: " + ex.Message, danger: true);
        }
        UpdateView();
    }

    static bool IsCoopFailure(Exception ex) =>
        ex is CoopException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException
            or OperationCanceledException or System.Net.Quic.QuicException or System.Security.Authentication.AuthenticationException;

    static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "Timed out.",
        System.Net.WebSockets.WebSocketException => "Couldn't reach the co-op service. Check your internet connection.",
        System.Security.Authentication.AuthenticationException => "The connection didn't pass its security check, so it was dropped.",
        _ => char.ToUpper(ex.Message[0]) + ex.Message[1..] + ".",
    };

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
            if (!InParty) return;
            AddStatus(_host is not null ? "Your game closed, so the party ended." : "Your game closed, so you left the party.");
            await EndAsync();
        });
    }

    async Task EndAsync()
    {
        var host = _host;
        var join = _join;
        _host = null;
        _join = null;
        _invited.Clear();
        _game?.Dispose();
        _game = null;
        if (host is not null) await host.DisposeAsync();
        if (join is not null) await join.DisposeAsync();
        UpdateView();
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

    async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!InParty)
        {
            Close();
            return;
        }
        if (!ConfirmEnd()) return;
        AddStatus(_host is not null ? "You disbanded the party." : "You left the party.");
        await EndAsync();
    }

    bool ConfirmEnd()
    {
        var playing = _game is { HasExited: false };
        return DarkDialog.Show(this, _host is not null ? "Disband the party?" : "Leave the party?",
            _host is not null
                ? playing ? "Everyone else loses their connection to your game." : "Everyone in it is told the party closed."
                : playing ? "Your game loses its connection to the host." : "The host sees you leave.",
            _host is not null ? "Disband" : "Leave", "Stay");
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (InParty && !ConfirmEnd())
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
