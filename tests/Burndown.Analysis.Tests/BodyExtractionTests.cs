namespace Burndown.Analysis.Tests;

public class BodyExtractionTests
{
    [Fact]
    public void Finds_method_signature_dependencies()
    {
        var graph = TestCompilation.Analyze("""
            namespace Shop;
            public class Order { }
            public class Receipt { }
            public class Checkout
            {
                public Receipt Pay(Order order) => throw null!;
            }
            """);

        Assert.Equal(
            ["MethodSignature -> Shop.Order", "MethodSignature -> Shop.Receipt"],
            graph.EdgesFrom("Shop.Checkout"));
    }

    [Fact]
    public void Finds_instantiations_without_also_counting_them_as_references()
    {
        var graph = TestCompilation.Analyze("""
            namespace Shop;
            public class Order { }
            public class Factory
            {
                public void Build()
                {
                    var a = new Order();
                    var b = new Shop.Order();
                    Order c = new();
                }
            }
            """);

        // `var` and the declared type `Order c` are references; the three `new` expressions are one edge.
        Assert.Equal(["Instantiates -> Shop.Order", "References -> Shop.Order"], graph.EdgesFrom("Shop.Factory"));
    }

    [Fact]
    public void Attributes_calls_to_the_type_that_declares_the_method()
    {
        var graph = TestCompilation.Analyze("""
            namespace Shop;
            public class Order { }
            public interface IOrderRepository { Order Get(int id); }
            public static class AuditLog { public static void Write(string message) { } }
            public static class OrderExtensions { public static string Describe(this Order order) => ""; }
            public class OrderService
            {
                private readonly IOrderRepository _repository;
                public OrderService(IOrderRepository repository) => _repository = repository;

                public string Describe(int id)
                {
                    AuditLog.Write("loading");
                    return _repository.Get(id).Describe();
                }
            }
            """);

        var edges = graph.EdgesFrom("Shop.OrderService");
        Assert.Contains("Calls -> Shop.IOrderRepository", edges);
        Assert.Contains("Calls -> Shop.AuditLog", edges);
        // Describe() looks like a method on Order but is a static call on the extension class.
        Assert.Contains("Calls -> Shop.OrderExtensions", edges);
        Assert.DoesNotContain("Calls -> Shop.Order", edges);
    }

    [Fact]
    public void Finds_type_references_in_casts_typeof_static_members_and_inferred_locals()
    {
        var graph = TestCompilation.Analyze("""
            using System;
            namespace Shop;
            public class Order { }
            public class Invoice { }
            public class Settings { public static int Retries = 3; }
            public enum Status { Open }
            public class OrderSource { public static Order Next() => new(); }
            public class Worker
            {
                public void Run(object input)
                {
                    var invoice = (Invoice)input;
                    Type type = typeof(Status);
                    int retries = Settings.Retries;
                    var next = OrderSource.Next();
                }
            }
            """);

        Assert.Equal(
            [
                "Calls -> Shop.OrderSource", // a static call is a Calls edge, not also a reference
                "References -> Shop.Invoice",
                "References -> Shop.Order", // inferred from `var next`, never written in the source
                "References -> Shop.Settings",
                "References -> Shop.Status",
            ],
            graph.EdgesFrom("Shop.Worker"));
    }

    [Fact]
    public void Treats_open_generic_typeof_as_a_reference_not_an_unresolved_type()
    {
        // The DI-registration pattern that produced a false warning on jasontaylordev/CleanArchitecture.
        var graph = TestCompilation.Analyze("""
            using System;
            namespace Shop;
            public class LoggingBehaviour<TRequest, TResponse> { }
            public static class DependencyInjection
            {
                public static Type Behaviour() => typeof(LoggingBehaviour<,>);
            }
            """);

        Assert.Equal(["References -> Shop.LoggingBehaviour<TRequest, TResponse>"], graph.EdgesFrom("Shop.DependencyInjection"));
        Assert.Empty(graph.Warnings);
    }

    [Fact]
    public void Looks_inside_lambdas_initializers_and_accessors()
    {
        var graph = TestCompilation.Analyze("""
            using System;
            namespace Shop;
            public class Order { }
            public class Invoice { }
            public class Receipt { }
            public class Cart
            {
                private readonly Func<Order> _factory = () => new Order();
                public Invoice Current { get { return new Invoice(); } }
                public Receipt Last => new Receipt();
            }
            """);

        var instantiations = graph.EdgesFrom("Shop.Cart").Where(e => e.StartsWith("Instantiates")).ToArray();
        Assert.Equal(
            ["Instantiates -> Shop.Invoice", "Instantiates -> Shop.Order", "Instantiates -> Shop.Receipt"],
            instantiations);
    }

    [Fact]
    public void Attributes_nested_type_bodies_to_the_nested_type()
    {
        var graph = TestCompilation.Analyze("""
            namespace Shop;
            public class Order { }
            public class Outer
            {
                public class Inner
                {
                    public void Make() => new Order();
                }
            }
            """);

        Assert.Empty(graph.EdgesFrom("Shop.Outer"));
        Assert.Equal(["Instantiates -> Shop.Order"], graph.EdgesFrom("Shop.Outer.Inner"));
    }

    [Fact]
    public void Reads_bodies_from_every_partial_declaration()
    {
        var graph = TestCompilation.Analyze(
            "namespace Shop; public class Order { } public class Invoice { }",
            "namespace Shop; public partial class Cart { public void A() => new Order(); }",
            "namespace Shop; public partial class Cart { public void B() => new Invoice(); }");

        Assert.Equal(
            ["Instantiates -> Shop.Invoice", "Instantiates -> Shop.Order"],
            graph.EdgesFrom("Shop.Cart"));
    }
}
