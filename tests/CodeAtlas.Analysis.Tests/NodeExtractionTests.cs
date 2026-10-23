using CodeAtlas.Core;

namespace CodeAtlas.Analysis.Tests;

public class NodeExtractionTests
{
    [Fact]
    public void Finds_every_kind_of_type_declaration()
    {
        var graph = TestCompilation.Analyze("""
            namespace Shop;
            public class Order { }
            public interface IOrderRepository { }
            public struct Money { }
            public record Customer(string Name);
            public record struct Point(int X, int Y);
            public enum OrderStatus { Open, Closed }
            public delegate void OrderPlaced(Order order);
            """);

        var kinds = graph.Nodes.ToDictionary(n => n.FullName, n => n.Kind);
        Assert.Equal(NodeKind.Class, kinds["Shop.Order"]);
        Assert.Equal(NodeKind.Interface, kinds["Shop.IOrderRepository"]);
        Assert.Equal(NodeKind.Struct, kinds["Shop.Money"]);
        Assert.Equal(NodeKind.Record, kinds["Shop.Customer"]);
        Assert.Equal(NodeKind.RecordStruct, kinds["Shop.Point"]);
        Assert.Equal(NodeKind.Enum, kinds["Shop.OrderStatus"]);
        Assert.Equal(NodeKind.Delegate, kinds["Shop.OrderPlaced"]);
        Assert.Equal(7, graph.Nodes.Count);
    }

    [Fact]
    public void Merges_partial_declarations_into_one_node_with_every_location()
    {
        var graph = TestCompilation.Analyze(
            "namespace Shop; public partial class Order { }",
            "namespace Shop;\n\npublic partial class Order { }");

        var order = Assert.Single(graph.Nodes);
        Assert.Equal(
            [new SourceLocation("App/File0.cs", 1), new SourceLocation("App/File1.cs", 3)],
            order.Locations.OrderBy(l => l.FilePath));
    }

    [Fact]
    public void Keeps_same_named_types_in_different_namespaces_apart()
    {
        var graph = TestCompilation.Analyze(
            "namespace Sales { public class Customer { } }",
            "namespace Support { public class Customer { } }");

        Assert.Equal(2, graph.Nodes.Select(n => n.Id).Distinct().Count());
        Assert.All(graph.Nodes, n => Assert.Equal("Customer", n.Name));
    }

    [Fact]
    public void Keeps_generic_and_non_generic_types_with_the_same_name_apart()
    {
        var graph = TestCompilation.Analyze("""
            namespace Data;
            public class Repository { }
            public class Repository<T> { }
            """);

        Assert.Equal(
            ["App:Data.Repository", "App:Data.Repository`1"],
            graph.Nodes.Select(n => n.Id));
        Assert.Equal("Repository<T>", graph.Node("Data.Repository<T>").Name);
    }

    [Fact]
    public void Includes_nested_types()
    {
        var graph = TestCompilation.Analyze("""
            namespace Shop;
            public class Order
            {
                public class Line { }
            }
            """);

        Assert.Contains(graph.Nodes, n => n.FullName == "Shop.Order.Line");
    }

    [Fact]
    public void Records_namespace_and_project()
    {
        var graph = TestCompilation.Analyze("namespace Shop.Domain; public class Order { }");

        var order = graph.Node("Shop.Domain.Order");
        Assert.Equal("Shop.Domain", order.Namespace);
        Assert.Equal("App", order.Project);
    }
}
