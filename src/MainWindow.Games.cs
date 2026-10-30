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
        if (_settings.Games.Count > 0 && !_settings.Games.Contains(_game.Id)) _game = GameDefinition.Find(_settings.Games[0])!;

        _rail.Clear();
        foreach (var game in _settings.Games.Select(id => GameDefinition.Find(id)!))
            _rail.Add(new RailGame(game) { Icon = RailIcon(game) });
        GameRail.ItemsSource = _rail;
        UpdateRailSelection();
        NoGamesPanel.Visibility = NoGames ? Visibility.Visible : Visibility.Collapsed;
        UpdateCoopPresence(); // online for co-op invites only while Azeroth Survivors is in the side bar
    }

    /// <summary>
    /// Nothing in the side bar yet (a new install, or every game removed): the welcome panel shows instead of the
    /// list, and <see cref="_game"/> is only a placeholder.
    /// </summary>
    bool NoGames => _settings.Games.Count == 0;

    void UpdateRailSelection()
    {
        foreach (var item in _rail)
            item.IsSelected = item.Game == _game;
    }

    /// <summary>After a game's folder or version changes: its side bar icon follows (see <see cref="RailIcon"/>).</summary>
    void UpdateRailIcon(GameDefinition game)
    {
        if (_rail.FirstOrDefault(r => r.Game == game) is { } item && RailIcon(game) is { } icon)
            item.Icon = icon;
    }

    /// <summary>
    /// A game's side bar icon: for a game with versions, the icon of the version last picked for it (WoW: Forever's,
    /// Classic's …, as in the version picker); otherwise, or if that version has none, the game's own icon.
    /// </summary>
    ImageSource? RailIcon(GameDefinition game)
    {
        var root = RootOf(game);
        if (game.HasVersions && root is not null
            && (_settings.StateFor(game.Id).Flavor ?? game.DefaultFlavor) is { } folder
            && game.GetFlavors(root).FirstOrDefault(f => f.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)) is { } flavor
            && game.FlavorIcon(root, flavor) is { } icon)
            return icon;
        return game.LoadIcon(root);
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

    /// <param name="force">Load it even if it's already the current game (adding the first game).</param>
    void SwitchGame(GameDefinition game, bool force = false)
    {
        if ((game == _game && !force) || _working) return;
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
        if ((sender as FrameworkElement)?.DataContext is not RailGame item || _working) return;
        _settings.Games.Remove(item.Game.Id);
        Storage.Save(SettingsFile, _settings);
        if (NoGames)
        {
            if (_detailsFor is not null) ShowList();
            ClearSelection();
            LoadRail();
            LoadFlavors(); // empties the list
            return;
        }
        if (item.Game == _game) SwitchGame(GameDefinition.Find(_settings.Games[0])!);
        LoadRail();
    }

    /// <summary>
    /// + opens the "Add a game" window: every game the app supports, with its addons in the org and where it's
    /// installed. Games already in the side bar show as added; any other game can be added, addons or not.
    /// </summary>
    void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var cards = GameDefinition.All.Select(game =>
        {
            var root = RootOf(game);
            return new GameCard(game, game.LoadIcon(root), _remote.Count(r => r.Game == game.Id),
                _settings.Games.Contains(game.Id), root);
        });
        var window = new AddGameWindow(cards) { Owner = this };
        if (window.ShowDialog() == true && window is { Chosen: { } chosen, ChosenRoot: { } root }) AddGame(chosen, root);
    }

    void AddGame(GameDefinition game, string root)
    {
        if (_settings.Games.Contains(game.Id)) return;
        _settings.Games.Add(game.Id);
        _settings.StateFor(game.Id).Root = root;
        Storage.Save(SettingsFile, _settings);
        LoadRail();
        SwitchGame(game, force: true);
    }
}
