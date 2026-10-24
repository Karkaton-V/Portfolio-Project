using System.Diagnostics;
using System.Text;

namespace CodeAtlas.Repos;

/// <summary>An external program (git, gh, dotnet) failed or couldn't be found. The message says which and why.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>
/// Runs the command-line tools CodeAtlas relies on. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>,
/// never a single command string, so a repository name can't be interpreted as extra arguments.
/// </summary>
public static class Tools
{
    /// <summary>
    /// The user's GitHub token: <c>GH_TOKEN</c> or <c>GITHUB_TOKEN</c> if set, otherwise the GitHub CLI's
    /// (<c>gh auth token</c>). CodeAtlas only holds it in memory for the request; it is never written anywhere.
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

    /// <summary>A shallow clone (latest commit only): fast, and all CodeAtlas needs.</summary>
    public static Task CloneAsync(GitHubRepoRef repo, string destination, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destination);
        return RunAsync("git", ["clone", "--depth", "1", "--quiet", repo.CloneUrl, destination], workingDirectory: null, cancellationToken);
    }

    /// <summary>
    /// Deletes a clone CodeAtlas made. Git marks its object files read-only, which makes a plain recursive
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
