using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DrangedAddons;

/// <summary>
/// Direct co-op for Azeroth Survivors over a hole-punched connection (<see cref="CoopTunnel"/>) instead of the game's
/// relay, which sits overseas: two players in the same country saw 500+ ms through it.
///
/// Screens: Start (Host a lobby / Join a lobby), Host (code, lock, password, who's in with Remove, invite members,
/// Start game), Join (a code, or an open lobby from members online) and, once joined, the lobby's member list.
/// The window stays open while playing, because the connection lives in this app; closing it, or the game, ends co-op.
/// </summary>
public partial class CoopWindow : Window
{
    enum Screen { Start, Host, Join, Joined }

    readonly string _exe;
    readonly string _name;
    readonly string _key;
    readonly Action<bool> _setOnline;
    readonly DispatcherTimer _passwordDelay = new() { Interval = TimeSpan.FromMilliseconds(600) };
    Screen _screen = Screen.Start;
    CoopPresence? _presence;
    CoopHost? _host;
    CoopJoin? _join;
    int _hostGamePort;
    Process? _game;
    bool _busy;
    /// <summary>Members invited to this lobby and not in it yet, by member id.</summary>
    readonly Dictionary<string, CoopPlayer> _invited = new();

    /// <summary>Raised when the player answers someone's invite, so it stops being offered.</summary>
    public event Action<CoopPlayer>? Answered;

