using System.Net;
using System.Text.Json;

namespace DrangedAddons;

public sealed class RateLimitException(DateTimeOffset? reset) : Exception(
    reset is { } r
        ? $"GitHub's request limit was reached. Try again after {r.ToLocalTime():t}."
        : "GitHub's request limit was reached. Try again in a few minutes.")
{
    public DateTimeOffset? Reset { get; } = reset;
}

/// <summary>How the app is talking to GitHub, and how much of this hour's allowance is left.</summary>
public sealed record GitHubAccess(string Source, bool SignedIn, int Limit, int Remaining, DateTimeOffset Reset);

public sealed record CachedResponse(string ETag, string Body);

/// <summary>Bytes downloaded so far, and the total if known.</summary>
public readonly record struct DownloadProgress(long Bytes, long? Total)
{
    /// <summary>0–1. With no known size it creeps towards 1 as data arrives (halfway at 2 MB) so it still moves.</summary>
    public double Fraction => Total is > 0 ? Math.Min(1, (double)Bytes / Total.Value) : 1 - Math.Exp(-Bytes / 2_900_000.0);

    /// <summary>"45%", or "1.2 MB" when the size isn't known.</summary>
    public string Text => Total is > 0 ? $"{Fraction:P0}".Replace(" ", "") : $"{Bytes / 1_048_576.0:0.0} MB";
}

/// <summary>
/// Reads the public DrangedAddons org. API responses are cached with ETags, and GitHub doesn't count
/// "304 Not Modified" replies against the hourly limit when signed in (anonymous 304s do count toward 60/hour).
/// Once a check has built the addon list, a later check that gets 304 for the org repo list stops there, unless a
/// repo was pushed in the last 15 minutes: a tag push changes the list, but the release its workflow publishes
/// afterwards doesn't, so each repo is still asked until that release has had time to appear. File contents at a commit come from
/// raw.githubusercontent.com and source zips from codeload.github.com, neither of which uses the API limit.
/// Signed in it's 5,000 API requests an hour, using a token saved in the app (File → GitHub token…),
/// GH_TOKEN / GITHUB_TOKEN, or the GitHub CLI's login on this PC.
/// </summary>
public sealed class GitHubClient
{
    public const string Org = "DrangedAddons";
    const string CacheFile = "http-cache.json";
    const string TocCacheFile = "toc-versions.json";

    static readonly HttpClient Http = CreateHttp();

    // A token saved in the app wins; otherwise GH_TOKEN / GITHUB_TOKEN or the GitHub CLI's login (looked up once in
    // the background). Tokens are only ever sent to api.github.com.
    static readonly Task<string?> TokenTask = Task.Run(FindToken);
    static volatile string? _savedToken;
    static volatile string? _rejectedToken;

    /// <summary>Uses (or, with null, stops using) a token the user saved in the app.</summary>
    public static void UseSavedToken(string? token) => _savedToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim();

