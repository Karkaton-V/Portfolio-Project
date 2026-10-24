using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAtlas.Core;
using CodeAtlas.Core.Rules;

namespace CodeAtlas.Report.Tests;

public class HtmlReportWriterTests
{
    private static CodeNode Node(string fullName, string project = "Shop")
    {
        var lastDot = fullName.LastIndexOf('.');
        return new CodeNode(fullName, fullName[(lastDot + 1)..], fullName, fullName[..lastDot], project, NodeKind.Class,
            [new SourceLocation($"{fullName}.cs", 1)]);
    }

    private static readonly DependencyGraph Graph = new()
    {
        Nodes = [Node("Shop.Billing.Invoice"), Node("Shop.Orders.Order"), Node("Shop.Orders.OrderService")],
        Edges =
        [
            new CodeEdge("Shop.Billing.Invoice", "Shop.Orders.Order", DependencyKind.Property, null),
            new CodeEdge("Shop.Orders.Order", "Shop.Billing.Invoice", DependencyKind.Calls, null),
            new CodeEdge("Shop.Orders.OrderService", "Shop.Orders.Order", DependencyKind.Instantiates, null),
        ],
    };

    private static string Write(DependencyGraph graph, CheckResult? check = null, string title = "Shop") =>
        HtmlReportWriter.Write(graph, new ReportOptions { Title = title, ToolVersion = "9.9.9", Check = check });

    /// <summary>Pulls the embedded JSON back out of the page, the same way the page's script reads it.</summary>
    private static JsonElement EmbeddedData(string html)
    {
        var match = Regex.Match(html, """<script id="codeatlas-data" type="application/json">(.*?)</script>""", RegexOptions.Singleline);
        Assert.True(match.Success, "The report has no embedded data block.");
        return JsonDocument.Parse(match.Groups[1].Value).RootElement;
    }

    [Fact]
    public void Is_a_single_self_contained_file()
    {
        var html = Write(Graph);

        Assert.StartsWith("<!doctype html>", html);
        Assert.DoesNotMatch(@"<script[^>]*\bsrc=", html);
        Assert.DoesNotMatch(@"<link\b", html);
        Assert.Contains("The Cytoscape Consortium", html); // the library itself is inlined
        Assert.DoesNotMatch(@"\{\{[A-Z]+\}\}", html); // every template placeholder was filled
    }

    [Fact]
    public void Does_not_substitute_placeholder_text_that_comes_from_the_code_being_analyzed()
    {
        var graph = new DependencyGraph { Nodes = [Node("Shop.Orders.Order")], Edges = [] };

        var html = Write(graph, title: "/*{{SCRIPT}}*/");

        // The title is inserted verbatim (HTML-encoded), not replaced by the page's script.
        Assert.Equal("/*{{SCRIPT}}*/", EmbeddedData(html).GetProperty("title").GetString());
        Assert.Single(Regex.Matches(html, "CodeAtlas report viewer"));
    }

    [Fact]
    public void Embeds_the_graph_and_the_analysis_results()
    {
        var data = EmbeddedData(Write(Graph));

        Assert.Equal("Shop", data.GetProperty("title").GetString());
        Assert.Equal("9.9.9", data.GetProperty("toolVersion").GetString());
        Assert.Equal(3, data.GetProperty("nodes").GetArrayLength());

        var cycle = Assert.Single(data.GetProperty("namespaceCycles").EnumerateArray());
        Assert.Equal(2, cycle.GetProperty("evidenceCount").GetInt32());

        // Coupling rows are [node index, incoming, outgoing, instability].
        var mostConnected = data.GetProperty("coupling")[0];
        Assert.Equal("Shop.Orders.Order", data.GetProperty("nodes")[mostConnected[0].GetInt32()].GetProperty("id").GetString());
        Assert.Equal(2, mostConnected[1].GetInt32());
    }

    [Fact]
    public void Writes_edges_as_compact_rows_that_point_into_the_node_kind_and_file_tables()
    {
        var located = new DependencyGraph
        {
            Nodes = Graph.Nodes,
            Edges = [new CodeEdge("Shop.Billing.Invoice", "Shop.Orders.Order", DependencyKind.Property, new SourceLocation("Billing/Invoice.cs", 7))],
        };

        var data = EmbeddedData(Write(located));
        var row = data.GetProperty("edges")[0].EnumerateArray().Select(v => v.GetInt32()).ToArray();

        // [source, target, kind, file, line]
        Assert.Equal(5, row.Length);
        Assert.Equal("Shop.Billing.Invoice", data.GetProperty("nodes")[row[0]].GetProperty("id").GetString());
        Assert.Equal("Shop.Orders.Order", data.GetProperty("nodes")[row[1]].GetProperty("id").GetString());
        Assert.Equal("Property", data.GetProperty("kinds")[row[2]].GetString());
        Assert.Equal("Billing/Invoice.cs", data.GetProperty("files")[row[3]].GetString());
        Assert.Equal(7, row[4]);

        // Locations without a file are shortened to [source, target, kind].
        Assert.Equal(3, EmbeddedData(Write(Graph)).GetProperty("edges")[0].GetArrayLength());

        Assert.False(data.TryGetProperty("check", out _)); // no config, so no rule results
    }

    [Fact]
    public void Includes_rule_results_when_given()
    {
        var config = ArchitectureConfig.Parse("""
            { "rules": [{ "name": "Billing stays out of Orders", "from": "Shop.Billing", "forbid": ["Shop.Orders"] }] }
            """);

        var data = EmbeddedData(Write(Graph, ArchitectureChecker.Check(Graph, config)));

        var check = data.GetProperty("check");
        Assert.Equal(1, check.GetProperty("errorCount").GetInt32());
        var violation = Assert.Single(check.GetProperty("violations").EnumerateArray());
        Assert.Equal("Shop.Billing.Invoice", violation.GetProperty("nodeId").GetString());
    }

    [Fact]
    public void Cannot_be_broken_out_of_by_hostile_names()
    {
        const string hostile = "</script><img src=x onerror=alert(1)>";
        var graph = new DependencyGraph { Nodes = [Node($"Evil.{hostile}")], Edges = [] };

        var html = Write(graph, title: hostile);

        // The name may only appear escaped: < in the JSON, &lt; in the <title>.
        Assert.DoesNotContain(hostile, html);
        Assert.Contains("&lt;/script&gt;", html);
        Assert.Equal($"Evil.{hostile}", EmbeddedData(html).GetProperty("nodes")[0].GetProperty("fullName").GetString());
    }

    [Fact]
    public void Embeds_the_starting_theme_and_the_theme_list()
    {
        var data = EmbeddedData(HtmlReportWriter.Write(Graph, new ReportOptions { Title = "Shop", Theme = "Solarized" }));

        Assert.Equal("solarized", data.GetProperty("theme").GetString());
        Assert.Equal(ReportThemes.Names, data.GetProperty("themes").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public void Rejects_an_unknown_theme()
    {
        Assert.Throws<ArgumentException>(() => HtmlReportWriter.Write(Graph, new ReportOptions { Title = "Shop", Theme = "neon" }));
    }

    [Fact]
    public void Ships_every_asset_the_page_needs()
    {
        foreach (var asset in new[] { "report.html", "report.css", "report.js", "cytoscape.min.js", "cytoscape.LICENSE" })
        {
            Assert.False(string.IsNullOrWhiteSpace(HtmlReportWriter.Asset(asset)), $"{asset} is empty or missing.");
        }
    }
}
