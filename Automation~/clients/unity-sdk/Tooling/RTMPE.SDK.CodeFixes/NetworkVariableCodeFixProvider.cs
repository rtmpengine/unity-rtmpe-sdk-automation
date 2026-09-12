using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RTMPE.SDK.Analyzers;
using RTMPE.SDK.Conversion.Core;
using RTMPE.SDK.Transforms;

namespace RTMPE.SDK.CodeFixes
{
    /// <summary>
    /// One-click fix for <c>RTMPE2002</c>: converts the field in place and
    /// names the member it is assigned to.
    ///
    /// <para>🔑 No record is read, and none is needed. An identity is derived
    /// from the declaring type and the member's own name, so the edit carries
    /// everything the identity depends on and a <c>CodeAction</c>'s inability to
    /// persist a sidecar atomically with a document edit stops mattering. This
    /// used to be offered only where a ledger already held an issued id, because
    /// the fix could not mint one.</para>
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NetworkVariableCodeFixProvider)), Shared]
    public sealed class NetworkVariableCodeFixProvider : CodeFixProvider
    {
        private const string InPlaceTitle = "Convert to NetworkVariable";
        private const string CompanionTitle = "Add companion NetworkVariable";
        private const string CompanionSuffix = "Net";

        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(DiagnosticIds.ConversionNetworkVariableCandidate);

        // Deliberately no FixAll: each fix's edit set overlaps its siblings' (the
        // shared using and the injected OnNetworkSpawn hook), and BatchFixer merges
        // per-diagnostic text changes computed against the ORIGINAL document — a
        // multi-member "fix all" could drop conflicting edits and diverge from the
        // headless host's single-plan output. Batch conversion belongs to the CLI
        // hosts (`make convert` / `make gen-rpc`), which build one plan per type.
        public override FixAllProvider GetFixAllProvider() => null;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var (root, field) = await CodeFixRoots.FindAsync<FieldDeclarationSyntax>(context).ConfigureAwait(false);
            var type = field?.FirstAncestorOrSelf<ClassDeclarationSyntax>();
            if (root is null || field is null || type is null)
            {
                return;
            }

            var declarator = root.FindNode(context.Span)?.FirstAncestorOrSelf<VariableDeclaratorSyntax>()
                ?? (field.Declaration.Variables.Count == 1 ? field.Declaration.Variables[0] : null);
            if (declarator is null)
            {
                return;
            }

            string memberName = declarator.Identifier.ValueText;
            if (!NetworkVariableTypeMap.TryMap(TypeName(field.Declaration.Type), out string nvType))
            {
                return;
            }

            var model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (model?.GetDeclaredSymbol(type, context.CancellationToken) is not INamedTypeSymbol typeSymbol)
            {
                return;
            }

            // ⛔ No record is read, and none is needed. This fix used to refuse
            // unless a sidecar had already issued an id for the member, because
            // an id was a scarce thing only the deterministic host could hand
            // out and an IDE guessing one would burn it. Identity is now derived
            // from the member's own name, so the fix emits exactly what the host
            // would and the two cannot disagree.
            bool companionArm = IsSerializedSyntactically(field);
            string plannedMember = companionArm ? memberName + CompanionSuffix : memberName;

            var plan = new ConversionPlan(new[]
            {
                new PlannedConversion(
                    memberName, nvType,
                    companionArm ? ConversionArm.Companion : ConversionArm.InPlace,
                    companionArm ? plannedMember : null),
            });

            var newRoot = NetworkVariableGenerationTransform.Apply(root, type, plan);
            if (ReferenceEquals(newRoot, root))
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    companionArm ? CompanionTitle : InPlaceTitle,
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(newRoot)),
                    equivalenceKey: nameof(NetworkVariableCodeFixProvider)),
                context.Diagnostics[0]);
        }

        private static bool IsSerializedSyntactically(FieldDeclarationSyntax field)
        {
            bool isPublic = field.Modifiers.Any(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword);
            bool serializeField = HasAttribute(field, "SerializeField");
            bool nonSerialized = HasAttribute(field, "NonSerialized");
            return (isPublic || serializeField) && !nonSerialized;
        }

        private static bool HasAttribute(FieldDeclarationSyntax field, string shortName)
        {
            foreach (var list in field.AttributeLists)
            {
                foreach (var attribute in list.Attributes)
                {
                    string name = attribute.Name is QualifiedNameSyntax qualified
                        ? qualified.Right.Identifier.ValueText
                        : (attribute.Name as IdentifierNameSyntax)?.Identifier.ValueText;
                    if (name == shortName || name == shortName + "Attribute")
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string TypeName(TypeSyntax type)
            => type switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                PredefinedTypeSyntax predefined => predefined.Keyword.ValueText,
                _ => type?.ToString(),
            };

        // Namespaces joined by '.', nested types by '+' — the metadata spelling
        // the ledger's filename and type binding are keyed on.
        private static string FullMetadataName(INamedTypeSymbol type)
        {
            string name = type.MetadataName;
            for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            {
                name = outer.MetadataName + "+" + name;
            }

            for (var ns = type.ContainingNamespace; ns is { IsGlobalNamespace: false }; ns = ns.ContainingNamespace)
            {
                name = ns.Name + "." + name;
            }

            return name;
        }
    }
}
