using System;
using System.Collections.Generic;
using System.Linq;

namespace RTMPE.SDK.Conversion.Core
{
    /// <summary>
    /// The closed, deterministic field-type → NetworkVariable map. Exactly the
    /// eight types the runtime ships an author-constructible wrapper for; nothing
    /// is configurable and nothing else may ever be emitted. Lists are
    /// deliberately absent (construction exists but usage rewriting is
    /// unstudied), and Color/double/long have no runtime wrapper at all.
    /// </summary>
    public static class NetworkVariableTypeMap
    {
        /// <summary>
        /// The map itself, declared once — field type as an author spells it
        /// (<c>int</c>, <c>Vector2Int</c>) to the wrapper that carries it.
        /// </summary>
        /// <remarks>
        /// 🚨 One table rather than a <c>switch</c>, and the reason is measured:
        /// the IDE's conversion analyzer restated this set in a predicate of its
        /// own, so `Vector2` and `Vector2Int` reached the map, the transform, the
        /// runtime and the documentation — and the diagnostic that OFFERS the
        /// conversion never fired for either. Half a feature, with every suite
        /// green. A consumer that needs the set now reads it here.
        /// </remarks>
        public static readonly IReadOnlyDictionary<string, string> ByFieldTypeName =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["int"]        = "NetworkVariableInt",
                ["float"]      = "NetworkVariableFloat",
                ["bool"]       = "NetworkVariableBool",
                ["string"]     = "NetworkVariableString",
                ["Vector2"]    = "NetworkVariableVector2",
                // ⚠️ Its own entry and its own wrapper: Vector2Int is an INTEGER
                // pair whose wire form is int32. Mapping it onto the float
                // wrapper would round every coordinate past 2²⁴ and lose nothing
                // visibly until a board got large.
                ["Vector2Int"] = "NetworkVariableVector2Int",
                ["Vector3"]    = "NetworkVariableVector3",
                ["Quaternion"] = "NetworkVariableQuaternion",
            };

        /// <summary>
        /// The types this map carries, ordered, as a sentence fragment — for the
        /// refusals that name the set an author may choose from.
        /// </summary>
        /// <remarks>
        /// Asked of the map so the sentence cannot describe a set the tool does
        /// not carry: a type added above joins every refusal by being added.
        /// Ordered because a reason that reorders itself between runs is a reason
        /// nobody can diff.
        /// </remarks>
        public static readonly string SupportedTypeList =
            string.Join(", ", ByFieldTypeName.Keys.OrderBy(name => name, StringComparer.Ordinal));

        /// <summary>
        /// What to do instead when the closed map does not carry a field's type.
        /// </summary>
        /// <remarks>
        /// 🔑 One sentence, two refusals. The transform refuses through the
        /// library API and the CLI refuses before the transform is ever called —
        /// and the CLI is the path an author actually takes, so a remedy written
        /// only into the transform reached nobody. Two copies of one instruction
        /// is how they would disagree; this is the single copy.
        /// <para>
        /// ⚠️ Every clause was measured against the shipped runtime rather than
        /// reasoned. <c>NetworkVariable{T}</c> constrains T to a struct that is
        /// <c>IEquatable{T}</c>; <c>ApplyFromWire</c> is the only member that
        /// raises <c>OnValueChanged</c> from a wire value and is
        /// <c>protected internal</c> precisely so a type outside the package can
        /// reach it; and the base arm has to gate its own writes, because the
        /// refusal <c>NetworkVariableString</c> uses is <c>private protected</c>
        /// and a subclass in a game assembly cannot call it — without that clause
        /// an author's variable stores an unowned write, marks itself dirty, and
        /// is never sent.
        /// </para>
        /// </remarks>
        public const string DeclareYourOwnVariableRemedy =
            "a type of the game's own is replicated by declaring the variable rather than by converting"
            + " the field: derive from NetworkVariable<T> where T is a struct implementing IEquatable<T>,"
            + " applying an inbound value through ApplyFromWire because that is what raises"
            + " OnValueChanged; or, for anything else, from NetworkVariableBase as NetworkVariableString"
            + " does — which also means declaring your own event and your own ownership gate, because the"
            + " base's refusal of an unowned write is not reachable from outside the package. Implement"
            + " Serialize and Deserialize either way. The base constructor registers the variable with its"
            + " owner, so the flush loop finds it with nothing else changed."
            // 🔴 The sentence above described the from-scratch path and stopped
            // there, so an author holding a List<T> was sent to NetworkVariableBase
            // — its own event, its own ownership gate, both halves of the wire —
            // while NetworkVariableList<T> shipped in the same package with four
            // concrete types and exactly two members to fill in. An integrator
            // building Snake read the refusal, concluded a list could not be
            // replicated, and was right about the tool and wrong about the SDK.
            // ⛔ Held by WhatARefusalOffersInsteadTests, which reads the runtime
            // file: a remedy naming a type the package does not carry is worse
            // than one that names nothing.
            + " Where the field is a List<T> the package already carries NetworkVariableList<T>:"
            + " derive from it and implement WriteElement and ReadElement — two members for a custom"
            + " element type — rather than starting from NetworkVariableBase, and note that"
            + " NetworkVariableListInt, NetworkVariableListFloat, NetworkVariableListVector3 and"
            + " NetworkVariableListString are already concrete";

        /// <summary>
        /// Maps a field's type name — the unqualified spelling (<c>int</c>,
        /// <c>Vector3</c>) — to the NetworkVariable type that carries it.
        /// </summary>
        public static bool TryMap(string fieldTypeName, out string networkVariableTypeName)
        {
            if (fieldTypeName != null && ByFieldTypeName.TryGetValue(fieldTypeName, out networkVariableTypeName))
            {
                return true;
            }

            networkVariableTypeName = null;
            return false;
        }
    }
}
