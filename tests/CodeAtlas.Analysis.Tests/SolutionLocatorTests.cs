namespace CodeAtlas.Analysis.Tests;

/// <summary>Each test builds a small folder tree in a fresh temp directory and deletes it afterwards.</summary>
public sealed class SolutionLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("burndown-locator-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _root);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Returns_a_solution_or_project_file_unchanged()
    {
        var solution = Touch("App.sln");
        var project = Touch("Lib/Lib.csproj");

        Assert.Equal(solution, SolutionLocator.Resolve(solution));
        Assert.Equal(project, SolutionLocator.Resolve(project));
    }

    [Fact]
    public void Finds_the_solution_in_a_folder()
    {
        var solution = Touch("App.sln");
        Touch("src/App/App.csproj");

        Assert.Equal(solution, SolutionLocator.Resolve(_root));
    }

    [Fact]
    public void Prefers_slnx_when_both_formats_of_the_same_solution_exist()
    {
        Touch("App.sln");
        var slnx = Touch("App.slnx");

        Assert.Equal(slnx, SolutionLocator.Resolve(_root));
    }

    [Fact]
    public void Falls_back_to_a_single_project_in_the_folder()
    {
        var project = Touch("Tool.csproj");

        Assert.Equal(project, SolutionLocator.Resolve(_root));
    }

    [Fact]
    public void Looks_a_few_levels_down_when_the_folder_has_no_solution()
    {
        var solution = Touch("src/Shop/Shop.sln");
        Touch("src/Shop/bin/Debug/Copy.sln"); // build output is never searched

        Assert.Equal(solution, SolutionLocator.Resolve(_root));
    }

    [Fact]
    public void Lists_the_candidates_when_the_choice_is_ambiguous()
    {
        Touch("Api.sln");
        Touch("Worker.sln");

        var error = Assert.Throws<SolutionNotFoundException>(() => SolutionLocator.Resolve(_root));
        Assert.Contains("Api.sln", error.Message);
        Assert.Contains("Worker.sln", error.Message);
    }

    [Fact]
    public void Explains_when_nothing_can_be_analyzed()
    {
        Touch("README.md");

        Assert.Throws<SolutionNotFoundException>(() => SolutionLocator.Resolve(_root));
        Assert.Throws<SolutionNotFoundException>(() => SolutionLocator.Resolve(Path.Combine(_root, "README.md")));
        Assert.Throws<SolutionNotFoundException>(() => SolutionLocator.Resolve(Path.Combine(_root, "missing")));
    }
}
