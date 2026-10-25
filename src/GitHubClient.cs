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
/// "304 Not Modified" replies against the hourly limit. File contents at a commit come from
/// raw.githubusercontent.com and source zips from codeload.github.com, neither of which uses that limit.
/// Anonymous use gets 60 API requests an hour per internet connection (and then 304s do count); signed in it's
/// 5,000 an hour, using a token saved in the app (File → GitHub token…), GH_TOKEN / GITHUB_TOKEN, or the GitHub
/// CLI's login on this PC.
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

    // "repo@sha:path" → .toc version ("" when the file is missing). Commits never change, so this never expires.
    readonly Dictionary<string, string> _tocVersions = Storage.Load<Dictionary<string, string>>(TocCacheFile);

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DrangedAddons-Manager/{typeof(GitHubClient).Assembly.GetName().Version}");
        return http;
    }

    public async Task<List<RemoteAddon>> GetAddonsAsync(CancellationToken ct = default)
    {
        var reposJson = await GetJsonAsync($"https://api.github.com/orgs/{Org}/repos?type=public&per_page=100", ct)
            ?? throw new InvalidOperationException($"The GitHub organisation '{Org}' was not found.");

        var repos = new List<(string Name, string Description, string Branch)>();
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
                repos.Add((name, OptionalString(repo, "description") ?? "", OptionalString(repo, "default_branch") ?? "main"));
            }
        }

        var addons = await Task.WhenAll(repos.Select(r => GetAddonAsync(r.Name, r.Description, r.Branch, ct)));
        SaveCaches();
        return addons.OfType<RemoteAddon>().OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    async Task<RemoteAddon?> GetAddonAsync(string repo, string description, string branch, CancellationToken ct)
    {
        var releaseTask = GetLatestReleaseAsync(repo, ct);
        var headTask = GetBranchHeadAsync(repo, branch, ct);
        await Task.WhenAll(releaseTask, headTask);
        var release = releaseTask.Result;
        var (tocPath, head) = headTask.Result;
        if (release is null && head is null) return null;
        // which games it's for (its .toc on the default branch; none listed: every game)
        var games = tocPath is not null && head?.Commit is { } sha
            ? await GetTocGamesAsync(repo, sha, tocPath, ct)
            : [];
        var author = tocPath is not null && head?.Commit is { } headSha
            ? await GetTocAuthorAsync(repo, headSha, tocPath, ct)
            : null;
        return new RemoteAddon(repo, description, branch, tocPath, release, head, games) { Author = author };
    }

    async Task<AddonVersion?> GetLatestReleaseAsync(string repo, CancellationToken ct)
    {
        var json = await GetJsonAsync($"https://api.github.com/repos/{Org}/{repo}/releases/latest", ct);
        if (json is null) return null; // no published release yet
        using var doc = JsonDocument.Parse(json);
        return ReleaseFromJson(doc.RootElement);
    }

    /// <summary>The newest commit on the default branch as an (untagged) beta version, if it has a readable .toc version.</summary>
    async Task<(string? TocPath, AddonVersion? Head)> GetBranchHeadAsync(string repo, string branch, CancellationToken ct)
    {
        var json = await GetJsonAsync($"https://api.github.com/repos/{Org}/{repo}/branches/{branch}", ct);
        if (json is null) return (null, null);

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
                return (path, new AddonVersion(version, false, null, null, sha, date, CommitUrl(repo, sha), null));
        }
        return (null, null);
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
        // One reader at a time (the details page reads ahead in the background while "Show more" can ask too).
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
            SaveCaches();
            history.TocVersions.AddRange(versions);
            history.Rebuild();
        }
        finally
        {
            history.Gate.Release();
        }
    }

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

    async Task<string?> GetTocVersionAsync(string repo, string sha, string path, CancellationToken ct)
    {
        var key = $"{repo}@{sha}:{path}";
        lock (_tocVersions)
        {
            if (_tocVersions.TryGetValue(key, out var known))
                return known.Length > 0 ? known : null;
        }
        var text = await GetRawAsync(repo, sha, path, ct);
        var version = text is null ? null : AddonInstaller.ParseTocVersion(text.Split('\n'));
        lock (_tocVersions) _tocVersions[key] = version ?? "";
        return version;
    }

    /// <summary>The games its .toc lists at this commit (cached with the versions: commits never change).</summary>
    async Task<IReadOnlyList<string>> GetTocGamesAsync(string repo, string sha, string path, CancellationToken ct)
    {
        var key = $"games|{repo}@{sha}:{path}";
        lock (_tocVersions)
        {
            if (_tocVersions.TryGetValue(key, out var known))
                return known.Split(',', StringSplitOptions.RemoveEmptyEntries);
        }
        var text = await GetRawAsync(repo, sha, path, ct);
        var games = text is null ? [] : AddonInstaller.ParseTocGames(text.Split('\n'));
        lock (_tocVersions) _tocVersions[key] = string.Join(',', games);
        return games;
    }

    /// <summary>"## Author:" from the .toc at a commit (cached; commits never change).</summary>
    async Task<string?> GetTocAuthorAsync(string repo, string sha, string path, CancellationToken ct)
    {
        var key = $"author|{repo}@{sha}:{path}";
        lock (_tocVersions)
        {
            if (_tocVersions.TryGetValue(key, out var known))
                return known.Length > 0 ? known : null;
        }
        var text = await GetRawAsync(repo, sha, path, ct);
        var author = text is null ? null : AddonInstaller.ParseTocField(text.Split('\n'), "Author");
        lock (_tocVersions) _tocVersions[key] = author ?? "";
        return author;
    }

    async Task<string?> GetRawAsync(string repo, string gitRef, string path, CancellationToken ct)
    {
        using var response = await SendRawAsync(HttpMethod.Get, repo, gitRef, path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await response.Content.ReadAsStringAsync(ct);
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
        try
        {
            var prefix = $"https://raw.githubusercontent.com/{Org}/";
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && url[prefix.Length..].Split('?')[0].Split('/', 3) is [var repo, var gitRef, var path])
                return await GetRawBytesAsync(repo, gitRef, Uri.UnescapeDataString(path), ct);
            using var response = await Http.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or RateLimitException or TaskCanceledException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>Whether a file exists at a branch or commit (false if GitHub can't be reached).</summary>
    public async Task<bool> HasFileAsync(string repo, string gitRef, string path)
    {
        try
        {
            using var response = await SendRawAsync(HttpMethod.Head, repo, gitRef, path, default);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or RateLimitException)
        {
            return false;
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
        lock (_cache) Storage.Save(CacheFile, _cache);
        lock (_tocVersions) Storage.Save(TocCacheFile, _tocVersions);
    }

    /// <summary>Returns the response body, or null on 404.</summary>
    internal async Task<string?> GetJsonAsync(string url, CancellationToken ct)
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
            return await GetJsonAsync(url, ct);
        }
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            return cached.Body;
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
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
                return await GetJsonAsync(url, ct);
            }
            throw new HttpRequestException($"GitHub refused {Describe(url)} ({(int)response.StatusCode}): {GitHubMessage(text)}");
        }
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub couldn't return {Describe(url)} ({(int)response.StatusCode} {response.ReasonPhrase}).");

        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.Headers.ETag?.ToString() is { } etag)
            lock (_cache) _cache[url] = new CachedResponse(etag, body);
        return body;
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
