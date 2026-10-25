using Burndown.Core;
using Microsoft.CodeAnalysis;

namespace Burndown.Analysis;

/// <summary>A type referenced by a declaration, before it is resolved into graph edges.</summary>
internal readonly record struct TypeReference(ITypeSymbol Type, DependencyKind Kind, Location? Location);

internal static class DependencyExtractor
{
    /// <summary>
    /// Lists the types a type's declaration refers to: its base type, interfaces, and the types of its
    /// constructor parameters, fields, properties, and method signatures. Bodies are handled by
    /// <see cref="BodyDependencyExtractor"/>.
    /// </summary>
    public static IEnumerable<TypeReference> Extract(INamedTypeSymbol type)
    {
        var declaration = type.Locations.FirstOrDefault(l => l.IsInSource);

        if (type.BaseType is not null)
        {
            yield return new TypeReference(type.BaseType, DependencyKind.Inherits, declaration);
        }

        var interfaceKind = type.TypeKind == TypeKind.Interface ? DependencyKind.Inherits : DependencyKind.Implements;
        foreach (var @interface in type.Interfaces)
        {
            yield return new TypeReference(@interface, interfaceKind, declaration);
        }

        foreach (var member in type.GetMembers())
        {
            // Skips compiler-generated members such as property backing fields and default constructors.
            if (member.IsImplicitlyDeclared)
            {
                continue;
            }

            switch (member)
            {
                case IFieldSymbol field:
                    yield return new TypeReference(field.Type, DependencyKind.Field, FirstSourceLocation(field));
                    break;

                case IPropertySymbol property:
                    yield return new TypeReference(property.Type, DependencyKind.Property, FirstSourceLocation(property));
                    break;

                case IMethodSymbol { MethodKind: MethodKind.Constructor } constructor:
                    foreach (var parameter in constructor.Parameters)
                    {
                        yield return new TypeReference(
                            parameter.Type, DependencyKind.ConstructorParameter, FirstSourceLocation(parameter));
                    }
                    break;

                case IMethodSymbol
                {
                    MethodKind: MethodKind.Ordinary or MethodKind.DelegateInvoke or MethodKind.ExplicitInterfaceImplementation
                        or MethodKind.UserDefinedOperator or MethodKind.Conversion,
                } method:
                    var location = FirstSourceLocation(method);
                    yield return new TypeReference(method.ReturnType, DependencyKind.MethodSignature, location);
                    foreach (var parameter in method.Parameters)
                    {
                        yield return new TypeReference(parameter.Type, DependencyKind.MethodSignature, location);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Expands a referenced type into the named types it is built from, so that
    /// <c>Dictionary&lt;string, List&lt;Order&gt;&gt;[]</c> yields Dictionary, string, List, and Order.
    /// Constructed generics are reduced to their definition (<c>List&lt;Order&gt;</c> becomes <c>List&lt;T&gt;</c>).
    /// Type parameters yield nothing; unresolved types are returned so the caller can report them.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> Flatten(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                foreach (var inner in Flatten(array.ElementType))
                {
                    yield return inner;
                }
                break;

            case IPointerTypeSymbol pointer:
                foreach (var inner in Flatten(pointer.PointedAtType))
                {
                    yield return inner;
                }
                break;

            case INamedTypeSymbol named:
                yield return named.OriginalDefinition;

                // typeof(Handler<,>) has no real type arguments; Roslyn fills the slots with placeholder error
                // types, which would otherwise be reported as unresolved.
                if (named.IsUnboundGenericType)
                {
                    break;
                }

                foreach (var argument in named.TypeArguments)
                {
                    foreach (var inner in Flatten(argument))
                    {
                        yield return inner;
                    }
                }
                break;
        }
    }

    private static Location? FirstSourceLocation(ISymbol symbol) => symbol.Locations.FirstOrDefault(l => l.IsInSource);
}
