using CodeAtlas.Core.Algorithms;

namespace CodeAtlas.Core.Tests;

public class CycleDetectorTests
{
    [Fact]
    public void Finds_a_namespace_cycle_with_the_type_edges_that_cause_it()
    {
        var graph = TestGraph.Create(
            "Shop.Billing.Invoice -Property-> Shop.Orders.Order",
            "Shop.Orders.Order -Calls-> Shop.Billing.TaxCalculator",
            "Shop.Orders.Order -> Shop.Common.Money");

        var cycle = Assert.Single(CycleDetector.FindCycles(graph, CycleLevel.Namespace));

        Assert.Equal(["Shop.Billing", "Shop.Orders"], cycle.Members);
        Assert.Equal(["Shop.Billing", "Shop.Orders", "Shop.Billing"], cycle.ExamplePath);
        Assert.Equal(
            ["Shop.Billing.Invoice -> Shop.Orders.Order", "Shop.Orders.Order -> Shop.Billing.TaxCalculator"],
            cycle.Evidence.Select(e => $"{e.SourceId} -> {e.TargetId}").Order());
    }

    [Fact]
    public void Ignores_type_cycles_inside_a_single_namespace_at_namespace_level()
    {
        var graph = TestGraph.Create("Shop.Order -> Shop.OrderLine", "Shop.OrderLine -> Shop.Order");

        Assert.Empty(CycleDetector.FindCycles(graph, CycleLevel.Namespace));

        var typeCycle = Assert.Single(CycleDetector.FindCycles(graph, CycleLevel.Type));
        Assert.Equal(["Shop.Order", "Shop.OrderLine"], typeCycle.Members);
    }

    [Fact]
    public void Example_path_is_a_shortest_loop()
    {
        // A -> B -> C -> A is the only loop through A, even though A also reaches D.
        var graph = TestGraph.Create(
            "A.X -> B.X",
            "B.X -> C.X",
            "C.X -> A.X",
            "B.X -> D.X",
            "D.X -> C.X");

        var cycle = Assert.Single(CycleDetector.FindCycles(graph, CycleLevel.Namespace));

        Assert.Equal(["A", "B", "C", "D"], cycle.Members);
        Assert.Equal(["A", "B", "C", "A"], cycle.ExamplePath);
    }

    [Fact]
    public void Orders_larger_cycles_first()
    {
        var graph = TestGraph.Create(
            "P.X -> Q.X", "Q.X -> P.X",
            "A.X -> B.X", "B.X -> C.X", "C.X -> A.X");

        var cycles = CycleDetector.FindCycles(graph, CycleLevel.Namespace);

        Assert.Equal([3, 2], cycles.Select(c => c.Members.Count));
    }
}
