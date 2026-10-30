using System.Windows;

namespace DrangedAddons;

// Direct co-op (Azeroth Survivors): the co-op window, and staying online so friends can invite you.
public partial class MainWindow
{
    CoopWindow? _coopWindow;
    CoopPresence? _coopPresence;
    bool _coopHosting;
    readonly List<(CoopPlayer From, string Code, DateTime At)> _coopInvites = new();

    static readonly GameDefinition CoopGame = GameDefinition.All.First(g => g.HasDirectCoop);

    /// <summary>
    /// Connected to the co-op service while the app runs, whatever the games, under the account's name and status
    /// (appearing offline stays connected, so friends and members still show, but nobody sees you). Called on start
    /// and every 15 seconds; a status change alone is sent without reconnecting, a new name reconnects.
    /// </summary>
    void UpdateCoopPresence()
    {
        var name = AccountName;
        var game = AccountInfo.Status == AccountStatus.AppearOffline ? null : RunningGame();
        var state = Account.Effective(AccountInfo.Status, game);
        if (_coopPresence?.Name == name)
        {
            _ = _coopPresence.SetStateAsync(state, game);
            UpdateAccountChip();
            return;
        }
        _coopPresence?.Dispose();
        _coopPresence = new CoopPresence(CoopKey, name, state, game);
        _coopPresence.Invited += (from, code) => Dispatcher.BeginInvoke(() => OnCoopInvite(from, code));
        _coopPresence.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            UpdateAccountChip();
            _friendsWindow?.Refresh();
            _ = SyncPictureAsync();
        });
        _coopWindow?.UsePresence(_coopPresence);
        _friendsWindow?.UsePresence(_coopPresence);
        UpdateAccountChip();
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

    /// <summary>Doesn't open anything over the game: a tray notification, or a line in the co-op window if it's open.</summary>
    void OnCoopInvite(CoopPlayer from, string code)
    {
        _coopInvites.RemoveAll(i => i.From.Id == from.Id || DateTime.UtcNow - i.At > TimeSpan.FromMinutes(15));
        _coopInvites.Add((from, code, DateTime.UtcNow));
        if (_coopWindow is not null) _coopWindow.ShowInvite(from, code);
        else _tray?.Notify($"{from.Name} invites you to co-op", "Click here to join their Azeroth Survivors party.", () => OpenCoopWindow());
    }

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
        _coopWindow = new CoopWindow(launch.Exe, AccountName, CoopKey, hosting =>
        {
            _coopHosting = hosting;
            UpdateCoopPresence();
        }, PlayGame);
        if (IsVisible) _coopWindow.Owner = this;
        _coopWindow.UsePresence(_coopPresence);
        _coopInvites.RemoveAll(i => DateTime.UtcNow - i.At > TimeSpan.FromMinutes(15));
        foreach (var (from, code, _) in _coopInvites) _coopWindow.ShowInvite(from, code);
        _coopWindow.Answered += from => _coopInvites.RemoveAll(i => i.From.Id == from.Id);
        _coopWindow.Closed += (_, _) =>
        {
            _coopWindow = null;
            if (!_coopHosting) return;
            _coopHosting = false;
            UpdateCoopPresence();
        };
        _coopWindow.Show();
    }
}
