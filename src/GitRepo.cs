using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DrangedAddons;

public sealed class GitException(string message) : Exception(message);

/// <summary>Where a Git working copy stands against its upstream branch on GitHub.</summary>
public sealed record GitStatus(string Branch, int Ahead, int Behind, int Changed, bool HeadIsTagged)
{
    /// <summary>The local code is exactly a tagged release: on a version tag with no uncommitted changes.</summary>
    public bool IsRelease => HeadIsTagged && Changed == 0;

    public string Summary
    {
        get
        {
            var parts = new List<string> { $"branch {Branch}" };
            if (Behind > 0) parts.Add($"{Behind} commit{(Behind == 1 ? "" : "s")} to pull");
            if (Ahead > 0) parts.Add($"{Ahead} unpushed commit{(Ahead == 1 ? "" : "s")}");
            if (Changed > 0) parts.Add($"{Changed} uncommitted file{(Changed == 1 ? "" : "s")}");
            if (Behind == 0 && Ahead == 0) parts.Add("in sync with GitHub");
            return string.Join("  ·  ", parts);
        }
    }
}

/// <summary>
/// Addon folders that are Git working copies. Their status is read straight from the .git folder, and compared with
/// GitHub through its API: on some PCs every process start stalls the whole desktop for a quarter of a second, and
/// a status check used to start 5-7 git processes per copy. The user's own git only runs to pull (fast-forward only,
/// so Git refuses, and changes nothing, whenever an update would clash with local commits or uncommitted edits) and,
/// rarely, to count commits GitHub hasn't seen.
/// </summary>
public static class GitRepo
{
    public static async Task<GitStatus> GetStatusAsync(string dir, string repo, GitHubClient github)
    {
        var git = Locate(dir) ?? throw new GitException("Not a Git working copy.");
        var (branch, head) = ReadHead(git);
        if (head is null) throw new GitException("This working copy has no commits yet.");
        var tagged = TagsAt(git, head);
        var changed = ChangedFiles(dir, git) ?? CountLines(await RunAsync(dir, "status", "--porcelain"));
        var (ahead, behind) = await CompareAsync(dir, repo, Upstream(git, branch), head, github);
        return new GitStatus(branch, ahead, behind, changed, tagged);
    }

    public static Task<string> PullAsync(string dir) => RunAsync(dir, "pull", "--ff-only");

    // ---- Against GitHub ---------------------------------------------------------------------------------------------

    /// <summary>Commits this copy has that GitHub's branch doesn't (ahead), and the other way round (behind).</summary>
    static async Task<(int Ahead, int Behind)> CompareAsync(string dir, string repo, string branch, string head, GitHubClient github)
    {
        if (branch == "HEAD") throw new GitException("Not on a branch (detached HEAD).");
        var url = $"https://api.github.com/repos/{GitHubClient.Org}/{repo}";
        string? json;
        try
        {
            // The branch answer is usually a cached "not modified", which doesn't count against GitHub's limit.
            json = await github.GetJsonAsync($"{url}/branches/{Uri.EscapeDataString(branch)}", default);
            if (json is null) throw new GitException($"Branch {branch} isn't on GitHub.");
            using (var doc = JsonDocument.Parse(json))
                if (doc.RootElement.GetProperty("commit").GetProperty("sha").GetString() == head) return (0, 0);

            json = await github.GetJsonAsync($"{url}/compare/{head}...{Uri.EscapeDataString(branch)}?per_page=1", default);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RateLimitException)
        {
            throw new GitException("Couldn't check GitHub: " + ex.Message);
        }
        if (json is not null)
        {
            using var doc = JsonDocument.Parse(json);
            // Compared from this copy's commit to GitHub's branch: GitHub's extra commits are the ones to pull.
            return (doc.RootElement.GetProperty("behind_by").GetInt32(), doc.RootElement.GetProperty("ahead_by").GetInt32());
        }

        // GitHub doesn't have this copy's commit: there are unpushed commits. Git counts them against its last fetch.
        try
        {
            var numbers = (await RunAsync(dir, "rev-list", "--left-right", "--count", "HEAD...@{u}"))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return (int.Parse(numbers[0]), int.Parse(numbers[1]));
        }
        catch (GitException)
        {
            throw new GitException($"Branch {branch} isn't tracking a branch on GitHub.");
        }
    }

