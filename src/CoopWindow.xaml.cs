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
/// Start game), Stage (a new lobby set up while the current one carries on), Join (a code, or an open lobby from members
/// online) and, once joined, the lobby's member list.
/// The window stays open while playing, because the connection lives in this app; closing it, or the game, ends co-op.
/// The game only reads its co-op settings when it starts, so co-op ending here restarts it in solo, and a game already
/// running restarts to play co-op. A player moved to the host's new lobby has their game closed instead, and it starts
/// again with the new lobby's game.
/// </summary>
public partial class CoopWindow : Window
{
    enum Screen { Start, Host, Stage, Join, Joined }

    readonly string _exe;

    /// <summary>The game these lobbies are for: its id tags the listed lobby, and only its lobbies are listed.</summary>
    static readonly string CoopGameId = GameDefinition.All.First(g => g.HasDirectCoop).Id;
    static GameDefinition CoopGame => GameDefinition.All.First(g => g.Id == CoopGameId);
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
    /// <summary>The game as started for co-op; its exit ends co-op. Null once closed from here.</summary>
    Process? _game;
    bool _launching;
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
        StagePanel.Visibility = Visible(screen == Screen.Stage);
        NewLobbyButton.Visibility = Visible(screen == Screen.Host);
        CreateButton.Visibility = Visible(screen == Screen.Stage);
        LobbyPanel.Visibility = Visible(screen is Screen.Host or Screen.Joined);
        HostSettings.Visibility = Visible(screen == Screen.Host);
        JoinedInfo.Visibility = Visible(screen == Screen.Joined);
        StartButton.Visibility = Visible(screen == Screen.Host);
        TitleText.Text = screen switch
        {
            Screen.Host => "Your lobby",
            Screen.Stage => "New lobby",
            Screen.Join => "Join a lobby",
            Screen.Joined => "In a lobby",
            _ => "Azeroth Survivors",
        };
        BackButton.Content = screen switch
        {
            Screen.Host => "Close lobby",
            Screen.Stage => "Cancel",
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
        ShowStage();
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
            case Screen.Stage:
                StatusList.Children.Clear();
                Show(Screen.Host);
                break;
            default:
                if (!ConfirmEnd()) return;
                await EndAsync();
                await RestartSoloAsync();
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
            UseHost(await CoopHost.StartAsync(_hostGamePort, _name, cancel.Token, CoopGameId));
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

    /// <summary>Shows <paramref name="host"/>'s lobby: its code, a fresh lobby chat, and its events (only while it's this window's lobby).</summary>
    void UseHost(CoopHost host)
    {
        _host = host;
        LogList.Children.Clear();
        _roster.Clear();
        _removed.Clear();
        _started = false;
        host.Status += line => Dispatcher.BeginInvoke(() =>
        {
            if (_host == host) AddStatus(line);
        });
        host.Chat += line => Dispatcher.BeginInvoke(() =>
        {
            if (_host == host) AddChat(line);
        });
        host.PartyChanged += party => Dispatcher.BeginInvoke(() =>
        {
            if (_host != host) return;
            Refresh();
            _ = ListLobbyAsync();
        });
        CodeText.Text = host.Code;
        AccessText.Text = "";
        CopyButton.Content = "Copy";
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
        var host = _host;
        if (host is null || host.Started || _launching) return;
        if (GameRunning && !DarkDialog.Show(this, "Restart the game?", "Azeroth Survivors is running. It restarts to host co-op.", "Restart", "Cancel"))
            return;
        _launching = true;
        StartButton.IsEnabled = false;
        try
        {
            await CloseGamesAsync();
            if (_host != host) return;
            StartGame(AzerothSurvivorsGame.DirectCoopEnvironment(_hostGamePort, null, null));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AddStatus("Couldn't start the game: " + ex.Message, danger: true);
            return;
        }
        finally
        {
            _launching = false;
            Refresh();
        }
        await host.StartGameAsync();
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

    // ---- New lobby (host): set up while this one carries on, then opened in one go ----

    /// <summary>Who comes along from this lobby (party ids), and members to invite, for the new lobby being set up.</summary>
    readonly HashSet<string> _bring = [];
    readonly Dictionary<string, CoopPlayer> _stageInvites = new();

    void NewLobby_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null || _busy) return;
        _bring.Clear();
        foreach (var member in _host.Party.Members) _bring.Add(member.Id);
        _stageInvites.Clear();
        StageLockedCheck.IsChecked = _host.Locked;
        StagePasswordBox.Password = HostPasswordBox.Password;
        StatusList.Children.Clear();
        Show(Screen.Stage);
    }

    void StagePassword_Changed(object sender, RoutedEventArgs e) => StagePasswordHint.Visibility = Visible(StagePasswordBox.Password.Length == 0);

