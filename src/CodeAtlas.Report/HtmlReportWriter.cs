using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodeAtlas.Core;
using CodeAtlas.Core.Algorithms;
using CodeAtlas.Core.Rules;

namespace CodeAtlas.Report;

public sealed record ReportOptions
{
    public required string Title { get; init; }

    public string ToolVersion { get; init; } = "";

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Results of <c>codeatlas check</c> to show in the report, if a config was available.</summary>
    public CheckResult? Check { get; init; }
}

/// <summary>
/// Writes a single, self-contained HTML file: the page, styles, script, Cytoscape.js, and the graph data
/// are all inlined, so the report opens offline and can be attached to a CI run or emailed.
/// </summary>
/// <remarks>
/// The analysis (cycles, coupling, rule results) runs here in C#; the page's script only draws and filters.
/// </remarks>
public static partial class HtmlReportWriter
{
    private static readonly JsonSerializerOptions DataOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        // The default encoder escapes <, >, & and quotes as \uXXXX, so type names can never close the
        // <script> element or inject markup.
    };

    public static string Write(DependencyGraph graph, ReportOptions options)
    {
        // Edges are by far the most numerous records, and as objects each one repeated two long type ids
        // and a file path (~350 bytes). They're written as number arrays that point into the node, kind,
        // and file tables instead (~20 bytes); the page's script expands them when it loads.
        var nodeIndex = graph.Nodes.Select((node, i) => (node.Id, i)).ToDictionary(n => n.Id, n => n.i, StringComparer.Ordinal);
        var kinds = Enum.GetNames<DependencyKind>();
        var files = new List<string>();
        var fileIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        int FileId(string path)
        {
            if (!fileIndex.TryGetValue(path, out var id))
            {
                id = files.Count;
                fileIndex[path] = id;
                files.Add(path);
            }

            return id;
        }

        int[] Location(SourceLocation location) => [FileId(location.FilePath), location.Line];

        var data = new
        {
            options.Title,
            options.ToolVersion,
            GeneratedAt = options.GeneratedAt.ToString("u"),
            Kinds = kinds,
            Files = files, // the same list instance; building Nodes and Edges below fills it in
            Nodes = graph.Nodes.Select(n => new
            {
                n.Id, n.Name, n.FullName, n.Namespace, n.Project, n.Kind,
                Locations = n.Locations.Select(Location).ToList(),
            }).ToList(),
            // [source, target, kind] or [source, target, kind, file, line]
            Edges = graph.Edges.Select(e => e.Location is null
                ? new[] { nodeIndex[e.SourceId], nodeIndex[e.TargetId], (int)e.Kind }
                : [nodeIndex[e.SourceId], nodeIndex[e.TargetId], (int)e.Kind, .. Location(e.Location)]).ToList(),
            graph.Warnings,
            NamespaceCycles = CycleDetector.FindCycles(graph, CycleLevel.Namespace)
                .Select(c => new { c.Members, c.ExamplePath, EvidenceCount = c.Evidence.Count }),
            // [node, incoming, outgoing, instability]
            Coupling = CouplingMetrics.Calculate(graph)
                .Select(c => new double[] { nodeIndex[c.NodeId], c.Incoming, c.Outgoing, c.Instability }),
            options.Check,
        };

        var sections = new Dictionary<string, string>
        {
            ["TITLE"] = WebUtility.HtmlEncode(options.Title),
            ["STYLES"] = Asset("report.css"),
            ["CYTOSCAPE"] = InlineScript(Asset("cytoscape.min.js")),
            ["SCRIPT"] = InlineScript(Asset("report.js")),
            ["DATA"] = JsonSerializer.Serialize(data, DataOptions),
        };

        // One pass over the template only. Chained string.Replace calls would also scan text inserted by
        // earlier replacements, so a placeholder-like string inside a script or a type name could be substituted.
        return Placeholder().Replace(Asset("report.html"), match => sections[match.Groups["name"].Value]);
    }

    /// <summary>Matches <c>{{NAME}}</c> and <c>/*{{NAME}}*/</c> (the comment form keeps the template valid CSS/JS).</summary>
    [GeneratedRegex(@"(/\*)?\{\{(?<name>[A-Z]+)\}\}(?(1)\*/)")]
    private static partial Regex Placeholder();

    /// <summary>A script can't contain "&lt;/script" verbatim; "&lt;\/script" means the same thing to JavaScript.</summary>
    private static string InlineScript(string script) =>
        script.Replace("</script", @"<\/script", StringComparison.OrdinalIgnoreCase);

    internal static string Asset(string fileName)
    {
        using var stream = typeof(HtmlReportWriter).Assembly.GetManifestResourceStream($"CodeAtlas.Report.{fileName}")
            ?? throw new InvalidOperationException($"Report asset '{fileName}' is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
