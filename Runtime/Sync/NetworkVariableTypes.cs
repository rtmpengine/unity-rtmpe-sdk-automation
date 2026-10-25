// RTMPE SDK — Runtime/Sync/NetworkVariableTypes.cs
//
// Concrete sealed NetworkVariable<T> implementations for the types most
// commonly synchronised in a multiplayer game.
//
// Each class:
//  • Seals the type (prevents further subclassing of concrete types).
//  • Calls the base constructor with (owner, memberName, initialValue).
//  • Implements Serialize   — uses BinaryWriter to emit value bytes LE.
//  • Implements Deserialize — uses BinaryReader + ApplyFromWire, which
//    stores the value AND raises OnValueChanged on the receiving client.
//    SetValueWithoutNotify is the PUBLIC "do not echo" contract and is
//    deliberately not the wire path: routing the wire through it left
//    OnValueChanged firing on no receiver at all.
//
// Wire format notes:
//  • BinaryWriter.Write(int/float/bool/etc.) on .NET uses the platform's
//    native byte order for multi-byte types.  All supported platforms
//    (x86, x64, ARM LE) are little-endian, so the output is LE — consistent
//    with TransformPacketBuilder (BitConverter.SingleToInt32Bits) and the Go
//    server's binary.LittleEndian encoding.
//  • BinaryWriter.Write(bool) writes a single 0x00/0x01 byte; ReadBoolean()
//    reads one byte and returns false iff the byte is 0. Symmetric. ✅
//  • BinaryWriter.Write(string) emits a 7-bit-encoded length prefix followed
//    by UTF-8 bytes (handled in NetworkVariableString, not here).
//
// Types provided:
//  NetworkVariableInt        — System.Int32    (4 bytes)
//  NetworkVariableFloat      — System.Single   (4 bytes, IEEE 754)
//  NetworkVariableBool       — System.Boolean  (1 byte)
//  NetworkVariableVector2    — UnityEngine.Vector2    (2 × 4 bytes)
//  NetworkVariableVector2Int — UnityEngine.Vector2Int (2 × 4 bytes, INT32)
//  NetworkVariableVector3    — UnityEngine.Vector3    (3 × 4 bytes)
//  NetworkVariableQuaternion — UnityEngine.Quaternion (4 × 4 bytes, XYZW)
//
// ⚠️ Vector2Int is an INTEGER pair and its serialisation is int32, not float32.
// Nothing about it can be NaN or infinite, so it carries none of the finiteness
// machinery its float siblings do — copying that across would be a check that
// can never fire, over a property the type cannot violate, and a reader would
// take the presence of the gate as evidence the value needs one.
//
// Note on Quaternion default value:
//  default(Quaternion) = Quaternion(0, 0, 0, 0) which is NOT a valid rotation.
//  Callers creating a rotation variable should pass Quaternion.identity
//  explicitly:
//      new NetworkVariableQuaternion(owner, nameof(_rotation), Quaternion.identity)
//
// Note on float NaN equality:
//  In .NET and Mono, IEquatable<float>.Equals(NaN, NaN) returns TRUE.
//  The Equals implementation special-cases NaN to satisfy the IEquatable
//  contract (an object must be equal to itself).  This differs from the
//  raw == operator which follows IEEE 754 (NaN != NaN).
//  Consequence: setting Value to NaN when it is already NaN is a no-op;
//  the second assignment does NOT fire OnValueChanged.

