using PopUp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PopUp.Analysis;

/// <summary>
/// Finds dependencies inside executable code: method, constructor, and accessor bodies, expression
/// bodies, and field/property initializers. Each syntax node is bound with the semantic model, so
/// <c>var</c>, aliases, and extension methods resolve to the types they really mean.
/// </summary>
internal static class BodyDependencyExtractor
{
    /// <param name="semanticModelFor">Returns a (cached) semantic model for a syntax tree of the type's compilation.</param>
    public static IEnumerable<TypeReference> Extract(INamedTypeSymbol type, Func<SyntaxTree, SemanticModel> semanticModelFor)
    {
        // A partial type has one declaration per file; each needs its own file's semantic model.
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            var declaration = reference.GetSyntax();
            var model = semanticModelFor(declaration.SyntaxTree);

            foreach (var node in BodyRoots(declaration).SelectMany(root => root.DescendantNodesAndSelf()))
            {
                var found = node switch
                {
                    BaseObjectCreationExpressionSyntax creation => Instantiation(creation, model),
                    InvocationExpressionSyntax invocation => Call(invocation, model),
                    IdentifierNameSyntax or GenericNameSyntax => TypeName((SimpleNameSyntax)node, model),
                    _ => null,
                };

                if (found is { } dependency)
                {
                    yield return dependency;
                }
            }
        }
    }

    /// <summary>
    /// The executable parts of a type declaration. Nested types are separate declarations with their own
    /// bodies, so they're skipped here and attributed to the nested type instead.
    /// </summary>
    private static IEnumerable<SyntaxNode> BodyRoots(SyntaxNode declaration)
    {
        // Top-level statements: the compiler's synthesized Program class is declared by the file itself.
        if (declaration is CompilationUnitSyntax compilationUnit)
        {
            return compilationUnit.Members.OfType<GlobalStatementSyntax>();
        }

        if (declaration is not TypeDeclarationSyntax typeDeclaration)
        {
            return [];
        }

        return typeDeclaration.Members.SelectMany(MemberBodies).OfType<SyntaxNode>();
    }

    private static IEnumerable<SyntaxNode?> MemberBodies(MemberDeclarationSyntax member) => member switch
    {
        ConstructorDeclarationSyntax constructor => [constructor.Initializer, constructor.Body, constructor.ExpressionBody],
        BaseMethodDeclarationSyntax method => [method.Body, method.ExpressionBody],
        PropertyDeclarationSyntax property => [property.ExpressionBody, property.Initializer, .. AccessorBodies(property)],
        IndexerDeclarationSyntax indexer => [indexer.ExpressionBody, .. AccessorBodies(indexer)],
        BasePropertyDeclarationSyntax other => AccessorBodies(other),
        BaseFieldDeclarationSyntax field => field.Declaration.Variables.Select(v => v.Initializer),
        _ => [],
    };

    private static IEnumerable<SyntaxNode?> AccessorBodies(BasePropertyDeclarationSyntax property) =>
        property.AccessorList?.Accessors.SelectMany(a => new SyntaxNode?[] { a.Body, a.ExpressionBody }) ?? [];

    private static TypeReference? Instantiation(BaseObjectCreationExpressionSyntax creation, SemanticModel model) =>
        model.GetTypeInfo(creation).Type is INamedTypeSymbol created
            // Only the created type itself. Type arguments (new List<Order>()) are picked up as References
            // from their own syntax nodes.
            ? new TypeReference(created.OriginalDefinition, DependencyKind.Instantiates, creation.GetLocation())
            : null;

    private static TypeReference? Call(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
        {
            return null;
        }

        // order.ToDto() as an extension method really calls a static method on the extension class.
        var owner = (method.ReducedFrom ?? method).ContainingType;
        return owner is null ? null : new TypeReference(owner.OriginalDefinition, DependencyKind.Calls, invocation.GetLocation());
    }

    private static TypeReference? TypeName(SimpleNameSyntax name, SemanticModel model)
    {
        if (IsCreatedType(name) || IsStaticCallReceiver(name) || model.GetSymbolInfo(name).Symbol is not ITypeSymbol type)
        {
            return null;
        }

        return new TypeReference(type, DependencyKind.References, name.GetLocation());
    }

    /// <summary>The <c>Order</c> in <c>new Order()</c> or <c>new Shop.Order()</c> is already an Instantiates edge.</summary>
    private static bool IsCreatedType(SimpleNameSyntax name)
    {
        SyntaxNode node = name;
        while (node.Parent is QualifiedNameSyntax qualified && qualified.Right == node)
        {
            node = qualified;
        }

        return node.Parent is ObjectCreationExpressionSyntax creation && creation.Type == node;
    }

    /// <summary>The <c>TaxCalculator</c> in <c>TaxCalculator.WithTax(x)</c> is already a Calls edge.</summary>
    private static bool IsStaticCallReceiver(SimpleNameSyntax name) =>
        name.Parent is MemberAccessExpressionSyntax access
        && access.Expression == name
        && access.Parent is InvocationExpressionSyntax invocation
        && invocation.Expression == access;
}
