// RTMPE SDK — Runtime/Core/InputPayload.cs
//
// One frame of player input, tick-stamped and wire-serialisable.
//
// Wire format (13 bytes, all little-endian):
//  [0..3]   tick   : u32  — monotone client tick counter
//  [4..7]   move_x : f32  — horizontal movement input (saturated to [-1, 1])
//  [8..11]  move_y : f32  — vertical   movement input (saturated to [-1, 1])
//  [12]     flags  : u8   — bit 0 = Jump, bits 1-7 reserved
//
// The synchronization service's input-batch parser rejects any axis value
// outside the [-1.01, +1.01] tolerance window
// (modules/synchronization/domain/entities/input_payload.go::isValidAxis)
// and drops the whole batch silently on the receive side.  Saturating
// to [-1, 1] at the sender boundary keeps every legitimate analogue-stick
// noise (1.0001 over-shoot) inside the server's tolerance margin while
// preventing a buggy controller that produces an out-of-range value
// (e.g. raw mouse-delta in world units) from silently freezing player
// movement.  NaN / ±Infinity are still rejected outright at the sender —
// the receive-side parser rejects them as well, and the local CSP buffer
// must not carry non-finite inputs into reconciliation replay.
//
// No UnityEngine dependency so this file compiles in both Unity and plain
// .NET xunit projects without stubs.

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// One tick of player input for client-side prediction: returned by
    /// <see cref="NetworkBehaviour.GatherInput"/> and passed back to
    /// <see cref="NetworkBehaviour.ApplyInput"/>.
    /// </summary>
    public struct InputPayload
    {
        // ── Fields ─────────────────────────────────────────────────────────────

        /// <summary>The tick this input belongs to. The SDK sets it; leave it 0.</summary>
        public uint  Tick;

        /// <summary>
        /// Horizontal input, from -1 to 1. Values outside the range are clamped
        /// when the input is sent; NaN and infinity are refused.
        /// </summary>
        public float MoveX;

        /// <summary>
        /// Forward and back input, from -1 to 1, clamped and checked the same way
        /// as <see cref="MoveX"/>.
        /// </summary>
        public float MoveY;

        /// <summary>Whether jump is pressed.</summary>
        public bool  Jump;

        // ── Wire layout ────────────────────────────────────────────────────────

        /// <summary>The size of a serialised <see cref="InputPayload"/> in bytes.</summary>
        public const int WireSize = 13;

        private const byte FlagJump = 0x01;

        // ── Serialisation ──────────────────────────────────────────────────────

        /// <summary>
        /// Writes this input into <paramref name="buf"/> at
        /// <paramref name="offset"/>, clamping <see cref="MoveX"/> and
        /// <see cref="MoveY"/> to the range -1 to 1.
        /// </summary>
        /// <param name="buf">The destination; at least <see cref="WireSize"/> bytes must follow <paramref name="offset"/>.</param>
        /// <param name="offset">Where to start writing.</param>
        /// <exception cref="ArgumentNullException"><paramref name="buf"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">Fewer than <see cref="WireSize"/> bytes follow <paramref name="offset"/>.</exception>
        /// <exception cref="InvalidOperationException"><see cref="MoveX"/> or <see cref="MoveY"/> is NaN or infinite.</exception>
        public void WriteTo(byte[] buf, int offset)
        {
            if (buf == null) throw new ArgumentNullException(nameof(buf));
            if ((uint)offset > (uint)buf.Length || buf.Length - offset < WireSize)
                throw new ArgumentException(
                    $"InputPayload.WriteTo: destination buffer is too small " +
                    $"(buf.Length={buf.Length}, offset={offset}, required={WireSize}).",
                    nameof(buf));
            // Sender-side finiteness gate.  A custom controller that
            // produces NaN MoveX (e.g. division by zero in a deadzone
            // computation) would otherwise be enqueued into the local
            // InputBuffer un-validated.  On the next reconciliation,
            // ReplayUnackedInputs hands the payload to user
            // ApplyInput which propagates NaN into transform.position;
            // the same payload is also sent to the gateway, where the
            // peer parser throws and tears the channel down.  Surfacing
            // the misuse at the sender boundary keeps the CSP simulation
            // domain finite and prevents a single controller bug from
            // promoting into a session-killing protocol error.
            if (float.IsNaN(MoveX) || float.IsInfinity(MoveX))
                throw new InvalidOperationException("InputPayload.MoveX is not finite");
            if (float.IsNaN(MoveY) || float.IsInfinity(MoveY))
                throw new InvalidOperationException("InputPayload.MoveY is not finite");
            // Saturate to the gameplay [-1, 1] envelope.  The synchronization
            // service's ParseInputBatch (input_payload.go::isValidAxis)
            // rejects any axis outside the [-1.01, +1.01] tolerance window
            // and silently drops the whole batch; clamping at the sender
            // keeps a buggy or hostile controller from forcing the player
            // into the silent-drop failure mode.  Analogue-stick noise
            // (1.0001 over-shoot from controller deadzone math) survives
            // unchanged because it sits inside the saturation band.
            float moveX = MoveX < -1f ? -1f : (MoveX > 1f ? 1f : MoveX);
            float moveY = MoveY < -1f ? -1f : (MoveY > 1f ? 1f : MoveY);
            WriteU32LE(buf, offset,      Tick);
            WriteF32LE(buf, offset + 4,  moveX);
            WriteF32LE(buf, offset + 8,  moveY);
            buf[offset + 12] = Jump ? FlagJump : (byte)0;
        }

        /// <summary>
        /// Reads an input from <paramref name="buf"/> at <paramref name="offset"/>.
        /// </summary>
        /// <param name="buf">The source; at least <see cref="WireSize"/> bytes must follow <paramref name="offset"/>.</param>
        /// <param name="offset">Where to start reading.</param>
        /// <returns>The input read.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="buf"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">Fewer than <see cref="WireSize"/> bytes follow <paramref name="offset"/>.</exception>
        /// <exception cref="InvalidOperationException">A movement value in the data is NaN or infinite.</exception>
        public static InputPayload ReadFrom(byte[] buf, int offset)
        {
            if (buf == null) throw new ArgumentNullException(nameof(buf));
            if ((uint)offset > (uint)buf.Length || buf.Length - offset < WireSize)
                throw new ArgumentException(
                    $"InputPayload.ReadFrom: source buffer is too small " +
                    $"(buf.Length={buf.Length}, offset={offset}, required={WireSize}).",
                    nameof(buf));
            float moveX = ReadF32LE(buf, offset + 4);
            float moveY = ReadF32LE(buf, offset + 8);
            // Reject NaN / ±Inf at the parser boundary. These values would
            // otherwise propagate into Unity transforms / physics and pin
            // the simulation in an unrecoverable state.
            if (float.IsNaN(moveX) || float.IsInfinity(moveX))
            {
                throw new InvalidOperationException("InputPayload.MoveX is not finite");
            }
            if (float.IsNaN(moveY) || float.IsInfinity(moveY))
            {
                throw new InvalidOperationException("InputPayload.MoveY is not finite");
            }
            return new InputPayload
            {
                Tick  = ReadU32LE(buf, offset),
                MoveX = moveX,
                MoveY = moveY,
                Jump  = (buf[offset + 12] & FlagJump) != 0,
            };
        }

        // ── Private wire helpers ───────────────────────────────────────────────

        private static void WriteU32LE(byte[] b, int o, uint v)
        {
            b[o]     = (byte) v;
            b[o + 1] = (byte)(v >>  8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        private static uint ReadU32LE(byte[] b, int o)
            => b[o]
            | ((uint)b[o + 1] <<  8)
            | ((uint)b[o + 2] << 16)
            | ((uint)b[o + 3] << 24);

        private static void WriteF32LE(byte[] b, int o, float v)
        {
            int bits = BitConverter.SingleToInt32Bits(v);
            b[o]     = (byte) bits;
            b[o + 1] = (byte)(bits >>  8);
            b[o + 2] = (byte)(bits >> 16);
            b[o + 3] = (byte)(bits >> 24);
        }

        private static float ReadF32LE(byte[] b, int o)
        {
            int bits = b[o]
                     | (b[o + 1] <<  8)
                     | (b[o + 2] << 16)
                     | (b[o + 3] << 24);
            return BitConverter.Int32BitsToSingle(bits);
        }
    }
}