    // ---- The .git folder --------------------------------------------------------------------------------------------

    /// <summary>A working copy's .git folder, and the folder its shared refs and objects live in (differs for worktrees).</summary>
    sealed record GitDirs(string Git, string Common);

    static GitDirs? Locate(string dir)
    {
        var dotGit = Path.Combine(dir, ".git");
        string git;
        if (Directory.Exists(dotGit)) git = dotGit;
        else if (File.Exists(dotGit) && File.ReadAllText(dotGit).Trim() is { } link && link.StartsWith("gitdir:"))
            git = Path.GetFullPath(link["gitdir:".Length..].Trim(), dir);
        else return null;
        var commonFile = Path.Combine(git, "commondir");
        var common = File.Exists(commonFile) ? Path.GetFullPath(File.ReadAllText(commonFile).Trim(), git) : git;
        return new GitDirs(git, common);
    }

    /// <summary>The branch checked out ("HEAD" when detached, like git rev-parse --abbrev-ref) and its commit.</summary>
    static (string Branch, string? Sha) ReadHead(GitDirs git)
    {
        var head = File.ReadAllText(Path.Combine(git.Git, "HEAD")).Trim();
        if (!head.StartsWith("ref: ")) return ("HEAD", head);
        var name = head[5..].Trim();
        var branch = name.StartsWith("refs/heads/") ? name["refs/heads/".Length..] : name;
        return (branch, ResolveRef(git, name));
    }

