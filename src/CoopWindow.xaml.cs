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

    /// <summary>The game these lobbies are for: its id tags the listed lobby, and only its lobbies are listed.</summary>
    static readonly string CoopGameId = GameDefinition.All.First(g => g.HasDirectCoop).Id;
    readonly string _name;
    readonly string _key;
    /// <summary>Hosting started (true) or ended (false): the manager keeps this player online while hosting.</summary>
    readonly Action<bool> _hosting;
    /// <summary>Starts the game the ordinary way (Play), as the main window's ▶ does for other games.</summary>
    readonly Action _play;
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

    public CoopWindow(string exe, string name, string key, Action<bool> hosting, Action play)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _exe = exe;
        _name = name;
        _key = key;
        _hosting = hosting;
        _play = play;
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
        // Start and Join are small; the lobby is wide enough for the slots beside the lobby chat.
        Width = screen is Screen.Host or Screen.Joined ? 1000 : 560;
        StartPanel.Visibility = Visible(screen == Screen.Start);
        JoinPanel.Visibility = Visible(screen == Screen.Join);
        LobbyPanel.Visibility = Visible(screen is Screen.Host or Screen.Joined);
        HostSettings.Visibility = Visible(screen == Screen.Host);
        JoinedInfo.Visibility = Visible(screen == Screen.Joined);
        StartButton.Visibility = Visible(screen == Screen.Host);
        TitleText.Text = screen switch
        {
            Screen.Host => "Your lobby",
            Screen.Join => "Join a lobby",
            Screen.Joined => "In a lobby",
            _ => "Azeroth Survivors",
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
        LogRosterChanges();
        ShowLobbyInfo();
        ShowSlots();
        ShowInviteList();
        ShowLobbies();
    }

    void Play_Click(object sender, RoutedEventArgs e)
    {
        _play();
        Close();
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
            _host = await CoopHost.StartAsync(_hostGamePort, _name, cancel.Token, CoopGameId);
            LogList.Children.Clear();
            _roster.Clear();
            _started = false;
            _host.Status += line => Dispatcher.BeginInvoke(() => AddStatus(line));
            _host.Chat += line => Dispatcher.BeginInvoke(() => AddChat(line));
            _host.PartyChanged += party => Dispatcher.BeginInvoke(() =>
            {
                Refresh();
                _ = ListLobbyAsync();
            });
            CodeText.Text = _host.Code;
            AccessText.Text = "";
            CopyButton.Content = "Copy";
            LockedCheck.IsChecked = false;
            HostPasswordBox.Clear();
            _ = ListLobbyAsync();
            Show(Screen.Host);
            AddLog("You opened the lobby.");
            _hosting(true);
        }
        catch (Exception ex) when (IsCoopFailure(ex))
        {
            CoopLog.Write("host", ex);
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
        var (wasLocked, hadPassword) = (_host.Locked, _host.HasPassword);
        await _host.SetSettingsAsync(LockedCheck.IsChecked == true, HostPasswordBox.Password);
        if (_host.Locked != wasLocked) AddLog(_host.Locked ? "You locked the lobby." : "You unlocked the lobby.");
        if (_host.HasPassword != hadPassword) AddLog(_host.HasPassword ? "You set a password." : "You removed the password.");
        else if (_host.HasPassword) AddLog("You changed the password.");
        ShowLobbyInfo();
        await ListLobbyAsync();
    }

    /// <summary>Lists this lobby to members online (Join a lobby → Open lobbies); a locked one shows without its code.</summary>
    Task ListLobbyAsync() =>
        _presence is null ? Task.CompletedTask
        : _host is null ? _presence.SetLobbyAsync(null)
        : _presence.SetLobbyAsync(new CoopLobby(_host.Code, _host.Locked, _host.HasPassword, _host.Party.Members.Count + 1, CoopGameId));

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
        _removed.Add(member.Id);
        await _host.KickAsync(member.Id);
        AddLog($"You removed {member.Name}.");
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

    // ---- Lobby chat: the log and messages ----

    /// <summary>Players last seen in the lobby, to log who joined and who left.</summary>
    readonly Dictionary<string, (string Name, MemberState State)> _roster = new();
    readonly HashSet<string> _removed = new();
    bool _started;

    void LogRosterChanges()
    {
        var party = _host?.Party ?? _join?.Party;
        if (party is null) return;
        foreach (var member in party.Members)
        {
            var seen = _roster.TryGetValue(member.Id, out var before);
            var you = _join is not null && member.Id == _join.Id;
            if (member.State == MemberState.Connected && (!seen || before.State != MemberState.Connected) && !you)
                AddLog($"{member.Name} joined.");
            _roster[member.Id] = (member.Name, member.State);
        }
        foreach (var gone in _roster.Keys.Except(party.Members.Select(m => m.Id)).ToList())
        {
            if (!_removed.Remove(gone) && _roster[gone].State == MemberState.Connected) AddLog($"{_roster[gone].Name} left.");
            _roster.Remove(gone);
        }
        if (party.Started && !_started)
        {
            _started = true;
            AddLog(_host is not null ? "You started the game." : $"{party.Host} started the game.");
        }
    }

    void AddLog(string text, bool danger = false) => AddLogLine(new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = danger ? Danger : Muted, FontStyle = FontStyles.Italic,
    });

    void AddChat(ChatLine line)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        block.Inlines.Add(new System.Windows.Documents.Run(line.From)
        {
            FontWeight = FontWeights.SemiBold, Foreground = line.FromHost ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("TextBrush"),
        });
        block.Inlines.Add(new System.Windows.Documents.Run(": " + line.Text));
        AddLogLine(block);
    }

    void AddLogLine(TextBlock block)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var time = new TextBlock { Text = DateTime.Now.ToString("HH:mm"), Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(time, Dock.Left);
        row.Children.Add(time);
        row.Children.Add(block);
        LogList.Children.Add(row);
        while (LogList.Children.Count > 300) LogList.Children.RemoveAt(0);
        LogScroll.ScrollToEnd();
    }

    void ChatBox_TextChanged(object sender, TextChangedEventArgs e) => ChatHint.Visibility = Visible(ChatBox.Text.Length == 0);

    void ChatBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        Send_Click(sender, e);
    }

    async void Send_Click(object sender, RoutedEventArgs e)
    {
        var text = ChatBox.Text.Trim();
        if (text.Length == 0) return;
        ChatBox.Clear();
        if (_host is not null) await _host.SendChatAsync(text);
        else if (_join is not null) await _join.SendChatAsync(text);
    }

    void OpenInvitePopup(UIElement from)
    {
        InvitePopup.PlacementTarget = from;
        InvitePopup.IsOpen = true;
        ShowInviteList();
        SearchBox.Focus();
    }

    // ---- The lobby: header and slots ----

    const int Slots = 5; // the game's party size

    void ShowLobbyInfo()
    {
        if (_host is not null)
        {
            AccessText.Text = _host.Locked ? "Only members you invite can join."
                : _host.HasPassword ? "Anyone with the code and password can join. Invited members skip the password."
                : "Anyone with the code can join.";
        }
        else if (_join?.Party is { } party)
        {
            HostNameText.Text = party.Host;
            GameStateText.Text = party.Started ? "Game started" : "Waiting for the host to start the game";
            CodeText.Text = _joinCode;
        }
    }

    /// <summary>
    /// Five slots, filled in join order: the host, then each player (Connecting, Ready, In game or Couldn't connect),
    /// then members invited but not in yet, then open slots. The host's actions sit on each slot.
    /// </summary>
    void ShowSlots()
    {
        SlotList.Children.Clear();
        SlotDots.Children.Clear();
        var party = _host?.Party ?? _join?.Party;
        if (party is null)
        {
            SlotList.Children.Add(Line("Loading…", Muted));
            return;
        }
        var invited = _host is null ? [] : _invited.Values.Where(i => !party.Members.Any(m => m.Member == i.Id)).ToList();
        var filled = 1 + party.Members.Count;
        SlotCount.Text = $"{filled}/{Slots}";
        for (var i = 0; i < Slots; i++)
            SlotDots.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 9, Height = 9, Margin = new Thickness(0, 0, 5, 0),
                Fill = i < filled ? (Brush)FindResource("AccentBrush") : Brushes.Transparent,
                Stroke = (Brush)FindResource(i < filled ? "AccentBrush" : "LineBrush"), StrokeThickness = 1.5,
            });

        var ready = (Brush)FindResource("ReadyBrush");
        SlotList.Children.Add(Slot(party.Host, _host is not null ? ["Host", "You"] : ["Host"],
            party.Started ? ("In game", ready) : ("Ready", ready), null, highlight: _host is not null));
        foreach (var member in party.Members)
        {
            var you = _join is not null && member.Id == _join.Id;
            var state = member.State switch
            {
                // With the round trip to the host once measured (the host's manager does, every 2 s).
                MemberState.Connected => ((party.Started ? "In game" : "Ready") + (member.Ping is { } ms ? $" · {ms} ms" : ""), ready),
                MemberState.Failed => ("Couldn't connect", Danger),
                _ => ("Connecting…", Muted),
            };
            SlotList.Children.Add(Slot(member.Name, you ? ["You"] : [], state,
                _host is not null ? ("Remove", () => _ = KickAsync(member)) : null, highlight: you));
        }
        foreach (var player in invited.Take(Slots - filled))
            SlotList.Children.Add(Slot(player.Name, [], ("Invited", Muted), null, pending: true));
        for (var i = filled + Math.Min(invited.Count, Slots - filled); i < Slots; i++)
            SlotList.Children.Add(OpenSlot());
    }

    /// <summary>A filled slot: a letter tile, the name with badges, the state, and the host's action.</summary>
    UIElement Slot(string name, string[] badges, (string Text, Brush Brush) state, (string Label, Action Click)? action,
        bool highlight = false, bool pending = false)
    {
        var row = new DockPanel();
        if (action is { } a)
        {
            var button = new Button { Content = a.Label, Style = (Style)FindResource("GhostBtn"), Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            button.Click += (_, _) => a.Click();
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
        }
        var stateText = new TextBlock { Text = state.Text, Foreground = state.Brush, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(stateText, Dock.Right);
        row.Children.Add(stateText);

        var tile = Tile(name, pending);
        DockPanel.SetDock(tile, Dock.Left);
        row.Children.Add(tile);

        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        title.Children.Add(new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Opacity = pending ? 0.6 : 1 });
        foreach (var badge in badges)
            title.Children.Add(new Border
            {
                BorderBrush = (Brush)FindResource("AccentBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = badge, FontSize = 11, Foreground = (Brush)FindResource("AccentBrush") },
            });
        row.Children.Add(title);

        return new Border
        {
            Child = row, Height = 64, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12, 0, 14, 0), CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource(pending ? "PanelBrush" : "Panel2Brush"),
            BorderBrush = (Brush)FindResource(highlight ? "AccentBrush" : "LineBrush"), BorderThickness = new Thickness(1),
        };
    }

    UIElement OpenSlot()
    {
        var row = new DockPanel();
        if (_host is not null)
        {
            var invite = new Button { Content = "Invite", Style = (Style)FindResource("GhostBtn"), Padding = new Thickness(14, 5, 14, 5), VerticalAlignment = VerticalAlignment.Center };
            invite.Click += (_, _) => OpenInvitePopup(invite);
            DockPanel.SetDock(invite, Dock.Right);
            row.Children.Add(invite);
        }
        var tile = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            BorderBrush = (Brush)FindResource("LineBrush"), VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(tile, Dock.Left);
        row.Children.Add(tile);
        row.Children.Add(new TextBlock { Text = "Open slot", Foreground = Muted, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) });
        return new Border
        {
            Child = row, Height = 64, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12, 0, 14, 0), CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)FindResource("LineBrush"), BorderThickness = new Thickness(1), Background = Brushes.Transparent,
        };
    }

    /// <summary>A rounded tile with the name's first letter, coloured from the name so each player keeps one colour.</summary>
    static UIElement Tile(string name, bool pending)
    {
        var hue = (uint)name.ToUpperInvariant().Aggregate(17, (h, c) => h * 31 + c) % 360;
        var color = FromHsl(hue, 0.45, 0.38);
        return new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(color), Opacity = pending ? 0.5 : 1,
            Child = new TextBlock
            {
                Text = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?", FontSize = 18, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
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
        Refresh();
    }

    void PresenceChanged() => Dispatcher.BeginInvoke(Refresh);

    /// <summary>Redraws the member lists (e.g. when a member's picture has arrived).</summary>
    public void RefreshLists()
    {
        ShowInviteList();
        ShowLobbies();
    }

    void PresenceDeclined(CoopPlayer from) => Dispatcher.BeginInvoke(() =>
    {
        if (!_invited.Remove(from.Id)) return;
        AddLog($"{from.Name} declined your invite.");
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
        if (_presence is null) list.Children.Add(Line("You're offline.", Muted));
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
        if (_presence.State == "offline") OnlineList.Children.Add(Line("You appear offline: members can't see you.", Muted));
        if (shown.Count == 0)
        {
            OnlineList.Children.Add(Line(all.Count == 0 ? "Nobody else is online." : "No matches.", Muted));
            return;
        }
        // Friends first, under their own heading; everyone else online after them.
        var friends = shown.Where(p => _presence.IsFriend(p.Id)).ToList();
        var others = shown.Where(p => !_presence.IsFriend(p.Id)).ToList();
        foreach (var (heading, players) in new[] { ("Friends", friends), ("Others online", others) })
        {
            if (players.Count == 0) continue;
            if (friends.Count > 0) OnlineList.Children.Add(ListHeading(heading));
            foreach (var player in players)
            {
                var joined = inLobby.Contains(player.Id);
                var invited = _invited.ContainsKey(player.Id);
                var label = joined ? "In lobby" : invited ? "Invited" : "Invite";
                OnlineList.Children.Add(Row(Display(player, sameName), Account.Label(player.State, player.Game), Account.StateBrush(player.State),
                    (label, () => _ = InviteAsync(player)), enabled: !joined && !invited && !full, player: player));
            }
        }
    }

    TextBlock ListHeading(string text) => new()
    {
        Text = text,
        Foreground = Muted,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, OnlineList.Children.Count == 0 ? 0 : 10, 0, 4),
    };

    async Task InviteAsync(CoopPlayer player)
    {
        if (_presence is null || _host is null) return;
        try
        {
            await _host.AllowAsync(player.Id); // gets in even if the lobby is locked or has a password
            await _presence.InviteAsync(player, _host.Code);
            _invited[player.Id] = player;
            AddLog($"You invited {player.Name}.");
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
        // Lobbies listed without a game came from managers before 0.33, which only hosted this game.
        var hosting = _presence!.Players.Where(p => p.Lobby is { } l && (l.Game ?? CoopGameId) == CoopGameId)
            .OrderBy(p => _presence.IsFriend(p.Id) ? 0 : 1).ToList();
        var sameName = SameNames(_presence.Players);
        if (hosting.Count == 0)
        {
            LobbyList.Children.Add(Line("Nobody is hosting right now.", Muted));
            return;
        }
        foreach (var player in hosting)
        {
            var lobby = player.Lobby!;
            var info = (_presence.IsFriend(player.Id) ? "Friend · " : "")
                       + $"{lobby.Count}/5" + (lobby.Password ? " · password" : "") + (lobby.Locked ? " · locked" : "");
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
            }), enabled: canJoin, player: player));
        }
    }

    string _joinCode = "";

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
            LogList.Children.Clear();
            _roster.Clear();
            _started = false;
            _join.Status += line => Dispatcher.BeginInvoke(() => AddStatus(line, danger: true));
            _join.Chat += line => Dispatcher.BeginInvoke(() => AddChat(line));
            _join.PartyChanged += _ => Dispatcher.BeginInvoke(() =>
            {
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
            _joinCode = code;
            CopyButton.Content = "Copy";
            Show(Screen.Joined);
            AddLog(_join.Party is { } joinedParty ? $"You joined {joinedParty.Host}'s lobby." : "You joined the lobby.");
            if (_join.Started) StartJoinGame();
        }
        catch (Exception ex) when (IsCoopFailure(ex))
        {
            CoopLog.Write("join " + code, ex);
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



    // ---- Shared ----

    static HashSet<string> SameNames(IEnumerable<CoopPlayer> players) =>
        players.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name, plus part of the member id when someone else online has the same name.</summary>
    static string Display(CoopPlayer player, HashSet<string> sameName) =>
        sameName.Contains(player.Name) ? $"{player.Name}  #{player.Id[..Math.Min(4, player.Id.Length)]}" : player.Name;

    UIElement Row(string name, string? state, Brush stateBrush, (string Label, Action Click)? button, bool enabled = true,
        CoopPlayer? player = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        if (player is not null)
        {
            var avatar = Avatar(player);
            DockPanel.SetDock(avatar, Dock.Left);
            row.Children.Add(avatar);
        }
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

    /// <summary>A member's picture (or initials) with their status dot.</summary>
    UIElement Avatar(CoopPlayer player) => MemberUi.Avatar(this, player, player.State);

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
            _hosting(false);
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

    void AddStatus(string text, bool danger = false)
    {
        if (_screen is Screen.Host or Screen.Joined)
        {
            AddLog(text, danger);
            return;
        }
        StatusList.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = danger ? Danger : Muted,
        });
    }
}