    /// <summary>Everyone in this lobby, ticked to come along, then members online to invite; at most a full party.</summary>
    void ShowStage()
    {
        if (_screen != Screen.Stage || _host is null) return;
        var members = _host.Party.Members;
        _bring.IntersectWith(members.Select(m => m.Id));
        var count = 1 + _bring.Count + _stageInvites.Count;
        StageCount.Text = $"{count}/{Slots}";
        CreateButton.IsEnabled = !_busy;

        BringList.Children.Clear();
        if (members.Count == 0) BringList.Children.Add(Line("Nobody else is in this lobby.", Muted));
        foreach (var member in members)
        {
            var bring = _bring.Contains(member.Id);
            var check = new CheckBox
            {
                Content = member.Name, IsChecked = bring, IsEnabled = bring || count < Slots, Margin = new Thickness(0, 4, 0, 4),
                Foreground = (Brush)FindResource("TextBrush"),
            };
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true) _bring.Add(member.Id);
                else _bring.Remove(member.Id);
                ShowStage();
            };
            BringList.Children.Add(check);
        }

        StageInviteList.Children.Clear();
        if (ShowPresenceState(StageInviteList)) return;
        var inLobby = members.Select(m => m.Member).OfType<string>().ToHashSet();
        var others = _presence!.Players.Where(p => !inLobby.Contains(p.Id)).OrderBy(p => _presence.IsFriend(p.Id) ? 0 : 1).ToList();
        if (others.Count == 0)
        {
            StageInviteList.Children.Add(Line("Nobody else is online.", Muted));
            return;
        }
        var sameName = SameNames(_presence.Players);
        foreach (var player in others)
        {
            var added = _stageInvites.ContainsKey(player.Id);
            StageInviteList.Children.Add(Row(Display(player, sameName), Account.Label(player.State, player.Game), Account.StateBrush(player.State),
                (added ? "Added" : "Add", () =>
                {
                    if (!_stageInvites.Remove(player.Id)) _stageInvites[player.Id] = player;
                    ShowStage();
                }), enabled: added || count < Slots, player: player));
        }
    }

    async void CreateLobby_Click(object sender, RoutedEventArgs e) => await CreateLobbyAsync();

    /// <summary>
    /// Opens the new lobby, lets in who's coming along and who's invited, moves those coming along (their managers
    /// leave this lobby before joining the new one), then closes this lobby and the game. The new lobby's game starts
    /// with Start game, as any lobby's does.
    /// </summary>
    async Task CreateLobbyAsync()
    {
        var old = _host;
        if (old is null || _busy) return;
        var bring = old.Party.Members.Where(m => _bring.Contains(m.Id) && m.Member is not null).ToList();
        var left = old.Party.Members.Count - bring.Count;
        var playing = GameRunning;
        if (playing || left > 0)
        {
            var what = new List<string>();
            if (playing) what.Add("Your game closes, and starts again when you start the new lobby's game.");
            if (bring.Count > 0) what.Add($"{string.Join(", ", bring.Select(m => m.Name))} move{(bring.Count == 1 ? "s" : "")} with you.");
            if (left > 0) what.Add("Everyone else leaves the party and their game restarts in solo.");
            if (!DarkDialog.Show(this, "Create the new lobby?", string.Join(" ", what), "Create lobby", "Cancel")) return;
        }
        _busy = true;
        CreateButton.IsEnabled = false;
        StatusList.Children.Clear();
        AddStatus("Opening the new lobby…");
        CoopHost? next = null;
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var port = CoopTunnel.FreeLocalPort();
            next = await CoopHost.StartAsync(port, _name, cancel.Token, CoopGameId);
            var (locked, password) = (StageLockedCheck.IsChecked == true, StagePasswordBox.Password);
            await next.SetSettingsAsync(locked, password);
            foreach (var member in bring) await next.AllowAsync(member.Member!);
            foreach (var player in _stageInvites.Values) await next.AllowAsync(player.Id);

            foreach (var member in bring) await old.MoveAsync(member.Id, next.Code);
            // They leave this lobby on their own; give them a moment before it closes on them.
            for (var waited = 0; waited < 50 && old.Party.Members.Any(m => bring.Any(b => b.Id == m.Id)); waited++)
                await Task.Delay(100);
            _host = null;
            _invited.Clear();
            await old.DisposeAsync();
            await CloseGamesAsync();

            _hostGamePort = port;
            UseHost(next);
            LockedCheck.IsChecked = locked;
            HostPasswordBox.Password = password;
            _passwordDelay.Stop(); // already applied
            PasswordHint.Visibility = Visible(password.Length == 0);
            StatusList.Children.Clear();
            Show(Screen.Host);
            AddLog("You opened a new lobby.");
            foreach (var member in bring) _invited[member.Member!] = new CoopPlayer(member.Member!, member.Name);
            if (bring.Count > 0) AddLog($"Moving {string.Join(", ", bring.Select(m => m.Name))} from your last lobby.");
            foreach (var player in _stageInvites.Values)
            {
                try
                {
                    if (_presence is null) break;
                    await _presence.InviteAsync(player, next.Code);
                    _invited[player.Id] = player;
                    AddLog($"You invited {player.Name}.");
                }
                catch (Exception ex) when (ex is CoopException or System.Net.WebSockets.WebSocketException)
                {
                    AddStatus($"Couldn't invite {player.Name}.", danger: true);
                }
            }
            _stageInvites.Clear();
            _ = ListLobbyAsync();
        }
        catch (Exception ex) when (IsCoopFailure(ex))
        {
            CoopLog.Write("new lobby", ex);
            if (next is not null && _host != next) await next.DisposeAsync();
            StatusList.Children.Clear();
            AddStatus(Describe(ex), danger: true);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
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
        var own = Account.LoadOwnPicture();
        // A joiner knows the host only by name; the host's presence shows the lobby by its code.
        var hostPicture = _host is not null ? own
            : PictureOf(_presence?.Players.FirstOrDefault(p => p.Lobby?.Code is { } code && code == _joinCode));
        SlotList.Children.Add(Slot(party.Host, hostPicture, _host is not null ? ["Host", "You"] : ["Host"],
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
            SlotList.Children.Add(Slot(member.Name, you ? own : PictureOf(member.Member), you ? ["You"] : [], state,
                _host is not null ? ("Remove", () => _ = KickAsync(member)) : null, highlight: you));
        }
        foreach (var player in invited.Take(Slots - filled))
            SlotList.Children.Add(Slot(player.Name, PictureOf(player), [], ("Invited", Muted), null, pending: true));
        for (var i = filled + Math.Min(invited.Count, Slots - filled); i < Slots; i++)
            SlotList.Children.Add(OpenSlot());
    }

    /// <summary>A member's picture from what presence knows of them (online players, then friends and requests); null for none.</summary>
    ImageSource? PictureOf(string? member)
    {
        if (member is null || _presence is null) return null;
        var friends = _presence.Friends;
        return PictureOf(_presence.Players.Concat(friends.Friends).Concat(friends.Incoming).Concat(friends.Outgoing)
            .FirstOrDefault(p => p.Id == member && p.Avatar is not null));
    }

    static ImageSource? PictureOf(CoopPlayer? player) => player is null ? null : Avatars.Get(player.Id, player.Avatar);

    /// <summary>A filled slot: the player's picture (or a letter tile), the name with badges, the state, and the host's action.</summary>
    UIElement Slot(string name, ImageSource? picture, string[] badges, (string Text, Brush Brush) state, (string Label, Action Click)? action,
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

        var tile = Tile(name, picture, pending);
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

    /// <summary>A rounded tile with the player's picture, or the name's first letter coloured from the name so each player keeps one colour.</summary>
    static UIElement Tile(string name, ImageSource? picture, bool pending)
    {
        if (picture is not null)
            return new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center,
                Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill }, Opacity = pending ? 0.5 : 1,
            };
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
        ShowSlots();
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

    /// <summary>Joins the lobby with <paramref name="code"/>; false if that didn't work (the reason is shown).</summary>
    async Task<bool> JoinAsync(string code, CoopPlayer? invitedBy = null)
    {
        if (_busy || _host is not null || _join is not null) return false;
        _busy = true;
        if (_screen != Screen.Join) Show(Screen.Join);
        CodeBox.Text = code;
        StatusList.Children.Clear();
        Refresh();
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var join = await CoopJoin.ConnectAsync(code, _name, _key, JoinPasswordBox.Password,
                line => Dispatcher.BeginInvoke(() => { StatusList.Children.Clear(); AddStatus(line); }), cancel.Token);
            _join = join;
            LogList.Children.Clear();
            _roster.Clear();
            _started = false;
            join.Status += line => Dispatcher.BeginInvoke(() =>
            {
                if (_join == join) AddStatus(line, danger: true);
            });
            join.Chat += line => Dispatcher.BeginInvoke(() =>
            {
                if (_join == join) AddChat(line);
            });
            join.PartyChanged += _ => Dispatcher.BeginInvoke(() =>
            {
                if (_join == join) Refresh();
            });
            join.StartRequested += () => Dispatcher.BeginInvoke(() =>
            {
                if (_join == join) _ = StartJoinGameAsync();
            });
            join.Ended += why => Dispatcher.BeginInvoke(async () =>
            {
                if (_join != join) return;
                var playing = GameRunning;
                await EndAsync();
                await RestartSoloAsync();
                Show(Screen.Start);
                AddStatus(why + (playing ? " Your game restarted in solo." : ""), danger: true);
            });
            join.Moved += next => Dispatcher.BeginInvoke(async () =>
            {
                if (_join != join) return;
                var host = join.Party?.Host ?? "The host";
                var playing = GameRunning;
                await EndAsync(); // out of this lobby before joining the next
                await CloseGamesAsync();
                if (await JoinAsync(next)) AddLog($"{host} moved the party to a new lobby.");
                else if (playing) StartSolo();
            });
            StatusList.Children.Clear();
            JoinPasswordBox.Clear();
            JoinPasswordRow.Visibility = Visibility.Collapsed;
            _joinCode = code;
            CopyButton.Content = "Copy";
            AddLog(join.Party is { } joinedParty ? $"You joined {joinedParty.Host}'s lobby." : "You joined the lobby.");
            Show(Screen.Joined); // logs who's already in
            if (join.Started) _ = StartJoinGameAsync();
            return true;
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
            return false;
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    /// <summary>The host started: start the game pointed at this join, restarting a copy that's already running.</summary>
    async Task StartJoinGameAsync()
    {
        var join = _join;
        if (join is null || _game is not null || _launching) return;
        _launching = true;
        try
        {
            await CloseGamesAsync();
            if (_join != join) return;
            StartGame(AzerothSurvivorsGame.DirectCoopEnvironment(null, $"127.0.0.1:{join.LocalPort}", null));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AddStatus("Couldn't start the game: " + ex.Message, danger: true);
        }
        finally
        {
            _launching = false;
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
        var game = GameStarter.Start(CoopGame, _exe, "", env);
        _game = game;
        game.EnableRaisingEvents = true;
        // The player closed the game: co-op ends, and the game stays closed.
        game.Exited += (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            if (_game != game) return; // closed from here
            _game = null;
            game.Dispose();
            if (_host is null && _join is null) return;
            await EndAsync();
            Show(Screen.Start);
            AddStatus("The game closed, so co-op ended.");
        });
    }

    /// <summary>Starts the game the ordinary way, without co-op; its exit means nothing to this window.</summary>
    void StartSolo()
    {
        try
        {
            GameStarter.Start(CoopGame, _exe, "").Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            AddStatus("Couldn't start the game: " + ex.Message, danger: true);
        }
    }

    /// <summary>Co-op ended from here: a game that's running restarts in solo, since it can't leave co-op on its own.</summary>
    async Task RestartSoloAsync()
    {
        if (!GameRunning) return;
        await CloseGamesAsync();
        StartSolo();
    }

    bool GameRunning
    {
        get
        {
            var running = RunningGames();
            foreach (var game in running) game.Dispose();
            return running.Count > 0;
        }
    }

    /// <summary>Copies of the game running from this exe, whoever started them.</summary>
    List<Process> RunningGames()
    {
        var running = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_exe)))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, _exe, StringComparison.OrdinalIgnoreCase))
                {
                    running.Add(process);
                    continue;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            process.Dispose();
        }
        return running;
    }

    /// <summary>
    /// Closes every copy of the game running from this exe (the one started for co-op, or one started some other way)
    /// as its close button would, and ends any still running 10 seconds later.
    /// </summary>
    async Task CloseGamesAsync()
    {
        _game?.Dispose();
        _game = null; // its exit no longer ends co-op
        foreach (var game in RunningGames())
        {
            using (game)
            {
                try
                {
                    game.CloseMainWindow();
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await game.WaitForExitAsync(wait.Token); }
                    catch (OperationCanceledException)
                    {
                        game.Kill(entireProcessTree: true);
                        await game.WaitForExitAsync();
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    async Task EndAsync()
    {
        var host = _host;
        var join = _join;
        _host = null;
        _join = null;
        _invited.Clear();
        if (host is not null)
        {
            if (_presence is not null) await _presence.SetLobbyAsync(null);
            await host.DisposeAsync();
            _hosting(false);
        }
        if (join is not null) await join.DisposeAsync();
    }

    /// <summary>Asks before ending co-op when that disconnects anyone or restarts the game; true to go ahead.</summary>
    bool ConfirmEnd()
    {
        if (_host is null && _join is null) return true;
        var others = _host is not null && _host.Party.Members.Count > 0;
        var playing = GameRunning;
        if (!others && !playing) return true;
        var what = string.Join(" ", new[] { others ? "Everyone in it is disconnected." : null, playing ? "Your game restarts in solo." : null }.OfType<string>());
        return _host is not null
            ? DarkDialog.Show(this, "Close the lobby?", what, "Close lobby", "Stay")
            : DarkDialog.Show(this, "Leave the lobby?", what, "Leave", "Stay");
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
        var coop = _host is not null || _join is not null;
        await EndAsync();
        UsePresence(null);
        if (coop) await RestartSoloAsync();
    }

    void AddStatus(string text, bool danger = false)
    {
        if (_screen is Screen.Host or Screen.Joined && (_host is not null || _join is not null))
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
