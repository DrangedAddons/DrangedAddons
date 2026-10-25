namespace DrangedAddons;

/// <summary>A commit on the default branch that changed the addon's .toc.</summary>
public sealed record TocCommit(string Sha, string? Parent, DateTimeOffset Date);

/// <summary>
/// An addon's version history as far as it has been read. Releases are all known up front; beta versions come
/// from the .toc at each commit that changed it, read newest first in batches
/// (<see cref="GitHubClient.ReadMoreVersionsAsync"/>), so a long history can be shown a page at a time.
/// </summary>
public sealed class VersionHistory(
    RemoteAddon addon,
    List<AddonVersion> releases,
    List<TocCommit> commits,
    Dictionary<string, string> changelog)
{
    public RemoteAddon Addon { get; } = addon;
    public IReadOnlyList<AddonVersion> Releases { get; } = releases;
    public IReadOnlyList<TocCommit> Commits { get; } = commits;

    /// <summary>The .toc version at each commit read so far (same order as <see cref="Commits"/>; null: no .toc).</summary>
    internal List<string?> TocVersions { get; } = [];

    /// <summary>Every commit's version has been read (or betas aren't included).</summary>
    public bool Complete => TocVersions.Count >= Commits.Count;

    /// <summary>
    /// The versions known so far, newest first, with CHANGELOG notes. While the history isn't fully read, it stops
    /// at the oldest version read: anything older (releases included) could still have betas to slot in between.
    /// </summary>
    public List<AddonVersion> Versions { get; private set; } = [];

    internal void Rebuild()
    {
        var versions = new List<AddonVersion>(Releases);
        string? floor = null;
        if (Commits.Count > 0 && Addon.LatestBeta?.Commit is { } headSha)
        {
            // Walk the commits read so far oldest → newest and note where each version first appears.
            var bumps = new List<(string Version, int Index)>();
            string? previous = null;
            for (var i = TocVersions.Count - 1; i >= 0; i--)
            {
                if (TocVersions[i] is not { } v || v == previous) continue;
                bumps.Add((v, i));
                previous = v;
            }

            // Until everything's read, the oldest version read may really start further back: leave it (and anything
            // older) out for now.
            var first = 0;
            if (!Complete)
            {
                if (bumps.Count == 0)
                {
                    Versions = [];
                    return;
                }
                floor = bumps[0].Version;
                first = 1;
            }
            for (var k = first; k < bumps.Count; k++)
            {
                var (version, index) = bumps[k];
                // Finished state of this version = the commit just before the next bump (or the branch head for the newest).
                var end = k + 1 < bumps.Count ? Commits[bumps[k + 1].Index].Parent : headSha;
                if (end is null || versions.Any(v => DrangedAddons.Versions.Compare(v.Version, version) == 0)) continue;
                versions.Add(new AddonVersion(version, false, null, null, end, Commits[index].Date,
                    $"https://github.com/{GitHubClient.Org}/{Addon.Name}/commit/{end}", null));
            }
        }

        Versions = versions
            .Where(v => floor is null || DrangedAddons.Versions.Compare(v.Version, floor) > 0)
            .Select(v => v with { Notes = changelog.GetValueOrDefault(v.Version) ?? v.Notes })
            .OrderByDescending(v => v.Version, Comparer<string>.Create(DrangedAddons.Versions.Compare))
            .ThenByDescending(v => v.Date)
            .ToList();
    }
}
