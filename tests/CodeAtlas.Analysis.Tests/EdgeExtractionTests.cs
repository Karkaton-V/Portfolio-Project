using CodeAtlas.Core;

namespace CodeAtlas.Analysis.Tests;

public class EdgeExtractionTests
{
    [Fact]
    public void Separates_inheritance_from_interface_implementation()
    {
        var graph = TestCompilation.Analyze("""
            namespace Zoo;
            public interface INamed { }
            public interface IAnimal : INamed { }
            public abstract class Animal { }
            public class Dog : Animal, IAnimal { }
            """);

        Assert.Equal(["Implements -> Zoo.IAnimal", "Inherits -> Zoo.Animal"], graph.EdgesFrom("Zoo.Dog"));
        Assert.Equal(["Inherits -> Zoo.INamed"], graph.EdgesFrom("Zoo.IAnimal"));
    }

    [Fact]
    public void Finds_constructor_field_and_property_dependencies()
    {
        var graph = TestCompilation.Analyze("""
            namespace Shop;
            public interface IOrderRepository { }
            public class Clock { }
            public class Order { }
            public class OrderService
            {
                private readonly IOrderRepository _repository;
                public OrderService(IOrderRepository repository, Clock clock) => _repository = repository;
                public Order? LastOrder { get; set; }
            }
            """);

        Assert.Equal(
            [
                "ConstructorParameter -> Shop.Clock",
                "ConstructorParameter -> Shop.IOrderRepository",
                "Field -> Shop.IOrderRepository",
                "Property -> Shop.Order",
            ],
            graph.EdgesFrom("Shop.OrderService"));
    }

    [Fact]
    public void Looks_inside_generic_arguments_and_arrays()
    {
        var graph = TestCompilation.Analyze("""
            using System.Collections.Generic;
            namespace Shop;
            public class Order { }
            public class Customer { }
            public class Invoice { }
            public interface IRepository<T> { }
            public class Report
            {
                public Report(IRepository<Order> orders) { }
                public Dictionary<string, List<Customer>> ByRegion { get; } = new();
                public Invoice[] Invoices = [];
            }
            """);

        Assert.Equal(
            [
                "ConstructorParameter -> Shop.IRepository<T>",
                "ConstructorParameter -> Shop.Order",
                "Field -> Shop.Invoice",
                "Property -> Shop.Customer",
            ],
            graph.EdgesFrom("Shop.Report"));
    }

    [Fact]
    public void Ignores_framework_types_and_self_references()
    {
        var graph = TestCompilation.Analyze("""
            using System;
            using System.Collections.Generic;
            namespace Shop;
            public class Node
            {
                public string Name { get; set; } = "";
                public List<Node> Children { get; } = new();
                public Node(DateTime created, Node? parent) { }
            }
            """);

        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void Does_not_double_count_compiler_generated_backing_fields()
    {
        var graph = TestCompilation.Analyze("""
            namespace Shop;
            public class Order { }
            public class Cart
            {
                public Order? Current { get; set; }
            }
            """);

        Assert.Equal(["Property -> Shop.Order"], graph.EdgesFrom("Shop.Cart"));
    }

    [Fact]
    public void Collapses_repeated_references_into_one_edge_per_kind()
    {
        var graph = TestCompilation.Analyze("""
            namespace Shop;
            public class Order { }
            public class Cart
            {
                private Order? _first;
                private Order? _second;
            }
            """);

        var edge = Assert.Single(graph.Edges);
        Assert.Equal(DependencyKind.Field, edge.Kind);
        Assert.Equal(new SourceLocation("App/File0.cs", 5), edge.Location);
    }

    [Fact]
    public void Connects_types_across_projects()
    {
        var domain = TestCompilation.Create("Shop.Domain", ["namespace Shop.Domain; public class Order { }"]);
        var api = TestCompilation.Create(
            "Shop.Api",
            ["namespace Shop.Api; public class OrdersController { public OrdersController(Shop.Domain.Order order) { } }"],
            domain.ToMetadataReference());

        // Add the referencing project first: the edge must survive even though its target
        // isn't known until the second project is added.
        var builder = new GraphBuilder();
        builder.AddCompilation(api);
        builder.AddCompilation(domain);
        var graph = builder.Build();

        var edge = Assert.Single(graph.Edges);
        Assert.Equal("Shop.Api:Shop.Api.OrdersController", edge.SourceId);
        Assert.Equal("Shop.Domain:Shop.Domain.Order", edge.TargetId);
    }

    [Fact]
    public void Reports_unresolved_types_instead_of_inventing_edges()
    {
        var graph = TestCompilation.Analyze("""
            namespace Shop;
            public class OrderService
            {
                public OrderService(IMissingRepository repository) { }
            }
            """);

        Assert.Empty(graph.Edges);
        var warning = Assert.Single(graph.Warnings);
        Assert.Contains("IMissingRepository", warning);
    }
}
