using Burndown.Core.Algorithms;

namespace Burndown.Core.Tests;

public class CouplingMetricsTests
{
    [Fact]
    public void Counts_distinct_incoming_and_outgoing_types()
    {
        var graph = TestGraph.Create(
            "App.OrderService -ConstructorParameter-> Data.IOrderRepository",
            "App.OrderService -Calls-> Data.IOrderRepository", // same pair, counted once
            "App.OrderService -Field-> Common.Clock",
            "Api.OrdersController -> App.OrderService",
            "Data.SqlOrderRepository -Implements-> Data.IOrderRepository");

        var metrics = CouplingMetrics.Calculate(graph).ToDictionary(m => m.NodeId);

        Assert.Equal(new TypeCoupling("App.OrderService", 1, 2, 0.67), metrics["App.OrderService"]);
        Assert.Equal(new TypeCoupling("Data.IOrderRepository", 2, 0, 0), metrics["Data.IOrderRepository"]);
        Assert.Equal(new TypeCoupling("Api.OrdersController", 0, 1, 1), metrics["Api.OrdersController"]);
    }

    [Fact]
    public void Lists_the_most_connected_types_first_and_includes_isolated_ones()
    {
        var graph = TestGraph.Create("A.Hub -> B.X", "C.Y -> A.Hub", "D.Lonely");

        var metrics = CouplingMetrics.Calculate(graph);

        Assert.Equal("A.Hub", metrics[0].NodeId);
        Assert.Contains(new TypeCoupling("D.Lonely", 0, 0, 0), metrics);
    }
}
