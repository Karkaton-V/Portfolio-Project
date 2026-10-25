using System.Net;
using System.Text;

namespace CodeAtlas.Repos.Tests;

public class GitHubClientTests
{
    /// <summary>Answers requests from a script instead of the network, and records what was asked.</summary>
    private sealed class FakeGitHub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, string? nextPage = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (nextPage is not null)
        {
            response.Headers.Add("Link", $"<{nextPage}>; rel=\"next\", <https://api.github.com/user/repos?page=9>; rel=\"last\"");
        }

        return response;
    }

    private const string PageOne = """
        [
          { "full_name": "Karkaton-V/Portfolio-Project", "description": "Burndown", "language": "C#", "private": true,
            "fork": false, "archived": false, "pushed_at": "2026-10-24T05:00:00Z" },
          { "full_name": "Gas-Capstone/UpKeep", "description": null, "language": "TypeScript", "private": false,
            "fork": false, "archived": false, "pushed_at": "2026-10-20T05:00:00Z" }
        ]
        """;

    private const string PageTwo = """
        [ { "full_name": "Karkaton-V/FlowerDefense", "language": "C#", "private": false, "fork": false, "archived": false } ]
        """;

    [Fact]
    public async Task Lists_every_page_of_the_users_repositories()
    {
        var github = new FakeGitHub(request => request.RequestUri!.Query.Contains("page=2")
            ? Json(PageTwo)
            : Json(PageOne, nextPage: "https://api.github.com/user/repos?per_page=100&page=2"));
        var client = new GitHubClient(new HttpClient(github), "test-token");

        var repos = await client.ListMyRepositoriesAsync();

        Assert.Equal(["Karkaton-V/Portfolio-Project", "Gas-Capstone/UpKeep", "Karkaton-V/FlowerDefense"], repos.Select(r => r.FullName));
        Assert.True(repos[0].IsCSharp);
        Assert.True(repos[0].Private);
        Assert.False(repos[1].IsCSharp);
        Assert.Equal(2, github.Requests.Count);
    }

    [Fact]
    public async Task Asks_for_owned_collaborator_and_organization_repositories_with_the_token()
    {
        var github = new FakeGitHub(_ => Json("[]"));
        await new GitHubClient(new HttpClient(github), "test-token").ListMyRepositoriesAsync();

        var request = Assert.Single(github.Requests);
        Assert.Contains("affiliation=owner,collaborator,organization_member", request.RequestUri!.Query);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
        Assert.NotEmpty(request.Headers.UserAgent); // GitHub rejects requests without one
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "sign-in")]
    [InlineData(HttpStatusCode.Forbidden, "rate limit")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Turns_errors_into_messages_a_person_can_act_on(HttpStatusCode status, string expected)
    {
        var client = new GitHubClient(new HttpClient(new FakeGitHub(_ => new HttpResponseMessage(status))), "test-token");

        var error = await Assert.ThrowsAsync<GitHubException>(() => client.ListMyRepositoriesAsync());
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Stops_following_pages_after_a_safety_limit()
    {
        // A server that always claims there's another page must not loop forever.
        var github = new FakeGitHub(_ => Json("[]", nextPage: "https://api.github.com/user/repos?page=again"));
        await new GitHubClient(new HttpClient(github), "test-token").ListMyRepositoriesAsync();

        Assert.Equal(20, github.Requests.Count);
    }
}
