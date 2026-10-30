using System.Net.WebSockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DrangedAddons;

/// <summary>
/// Friends for every game: search members by name, send and answer friend requests, see friends' status and lobbies.
/// Friendships are kept by the co-op service, so they work while either side is offline.
/// </summary>
public partial class FriendsWindow : Window
{
    CoopPresence? _presence;
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    List<CoopFound> _found = [];
    string _searched = "";

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

    Brush Muted => (Brush)FindResource("MutedBrush");

    /// <summary>Redraws everything from what the co-op service last said (called when friends or who's online change).</summary>
    public void Refresh()
    {
        ShowSearchResults();
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

        if (friends.Incoming.Count > 0)
        {
            Heading($"Requests ({friends.Incoming.Count})");
            foreach (var p in friends.Incoming)
                Lists.Children.Add(Row(p, null, null, ("Accept", true, () => Send(presence.AddFriendAsync(p.Id))),
                    ("Decline", false, () => Send(presence.RemoveFriendAsync(p.Id)))));
        }

        // Online friends first (with their live status and picture), then the rest by name.
        var shown = friends.Friends
            .Select(f => online.TryGetValue(f.Id, out var live) ? live : f)
            .OrderBy(f => online.ContainsKey(f.Id) ? 0 : 1)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Heading(shown.Count == 0 ? "Friends" : $"Friends ({shown.Count(f => online.ContainsKey(f.Id))} online)");
        if (shown.Count == 0) Lists.Children.Add(Line("Find someone by name above to add them."));
        foreach (var f in shown)
        {
            var isOnline = online.ContainsKey(f.Id);
            var status = !isOnline ? "Offline"
                : f.Lobby is { } lobby ? $"{Account.Label(f.State, f.Game)} · hosting {lobby.Count}/5"
                : Account.Label(f.State, f.Game);
            Lists.Children.Add(Row(f, isOnline ? f.State : "offline", status, ("Remove", false, () =>
            {
                if (DarkDialog.Show(this, $"Remove {f.Name} from your friends?",
                        "You'll both stop seeing each other in Friends. You can add each other again later.", "Remove", "Cancel"))
                    Send(presence.RemoveFriendAsync(f.Id));
            })));
        }

        if (friends.Outgoing.Count > 0)
        {
            Heading("Sent");
            foreach (var p in friends.Outgoing)
                Lists.Children.Add(Row(p, null, "Waiting for them", ("Cancel", false, () => Send(presence.RemoveFriendAsync(p.Id)))));
        }
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
            ShowSearchResults();
        }
    }

    async Task SearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (_presence is null || query.Length < 2) return;
        var found = await _presence.SearchAsync(query);
        if (SearchBox.Text.Trim() != query) return; // typed on since
        _found = found;
        _searched = query;
        ShowSearchResults();
    }

    void ShowSearchResults()
    {
        SearchResults.Children.Clear();
        SearchResults.Visibility = _searched.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_searched.Length == 0 || _presence is not { } presence) return;
        if (_found.Count == 0)
        {
            SearchResults.Children.Add(Line($"Nobody called “{_searched}” has used DrangedAddons yet."));
            return;
        }
        // The relation from the search can be stale once a request is sent or answered: use the current lists.
        var friends = presence.Friends;
        // Two members with one name: tell them apart by the start of their member id, as the co-op lists do.
        var sameName = _found.GroupBy(f => f.Player.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in _found)
        {
            var p = hit.Player;
            var relation = friends.Friends.Any(f => f.Id == p.Id) ? "friend"
                : friends.Incoming.Any(f => f.Id == p.Id) ? "incoming"
                : friends.Outgoing.Any(f => f.Id == p.Id) ? "outgoing"
                : null;
            (string, bool, Action)? button = relation switch
            {
                "friend" => null,
                "incoming" => ("Accept", true, () => Send(presence.AddFriendAsync(p.Id))),
                "outgoing" => null,
                _ => ("Add friend", true, () => Send(presence.AddFriendAsync(p.Id))),
            };
            var note = relation switch { "friend" => "Friend", "outgoing" => "Request sent", "incoming" => "Asked you", _ => null };
            if (sameName.Contains(p.Name)) note = $"#{p.Id[..4]}" + (note is null ? "" : $" · {note}");
            SearchResults.Children.Add(button is { } b ? Row(p, null, note, b) : Row(p, null, note));
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

    void Heading(string text) => Lists.Children.Add(new TextBlock
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, Lists.Children.Count == 0 ? 4 : 16, 0, 6),
    });

    TextBlock Line(string text) => new() { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };

    /// <param name="state">Status dot to show, or null for none (requests and search results don't show status).</param>
    /// <param name="note">Small text under the name, or null.</param>
    UIElement Row(CoopPlayer player, string? state, string? note, params (string Label, bool Accent, Action Click)[] buttons)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var avatar = MemberUi.Avatar(this, player, state, 32);
        DockPanel.SetDock(avatar, Dock.Left);
        row.Children.Add(avatar);
        foreach (var (label, accent, click) in buttons.Reverse())
        {
            var button = new Button
            {
                Content = label,
                Style = (Style)FindResource(accent ? "AccentBtn" : "GhostBtn"),
                Padding = new Thickness(12, 3, 12, 3),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Click += (_, _) => click();
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = player.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (note is not null)
            text.Children.Add(new TextBlock { Text = note, Foreground = Muted, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(text);
        return row;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
