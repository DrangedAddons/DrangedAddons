using System.Windows;

namespace DrangedAddons;

// Direct co-op (Azeroth Survivors): the co-op window, and staying online so friends can invite you.
public partial class MainWindow
{
    CoopWindow? _coopWindow;
    CoopPresence? _coopPresence;
    readonly List<(CoopPlayer From, string Code, DateTime At)> _coopInvites = new();

    static readonly GameDefinition CoopGame = GameDefinition.All.First(g => g.HasDirectCoop);

    /// <summary>
    /// Online while Azeroth Survivors is in the side bar and "Show me as online" is on, under the hero last played.
    /// Called on start and whenever one of those changes.
    /// </summary>
    void UpdateCoopPresence()
    {
        var root = _settings.CoopOnline && _settings.Games.Contains(CoopGame.Id) ? RootOf(CoopGame) : null;
        var name = root is null ? null : AzerothSurvivorsGame.LastHero(root) ?? Environment.UserName;
        if (_coopPresence?.Name == name) return;
        _coopPresence?.Dispose();
        _coopPresence = null;
        if (name is not null)
        {
            _coopPresence = new CoopPresence(CoopKey, name);
            _coopPresence.Invited += (from, code) => Dispatcher.BeginInvoke(() => OnCoopInvite(from, code));
        }
        _coopWindow?.UsePresence(_coopPresence);
    }

    /// <summary>This install's secret key on the co-op service; made on first use.</summary>
    string CoopKey
    {
        get
        {
            if (_settings.CoopId is null)
            {
                _settings.CoopId = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
                Storage.Save(SettingsFile, _settings);
            }
            return _settings.CoopId;
        }
    }

    void CoopOnline_Click(object sender, RoutedEventArgs e) => SetCoopOnline(CoopOnlineMenu.IsChecked);

    /// <summary>From the settings menu or the co-op window: both show the same setting.</summary>
    void SetCoopOnline(bool online)
    {
        _settings.CoopOnline = online;
        Storage.Save(SettingsFile, _settings);
        CoopOnlineMenu.IsChecked = online;
        _coopWindow?.SetOnlineCheck(online);
        UpdateCoopPresence();
    }

    /// <summary>Doesn't open anything over the game: a tray notification, or a line in the co-op window if it's open.</summary>
    void OnCoopInvite(CoopPlayer from, string code)
    {
        _coopInvites.RemoveAll(i => i.From.Id == from.Id || DateTime.UtcNow - i.At > TimeSpan.FromMinutes(15));
        _coopInvites.Add((from, code, DateTime.UtcNow));
        if (_coopWindow is not null) _coopWindow.ShowInvite(from, code);
        else _tray?.Notify($"{from.Name} invites you to co-op", "Click here to join their Azeroth Survivors party.", () => OpenCoopWindow());
    }

    void DirectCoop_Click(object sender, RoutedEventArgs e) => OpenCoopWindow();

    /// <summary>One co-op window at a time; it stays open (not modal) while the game runs, since it carries the connection.</summary>
    void OpenCoopWindow()
    {
        if (_coopWindow is not null)
        {
            if (_coopWindow.WindowState == WindowState.Minimized) _coopWindow.WindowState = WindowState.Normal;
            _coopWindow.Show();
            _coopWindow.Activate();
            return;
        }
        var root = RootOf(CoopGame);
        if (root is null || CoopGame.GetLaunch(root, CoopGame.GetFlavors(root)[0]) is not { } launch)
        {
            DarkDialog.Show(this, $"Couldn't start {CoopGame.Name}", $"Pick your {CoopGame.Name} folder first.");
            return;
        }
        var name = _coopPresence?.Name ?? AzerothSurvivorsGame.LastHero(root) ?? Environment.UserName;
        _coopWindow = new CoopWindow(launch.Exe, name, CoopKey, _settings.CoopOnline, SetCoopOnline);
        if (IsVisible) _coopWindow.Owner = this;
        _coopWindow.UsePresence(_coopPresence);
        _coopInvites.RemoveAll(i => DateTime.UtcNow - i.At > TimeSpan.FromMinutes(15));
        foreach (var (from, code, _) in _coopInvites) _coopWindow.ShowInvite(from, code);
        _coopWindow.Answered += from => _coopInvites.RemoveAll(i => i.From.Id == from.Id);
        _coopWindow.Closed += (_, _) => _coopWindow = null;
        _coopWindow.Show();
    }
}
