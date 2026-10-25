using System.Text.Json;
using System.Text.Json.Nodes;

namespace Burndown.Core.Rules;

/// <summary>
/// Writes check results as SARIF 2.1.0, the static-analysis format GitHub code scanning (and most CI
/// systems) can import. Each violation becomes a result anchored to a source line.
/// </summary>
public static class SarifWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <param name="pathPrefix">
    /// Prepended to every source path. SARIF consumers resolve paths against the repository root,
    /// while Burndown paths are relative to the analyzed solution.
    /// </param>
    public static string Write(CheckResult result, string toolVersion, string? pathPrefix = null)
    {
        var ruleIndex = result.Rules
            .Select((rule, index) => (rule.Id, index))
            .ToDictionary(r => r.Id, r => r.index, StringComparer.Ordinal);

        var sarif = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["driver"] = new JsonObject
                    {
                        ["name"] = "Burndown",
                        ["version"] = toolVersion,
                        ["rules"] = new JsonArray(result.Rules.Select(RuleNode).ToArray<JsonNode?>()),
                    },
                },
                ["results"] = new JsonArray(result.Violations
                    .Select(v => ResultNode(v, ruleIndex[v.RuleId], pathPrefix))
                    .ToArray<JsonNode?>()),
            }),
        };

        return sarif.ToJsonString(Indented);
    }

    private static JsonObject RuleNode(RuleInfo rule) => new()
    {
        ["id"] = rule.Id,
        ["shortDescription"] = new JsonObject { ["text"] = rule.Name },
        ["fullDescription"] = new JsonObject { ["text"] = rule.Description },
        ["defaultConfiguration"] = new JsonObject { ["level"] = Level(rule.Severity) },
    };

    private static JsonObject ResultNode(RuleViolation violation, int ruleIndex, string? pathPrefix)
    {
        var result = new JsonObject
        {
            ["ruleId"] = violation.RuleId,
            ["ruleIndex"] = ruleIndex,
            ["level"] = Level(violation.Severity),
            ["message"] = new JsonObject { ["text"] = violation.Message },
        };

        if (violation.Location is { } location)
        {
            result["locations"] = new JsonArray(new JsonObject
            {
                ["physicalLocation"] = new JsonObject
                {
                    ["artifactLocation"] = new JsonObject
                    {
                        ["uri"] = ToUri(location.FilePath, pathPrefix),
                        ["uriBaseId"] = "%SRCROOT%",
                    },
                    ["region"] = new JsonObject { ["startLine"] = location.Line },
                },
            });
        }

        return result;
    }

    private static string Level(RuleSeverity severity) => severity == RuleSeverity.Error ? "error" : "warning";

    /// <summary>A relative URI reference: forward slashes, each segment percent-encoded (spaces become %20).</summary>
    private static string ToUri(string path, string? prefix)
    {
        var combined = string.IsNullOrEmpty(prefix) ? path : $"{prefix.Trim('/', '\\')}/{path}";
        return string.Join('/', combined.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
    }
}
