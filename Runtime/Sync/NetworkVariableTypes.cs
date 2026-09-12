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
    /// A network-synchronised <see cref="int"/> (Int32) value.
    /// Serialises as 4 little-endian bytes.
    /// </summary>
    public sealed class NetworkVariableInt : NetworkVariable<int>
    {
        /// <param name="owner">Owning <see cref="NetworkBehaviour"/>.</param>
        /// <param name="memberName">
        /// The member this variable is assigned to — pass <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">Starting value (default 0).</param>
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
    /// A network-synchronised <see cref="float"/> (Single, IEEE 754) value.
    /// Serialises as 4 little-endian bytes.
    /// </summary>
    public sealed class NetworkVariableFloat : NetworkVariable<float>
    {
        /// <param name="owner">Owning <see cref="NetworkBehaviour"/>.</param>
        /// <param name="memberName">
        /// The member this variable is assigned to — pass <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">Starting value (default 0.0f).</param>
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
        /// The mirror of <see cref="Deserialize"/>'s refusal, and the reason it
        /// is here rather than in <see cref="Serialize"/>: a writer cannot
        /// refuse, because it returns nothing and the entry's length is already
        /// decided by the time it runs.  Refusing the WRITE is what keeps the
        /// owner and its replicas holding the same value.
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
        /// Defensive validation against malicious or corrupted payloads:
        /// non-finite floats (NaN, ±Infinity) are rejected so an
        /// <c>OnValueChanged</c> subscriber that pipes the value into a UI
        /// lerp, a HP-bar fill, or any cumulative float math cannot inherit
        /// a NaN that would freeze the consumer permanently.  On rejection
        /// the prior value is preserved (no event fires) and a Unity warning
        /// is logged.
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
    /// A network-synchronised <see cref="bool"/> value.
    /// Serialises as a single byte (0x00 = false, 0x01 = true).
    /// </summary>
    public sealed class NetworkVariableBool : NetworkVariable<bool>
    {
        /// <param name="owner">Owning <see cref="NetworkBehaviour"/>.</param>
        /// <param name="memberName">
        /// The member this variable is assigned to — pass <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">Starting value (default false).</param>
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
    /// A network-synchronised <see cref="Vector3"/> value.
    /// Serialises as 12 little-endian bytes (3 × IEEE 754 f32: X, Y, Z).
    ///
   /// Equality uses <see cref="Vector3.Equals(Vector3)"/> which is exact
    /// IEEE 754 component-wise equality.  Set the value only when the change
    /// is intentional, not on every frame, to avoid unnecessary dirty marks.
    /// </summary>
    public sealed class NetworkVariableVector3 : NetworkVariable<Vector3>
    {
        /// <param name="owner">Owning <see cref="NetworkBehaviour"/>.</param>
        /// <param name="memberName">
        /// The member this variable is assigned to — pass <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">Starting value (default Vector3.zero).</param>
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
        /// The mirror of <see cref="Deserialize"/>'s componentwise refusal; see
        /// <c>NetworkVariable{T}.IsSendableValue</c> for why the write is where
        /// it has to be refused.
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
        /// Componentwise finiteness gate.  A NaN/Inf component would otherwise
        /// reach an <c>OnValueChanged</c> subscriber that assigns to
        /// <c>transform.position</c> or <c>transform.localScale</c>, which
        /// Unity persists indefinitely and which culls the affected
        /// GameObject's renderer until a domain reload.  Mirrors the
        /// Quaternion validation discipline already in place.
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
    /// A network-synchronised <see cref="Vector2"/> value.
    /// Serialises as 8 little-endian bytes (2 × IEEE 754 f32: X, Y).
    /// </summary>
    /// <remarks>
    /// Equality is <see cref="Vector2.Equals(Vector2)"/>, which is exact
    /// componentwise IEEE 754 equality — not Unity's <c>==</c> operator, which
    /// compares with a tolerance. Set the value when it changes meaningfully
    /// rather than every frame.
    /// </remarks>
    /// <remarks>
    /// 🚨 <b>Changing a member's type between <see cref="NetworkVariableVector2"/>
    /// and <see cref="NetworkVariableVector2Int"/> is a silent wire break.</b> A
    /// variable's identity is derived from the owning type and the member NAME —
    /// the value type is not folded in — and both wrappers put exactly eight
    /// bytes on the wire. So a peer still running the previous build reads the
    /// new bytes under the same id, at the same width, and reinterprets them:
    /// <c>Vector2Int(1, 2)</c> read as a <c>Vector2</c> is
    /// <c>(1.4E-45, 2.8E-45)</c>, and a <c>Vector2(1.5f, -2.25f)</c> read as a
    /// <c>Vector2Int</c> is <c>(1069547520, -1055916032)</c>. Neither end logs
    /// anything, because neither end can tell.
    /// <para>
    /// The hazard is not new — <see cref="NetworkVariableInt"/> and
    /// <see cref="NetworkVariableFloat"/> are four bytes each and have always
    /// had it — but this pair is the one a game is likely to migrate BETWEEN,
    /// which is why it is written here. Rename the member in the same change
    /// that retypes it: a new name is a new identity, and the old one simply
    /// stops arriving.
    /// </para>
    /// </remarks>
    public sealed class NetworkVariableVector2 : NetworkVariable<Vector2>
    {
        /// <param name="owner">Owning <see cref="NetworkBehaviour"/>.</param>
        /// <param name="memberName">
        /// The member this variable is assigned to — pass <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">Starting value (default Vector2.zero).</param>
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
        /// The mirror of <see cref="Deserialize"/>'s componentwise refusal; see
        /// <c>NetworkVariable{T}.IsSendableValue</c> for why a write is where it
        /// has to be refused — a writer returns nothing and the entry's length
        /// is already decided by the time it runs.
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
        /// Componentwise finiteness gate, for the reason
        /// <see cref="NetworkVariableVector3"/> states: a NaN or infinite
        /// component reaches an <c>OnValueChanged</c> subscriber that assigns it
        /// to a transform or a UI rect, and Unity persists that indefinitely.
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
    /// A network-synchronised <see cref="Vector2Int"/> value — a grid
    /// coordinate, a tile, a board square.
    /// Serialises as 8 little-endian bytes (2 × <b>Int32</b>: X, Y).
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Int32, not float32.</b> This is the one shipped vector type whose
    /// wire form is integral, and the difference is not cosmetic: an integer
    /// pair round-trips exactly at every magnitude, where a float32 silently
    /// loses precision past 2²⁴ — a tile coordinate of 20,000,001 would come
    /// back 20,000,000.
    /// <para>
    /// ⛔ And it carries no finiteness gate, unlike every other vector here.
    /// There is no non-finite <see cref="int"/>: a check would be one that can
    /// never fire, and a reader meeting it would reasonably conclude the value
    /// needs guarding. The refusal machinery is absent because the property it
    /// would protect cannot be violated.
    /// </para>
    /// </remarks>
    public sealed class NetworkVariableVector2Int : NetworkVariable<Vector2Int>
    {
        /// <param name="owner">Owning <see cref="NetworkBehaviour"/>.</param>
        /// <param name="memberName">
        /// The member this variable is assigned to — pass <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">Starting value (default (0, 0)).</param>
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
        /// <remarks>
        /// Total for this TYPE: every 8-byte pair names a point, so there is
        /// nothing here to refuse. ⚠️ The base class still declines to apply a
        /// value when the owner is absent or unspawned — that is a rule about
        /// the lifecycle, not about the encoding.
        /// </remarks>
        public override void Deserialize(BinaryReader reader)
        {
            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            ApplyFromWire(new Vector2Int(x, y));
        }
    }

    // ── Quaternion ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A network-synchronised <see cref="Quaternion"/> value.
    /// Serialises as 16 little-endian bytes (4 × IEEE 754 f32: X, Y, Z, W).
    ///
   /// <b>Important:</b> <c>default(Quaternion)</c> is <c>(0, 0, 0, 0)</c>,
    /// which is NOT a valid unit quaternion.  Pass <see cref="Quaternion.identity"/>
    /// as <paramref name="initialValue"/> for rotation variables:
    /// <code>new NetworkVariableQuaternion(owner, nameof(_rotation), Quaternion.identity)</code>
    /// </summary>
    public sealed class NetworkVariableQuaternion : NetworkVariable<Quaternion>
    {
        /// <param name="owner">Owning <see cref="NetworkBehaviour"/>.</param>
        /// <param name="memberName">
        /// The member this variable is assigned to — pass <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">
        /// Starting value.  Pass <see cref="Quaternion.identity"/> for
        /// rotation variables — <c>default(Quaternion)</c> is NOT identity.
        /// </param>
        public NetworkVariableQuaternion(
            NetworkBehaviour owner,
            string           memberName,
            Quaternion       initialValue = default)
            : base(owner, memberName, initialValue) { }

        /// <inheritdoc/>
        /// <remarks>
        /// The value is sanitised on the way out for the same reason every other
        /// raw-quaternion writer is: <see cref="Deserialize"/> refuses a
        /// non-unit quaternion and <b>keeps the prior value</b>, so a variable
        /// carrying one is refused by every peer for as long as it holds it.
        /// <c>default(Quaternion)</c> is (0,0,0,0) — not identity — and it is
        /// this constructor's own default, so
        /// <c>new NetworkVariableQuaternion(this, nameof(_rotation))</c> produced exactly that
        /// state until this was added.  The parameter doc has warned about it
        /// all along; a warning is not a defence.
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
        /// Defensive validation against malicious or corrupted payloads:
        /// <list type="bullet">
        ///  <item>NaN/Inf components are rejected (would propagate into
        ///  <c>transform.rotation</c> via game code and break physics).</item>
        ///  <item>Magnitude must lie in [0.9, 1.1] — a legitimate sender may
        ///  accumulate small rounding error, but anything outside that band
        ///  is either a bug or a hostile client trying to inject a non-rotation.</item>
        ///  <item>In-band quaternions are renormalised to unit length before
        ///  exposure so consumers always observe a valid rotation.</item>
        /// </list>
        /// On rejection the prior value is preserved (no <c>OnValueChanged</c>
        /// fires) and a Unity warning is logged to aid investigation.
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