    /// <summary>The branch on GitHub that <paramref name="branch"/> tracks ([branch "x"] merge in .git/config), else the same name.</summary>
    static string Upstream(GitDirs git, string branch)
    {
        var config = Path.Combine(git.Common, "config");
        if (branch == "HEAD" || !File.Exists(config)) return branch;
        var inSection = false;
        foreach (var raw in File.ReadLines(config))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = line == $"[branch \"{branch}\"]";
                continue;
            }
            if (inSection && line.StartsWith("merge") && line.Split('=', 2) is [_, var value]
                && value.Trim() is var merge && merge.StartsWith("refs/heads/"))
                return merge["refs/heads/".Length..];
        }
        return branch;
    }

    static string? ResolveRef(GitDirs git, string name)
    {
        foreach (var root in new[] { git.Git, git.Common })
        {
            var file = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
        }
        foreach (var (sha, refName, _) in PackedRefs(git))
            if (refName == name) return sha;
        return null;
    }

    /// <summary>packed-refs: each ref with its commit, and an annotated tag's commit ("^" line) when given.</summary>
    static IEnumerable<(string Sha, string Name, string? Peeled)> PackedRefs(GitDirs git)
    {
        var file = Path.Combine(git.Common, "packed-refs");
        if (!File.Exists(file)) yield break;
        (string Sha, string Name)? last = null;
        foreach (var line in File.ReadLines(file))
        {
            if (line.StartsWith('#') || line.Length < 41) continue;
            if (line[0] == '^')
            {
                if (last is { } tag) yield return (tag.Sha, tag.Name, line[1..].Trim());
                last = null;
                continue;
            }
            if (last is { } previous) yield return (previous.Sha, previous.Name, null);
            last = (line[..40], line[41..].Trim());
        }
        if (last is { } final) yield return (final.Sha, final.Name, null);
    }

    /// <summary>Whether a version tag (v1.2.3) points at <paramref name="head"/>.</summary>
    static bool TagsAt(GitDirs git, string head)
    {
        static bool IsVersion(string name) => name.Length > 1 && name[0] is 'v' or 'V' && char.IsDigit(name[1]);

        foreach (var (sha, name, peeled) in PackedRefs(git))
            if (name.StartsWith("refs/tags/") && IsVersion(name["refs/tags/".Length..]) && (sha == head || peeled == head))
                return true;

        var tags = Path.Combine(git.Common, "refs", "tags");
        if (!Directory.Exists(tags)) return false;
        foreach (var file in Directory.EnumerateFiles(tags, "*", SearchOption.AllDirectories))
        {
            if (!IsVersion(Path.GetFileName(file))) continue;
            var sha = File.ReadAllText(file).Trim();
            if (sha == head || TagTarget(git, sha) == head) return true;
        }
        return false;
    }

    /// <summary>An annotated tag's commit, when the tag object is stored loose (null otherwise).</summary>
    static string? TagTarget(GitDirs git, string sha)
    {
        if (sha.Length != 40) return null;
        var file = Path.Combine(git.Common, "objects", sha[..2], sha[2..]);
        if (!File.Exists(file)) return null;
        try
        {
            using var zlib = new ZLibStream(File.OpenRead(file), CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.UTF8);
            var header = reader.ReadLine() ?? ""; // "tag <size>\0object <sha>"
            var at = header.IndexOf("\0object ", StringComparison.Ordinal);
            return header.StartsWith("tag ") && at >= 0 ? header[(at + 8)..].Trim() : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tracked files that differ from the index: size or time changed (and content too, so a file saved unchanged
    /// doesn't count), or deleted. New files Git doesn't track yet aren't counted. Null for an index this can't read
    /// (version 4); git status counts those.
    /// </summary>
    static int? ChangedFiles(string dir, GitDirs git)
    {
        var path = Path.Combine(git.Git, "index");
        if (!File.Exists(path)) return 0;
        var data = File.ReadAllBytes(path);
        if (data.Length < 12 || Encoding.ASCII.GetString(data, 0, 4) != "DIRC") return null;
        var version = Be32(data, 4);
        if (version is not (2 or 3)) return null;
        var count = Be32(data, 8);
        var changed = 0;
        var at = 12;
        for (var i = 0; i < count; i++)
        {
            var start = at;
            var mtimeSeconds = Be32(data, at + 8);
            var mtimeNanos = Be32(data, at + 12);
            var size = Be32(data, at + 36);
            var sha = Convert.ToHexStringLower(data, at + 40, 20);
            var flags = (data[at + 60] << 8) | data[at + 61];
            at += 62;
            var skipWorktree = false;
            if ((flags & 0x4000) != 0) // extended flags (version 3)
            {
                skipWorktree = (data[at] & 0x40) != 0;
                at += 2;
            }
            var end = Array.IndexOf(data, (byte)0, at);
            var name = Encoding.UTF8.GetString(data, at, end - at);
            at = start + ((end - start + 8) & ~7); // entries are padded with 1-8 NULs to a multiple of 8
            if (skipWorktree || (flags & 0x8000) != 0) continue; // skip-worktree / assume-unchanged
            if (((flags >> 12) & 3) != 0) { changed++; continue; } // merge conflict

            var file = new FileInfo(Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!file.Exists) { changed++; continue; }
            var written = new DateTimeOffset(file.LastWriteTimeUtc);
            var sameTime = (uint)written.ToUnixTimeSeconds() == mtimeSeconds
                           && (mtimeNanos == 0 || (uint)(written.UtcTicks % TimeSpan.TicksPerSecond * 100) == mtimeNanos);
            if ((uint)file.Length == size && sameTime) continue;
            if (!SameContent(file.FullName, sha)) changed++;
        }
        return changed;
    }

    /// <summary>Whether the file is the blob Git has, as is or with CRLF line ends turned into LF (core.autocrlf).</summary>
    static bool SameContent(string file, string sha)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (BlobSha(bytes) == sha) return true;
        var lf = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
            if (!(bytes[i] == '\r' && i + 1 < bytes.Length && bytes[i + 1] == '\n')) lf.Add(bytes[i]);
        return lf.Count != bytes.Length && BlobSha([.. lf]) == sha;
    }

    static string BlobSha(byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        return Convert.ToHexStringLower(SHA1.HashData([.. header, .. content]));
    }

    static uint Be32(byte[] data, int at) =>
        (uint)(data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3]);

    static int CountLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    // ---- Running git ------------------------------------------------------------------------------------------------

    static async Task<string> RunAsync(string dir, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(dir);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        // Never pop up a credential prompt from a background check.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "never";

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new GitException("Couldn't start git.");
        }
        catch (Win32Exception)
        {
            throw new GitException("Git isn't installed (or isn't on PATH).");
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new GitException($"git {args[0]} timed out.");
            }

            if (process.ExitCode != 0)
            {
                var error = (await stderr).Trim();
                throw new GitException(error.Length > 0 ? error : $"git {args[0]} failed (exit code {process.ExitCode}).");
            }
            return await stdout;
        }
    }
}
