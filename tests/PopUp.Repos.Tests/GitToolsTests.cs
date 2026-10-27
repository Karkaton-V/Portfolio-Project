namespace PopUp.Repos.Tests;

/// <summary>
/// Runs real git against a throwaway "origin" (branches main and feature) and clones of it, all in a temporary
/// folder: a shallow copy like the ones PopUp downloads, and a full clone like one of the user's own.
/// </summary>
public sealed class GitToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("popup-git-").FullName;

    public void Dispose() => Tools.DeleteClone(_root);

    private static Task<string> Git(string folder, params string[] arguments) =>
        Tools.RunAsync("git", ["-c", "user.name=PopUp Tests", "-c", "user.email=tests@example.com", .. arguments], folder);

    private static async Task Commit(string folder, string file)
    {
        await File.WriteAllTextAsync(Path.Combine(folder, file), file);
        await Git(folder, "add", file);
        await Git(folder, "commit", "--quiet", "-m", $"Add {file}");
    }

    private static async Task<string> Head(string folder, string revision = "HEAD") => (await Git(folder, "rev-parse", revision)).Trim();

    private async Task<string> Origin()
    {
        var origin = Path.Combine(_root, "origin");
        Directory.CreateDirectory(origin);
        await Git(origin, "init", "--quiet", "--initial-branch=main");
        await Commit(origin, "one.txt");
        await Git(origin, "switch", "--quiet", "--create", "feature");
        await Commit(origin, "feature.txt");
        await Git(origin, "switch", "--quiet", "main");
        return origin;
    }

    private async Task<string> Clone(string origin, string name, bool shallow = false)
    {
        var clone = Path.Combine(_root, name);
        // A shallow clone of a local folder needs a file:// URL; a plain path would just copy everything.
        await Git(_root, shallow
            ? ["clone", "--quiet", "--depth", "1", new Uri(origin).AbsoluteUri, clone]
            : ["clone", "--quiet", origin, clone]);
        return clone;
    }

    [Fact]
    public async Task Asks_the_remote_for_its_default_branch()
    {
        var origin = await Origin();
        Assert.Equal("main", await Tools.DefaultBranchAsync(await Clone(origin, "clone")));
    }

    [Fact]
    public async Task Makes_a_downloaded_copy_match_any_branch_on_github()
    {
        var origin = await Origin();
        var copy = await Clone(origin, "copy", shallow: true);
        await Commit(origin, "two.txt");

        await Tools.CheckOutLatestAsync(copy, "main", ownedByPopUp: true);
        Assert.Equal(await Head(origin, "main"), await Head(copy));

        // A shallow clone starts with only the default branch; another one is fetched on demand.
        await Tools.CheckOutLatestAsync(copy, "feature", ownedByPopUp: true);
        Assert.Equal("feature", await Tools.CurrentBranchAsync(copy));
        Assert.Equal(await Head(origin, "feature"), await Head(copy));
    }

    [Fact]
    public async Task Switches_and_fast_forwards_a_users_own_clone()
    {
        var origin = await Origin();
        var clone = await Clone(origin, "clone");

        await Tools.CheckOutLatestAsync(clone, "feature", ownedByPopUp: false);
        Assert.Equal("feature", await Tools.CurrentBranchAsync(clone));

        await Git(origin, "switch", "--quiet", "feature");
        await Commit(origin, "more.txt");
        await Tools.CheckOutLatestAsync(clone, "feature", ownedByPopUp: false);
        Assert.Equal(await Head(origin, "feature"), await Head(clone));

        await Tools.CheckOutLatestAsync(clone, "main", ownedByPopUp: false);
        Assert.Equal("main", await Tools.CurrentBranchAsync(clone));
    }

    [Fact]
    public async Task Never_discards_commits_a_user_hasnt_pushed()
    {
        var origin = await Origin();
        var clone = await Clone(origin, "clone");
        await Commit(clone, "mine.txt"); // only on this computer
        await Commit(origin, "theirs.txt"); // only on GitHub: the two have diverged
        var mine = await Head(clone);

        await Assert.ThrowsAsync<ToolException>(() => Tools.CheckOutLatestAsync(clone, "main", ownedByPopUp: false));

        Assert.Equal(mine, await Head(clone));
        Assert.True(File.Exists(Path.Combine(clone, "mine.txt")));
    }

    [Fact]
    public async Task Notices_uncommitted_changes()
    {
        var origin = await Origin();
        var clone = await Clone(origin, "clone");
        Assert.False(await Tools.HasUncommittedChangesAsync(clone));

        await File.WriteAllTextAsync(Path.Combine(clone, "one.txt"), "edited");
        Assert.True(await Tools.HasUncommittedChangesAsync(clone));
    }

    [Fact]
    public async Task Says_so_when_the_remote_cant_be_reached()
    {
        var origin = await Origin();
        var clone = await Clone(origin, "clone");
        Directory.Move(origin, origin + "-gone");

        await Assert.ThrowsAsync<ToolException>(() => Tools.DefaultBranchAsync(clone));
    }

    [Theory]
    [InlineData("feature/login", true)]
    [InlineData("release-1.2", true)]
    [InlineData("-x", false)] // would be read as an option
    [InlineData("a..b", false)] // would be read as a range
    [InlineData("has space", false)]
    [InlineData("x:y", false)]
    [InlineData("", false)]
    public void Only_uses_branch_names_git_would_accept(string branch, bool safe)
    {
        Assert.Equal(safe, Tools.IsSafeBranchName(branch));
    }
}
