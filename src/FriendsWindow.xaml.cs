using System.Net.WebSockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DrangedAddons;

/// <summary>What the Friends lists need from the rest of the app: you, and the lobby you're in (to invite to or join).</summary>
/// <param name="Picture">Your picture, or null for your initials.</param>
/// <param name="State">Your status ("online", "away", "ingame", "offline").</param>
/// <param name="Hosting">You host a lobby with room: friends get Invite.</param>
/// <param name="GameRunning">A game with lobbies is running and you aren't hosting: friends get Invite to lobby, which
/// sets up a new one. With neither, there's nothing to invite them to, so no button.</param>
/// <param name="LobbyCode">The lobby you're in, if any (its own listing isn't offered to join).</param>
/// <param name="LobbyMembers">Member ids in your lobby or invited to it.</param>
/// <param name="Invites">Lobby invites waiting for an answer.</param>
public sealed record FriendsContext(string Name, ImageSource? Picture, string State, string? Game, bool Hosting, bool GameRunning,
    string? LobbyCode, IReadOnlySet<string> LobbyMembers, IReadOnlyList<(CoopPlayer From, string Code)> Invites);

/// <summary>
/// Friends for every game, laid out like the in-game overlay's Friends window and with the same actions: you at the
/// top; a search that filters friends and finds members to add; lobby invites and friend requests to answer; friends
/// online with Invite (or Invite to lobby, which sets up a new one) and Join; offline friends folded away; requests
/// you've sent. Friendships are kept by the co-op service, so they work while either side is offline.
/// </summary>
public partial class FriendsWindow : Window
{
    CoopPresence? _presence;
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    List<CoopFound> _found = [];
    string _searched = "";
    bool _showOffline;

    /// <summary>You and your lobby, asked for on every redraw.</summary>
    public Func<FriendsContext>? Context { get; set; }

    /// <summary>Invite to lobby on a friend: invites them to yours, or sets up a new lobby with them in it.</summary>
    public event Action<CoopPlayer>? InviteToLobby;
    /// <summary>Join on a friend's lobby or an invite: the lobby's code, and who invited (null when joining from a friend).</summary>
    public event Action<string, CoopPlayer?>? JoinLobby;
    public event Action<CoopPlayer>? DeclineInvite;

