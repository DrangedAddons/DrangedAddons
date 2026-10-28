using System.ComponentModel;
using System.Diagnostics;

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
/// Runs the user's own git for addon folders that are Git working copies. Pulls are fast-forward only, so Git
/// refuses (and changes nothing) whenever an update would clash with local commits or uncommitted edits.
/// </summary>
public static class GitRepo
{
    // Signed-in checks run every minute. A fetch newer than this, with the same HEAD, is still the answer.
    static readonly TimeSpan FreshFetch = TimeSpan.FromMinutes(2);
    static readonly Dictionary<string, (string Fingerprint, DateTime FetchedUtc)> Fetches = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<GitStatus> GetStatusAsync(string dir, bool fetch)
    {
        if (fetch && !await FetchIsFreshAsync(dir))
        {
            try
            {
                await RunAsync(dir, "fetch", "--quiet");
            }
            catch (GitException ex)
            {
                throw new GitException("Couldn't check GitHub: " + ex.Message);
            }
            await RememberFetchAsync(dir);
        }

        var branch = (await RunAsync(dir, "rev-parse", "--abbrev-ref", "HEAD")).Trim();
        string counts;
        try
        {
            counts = await RunAsync(dir, "rev-list", "--left-right", "--count", "HEAD...@{u}");
        }
        catch (GitException)
        {
            throw new GitException($"Branch {branch} isn't tracking a branch on GitHub.");
        }
        var numbers = counts.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var changed = (await RunAsync(dir, "status", "--porcelain"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        // Is HEAD a release? Release tags look like v1.2.3.
        var tagged = (await RunAsync(dir, "tag", "--points-at", "HEAD"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(t => t.Length > 1 && (t[0] == 'v' || t[0] == 'V') && char.IsDigit(t[1]));
        return new GitStatus(branch, int.Parse(numbers[0]), int.Parse(numbers[1]), changed, tagged);
    }

    public static Task<string> PullAsync(string dir) => RunAsync(dir, "pull", "--ff-only");

    /// <summary>True when we fetched this checkout recently and its HEAD commit has not moved since.</summary>
    static async Task<bool> FetchIsFreshAsync(string dir)
    {
        // git rev-parse follows worktrees and packed refs. Reading .git/HEAD ourselves misses both.
        if (await HeadFingerprintAsync(dir) is not { } finger) return false;
        lock (Fetches)
            return Fetches.TryGetValue(dir, out var prev)
                   && prev.Fingerprint == finger
                   && DateTime.UtcNow - prev.FetchedUtc < FreshFetch;
    }

    static async Task RememberFetchAsync(string dir)
    {
        if (await HeadFingerprintAsync(dir) is not { } finger) return;
        lock (Fetches) Fetches[dir] = (finger, DateTime.UtcNow);
    }

    /// <summary>The commit HEAD points at, or null if this isn't a readable checkout.</summary>
    static async Task<string?> HeadFingerprintAsync(string workTree)
    {
        try
        {
            return (await RunAsync(workTree, "rev-parse", "HEAD")).Trim();
        }
        catch (GitException)
        {
            return null;
        }
    }

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
