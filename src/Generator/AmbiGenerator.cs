using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AmbiECS.Generator;

// One output per [Ambi] class: its IAmbiRegistrant<T> implementation, members, glue and messages.
[Generator(LanguageNames.CSharp)]
public sealed class AmbiGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<TypeModel> types = context.SyntaxProvider.ForAttributeWithMetadataName(
            TypeParser.AmbiAttribute,
            static (node, _) => node is ClassDeclarationSyntax,
            static (attributed, token) => TypeParser.Parse((INamedTypeSymbol)attributed.TargetSymbol, attributed.Attributes[0], token)
        );

        context.RegisterSourceOutput(types, static (output, type) => output.AddSource(type.HintName, TypeEmitter.Emit(type)));
    }
}
