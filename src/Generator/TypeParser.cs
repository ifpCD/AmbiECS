using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AmbiECS.Generator;

static class TypeParser
{
    public const string AmbiAttribute = "AmbiECS.AmbiAttribute";

    const string InputAttribute = "AmbiECS.InputAttribute";
    const string StateAttribute = "AmbiECS.StateAttribute";
    const string ScratchAttribute = "AmbiECS.ScratchAttribute";
    const string GlueAttribute = "AmbiECS.GlueAttribute";
    const string TrackAttribute = "AmbiECS.TrackAttribute";
    const string ReadbackAttribute = "AmbiECS.ReadbackAttribute";
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
        HashSet<string> marked = [];
        ImmutableArray<ReadbackModel>.Builder readbacks = ImmutableArray.CreateBuilder<ReadbackModel>();

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
                else if (name == TrackAttribute)
                    marked.Add(member.Name);
                else if (
                    name == ReadbackAttribute
                    && member is IPropertySymbol property
                    && attribute.ConstructorArguments.FirstOrDefault().Value is string source
                    && Partial(property, token) is PartialModel declaration
                )
                {
                    string? after = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == "After").Value.Value as string;
                    readbacks.Add(new ReadbackModel(member.Name, source, after, declaration));
                }
            }
        }

        CodeIndex code = CodeIndex.Of(type, token);

        Dictionary<string, PartialModel> tracked = new();
        HashSet<string> partials = [];

        foreach (ISymbol member in type.GetMembers())
        {
            if (member is not IPropertySymbol property)
                continue;

            PartialModel? declaration = property.IsStatic || code.Implemented.Contains(property.Name) ? null : Partial(property, token);

            if (declaration is not { Settable: true })
            {
                if (marked.Contains(property.Name))
                    errors.Add($"[Track] on {property.Name} takes a settable partial property, which the generator implements.");

                continue;
            }

            partials.Add(property.Name);

            if (marked.Contains(property.Name) || glued.Exists(glue => glue.Member.Name == property.Name))
                tracked[property.Name] = declaration;
        }

        ImmutableArray<GlueModel>.Builder glues = ImmutableArray.CreateBuilder<GlueModel>();
        List<HashSet<string>> sources = [];
        int bits = 0;

        foreach ((ISymbol member, string column) in glued)
        {
            ColumnModel? target = columns.FirstOrDefault(candidate => candidate.Name == column);

            if (target is null)
            {
                errors.Add($"[Glue] on {member.Name} names '{column}', which is not an [Input], [State] or [Scratch] column of {type.Name}.");
                continue;
            }

            bool isTracked = member is IPropertySymbol && tracked.ContainsKey(member.Name);
            HashSet<string> reads = member is IPropertySymbol && !isTracked ? code.Reads(member.Name, partials) : [];

            foreach (string read in reads.Where(read => !tracked.ContainsKey(read)))
                errors.Add($"{member.Name} reads {read}, an unimplemented partial property: mark it [Track] to have it tracked.");

            reads.RemoveWhere(read => !tracked.ContainsKey(read));
            ulong bit = isTracked || reads.Count > 0 ? 1UL << Math.Min(bits++, 63) : 0;

            glues.Add(Glue(member, target, isTracked, bit, errors));
            sources.Add(reads);
        }

        ImmutableArray<PropertyModel>.Builder properties = ImmutableArray.CreateBuilder<PropertyModel>();

        foreach (KeyValuePair<string, PartialModel> property in tracked)
        {
            ImmutableArray<int>.Builder writes = ImmutableArray.CreateBuilder<int>();
            ulong mask = 0;

            for (int i = 0; i < glues.Count; i++)
            {
                if (glues[i].Member == property.Key)
                {
                    writes.Add(i);
                    mask |= glues[i].Bit;
                }
            }

            for (int i = 0; i < glues.Count; i++)
            {
                if (sources[i].Contains(property.Key))
                {
                    writes.Add(i);
                    mask |= glues[i].Bit;
                }
            }

            properties.Add(new PropertyModel(property.Key, property.Value, mask, new(writes.ToImmutable())));
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
            code.HasSyncHook,
            new(columns.ToImmutable()),
            new(glues.ToImmutable()),
            new(properties.ToImmutable()),
            new(readbacks.ToImmutable()),
            new(isBehaviour ? Messages(type, manual) : ImmutableArray<MessageModel>.Empty),
            new(errors.ToImmutable())
        );
    }

    static ColumnModel Column(IFieldSymbol field, ColumnKind kind, AttributeData attribute, ImmutableArray<string>.Builder errors)
    {
        int stride = 1;
        int perRow = 1;
        int length = 0;
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

        return new ColumnModel(field.Name, kind, storage ?? Storage.NativeArray, elementName, stride, perRow, length, initial);
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

    static GlueModel Glue(
        ISymbol member,
        ColumnModel column,
        bool tracked,
        ulong bit,
        ImmutableArray<string>.Builder errors
    )
    {
        ITypeSymbol type = member is IPropertySymbol property ? property.Type : ((IFieldSymbol)member).Type;

        ITypeSymbol element = type;
        bool sequence = false;
        bool isList = false;

        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            element = array.ElementType;
            sequence = true;
        }
        else if (type is INamedTypeSymbol { MetadataName: "List`1" } list && In(list, "System.Collections.Generic"))
        {
            element = list.TypeArguments[0];
            sequence = true;
            isList = true;
        }

        if (column.Kind != ColumnKind.Input)
            errors.Add($"{member.Name} feeds {column.Name}, which is not an [Input] column.");
        else if (column.Stride > 1 && !sequence)
            errors.Add($"{member.Name} feeds the strided column {column.Name}, which takes a List<T> or T[].");
        else if (member is IPropertySymbol { GetMethod: null })
            errors.Add($"{member.Name} feeds {column.Name} but has no getter to write its row from.");

        return new GlueModel(
            member.Name,
            column.Name,
            sequence,
            isList,
            IsAmbi(element),
            element.ToDisplayString(Qualified),
            tracked,
            bit
        );
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
        }

        return messages.ToImmutable();
    }

    static IMethodSymbol? Declared(INamedTypeSymbol type, string name) =>
        type.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(method => method.Parameters.Length == 0 && !method.IsStatic);

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

    sealed class CodeIndex
    {
        readonly Dictionary<string, List<SyntaxNode>> _bodies = new();

        public HashSet<string> Implemented { get; } = [];

        public bool HasSyncHook { get; private set; }

        public static CodeIndex Of(INamedTypeSymbol type, CancellationToken token)
        {
            CodeIndex code = new();

            foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(token) is not TypeDeclarationSyntax declaration)
                    continue;

                foreach (MemberDeclarationSyntax member in declaration.Members)
                {
                    if (member is PropertyDeclarationSyntax property)
                    {
                        string name = property.Identifier.ValueText;
                        bool partial = property.Modifiers.Any(SyntaxKind.PartialKeyword);

                        if (property.ExpressionBody is not null)
                            code.Add(name, property.ExpressionBody, partial);

                        foreach (AccessorDeclarationSyntax accessor in property.AccessorList?.Accessors ?? default)
                        {
                            if (((SyntaxNode?)accessor.Body ?? accessor.ExpressionBody) is { } body)
                                code.Add(name, body, partial);
                        }
                    }
                    else if (member is MethodDeclarationSyntax method)
                    {
                        string name = method.Identifier.ValueText;
                        code.HasSyncHook |= name == "OnRowSynced";

                        if (((SyntaxNode?)method.Body ?? method.ExpressionBody) is { } body)
                            code.Add(name, body, false);
                    }
                }
            }

            return code;
        }

        public HashSet<string> Reads(string member, HashSet<string> properties)
        {
            HashSet<string> visited = [member];
            HashSet<string> reads = [];
            Stack<string> pending = new();
            pending.Push(member);

            while (pending.Count > 0)
            {
                if (!_bodies.TryGetValue(pending.Pop(), out List<SyntaxNode>? bodies))
                    continue;

                foreach (SyntaxNode body in bodies)
                {
                    foreach (IdentifierNameSyntax identifier in body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                    {
                        string name = identifier.Identifier.ValueText;

                        if (properties.Contains(name))
                            reads.Add(name);
                        else if (visited.Add(name))
                            pending.Push(name);
                    }
                }
            }

            return reads;
        }

        void Add(string name, SyntaxNode body, bool partial)
        {
            if (!_bodies.TryGetValue(name, out List<SyntaxNode>? bodies))
            {
                bodies = [];
                _bodies[name] = bodies;
            }

            bodies.Add(body);

            if (partial)
                Implemented.Add(name);
        }
    }
}
