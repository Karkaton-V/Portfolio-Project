namespace Burndown.Repos.Tests;

public class GitHubRepoRefTests
{
    [Theory]
    [InlineData("jasontaylordev/CleanArchitecture")]
    [InlineData("github:jasontaylordev/CleanArchitecture")]
    [InlineData("https://github.com/jasontaylordev/CleanArchitecture")]
    [InlineData("https://github.com/jasontaylordev/CleanArchitecture.git")]
    [InlineData("https://github.com/jasontaylordev/CleanArchitecture/")]
    [InlineData("https://github.com/jasontaylordev/CleanArchitecture/tree/main/src")]
    [InlineData("http://www.github.com/jasontaylordev/CleanArchitecture")]
    [InlineData("git@github.com:jasontaylordev/CleanArchitecture.git")]
    [InlineData("  jasontaylordev/CleanArchitecture  ")]
    public void Parses_the_ways_people_paste_a_repository(string text)
    {
        Assert.True(GitHubRepoRef.TryParse(text, out var repo));
        Assert.Equal(new GitHubRepoRef("jasontaylordev", "CleanArchitecture"), repo);
        Assert.Equal("https://github.com/jasontaylordev/CleanArchitecture.git", repo.CloneUrl);
    }

    [Theory]
    [InlineData("Karkaton-V/Portfolio-Project", "Karkaton-V", "Portfolio-Project")]
    [InlineData("dotnet/aspnetcore.docs", "dotnet", "aspnetcore.docs")]
    [InlineData("a/b_c", "a", "b_c")]
    public void Keeps_owner_and_name_characters_github_allows(string text, string owner, string name)
    {
        Assert.True(GitHubRepoRef.TryParse(text, out var repo));
        Assert.Equal((owner, name), (repo.Owner, repo.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("just-a-name")]
    [InlineData("https://gitlab.com/owner/name")]
    [InlineData("owner/..")]
    [InlineData("-owner/name")] // owners can't start with a hyphen
    [InlineData("owner name/repo")]
    [InlineData("owner/name; rm -rf /")]
    public void Rejects_things_that_are_not_a_github_repository(string text)
    {
        Assert.False(GitHubRepoRef.TryParse(text, out _));
    }
}
