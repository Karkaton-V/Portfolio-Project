namespace Burndown.Analysis.Tests;

public sealed class ConfigLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("burndown-config-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _root);
        File.WriteAllText(path, "{}");
        return path;
    }

    [Fact]
    public void Finds_the_config_in_a_parent_folder_up_to_the_repository_root()
    {
        Directory.CreateDirectory(Path.Combine(_root, "repo", ".git"));
        var config = Touch("repo/burndown.json");
        Touch("burndown.json"); // outside the repository: never used

        Assert.Equal(config, ConfigLocator.Find(Path.Combine(_root, "repo", "src", "App")));
        Directory.CreateDirectory(Path.Combine(_root, "other", ".git"));
        Assert.Null(ConfigLocator.Find(Path.Combine(_root, "other")));
    }

    [Fact]
    public void Still_reads_a_config_with_the_name_from_before_the_rename()
    {
        var legacy = Touch("old/codeatlas.json");
        Assert.Equal(legacy, ConfigLocator.Find(Path.Combine(_root, "old")));

        var current = Touch("old/burndown.json");
        Assert.Equal(current, ConfigLocator.Find(Path.Combine(_root, "old"))); // the new name wins when both exist
    }
}
