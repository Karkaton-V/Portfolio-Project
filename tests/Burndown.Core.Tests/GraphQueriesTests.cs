namespace Burndown.Core.Tests;

public class GraphQueriesTests
{
    private static readonly DependencyGraph Graph = TestGraph.Create(
        "Shop.Orders.Order",
        "Shop.Orders.OrderLine",
        "Shop.Sales.Customer",
        "Shop.Support.Customer");

    private static string[] Find(string query) => Graph.FindTypes(query).Select(n => n.FullName).ToArray();

    [Fact]
    public void Matches_a_full_name() => Assert.Equal(["Shop.Orders.Order"], Find("Shop.Orders.Order"));

    [Fact]
    public void Prefers_an_exact_simple_name_over_a_longer_one() => Assert.Equal(["Shop.Orders.Order"], Find("Order"));

    [Fact]
    public void Matches_a_namespace_suffix() => Assert.Equal(["Shop.Sales.Customer"], Find("Sales.Customer"));

    [Fact]
    public void Returns_every_candidate_when_ambiguous() =>
        Assert.Equal(["Shop.Sales.Customer", "Shop.Support.Customer"], Find("Customer"));

    [Fact]
    public void Returns_nothing_for_unknown_names() => Assert.Empty(Find("Invoice"));

    [Fact]
    public void Matches_generic_types_without_their_type_parameters()
    {
        var graph = new DependencyGraph
        {
            Nodes = [new CodeNode("App:Data.Repository`1", "Repository<T>", "Data.Repository<T>", "Data", "App", NodeKind.Class, [])],
            Edges = [],
        };

        Assert.Single(graph.FindTypes("Repository"));
        Assert.Single(graph.FindTypes("Data.Repository"));
    }
}
