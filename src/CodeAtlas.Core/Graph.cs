namespace CodeAtlas.Core;

/// <summary>The kind of type a <see cref="CodeNode"/> represents.</summary>
public enum NodeKind
{
    Class,
    Interface,
    Struct,
    Record,
    RecordStruct,
    Enum,
    Delegate,
}

/// <summary>
/// Why one type depends on another. Kept separate so a viewer can filter noisy kinds
/// (e.g. hide property references) without losing structural ones (inheritance).
/// </summary>
public enum DependencyKind
{
    /// <summary>Class extends a base class, or an interface extends another interface.</summary>
    Inherits,

    /// <summary>Class, struct, or record implements an interface.</summary>
    Implements,

    /// <summary>Type appears in a constructor parameter (typically dependency injection).</summary>
    ConstructorParameter,

    /// <summary>Type appears in a field declaration.</summary>
    Field,

    /// <summary>Type appears in a property declaration.</summary>
    Property,
}

/// <summary>A 1-based line in a source file. Paths are relative to the analyzed solution when possible.</summary>
public sealed record SourceLocation(string FilePath, int Line);

/// <summary>
/// A type declared in the analyzed solution. A partial type is a single node with one
/// location per declaration.
/// </summary>
/// <param name="Id">Stable identity: <c>{assembly}:{documentation comment id}</c>, e.g. <c>Orders.Domain:Orders.Repository`1</c>.</param>
public sealed record CodeNode(
    string Id,
    string Name,
    string FullName,
    string Namespace,
    string Project,
    NodeKind Kind,
    IReadOnlyList<SourceLocation> Locations);

/// <summary>A directed dependency: <see cref="SourceId"/> depends on <see cref="TargetId"/>.</summary>
/// <param name="Location">Where the dependency was observed (first occurrence).</param>
public sealed record CodeEdge(
    string SourceId,
    string TargetId,
    DependencyKind Kind,
    SourceLocation? Location);

/// <summary>The result of analyzing a solution: solution-defined types and the dependencies between them.</summary>
public sealed class DependencyGraph
{
    public required IReadOnlyList<CodeNode> Nodes { get; init; }

    public required IReadOnlyList<CodeEdge> Edges { get; init; }

    /// <summary>Problems that may make the graph incomplete (failed project loads, unresolved types).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