    // Test builds only (DRANGEDADDONS_DEV=1): pretend not to be signed in / that GitHub's limit was hit.
    static readonly bool IsDev = Environment.GetEnvironmentVariable("DRANGEDADDONS_DEV") == "1";
    static readonly bool TestAnonymous = IsDev && Environment.GetEnvironmentVariable("DRANGEDADDONS_TEST_ANON") == "1";
    static readonly bool TestRateLimit = IsDev && Environment.GetEnvironmentVariable("DRANGEDADDONS_TEST_RATELIMIT") == "1";
    static readonly bool TestSlowDownload = IsDev && Environment.GetEnvironmentVariable("DRANGEDADDONS_TEST_SLOWDOWNLOAD") == "1";
    /// <summary>Dev only: "Repo=game;…" puts repos in another game, to test games without tagging real repos.</summary>
    static readonly Dictionary<string, string> TestGames = IsDev
        ? (Environment.GetEnvironmentVariable("DRANGEDADDONS_TEST_GAMES") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase)
        : new();
    /// <summary>Dev only: extra image URLs (";"-separated) added to every addon's gallery, to test it without screenshots.</summary>
    public static readonly string[] TestGalleryUrls = IsDev
        ? (Environment.GetEnvironmentVariable("DRANGEDADDONS_TEST_GALLERY") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
        : [];

    static async Task<string?> CurrentTokenAsync()
    {
        if (TestAnonymous && _savedToken is null) return null;
        var token = _savedToken ?? await TokenTask;
        return token is not null && token == _rejectedToken ? null : token;
    }

    /// <summary>Where the token in use comes from, for display.</summary>
    static async Task<string> TokenSourceAsync()
    {
        var token = await CurrentTokenAsync();
        if (token is null && _savedToken is not null && _savedToken == _rejectedToken)
            return "Not signed in: your saved token has expired or was revoked (add a new one)";
        if (token is null) return "Not signed in";
        if (token == _savedToken) return "Your GitHub token (saved in DrangedAddons)";
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
        {
            if (Environment.GetEnvironmentVariable(name)?.Trim() == token) return $"The {name} environment variable";
        }
        return "Your GitHub CLI login on this PC";
    }

    public static async Task<bool> IsSignedInAsync() => await CurrentTokenAsync() is not null;

    /// <summary>
    /// This hour's allowance for the given token (default: the one in use). Asking doesn't use any of it.
    /// Returns null if GitHub can't be reached; throws UnauthorizedAccessException for a token GitHub rejects.
    /// </summary>
    public static async Task<GitHubAccess?> GetAccessAsync(string? tryToken = null)
    {
        var token = tryToken ?? await CurrentTokenAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/rate_limit");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            using var response = await Http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException("GitHub didn't accept that token. Check it was copied in full and hasn't expired.");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var core = doc.RootElement.GetProperty("resources").GetProperty("core");
            return new GitHubAccess(
                tryToken is not null ? "That token" : await TokenSourceAsync(),
                token is not null,
                core.GetProperty("limit").GetInt32(),
                core.GetProperty("remaining").GetInt32(),
                DateTimeOffset.FromUnixTimeSeconds(core.GetProperty("reset").GetInt64()));
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    static string? FindToken()
    {
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } token)
                return token.Trim();
        }
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var gh = System.Diagnostics.Process.Start(info);
            if (gh is null) return null;
            var output = gh.StandardOutput.ReadToEndAsync();
            if (!gh.WaitForExit(5000))
            {
                gh.Kill();
                return null;
            }
            var token = output.Result.Trim();
            return gh.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch
        {
            return null; // GitHub CLI not installed: stay anonymous
        }
    }

    readonly Dictionary<string, CachedResponse> _cache = Storage.Load<Dictionary<string, CachedResponse>>(CacheFile);
    readonly object _httpWrite = new();
    int _httpGen;
    bool _httpDirty;

    // "repo@sha:path" → .toc version ("" when the file is missing). "games|" and "author|" hold the other
    // fields from that same file. Commits never change, so this never expires.
    readonly Dictionary<string, string> _tocVersions = Storage.Load<Dictionary<string, string>>(TocCacheFile);
    readonly object _tocWrite = new();
    int _tocGen;
    bool _tocDirty;

    // One version-history walk at a time, so opening several addons doesn't fire hundreds of .toc reads at once.
    readonly SemaphoreSlim _tocWalk = new(1, 1);

    // Raw files and images fetched this session. Cleared when the org repo list actually changes, so a branch
    // name ("main") isn't served forever after a push. Keyed so two callers share one download.
    readonly Dictionary<string, Task<string?>> _rawMemo = new(StringComparer.Ordinal);
    readonly Dictionary<string, Task<byte[]?>> _imageMemo = new(StringComparer.Ordinal);

    // The addon list built for the org-list ETag. A 304 with the same ETag returns this instead of refetching
    // every repo. Null until a check has succeeded, so the first check of a run still walks the repos.
    List<RemoteAddon>? _addonSnapshot;
    string? _snapshotETag;
    int _snapshotProfiles; // GameProfiles.Version when it was made: a new game profile can claim repos by topic

    static readonly JsonSerializerOptions CompactJson = new();

    readonly record struct JsonFetched(string? Body, bool NotModified, string? ETag);

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DrangedAddons-Manager/{typeof(GitHubClient).Assembly.GetName().Version}");
        return http;
    }

    public async Task<List<RemoteAddon>> GetAddonsAsync(CancellationToken ct = default)
    {
        var fetched = await FetchJsonAsync($"https://api.github.com/orgs/{Org}/repos?type=public&per_page=100", ct);
        // Test game overrides are applied while building the list, so a dev run always rebuilds.
        if (fetched.NotModified && _addonSnapshot is not null && fetched.ETag == _snapshotETag && TestGames.Count == 0
            && _snapshotProfiles == GameProfiles.Version
            && !PushedRecently(fetched.Body))
            return _addonSnapshot;
        var reposJson = fetched.Body
            ?? throw new InvalidOperationException($"The GitHub organisation '{Org}' was not found.");

        ClearFetchedFiles();
        var repos = new List<(string Name, string Description, string Branch, string Game)>();
        using (var doc = JsonDocument.Parse(reposJson))
        {
            foreach (var repo in doc.RootElement.EnumerateArray())
            {
                if (repo.GetProperty("private").GetBoolean() || repo.GetProperty("archived").GetBoolean())
                    continue;
                var name = repo.GetProperty("name").GetString() ?? "";
                // Skip this app's own repo and org meta repos like ".github".
                if (name.Length == 0 || name.StartsWith('.') || name.Equals(Org, StringComparison.OrdinalIgnoreCase))
                    continue;
                var topics = repo.TryGetProperty("topics", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : [];
                var game = TestGames.TryGetValue(name, out var testGame) ? testGame : GameDefinition.ForTopics(topics);
                repos.Add((name, OptionalString(repo, "description") ?? "", OptionalString(repo, "default_branch") ?? "main", game));
            }
        }

        var addons = await Task.WhenAll(repos.Select(r => GetAddonAsync(r.Name, r.Description, r.Branch, r.Game, ct)));
        var list = addons.OfType<RemoteAddon>().OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _addonSnapshot = list;
        _snapshotETag = fetched.ETag;
        _snapshotProfiles = GameProfiles.Version;
        await Task.Run(SaveCaches);
        return list;
    }

    /// <summary>
    /// Whether any repo in the org list was pushed within <see cref="ReleaseWindow"/>. A tag push changes the list,
    /// but the release its workflow publishes a minute or two later doesn't, so until that window has passed every
    /// check still asks each repo for its release.
    /// </summary>
    static bool PushedRecently(string? reposJson)
    {
        if (reposJson is null) return true;
        try
        {
            using var doc = JsonDocument.Parse(reposJson);
            var since = DateTimeOffset.UtcNow - ReleaseWindow;
            return doc.RootElement.EnumerateArray().Any(r =>
                r.TryGetProperty("pushed_at", out var p) && p.ValueKind == JsonValueKind.String
                && p.TryGetDateTimeOffset(out var pushed) && pushed > since);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>How long after a push a release can still appear (release workflows take a few minutes).</summary>
    static readonly TimeSpan ReleaseWindow = TimeSpan.FromMinutes(15);

    async Task<RemoteAddon?> GetAddonAsync(string repo, string description, string branch, string game, CancellationToken ct)
    {
        var releaseTask = GetLatestReleaseAsync(repo, ct);
        var headTask = GetBranchHeadAsync(repo, branch, ct);
        await Task.WhenAll(releaseTask, headTask);
        var release = releaseTask.Result;
        var (tocPath, head, headSha) = headTask.Result;
        if (release is null && head is null) return null;
        // which games it's for (its .toc on the default branch; none listed: every game).
        // GetBranchHead already parsed this file, so these two are cache hits.
        var games = tocPath is not null && head?.Commit is { } sha
            ? await GetTocGamesAsync(repo, sha, tocPath, ct)
            : [];
        var author = tocPath is not null && head?.Commit is { } headCommit
            ? await GetTocAuthorAsync(repo, headCommit, tocPath, ct)
            : null;
        return new RemoteAddon(repo, description, branch, tocPath, release, head, games)
        {
            Author = author,
            Game = game,
            HeadCommit = string.IsNullOrEmpty(headSha) ? null : headSha,
        };
    }

    async Task<AddonVersion?> GetLatestReleaseAsync(string repo, CancellationToken ct)
    {
        var json = await GetJsonAsync($"https://api.github.com/repos/{Org}/{repo}/releases/latest", ct);
        if (json is null) return null; // no published release yet
        using var doc = JsonDocument.Parse(json);
        return ReleaseFromJson(doc.RootElement);
    }

    /// <summary>
    /// The newest commit on the default branch. The version is set when that commit has a readable .toc;
    /// the SHA is returned either way so an icon can be stamped to the commit that contains it.
    /// </summary>
    async Task<(string? TocPath, AddonVersion? Head, string? Sha)> GetBranchHeadAsync(string repo, string branch, CancellationToken ct)
    {
        var json = await GetJsonAsync($"https://api.github.com/repos/{Org}/{repo}/branches/{branch}", ct);
        if (json is null) return (null, null, null);

        string sha;
        DateTimeOffset date;
        using (var doc = JsonDocument.Parse(json))
        {
            var commit = doc.RootElement.GetProperty("commit");
            sha = commit.GetProperty("sha").GetString() ?? "";
            date = commit.GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTimeOffset();
        }

        // Addon at the repo root (like CraftRoute), or in a folder named after the repo.
        foreach (var path in new[] { $"{repo}.toc", $"{repo}/{repo}.toc" })
        {
            if (await GetTocVersionAsync(repo, sha, path, ct) is { } version)
                return (path, new AddonVersion(version, false, null, null, sha, date, CommitUrl(repo, sha), null), sha);
        }
        return (null, null, sha);
    }

    /// <summary>
    /// Every installable version, newest first: all published releases, plus (when <paramref name="includeBeta"/>)
    /// every untagged version found on the default branch. Reads the whole history; the details page loads it in
    /// steps instead (<see cref="StartVersionHistoryAsync"/>).
    /// </summary>
    public async Task<List<AddonVersion>> GetVersionHistoryAsync(RemoteAddon addon, bool includeBeta, CancellationToken ct = default)
    {
        var history = await StartVersionHistoryAsync(addon, includeBeta, ct);
        await ReadMoreVersionsAsync(history, history.Commits.Count, ct);
        await Task.Run(SaveTocCache);
        return history.Versions;
    }

    /// <summary>
    /// The quick part of a version history: releases, the commits that changed the .toc (betas only) and the
    /// changelog, fetched together. No beta versions are known until <see cref="ReadMoreVersionsAsync"/> reads some.
    /// </summary>
    public async Task<VersionHistory> StartVersionHistoryAsync(RemoteAddon addon, bool includeBeta, CancellationToken ct = default)
    {
        var releases = GetReleasesAsync(addon, includeBeta, ct);
        var commits = includeBeta && addon.TocPath is not null && addon.LatestBeta?.Commit is not null
            ? GetTocCommitsAsync(addon, ct)
            : Task.FromResult(new List<TocCommit>());
        var changelog = GetChangelogAsync(addon, ct);
        await Task.WhenAll(releases, commits, changelog);
        var history = new VersionHistory(addon, releases.Result, commits.Result, changelog.Result);
        ReadCachedVersions(history);
        history.Rebuild();
        await Task.Run(SaveHttpCache);
        return history;
    }

    /// <summary>Takes every commit whose .toc version is already cached on disk, newest first (no requests).</summary>
    void ReadCachedVersions(VersionHistory history)
    {
        if (history.Addon.TocPath is not { } tocPath) return;
        lock (_tocVersions)
        {
            while (history.TocVersions.Count < history.Commits.Count
                   && _tocVersions.TryGetValue($"{history.Addon.Name}@{history.Commits[history.TocVersions.Count].Sha}:{tocPath}", out var known))
                history.TocVersions.Add(known.Length > 0 ? known : null);
        }
    }

    /// <summary>
    /// Reads the .toc version at the next <paramref name="count"/> commits (newest first), which turns them into beta
    /// versions. One small file each, cached on disk for good: commits never change.
    /// </summary>
    public async Task ReadMoreVersionsAsync(VersionHistory history, int count, CancellationToken ct = default)
    {
        // One walk in the app, then one reader per history (the background read and "Show more").
        await _tocWalk.WaitAsync(ct);
        try
        {
            await history.Gate.WaitAsync(ct);
            try
            {
                var from = history.TocVersions.Count;
                var batch = history.Commits.Skip(from).Take(count).ToList();
                if (batch.Count == 0 || history.Addon.TocPath is not { } tocPath) return;
                // Gentle without a token: GitHub limits bursts of anonymous file requests.
                using var throttle = new SemaphoreSlim(await CurrentTokenAsync() is null ? 4 : 8);
                var versions = await Task.WhenAll(batch.Select(async c =>
                {
                    await throttle.WaitAsync(ct);
                    try { return await GetTocVersionAsync(history.Addon.Name, c.Sha, tocPath, ct); }
                    finally { throttle.Release(); }
                }));
                history.TocVersions.AddRange(versions);
                history.Rebuild();
            }
            finally
            {
                history.Gate.Release();
            }
        }
        finally
        {
            _tocWalk.Release();
        }
    }

    /// <summary>A published release with this version, including GitHub prereleases. Null if it was never tagged.</summary>
    internal async Task<AddonVersion?> FindReleaseAsync(RemoteAddon addon, string version, CancellationToken ct = default)
    {
        var releases = await GetReleasesAsync(addon, includePrereleases: true, ct);
        await Task.Run(SaveHttpCache);
        return releases.FirstOrDefault(v => Versions.Compare(v.Version, version) == 0);
    }

    /// <param name="includePrereleases">
    /// GitHub's prerelease flag, which is separate from an untagged branch beta. "Show beta versions" passes
    /// this through, so a release marked prerelease is listed only then. <c>/releases/latest</c> never returns one.
    /// </param>
    async Task<List<AddonVersion>> GetReleasesAsync(RemoteAddon addon, bool includePrereleases, CancellationToken ct)
    {
        var releases = new List<AddonVersion>();
        for (var page = 1; page <= 10; page++)
        {
            var json = await GetJsonAsync($"https://api.github.com/repos/{Org}/{addon.Name}/releases?per_page=100&page={page}", ct);
            if (json is null) break;
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.EnumerateArray().ToList();
            foreach (var item in items)
            {
                if (!includePrereleases && item.GetProperty("prerelease").GetBoolean()) continue;
                if (ReleaseFromJson(item) is { } release) releases.Add(release);
            }
            if (items.Count < 100) break;
        }
        return releases;
    }

    /// <summary>The commits on the default branch that changed the .toc, newest first.</summary>
    async Task<List<TocCommit>> GetTocCommitsAsync(RemoteAddon addon, CancellationToken ct)
    {
        var commits = new List<TocCommit>();
        for (var page = 1; page <= 10; page++)
        {
            var json = await GetJsonAsync(
                $"https://api.github.com/repos/{Org}/{addon.Name}/commits?sha={addon.DefaultBranch}&path={addon.TocPath}&per_page=100&page={page}", ct);
            if (json is null) break;
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.EnumerateArray().ToList();
            foreach (var c in items)
            {
                var parents = c.GetProperty("parents");
                commits.Add(new TocCommit(
                    c.GetProperty("sha").GetString()!,
                    parents.GetArrayLength() > 0 ? parents[0].GetProperty("sha").GetString() : null,
                    c.GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTimeOffset()));
            }
            if (items.Count < 100) break;
        }
        return commits;
    }

    /// <summary>CHANGELOG.md sections keyed by version ("## 0.53.2" headings).</summary>
    async Task<Dictionary<string, string>> GetChangelogAsync(RemoteAddon addon, CancellationToken ct)
    {
        var sections = new Dictionary<string, string>();
        string? text = null;
        try
        {
            text = await GetRawAsync(addon.Name, addon.DefaultBranch, "CHANGELOG.md", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or RateLimitException)
        {
            // No changelog available: fall back to release notes.
        }
        if (text is null) return sections;

        string? current = null;
        var body = new List<string>();
        void Flush()
        {
            if (current is not null && !sections.ContainsKey(current))
                sections[current] = string.Join("\n", body).Trim();
            body.Clear();
        }
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("## "))
            {
                Flush();
                current = line[3..].Trim().Split(' ', 2)[0].Trim('[', ']').TrimStart('v', 'V');
            }
            else if (current is not null)
            {
                body.Add(line);
            }
        }
        Flush();
        return sections;
    }

    static string TocKey(string repo, string sha, string path) => $"{repo}@{sha}:{path}";

    /// <summary>
    /// Downloads the .toc once and stores its version, games and author. A cache written before all three were
    /// stored together misses the later keys and is filled in with one download.
    /// </summary>
    async Task EnsureTocAsync(string repo, string sha, string path, CancellationToken ct)
    {
        var key = TocKey(repo, sha, path);
        var gamesKey = $"games|{key}";
        var authorKey = $"author|{key}";
        lock (_tocVersions)
        {
            if (_tocVersions.ContainsKey(key) && _tocVersions.ContainsKey(gamesKey) && _tocVersions.ContainsKey(authorKey))
                return;
        }

        var text = await GetRawAsync(repo, sha, path, ct);
        var lines = text is null ? [] : text.Split('\n');
        lock (_tocVersions)
        {
            _tocVersions[key] = text is null ? "" : AddonInstaller.ParseTocVersion(lines) ?? "";
            _tocVersions[gamesKey] = text is null ? "" : string.Join(',', AddonInstaller.ParseTocGames(lines));
            _tocVersions[authorKey] = text is null ? "" : AddonInstaller.ParseTocField(lines, "Author") ?? "";
            _tocDirty = true;
            _tocGen++;
        }
    }

    async Task<string?> GetTocVersionAsync(string repo, string sha, string path, CancellationToken ct)
    {
        var key = TocKey(repo, sha, path);
        await EnsureTocAsync(repo, sha, path, ct);
        lock (_tocVersions)
            return _tocVersions.TryGetValue(key, out var known) && known.Length > 0 ? known : null;
    }

    /// <summary>The games its .toc lists at this commit (cached with the version: commits never change).</summary>
    async Task<IReadOnlyList<string>> GetTocGamesAsync(string repo, string sha, string path, CancellationToken ct)
    {
        var key = $"games|{TocKey(repo, sha, path)}";
        await EnsureTocAsync(repo, sha, path, ct);
        lock (_tocVersions)
            return _tocVersions.TryGetValue(key, out var known)
                ? known.Split(',', StringSplitOptions.RemoveEmptyEntries)
                : [];
    }

    /// <summary>"## Author:" from the .toc at a commit (cached; commits never change).</summary>
    async Task<string?> GetTocAuthorAsync(string repo, string sha, string path, CancellationToken ct)
    {
        var key = $"author|{TocKey(repo, sha, path)}";
        await EnsureTocAsync(repo, sha, path, ct);
        lock (_tocVersions)
            return _tocVersions.TryGetValue(key, out var known) && known.Length > 0 ? known : null;
    }

    async Task<string?> GetRawAsync(string repo, string gitRef, string path, CancellationToken ct)
    {
        var key = $"{repo}\n{gitRef}\n{path}";
        Task<string?> task;
        lock (_rawMemo)
        {
            if (!_rawMemo.TryGetValue(key, out task!))
                _rawMemo[key] = task = ReadRawAsync(repo, gitRef, path);
        }
        try
        {
            return await task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (!task.IsCompleted)
        {
            throw; // this caller stopped waiting; the shared download stays cached
        }
        catch
        {
            if (task.IsFaulted)
            {
                lock (_rawMemo)
                    if (_rawMemo.TryGetValue(key, out var current) && current == task)
                        _rawMemo.Remove(key);
            }
            throw;
        }
    }

    async Task<string?> ReadRawAsync(string repo, string gitRef, string path)
    {
        using var response = await SendRawAsync(HttpMethod.Get, repo, gitRef, path, CancellationToken.None);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await response.Content.ReadAsStringAsync(CancellationToken.None);
    }

    /// <summary>
    /// A file from raw.githubusercontent.com (signed in when possible: GitHub limits anonymous file downloads too).
    /// Throws RateLimitException when GitHub is limiting requests, a readable error for other failures; 404 is
    /// returned for the caller to treat as "no such file".
    /// </summary>
    async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string repo, string gitRef, string path, CancellationToken ct)
    {
        var url = $"https://raw.githubusercontent.com/{Org}/{repo}/{gitRef}/{path}";
        HttpRequestMessage Request(string? token)
        {
            var request = new HttpRequestMessage(method, url);
            if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            return request;
        }

        var token = await CurrentTokenAsync();
        var response = await Http.SendAsync(Request(token), ct);
        if (token is not null && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            response = await Http.SendAsync(Request(null), ct); // the token can't be used here: try without
        }
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var reset = ResetTime(response);
            response.Dispose();
            throw new RateLimitException(reset);
        }
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            var status = $"{(int)response.StatusCode} {response.ReasonPhrase}";
            response.Dispose();
            throw new HttpRequestException($"GitHub couldn't return {repo}/{path} ({status}).");
        }
        return response;
    }

    static bool IsRateLimited(HttpResponseMessage response, string body) =>
        response.StatusCode == HttpStatusCode.TooManyRequests
        || (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0")
        || response.Headers.RetryAfter is not null
        || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase);

    /// <summary>When GitHub says to try again: its reset time, or now + Retry-After.</summary>
    static DateTimeOffset? ResetTime(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ratelimit-reset", out var r) && long.TryParse(r.FirstOrDefault(), out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (response.Headers.RetryAfter is { } retry)
            return retry.Date ?? DateTimeOffset.Now + (retry.Delta ?? TimeSpan.FromMinutes(1));
        return null;
    }

    /// <summary>"CastCount releases" style description of an API URL for error messages.</summary>
    static string Describe(string url)
    {
        var path = new Uri(url).AbsolutePath.Trim('/').Split('/');
        return path.Length >= 4 && path[0] == "repos" ? $"{path[2]} {path[3]}" : string.Join('/', path);
    }

    static string GitHubMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    // ---- Details page ---------------------------------------------------------------------------------------------

    /// <summary>The README on the default branch, or null if there isn't one.</summary>
    public async Task<string?> GetReadmeAsync(RemoteAddon addon, CancellationToken ct = default)
    {
        foreach (var name in new[] { "README.md", "readme.md", "Readme.md", "README" })
            if (await GetRawAsync(addon.Name, addon.DefaultBranch, name, ct) is { } text)
                return text;
        return null;
    }

    /// <summary>CHANGELOG.md on the default branch, or null if there isn't one.</summary>
    public Task<string?> GetChangelogTextAsync(RemoteAddon addon, CancellationToken ct = default) =>
        GetRawAsync(addon.Name, addon.DefaultBranch, "CHANGELOG.md", ct);

    /// <summary>
    /// Downloads of every release's files added up (GitHub counts each download of a release asset, including the
    /// ones this app makes). Null if the repo has no releases.
    /// </summary>
    public async Task<long?> GetDownloadCountAsync(string repo, CancellationToken ct = default)
    {
        long total = 0;
        for (var page = 1; page <= 10; page++)
        {
            var json = await GetJsonAsync($"https://api.github.com/repos/{Org}/{repo}/releases?per_page=100&page={page}", ct);
            if (json is null) return page == 1 ? null : total;
            using var doc = JsonDocument.Parse(json);
            var releases = doc.RootElement.EnumerateArray().ToList();
            foreach (var release in releases)
                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                    if (asset.TryGetProperty("download_count", out var count) && count.ValueKind == JsonValueKind.Number)
                        total += count.GetInt64();
            if (releases.Count < 100) break;
        }
        return total;
    }

    static readonly string[] GalleryFolders = ["screenshots", "screenshot", "gallery", "media", "images"];
    static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp"];

    /// <summary>Images in a screenshots / gallery / media / images folder anywhere in the repo (default branch).</summary>
    public async Task<List<string>> GetGalleryPathsAsync(RemoteAddon addon, CancellationToken ct = default)
    {
        var paths = new List<string>();
        var json = await GetJsonAsync($"https://api.github.com/repos/{Org}/{addon.Name}/git/trees/{addon.DefaultBranch}?recursive=1", ct);
        if (json is null) return paths;
        using var doc = JsonDocument.Parse(json);
        foreach (var entry in doc.RootElement.GetProperty("tree").EnumerateArray())
        {
            if (OptionalString(entry, "type") != "blob" || OptionalString(entry, "path") is not { } path) continue;
            var parts = path.Split('/');
            if (parts.Length < 2 || !ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;
            if (parts[..^1].Any(folder => GalleryFolders.Contains(folder.ToLowerInvariant())))
                paths.Add(path);
        }
        return paths;
    }

    public static string RawUrl(string repo, string gitRef, string path) =>
        $"https://raw.githubusercontent.com/{Org}/{repo}/{gitRef}/{path}";

    /// <summary>
    /// An image's bytes. Files in this org's repos go through the raw-file path (token, rate limits); anything else
    /// is a plain download. Null if it can't be fetched.
    /// </summary>
    public async Task<byte[]?> GetImageBytesAsync(string url, CancellationToken ct = default)
    {
        Task<byte[]?> task;
        lock (_imageMemo)
        {
            if (!_imageMemo.TryGetValue(url, out task!))
                _imageMemo[url] = task = DownloadImageAsync(url);
        }
        try
        {
            var bytes = await task.WaitAsync(ct);
            if (bytes is null)
            {
                lock (_imageMemo)
                    if (_imageMemo.TryGetValue(url, out var current) && current == task)
                        _imageMemo.Remove(url);
            }
            return bytes;
        }
        catch (OperationCanceledException) when (!task.IsCompleted)
        {
            throw; // this caller stopped waiting; the shared download stays cached
        }
        catch
        {
            if (task.IsFaulted)
            {
                lock (_imageMemo)
                    if (_imageMemo.TryGetValue(url, out var current) && current == task)
                        _imageMemo.Remove(url);
            }
            throw;
        }
    }

    async Task<byte[]?> DownloadImageAsync(string url)
    {
        try
        {
            var prefix = $"https://raw.githubusercontent.com/{Org}/";
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && url[prefix.Length..].Split('?')[0].Split('/', 3) is [var repo, var gitRef, var path])
                return await GetRawBytesAsync(repo, gitRef, Uri.UnescapeDataString(path), CancellationToken.None);
            using var response = await Http.GetAsync(url, CancellationToken.None);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(CancellationToken.None) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or RateLimitException or TaskCanceledException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>A file's bytes at a branch or commit, or null if it doesn't exist.</summary>
    public async Task<byte[]?> GetRawBytesAsync(string repo, string gitRef, string path, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(HttpMethod.Get, repo, gitRef, path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    static AddonVersion? ReleaseFromJson(JsonElement release)
    {
        if (release.GetProperty("draft").GetBoolean()) return null;
        var zipUrl = release.GetProperty("assets").EnumerateArray()
            .Where(a => (a.GetProperty("name").GetString() ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.GetProperty("browser_download_url").GetString())
            .FirstOrDefault();
        if (zipUrl is null) return null; // only packaged releases are installable

        var tag = release.GetProperty("tag_name").GetString() ?? "";
        var published = release.GetProperty("published_at");
        return new AddonVersion(
            tag.TrimStart('v', 'V'),
            true,
            tag,
            zipUrl,
            null,
            published.ValueKind == JsonValueKind.String ? published.GetDateTimeOffset() : DateTimeOffset.MinValue,
            release.GetProperty("html_url").GetString() ?? "",
            OptionalString(release, "body"));
    }

    static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static string CommitUrl(string repo, string sha) => $"https://github.com/{Org}/{repo}/commit/{sha}";

    internal void SaveCaches()
    {
        SaveHttpCache();
        SaveTocCache();
    }

    /// <summary>Writes the HTTP cache if a request stored a new body. A 304 leaves it untouched.</summary>
    internal void SaveHttpCache() => SaveSnapshot(_httpWrite, _cache, () => _httpDirty, () => _httpGen, v => _httpDirty = v, CacheFile);

    /// <summary>Writes the .toc cache if a file was parsed. History walks call this once at the end, not per page.</summary>
    internal void SaveTocCache() => SaveSnapshot(_tocWrite, _tocVersions, () => _tocDirty, () => _tocGen, v => _tocDirty = v, TocCacheFile);

    void SaveSnapshot<T>(object writeLock, Dictionary<string, T> source, Func<bool> dirty, Func<int> gen, Action<bool> setDirty, string file)
    {
        lock (writeLock)
        {
            Dictionary<string, T> copy;
            int seen;
            lock (source)
            {
                if (!dirty()) return;
                copy = new Dictionary<string, T>(source);
                seen = gen();
            }
            Storage.WriteAtomic(file, JsonSerializer.Serialize(copy, CompactJson));
            lock (source)
                if (gen() == seen) setDirty(false);
        }
    }

    void ClearFetchedFiles()
    {
        lock (_rawMemo) _rawMemo.Clear();
        lock (_imageMemo) _imageMemo.Clear();
    }

    /// <summary>Returns the response body, or null on 404.</summary>
    internal async Task<string?> GetJsonAsync(string url, CancellationToken ct) =>
        (await FetchJsonAsync(url, ct)).Body;

    /// <summary>The body, whether GitHub said nothing changed, and the ETag that body belongs to.</summary>
    async Task<JsonFetched> FetchJsonAsync(string url, CancellationToken ct)
    {
        CachedResponse? cached;
        lock (_cache) _cache.TryGetValue(url, out cached);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (cached is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        var token = await CurrentTokenAsync();
        if (token is not null)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        if (TestRateLimit && token is null)
            throw new RateLimitException(DateTimeOffset.Now.AddMinutes(37));

        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized && token is not null)
        {
            // Expired or revoked login: carry on anonymously.
            _rejectedToken = token;
            return await FetchJsonAsync(url, ct);
        }
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            return new JsonFetched(cached.Body, true, cached.ETag);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new JsonFetched(null, false, null);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            // The hourly limit (remaining 0) or GitHub's short-term "too many at once" limit (Retry-After).
            if (IsRateLimited(response, text)) throw new RateLimitException(ResetTime(response));
            if (token is not null)
            {
                // The token isn't allowed to see this (e.g. a token tied to the org that's awaiting approval):
                // public data doesn't need it, so carry on anonymously.
                _rejectedToken = token;
                return await FetchJsonAsync(url, ct);
            }
            throw new HttpRequestException($"GitHub refused {Describe(url)} ({(int)response.StatusCode}): {GitHubMessage(text)}");
        }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub couldn't return {Describe(url)} ({(int)response.StatusCode} {response.ReasonPhrase}).");

        var body = await response.Content.ReadAsStringAsync(ct);
        var etag = response.Headers.ETag?.ToString();
        if (etag is not null)
        {
            lock (_cache)
            {
                _cache[url] = new CachedResponse(etag, body);
                _httpDirty = true;
                _httpGen++;
            }
        }
        return new JsonFetched(body, false, etag);
    }

    /// <summary>Source zip of the repo at a commit.</summary>
    public static string SourceZipUrl(string repo, string sha) => $"https://codeload.github.com/{Org}/{repo}/zip/{sha}";

    /// <summary>
    /// Downloads to a file, reporting bytes so far and the total size (null when the server doesn't say, as for
    /// source zips of beta builds) about every 100 ms.
    /// </summary>
    public async Task DownloadAsync(string url, string destination, IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destination);

        var buffer = new byte[TestSlowDownload ? 16384 : 81920];
        long done = 0;
        var lastReport = DateTime.MinValue;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (TestSlowDownload) await Task.Delay(60, ct);
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (progress is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 100)
            {
                lastReport = DateTime.UtcNow;
                progress.Report(new DownloadProgress(done, total));
            }
        }
        progress?.Report(new DownloadProgress(done, total ?? done));
    }
}