using System.IO;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Sync
{
    // ── int ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A replicated <see cref="int"/> value.
    /// </summary>
    public sealed class NetworkVariableInt : NetworkVariable<int>
    {
        /// <summary>
        /// Registers the variable with <paramref name="owner"/> and sets its
        /// initial value without raising <c>OnValueChanged</c>.
        /// </summary>
        /// <param name="owner">The component the variable belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the variable is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">The starting value (default 0).</param>
        public NetworkVariableInt(
            NetworkBehaviour owner,
            string           memberName,
            int              initialValue = default)
            : base(owner, memberName, initialValue) { }

        /// <inheritdoc/>
        public override void Serialize(BinaryWriter writer) => writer.Write(Value);

        /// <inheritdoc/>
        public override void Deserialize(BinaryReader reader)
            => ApplyFromWire(reader.ReadInt32());
    }

    // ── float ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A replicated <see cref="float"/> value. NaN and infinity are refused.
    /// </summary>
    public sealed class NetworkVariableFloat : NetworkVariable<float>
    {
        /// <summary>
        /// Registers the variable with <paramref name="owner"/> and sets its
        /// initial value without raising <c>OnValueChanged</c>.
        /// </summary>
        /// <param name="owner">The component the variable belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the variable is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">
        /// The starting value (default 0). NaN or infinity is replaced by 0, with
        /// a warning.
        /// </param>
        public NetworkVariableFloat(
            NetworkBehaviour owner,
            string           memberName,
            float            initialValue = default)
            : base(owner, memberName, initialValue) { }

        // Deserialize runs once per inbound entry, so a sender writing NaN
        // writes one console line per packet unless something bounds it.
        //
        // ⚠️ Static, not per variable. One update batch names many variable ids,
        // and each is a different instance — so a per-instance budget is freshly
        // open for every entry in the datagram and bounds nothing at all. The
        // sibling gate on NetworkBehaviour's unknown-id path says the same thing
        // in the same words, and this was written the other way first.
        private static long _lastNonFiniteFloatWarnTicks;

        internal static void ResetDiagnosticGatesForTest()
            => System.Threading.Interlocked.Exchange(ref _lastNonFiniteFloatWarnTicks, 0);

        /// <inheritdoc/>
        /// <remarks>
        /// This type refuses NaN and infinity.
        /// </remarks>
        protected override bool IsSendableValue(float value, out string reason)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                reason = $"{value} is not finite.";
                return false;
            }
            reason = null;
            return true;
        }

        /// <inheritdoc/>
        public override void Serialize(BinaryWriter writer) => writer.Write(Value);

        /// <inheritdoc/>
        /// <remarks>
        /// A received NaN or infinity is refused: the previous value is kept, no
        /// event is raised, and a warning is logged at most once a second.
        /// </remarks>
        public override void Deserialize(BinaryReader reader)
        {
            float v = reader.ReadSingle();
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastNonFiniteFloatWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] NetworkVariableFloat.Deserialize: rejected non-finite " +
                        $"value {v} — keeping prior value.");
                return;
            }
            ApplyFromWire(v);
        }
    }

    // ── bool ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A replicated <see cref="bool"/> value.
    /// </summary>
    public sealed class NetworkVariableBool : NetworkVariable<bool>
    {
        /// <summary>
        /// Registers the variable with <paramref name="owner"/> and sets its
        /// initial value without raising <c>OnValueChanged</c>.
        /// </summary>
        /// <param name="owner">The component the variable belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the variable is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">The starting value (default <see langword="false"/>).</param>
        public NetworkVariableBool(
            NetworkBehaviour owner,
            string           memberName,
            bool             initialValue = default)
            : base(owner, memberName, initialValue) { }

        /// <inheritdoc/>
        public override void Serialize(BinaryWriter writer) => writer.Write(Value);

        /// <inheritdoc/>
        public override void Deserialize(BinaryReader reader)
            => ApplyFromWire(reader.ReadBoolean());
    }

    // ── Vector3 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A replicated <see cref="Vector3"/> value. A NaN or infinite component is
    /// refused.
    /// </summary>
    /// <remarks>
    /// A write is compared with the value held exactly, component by component,
    /// so any difference marks the variable for sending. Write the value when it
    /// changes meaningfully rather than every frame.
    /// </remarks>
    public sealed class NetworkVariableVector3 : NetworkVariable<Vector3>
    {
        /// <summary>
        /// Registers the variable with <paramref name="owner"/> and sets its
        /// initial value without raising <c>OnValueChanged</c>.
        /// </summary>
        /// <param name="owner">The component the variable belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the variable is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">
        /// The starting value (default <c>Vector3.zero</c>). A value with a NaN or
        /// infinite component is replaced by <c>Vector3.zero</c>, with a warning.
        /// </param>
        public NetworkVariableVector3(
            NetworkBehaviour owner,
            string           memberName,
            Vector3          initialValue = default)
            : base(owner, memberName, initialValue) { }

        // See NetworkVariableFloat: the rate belongs to the sender, and one
        // datagram names many variables.
        private static long _lastNonFiniteVectorWarnTicks;

        internal static void ResetDiagnosticGatesForTest()
            => System.Threading.Interlocked.Exchange(ref _lastNonFiniteVectorWarnTicks, 0);

        /// <inheritdoc/>
        /// <remarks>
        /// This type refuses a value with a NaN or infinite component.
        /// </remarks>
        protected override bool IsSendableValue(Vector3 value, out string reason)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y) || !IsFinite(value.z))
            {
                reason = $"({value.x},{value.y},{value.z}) has a non-finite component.";
                return false;
            }
            reason = null;
            return true;
        }

        /// <inheritdoc/>
        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(Value.x);
            writer.Write(Value.y);
            writer.Write(Value.z);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// A received value with a NaN or infinite component is refused: the
        /// previous value is kept, no event is raised, and a warning is logged at
        /// most once a second.
        /// </remarks>
        public override void Deserialize(BinaryReader reader)
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastNonFiniteVectorWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] NetworkVariableVector3.Deserialize: rejected non-finite " +
                        $"vector ({x},{y},{z}) — keeping prior value.");
                return;
            }
            ApplyFromWire(new Vector3(x, y, z));
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }

    // ── Vector2 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A replicated <see cref="Vector2"/> value. A NaN or infinite component is
    /// refused.
    /// </summary>
    /// <remarks>
    /// <para>A write is compared with the value held exactly, component by
    /// component (not with the tolerance of Unity's <c>==</c> operator). Write
    /// the value when it changes meaningfully rather than every frame.</para>
    /// <para>Changing a member's type between <see cref="NetworkVariableVector2"/>
    /// and <see cref="NetworkVariableVector2Int"/> keeps the variable's identity,
    /// because the identity comes from the component type and the member name,
    /// and a client still running the previous build then reads the new values
    /// wrongly without any warning. Rename the member in the same change, so the
    /// new variable has a new identity. The same applies between
    /// <see cref="NetworkVariableInt"/> and <see cref="NetworkVariableFloat"/>.</para>
    /// </remarks>
    public sealed class NetworkVariableVector2 : NetworkVariable<Vector2>
    {
        /// <summary>
        /// Registers the variable with <paramref name="owner"/> and sets its
        /// initial value without raising <c>OnValueChanged</c>.
        /// </summary>
        /// <param name="owner">The component the variable belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the variable is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">
        /// The starting value (default <c>Vector2.zero</c>). A value with a NaN or
        /// infinite component is replaced by <c>Vector2.zero</c>, with a warning.
        /// </param>
        public NetworkVariableVector2(
            NetworkBehaviour owner,
            string           memberName,
            Vector2          initialValue = default)
            : base(owner, memberName, initialValue) { }

        // See NetworkVariableFloat: the rate belongs to the sender, and one
        // datagram names many variables, so the budget is per TYPE.
        private static long _lastNonFiniteVector2WarnTicks;

        internal static void ResetDiagnosticGatesForTest()
            => System.Threading.Interlocked.Exchange(ref _lastNonFiniteVector2WarnTicks, 0);

        /// <inheritdoc/>
        /// <remarks>
        /// This type refuses a value with a NaN or infinite component.
        /// </remarks>
        protected override bool IsSendableValue(Vector2 value, out string reason)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y))
            {
                reason = $"({value.x},{value.y}) has a non-finite component.";
                return false;
            }
            reason = null;
            return true;
        }

        /// <inheritdoc/>
        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(Value.x);
            writer.Write(Value.y);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// A received value with a NaN or infinite component is refused: the
        /// previous value is kept, no event is raised, and a warning is logged at
        /// most once a second.
        /// </remarks>
        public override void Deserialize(BinaryReader reader)
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            if (!IsFinite(x) || !IsFinite(y))
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastNonFiniteVector2WarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] NetworkVariableVector2.Deserialize: rejected non-finite " +
                        $"vector ({x},{y}) — keeping prior value.");
                return;
            }
            ApplyFromWire(new Vector2(x, y));
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }

    // ── Vector2Int ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A replicated <see cref="Vector2Int"/> value, such as a grid coordinate or
    /// a tile. Components are sent as 32-bit integers, so large values stay
    /// exact.
    /// </summary>
    /// <remarks>
    /// See <see cref="NetworkVariableVector2"/> before changing a member between
    /// the two types.
    /// </remarks>
    public sealed class NetworkVariableVector2Int : NetworkVariable<Vector2Int>
    {
        /// <summary>
        /// Registers the variable with <paramref name="owner"/> and sets its
        /// initial value without raising <c>OnValueChanged</c>.
        /// </summary>
        /// <param name="owner">The component the variable belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the variable is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">The starting value (default (0, 0)).</param>
        public NetworkVariableVector2Int(
            NetworkBehaviour owner,
            string           memberName,
            Vector2Int       initialValue = default)
            : base(owner, memberName, initialValue) { }

        /// <inheritdoc/>
        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(Value.x);
            writer.Write(Value.y);
        }

        /// <inheritdoc/>
        public override void Deserialize(BinaryReader reader)
        {
            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            ApplyFromWire(new Vector2Int(x, y));
        }
    }

    // ── Quaternion ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A replicated <see cref="Quaternion"/> value.
    /// </summary>
    /// <remarks>
    /// <para><c>default(Quaternion)</c> is (0, 0, 0, 0), which is not a valid
    /// rotation: pass <see cref="Quaternion.identity"/> as the initial
    /// value.</para>
    /// <para>A value that is not a valid unit quaternion is sent as the nearest
    /// valid rotation, with a warning.</para>
    /// </remarks>
    /// <example>
    /// <code>_rotation = new NetworkVariableQuaternion(this, nameof(_rotation), Quaternion.identity);</code>
    /// </example>
    public sealed class NetworkVariableQuaternion : NetworkVariable<Quaternion>
    {
        /// <summary>
        /// Registers the variable with <paramref name="owner"/> and sets its
        /// initial value without raising <c>OnValueChanged</c>.
        /// </summary>
        /// <param name="owner">The component the variable belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the variable is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">
        /// The starting value. Pass <see cref="Quaternion.identity"/>:
        /// <c>default(Quaternion)</c> is (0, 0, 0, 0), not the identity.
        /// </param>
        public NetworkVariableQuaternion(
            NetworkBehaviour owner,
            string           memberName,
            Quaternion       initialValue = default)
            : base(owner, memberName, initialValue) { }

        /// <inheritdoc/>
        /// <remarks>
        /// A value that is not a valid unit quaternion is written as the nearest
        /// valid rotation (the identity for a zero, NaN or infinite one), and a
        /// warning is logged at most once a second.
        /// </remarks>
        public override void Serialize(BinaryWriter writer)
        {
            var forWire = WireQuaternion.ForWire(Value, out bool substituted);
            if (substituted && RTMPE.Core.WarnGate.ShouldEmit(ref _lastRotationSubstitutionWarnTicks))
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkVariableQuaternion (id {VariableId}) holds a rotation the " +
                    "protocol does not carry (a zero, non-finite or grossly non-unit " +
                    "quaternion). Sending the nearest valid rotation instead — the original " +
                    "would have been refused by every peer, which keeps its PRIOR value, so the " +
                    "variable would never have propagated. `default(Quaternion)` is (0,0,0,0), " +
                    "not identity.");
            }
            writer.Write(forWire.x);
            writer.Write(forWire.y);
            writer.Write(forWire.z);
            writer.Write(forWire.w);
        }

        // Static and one-per-second, as in every other raw-quaternion writer.
        private static long _lastRotationSubstitutionWarnTicks;
        private static long _lastNonFiniteQuaternionWarnTicks;
        private static long _lastNonUnitQuaternionWarnTicks;

        internal static void ResetDiagnosticGatesForTest()
        {
            System.Threading.Interlocked.Exchange(ref _lastNonFiniteQuaternionWarnTicks, 0);
            System.Threading.Interlocked.Exchange(ref _lastNonUnitQuaternionWarnTicks, 0);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// A received rotation with a NaN or infinite component, or with a length
        /// outside 0.9 to 1.1, is refused: the previous value is kept, no event is
        /// raised, and a warning is logged at most once a second. Other values are
        /// normalised to unit length.
        /// </remarks>
        public override void Deserialize(BinaryReader reader)
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            float w = reader.ReadSingle();

            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z) || !IsFinite(w))
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastNonFiniteQuaternionWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] NetworkVariableQuaternion.Deserialize: rejected non-finite " +
                        $"quaternion ({x},{y},{z},{w}) — keeping prior value.");
                return;
            }

            float magSq = x * x + y * y + z * z + w * w;
            if (magSq < 0.81f || magSq > 1.21f) // [0.9², 1.1²]
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastNonUnitQuaternionWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] NetworkVariableQuaternion.Deserialize: rejected non-unit " +
                        $"quaternion (magSq={magSq:F4}, expected ≈1) — keeping prior value.");
                return;
            }

            // Renormalise small rounding error so consumers always read a unit quaternion.
            float invMag = 1f / Mathf.Sqrt(magSq);
            ApplyFromWire(new Quaternion(x * invMag, y * invMag, z * invMag, w * invMag));
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
