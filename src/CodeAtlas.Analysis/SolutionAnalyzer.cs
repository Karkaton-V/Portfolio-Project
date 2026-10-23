using CodeAtlas.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeAtlas.Analysis;

/// <summary>Loads a solution or project with MSBuild and builds its dependency graph.</summary>
/// <remarks>
/// Loading evaluates MSBuild project files, which can run arbitrary build logic.
/// Only analyze solutions you trust.
/// </remarks>
public static class SolutionAnalyzer
{
    /// <param name="path">A <c>.sln</c>, <c>.slnx</c>, or <c>.csproj</c> file.</param>
    /// <param name="progress">Receives the name of each project as it is analyzed.</param>
    public static async Task<DependencyGraph> AnalyzeAsync(
        string path,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
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

        var projects = workspace.CurrentSolution.Projects
            .Where(p => p.Language == LanguageNames.CSharp)
            .OrderBy(p => p.Name, StringComparer.Ordinal);

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(project.Name);

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                builder.AddWarning($"Could not create a compilation for {project.Name}; it was skipped.");
                continue;
            }

            builder.AddCompilation(compilation, ProjectLabel(project));
        }

        return builder.Build();
    }

    /// <summary>Multi-targeted projects load once per framework, named like "Orders.Api(net9.0)". Strip the suffix.</summary>
    private static string ProjectLabel(Project project)
    {
        var parenthesis = project.Name.IndexOf('(');
        return parenthesis > 0 ? project.Name[..parenthesis] : project.Name;
    }
}
