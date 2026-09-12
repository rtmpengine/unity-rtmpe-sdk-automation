// RTMPE SDK — Runtime/Sync/WireVector.cs
//
// One definition of "a coordinate this protocol carries", shared by the senders
// and the receivers — the positional half of what WireQuaternion states for
// rotations.
//
// Every parser of a raw-wire RECORD — transform, physics, spawn — refuses a
// non-finite component (the narrowing is deliberate; see the type's remarks
// below).  NaN and the infinities propagate through interpolation, physics and
// every parent-transform multiplication that touches them, so a receiver that
// admitted one would poison state the sender never sees.  The refusal is correct.  What was missing is its
// counterpart — the SENDERS wrote whatever they were handed, so the SDK could
// build a record it knows will be thrown away.  What that costs, per path, was
// measured against the deployed services rather than assumed:
//
//   • a Spawn carrying a non-finite position builds and the gateway relays the
//     payload VERBATIM (`nats/forwarder.rs`, `forward_spawn` — no coordinate
//     validation), so every peer's SpawnPacketParser refuses it and the spawner
//     is the only client that can ever see the object.  This is the severe one;
//   • a StateSync uplink with a non-finite position or scale is refused at the
//     GATEWAY — `parse_full_transform` reads through `read_f32_le`, which
//     answers None on any non-finite component — so the datagram is dropped and
//     the client is told nothing.  One record per datagram, so nothing else goes
//     with it;
//   • a PhysicsSync frame reaches `physics_sync_drop_handler`, which checks the
//     framing and drops it: no Sync Service consumer ingests rigidbody state, so
//     nothing parses the coordinates at all today.
//
// ⛔ **There is no StateDelta cascade here**, and an earlier version of this
// comment said there was.  That cascade belongs to the ROTATION (SYNC-RD-03):
// the sync service's `writeQuaternionLE` clamps only NON-FINITE components, so a
// `(0,0,0,0)` quaternion survives into a downlink batch and the batch has no
// per-record length to resynchronise on.  Its `writeFloat32LE` clamps a
// non-finite coordinate to 0, and the gateway refuses one before that anyway.
//
// 🔑 So the loss this file prevents on the transform and physics paths is one
// update and the bandwidth to send it — and, in every case, the thing that
// actually persists: the caller's `_lastSent*` write.  A non-finite value
// recorded as the last one sent makes every later change-detection comparison
// false, because a comparison against NaN is false, and the field stops
// broadcasting for good.
//
// ⚠️ The repair is NOT the rotation's, and that is the whole reason this is a
// separate file rather than another method on WireQuaternion.  A non-unit
// quaternion has a nearest valid rotation, so WireQuaternion.ForWire can hand
// the wire a substitute and say so.  A non-finite coordinate has no nearest
// valid coordinate: the origin and the last known value are both somewhere the
// object is not, and putting either on the wire teleports it in front of every
// other player — a silent, plausible-looking failure that is strictly worse
// than the drop it would replace.  So this file only ASKS; the builders refuse
// the record and report, and the caller does not advance the baseline it would
// have measured the next send against.

using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// The wire's coordinate contract: the finiteness the raw-wire parsers
    /// enforce, declared once so a builder can ask the same question before it
    /// writes.
    /// </summary>
    /// <remarks>
    /// <para>A predicate the sender and the receiver each spell for themselves
    /// is two predicates that agree until one of them is edited.</para>
    /// <para>⛔ "The raw-wire parsers" is narrower than "every reader in this
    /// SDK", deliberately.  <c>NetworkVariableListFloat</c> /
    /// <c>NetworkVariableListVector3</c> and the RPC argument serialiser gate
    /// nothing on either side, so they carry a non-finite value end to end —
    /// symmetric, and a separate question from this asymmetry.
    /// ⛔ <c>NetworkVariableFloat</c>, <c>NetworkVariableVector3</c> and
    /// <c>NetworkVariableVector2</c> are NO LONGER asymmetric: each overrides
    /// <c>IsSendableValue</c> and refuses a non-finite component on the WRITE,
    /// which is what SYNC-RD-10 asked for and what this paragraph used to record
    /// as outstanding. <c>NetworkVariableVector2Int</c> carries no such gate and
    /// needs none — there is no non-finite integer, and a check that can never
    /// fire tells a reader the value needs guarding.</para>
    /// </remarks>
    internal static class WireVector
    {
        /// <summary>
        /// Whether <paramref name="v"/> is neither NaN nor ±Infinity.
        /// </summary>
        /// <remarks>
        /// .NET Standard 2.1 has <c>float.IsFinite</c>, but the SDK's Unity
        /// floor predates it, so the test is spelled out.
        /// </remarks>
        internal static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>Whether every component of <paramref name="v"/> is finite.</summary>
        internal static bool IsFinite(Vector3 v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);

        /// <summary>Whether both components of <paramref name="v"/> are finite.</summary>
        /// <remarks>
        /// Spelled with a <c>Vector2</c>'s components rather than taking a
        /// <c>Vector2</c> parameter: three of the five test projects that
        /// compile this file stub UnityEngine and declare no <c>Vector2</c> at
        /// all, so the overload would put a type they do not have into a file
        /// they need for a rule about <c>Vector3</c>.  Every caller is in the
        /// 2-D physics encoder.
        /// </remarks>
        internal static bool IsFinite(float x, float y) => IsFinite(x) && IsFinite(y);
    }
}
