using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Burndown.Repos.Tests;

public sealed class RemoteAnalyzerTests : IDisposable
{
    private static readonly GitHubRepoRef Runner = new("Karkaton-V", "Portfolio-Project");
    private static readonly GitHubRepoRef Target = new("jasontaylordev", "CleanArchitecture");

    private readonly string _folder = Directory.CreateTempSubdirectory("burndown-remote-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>
    /// Plays GitHub's side of a workflow run: the dispatch is accepted, the run appears on the second look,
    /// is queued, runs, finishes with <paramref name="conclusion"/>, and offers the report as a zip artifact.
    /// </summary>
    private sealed class FakeActions(string conclusion = "success", bool runEverAppears = true) : HttpMessageHandler
    {
        private string? _requestId;
        private int _runLookups;
        private int _statusChecks;

        public string? DispatchedRepository { get; private set; }

        public string? DispatchedRef { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/repos/Karkaton-V/Portfolio-Project")
            {
                return Json("""{ "default_branch": "main" }""");
            }

            if (path.EndsWith("/dispatches", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                DispatchedRef = body.RootElement.GetProperty("ref").GetString();
                DispatchedRepository = body.RootElement.GetProperty("inputs").GetProperty("repository").GetString();
                _requestId = body.RootElement.GetProperty("inputs").GetProperty("request_id").GetString();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.EndsWith("/workflows/burndown-analyze.yml/runs", StringComparison.Ordinal))
            {
                // GitHub takes a moment to create the run, and other runs are listed too.
                _runLookups++;
                var ours = runEverAppears && _runLookups > 1 ? $",{Run("queued", null, $"Burndown: x [{_requestId}]")}" : "";
                return Json($$"""{ "workflow_runs": [ {{Run("completed", "success", "Burndown: other [abc]", id: 1)}}{{ours}} ] }""");
            }

            if (path == "/repos/Karkaton-V/Portfolio-Project/actions/runs/42")
            {
                _statusChecks++;
                return Json(_statusChecks < 2 ? Run("in_progress", null, "") : Run("completed", conclusion, ""));
            }

            if (path.EndsWith("/runs/42/jobs", StringComparison.Ordinal))
            {
                return Json("""{ "jobs": [ { "steps": [ { "name": "Build Burndown", "status": "completed" }, { "name": "Analyze", "status": "in_progress" } ] } ] }""");
            }

            if (path.EndsWith("/runs/42/artifacts", StringComparison.Ordinal))
            {
                return Json("""{ "artifacts": [ { "id": 7, "name": "burndown-report" } ] }""");
            }

            if (path.EndsWith("/artifacts/7/zip", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ReportZip()) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string Run(string status, string? conclusion, string title, long id = 42) =>
            JsonSerializer.Serialize(new
            {
                id,
                status,
                conclusion,
                html_url = $"https://github.com/Karkaton-V/Portfolio-Project/actions/runs/{id}",
                display_title = title,
            });

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static byte[] ReportZip()
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(archive.CreateEntry("report.html").Open()))
            {
                writer.Write("<!doctype html><title>remote report</title>");
            }

            return stream.ToArray();
        }
    }

    private static RemoteAnalyzer Analyzer(FakeActions github) =>
        new(new GitHubClient(new HttpClient(github), "test-token"), Runner) { PollInterval = TimeSpan.Zero, StartAttempts = 5 };

    private sealed class Messages : IProgress<string>
    {
        public List<string> All { get; } = [];

        public void Report(string value) => All.Add(value);
    }

    [Fact]
    public async Task Runs_the_workflow_and_saves_the_report_it_produces()
    {
        var github = new FakeActions();
        var report = Path.Combine(_folder, "reports", "clean.html");
        var progress = new Messages();

        var runUrl = await Analyzer(github).AnalyzeAsync(Target, report, progress);

        Assert.Equal("jasontaylordev/CleanArchitecture", github.DispatchedRepository);
        Assert.Equal("main", github.DispatchedRef); // dispatched on the runner's default branch
        Assert.Equal("<!doctype html><title>remote report</title>", await File.ReadAllTextAsync(report));
        Assert.EndsWith("/actions/runs/42", runUrl); // found our run, not the other one listed first
        Assert.Contains(progress.All, m => m.Contains("Running on GitHub: Analyze"));
    }

    [Fact]
    public async Task Explains_a_failed_run_and_links_to_its_log()
    {
        var error = await Assert.ThrowsAsync<GitHubException>(() =>
            Analyzer(new FakeActions(conclusion: "failure")).AnalyzeAsync(Target, Path.Combine(_folder, "r.html"), new Messages()));

        Assert.Contains("failure", error.Message);
        Assert.Contains("https://github.com/Karkaton-V/Portfolio-Project/actions/runs/42", error.Message);
    }

    [Fact]
    public async Task Gives_up_when_github_never_starts_the_run()
    {
        var error = await Assert.ThrowsAsync<GitHubException>(() =>
            Analyzer(new FakeActions(runEverAppears: false)).AnalyzeAsync(Target, Path.Combine(_folder, "r.html"), new Messages()));

        Assert.Contains("didn't start", error.Message);
    }
}
