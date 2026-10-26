using PopUp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PopUp.Analysis.Tests;

/// <summary>
/// Builds Roslyn compilations from source strings in memory. This keeps extraction tests fast
/// and independent of MSBuild, installed SDKs, and the file system.
/// </summary>
internal static class TestCompilation
{
    // The assemblies of the running .NET runtime, so test sources can use System types.
    private static readonly MetadataReference[] FrameworkReferences =
        (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? throw new InvalidOperationException("No platform assemblies found."))
        .Split(Path.PathSeparator)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    public static CSharpCompilation Create(string assemblyName, string[] sources, params MetadataReference[] references)
    {
        var trees = sources.Select((source, i) => CSharpSyntaxTree.ParseText(source, path: $"{assemblyName}/File{i}.cs"));
        return CSharpCompilation.Create(
            assemblyName,
            trees,
            FrameworkReferences.Concat(references),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>Analyzes the sources as a single project named "App".</summary>
    public static DependencyGraph Analyze(params string[] sources)
    {
        var builder = new GraphBuilder();
        builder.AddCompilation(Create("App", sources));
        return builder.Build();
    }

    public static CodeNode Node(this DependencyGraph graph, string fullName) =>
        graph.Nodes.Single(n => n.FullName == fullName);

    /// <summary>The edges leaving <paramref name="fullName"/>, as "Kind -> TargetFullName" for readable assertions.</summary>
    public static string[] EdgesFrom(this DependencyGraph graph, string fullName)
    {
        var namesById = graph.Nodes.ToDictionary(n => n.Id, n => n.FullName);
        var sourceId = graph.Node(fullName).Id;
        return graph.Edges
            .Where(e => e.SourceId == sourceId)
            .Select(e => $"{e.Kind} -> {namesById[e.TargetId]}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
