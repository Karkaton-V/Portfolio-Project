using PopUp.Core.Rules;

namespace PopUp.Core.Tests;

public class ArchitectureCheckerTests
{
    private static CheckResult Check(DependencyGraph graph, string configJson) =>
        ArchitectureChecker.Check(graph, ArchitectureConfig.Parse(configJson));

    private static readonly DependencyGraph Layered = TestGraph.Create(
        "Shop.Api.OrdersController -ConstructorParameter-> Shop.App.OrderService",
        "Shop.App.OrderService -ConstructorParameter-> Shop.Domain.IOrderRepository",
        "Shop.App.OrderService -Calls-> Shop.Api.ApiErrors", // the violation: App reaching up into Api
        "Shop.App.OrderService -References-> Shop.Api.ApiErrors",
        "Shop.Domain.Order -Property-> Shop.Domain.Money");

    [Fact]
    public void Reports_forbidden_dependencies_once_per_pair_of_types()
    {
        var result = Check(Layered, """
            {
              "layers": { "App": ["Shop.App.**"], "Api": ["Shop.Api.**"] },
              "rules": [{ "name": "App must not use Api", "from": "App", "forbid": ["Api"] }]
            }
            """);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("app-must-not-use-api", violation.RuleId);
        Assert.Equal("Shop.App.OrderService depends on Shop.Api.ApiErrors (Calls, References).", violation.Message);
        Assert.Equal(new SourceLocation("Shop.App.OrderService.cs", 3), violation.Location);
        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public void May_only_depend_on_allows_the_layer_itself_and_the_listed_layers()
    {
        var result = Check(Layered, """
            {
              "layers": { "Domain": ["Shop.Domain.**"], "App": ["Shop.App.**"] },
              "rules": [
                { "name": "Domain is self-contained", "from": "Domain", "mayOnlyDependOn": [] },
                { "name": "App uses only Domain", "from": "App", "mayOnlyDependOn": ["Domain"] }
              ]
            }
            """);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("app-uses-only-domain", violation.RuleId);
        Assert.Contains("Shop.Api.ApiErrors", violation.Message);
    }

    [Fact]
    public void Checks_only_the_listed_dependency_kinds()
    {
        var result = Check(Layered, """
            { "rules": [{ "name": "R", "from": "Shop.App", "forbid": ["Shop.Api"], "kinds": ["Instantiates"] }] }
            """);

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Selects_types_by_project()
    {
        var graph = TestGraph.Create(
            "Shop.Domain:Shop.Orders.Order -> Shop.Web:Shop.Orders.OrderPage",
            "Shop.Web:Shop.Orders.OrderPage -> Shop.Domain:Shop.Orders.Order");

        var result = Check(graph, """
            { "rules": [{ "name": "R", "from": "project:Shop.Domain", "forbid": ["project:Shop.Web"] }] }
            """);

        var violation = Assert.Single(result.Violations);
        Assert.StartsWith("Shop.Orders.Order depends on Shop.Orders.OrderPage", violation.Message);
    }

    [Fact]
    public void Warns_about_selectors_that_match_nothing()
    {
        var result = Check(Layered, """
            { "rules": [{ "name": "Typo", "from": "Shop.Domian.**", "forbid": ["Shop.Api.**"] }] }
            """);

        Assert.Empty(result.Violations);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("'Shop.Domian.**'", warning);
    }

    [Fact]
    public void Reports_namespace_cycles_when_enabled()
    {
        var graph = TestGraph.Create(
            "Shop.Billing.Invoice -Property-> Shop.Orders.Order",
            "Shop.Orders.Order -Calls-> Shop.Billing.TaxCalculator");

        var off = Check(graph, "{}");
        var on = Check(graph, """{ "namespaceCycles": "warning" }""");

        Assert.Empty(off.Violations);
        var violation = Assert.Single(on.Violations);
        Assert.Equal(ArchitectureChecker.NamespaceCycleRuleId, violation.RuleId);
        Assert.Equal(RuleSeverity.Warning, violation.Severity);
        Assert.StartsWith("Namespace cycle: Shop.Billing -> Shop.Orders -> Shop.Billing.", violation.Message);
        Assert.Equal(new SourceLocation("Shop.Billing.Invoice.cs", 1), violation.Location);
        Assert.Equal((0, 1), (on.ErrorCount, on.WarningCount));
    }

    [Fact]
    public void Lists_every_rule_even_without_violations()
    {
        var clean = TestGraph.Create("Shop.Api.OrdersController -> Shop.Domain.Order");

        var result = Check(clean, """
            {
              "rules": [{ "name": "Domain alone", "from": "Shop.Domain", "forbid": ["Shop.Api"], "severity": "warning" }],
              "namespaceCycles": "error"
            }
            """);

        Assert.Empty(result.Violations);
        Assert.Equal(["domain-alone", "namespace-cycle"], result.Rules.Select(r => r.Id));
        Assert.Equal("Types in Shop.Domain must not depend on Shop.Api.", result.Rules[0].Description);
    }
}
