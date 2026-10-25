using Microsoft.CodeAnalysis;

namespace Burndown.Analysis;

internal static class SymbolIds
{
    /// <summary>
    /// Builds a node id that survives across compilations. Two <see cref="INamedTypeSymbol"/> instances
    /// for the same type (e.g. seen from its own project and from a project that references it) are
    /// different objects, so the id is a string: the assembly name plus the documentation comment id.
    /// The doc id includes generic arity, so <c>Repository</c> and <c>Repository&lt;T&gt;</c> stay distinct,
    /// and the assembly prefix keeps same-named types in different projects apart.
    /// </summary>
    public static string For(INamedTypeSymbol type)
    {
        var definition = type.OriginalDefinition;
        var docId = definition.GetDocumentationCommentId();
        var name = docId is not null && docId.StartsWith("T:", StringComparison.Ordinal)
            ? docId[2..]
            : definition.ToDisplayString();

        return $"{definition.ContainingAssembly?.Name ?? "?"}:{name}";
    }
}
