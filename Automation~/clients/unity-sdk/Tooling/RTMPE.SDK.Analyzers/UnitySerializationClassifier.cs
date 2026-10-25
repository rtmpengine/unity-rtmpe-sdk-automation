using System.Linq;
using Microsoft.CodeAnalysis;

namespace RTMPE.SDK.Analyzers
{
    /// <summary>
    /// The single answer to "does Unity persist this field?" — the gate every
    /// conversion decision keys off, because retyping a Unity-serialized field
    /// in place silently discards the Inspector-assigned value stored in scene
    /// and prefab YAML. Unity serializes public instance fields and
    /// <c>[SerializeField]</c> non-public fields, and skips <c>static</c>,
    /// <c>const</c>, <c>readonly</c>, and <c>[NonSerialized]</c> members.
    /// </summary>
    internal static class UnitySerializationClassifier
    {
        public static bool IsUnitySerialized(
            IFieldSymbol field, INamedTypeSymbol serializeField, INamedTypeSymbol nonSerialized)
        {
            if (field.IsStatic || field.IsConst || field.IsReadOnly || field.IsImplicitlyDeclared)
            {
                return false;
            }

            if (HasAttribute(field, nonSerialized))
            {
                return false;
            }

            return field.DeclaredAccessibility == Accessibility.Public
                || HasAttribute(field, serializeField);
        }

        public static bool HasAttribute(IFieldSymbol field, INamedTypeSymbol attribute)
            => attribute is not null
                && field.GetAttributes().Any(
                    a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));
    }
}
