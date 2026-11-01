// RTMPE SDK — Tests/Runtime/Phase0FlatBuffersTests.cs
//
// Phase 0 (2026-04-25) FlatBuffers wiring sanity-check.  Mirrors the Go
// equivalent at modules/synchronization/infrastructure/contracts/contracts_phase0_test.go
// so a regression in any one of the three runtimes (Rust gateway, Go services,
// Unity SDK) is caught by its own ecosystem's CI gate — none of them have to
// trust the other two.
//
// What this test proves:
//
//  1. The vendored `Google.FlatBuffers` runtime under
//     Runtime/Infrastructure/Serialization/FlatBuffers/ compiles inside the
//     SDK assembly and links against the generated bindings under
//     Runtime/Infrastructure/Serialization/Generated/.
//  2. A round-trip encode + decode of `RTMPE.States.InputPayload` returns
//     every field byte-for-byte intact.
//
// What this test does NOT do:
//
//  • It does NOT touch the network, NetworkManager, or NetworkTransform.
//    Phase 0 ships zero wire-format change; sending FlatBuffers payloads
//    to the gateway is Phase 1+ work.

using NUnit.Framework;
using Google.FlatBuffers;
// Type alias to disambiguate from RTMPE.Core.InputPayload (the 13-byte
// binary struct used by the SDK input pipeline).  The FlatBuffers table is
// a separate, richer type intended for Phase 1 wire migration.
using FbInputPayload = RTMPE.States.InputPayload;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Contracts")]
    public class Phase0FlatBuffersTests
    {
        // The table as the schema declares it today: tick, move_x, move_y and
        // flags.  It used to carry a player id, an input sequence and a button
        // word, and this test kept asserting those after the schema dropped
        // them — so the assembly it lives in did not compile (audit P3-M9).
        [Test]
        public void Phase0_InputPayload_RoundTripsBytewise()
        {
            const uint  wantTick  = 7;
            const float wantMoveX = 0.5f;
            const float wantMoveY = -0.25f;
            const byte  wantFlags = 0b1011;

            var b = new FlatBufferBuilder(initialSize: 64);

            FbInputPayload.StartInputPayload(b);
            FbInputPayload.AddTick(b, wantTick);
            FbInputPayload.AddMoveX(b, wantMoveX);
            FbInputPayload.AddMoveY(b, wantMoveY);
            FbInputPayload.AddFlags(b, wantFlags);
            var end = FbInputPayload.EndInputPayload(b);
            b.Finish(end.Value);

            // Reparse the same bytes via the public root accessor — exactly
            // the path a real consumer would take.
            var bb  = new ByteBuffer(b.SizedByteArray());
            var got = FbInputPayload.GetRootAsInputPayload(bb);

            Assert.AreEqual(wantTick,  got.Tick);
            Assert.AreEqual(wantMoveX, got.MoveX);
            Assert.AreEqual(wantMoveY, got.MoveY);
            Assert.AreEqual(wantFlags, got.Flags);
        }
    }
}
