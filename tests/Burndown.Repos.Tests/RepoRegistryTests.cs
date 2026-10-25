namespace Burndown.Repos.Tests;

/// <summary>Each test gets its own temporary Burndown home, deleted afterwards.</summary>
public sealed class RepoRegistryTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("burndown-home-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    /// <summary>A folder path under the temporary home, built with this platform's separators.</summary>
    private string Folder(string name) => Path.Combine(_home, "code", name);

    private static RegisteredRepo Repo(string name, string path, string? gitHub = null) =>
        new(name, path, gitHub, Managed: gitHub is not null, AddedAt: DateTimeOffset.Parse("2026-10-24T12:00:00Z"));

    [Fact]
    public void Starts_empty_and_round_trips_through_its_file()
    {
        var registry = RepoRegistry.Load(_home);
        Assert.Empty(registry.Repos);

        registry.Add(Repo("shop", Folder("shop")));
        registry.Add(Repo("atlas", Folder("atlas"), "Karkaton-V/Portfolio-Project"));
        registry.Save();

        var reloaded = RepoRegistry.Load(_home);
        Assert.Equal(["atlas", "shop"], reloaded.Repos.Select(r => r.Name)); // kept sorted
        Assert.Equal("Karkaton-V/Portfolio-Project", reloaded.Find("atlas")?.GitHub);
        Assert.False(File.Exists(Path.Combine(_home, "repos.json.tmp"))); // the temporary file was moved into place
    }

    [Fact]
    public void Explains_a_damaged_file_and_can_set_it_aside_to_start_over()
    {
        var file = Path.Combine(_home, "repos.json");
        File.WriteAllText(file, """[ { "name": "x", "path": "C:\Users" } ]"""); // a hand edit with an unescaped backslash

        var error = Assert.Throws<RepoRegistryException>(() => RepoRegistry.Load(_home));
        Assert.Contains("damaged", error.Message);

        var aside = RepoRegistry.SetAsideDamaged(_home);

        Assert.Empty(RepoRegistry.Load(_home).Repos);
        Assert.Contains("C:\\Users", File.ReadAllText(aside)); // the original is kept, not deleted
    }

    [Fact]
    public void Moves_the_old_data_folder_and_the_paths_that_pointed_into_it()
    {
        // ~/.codeatlas from before the rename: one repository Burndown downloaded, one folder the user added.
        var from = Path.Combine(_home, ".codeatlas");
        var downloaded = Path.Combine(from, "repos", "Karkaton-V", "Portfolio-Project");
        Directory.CreateDirectory(downloaded);
        var old = RepoRegistry.Load(from);
        old.Add(Repo("portfolio", downloaded, "Karkaton-V/Portfolio-Project"));
        old.Add(Repo("shop", Folder("shop")));
        old.Save();

        var to = Path.Combine(_home, ".burndown");
        RepoRegistry.MoveHome(from, to);

        Assert.False(Directory.Exists(from));
        var moved = RepoRegistry.Load(to);
        Assert.Equal(Path.Combine(to, "repos", "Karkaton-V", "Portfolio-Project"), moved.Find("portfolio")?.Path);
        Assert.True(Directory.Exists(moved.Find("portfolio")?.Path));
        Assert.Equal(Folder("shop"), moved.Find("shop")?.Path); // outside the data folder: unchanged
    }

    [Fact]
    public void Finds_repos_case_insensitively_with_or_without_the_at_sign()
    {
        var registry = RepoRegistry.Load(_home);
        registry.Add(Repo("Shop", Folder("shop")));

        Assert.NotNull(registry.Find("shop"));
        Assert.NotNull(registry.Find("@SHOP"));
        Assert.Null(registry.Find("store"));
    }

    [Fact]
    public void Refuses_duplicate_names_and_duplicate_folders()
    {
        var registry = RepoRegistry.Load(_home);
        registry.Add(Repo("shop", Folder("shop")));

        Assert.Throws<RepoRegistryException>(() => registry.Add(Repo("SHOP", Folder("other"))));
        // The same folder written with a trailing separator is still the same folder.
        var error = Assert.Throws<RepoRegistryException>(() => registry.Add(Repo("store", Folder("shop") + Path.DirectorySeparatorChar)));
        Assert.Contains("'shop'", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    public void Refuses_names_that_would_be_awkward_on_the_command_line(string name)
    {
        Assert.Throws<RepoRegistryException>(() => RepoRegistry.Load(_home).Add(Repo(name, Folder("x"))));
    }

    [Fact]
    public void Suggests_unique_command_line_friendly_names()
    {
        var registry = RepoRegistry.Load(_home);
        Assert.Equal("clean-architecture", registry.SuggestName("Clean Architecture"));

        registry.Add(Repo("clean-architecture", Folder("a")));
        Assert.Equal("clean-architecture-2", registry.SuggestName("Clean Architecture"));
        Assert.Equal("repo", registry.SuggestName("!!!"));
    }

    [Fact]
    public void Updates_and_removes_entries()
    {
        var registry = RepoRegistry.Load(_home);
        registry.Add(Repo("shop", Folder("shop")));

        var current = registry.Find("shop") ?? throw new InvalidOperationException("shop should be saved");
        registry.Update(current with { AnalyzedAt = DateTimeOffset.Parse("2026-10-24T13:00:00Z") });
        Assert.NotNull(registry.Find("shop")?.AnalyzedAt);

        Assert.True(registry.Remove("@shop"));
        Assert.False(registry.Remove("shop"));
        Assert.Empty(registry.Repos);
    }

    [Fact]
    public void Clones_into_an_owner_and_name_folder_under_its_home()
    {
        var registry = RepoRegistry.Load(_home);

        Assert.Equal(
            Path.Combine(_home, "repos", "jasontaylordev", "CleanArchitecture"),
            registry.CloneDirectoryFor(new GitHubRepoRef("jasontaylordev", "CleanArchitecture")));
    }

    [Fact]
    public void Explains_a_damaged_registry_file_instead_of_crashing()
    {
        File.WriteAllText(Path.Combine(_home, "repos.json"), "{ not json");

        var error = Assert.Throws<RepoRegistryException>(() => RepoRegistry.Load(_home));
        Assert.Contains("damaged", error.Message);
    }

    [Fact]
    public void Deletes_a_clone_even_when_git_made_its_files_read_only()
    {
        var clone = Folder("clone");
        Directory.CreateDirectory(Path.Combine(clone, ".git", "objects"));
        var objectFile = Path.Combine(clone, ".git", "objects", "pack");
        File.WriteAllText(objectFile, "x");
        File.SetAttributes(objectFile, FileAttributes.ReadOnly);

        Tools.DeleteClone(clone);

        Assert.False(Directory.Exists(clone));
    }
}
