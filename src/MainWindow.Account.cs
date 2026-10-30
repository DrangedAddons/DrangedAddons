using System.Windows;
using System.Windows.Threading;

namespace DrangedAddons;

// The local account (name, picture, status) and its chip at the bottom of the side bar.
public partial class MainWindow
{
    readonly DispatcherTimer _accountTick = new() { Interval = TimeSpan.FromSeconds(15) };
    bool _syncingPicture;

    AccountSettings AccountInfo => _settings.Account ??= new AccountSettings();

    /// <summary>The name members see: the one picked, else <see cref="DefaultAccountName"/>.</summary>
    string AccountName => Clean(AccountInfo.Name) ?? DefaultAccountName;

    /// <summary>The hero last played in Azeroth Survivors, else the Windows user name.</summary>
    string DefaultAccountName =>
        (RootOf(CoopGame) is { } root ? Clean(AzerothSurvivorsGame.LastHero(root)) : null) ?? Clean(Environment.UserName) ?? "Player";

    static string? Clean(string? name) =>
        name?.Trim() is { Length: > 0 } trimmed ? trimmed[..Math.Min(trimmed.Length, Account.NameLength)] : null;

    /// <summary>What members see now: appear offline, in game, away (picked or idle), or online.</summary>
    string AccountState =>
        Account.Effective(AccountInfo.Status,
            RootOf(CoopGame) is { } root && CoopGame.GetLaunch(root, CoopGame.GetFlavors(root)[0]) is { } launch
            && Account.IsRunning(launch.Exe));

    /// <summary>In game and idle-away change on their own: checked every 15 seconds.</summary>
    void StartAccount()
    {
        _accountTick.Tick += (_, _) => UpdateCoopPresence();
        _accountTick.Start();
        Avatars.Loaded += () => _coopWindow?.RefreshLists();
        UpdateAccountChip();
    }

    void UpdateAccountChip()
    {
        var name = AccountName;
        var picture = AccountInfo.Picture is null ? null : Account.LoadOwnPicture();
        AccountPicture.ImageSource = picture;
        AccountPictureCircle.Visibility = picture is null ? Visibility.Collapsed : Visibility.Visible;
        AccountInitials.Text = Account.Initials(name);
        AccountInitials.Visibility = picture is null ? Visibility.Visible : Visibility.Collapsed;

        // Grey unless the co-op service actually lists us: not connected (yet) counts as offline.
        var shown = _coopPresence is { Connected: true } p ? p.State : "offline";
        AccountDot.Fill = Account.StateBrush(shown);
        var where = _coopPresence is null ? "Not connected (add Azeroth Survivors to go online)"
            : !_coopPresence.Connected ? "Connecting…"
            : AccountInfo.Status == AccountStatus.AppearOffline ? "Appearing offline"
            : Account.Label(shown);
        AccountButton.ToolTip = $"{name}\n{where}";
    }

    void Account_Click(object sender, RoutedEventArgs e)
    {
        var window = new AccountWindow(AccountInfo, DefaultAccountName, Account.LoadOwnPicture()) { Owner = this };
        if (window.ShowDialog() != true) return;

        AccountInfo.Name = Clean(window.ChosenName);
        AccountInfo.Status = window.ChosenStatus;
        if (window.PictureChanged)
        {
            try
            {
                if (window.ChosenPicture is { } bytes)
                {
                    Directory.CreateDirectory(Storage.Dir);
                    File.WriteAllBytes(Account.PictureFile, bytes);
                    AccountInfo.Picture = Account.Hash(bytes);
                }
                else
                {
                    File.Delete(Account.PictureFile);
                    AccountInfo.Picture = null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DarkDialog.Show(this, "Couldn't save your picture", ex.Message);
            }
        }
        Storage.Save(SettingsFile, _settings);
        UpdateCoopPresence(); // a new name reconnects; a new status is just sent
        _ = SyncPictureAsync();
    }

    /// <summary>Puts this member's picture on the co-op service (or removes it) when it differs from the one last sent.</summary>
    async Task SyncPictureAsync()
    {
        if (_syncingPicture || _coopPresence is not { Connected: true } || AccountInfo.Picture == AccountInfo.PictureUploaded) return;
        _syncingPicture = true;
        var sent = false;
        try
        {
            var wanted = AccountInfo.Picture;
            byte[]? bytes = wanted is null ? null : await File.ReadAllBytesAsync(Account.PictureFile);
            AccountInfo.PictureUploaded = await Avatars.UploadAsync(CoopKey, bytes);
            Storage.Save(SettingsFile, _settings);
            sent = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            // Tried again on the next reconnect or picture change.
        }
        finally
        {
            _syncingPicture = false;
        }
        // Picked another picture while that one was going up.
        if (sent && AccountInfo.Picture != AccountInfo.PictureUploaded) _ = SyncPictureAsync();
    }
}
