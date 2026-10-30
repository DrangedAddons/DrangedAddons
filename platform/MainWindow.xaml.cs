using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using DrangedAddons;

namespace DrangedPlatform;

/// <summary>
/// The platform at a glance: whether the Worker and STUN answer, live counts from /stats, each game's profile, the
/// connectivity types, what runs where, and the security controls. Live parts refresh every 15 seconds.
/// </summary>
public partial class MainWindow : Window
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    bool _refreshing;
    Check? _worker, _stun;
    Stats? _stats;
    StatsProblem _problem = StatsProblem.None;

    static readonly Dictionary<string, string> GameNames = PlatformData.Profiles.ToDictionary(p => p.game_id, p => p.game);

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Title = $"DrangedAddons Platform {Installer.Version}";
        _timer.Tick += async (_, _) => await RefreshAsync();
        Loaded += async (_, _) =>
        {
            Draw();
            await RefreshAsync();
            _timer.Start();
        };
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var worker = Live.PingWorkerAsync();
            var stun = Live.PingStunAsync();
            var stats = Live.GetStatsAsync();
            await Task.WhenAll(worker, stun, stats);
            (_worker, _stun) = (worker.Result, stun.Result);
            (_stats, _problem) = stats.Result;
            UpdatedText.Text = $"Updated {DateTime.Now:T}";
            Draw();
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }

    // ---- Drawing ----

    Brush Res(string key) => (Brush)FindResource(key);

    void Draw()
    {
        Root.Children.Clear();
        DrawHealth();
        DrawLive();
        DrawProfiles();
        DrawConnectivity();
        DrawInfrastructure();
        DrawSecurity();
    }

    void DrawHealth()
    {
        var row = Section("Services", "Checked from this PC every 15 seconds.");
        var grid = Columns(3, 260);
        grid.Children.Add(HealthCard("Rendezvous Worker", Live.WorkerHost, _worker));
        grid.Children.Add(HealthCard("STUN", $"{Live.StunHost}:3478", _stun));
        var (ok, detail) = _problem switch
        {
            StatsProblem.None when _stats is not null => ((bool?)true, "Counts only, admin token"),
            StatsProblem.NoToken => (false, "No token on this PC"),
            StatsProblem.NotDeployed => (false, "Not deployed to the Worker yet"),
            StatsProblem.Refused => (false, "Token refused"),
            StatsProblem.Unreachable => (false, "Couldn't reach the Worker"),
            _ => (null, "Checking…"),
        };
        grid.Children.Add(HealthCard("Live stats", "/stats", ok is null ? null : new Check(ok.Value, null, detail)));
        row.Children.Add(grid);
    }

    UIElement HealthCard(string name, string where, Check? check)
    {
        var card = Card();
        var head = new DockPanel();
        var state = check is null ? ("Checking…", Res("MutedBrush"))
            : check.Ok ? (check.Milliseconds is { } ms ? $"{ms} ms" : "OK", Res("OkBrush"))
            : ("Down", Res("BadBrush"));
        head.Children.Add(Docked(Text(state.Item1, state.Item2, 13, FontWeights.SemiBold), System.Windows.Controls.Dock.Right));
        head.Children.Add(Text(name, Res("TextBrush"), 14, FontWeights.SemiBold));
        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(Text(where, Res("MutedBrush"), 12));
        if (check is not null) stack.Children.Add(Text(check.Detail, check.Ok ? Res("MutedBrush") : Res("BadBrush"), 12));
        card.Child = stack;
        return card;
    }

    void DrawLive()
    {
        var section = Section("Live now", _stats is { } s
            ? $"From the Worker at {s.At.ToLocalTime():T}. Counts only: it reports no names or ids."
            : "Live counts from the Worker's /stats.");
        if (_stats is not { } st)
        {
            if (_problem != StatsProblem.None) section.Children.Add(Notice(_problem switch
            {
                StatsProblem.NoToken => $"There's no stats token at {Live.TokenFile}.",
                StatsProblem.NotDeployed => "The Worker doesn't have /stats yet: deploy coop-rendezvous (with its STATS_TOKEN secret) to turn on live numbers.",
                StatsProblem.Refused => "The Worker refused the token: the one on this PC doesn't match the Worker's STATS_TOKEN secret.",
                _ => "Couldn't reach the Worker.",
            }));
            return;
        }
        var tiles = Columns(6, 140);
        tiles.Children.Add(Tile("Connected apps", st.Connected, $"{st.Hidden} appearing offline"));
        tiles.Children.Add(Tile("In a game", st.InGame, "any game"));
        tiles.Children.Add(Tile("Open lobbies", st.Lobbies.Values.Sum(l => l.Lobbies), "hosted now"));
        tiles.Children.Add(Tile("Players in lobbies", st.Lobbies.Values.Sum(l => l.Players), "hosts included"));
        tiles.Children.Add(Tile("Members", st.Members, $"{st.SeenWeek} seen this week"));
        tiles.Children.Add(Tile("Friendships", st.Friendships, $"{st.PendingRequests} requests waiting"));
        section.Children.Add(tiles);

        // Status of connected apps as one bar.
        var card = Card();
        var stack = new StackPanel();
        stack.Children.Add(Text("Status of connected apps", Res("TextBrush"), 13, FontWeights.SemiBold));
        var parts = new (string Label, int N, string Color)[]
        {
            ("Online", st.Online, "#3BA55D"), ("Away", st.Away, "#E8A33A"), ("In game", st.InGame, "#4C9BF0"), ("Appearing offline", st.Hidden, "#5A6270"),
        };
        var total = Math.Max(1, parts.Sum(p => p.N));
        var bar = new Grid { Height = 12, Margin = new Thickness(0, 10, 0, 8) };
        foreach (var p in parts)
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(p.N, 0), GridUnitType.Star) });
        if (parts.All(p => p.N == 0)) bar.Children.Add(new Border { Background = Res("Panel2Brush"), CornerRadius = new CornerRadius(4) });
        for (var i = 0; i < parts.Length; i++)
        {
            var b = new Border { Background = Brush(parts[i].Color) };
            Grid.SetColumn(b, i);
            bar.Children.Add(b);
        }
        stack.Children.Add(new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = bar });
        var legend = new WrapPanel();
        foreach (var p in parts)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
            item.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = Brush(p.Color), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(Text($"{p.Label} {p.N:N0}", Res("MutedBrush"), 12));
            legend.Children.Add(item);
        }
        stack.Children.Add(legend);
        card.Child = stack;
        card.Margin = new Thickness(0, 12, 0, 0);
        section.Children.Add(card);
    }

    void DrawProfiles()
    {
        var section = Section("Game profiles", "Each game sits on the platform as a profile: which connectivity type it uses, how the game is told to host and join, and its lobby rules.");
        var grid = Columns(2, 420);
        foreach (var p in PlatformData.Profiles)
        {
            var card = Card();
            var stack = new StackPanel();
            var type = PlatformData.Connectivity.FirstOrDefault(t => t.id == p.connectivity);
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            if (type is not null) head.Children.Add(Docked(Pill(type.name, type.status == "Live"), System.Windows.Controls.Dock.Right));
            head.Children.Add(Text(p.game, Res("TextBrush"), 16, FontWeights.SemiBold));
            stack.Children.Add(head);

            if (_stats is { } st)
            {
                var live = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
                st.Lobbies.TryGetValue(p.game_id, out var l);
                live.Children.Add(Mini(st.InGameByGame.GetValueOrDefault(p.game_id), "in game now"));
                if (p.connectivity != "presence-only")
                {
                    live.Children.Add(Mini(l.Lobbies, "open lobbies"));
                    live.Children.Add(Mini(l.Players, "in lobbies"));
                }
                stack.Children.Add(live);
            }

            stack.Children.Add(Pairs(
                ("Party size", p.party_size?.ToString()),
                ("Hosting", p.host_setup),
                ("Joining", p.join_setup),
                ("Lobby", p.lobby),
                ("In game when", p.in_game_detection)));
            stack.Children.Add(Text(p.profile_state, Res("MutedBrush"), 12, margin: new Thickness(0, 10, 0, 0)));
            card.Child = stack;
            grid.Children.Add(card);
        }
        section.Children.Add(grid);
    }

    void DrawConnectivity()
    {
        var section = Section("Connectivity types", "The ways the platform can connect players. A profile picks one; adding a type makes it available to every game.");
        var grid = Columns(2, 420);
        foreach (var t in PlatformData.Connectivity)
        {
            var card = Card();
            var stack = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            head.Children.Add(Docked(Pill(t.status, t.status == "Live"), System.Windows.Controls.Dock.Right));
            head.Children.Add(Text(t.name, Res("TextBrush"), 15, FontWeights.SemiBold));
            stack.Children.Add(head);
            var users = PlatformData.Profiles.Where(p => p.connectivity == t.id).Select(p => p.game).ToList();
            stack.Children.Add(Pairs(("How", t.how), ("Needs", t.needs), ("Latency", t.latency),
                ("Used by", users.Count == 0 ? "No game yet" : string.Join(", ", users))));
            card.Child = stack;
            grid.Children.Add(card);
        }
        section.Children.Add(grid);
    }

    void DrawInfrastructure()
    {
        var section = Section("Infrastructure", "What runs where, from the app on each PC down to the game's own connection.");
        foreach (var layer in PlatformData.Components.Select(c => c.layer).Distinct())
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            row.Children.Add(Docked(Text(layer, Res("TextBrush"), 13, FontWeights.SemiBold, new Thickness(0, 10, 0, 0), width: 110), System.Windows.Controls.Dock.Left));
            var items = Columns(3, 240);
            foreach (var c in PlatformData.Components.Where(c => c.layer == layer))
            {
                var status = c.component == "Dashboard stats" && _stats is not null ? "Live" : c.status;
                var card = Card();
                var stack = new StackPanel();
                var head = new DockPanel();
                head.Children.Add(Docked(Pill(status, status == "Live"), System.Windows.Controls.Dock.Right));
                head.Children.Add(Text(c.component, Res("TextBrush"), 13, FontWeights.SemiBold));
                stack.Children.Add(head);
                stack.Children.Add(Text(c.runs_on, Res("MutedBrush"), 12, margin: new Thickness(0, 4, 0, 2)));
                stack.Children.Add(Text(c.detail, Res("TextBrush"), 12));
                card.Child = stack;
                items.Children.Add(card);
            }
            row.Children.Add(items);
            section.Children.Add(row);
        }
    }

    void DrawSecurity()
    {
        var section = Section("Security", "What protects members, lobbies and connections, and where each rule is enforced.");
        var card = Card();
        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        var rows = new List<(string, string, string, bool)> { ("Area", "Control", "Enforced by", true) };
        rows.AddRange(PlatformData.Security.Select(s => (s.area, s.control, s.enforced_by, false)));
        for (var i = 0; i < rows.Count; i++)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var (a, c, e, header) = rows[i];
            var cells = new[] { a, c, e };
            for (var col = 0; col < 3; col++)
            {
                var cell = new Border
                {
                    BorderBrush = Res("LineBrush"),
                    BorderThickness = new Thickness(0, 0, 0, i == rows.Count - 1 ? 0 : 1),
                    Padding = new Thickness(0, 8, 12, 8),
                    Child = Text(cells[col], header ? Res("MutedBrush") : Res("TextBrush"), header ? 12 : 13,
                        header || col == 0 ? FontWeights.SemiBold : FontWeights.Normal),
                };
                Grid.SetRow(cell, i);
                Grid.SetColumn(cell, col);
                table.Children.Add(cell);
            }
        }
        card.Child = table;
        section.Children.Add(card);
    }

    // ---- Building blocks ----

    StackPanel Section(string title, string note)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 30) };
        section.Children.Add(Text(title, Res("TextBrush"), 18, FontWeights.SemiBold));
        section.Children.Add(Text(note, Res("MutedBrush"), 12, margin: new Thickness(0, 2, 0, 12)));
        Root.Children.Add(section);
        return section;
    }

    Border Card() => new()
    {
        Background = Res("PanelBrush"),
        BorderBrush = Res("LineBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16, 14, 16, 14),
        Margin = new Thickness(0, 0, 12, 12),
    };

    /// <summary>A grid of equal columns that drops to fewer as the window narrows (each at least <paramref name="minWidth"/>).</summary>
    static UniformGrid Columns(int max, double minWidth)
    {
        var grid = new UniformGrid { Columns = max };
        grid.SizeChanged += (_, e) =>
        {
            var fit = Math.Clamp((int)(e.NewSize.Width / minWidth), 1, max);
            if (grid.Columns != fit) grid.Columns = fit;
        };
        return grid;
    }

    UIElement Tile(string label, int value, string sub)
    {
        var card = Card();
        var stack = new StackPanel();
        stack.Children.Add(Text(label, Res("MutedBrush"), 12));
        stack.Children.Add(Text(value.ToString("N0"), Res("TextBrush"), 30, FontWeights.Light));
        stack.Children.Add(Text(sub, Res("MutedBrush"), 11));
        card.Child = stack;
        return card;
    }

    UIElement Mini(int value, string label)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 22, 0) };
        stack.Children.Add(Text(value.ToString("N0"), Res("TextBrush"), 20, FontWeights.Light));
        stack.Children.Add(Text(label, Res("MutedBrush"), 11));
        return stack;
    }

    UIElement Pill(string text, bool live) => new Border
    {
        BorderBrush = Res("LineBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(8, 1, 8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 0, 0),
        Child = Text(text, live ? Res("OkBrush") : Res("WarnBrush"), 11),
    };

    UIElement Notice(string text) => new Border
    {
        Background = Res("PanelBrush"),
        BorderBrush = Res("WarnBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14, 10, 14, 10),
        Child = Text(text, Res("TextBrush"), 13),
    };

    /// <summary>Label / value lines; empty values are left out.</summary>
    UIElement Pairs(params (string Label, string? Value)[] pairs)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        foreach (var (label, value) in pairs.Where(p => !string.IsNullOrEmpty(p.Value)))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Text(label, Res("MutedBrush"), 12, margin: new Thickness(0, 2, 8, 4));
            var v = Text(value!, Res("TextBrush"), 12, margin: new Thickness(0, 2, 0, 4));
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
            row++;
        }
        return grid;
    }

    static TextBlock Text(string text, Brush brush, double size, FontWeight? weight = null, Thickness? margin = null, double? width = null)
    {
        var t = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(0),
        };
        if (width is { } w) t.Width = w;
        return t;
    }

    static UIElement Docked(UIElement element, System.Windows.Controls.Dock side)
    {
        DockPanel.SetDock(element, side);
        return element;
    }

    static readonly Dictionary<string, Brush> Brushes = new();

    static Brush Brush(string hex)
    {
        if (Brushes.TryGetValue(hex, out var b)) return b;
        var made = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        made.Freeze();
        return Brushes[hex] = made;
    }
}
