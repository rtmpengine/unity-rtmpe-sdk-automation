// RTMPE SDK — Runtime/Sync/WireQuaternion.cs
//
// One definition of "a rotation this protocol carries", shared by the senders
// and the receivers.
//
// Every inbound path that reads a raw quaternion refuses one that is not close
// enough to unit length — a zero quaternion is not a rotation at all, and a
// grossly non-unit one turns into NaN the moment a parent transform multiplies
// it.  The refusal is correct.  What was missing is its counterpart:
//
// ⚠️ "Close enough" is TWO bands, not one, and this file states the stricter.
// The three raw-wire parsers band the SQUARE at [0.9, 1.1] — magnitude
// [0.949, 1.049].  `NetworkVariableQuaternion.Deserialize` and
// `NetworkRigidbody` band the square at [0.81, 1.21] — magnitude [0.9, 1.1],
// about twice as wide.  Nothing here changes that split; what matters is that
// the value this file returns satisfies BOTH, because the strict band is a
// subset of the wide one.  An earlier version of this comment claimed a single
// band across every reader, and that was simply false.
// the SENDERS wrote whatever they were handed, so the SDK could emit a pose its
// own parser would drop, and the loss is silent and total —
//
//   • a Spawn carrying `default(Quaternion)` builds, relays through the gateway
//     verbatim, and is refused by every peer, so the spawner is the only client
//     that ever sees the object;
//   • a StateDelta record with a zero rotation is refused mid-frame, and the
//     concatenated batch has no per-record length to resynchronise on, so
//     `HandleStateSyncPacket` stops there and every object AFTER it in that
//     frame loses its update too.
//
// `default(Quaternion)` is (0,0,0,0), not identity, so an integrator building
// `new TransformState { Position = p }` or calling a spawn overload without a
// rotation produces exactly that value.

using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// The wire's rotation contract: the band every raw-quaternion parser
    /// enforces, and the sanitiser every raw-quaternion builder applies so it
    /// cannot emit a value the band refuses.
    /// </summary>
    internal static class WireQuaternion
    {
        /// <summary>
        /// Lower bound on |q|² for a rotation the wire carries.
        /// </summary>
        /// <remarks>
        /// ±0.1 on the SQUARE — so ±0.05 on the magnitude, not ±0.1: wide enough
        /// for accumulated float drift on a quaternion that has been multiplied
        /// a few thousand times, narrow enough that a zero or a runaway value is
        /// refused.  Declared once and
        /// read by both directions — a band the sender and the receiver each
        /// spell for themselves is two bands that agree until one is edited.
        /// </remarks>
        internal const float MinMagSq = 0.9f;

        /// <summary>Upper bound on |q|², the mirror of <see cref="MinMagSq"/>.</summary>
        internal const float MaxMagSq = 1.1f;

        /// <summary>
        /// Below this, a quaternion carries no rotation to preserve and is
        /// replaced by identity rather than normalised — dividing by a
        /// magnitude this small amplifies float noise into an arbitrary
        /// rotation, which is worse than the honest answer of "none stated".
        /// </summary>
        private const float DegenerateMagSq = 1e-12f;

        /// <summary>
        /// Whether <paramref name="q"/> is a rotation every inbound parser in
        /// this SDK accepts.  Non-finite components are refused first: NaN
        /// compares false against both bounds, so a band test alone admits them.
        /// </summary>
        internal static bool IsAcceptable(Quaternion q)
        {
            // ⚠️ The explicit finiteness test is NOT redundant, and the reason is
            // the shape of the expression rather than its meaning.  Written as
            // `magSq >= Min && magSq <= Max`, NaN answers false and is refused;
            // written the way the parsers state the same rule —
            // `if (magSq < Min || magSq > Max) return false;` — NaN answers
            // false to BOTH comparisons and is ADMITTED.  The two spellings are
            // one edit apart, and only this line survives the edit.
            if (!IsFinite(q.x) || !IsFinite(q.y) || !IsFinite(q.z) || !IsFinite(q.w))
                return false;

            float magSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            return magSq >= MinMagSq && magSq <= MaxMagSq;
        }

        /// <summary>
        /// The value to put on the wire for <paramref name="q"/>: itself when it
        /// is already acceptable, its normalisation when it carries a rotation
        /// but not a unit-length one, and <see cref="Quaternion.identity"/> when
        /// it carries none at all.
        /// </summary>
        /// <param name="q">the caller's rotation.</param>
        /// <param name="substituted">
        /// <see langword="true"/> when the returned value is not the one passed
        /// in — i.e. when the wire would have refused the caller's rotation.
        /// Benign drift inside the band does not set it, so a caller reporting
        /// on this reports a real defect rather than ordinary float noise.
        /// </param>
        /// <remarks>
        /// The result always satisfies <see cref="IsAcceptable"/>, which is what
        /// makes "this SDK cannot emit a pose its own parser refuses" a property
        /// rather than a convention.
        /// </remarks>
        internal static Quaternion ForWire(Quaternion q, out bool substituted)
        {
            // No finiteness arm here, deliberately.  NaN and the infinities
            // propagate into `magSq`, fail the band test, survive the degenerate
            // test, poison the normalisation and are caught by the post-condition
            // below — identity, with `substituted` true, which is the answer a
            // dedicated arm would have produced.  It was written and then
            // deleted: a line no test can distinguish from its absence is a
            // comment that compiles.
            float magSq = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (magSq >= MinMagSq && magSq <= MaxMagSq)
            {
                substituted = false;
                return q;
            }

            substituted = true;
            if (magSq <= DegenerateMagSq) return Quaternion.identity;

            float invMag = 1f / (float)System.Math.Sqrt(magSq);
            var normalised = new Quaternion(
                q.x * invMag, q.y * invMag, q.z * invMag, q.w * invMag);

            // The post-condition is ENFORCED rather than reasoned about.  Every
            // caller writes this value straight to the wire, so "the arithmetic
            // should land in the band" is not good enough — if it did not, the
            // honest answer is identity.
            //
            // ⚠️ It is not theoretical.  `magSq` reaches +∞ while every
            // component is finite — 1e20 squared overflows a float — and
            // `1/sqrt(∞)` is 0, so the "normalised" result is (0,0,0,0):
            // precisely the value this function exists to remove, produced by
            // the arithmetic meant to remove it.  A dedicated arm for that one
            // case was written first and then deleted: it and this line are
            // redundant, no test can tell them apart, and shipping the narrower
            // of two lines that do the same work is how a guard nobody can
            // justify survives into the next audit.
            return IsAcceptable(normalised) ? normalised : Quaternion.identity;
        }

        private static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }
}
