using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

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

    /// <summary>Where it was found on this PC, if it was.</summary>
    public string? Root { get; } = root;

    public string AddonsText => addons switch { 0 => "No addons yet", 1 => "1 addon", _ => $"{addons} addons" };
    public Brush AddonsBrush => addons > 0 ? HasAddonsBrush : NoAddonsBrush;

    public string LocationText => Root is not null
        ? $"Found on this PC: {Root}"
        : "Not found in the usual places: you'll pick where it's installed.";

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

/// <summary>
/// Step 1 lists every game the app supports (searchable), with its addons in the org and where it's installed.
/// Add goes to step 2: where the game is installed, filled in with the folder found (or the usual place), checked
/// as you type or browse.
/// </summary>
public partial class AddGameWindow : Window
{
    /// <summary>The game picked and its folder, once the window has closed with one.</summary>
    public GameDefinition? Chosen { get; private set; }
    public string? ChosenRoot { get; private set; }

    readonly List<GameCard> _games;
    GameCard? _card;

    public AddGameWindow(IEnumerable<GameCard> games)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _games = games.ToList();
        GameList.ItemsSource = _games;
        Loaded += (_, _) => SearchBox.Focus();
    }

    /// <summary>Shows the games whose name or description has every word typed.</summary>
    void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var words = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = _games
            .Where(g => words.All(w => $"{g.Name} {g.Description}".Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        GameList.ItemsSource = shown;
        NoMatchText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GameCard { CanAdd: true } card) return;
        _card = card;
        ChosenName.Text = card.Name;
        ChosenInitials.Text = card.Initials;
        ChosenIcon.Source = card.Icon;
        ChosenInitialsTile.Visibility = card.HasIcon ? Visibility.Collapsed : Visibility.Visible;
        FolderNote.Text = card.Root is not null
            ? "Found on this PC. Change it if you play from a different copy."
            : $"It wasn't found in the usual places, so this is where it's usually installed. {card.Game.FolderHint}";
        FolderBox.Text = card.Root ?? card.Game.TypicalRoot;
        PickPanel.Visibility = Visibility.Collapsed;
        FolderPanel.Visibility = Visibility.Visible;
        FolderBox.Focus();
        FolderBox.SelectAll();
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        FolderPanel.Visibility = Visibility.Collapsed;
        PickPanel.Visibility = Visibility.Visible;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_card is null) return;
        var dialog = new OpenFolderDialog { Title = $"Select your {_card.Name} folder" };
        if (Directory.Exists(FolderBox.Text)) dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    /// <summary>Checks the folder as it changes: Add game is only offered for a real install.</summary>
    void FolderBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_card is null) return;
        var root = FolderBox.Text.Trim().Length > 0 ? _card.Game.NormalizeRoot(FolderBox.Text) : null;
        ConfirmButton.IsEnabled = root is not null;
        FolderStatus.Text = root is not null
            ? $"✓ {_card.Name} is installed here" + (string.Equals(root, FolderBox.Text.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? "." : $": {root}")
            : $"✗ {_card.Name} isn't in this folder. {_card.Game.FolderHint}";
        FolderStatus.Foreground = (Brush)FindResource(root is not null ? "AccentBrush" : "DangerBrush");
    }

    void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_card is null || _card.Game.NormalizeRoot(FolderBox.Text) is not { } root) return;
        Chosen = _card.Game;
        ChosenRoot = root;
        DialogResult = true;
    }
}
