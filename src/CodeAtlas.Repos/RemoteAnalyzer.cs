using System.IO.Compression;
using System.Text.Json;

namespace CodeAtlas.Repos;

/// <summary>
/// Analyzes a repository on GitHub's servers: starts the CodeAtlas workflow in a "runner" repository (the
/// CodeAtlas repository, or a fork of it), follows the run, and downloads the report it produces. Used when a
/// repository isn't on this computer and can't be cloned, so nothing has to be downloaded or built locally.
/// </summary>
public sealed class RemoteAnalyzer(GitHubClient client, GitHubRepoRef runner)
{
    public const string WorkflowFile = "codeatlas-analyze.yml";
    private const string ArtifactName = "codeatlas-report";

    public GitHubRepoRef Runner => runner;

    /// <summary>How often to check on the run. Tests set this to zero.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for GitHub to start the run after it's requested.</summary>
    public int StartAttempts { get; init; } = 24;

    /// <summary>Writes the report for <paramref name="target"/> to <paramref name="reportPath"/>.</summary>
    /// <returns>The run's page on GitHub, for "see details" links.</returns>
    public async Task<string> AnalyzeAsync(
        GitHubRepoRef target,
        string reportPath,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        // GitHub doesn't return the run it starts, so tag the request and look for the tag in run titles.
        var requestId = Guid.NewGuid().ToString("N")[..12];
        var branch = await client.GetDefaultBranchAsync(runner, cancellationToken);

        progress.Report($"Asking GitHub to analyze {target.FullName}…");
        await client.DispatchWorkflowAsync(runner, WorkflowFile, branch,
            new Dictionary<string, string> { ["repository"] = target.FullName, ["request_id"] = requestId }, cancellationToken);

        WorkflowRun? run = null;
        for (var attempt = 0; attempt < StartAttempts && run is null; attempt++)
        {
            await Task.Delay(PollInterval, cancellationToken);
            run = await client.FindWorkflowRunAsync(runner, WorkflowFile, requestId, cancellationToken);
        }

        if (run is null)
        {
            throw new GitHubException($"GitHub accepted the request but didn't start the analysis. Check the Actions tab of {runner.FullName}.");
        }

        while (!run.IsCompleted)
        {
            var step = run.Status == "queued" ? null : await client.GetCurrentStepAsync(runner, run.Id, cancellationToken);
            progress.Report(run.Status == "queued"
                ? "Waiting for a GitHub machine…"
                : $"Running on GitHub: {step ?? "starting"}…");
            await Task.Delay(PollInterval, cancellationToken);
            run = await client.GetWorkflowRunAsync(runner, run.Id, cancellationToken);
        }

        if (run.Conclusion != "success")
        {
            throw new GitHubException($"The analysis on GitHub {run.Conclusion ?? "failed"}. The run's log explains why: {run.HtmlUrl}");
        }

        progress.Report("Downloading the report…");
        var zip = await client.DownloadArtifactAsync(runner, run.Id, ArtifactName, cancellationToken);
        await ExtractReportAsync(zip, reportPath, cancellationToken);
        return run.HtmlUrl;
    }

    private static async Task ExtractReportAsync(byte[] zip, string reportPath, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        var entry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            ?? throw new GitHubException("The report artifact didn't contain an HTML report.");

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
        await using var source = entry.Open();
        await using var destination = File.Create(reportPath);
        await source.CopyToAsync(destination, cancellationToken);
    }
}

/// <summary>
/// User settings, in <c>settings.json</c> next to the saved repositories. Today: which repository runs the
/// GitHub analysis workflow (CodeAtlas's own, unless you point it at a fork with <c>{ "runner": "you/CodeAtlas" }</c>
/// or the CODEATLAS_RUNNER environment variable).
/// </summary>
public sealed record CodeAtlasSettings(string Runner)
{
    public const string DefaultRunner = "Karkaton-V/Portfolio-Project";

    public static CodeAtlasSettings Load(string home)
    {
        if (Environment.GetEnvironmentVariable("CODEATLAS_RUNNER") is { Length: > 0 } fromEnvironment)
        {
            return new CodeAtlasSettings(fromEnvironment);
        }

        var file = Path.Combine(home, "settings.json");
        if (File.Exists(file))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.TryGetProperty("runner", out var runner) && runner.GetString() is { Length: > 0 } value)
                {
                    return new CodeAtlasSettings(value);
                }
            }
            catch (JsonException)
            {
                // A damaged settings file falls back to the defaults rather than blocking the app.
            }
        }

        return new CodeAtlasSettings(DefaultRunner);
    }
}
