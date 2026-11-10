namespace DrangedAddons;

/// <summary>The status a member picks. In game and idle-away are worked out on top of Online (Account.Effective in the app).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AccountStatus>))]
public enum AccountStatus { Online, Away, AppearOffline }

/// <summary>
/// The member's local account: a display name, a picture and a status. Nothing is registered anywhere; the install's
/// secret co-op key (settings CoopId) is the identity, and others only see a hash of it.
/// </summary>
public sealed class AccountSettings
{
    /// <summary>Null: the hero last played in Azeroth Survivors, else the Windows user name.</summary>
    public string? Name { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Online;

    /// <summary>Hash of the picture in avatar.jpg (null: no picture), and of the one last uploaded to the co-op service.</summary>
    public string? Picture { get; set; }
    public string? PictureUploaded { get; set; }

    /// <summary>The first-run welcome (pick a name and picture) has been shown.</summary>
    public bool Welcomed { get; set; }
}
