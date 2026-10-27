using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PopUp.Repos;

/// <summary>A repository as the GitHub API describes it (the fields PopUp uses).</summary>
/// <param name="Language">GitHub's guess at the main language, e.g. "C#"; null for empty repositories.</param>
public sealed record GitHubRepo(
    string FullName,
    string? Description,
    string? Language,
    bool Private,
    bool Fork,
    bool Archived,
    DateTimeOffset? PushedAt)
{
    public bool IsCSharp => string.Equals(Language, "C#", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A GitHub request failed. The message is written for the person using PopUp.</summary>
public sealed class GitHubException(string message) : Exception(message);

/// <summary>A GitHub Actions workflow run.</summary>
/// <param name="Status">queued, in_progress, completed, ...</param>
/// <param name="Conclusion">Once completed: success, failure, cancelled, ...</param>
public sealed record WorkflowRun(long Id, string Status, string? Conclusion, string HtmlUrl, string DisplayTitle)
{
    public bool IsCompleted => Status == "completed";
}

/// <summary>
/// A small client for the GitHub REST API: just the calls PopUp needs. Takes an <see cref="HttpClient"/>
/// so tests can substitute the network.
/// </summary>
public sealed partial class GitHubClient(HttpClient http, string token)
{
    private const int MaxPages = 20; // 2,000 repositories is plenty, and bounds a runaway pagination loop

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static Uri ApiBase { get; } = new("https://api.github.com/");

    /// <summary>
    /// Every repository the signed-in user can see as an owner, a collaborator, or an organization member,
    /// most recently pushed first. Follows the API's pagination (the <c>Link</c> header).
    /// </summary>
    public async Task<IReadOnlyList<GitHubRepo>> ListMyRepositoriesAsync(CancellationToken cancellationToken = default)
    {
        var repos = new List<GitHubRepo>();
        Uri? next = new(ApiBase, "user/repos?affiliation=owner,collaborator,organization_member&sort=pushed&per_page=100");

        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            using var response = await SendAsync(next, cancellationToken);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            repos.AddRange(await JsonSerializer.DeserializeAsync<List<GitHubRepo>>(body, Options, cancellationToken) ?? []);
            next = NextPage(response);
        }

        return repos;
    }

    /// <summary>The login of the account the token belongs to.</summary>
    public async Task<string> GetLoginAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(new Uri(ApiBase, "user"), cancellationToken);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("login").GetString() ?? "";
    }

    /// <summary>Every branch of a repository, by name, in GitHub's order.</summary>
    public async Task<IReadOnlyList<string>> ListBranchesAsync(GitHubRepoRef repo, CancellationToken cancellationToken = default)
    {
        var branches = new List<string>();
        Uri? next = new(ApiBase, $"repos/{repo.FullName}/branches?per_page=100");

        for (var page = 0; next is not null && page < MaxPages; page++)
        {
            using var response = await SendAsync(next, cancellationToken,
                notFound: $"{repo.FullName} doesn't exist, or this account can't see it.");
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            branches.AddRange(document.RootElement.EnumerateArray()
                .Select(branch => branch.GetProperty("name").GetString())
                .OfType<string>());
            next = NextPage(response);
        }

        return branches;
    }

    public async Task<string> GetDefaultBranchAsync(GitHubRepoRef repo, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(new Uri(ApiBase, $"repos/{repo.FullName}"), cancellationToken,
            notFound: $"{repo.FullName} doesn't exist, or this account can't see it.");
        return document.RootElement.GetProperty("default_branch").GetString() ?? "main";
    }

    /// <summary>Starts a <c>workflow_dispatch</c> workflow. GitHub answers before the run exists; find it with <see cref="FindWorkflowRunAsync"/>.</summary>
    public async Task DispatchWorkflowAsync(
        GitHubRepoRef repo,
        string workflowFile,
        string gitRef,
        IReadOnlyDictionary<string, string> inputs,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new { @ref = gitRef, inputs });
        using var response = await SendAsync(
            new Uri(ApiBase, $"repos/{repo.FullName}/actions/workflows/{workflowFile}/dispatches"),
            cancellationToken,
            HttpMethod.Post,
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            notFound: $"The {workflowFile} workflow isn't in {repo.FullName}'s default branch, or this account can't run it.");
    }

    /// <summary>The most recent run of a workflow whose title contains <paramref name="marker"/>, if it has started.</summary>
    public async Task<WorkflowRun?> FindWorkflowRunAsync(
        GitHubRepoRef repo, string workflowFile, string marker, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            new Uri(ApiBase, $"repos/{repo.FullName}/actions/workflows/{workflowFile}/runs?event=workflow_dispatch&per_page=30"),
            cancellationToken);
        return document.RootElement.GetProperty("workflow_runs").EnumerateArray()
            .Select(ReadRun)
            .FirstOrDefault(run => run.DisplayTitle.Contains(marker, StringComparison.Ordinal));
    }

    public async Task<WorkflowRun> GetWorkflowRunAsync(GitHubRepoRef repo, long runId, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(new Uri(ApiBase, $"repos/{repo.FullName}/actions/runs/{runId}"), cancellationToken);
        return ReadRun(document.RootElement);
    }

    /// <summary>The name of the step a run is working on right now, for progress messages.</summary>
    public async Task<string?> GetCurrentStepAsync(GitHubRepoRef repo, long runId, CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(new Uri(ApiBase, $"repos/{repo.FullName}/actions/runs/{runId}/jobs"), cancellationToken);
        return document.RootElement.GetProperty("jobs").EnumerateArray()
            .SelectMany(job => job.TryGetProperty("steps", out var steps) ? steps.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Where(step => step.GetProperty("status").GetString() == "in_progress")
            .Select(step => step.GetProperty("name").GetString())
            .FirstOrDefault();
    }

    /// <summary>Downloads a run's artifact as a zip file.</summary>
    public async Task<byte[]> DownloadArtifactAsync(
        GitHubRepoRef repo, long runId, string artifactName, CancellationToken cancellationToken = default)
    {
        long artifactId;
        using (var document = await GetJsonAsync(new Uri(ApiBase, $"repos/{repo.FullName}/actions/runs/{runId}/artifacts"), cancellationToken))
        {
            artifactId = document.RootElement.GetProperty("artifacts").EnumerateArray()
                .Where(a => a.GetProperty("name").GetString() == artifactName)
                .Select(a => a.GetProperty("id").GetInt64())
                .DefaultIfEmpty(-1)
                .First();
        }

        if (artifactId < 0)
        {
            throw new GitHubException($"The run finished without producing a '{artifactName}' artifact.");
        }

        // GitHub redirects to a storage URL; HttpClient follows it and doesn't forward the token to the other host.
        using var response = await SendAsync(new Uri(ApiBase, $"repos/{repo.FullName}/actions/artifacts/{artifactId}/zip"), cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static WorkflowRun ReadRun(JsonElement run) => new(
        run.GetProperty("id").GetInt64(),
        run.GetProperty("status").GetString() ?? "",
        run.TryGetProperty("conclusion", out var conclusion) ? conclusion.GetString() : null,
        run.GetProperty("html_url").GetString() ?? "",
        run.TryGetProperty("display_title", out var title) ? title.GetString() ?? "" : "");

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken cancellationToken, string? notFound = null)
    {
        using var response = await SendAsync(uri, cancellationToken, notFound: notFound);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri uri,
        CancellationToken cancellationToken,
        HttpMethod? method = null,
        HttpContent? content = null,
        string? notFound = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PopUp", "1.0"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        response.Dispose();
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new GitHubException(
                "GitHub rejected the sign-in. Run `gh auth login` again, or set a valid GITHUB_TOKEN."),
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => new GitHubException(
                "GitHub refused the request, usually because of its rate limit. Try again in a few minutes."),
            HttpStatusCode.NotFound when notFound is not null => new GitHubException(notFound),
            _ => new GitHubException($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase} for {uri.AbsolutePath}."),
        };
    }

    /// <summary>The <c>rel="next"</c> URL from a <c>Link: &lt;url&gt;; rel="next", ...</c> header, if any.</summary>
    internal static Uri? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        var match = NextLink().Match(string.Join(",", values));
        return match.Success ? new Uri(match.Groups["url"].Value) : null;
    }

    [GeneratedRegex("<(?<url>[^>]+)>\\s*;\\s*rel=\"next\"")]
    private static partial Regex NextLink();
}
