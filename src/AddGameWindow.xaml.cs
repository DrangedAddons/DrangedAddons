using System.Windows;
using System.Windows.Media;

namespace DrangedAddons;

/// <summary>A game in the "Add a game" window.</summary>
public sealed class GameCard(GameDefinition game, ImageSource? icon, int addons, bool added, string? root)
{
    static readonly Brush HasAddonsBrush = Frozen(0x4C, 0xAF, 0x7D);
    static readonly Brush NoAddonsBrush = Frozen(0x8B, 0x93, 0xA1);

    public GameDefinition Game { get; } = game;
    public string Name => Game.Name;
    public string Description => Game.Description;
    public string Initials => string.Concat(Game.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]));
    public ImageSource? Icon { get; } = icon;
    public bool HasIcon => Icon is not null;
    public bool NoIcon => Icon is null;

    public string AddonsText => addons switch { 0 => "No addons yet", 1 => "1 addon", _ => $"{addons} addons" };
    public Brush AddonsBrush => addons > 0 ? HasAddonsBrush : NoAddonsBrush;

    public string LocationText => root is not null
        ? $"Found on this PC: {root}"
        : "Not found in the usual places: you'll be asked where it's installed.";

    /// <summary>Can be added: has addons to install and isn't in the side bar already.</summary>
    public bool CanAdd => addons > 0 && !added;
    public string ButtonText => added ? "Added" : "Add";

    /// <summary>Games with nothing to install yet are shown faded.</summary>
    public double CardOpacity => addons > 0 || added ? 1 : 0.6;

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Lists every game the app supports, with its addons in the org and where it's installed; Add picks one.</summary>
public partial class AddGameWindow : Window
{
    /// <summary>The game picked, once the window has closed with one.</summary>
    public GameDefinition? Chosen { get; private set; }

    public AddGameWindow(IEnumerable<GameCard> games)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        GameList.ItemsSource = games.ToList();
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameCard { CanAdd: true } card) return;
        Chosen = card.Game;
        DialogResult = true;
    }
}
