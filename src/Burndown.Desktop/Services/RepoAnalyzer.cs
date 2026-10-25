using System.IO;
using System.Reflection;
using Burndown.Analysis;
using Burndown.Core;
using Burndown.Core.Rules;
using Burndown.Report;
using Burndown.Repos;

namespace Burndown.Desktop.Services;

/// <summary>What one analysis produced, for the sidebar and status text.</summary>
public sealed record AnalysisSummary(int Types, int Dependencies, int? Errors, int? Warnings, IReadOnlyList<string> GraphWarnings);

/// <summary>
/// Runs the same pipeline as <c>burndown report</c> for a saved repository: find its solution and
/// burndown.json, analyze, check the rules, and write the HTML report the window displays.
/// </summary>
public static class RepoAnalyzer
{
    public static async Task<AnalysisSummary> AnalyzeAsync(
        RegisteredRepo repo,
        string reportPath,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var solution = SolutionLocator.Resolve(repo.Path);
        progress.Report($"Loading {Path.GetFileName(solution)}…");

        var configPath = ConfigLocator.Find(Path.GetDirectoryName(solution) ?? repo.Path);
        var config = configPath is null
            ? null
            : ArchitectureConfig.Parse(await File.ReadAllTextAsync(configPath, cancellationToken));

        var graph = await SolutionAnalyzer.AnalyzeAsync(solution, progress, cancellationToken, new ProjectFilter(config?.Exclude ?? []));
        var check = config is null ? null : ArchitectureChecker.Check(graph, config);

        var html = HtmlReportWriter.Write(graph, new ReportOptions
        {
            Title = config?.Report.Title ?? repo.GitHub ?? repo.Name,
            Theme = config?.Report.Theme?.ToLowerInvariant() ?? ReportThemes.Default,
            ToolVersion = Version,
            Check = check,
        });

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
        await File.WriteAllTextAsync(reportPath, html, cancellationToken);

        return new AnalysisSummary(graph.Nodes.Count, graph.Edges.Count, check?.ErrorCount, check?.WarningCount, graph.Warnings);
    }

    private static string Version
    {
        get
        {
            var version = typeof(RepoAnalyzer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
            var metadata = version.IndexOf('+');
            return metadata < 0 ? version : version[..metadata];
        }
    }
}