    public FriendsWindow(CoopPresence? presence)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            await SearchAsync();
        };
        UsePresence(presence);
        Loaded += (_, _) => SearchBox.Focus();
    }

    public void UsePresence(CoopPresence? presence)
    {
        _presence = presence;
        Refresh();
    }

    Brush Res(string key) => (Brush)FindResource(key);

    /// <summary>Redraws everything from what the co-op service last said (called when friends or who's online change).</summary>
    public void Refresh()
    {
        var context = Context?.Invoke();
        ShowYou(context);
        Lists.Children.Clear();
        if (_presence is not { Connected: true } presence)
        {
            StateText.Text = "Connecting…";
            StateText.Visibility = Visibility.Visible;
            return;
        }
        StateText.Visibility = Visibility.Collapsed;
        var online = presence.Players.ToDictionary(p => p.Id);
        var friends = presence.Friends;
        var query = SearchBox.Text.Trim();
        bool Matches(CoopPlayer p) => query.Length == 0 || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase);

        ShowSearchResults(presence);

        if (context is { Invites.Count: > 0 })
        {
            Heading("Lobby invites");
            foreach (var (from, code) in context.Invites)
                Lists.Children.Add(Row(from, null, "Invited you to their lobby", ("Join", true, () => JoinLobby?.Invoke(code, from)),
                    ("Decline", false, () => DeclineInvite?.Invoke(from))));
        }

        if (friends.Incoming.Count > 0)
        {
            Heading($"Friend requests ({friends.Incoming.Count})");
            foreach (var p in friends.Incoming)
                Lists.Children.Add(Row(p, null, null, ("Accept", true, () => Send(presence.AddFriendAsync(p.Id))),
                    ("Decline", false, () => Send(presence.RemoveFriendAsync(p.Id)))));
        }

        // Friends with what presence knows now (status, lobby, picture); online ones first.
        var all = friends.Friends.Select(f => online.TryGetValue(f.Id, out var live) ? live : f with { State = "offline", Lobby = null })
            .Where(Matches).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var on = all.Where(f => f.State != "offline").ToList();
        var off = all.Where(f => f.State == "offline").ToList();
        Heading($"Online ({on.Count})");
        if (on.Count == 0)
            Lists.Children.Add(Line(friends.Friends.Count == 0 ? "No friends yet. Search above to find someone and add them."
                : query.Length > 0 ? "No friends online match." : "Nobody's online."));
        foreach (var f in on) Lists.Children.Add(FriendRow(presence, context, f));

        if (off.Count > 0)
        {
            var toggle = new Button
            {
                Style = (Style)FindResource("GhostBtn"), HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 4),
                Content = $"{(_showOffline ? "▾" : "▸")}  Offline ({off.Count})", Foreground = Res("MutedBrush"),
            };
            toggle.Click += (_, _) =>
            {
                _showOffline = !_showOffline;
                Refresh();
            };
            Lists.Children.Add(toggle);
            if (_showOffline || query.Length > 0)
                foreach (var f in off) Lists.Children.Add(FriendRow(presence, context, f));
        }

        if (friends.Outgoing.Count > 0)
        {
            Heading("Sent");
            foreach (var p in friends.Outgoing.Where(Matches))
                Lists.Children.Add(Row(p, null, "Waiting for them", ("Cancel", false, () => Send(presence.RemoveFriendAsync(p.Id)))));
        }
    }

    void ShowYou(FriendsContext? context)
    {
        if (context is null) return;
        var you = new CoopPlayer("you", context.Name, State: context.State);
        var picture = MemberUi.Avatar(this, you, context.State, 40);
        if (context.Picture is not null && picture is Grid grid)
            grid.Children.Insert(2, new System.Windows.Shapes.Ellipse { Fill = new ImageBrush(context.Picture) { Stretch = Stretch.UniformToFill } });
        YouPicture.Content = picture;
        YouName.Text = context.Name;
        YouStatus.Text = Account.Label(context.State, context.Game);
        YouStatus.Foreground = Account.StateBrush(context.State);
    }

    /// <summary>A friend: picture with status, name, what they're doing (and their lobby), and what you can do with them.</summary>
    UIElement FriendRow(CoopPresence presence, FriendsContext? context, CoopPlayer f)
    {
        var buttons = new List<(string, bool, Action)>();
        var inLobby = context?.LobbyMembers.Contains(f.Id) == true;
        if (f.State != "offline" && !inLobby && context is { Hosting: true } or { GameRunning: true })
            buttons.Add((context.Hosting ? "Invite" : "Invite to lobby", false, () => InviteToLobby?.Invoke(f)));
        if (f.Lobby is { Code: { } code, Locked: false } lobby && lobby.Count < 5 && code != context?.LobbyCode && context?.Hosting != true)
            buttons.Add(("Join", true, () => JoinLobby?.Invoke(code, null)));
        var status = f.State == "offline" ? "Offline"
            : f.Lobby is { } l ? $"{Account.Label(f.State, f.Game)} · lobby {l.Count}/5{(l.Locked ? " · locked" : "")}"
            : Account.Label(f.State, f.Game);
        var row = Row(f, f.State, status, [.. buttons]);
        // The rest is in a menu, as in the overlay: things you don't do often.
        var more = new Button { Content = "⋯", Style = (Style)FindResource("GhostBtn"), Padding = new Thickness(8, 1, 8, 3), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = $"Remove {f.Name} from friends" };
        remove.Click += (_, _) =>
        {
            if (DarkDialog.Show(this, $"Remove {f.Name} from your friends?",
                    "You'll both stop seeing each other in Friends. You can add each other again later.", "Remove", "Cancel"))
                Send(presence.RemoveFriendAsync(f.Id));
        };
        menu.Items.Add(remove);
        more.ContextMenu = menu;
        more.Click += (_, _) =>
        {
            menu.PlacementTarget = more;
            menu.IsOpen = true;
        };
        DockPanel.SetDock(more, Dock.Right);
        ((DockPanel)row).Children.Insert(1, more);
        return row;
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDelay.Stop();
        if (SearchBox.Text.Trim().Length >= 2) _searchDelay.Start();
        else
        {
            _found = [];
            _searched = "";
        }
        Refresh();
    }

    async Task SearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (_presence is null || query.Length < 2) return;
        var found = await _presence.SearchAsync(query);
        if (SearchBox.Text.Trim() != query) return; // typed on since
        _found = found;
        _searched = query;
        Refresh();
    }

    /// <summary>Members found by the search who aren't friends yet: add them, or answer their request.</summary>
    void ShowSearchResults(CoopPresence presence)
    {
        if (_searched.Length == 0) return;
        // The relation from the search can be stale once a request is sent or answered: use the current lists.
        var friends = presence.Friends;
        var strangers = _found.Where(f => friends.Friends.All(x => x.Id != f.Player.Id)).ToList();
        Heading("Members");
        if (strangers.Count == 0)
        {
            Lists.Children.Add(Line(_found.Count == 0 ? $"Nobody called “{_searched}” has used DrangedAddons yet." : "Everyone found is already your friend."));
            return;
        }
        // Two members with one name: tell them apart by the start of their member id, as the co-op lists do.
        var sameName = strangers.GroupBy(f => f.Player.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in strangers)
        {
            var p = hit.Player;
            var incoming = friends.Incoming.Any(f => f.Id == p.Id);
            var outgoing = friends.Outgoing.Any(f => f.Id == p.Id);
            var note = incoming ? "Asked to be your friend" : outgoing ? "Request sent" : null;
            if (sameName.Contains(p.Name)) note = $"#{p.Id[..4]}" + (note is null ? "" : $" · {note}");
            Lists.Children.Add(outgoing ? Row(p, null, note)
                : Row(p, null, note, (incoming ? "Accept" : "Add friend", true, () => Send(presence.AddFriendAsync(p.Id)))));
        }
    }

    /// <summary>Sends a friends change; the co-op service answers with the new lists, which redraw this window.</summary>
    void Send(Task request)
    {
        request.ContinueWith(t =>
        {
            if (t.Exception?.InnerException is CoopException or WebSocketException)
                Dispatcher.BeginInvoke(() => DarkDialog.Show(this, "Couldn't reach the co-op service", "Try again in a moment."));
        }, TaskScheduler.Default);
    }

    /// <summary>A section heading, small and muted in capitals, as in the overlay.</summary>
    void Heading(string text) => Lists.Children.Add(new TextBlock
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeights.Bold,
        Foreground = Res("MutedBrush"),
        Margin = new Thickness(0, Lists.Children.Count == 0 ? 8 : 18, 0, 6),
    });

    TextBlock Line(string text) => new() { Text = text, Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

    /// <param name="state">Status dot and colour for the note, or null for none (requests and search results).</param>
    /// <param name="note">Small text under the name, or null.</param>
    UIElement Row(CoopPlayer player, string? state, string? note, params (string Label, bool Accent, Action Click)[] buttons)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var avatar = MemberUi.Avatar(this, player, state, 34);
        DockPanel.SetDock(avatar, Dock.Left);
        row.Children.Add(avatar);
        foreach (var (label, accent, click) in buttons.Reverse())
        {
            var button = new Button
            {
                Content = label,
                Style = (Style)FindResource(accent ? "AccentBtn" : "Btn"),
                Padding = new Thickness(12, 3, 12, 3),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Click += (_, _) => click();
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = player.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = state == "offline" ? Res("MutedBrush") : Res("TextBrush"),
        });
        if (note is not null)
            text.Children.Add(new TextBlock
            {
                Text = note, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = state is null or "offline" ? Res("MutedBrush") : Account.StateBrush(state),
            });
        row.Children.Add(text);
        return row;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
