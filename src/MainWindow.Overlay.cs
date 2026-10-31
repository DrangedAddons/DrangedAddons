using System.Text.Json.Nodes;
using System.Windows.Media;

namespace DrangedAddons;

// What the in-game overlay shows and what it can do (docs/overlay.md): friends, the lobby and its chat, and the game's
// addons. OverlayHub carries it to every running overlay.
public partial class MainWindow
{
    /// <summary>Friends online and friend requests last time, to notify overlays of what's new.</summary>
    HashSet<string>? _overlayOnline, _overlayRequests;

    void StartOverlay()
    {
        OverlayHub.Provide = OverlaySnapshot;
        OverlayHub.Handle = OverlayAction;
        Avatars.Loaded += OverlayHub.Changed;
    }

    /// <summary>This player's member id: what the co-op service shows others (a hash of the install key).</summary>
    string OwnMemberId => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes("drangedaddons-coop:" + CoopKey))[..8]);

    OverlayHub.Snapshot OverlaySnapshot(string gameId)
    {
        var me = OwnMemberId;
        var pictures = new List<(string, string, ImageSource)>();
        var known = new Dictionary<string, CoopPlayer>();
        void Picture(string? id, string? hash)
        {
            if (id is null || hash is null || pictures.Any(p => p.Item1 == id)) return;
            var image = id == me ? Account.LoadOwnPicture() : Avatars.Get(id, hash); // null while it's fetched; Loaded brings it
            if (image is not null) pictures.Add((id, hash, image));
        }

        var you = new JsonObject { ["id"] = me, ["name"] = AccountName, ["avatar"] = AccountInfo.Picture };
        Picture(me, AccountInfo.Picture);

        JsonObject? friends = null;
        if (_coopPresence is { } presence)
        {
            foreach (var p in presence.Players) known[p.Id] = p;
            var lobbyMembers = _coopWindow?.LobbyMembers ?? [];
            var myLobby = _coopWindow?.LobbyCode;
            var hosting = _coopWindow?.IsHosting == true && !_coopWindow.LobbyFull;
            var list = new JsonArray();
            // Friends as the co-op service lists them, with what presence knows now (state, lobby, picture).
            foreach (var f in presence.Friends.Friends.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var live = known.GetValueOrDefault(f.Id);
                var state = live?.State ?? "offline";
                var lobby = live?.Lobby is { } l && (l.Game ?? CoopGame.Id) == gameId
                    ? new JsonObject
                    {
                        ["code"] = l.Code, ["count"] = l.Count, ["size"] = 5, ["locked"] = l.Locked, ["password"] = l.Password,
                        ["joinable"] = l.Code is not null && !l.Locked && l.Count < 5 && l.Code != myLobby && _coopWindow?.IsHosting != true,
                    }
                    : null;
                list.Add(new JsonObject
                {
                    ["id"] = f.Id, ["name"] = live?.Name ?? f.Name, ["state"] = state, ["label"] = Account.Label(state, live?.Game),
                    ["lobby"] = lobby, ["invitable"] = hosting && state != "offline" && !lobbyMembers.Contains(f.Id),
                });
                Picture(f.Id, live?.Avatar ?? f.Avatar);
                known[f.Id] = live ?? f;
            }
            var incoming = new JsonArray();
            foreach (var p in presence.Friends.Incoming)
            {
                incoming.Add(new JsonObject { ["id"] = p.Id, ["name"] = p.Name });
                Picture(p.Id, p.Avatar);
            }
            var invites = new JsonArray();
            foreach (var (from, code, at) in _coopInvites.Where(i => DateTime.UtcNow - i.At < TimeSpan.FromMinutes(15)))
            {
                invites.Add(new JsonObject { ["from"] = new JsonObject { ["id"] = from.Id, ["name"] = from.Name }, ["code"] = code });
                Picture(from.Id, from.Avatar);
            }
            friends = new JsonObject { ["friends"] = list, ["incoming"] = incoming, ["invites"] = invites };
        }

        JsonObject? lobbyState = null;
        if (gameId == CoopGame.Id && _coopWindow is { } coop)
        {
            lobbyState = coop.OverlayLobby(me);
            foreach (var id in coop.OverlayMembers(me))
                if (known.GetValueOrDefault(id)?.Avatar is { } hash) Picture(id, hash);
        }

        return new OverlayHub.Snapshot(you, friends, lobbyState, OverlayAddons(gameId), pictures);
    }

    /// <summary>The game's installed addons: from the list on screen when it's that game's (with updates), else from what was installed.</summary>
    JsonObject OverlayAddons(string gameId)
    {
        var addons = new JsonArray();
        if (_game.Id == gameId)
        {
            foreach (var row in _rows.Where(r => r.State != AddonState.NotInstalled).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                addons.Add(new JsonObject { ["name"] = row.Name, ["version"] = row.InstalledVersion, ["update"] = row.State == AddonState.UpdateAvailable });
        }
        else if (GameDefinition.Find(gameId) is { } game && RootOf(game) is { } root && game.GetFlavors(root) is { Count: > 0 } flavors)
        {
            foreach (var (name, record) in _manifest.For(flavors[0].AddOnsPath).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                addons.Add(new JsonObject { ["name"] = name, ["version"] = record.Version, ["update"] = false });
        }
        return new JsonObject { ["addons"] = addons };
    }

    /// <summary>What an overlay asked for. The overlay has already asked the player about anything that restarts the game.</summary>
    async void OverlayAction(string gameId, JsonObject action)
    {
        var id = (string?)action["id"];
        var code = (string?)action["code"];
        var presence = _coopPresence;
        try
        {
            switch ((string?)action["a"])
            {
                case "acceptFriend" when presence is not null && id is not null:
                    await presence.AddFriendAsync(id);
                    break;
                case "declineFriend" when presence is not null && id is not null:
                    await presence.RemoveFriendAsync(id);
                    break;
                case "invite" when presence?.Players.FirstOrDefault(p => p.Id == id) is { } player && _coopWindow is { } coop:
                    await coop.InviteFromOverlayAsync(player);
                    break;
                case "acceptInvite" when id is not null && code is not null:
                    _coopInvites.RemoveAll(i => i.From.Id == id);
                    await CoopForOverlay()?.JoinFromOverlayAsync(code)!;
                    break;
                case "declineInvite" when id is not null:
                    if (_coopInvites.FirstOrDefault(i => i.From.Id == id) is { From: { } from } && presence is not null) await presence.DeclineAsync(from);
                    _coopInvites.RemoveAll(i => i.From.Id == id);
                    break;
                case "join" when code is not null:
                    await CoopForOverlay()?.JoinFromOverlayAsync(code)!;
                    break;
                case "host":
                    await CoopForOverlay()?.HostFromOverlayAsync()!;
                    break;
                case "kick" when id is not null && _coopWindow is { } coop:
                    await coop.KickFromOverlayAsync(id);
                    break;
                case "start" when _coopWindow is { } coop:
                    await coop.StartFromOverlayAsync();
                    break;
                case "leave" or "close" when _coopWindow is { } coop:
                    await coop.EndFromOverlayAsync();
                    break;
                case "chat" when _coopWindow is { } coop && (string?)action["text"] is { } text:
                    await coop.ChatFromOverlayAsync(text);
                    break;
            }
        }
        catch (Exception ex) when (ex is CoopException or System.Net.WebSockets.WebSocketException or IOException)
        {
            OverlayHub.Toast("Couldn't do that", ex.Message);
        }
        OverlayHub.Changed();
    }

    /// <summary>The co-op window for an overlay's request: opened if it isn't, without taking focus from the game.</summary>
    CoopWindow? CoopForOverlay()
    {
        if (_coopWindow is null) OpenCoopWindow(activate: false);
        return _coopWindow;
    }

    /// <summary>After presence changes: friends who came online and new friend requests, as toasts in running overlays.</summary>
    void NotifyOverlays()
    {
        if (_coopPresence is not { } presence) return;
        var online = presence.Friends.Friends.Where(f => presence.Players.Any(p => p.Id == f.Id)).ToDictionary(f => f.Id, f => f.Name);
        var requests = presence.Friends.Incoming.ToDictionary(p => p.Id, p => p.Name);
        if (_overlayOnline is not null)
            foreach (var (id, name) in online.Where(o => !_overlayOnline.Contains(o.Key)))
                OverlayHub.Toast($"{name} is online", "");
        if (_overlayRequests is not null)
            foreach (var (id, name) in requests.Where(r => !_overlayRequests.Contains(r.Key)))
                OverlayHub.Toast($"{name} wants to be friends", "Answer in Friends.");
        _overlayOnline = [.. online.Keys];
        _overlayRequests = [.. requests.Keys];
        OverlayHub.Changed();
    }
}
