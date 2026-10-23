using System.Text.Json;
using CodeAtlas.Core.Rules;

namespace CodeAtlas.Core.Tests;

public class SarifWriterTests
{
    private static readonly CheckResult Result = new(
        Rules:
        [
            new RuleInfo("domain-alone", "Domain alone", "Types in Domain must not depend on Api.", RuleSeverity.Error),
            new RuleInfo("namespace-cycle", "No namespace cycles", "No loops.", RuleSeverity.Warning),
        ],
        Violations:
        [
            new RuleViolation("namespace-cycle", RuleSeverity.Warning, "Cycle!", new SourceLocation("Shop Domain/Order.cs", 12)),
            new RuleViolation("domain-alone", RuleSeverity.Error, "Bad!", Location: null),
        ],
        Warnings: []);

    private static JsonElement Run(string? pathPrefix = null) =>
        JsonDocument.Parse(SarifWriter.Write(Result, "1.2.3", pathPrefix)).RootElement.GetProperty("runs")[0];

    [Fact]
    public void Writes_a_sarif_2_1_0_log_with_every_rule()
    {
        var root = JsonDocument.Parse(SarifWriter.Write(Result, "1.2.3")).RootElement;
        Assert.Equal("2.1.0", root.GetProperty("version").GetString());

        var driver = root.GetProperty("runs")[0].GetProperty("tool").GetProperty("driver");
        Assert.Equal("CodeAtlas", driver.GetProperty("name").GetString());
        Assert.Equal("1.2.3", driver.GetProperty("version").GetString());
        Assert.Equal(
            ["domain-alone", "namespace-cycle"],
            driver.GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
    }

    [Fact]
    public void Links_each_result_to_its_rule_and_severity()
    {
        var results = Run().GetProperty("results");

        Assert.Equal("namespace-cycle", results[0].GetProperty("ruleId").GetString());
        Assert.Equal(1, results[0].GetProperty("ruleIndex").GetInt32());
        Assert.Equal("warning", results[0].GetProperty("level").GetString());
        Assert.Equal("Cycle!", results[0].GetProperty("message").GetProperty("text").GetString());
        Assert.Equal("error", results[1].GetProperty("level").GetString());
    }

    [Fact]
    public void Writes_repository_relative_escaped_locations()
    {
        var location = Run(pathPrefix: "samples/Shop/").GetProperty("results")[0]
            .GetProperty("locations")[0].GetProperty("physicalLocation");

        Assert.Equal("samples/Shop/Shop%20Domain/Order.cs", location.GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.Equal(12, location.GetProperty("region").GetProperty("startLine").GetInt32());
    }

    [Fact]
    public void Omits_locations_when_none_are_known()
    {
        Assert.False(Run().GetProperty("results")[1].TryGetProperty("locations", out _));
    }
}
