namespace Burndown.Core.Tests;

public class ProjectFilterTests
{
    [Theory]
    [InlineData("*Tests", "Shop.Tests", false)]
    [InlineData("*Tests", "Shop.UnitTests", false)]
    [InlineData("*tests", "Shop.IntegrationTests", false)] // case-insensitive
    [InlineData("*Tests", "Shop.Domain", true)]
    [InlineData("Samples.*", "Samples.Basic", false)]
    [InlineData("Samples.*", "Shop.Samples", true)]
    [InlineData("Shop.Api", "Shop.Api", false)]
    [InlineData("Shop.Api", "Shop.Api2", true)] // whole-name match, not a prefix
    [InlineData("Shop.Ap?", "Shop.Api", false)]
    public void Matches_project_names_with_simple_globs(string pattern, string project, bool included)
    {
        Assert.Equal(included, new ProjectFilter([pattern]).Includes(project));
    }

    [Fact]
    public void Removes_excluded_projects_and_every_edge_that_touches_them()
    {
        var graph = TestGraph.Create(
            "App:Shop.OrderService -> Domain:Shop.Order",
            "Tests:Shop.OrderServiceTests -> App:Shop.OrderService",
            "Tests:Shop.OrderServiceTests -> Domain:Shop.Order");

        var filtered = new ProjectFilter(["Tests"]).Apply(graph);

        Assert.Equal(["Shop.Order", "Shop.OrderService"], filtered.Nodes.Select(n => n.Id).Order());
        var edge = Assert.Single(filtered.Edges);
        Assert.Equal(("Shop.OrderService", "Shop.Order"), (edge.SourceId, edge.TargetId));
    }

    [Fact]
    public void Ignores_blank_patterns_and_returns_the_graph_unchanged_when_there_are_none()
    {
        var graph = TestGraph.Create("A.X -> B.Y");
        var filter = new ProjectFilter([" ", ""]);

        Assert.Empty(filter.Patterns);
        Assert.Same(graph, filter.Apply(graph));
    }
}
