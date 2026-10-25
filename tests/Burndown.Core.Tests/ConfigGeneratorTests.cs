using Burndown.Core.Rules;

namespace Burndown.Core.Tests;

public class ConfigGeneratorTests
{
    // A layered solution with conventional names, one test project, and one project that fits no convention.
    private static readonly DependencyGraph Layered = TestGraph.Create(
        "Shop.Web:Shop.Web.OrdersController -> Shop.Application:Shop.Application.OrderService",
        "Shop.Application:Shop.Application.OrderService -> Shop.Domain:Shop.Domain.Order",
        "Shop.Infrastructure:Shop.Infrastructure.OrderRepository -> Shop.Application:Shop.Application.IOrderRepository",
        "Shop.UnitTests:Shop.UnitTests.OrderServiceTests -> Shop.Application:Shop.Application.OrderService",
        "Shop.Tools:Shop.Tools.Seeder");

    [Theory]
    [InlineData("Shop.Domain", "Domain")]
    [InlineData("Shop.Core", "Domain")]
    [InlineData("Shop.Application", "Application")]
    [InlineData("Shop.App", "Application")]
    [InlineData("Shop.Persistence", "Infrastructure")]
    [InlineData("Shop.Web", "Presentation")]
    [InlineData("Shop.Api", "Presentation")]
    [InlineData("Shop.Web.Infrastructure", "Infrastructure")] // the most specific (last) part wins
    [InlineData("Shop.Tools", null)]
    public void Guesses_layers_from_project_names(string project, string? layer)
    {
        Assert.Equal(layer, ConfigGenerator.LayerOf(project));
    }

    [Fact]
    public void Writes_a_config_that_parses_and_matches_the_solution()
    {
        var generated = ConfigGenerator.Generate(Layered, "Shop");
        var config = ArchitectureConfig.Parse(generated.Json); // throws if the generated JSON is invalid

        Assert.Equal(
            ["Domain", "Application", "Infrastructure", "Presentation", "Tests", "Production"],
            config.Layers.Keys);
        Assert.Equal(["project:Shop.UnitTests"], config.Layers["Tests"]);
        Assert.Equal(["Shop.Tools"], generated.Unassigned);
        Assert.Equal(RuleSeverity.Warning, config.NamespaceCycles);
        Assert.Equal("Shop", config.Report.Title);
    }

    [Fact]
    public void Writes_the_usual_layering_rules_for_the_layers_that_exist()
    {
        var config = ArchitectureConfig.Parse(ConfigGenerator.Generate(Layered, "Shop").Json);

        Assert.Equal(
            [
                "Domain depends on nothing else",
                "Application depends only on Domain",
                "Infrastructure does not depend on Presentation",
                "Production code does not depend on tests",
            ],
            config.Rules.Select(r => r.Name));

        // A well-layered solution passes its own generated rules.
        Assert.Equal(0, ArchitectureChecker.Check(Layered, config).ErrorCount);
    }

    [Fact]
    public void Catches_a_layering_violation_in_a_solution_that_has_one()
    {
        var tangled = TestGraph.Create(
            "Shop.Domain:Shop.Domain.Order -> Shop.Web:Shop.Web.OrdersController",
            "Shop.Web:Shop.Web.OrdersController -> Shop.Domain:Shop.Domain.Order");

        var config = ArchitectureConfig.Parse(ConfigGenerator.Generate(tangled, "Shop").Json);

        var violation = Assert.Single(ArchitectureChecker.Check(tangled, config).Violations, v => v.RuleId == "domain-depends-on-nothing-else");
        Assert.Contains("Shop.Web.OrdersController", violation.Message);
    }

    [Fact]
    public void Still_writes_a_valid_config_when_no_layers_are_recognized()
    {
        var generated = ConfigGenerator.Generate(TestGraph.Create("Acme.Widgets.Gear -> Acme.Widgets.Cog"), "Acme");
        var config = ArchitectureConfig.Parse(generated.Json);

        Assert.Empty(config.Rules);
        Assert.Equal(0, generated.RuleCount);
        Assert.Contains("No layers were recognized", generated.Json);
    }
}
