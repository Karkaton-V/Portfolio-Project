using System.Diagnostics;
using Burndown.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Burndown.Analysis;

/// <summary>Loads a solution or project with MSBuild and builds its dependency graph.</summary>
/// <remarks>
/// Loading evaluates MSBuild project files, which can run arbitrary build logic.
/// Only analyze solutions you trust.
/// </remarks>
public static class SolutionAnalyzer
{
    /// <param name="path">A <c>.sln</c>, <c>.slnx</c>, or <c>.csproj</c> file.</param>
    /// <param name="progress">Receives status messages, including how long each phase took.</param>
    /// <param name="filter">Projects to skip. Excluded projects aren't compiled at all, which also saves time.</param>
    public static async Task<DependencyGraph> AnalyzeAsync(
        string path,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        ProjectFilter? filter = null)
    {
        filter ??= ProjectFilter.None;
        var fullPath = Path.GetFullPath(path);
        var clock = Stopwatch.StartNew();
        using var workspace = MSBuildWorkspace.Create();

        switch (Path.GetExtension(fullPath).ToLowerInvariant())
        {
            case ".sln" or ".slnx":
                await workspace.OpenSolutionAsync(fullPath, cancellationToken: cancellationToken);
                break;
            case ".csproj":
                await workspace.OpenProjectAsync(fullPath, cancellationToken: cancellationToken);
                break;
            default:
                throw new ArgumentException($"Expected a .sln, .slnx, or .csproj file but got '{path}'.", nameof(path));
        }

        var builder = new GraphBuilder(Path.GetDirectoryName(fullPath));

        // MSBuildWorkspace doesn't throw when a project fails to load; it records a diagnostic and
        // carries on. Surface these so a partial graph is never mistaken for a complete one.
        foreach (var message in workspace.Diagnostics.Select(d => $"{d.Kind}: {d.Message}").Distinct())
        {
            builder.AddWarning(message);
        }

        var csharpProjects = workspace.CurrentSolution.Projects
            .Where(p => p.Language == LanguageNames.CSharp)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        var projects = csharpProjects.Where(p => filter.Includes(ProjectLabel(p))).ToList();
        var skipped = csharpProjects.Count - projects.Count;
        progress?.Report($"Loaded {csharpProjects.Count} projects in {Seconds(clock)}" +
            (skipped > 0 ? $"; skipping {skipped} excluded by {string.Join(", ", filter.Patterns)}" : ""));

        // Compile and extract every project in parallel. Projects that reference each other share Roslyn's
        // cached compilations, so this doesn't redo work. Results land in a fixed slot per project and are
        // merged in name order, which keeps the graph identical from run to run.
        var phase = Stopwatch.StartNew();
        var extractions = new ProjectExtraction?[projects.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, projects.Count),
            new ParallelOptions { CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var project = projects[index];
                var compilation = await project.GetCompilationAsync(token);
                if (compilation is not null)
                {
                    extractions[index] = builder.Extract(compilation, ProjectLabel(project), token);
                }

                progress?.Report($"Analyzed {project.Name}");
            });

        for (var index = 0; index < projects.Count; index++)
        {
            if (extractions[index] is { } extraction)
            {
                builder.Add(extraction);
            }
            else
            {
                builder.AddWarning($"Could not create a compilation for {projects[index].Name}; it was skipped.");
            }
        }

        var graph = builder.Build();
        progress?.Report(
            $"Compiled and extracted dependencies in {phase.Elapsed.TotalSeconds:0.0} s ({Seconds(clock)} total, " +
            $"using up to {Environment.ProcessorCount} threads)");
        return graph;
    }

    private static string Seconds(Stopwatch clock) => $"{clock.Elapsed.TotalSeconds:0.0} s";

    /// <summary>Multi-targeted projects load once per framework, named like "Orders.Api(net9.0)". Strip the suffix.</summary>
    private static string ProjectLabel(Project project)
    {
        var parenthesis = project.Name.IndexOf('(');
        return parenthesis > 0 ? project.Name[..parenthesis] : project.Name;
    }
}
