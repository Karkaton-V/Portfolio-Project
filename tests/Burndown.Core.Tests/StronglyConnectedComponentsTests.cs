using Burndown.Core.Algorithms;

namespace Burndown.Core.Tests;

public class StronglyConnectedComponentsTests
{
    private static IReadOnlyList<string[]> Cycles(params (string From, string To)[] edges)
    {
        var successors = edges.ToLookup(e => e.From, e => e.To);
        var nodes = edges.SelectMany(e => new[] { e.From, e.To }).Distinct().Order();

        return StronglyConnectedComponents.FindCycles(nodes, n => successors[n])
            .Select(c => c.Order().ToArray())
            .OrderBy(c => c[0])
            .ToList();
    }

    [Fact]
    public void Finds_nothing_in_an_acyclic_graph()
    {
        Assert.Empty(Cycles(("A", "B"), ("B", "C"), ("A", "C")));
    }

    [Fact]
    public void Finds_a_two_node_cycle()
    {
        Assert.Equal([["A", "B"]], Cycles(("A", "B"), ("B", "A")));
    }

    [Fact]
    public void Finds_a_three_node_cycle_and_excludes_nodes_that_only_point_into_it()
    {
        Assert.Equal([["A", "B", "C"]], Cycles(("Entry", "A"), ("A", "B"), ("B", "C"), ("C", "A"), ("C", "Exit")));
    }

    [Fact]
    public void Reports_a_self_loop_as_a_cycle()
    {
        Assert.Equal([["A"]], Cycles(("A", "A"), ("A", "B")));
    }

    [Fact]
    public void Keeps_separate_cycles_separate()
    {
        Assert.Equal(
            [["A", "B"], ["C", "D", "E"]],
            Cycles(("A", "B"), ("B", "A"), ("B", "C"), ("C", "D"), ("D", "E"), ("E", "C")));
    }

    [Fact]
    public void Merges_overlapping_loops_into_one_component()
    {
        // A <-> B and B <-> C share B, so all three can reach each other.
        Assert.Equal([["A", "B", "C"]], Cycles(("A", "B"), ("B", "A"), ("B", "C"), ("C", "B")));
    }

    [Fact]
    public void Handles_very_long_chains_without_overflowing_the_stack()
    {
        // A recursive implementation would need 200,000 nested calls here.
        const int length = 200_000;
        var graph = TestGraph.Chain(length, closeLoop: true);
        var successors = graph.Edges.ToLookup(e => e.SourceId, e => e.TargetId);

        var cycle = Assert.Single(StronglyConnectedComponents.FindCycles(graph.Nodes.Select(n => n.Id), n => successors[n]));
        Assert.Equal(length, cycle.Count);
    }
}
