using Burndown.Core;

namespace Burndown.Analysis.Tests;

public class GraphJsonTests
{
    [Fact]
    public void Round_trips_a_graph()
    {
        var original = TestCompilation.Analyze("""
            namespace Shop;
            public interface IOrderRepository { }
            public class OrderService(IOrderRepository repository) { }
            """);

        var copy = GraphJson.Deserialize(GraphJson.Serialize(original));

        Assert.Equal(original.Nodes.Select(n => (n.Id, n.Kind)), copy.Nodes.Select(n => (n.Id, n.Kind)));
        Assert.Equal(original.Edges, copy.Edges);
    }

    [Fact]
    public void Writes_camel_case_names_and_enums_as_strings()
    {
        var json = GraphJson.Serialize(TestCompilation.Analyze("""
            namespace Shop;
            public class Animal { }
            public class Dog : Animal { }
            """));

        Assert.Contains("\"sourceId\":", json);
        Assert.Contains("\"kind\": \"Inherits\"", json);
    }

    [Fact]
    public void Ordering_does_not_depend_on_source_file_order()
    {
        string[] sources =
        [
            "namespace B; public class Second { public A.First? Link { get; set; } }",
            "namespace A; public class First { public B.Second? Link { get; set; } }",
        ];

        var forward = TestCompilation.Analyze(sources);
        // Enumerable.Reverse explicitly: from C# 14, array.Reverse() binds to the in-place, void Span.Reverse().
        var reversed = TestCompilation.Analyze([.. Enumerable.Reverse(sources)]);

        // File paths differ between the runs (File0 vs File1), so compare identities only.
        Assert.Equal(forward.Nodes.Select(n => n.Id), reversed.Nodes.Select(n => n.Id));
        Assert.Equal(
            forward.Edges.Select(e => (e.SourceId, e.TargetId, e.Kind)),
            reversed.Edges.Select(e => (e.SourceId, e.TargetId, e.Kind)));
    }
}
