using Burndown.Core.Rules;

namespace Burndown.Core.Tests;

public class ArchitectureConfigTests
{
    [Fact]
    public void Parses_a_hand_written_config_with_comments_and_trailing_commas()
    {
        var config = ArchitectureConfig.Parse("""
            {
              // Layers name groups of namespaces
              "layers": { "Domain": ["Shop.Domain.**"], },
              "rules": [
                { "name": "Domain stays pure", "from": "Domain", "mayOnlyDependOn": [], "kinds": ["Calls", "instantiates"] },
                { "id": "no-api", "name": "No API", "from": "Shop.App.**", "forbid": ["Shop.Api.**"], "severity": "warning" },
              ],
              "namespaceCycles": "error",
            }
            """);

        Assert.Equal(["Shop.Domain.**"], config.Layers["Domain"]);
        Assert.Equal(["domain-stays-pure", "no-api"], config.Rules.Select(r => r.RuleId));
        Assert.Equal([DependencyKind.Calls, DependencyKind.Instantiates], config.Rules[0].Kinds);
        Assert.Equal(RuleSeverity.Error, config.Rules[0].Severity);
        Assert.Equal(RuleSeverity.Warning, config.Rules[1].Severity);
        Assert.Equal(RuleSeverity.Error, config.NamespaceCycles);
    }

    [Fact]
    public void Leaves_cycle_checking_off_by_default()
    {
        Assert.Null(ArchitectureConfig.Parse("{}").NamespaceCycles);
    }

    [Fact]
    public void Reads_exclude_patterns_and_report_settings()
    {
        var config = ArchitectureConfig.Parse("""
            { "exclude": ["*Tests", "Samples.*"], "report": { "title": "Shop", "theme": "Nord" } }
            """);

        Assert.Equal(["*Tests", "Samples.*"], config.Exclude);
        Assert.Equal("Shop", config.Report.Title);
        Assert.Equal("Nord", config.Report.Theme); // theme names are case-insensitive
    }

    [Fact]
    public void Rejects_an_unknown_report_theme_and_lists_the_choices()
    {
        var error = Assert.Throws<ArchitectureConfigException>(() =>
            ArchitectureConfig.Parse("""{ "report": { "theme": "neon" } }"""));

        Assert.Contains("'neon'", error.Message);
        Assert.Contains("solarized", error.Message);
    }

    [Theory]
    [InlineData("""{ "rules": [{ "name": "R", "from": "A" }] }""", "exactly one of")]
    [InlineData("""{ "rules": [{ "name": "R", "from": "A", "forbid": ["B"], "mayOnlyDependOn": [] }] }""", "exactly one of")]
    [InlineData("""{ "rules": [{ "name": "R", "from": "A", "forbid": [] }] }""", "empty 'forbid'")]
    [InlineData("""{ "rules": [{ "name": "R", "from": "A", "forbidden": ["B"] }] }""", "forbidden")]
    [InlineData("""{ "rules": [{ "from": "A", "forbid": ["B"] }] }""", "name")]
    [InlineData("""{ "rules": [{ "name": "R", "from": "A", "forbid": ["B"], "severity": "fatal" }] }""", "severity")]
    [InlineData("""{ "layers": { "Empty": [] } }""", "Layer 'Empty'")]
    [InlineData("""{ "rules": [{ "name": "Same", "from": "A", "forbid": ["B"] }, { "name": "same!", "from": "C", "forbid": ["D"] }] }""", "'same'")]
    [InlineData("""{ "rules": [{ "name": "R", "from": "A", "forbid": [""] }] }""", "can't be empty")]
    public void Explains_what_is_wrong_with_an_invalid_config(string json, string expectedMessagePart)
    {
        var error = Assert.Throws<ArchitectureConfigException>(() => ArchitectureConfig.Parse(json));
        Assert.Contains(expectedMessagePart, error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
