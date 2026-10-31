using System.Text.Json.Nodes;

namespace DrangedAddons;

// The lobby as the in-game overlay sees and drives it (OverlayHub). The overlay asks its own questions before
// anything that restarts the game or removes someone, so these don't open dialogs over the game.
public partial class CoopWindow
{
    /// <summary>The lobby chat as the overlay shows it: when, who (null for the lobby itself), and what.</summary>
    readonly List<(DateTime At, string? From, bool FromHost, string Text)> _overlayLog = [];

    public bool IsHosting => _host is not null;

    /// <summary>Member ids of everyone in this lobby (players, and members invited and not in yet).</summary>
    public HashSet<string> LobbyMembers
    {
        get
        {
            var party = _host?.Party ?? _join?.Party;
            var ids = party?.Members.Select(m => m.Member).OfType<string>().ToHashSet() ?? [];
            ids.UnionWith(_invited.Keys);
            return ids;
        }
    }

    public bool LobbyFull => (_host?.Party.Members.Count ?? Slots) + 1 + _invited.Count >= Slots;

    /// <summary>The code of the lobby this window is in, or null.</summary>
    public string? LobbyCode => _host?.Code ?? (_join is null ? null : _joinCode);

    void OverlayLine(string? from, bool fromHost, string text)
    {
        _overlayLog.Add((DateTime.Now, from, fromHost, text));
        if (_overlayLog.Count > 150) _overlayLog.RemoveAt(0);
        OverlayHub.Changed();
    }

    void ClearLog()
    {
        LogList.Children.Clear();
        _overlayLog.Clear();
    }

    /// <summary>
    /// The lobby for the overlay: role, code, host, started, access, slots (the host first, then players), members
    /// invited and not in yet, and the lobby chat. <paramref name="you"/> is this player's member id. Null when not
    /// in a lobby.
    /// </summary>
    public JsonObject? OverlayLobby(string you)
    {
        var party = _host?.Party ?? _join?.Party;
        if (party is null) return null;
        var slots = new JsonArray();
        // A joiner knows the host by name; the host's presence lists the lobby by its code.
        var hostMember = _host is not null ? you : _presence?.Players.FirstOrDefault(p => p.Lobby?.Code is { } code && code == _joinCode)?.Id;
        slots.Add(new JsonObject
        {
            ["id"] = "host", ["member"] = hostMember, ["name"] = party.Host, ["host"] = true, ["you"] = _host is not null,
            ["state"] = party.Started ? "In game" : "Ready", ["ok"] = true,
        });
        foreach (var m in party.Members)
        {
            var mine = _join is not null && m.Id == _join.Id;
            slots.Add(new JsonObject
            {
                ["id"] = m.Id, ["member"] = mine ? you : m.Member, ["name"] = m.Name, ["you"] = mine,
                ["state"] = m.State switch
                {
                    MemberState.Connected => party.Started ? "In game" : "Ready",
                    MemberState.Failed => "Couldn't connect",
                    _ => "Connecting…",
                },
                ["ping"] = m.State == MemberState.Connected ? m.Ping : null,
                ["ok"] = m.State == MemberState.Connected, ["failed"] = m.State == MemberState.Failed,
            });
        }
        var invited = new JsonArray();
        foreach (var p in _invited.Values.Where(i => !party.Members.Any(m => m.Member == i.Id)))
            invited.Add(new JsonObject { ["member"] = p.Id, ["name"] = p.Name });
        var log = new JsonArray();
        foreach (var (at, from, fromHost, text) in _overlayLog.TakeLast(100))
            log.Add(new JsonObject { ["time"] = at.ToString("HH:mm"), ["from"] = from, ["host"] = fromHost, ["text"] = text });
        return new JsonObject
        {
            ["role"] = _host is not null ? "host" : "join", ["code"] = LobbyCode, ["host"] = party.Host, ["started"] = party.Started,
            ["locked"] = _host?.Locked ?? false, ["password"] = _host?.HasPassword ?? false, ["size"] = Slots,
            // The host's own password (it never leaves this PC: the overlay is in a game on it), so the overlay can show
            // and edit it as this window does; and the line under the settings.
            ["passwordText"] = _host is not null ? HostPasswordBox.Password : null, ["access"] = AccessLine,
            ["slots"] = slots, ["invited"] = invited, ["log"] = log,
        };
    }

    /// <summary>The member ids in the lobby with their names, for pictures.</summary>
    public IEnumerable<string> OverlayMembers(string you)
    {
        if (OverlayLobby(you)?["slots"] is JsonArray slots)
            foreach (var s in slots)
                if ((string?)s?["member"] is { } id) yield return id;
        foreach (var id in _invited.Keys) yield return id;
    }

    public Task HostFromOverlayAsync() => _host is null && _join is null && !_busy ? HostAsync() : Task.CompletedTask;

    public Task<bool> JoinFromOverlayAsync(string code) =>
        _host is null && _join is null && !_busy && CoopTunnel.NormalizeCode(code) is { } c ? JoinAsync(c) : Task.FromResult(false);

    public Task InviteFromOverlayAsync(CoopPlayer player) => _host is not null && !LobbyFull ? InviteAsync(player) : Task.CompletedTask;

    public Task KickFromOverlayAsync(string partyId) =>
        _host?.Party.Members.FirstOrDefault(m => m.Id == partyId) is { } member ? KickAsync(member, ask: false) : Task.CompletedTask;

    public Task StartFromOverlayAsync() => StartGameAsync(ask: false);

    /// <summary>Close lobby (host) or Leave lobby (joiner): co-op ends and the game restarts in solo.</summary>
    public async Task EndFromOverlayAsync()
    {
        if (_host is null && _join is null) return;
        await EndAsync();
        await RestartSoloAsync();
        StatusList.Children.Clear();
        Show(Screen.Start);
    }

    /// <summary>The in-game new-lobby window's Create lobby: the same as this window's, with what it chose.</summary>
    public Task CreateLobbyFromOverlayAsync(bool locked, string password, IEnumerable<string> bring, IEnumerable<CoopPlayer> invite)
    {
        if (_busy) return Task.CompletedTask;
        _bring.Clear();
        _bring.UnionWith(bring);
        _stageInvites.Clear();
        foreach (var player in invite) _stageInvites[player.Id] = player;
        StageLockedCheck.IsChecked = locked;
        StagePasswordBox.Password = password;
        return CreateLobbyAsync(ask: false);
    }

    /// <summary>Who can join, from the overlay: the same as changing them here (and shown here too).</summary>
    public Task SettingsFromOverlayAsync(bool locked, string password)
    {
        if (_host is null) return Task.CompletedTask;
        LockedCheck.IsChecked = locked;
        HostPasswordBox.Password = password;
        _passwordDelay.Stop(); // applied now, not after the typing pause
        PasswordHint.Visibility = Visible(password.Length == 0);
        return ApplySettingsAsync();
    }

    public Task ChatFromOverlayAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return Task.CompletedTask;
        return _host is not null ? _host.SendChatAsync(text) : _join?.SendChatAsync(text) ?? Task.CompletedTask;
    }
}
