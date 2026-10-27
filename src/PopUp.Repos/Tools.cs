using System.Diagnostics;
using System.Text;

namespace PopUp.Repos;

/// <summary>An external program (git, gh, dotnet) failed or couldn't be found. The message says which and why.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>
/// Runs the command-line tools PopUp relies on. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>,
/// never a single command string, so a repository name can't be interpreted as extra arguments.
/// </summary>
public static class Tools
{
    /// <summary>
    /// The user's GitHub token: <c>GH_TOKEN</c> or <c>GITHUB_TOKEN</c> if set, otherwise the GitHub CLI's
    /// (<c>gh auth token</c>). PopUp only holds it in memory for the request; it is never written anywhere.
    /// </summary>
    public static async Task<string?> FindGitHubTokenAsync(CancellationToken cancellationToken = default)
    {
        foreach (var variable in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } token)
            {
                return token.Trim();
            }
        }

        try
        {
            var output = await RunAsync("gh", ["auth", "token"], workingDirectory: null, cancellationToken);
            return output.Trim() is { Length: > 0 } token ? token : null;
        }
        catch (ToolException)
        {
            return null; // gh isn't installed or isn't signed in
        }
    }

    /// <summary>A shallow clone (latest commit only): fast, and all PopUp needs.</summary>
    public static Task CloneAsync(GitHubRepoRef repo, string destination, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destination);
        return RunAsync("git", ["clone", "--depth", "1", "--quiet", repo.CloneUrl, destination], workingDirectory: null, cancellationToken);
    }

    /// <summary>
    /// Deletes a clone PopUp made. Git marks its object files read-only, which makes a plain recursive
    /// delete fail on Windows, so clear that first. Failures are ignored: the files can be removed by hand.
    /// </summary>
    public static void DeleteClone(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Brings a clone up to date with its remote's default branch.</summary>
    public static Task PullAsync(string repository, CancellationToken cancellationToken = default) =>
        RunAsync("git", ["pull", "--ff-only", "--quiet"], repository, cancellationToken);

    /// <summary>The commit a clone has checked out.</summary>
    public static async Task<string> HeadAsync(string repository, CancellationToken cancellationToken = default) =>
        (await RunAsync("git", ["rev-parse", "HEAD"], repository, cancellationToken)).Trim();

    /// <summary>The branch a clone has checked out, or null for a detached checkout.</summary>
    public static async Task<string?> CurrentBranchAsync(string repository, CancellationToken cancellationToken = default)
    {
        var name = (await RunAsync("git", ["rev-parse", "--abbrev-ref", "HEAD"], repository, cancellationToken)).Trim();
        return name == "HEAD" ? null : name;
    }

    /// <summary>The default branch of a clone's <c>origin</c> on GitHub, asked of GitHub itself (no API token needed).</summary>
    public static async Task<string> DefaultBranchAsync(string repository, CancellationToken cancellationToken = default)
    {
        // Prints e.g. "ref: refs/heads/main<TAB>HEAD" followed by the commit line.
        var output = await RunAsync("git", ["ls-remote", "--symref", "origin", "HEAD"], repository, cancellationToken);
        const string Prefix = "ref: refs/heads/";
        var line = output.Split('\n').FirstOrDefault(l => l.StartsWith(Prefix, StringComparison.Ordinal))
            ?? throw new ToolException("git ls-remote didn't report origin's default branch.");
        return line[Prefix.Length..].Split('\t')[0].Trim();
    }

    public static async Task<bool> HasUncommittedChangesAsync(string repository, CancellationToken cancellationToken = default) =>
        (await RunAsync("git", ["status", "--porcelain"], repository, cancellationToken)).Trim().Length > 0;

    /// <summary>
    /// Checks out the newest commit of <paramref name="branch"/> from <c>origin</c>. In a copy PopUp owns, the
    /// local branch is simply made to match GitHub (fetching only that commit). In someone's own clone the
    /// branch is switched to and fast-forwarded, which fails instead of discarding commits that aren't on GitHub;
    /// check <see cref="HasUncommittedChangesAsync"/> first, since switching branches carries changes along.
    /// </summary>
    public static async Task CheckOutLatestAsync(string repository, string branch, bool ownedByPopUp, CancellationToken cancellationToken = default)
    {
        if (!IsSafeBranchName(branch))
        {
            throw new ToolException($"'{branch}' isn't a branch name PopUp can use.");
        }

        var remoteRef = $"refs/remotes/origin/{branch}";
        var refspec = $"+refs/heads/{branch}:{remoteRef}";
        if (ownedByPopUp)
        {
            await RunAsync("git", ["fetch", "--quiet", "--depth", "1", "origin", refspec], repository, cancellationToken);
            await RunAsync("git", ["checkout", "--quiet", "--force", "-B", branch, remoteRef], repository, cancellationToken);
            return;
        }

        await RunAsync("git", ["fetch", "--quiet", "origin", refspec], repository, cancellationToken);
        if (await CurrentBranchAsync(repository, cancellationToken) != branch)
        {
            var hasLocalBranch = true;
            try
            {
                await RunAsync("git", ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], repository, cancellationToken);
            }
            catch (ToolException)
            {
                hasLocalBranch = false;
            }

            await RunAsync("git", hasLocalBranch
                ? ["switch", "--quiet", branch]
                : ["switch", "--quiet", "--create", branch, "--track", remoteRef], repository, cancellationToken);
        }

        await RunAsync("git", ["merge", "--quiet", "--ff-only", remoteRef], repository, cancellationToken);
    }

    /// <summary>
    /// Git's own rules for branch names, enough that a name can't be read as an option or a revision range.
    /// </summary>
    internal static bool IsSafeBranchName(string branch) =>
        branch.Length > 0
        && !branch.StartsWith('-') && !branch.StartsWith('/') && !branch.EndsWith('/') && !branch.EndsWith(".lock", StringComparison.Ordinal)
        && !branch.Contains("..", StringComparison.Ordinal) && !branch.Contains("@{", StringComparison.Ordinal)
        && !branch.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '~' or '^' or ':' or '?' or '*' or '[' or '\\');

    /// <summary>
    /// Restores NuGet packages so referenced library types resolve during analysis. Like analysis itself, this
    /// evaluates the repository's MSBuild files, so only do it for repositories you trust.
    /// </summary>
    public static Task RestoreAsync(string solution, CancellationToken cancellationToken = default) =>
        RunAsync("dotnet", ["restore", solution, "--verbosity", "quiet"], Path.GetDirectoryName(solution), cancellationToken);

    public static async Task<string> RunAsync(
        string program,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // Never let git stop and wait for a password prompt in a window nobody can see.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new ToolException($"Couldn't start {program}.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ToolException($"{program} isn't installed or isn't on the PATH.");
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            if (process.ExitCode != 0)
            {
                var detail = (await error).Trim() is { Length: > 0 } stderr ? stderr : (await output).Trim();
                throw new ToolException($"{program} {arguments.FirstOrDefault()} failed: {detail}");
            }

            return await output;
        }
    }
}