    public CoopWindow(string exe, string name, string key, bool online, Action<bool> setOnline)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _exe = exe;
        _name = name;
        _key = key;
        _setOnline = setOnline;
        OnlineCheck.IsChecked = online;
        _passwordDelay.Tick += async (_, _) =>
        {
            _passwordDelay.Stop();
            await ApplySettingsAsync();
        };
        Show(Screen.Start);
        if (!CoopTunnel.IsSupported)
        {
            AddStatus("Needs Windows 11 or Windows Server 2022.", danger: true);
            HostButton.IsEnabled = JoinButton.IsEnabled = false;
        }
    }

    Brush Muted => (Brush)FindResource("MutedBrush");
    Brush Danger => (Brush)FindResource("DangerBrush");

    // ---- Screens ----

    void Show(Screen screen)
    {
        _screen = screen;
        StartPanel.Visibility = Visible(screen == Screen.Start);
        HostPanel.Visibility = Visible(screen == Screen.Host);
        JoinPanel.Visibility = Visible(screen == Screen.Join);
        MembersPanel.Visibility = Visible(screen is Screen.Host or Screen.Joined);
        InvitePanel.Visibility = Visible(screen == Screen.Host);
        StartButton.Visibility = Visible(screen == Screen.Host);
        TitleText.Text = screen switch
        {
            Screen.Host => "Your lobby",
            Screen.Join => "Join a lobby",
            Screen.Joined => _join?.Party is { } p ? $"{p.Host}'s lobby" : "Lobby",
            _ => "Direct co-op",
        };
        BackButton.Content = screen switch
        {
            Screen.Host => "Close lobby",
            Screen.Join => "Back",
            Screen.Joined => "Leave lobby",
            _ => "Close",
        };
        if (screen == Screen.Join) CodeBox.Focus();
        Refresh();
    }

    static Visibility Visible(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    void Refresh()
    {
        if (_host is not null)
        {
            StartButton.Content = _host.Started ? "Game started" : "Start game";
            StartButton.IsEnabled = !_host.Started;
        }
        JoinCodeButton.IsEnabled = !_busy;
        ShowMembers();
        ShowInviteList();
        ShowLobbies();
    }

    void Host_Click(object sender, RoutedEventArgs e) => _ = HostAsync();

    void Join_Click(object sender, RoutedEventArgs e)
    {
        StatusList.Children.Clear();
        Show(Screen.Join);
    }

    async void Back_Click(object sender, RoutedEventArgs e)
    {
        switch (_screen)
        {
            case Screen.Start:
                Close();
                break;
            case Screen.Join:
                StatusList.Children.Clear();
                Show(Screen.Start);
                break;
            default:
                if (!ConfirmEnd()) return;
                await EndAsync();
                StatusList.Children.Clear();
                Show(Screen.Start);
                break;
        }
    }

    // ---- Host ----

    async Task HostAsync()
    {
        if (_busy || _host is not null || _join is not null) return;
        _busy = true;
        HostButton.IsEnabled = JoinButton.IsEnabled = false;
        StatusList.Children.Clear();
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _hostGamePort = CoopTunnel.FreeLocalPort();
            _host = await CoopHost.StartAsync(_hostGamePort, _name, cancel.Token);
            _host.Status += line => Dispatcher.BeginInvoke(() => AddStatus(line));
            _host.PartyChanged += party => Dispatcher.BeginInvoke(() =>
            {
                Refresh();
                _ = ListLobbyAsync();
            });
            CodeText.Text = _host.Code;
            CopyButton.Content = "Copy";
            LockedCheck.IsChecked = false;
            HostPasswordBox.Clear();
            _ = ListLobbyAsync();
            Show(Screen.Host);
        }
        catch (Exception ex) when (IsCoopFailure(ex))
        {
            AddStatus(Describe(ex), danger: true);
            await EndAsync();
        }
        finally
        {
            _busy = false;
            HostButton.IsEnabled = JoinButton.IsEnabled = CoopTunnel.IsSupported;
        }
    }

    void Settings_Changed(object sender, RoutedEventArgs e) => _ = ApplySettingsAsync();

    void HostPassword_Changed(object sender, RoutedEventArgs e)
    {
        PasswordHint.Visibility = Visible(HostPasswordBox.Password.Length == 0);
        _passwordDelay.Stop();
        _passwordDelay.Start(); // apply once typing pauses
    }

    async Task ApplySettingsAsync()
    {
        if (_host is null) return;
        await _host.SetSettingsAsync(LockedCheck.IsChecked == true, HostPasswordBox.Password);
        await ListLobbyAsync();
    }

    /// <summary>Lists this lobby to members online (Join a lobby → Open lobbies); a locked one shows without its code.</summary>
    Task ListLobbyAsync() =>
        _presence is null ? Task.CompletedTask
        : _host is null ? _presence.SetLobbyAsync(null)
        : _presence.SetLobbyAsync(new CoopLobby(_host.Code, _host.Locked, _host.HasPassword, _host.Party.Members.Count + 1));

    async void StartGame_Click(object sender, RoutedEventArgs e)
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
        await _host.StartGameAsync();
        Refresh();
    }

    async Task KickAsync(PartyMember member)
    {
        if (_host is null) return;
        if (!DarkDialog.Show(this, $"Remove {member.Name}?", $"{member.Name} can't rejoin this lobby.", "Remove", "Cancel", danger: true))
            return;
        if (member.Member is { } id) _invited.Remove(id);
        await _host.KickAsync(member.Id);
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

    // ---- Members in the lobby ----

    void ShowMembers()
    {
        MemberList.Children.Clear();
        var party = _host?.Party ?? _join?.Party;
        if (party is null)
        {
            if (_join is not null) MemberList.Children.Add(Line("Loading…", Muted));
            return;
        }
        MembersHeader.Text = $"In the lobby ({party.Members.Count + 1}/5)";
        if (_join is not null && !party.Started) MembersHeader.Text += " · waiting for the host to start";
        MemberList.Children.Add(Row(party.Host + (_host is not null ? " (you)" : ""), "Host", Muted, null));
        foreach (var member in party.Members)
        {
            var you = _join is not null && member.Id == _join.Id;
            var (state, brush) = member.State switch
            {
                MemberState.Connected => (party.Started ? "In game" : "Ready", Muted),
                MemberState.Failed => ("Couldn't connect", Danger),
                _ => ("Connecting…", Muted),
            };
            MemberList.Children.Add(Row(member.Name + (you ? " (you)" : ""), state, brush,
                _host is not null ? ("Remove", () => _ = KickAsync(member)) : null));
        }
        if (_host is not null)
            foreach (var invited in _invited.Values.Where(i => !party.Members.Any(m => m.Member == i.Id)))
                MemberList.Children.Add(Row(invited.Name, "Invited", Muted, null));
    }

    // ---- Invite members (host) ----

    /// <summary>Follows <paramref name="presence"/> (null when offline) for members online, open lobbies and declines.</summary>
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
            _ = ListLobbyAsync();
        }
        OnlineCheck.ToolTip = presence is null ? null : $"Members see you as {presence.Name}";
        Refresh();
    }

    void PresenceChanged() => Dispatcher.BeginInvoke(Refresh);

    void PresenceDeclined(CoopPlayer from) => Dispatcher.BeginInvoke(() =>
    {
        if (!_invited.Remove(from.Id)) return;
        AddStatus($"{from.Name} declined.");
        Refresh();
    });

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = Visible(SearchBox.Text.Length == 0);
        ShowInviteList();
    }

    /// <summary>A row's message when there's nothing to list: offline, connecting, or nobody.</summary>
    bool ShowPresenceState(Panel list)
    {
        if (_presence is null) list.Children.Add(Line("Turn on Show me online to see members.", Muted));
        else if (!_presence.Connected) list.Children.Add(Line("Connecting…", Muted));
        else return false;
        return true;
    }

    void ShowInviteList()
    {
        OnlineList.Children.Clear();
        if (_host is null || ShowPresenceState(OnlineList)) return;
        var all = _presence!.Players;
        var query = SearchBox.Text.Trim();
        var shown = all.Where(p => query.Length == 0 || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var sameName = SameNames(all);
        var inLobby = _host.Party.Members.Select(m => m.Member).OfType<string>().ToHashSet();
        var full = _host.Party.Members.Count + 1 >= 5;
        if (shown.Count == 0)
        {
            OnlineList.Children.Add(Line(all.Count == 0 ? "Nobody else is online." : "No matches.", Muted));
            return;
        }
        foreach (var player in shown)
        {
            var joined = inLobby.Contains(player.Id);
            var invited = _invited.ContainsKey(player.Id);
            var label = joined ? "In lobby" : invited ? "Invited" : "Invite";
            OnlineList.Children.Add(Row(Display(player, sameName), null, Muted,
                (label, () => _ = InviteAsync(player)), enabled: !joined && !invited && !full));
        }
    }

    async Task InviteAsync(CoopPlayer player)
    {
        if (_presence is null || _host is null) return;
        try
        {
            await _host.AllowAsync(player.Id); // gets in even if the lobby is locked or has a password
            await _presence.InviteAsync(player, _host.Code);
            _invited[player.Id] = player;
        }
        catch (Exception ex) when (ex is CoopException or System.Net.WebSockets.WebSocketException)
        {
            AddStatus($"Couldn't invite {player.Name}.", danger: true);
        }
        Refresh();
    }

    // ---- Join ----

    void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        CodeHint.Visibility = Visible(CodeBox.Text.Length == 0);
        if (!_busy) StatusList.Children.Clear();
    }

    void JoinCode_Click(object sender, RoutedEventArgs e)
    {
        if (CoopTunnel.NormalizeCode(CodeBox.Text) is { } code) _ = JoinAsync(code);
        else
        {
            StatusList.Children.Clear();
            AddStatus("A code is 6 letters and numbers.", danger: true);
        }
    }

    void JoinPassword_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) JoinCode_Click(sender, e);
    }

    /// <summary>Members hosting right now; locked lobbies show but can't be joined from here.</summary>
    void ShowLobbies()
    {
        LobbyList.Children.Clear();
        if (ShowPresenceState(LobbyList)) return;
        var hosting = _presence!.Players.Where(p => p.Lobby is not null).ToList();
        var sameName = SameNames(_presence.Players);
        if (hosting.Count == 0)
        {
            LobbyList.Children.Add(Line("Nobody is hosting right now.", Muted));
            return;
        }
        foreach (var player in hosting)
        {
            var lobby = player.Lobby!;
            var info = $"{lobby.Count}/5" + (lobby.Password ? " · password" : "") + (lobby.Locked ? " · locked" : "");
            var canJoin = lobby is { Locked: false, Code: not null } && lobby.Count < 5 && !_busy;
            LobbyList.Children.Add(Row(Display(player, sameName), info, Muted, ("Join", () =>
            {
                CodeBox.Text = lobby.Code!;
                if (lobby.Password && JoinPasswordBox.Password.Length == 0)
                {
                    JoinPasswordRow.Visibility = Visibility.Visible;
                    JoinPasswordBox.Focus();
                    return;
                }
                _ = JoinAsync(lobby.Code!);
            }), enabled: canJoin));
        }
    }

    async Task JoinAsync(string code, CoopPlayer? invitedBy = null)
    {
        if (_busy || _host is not null || _join is not null) return;
        _busy = true;
        if (_screen != Screen.Join) Show(Screen.Join);
        CodeBox.Text = code;
        StatusList.Children.Clear();
        Refresh();
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _join = await CoopJoin.ConnectAsync(code, _name, _key, JoinPasswordBox.Password,
                line => Dispatcher.BeginInvoke(() => { StatusList.Children.Clear(); AddStatus(line); }), cancel.Token);
            _join.Status += line => Dispatcher.BeginInvoke(() => AddStatus(line, danger: true));
            _join.PartyChanged += _ => Dispatcher.BeginInvoke(() =>
            {
                if (_screen == Screen.Joined) TitleText.Text = $"{_join?.Party?.Host}'s lobby";
                Refresh();
            });
            _join.StartRequested += () => Dispatcher.BeginInvoke(StartJoinGame);
            _join.Ended += why => Dispatcher.BeginInvoke(async () =>
            {
                await EndAsync();
                Show(Screen.Start);
                AddStatus(why, danger: true);
            });
            StatusList.Children.Clear();
            JoinPasswordBox.Clear();
            JoinPasswordRow.Visibility = Visibility.Collapsed;
            Show(Screen.Joined);
            if (_join.Started) StartJoinGame();
        }
        catch (Exception ex) when (IsCoopFailure(ex))
        {
            StatusList.Children.Clear();
            AddStatus(Describe(ex), danger: true);
            if (ex is CoopException { Reason: "password" or "wrong password" })
            {
                JoinPasswordRow.Visibility = Visibility.Visible;
                JoinPasswordBox.Focus();
            }
            await EndAsync();
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    void StartJoinGame()
    {
        if (_join is null || _game is not null) return;
        try
        {
            StartGame(AzerothSurvivorsGame.DirectCoopEnvironment(null, $"127.0.0.1:{_join.LocalPort}", null));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AddStatus("Couldn't start the game: " + ex.Message, danger: true);
        }
        Refresh();
    }

    // ---- Invites to this player ----

    /// <summary>Adds (or refreshes) an invite card: Join joins that lobby, Decline tells the host.</summary>
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
            Answered?.Invoke(from);
            try { if (_presence is not null) await _presence.DeclineAsync(from); }
            catch (Exception ex) when (ex is CoopException or System.Net.WebSockets.WebSocketException) { }
        };
        join.Click += async (_, _) =>
        {
            if (_busy || _host is not null || _join is not null) return;
            InviteList.Children.Remove(card);
            Answered?.Invoke(from);
            await JoinAsync(code, from);
        };
        buttons.Children.Add(decline);
        buttons.Children.Add(join);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(new TextBlock { Text = $"{from.Name} invited you", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        card.Child = row;
        InviteList.Children.Add(card);
    }

    void OnlineCheck_Click(object sender, RoutedEventArgs e) => _setOnline(OnlineCheck.IsChecked == true);

    /// <summary>The same setting changed from the manager's settings menu.</summary>
    public void SetOnlineCheck(bool online) => OnlineCheck.IsChecked = online;

    // ---- Shared ----

    static HashSet<string> SameNames(IEnumerable<CoopPlayer> players) =>
        players.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name, plus part of the member id when someone else online has the same name.</summary>
    static string Display(CoopPlayer player, HashSet<string> sameName) =>
        sameName.Contains(player.Name) ? $"{player.Name}  #{player.Id[..Math.Min(4, player.Id.Length)]}" : player.Name;

    UIElement Row(string name, string? state, Brush stateBrush, (string Label, Action Click)? button, bool enabled = true)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        if (button is { } b)
        {
            var control = new Button
            {
                Content = b.Label,
                Style = (Style)FindResource(enabled ? "Btn" : "GhostBtn"),
                Padding = new Thickness(12, 3, 12, 3),
                Margin = new Thickness(10, 0, 0, 0),
                IsEnabled = enabled,
            };
            control.Click += (_, _) => b.Click();
            DockPanel.SetDock(control, Dock.Right);
            row.Children.Add(control);
        }
        if (state is not null)
        {
            var text = new TextBlock { Text = state, Foreground = stateBrush, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(text, Dock.Right);
            row.Children.Add(text);
        }
        row.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }

    static TextBlock Line(string text, Brush brush) => new() { Text = text, Foreground = brush, TextWrapping = TextWrapping.Wrap };

    static bool IsCoopFailure(Exception ex) =>
        ex is CoopException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException
            or OperationCanceledException or System.Net.Quic.QuicException or System.Security.Authentication.AuthenticationException;

    static string Describe(Exception ex) => ex switch
    {
        CoopException => ex.Message,
        OperationCanceledException => "Timed out.",
        System.Net.WebSockets.WebSocketException => "Couldn't reach the co-op service.",
        System.Security.Authentication.AuthenticationException => "The connection failed its security check.",
        _ => "Couldn't connect.",
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
            if (_host is null && _join is null) return;
            await EndAsync();
            Show(Screen.Start);
            AddStatus("The game closed, so co-op ended.");
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
        if (host is not null)
        {
            if (_presence is not null) await _presence.SetLobbyAsync(null);
            await host.DisposeAsync();
        }
        if (join is not null) await join.DisposeAsync();
    }

    bool ConfirmEnd()
    {
        if (_host is null && _join is null) return true;
        var others = _host is not null ? _host.Party.Members.Count > 0 : true;
        if (!others && _game is not { HasExited: false }) return true;
        return _host is not null
            ? DarkDialog.Show(this, "Close the lobby?", "Everyone in it is disconnected.", "Close lobby", "Stay")
            : DarkDialog.Show(this, "Leave the lobby?", "Your game is disconnected from the host.", "Leave", "Stay");
    }

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!ConfirmEnd())
        {
            e.Cancel = true;
            return;
        }
        _passwordDelay.Stop();
        await EndAsync();
        UsePresence(null);
    }

    void AddStatus(string text, bool danger = false) =>
        StatusList.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = danger ? Danger : Muted,
        });
}
