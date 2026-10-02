using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RTMPE.SDK.Transforms
{
    /// <summary>
    /// Answers, for a syntax-only rewriter, whether a name it is about to emit as a
    /// bare identifier is already spoken for.
    /// <para>
    /// The transforms emit fixed spellings — <c>IsOwner</c>, <c>RPC</c> — that are
    /// expected to bind to inherited SDK members. C# resolves the innermost
    /// declaration first, so any member of the same name on the type, or any local
    /// of that name in the body, wins instead: the emitted code then reads the
    /// author's value, or does not compile. Without a semantic model that binding
    /// cannot be checked after the fact, so it is refused before the edit.
    /// </para>
    /// <para>
    /// The reading is bounded in one direction and deliberately loose in the other.
    /// A base type in another file is invisible to a syntax pass — the same
    /// documented limit the RPC transform's own shadow checks carry. In the other
    /// direction it is over-eager on purpose: the whole declaration is read, so a
    /// name introduced only inside a nested lambda or local function refuses too,
    /// even though it could not capture the binding at the outer statement. Ordering
    /// scopes needs a semantic model; declining a safe edit costs an author one
    /// rename, and taking the binding silently costs them a bug they cannot see.
    /// </para>
    /// </summary>
    internal static class NameBinding
    {
        /// <summary>
        /// A short description of what already holds <paramref name="name"/> in the
        /// scope <paramref name="member"/> sits in, or <c>null</c> when the name is
        /// free. The description completes the sentence "'X' is already …".
        /// </summary>
        public static string Describe(MemberDeclarationSyntax member, string name)
        {
            string local = DescribeLocal(member, name);
            if (local != null)
            {
                return local;
            }

            var type = member.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            return type is null ? null : DescribeOnType(type, name);
        }

        /// <summary>
        /// As <see cref="Describe"/>, for a name emitted against a type rather than
        /// inside one member — the receiver of a <c>this.Name(...)</c> call, which
        /// only a member of the type can rebind.
        /// </summary>
        public static string DescribeOnType(TypeDeclarationSyntax type, string name)
        {
            // A member may not share its enclosing type's name (CS0542), and a bare
            // identifier that matches it resolves to the type, not to an inherited
            // member — so the name is taken either way.
            if (type.Identifier.ValueText == name)
            {
                return "the name of the enclosing type";
            }

            foreach (var member in type.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field
                        when field.Declaration.Variables.Any(v => v.Identifier.ValueText == name):
                        return "a field on the type";
                    case PropertyDeclarationSyntax property when property.Identifier.ValueText == name:
                        return "a property on the type";
                    case MethodDeclarationSyntax method when method.Identifier.ValueText == name:
                        return "a method on the type";
                    case EventFieldDeclarationSyntax eventField
                        when eventField.Declaration.Variables.Any(v => v.Identifier.ValueText == name):
                    case EventDeclarationSyntax eventDeclaration
                        when eventDeclaration.Identifier.ValueText == name:
                        return "an event on the type";
                    case DelegateDeclarationSyntax nestedDelegate
                        when nestedDelegate.Identifier.ValueText == name:
                        return "a delegate declared on the type";
                    case BaseTypeDeclarationSyntax nested when nested.Identifier.ValueText == name:
                        return "a nested type";
                }
            }

            return null;
        }

        // Everything that introduces the spelling into a narrower scope than the
        // type. A local's scope is its whole enclosing block, so one declared after
        // the insertion point still captures the name there (CS0841) — position is
        // not part of this test.
        private static string DescribeLocal(SyntaxNode scope, string name)
        {
            foreach (var node in scope.DescendantNodes())
            {
                switch (node)
                {
                    case VariableDeclaratorSyntax declarator
                        when declarator.Identifier.ValueText == name
                            && declarator.Parent?.Parent is not FieldDeclarationSyntax:
                        return "a local declaration";
                    case ParameterSyntax parameter when parameter.Identifier.ValueText == name:
                        return "a parameter";
                    case SingleVariableDesignationSyntax designation when designation.Identifier.ValueText == name:
                        return "a pattern variable";
                    case CatchDeclarationSyntax catchDeclaration when catchDeclaration.Identifier.ValueText == name:
                        return "a catch variable";
                    case ForEachStatementSyntax forEach when forEach.Identifier.ValueText == name:
                        return "a foreach iteration variable";
                    case LocalFunctionStatementSyntax localFunction when localFunction.Identifier.ValueText == name:
                        return "a local function";
                    case FromClauseSyntax fromClause when fromClause.Identifier.ValueText == name:
                    case LetClauseSyntax letClause when letClause.Identifier.ValueText == name:
                    case JoinClauseSyntax joinClause when joinClause.Identifier.ValueText == name:
                    case JoinIntoClauseSyntax joinInto when joinInto.Identifier.ValueText == name:
                    case QueryContinuationSyntax continuation when continuation.Identifier.ValueText == name:
                        return "a query range variable";
                }
            }

            return null;
        }
    }
}
