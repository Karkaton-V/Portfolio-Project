using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodeAtlas.Core.Rules;

public enum RuleSeverity
{
    /// <summary>Fails the check (non-zero exit code).</summary>
    Error,

    /// <summary>Reported, but doesn't fail the check.</summary>
    Warning,
}

/// <summary>The config file is malformed. The message says what to fix.</summary>
public sealed class ArchitectureConfigException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// One allowed-or-forbidden dependency rule. <see cref="From"/> and the target lists are selectors:
/// a layer name, a <see cref="NamespacePattern"/>, or <c>project:Name</c>.
/// </summary>
public sealed record DependencyRule
{
    /// <summary>Stable id for SARIF and suppressions. Defaults to a slug of the name.</summary>
    public string? Id { get; init; }

    public required string Name { get; init; }

    public required string From { get; init; }

    /// <summary>Types in <see cref="From"/> must not depend on any of these.</summary>
    public IReadOnlyList<string>? Forbid { get; init; }

    /// <summary>Types in <see cref="From"/> may depend only on these and on <see cref="From"/> itself. Empty means "only itself".</summary>
    public IReadOnlyList<string>? MayOnlyDependOn { get; init; }

    /// <summary>Only check these kinds of dependency. Null means all.</summary>
    public IReadOnlyList<DependencyKind>? Kinds { get; init; }

    public RuleSeverity Severity { get; init; } = RuleSeverity.Error;

    [JsonIgnore]
    public string RuleId => Id ?? Slug(Name);

    /// <summary>Whichever of <see cref="Forbid"/> or <see cref="MayOnlyDependOn"/> is set.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Targets => Forbid ?? MayOnlyDependOn ?? [];

    /// <summary>"Domain must not depend on API!" becomes "domain-must-not-depend-on-api".</summary>
    private static string Slug(string text) =>
        Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}

/// <summary>The contents of a <c>codeatlas.json</c> file.</summary>
public sealed record ArchitectureConfig
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // The file is written by hand, so be forgiving about comments and trailing commas,
        // but strict about misspelled property names, which would otherwise be silently ignored.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Named groups of selectors, so rules can say "Domain" instead of repeating patterns.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Layers { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    public IReadOnlyList<DependencyRule> Rules { get; init; } = [];

    /// <summary>When set, every namespace cycle is reported with this severity.</summary>
    public RuleSeverity? NamespaceCycles { get; init; }

    /// <summary>Projects to leave out of every command, as <see cref="ProjectFilter"/> globs (e.g. <c>*Tests</c>).</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>Defaults for <c>codeatlas report</c>.</summary>
    public ReportSettings Report { get; init; } = new();

    public static ArchitectureConfig Parse(string json)
    {
        ArchitectureConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ArchitectureConfig>(json, Options);
        }
        catch (JsonException ex)
        {
            // System.Text.Json describes problems in terms of .NET types; reword the common one for config authors.
            var message = Regex.Replace(
                ex.Message, @"could not be mapped to any \.NET member contained in type '[^']*'", "is not a recognized setting");
            var line = ex.LineNumber is { } zeroBased ? $"Line {zeroBased + 1}: " : "";
            throw new ArchitectureConfigException(line + message, ex);
        }

        if (config is null)
        {
            throw new ArchitectureConfigException("The config file is empty.");
        }

        config.Validate();
        return config;
    }

    private void Validate()
    {
        if (Report.Theme is { } theme && !ReportThemes.IsKnown(theme))
        {
            throw new ArchitectureConfigException(
                $"Unknown report theme '{theme}'. Choose one of: {string.Join(", ", ReportThemes.Names)}.");
        }

        foreach (var (name, selectors) in Layers)
        {
            if (selectors.Count == 0)
            {
                throw new ArchitectureConfigException($"Layer '{name}' needs at least one namespace pattern.");
            }
        }

        foreach (var rule in Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name) || string.IsNullOrWhiteSpace(rule.From))
            {
                throw new ArchitectureConfigException("Every rule needs a 'name' and a 'from'.");
            }

            if ((rule.Forbid is null) == (rule.MayOnlyDependOn is null))
            {
                throw new ArchitectureConfigException(
                    $"Rule '{rule.Name}' must have exactly one of 'forbid' or 'mayOnlyDependOn'.");
            }

            if (rule.Forbid is { Count: 0 })
            {
                throw new ArchitectureConfigException($"Rule '{rule.Name}' has an empty 'forbid' list.");
            }

            // Resolving every selector up front reports bad patterns before any analysis runs.
            foreach (var selector in rule.Targets.Prepend(rule.From))
            {
                Selector.Resolve(selector, Layers);
            }
        }

        var duplicate = Rules.GroupBy(r => r.RuleId).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArchitectureConfigException(
                $"More than one rule has the id '{duplicate.Key}'. Give them distinct names or 'id' values.");
        }
    }
}

/// <summary>A rule's <c>from</c> or target, resolved to concrete matchers.</summary>
internal sealed class Selector
{
    private const string ProjectPrefix = "project:";

    private readonly IReadOnlyList<NamespacePattern> _namespaces;
    private readonly IReadOnlyList<string> _projects;

    private Selector(string text, IReadOnlyList<NamespacePattern> namespaces, IReadOnlyList<string> projects)
    {
        Text = text;
        _namespaces = namespaces;
        _projects = projects;
    }

    public string Text { get; }

    public static Selector Resolve(string text, IReadOnlyDictionary<string, IReadOnlyList<string>> layers)
    {
        IReadOnlyList<string> parts = layers.TryGetValue(text, out var layer) ? layer : [text];

        return new Selector(
            text,
            parts.Where(p => !p.StartsWith(ProjectPrefix, StringComparison.Ordinal)).Select(NamespacePattern.Parse).ToList(),
            parts.Where(p => p.StartsWith(ProjectPrefix, StringComparison.Ordinal)).Select(p => p[ProjectPrefix.Length..]).ToList());
    }

    public bool Matches(CodeNode node) =>
        _projects.Contains(node.Project, StringComparer.Ordinal) || _namespaces.Any(p => p.Matches(node.Namespace));
}

/// <summary>The <c>report</c> section of <c>codeatlas.json</c>. Command-line options override these.</summary>
public sealed record ReportSettings
{
    /// <summary>One of <see cref="ReportThemes.Names"/>. Viewers can still switch themes in the report itself.</summary>
    public string? Theme { get; init; }

    public string? Title { get; init; }
}
