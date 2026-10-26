namespace PopUp.Repos.Tests;

/// <summary>Builds small folder trees with real .git/config files in a temporary folder.</summary>
public sealed class LocalRepoFinderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("popup-finder-").FullName;

    public void Dispose()
    {
        Tools.DeleteClone(_root);
    }

    private string Clone(string relativePath, string originUrl)
    {
        var folder = Path.GetFullPath(Path.Combine(_root, relativePath)); // normalizes the separators
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        File.WriteAllText(Path.Combine(folder, ".git", "config"), $"""
            [core]
                repositoryformatversion = 0
            [remote "upstream"]
                url = https://github.com/someone-else/fork-source.git
            [remote "origin"]
                url = {originUrl}
                fetch = +refs/heads/*:refs/remotes/origin/*
            """);
        return folder;
    }

    [Fact]
    public void Finds_clones_by_their_origin_remote()
    {
        var https = Clone("GitHub/Portfolio Project", "https://github.com/Karkaton-V/Portfolio-Project.git");
        var ssh = Clone("GitHub/UpKeep", "git@github.com:Gas-Capstone/UpKeep.git");
        Clone("GitHub/Elsewhere", "https://gitlab.com/someone/project.git"); // not GitHub: ignored

        var found = LocalRepoFinder.Find([Path.Combine(_root, "GitHub")]);

        Assert.Equal([https], found["karkaton-v/portfolio-project"]); // case-insensitive lookup
        Assert.Equal([ssh], found["Gas-Capstone/UpKeep"]);
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Reads_origin_not_other_remotes()
    {
        var folder = Clone("fork", "https://github.com/me/fork.git");

        Assert.Equal(new GitHubRepoRef("me", "fork"), LocalRepoFinder.ReadOrigin(folder));
    }

    [Fact]
    public void Follows_a_worktree_back_to_the_main_repository_config()
    {
        // A worktree's .git is a file pointing at .git/worktrees/<name>; its remotes live in the main config.
        var main = Clone("main", "https://github.com/Karkaton-V/Portfolio-Project.git");
        var worktreeGitDir = Path.Combine(main, ".git", "worktrees", "feature");
        Directory.CreateDirectory(worktreeGitDir);
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..");

        var worktree = Path.Combine(_root, "feature-checkout");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDir}");

        Assert.Equal("Karkaton-V/Portfolio-Project", LocalRepoFinder.ReadOrigin(worktree)?.FullName);
    }

    [Fact]
    public void Searches_a_couple_of_levels_down_but_not_into_build_output()
    {
        var nested = Clone("GitHub/school/csc477-SpaceShooter", "https://github.com/Karkaton-V/csc477-SpaceShooter");
        Clone("GitHub/school/node_modules/some-package", "https://github.com/npm/some-package");

        var found = LocalRepoFinder.Find([Path.Combine(_root, "GitHub")]);

        Assert.Equal([nested], found["Karkaton-V/csc477-SpaceShooter"]);
        Assert.False(found.ContainsKey("npm/some-package"));
    }

    [Fact]
    public void Doesnt_treat_a_repositorys_subfolders_as_more_repositories()
    {
        var outer = Clone("GitHub/app", "https://github.com/me/app");
        Clone("GitHub/app/vendor/lib", "https://github.com/them/lib"); // e.g. a submodule

        var found = LocalRepoFinder.Find([Path.Combine(_root, "GitHub")]);

        Assert.Equal([outer], found["me/app"]);
        Assert.False(found.ContainsKey("them/lib"));
    }

    [Fact]
    public void Describes_a_clone_by_its_github_owner_and_name_even_from_a_subfolder()
    {
        var clone = Clone("GitHub/Portfolio Project", "https://github.com/Karkaton-V/Portfolio-Project.git");
        Directory.CreateDirectory(Path.Combine(clone, "samples", "ShopSample"));

        var location = LocalRepoFinder.Describe(Path.Combine(clone, "samples", "ShopSample"));

        Assert.Equal(("Karkaton-V", "Portfolio-Project"), (location.Owner, location.Name));
        Assert.Equal("https://github.com/Karkaton-V/Portfolio-Project", location.GitHub?.WebUrl);
    }

    [Fact]
    public void Describes_a_plain_folder_by_its_parent_and_its_own_name()
    {
        var folder = Path.Combine(_root, "popup-samples", "CleanArchitecture");
        Directory.CreateDirectory(folder);

        var location = LocalRepoFinder.Describe(folder + Path.DirectorySeparatorChar);

        Assert.Equal(("popup-samples", "CleanArchitecture"), (location.Owner, location.Name));
        Assert.Null(location.GitHub);
    }

    [Fact]
    public void Skips_folders_that_dont_exist()
    {
        Assert.Empty(LocalRepoFinder.Find([Path.Combine(_root, "missing")]));
    }

    [Theory]
    [InlineData("[remote \"origin\"]\n\turl = https://github.com/a/b.git", "https://github.com/a/b.git")]
    [InlineData("[remote \"origin\"]\r\n\tfetch = x\r\n\turl=git@github.com:a/b.git\r\n", "git@github.com:a/b.git")]
    [InlineData("[remote \"upstream\"]\n\turl = https://github.com/x/y", null)]
    [InlineData("", null)]
    public void Parses_the_origin_url_from_git_config(string config, string? expected)
    {
        Assert.Equal(expected, LocalRepoFinder.ParseOriginUrl(config));
    }
}
