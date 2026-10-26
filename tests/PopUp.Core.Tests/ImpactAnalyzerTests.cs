using PopUp.Core.Algorithms;

namespace PopUp.Core.Tests;

public class ImpactAnalyzerTests
{
    // Api.OrdersController -> App.OrderService -> Data.IOrderRepository <- Data.SqlOrderRepository
    //                                          -> Common.Clock
    private static readonly DependencyGraph Shop = TestGraph.Create(
        "Api.OrdersController -ConstructorParameter-> App.OrderService",
        "App.OrderService -ConstructorParameter-> Data.IOrderRepository",
        "App.OrderService -Field-> Common.Clock",
        "Data.SqlOrderRepository -Implements-> Data.IOrderRepository",
        "Tests.OrderServiceTests -Instantiates-> App.OrderService");

    private static string[] Describe(IEnumerable<ImpactedType> impacted) =>
        impacted.Select(i => $"{i.NodeId} ({i.Distance})").ToArray();

    [Fact]
    public void Finds_direct_and_transitive_dependents_by_distance()
    {
        var impacted = ImpactAnalyzer.FindDependents(Shop, "Data.IOrderRepository");

        Assert.Equal(
            [
                "App.OrderService (1)",
                "Data.SqlOrderRepository (1)",
                "Api.OrdersController (2)",
                "Tests.OrderServiceTests (2)",
            ],
            Describe(impacted));
    }

    [Fact]
    public void Records_the_path_from_each_dependent_down_to_the_target()
    {
        var controller = ImpactAnalyzer.FindDependents(Shop, "Data.IOrderRepository")
            .Single(i => i.NodeId == "Api.OrdersController");

        Assert.Equal(
            ["Api.OrdersController -> App.OrderService", "App.OrderService -> Data.IOrderRepository"],
            controller.Path.Select(e => $"{e.SourceId} -> {e.TargetId}"));
    }

    [Fact]
    public void Excludes_the_target_and_things_it_merely_uses()
    {
        var impacted = Describe(ImpactAnalyzer.FindDependents(Shop, "App.OrderService"));

        Assert.DoesNotContain(impacted, i => i.StartsWith("App.OrderService"));
        Assert.DoesNotContain(impacted, i => i.StartsWith("Common.Clock"));
        Assert.DoesNotContain(impacted, i => i.StartsWith("Data."));
    }

    [Fact]
    public void Stops_at_the_maximum_depth()
    {
        var impacted = ImpactAnalyzer.FindDependents(Shop, "Data.IOrderRepository", maxDepth: 1);

        Assert.Equal(["App.OrderService (1)", "Data.SqlOrderRepository (1)"], Describe(impacted));
    }

    [Fact]
    public void Follows_only_the_requested_dependency_kinds()
    {
        var impacted = ImpactAnalyzer.FindDependents(
            Shop, "Data.IOrderRepository", kinds: new HashSet<DependencyKind> { DependencyKind.Implements });

        Assert.Equal(["Data.SqlOrderRepository (1)"], Describe(impacted));
    }

    [Fact]
    public void Terminates_when_dependents_form_a_cycle()
    {
        var graph = TestGraph.Create("A.X -> B.X", "B.X -> A.X", "C.X -> A.X", "A.X -> Target.X");

        var impacted = ImpactAnalyzer.FindDependents(graph, "Target.X");

        Assert.Equal(["A.X (1)", "B.X (2)", "C.X (2)"], Describe(impacted));
    }

    [Fact]
    public void Rejects_an_unknown_target()
    {
        Assert.Throws<ArgumentException>(() => ImpactAnalyzer.FindDependents(Shop, "Nope.Missing"));
    }
}
