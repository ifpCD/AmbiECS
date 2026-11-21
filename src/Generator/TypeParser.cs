using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AmbiECS.Generator;

// Symbols to models. Anything the emitter cannot turn into valid code becomes an error, emitted as #error in place of the type.
static class TypeParser
{
    public const string AmbiAttribute = "AmbiECS.AmbiAttribute";

    const string InputAttribute = "AmbiECS.InputAttribute";
    const string StateAttribute = "AmbiECS.StateAttribute";
    const string ScratchAttribute = "AmbiECS.ScratchAttribute";
    const string GlueAttribute = "AmbiECS.GlueAttribute";
    const string CleanupAttribute = "Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanupAttribute";

    static readonly string[] MessageNames = ["OnEnable", "OnDisable", "OnDestroy", "OnValidate"];

    static readonly SymbolDisplayFormat Qualified = SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
        SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
    );

    public static TypeModel Parse(INamedTypeSymbol type, AttributeData ambi, CancellationToken token)
    {
        ImmutableArray<string>.Builder errors = ImmutableArray.CreateBuilder<string>();

        if (type.ContainingType is not null || type.TypeParameters.Length > 0)
            errors.Add($"{type.Name} has to be a top-level, non-generic class to be an Ambi type.");

        int capacity = 64;
        bool fixedCapacity = false;
        bool retain = true;
        bool manual = false;

        foreach (KeyValuePair<string, TypedConstant> argument in ambi.NamedArguments)
        {
            object? value = argument.Value.Value;

            switch (argument.Key)
            {
                case "Capacity" when value is int count:
                    capacity = count;
                    break;
                case "Fixed" when value is bool flag:
                    fixedCapacity = flag;
                    break;
                case "RetainColumnsWhenEmpty" when value is bool flag:
                    retain = flag;
                    break;
                case "Registration" when value is int registration:
                    manual = registration == 1;
                    break;
            }
        }

        ImmutableArray<ColumnModel>.Builder columns = ImmutableArray.CreateBuilder<ColumnModel>();
        List<(ISymbol Member, string Column)> glued = [];

        foreach (ISymbol member in type.GetMembers())
        {
            foreach (AttributeData attribute in member.GetAttributes())
            {
                string? name = attribute.AttributeClass?.ToDisplayString();

                ColumnKind? kind = name switch
                {
                    InputAttribute => ColumnKind.Input,
                    StateAttribute => ColumnKind.State,
                    ScratchAttribute => ColumnKind.Scratch,
                    _ => null,
                };

                if (kind is ColumnKind columnKind && member is IFieldSymbol { IsStatic: true } field)
                    columns.Add(Column(field, columnKind, attribute, errors));
                else if (
                    name == GlueAttribute
                    && member is IFieldSymbol or IPropertySymbol
                    && attribute.ConstructorArguments.FirstOrDefault().Value is string column
                )
                    glued.Add((member, column));
            }
        }

        ImmutableArray<GlueModel>.Builder glues = ImmutableArray.CreateBuilder<GlueModel>();

        foreach ((ISymbol member, string column) in glued)
        {
            ColumnModel? target = columns.FirstOrDefault(candidate => candidate.Name == column);

            if (target is null)
                errors.Add($"[Glue] on {member.Name} names '{column}', which is not an [Input], [State] or [Scratch] column of {type.Name}.");
            else
                glues.Add(Glue(member, target, errors, token));
        }

        foreach (ColumnModel column in columns)
        {
            if (column.Storage == Storage.TransformAccessArray && !glues.Any(glue => glue.Column == column.Name))
                errors.Add($"{column.Name} needs a [Glue] member to load its rows' transforms from.");
        }

        bool isBehaviour = DerivesFrom(type, "UnityEngine", "MonoBehaviour");

        return new TypeModel(
            type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            type.Name,
            isBehaviour,
            type.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == CleanupAttribute),
            capacity,
            fixedCapacity,
            retain,
            new(columns.ToImmutable()),
            new(glues.ToImmutable()),
            new(isBehaviour ? Messages(type, manual) : ImmutableArray<MessageModel>.Empty),
            new(errors.ToImmutable())
        );
    }

    static ColumnModel Column(IFieldSymbol field, ColumnKind kind, AttributeData attribute, ImmutableArray<string>.Builder errors)
    {
        int stride = 1;
        int perRow = 1;
        int length = 0;
        bool loadOnly = false;
        TypedConstant? constant = null;
        string? defaultFrom = null;

        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            object? value = argument.Value.Value;

            switch (argument.Key)
            {
                case "Stride" when value is int count:
                    stride = Math.Max(1, count);
                    break;
                case "PerRow" when value is int count:
                    perRow = Math.Max(1, count);
                    break;
                case "Length" when value is int count:
                    length = count;
                    break;
                case "LoadOnly" when value is bool flag:
                    loadOnly = flag;
                    break;
                case "Default":
                    constant = argument.Value;
                    break;
                case "DefaultFrom" when value is string member:
                    defaultFrom = member;
                    break;
            }
        }

        Storage? storage = StorageOf(field.Type, out ITypeSymbol? element);
        string elementName = element?.ToDisplayString(Qualified) ?? "";

        if (storage is null || (storage == Storage.TransformAccessArray && kind != ColumnKind.Input))
        {
            string allowed = kind == ColumnKind.Input ? "NativeArray<T>, T[] or TransformAccessArray" : "NativeArray<T> or T[]";
            errors.Add($"{field.Name} is a {field.Type.ToDisplayString()}: [{kind}] columns are {allowed}.");
        }

        string initial = "default";

        if (defaultFrom is not null)
            initial = defaultFrom;
        else if (constant is { IsNull: false } typed && SymbolDisplay.FormatPrimitive(typed.Value!, true, false) is string literal)
            initial = $"({elementName})({literal})";

        return new ColumnModel(field.Name, kind, storage ?? Storage.NativeArray, elementName, stride, perRow, length, loadOnly, initial);
    }

    static Storage? StorageOf(ITypeSymbol type, out ITypeSymbol? element)
    {
        element = null;

        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            element = array.ElementType;
            return Storage.ManagedArray;
        }

        if (type is INamedTypeSymbol { MetadataName: "NativeArray`1" } native && In(native, "Unity.Collections"))
        {
            element = native.TypeArguments[0];
            return Storage.NativeArray;
        }

        if (type is INamedTypeSymbol { MetadataName: "TransformAccessArray" } transforms && In(transforms, "UnityEngine.Jobs"))
            return Storage.TransformAccessArray;

        return null;
    }

    static GlueModel Glue(ISymbol member, ColumnModel column, ImmutableArray<string>.Builder errors, CancellationToken token)
    {
        ITypeSymbol type = member is IPropertySymbol property ? property.Type : ((IFieldSymbol)member).Type;
        PartialModel? partial = member is IPropertySymbol declared ? Partial(declared, token) : null;
        bool readBack = partial is { ReadBack: true };

        ITypeSymbol element = type;
        string source = member.Name;
        bool sequence = false;

        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            element = array.ElementType;
            sequence = true;
        }
        else if (type is INamedTypeSymbol { MetadataName: "List`1" } list && In(list, "System.Collections.Generic"))
        {
            element = list.TypeArguments[0];
            sequence = true;
            source = $"global::System.Runtime.InteropServices.CollectionsMarshal.AsSpan({member.Name})";
        }

        if (readBack)
        {
            if (column.Stride > 1 || column.Storage == Storage.TransformAccessArray)
                errors.Add($"{member.Name} reads {column.Name} back, which takes an indexed column without a stride.");
        }
        else if (column.Kind != ColumnKind.Input)
            errors.Add($"{member.Name} feeds {column.Name}, which is not an [Input] column. Glue a get-only partial property to read it back.");
        else if (column.Stride > 1 && !sequence)
            errors.Add($"{member.Name} feeds the strided column {column.Name}, which takes a List<T> or T[].");

        return new GlueModel(member.Name, column.Name, sequence, IsAmbi(element), element.ToDisplayString(Qualified), source, partial);
    }

    // The defining declaration of a partial property, which the generator implements. Null for anything written by hand.
    static PartialModel? Partial(IPropertySymbol property, CancellationToken token)
    {
        if (
            property.DeclaringSyntaxReferences.Length != 1
            || property.DeclaringSyntaxReferences[0].GetSyntax(token) is not PropertyDeclarationSyntax syntax
            || !syntax.Modifiers.Any(SyntaxKind.PartialKeyword)
            || syntax.AccessorList is null
        )
            return null;

        bool hasGetter = false;
        string getModifiers = "";
        string setKeyword = "";
        string setModifiers = "";

        foreach (AccessorDeclarationSyntax accessor in syntax.AccessorList.Accessors)
        {
            if (accessor.Body is not null || accessor.ExpressionBody is not null)
                return null; // an implementing declaration

            string modifiers = string.Concat(accessor.Modifiers.Select(modifier => modifier.Text + " "));

            if (accessor.Keyword.IsKind(SyntaxKind.GetKeyword))
            {
                hasGetter = true;
                getModifiers = modifiers;
            }
            else
            {
                setKeyword = accessor.Keyword.Text;
                setModifiers = modifiers;
            }
        }

        return new PartialModel(
            string.Join(" ", syntax.Modifiers.Select(modifier => modifier.Text)),
            property.Type.ToDisplayString(Qualified),
            hasGetter,
            getModifiers,
            setKeyword,
            setModifiers
        );
    }

    // The messages a component needs and has not declared itself; a declared one calls into Ambi by hand.
    static ImmutableArray<MessageModel> Messages(INamedTypeSymbol type, bool manual)
    {
        ImmutableArray<MessageModel>.Builder messages = ImmutableArray.CreateBuilder<MessageModel>();

        foreach (string name in MessageNames)
        {
            if (manual && name is "OnEnable" or "OnDisable")
                continue;

            if (Declared(type, name) is not null)
                continue;

            IMethodSymbol? inherited = Inherited(type, name);

            if (inherited is null)
                messages.Add(new MessageModel(name, "", false, false));
            else if ((inherited.IsVirtual || inherited.IsAbstract || inherited.IsOverride) && !inherited.IsSealed)
                messages.Add(new MessageModel(name, AccessibilityOf(inherited, type), true, !inherited.IsAbstract));

            // a non-virtual inherited message can't be extended without hiding it: left to the user
        }

        return messages.ToImmutable();
    }

    static IMethodSymbol? Declared(INamedTypeSymbol type, string name) =>
        type.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(method => method.Parameters.Length == 0 && !method.IsStatic);

    // From the user's own base classes, down to UnityEngine.
    static IMethodSymbol? Inherited(INamedTypeSymbol type, string name)
    {
        for (INamedTypeSymbol? current = type.BaseType; current is not null && !In(current, "UnityEngine"); current = current.BaseType)
        {
            if (Declared(current, name) is IMethodSymbol method)
                return method;
        }

        return null;
    }

    static string AccessibilityOf(IMethodSymbol method, INamedTypeSymbol type) =>
        method.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedOrInternal => SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, type.ContainingAssembly)
                ? "protected internal"
                : "protected",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => "protected",
        };

    static bool IsAmbi(ITypeSymbol type) =>
        type.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == AmbiAttribute)
        || type.AllInterfaces.Any(contract => contract.MetadataName == "IAmbiRegistrant`1" && In(contract, "AmbiECS"));

    static bool DerivesFrom(INamedTypeSymbol type, string ns, string name)
    {
        for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name == name && In(current, ns))
                return true;
        }

        return false;
    }

    static bool In(ISymbol symbol, string ns) => symbol.ContainingNamespace?.ToDisplayString() == ns;
}
