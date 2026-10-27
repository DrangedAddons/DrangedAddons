using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DrangedAddons;

/// <summary>A game in the side bar.</summary>
public sealed class RailGame(GameDefinition game) : INotifyPropertyChanged
{
    public GameDefinition Game { get; } = game;
    public string Name => Game.Name;

    /// <summary>First letters of the name, as written ("WoW", "AS"), for when there's no icon.</summary>
    public string Initials => string.Concat(Game.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]));

    ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Changed(nameof(Icon), nameof(HasIcon), nameof(NoIcon));
        }
    }
    public bool HasIcon => _icon is not null;
    public bool NoIcon => _icon is null;

    bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            Changed(nameof(IsSelected));
        }
    }

    bool _canRemove;
    public bool CanRemove
    {
        get => _canRemove;
        set
        {
            _canRemove = value;
            Changed(nameof(CanRemove));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Changed(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>The game side bar: switching games, adding one (from those with addons in the org), removing one.</summary>
public partial class MainWindow
{
    readonly ObservableCollection<RailGame> _rail = [];

    void LoadRail()
    {
        _settings.Games = _settings.Games.Where(id => GameDefinition.Find(id) is not null).Distinct().ToList();
        if (_settings.Games.Count == 0) _settings.Games.Add(GameDefinition.Wow.Id);
        if (!_settings.Games.Contains(_game.Id)) _game = GameDefinition.Find(_settings.Games[0])!;

        _rail.Clear();
        foreach (var game in _settings.Games.Select(id => GameDefinition.Find(id)!))
            _rail.Add(new RailGame(game) { Icon = game.LoadIcon(RootOf(game)) });
        GameRail.ItemsSource = _rail;
        UpdateRailSelection();
    }

    void UpdateRailSelection()
    {
        foreach (var item in _rail)
        {
            item.IsSelected = item.Game == _game;
            item.CanRemove = _rail.Count > 1;
        }
    }

    /// <summary>After a game's folder is found or changed: its icon comes from the program in it.</summary>
    void UpdateRailIcon(GameDefinition game)
    {
        if (_rail.FirstOrDefault(r => r.Game == game) is { } item && game.LoadIcon(_root) is { } icon)
            item.Icon = icon;
    }

    /// <summary>A game's folder: the one saved for it if it's still there, else wherever it's found.</summary>
    string? RootOf(GameDefinition game) =>
        (_settings.StateFor(game.Id).Root is { } saved ? game.NormalizeRoot(saved) : null) ?? game.FindRoot();

    /// <summary>Every version of every game in the side bar that's installed (for auto-update).</summary>
    IEnumerable<(GameDefinition Game, GameFlavor Flavor)> AllFlavors() =>
        _settings.Games
            .Select(GameDefinition.Find)
            .OfType<GameDefinition>()
            .SelectMany(game => RootOf(game) is { } root
                ? game.GetFlavors(root).Select(flavor => (game, flavor))
                : []);

    void SwitchGame(GameDefinition game)
    {
        if (game == _game || _working) return;
        _game = game;
        _settings.Game = game.Id;
        Storage.Save(SettingsFile, _settings);
        if (_detailsFor is not null) ShowList();
        ClearSelection();
        UpdateRailSelection();
        LoadFlavors(); // rebuilds the list for this game
        UpdateSummary();
    }

    void RailGame_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RailGame item) SwitchGame(item.Game);
    }

    void RailRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RailGame item || _rail.Count <= 1 || _working) return;
        _settings.Games.Remove(item.Game.Id);
        Storage.Save(SettingsFile, _settings);
        if (item.Game == _game) SwitchGame(GameDefinition.Find(_settings.Games[0])!);
        LoadRail();
    }

    /// <summary>
    /// + lists the games the app knows that have at least one addon in the org and aren't in the side bar yet.
    /// </summary>
    void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var menu = AddGameButton.ContextMenu;
        menu.Items.Clear();
        var available = GameDefinition.All
            .Where(g => !_settings.Games.Contains(g.Id))
            .Select(g => (Game: g, Addons: _remote.Count(r => r.Game == g.Id)))
            .Where(x => x.Addons > 0)
            .ToList();
        foreach (var (game, addons) in available)
        {
            var item = new MenuItem { Header = $"{game.Name}  ({addons} addon{(addons == 1 ? "" : "s")})" };
            item.Click += (_, _) => AddGame(game);
            menu.Items.Add(item);
        }
        if (available.Count == 0)
            menu.Items.Add(new MenuItem
            {
                Header = _remote.Count == 0 ? "Checking GitHub for addons…" : "No other games have addons yet",
                IsEnabled = false,
            });
        menu.PlacementTarget = AddGameButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
        menu.IsOpen = true;
    }

    void AddGame(GameDefinition game)
    {
        if (_settings.Games.Contains(game.Id)) return;
        _settings.Games.Add(game.Id);
        Storage.Save(SettingsFile, _settings);
        LoadRail();
        SwitchGame(game);
        if (_root is null) PickGameFolder(); // not found in the usual places: ask where it is
    }
}
