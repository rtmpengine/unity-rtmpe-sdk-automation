// RTMPE SDK — Runtime/Core/Rpc/RecentRpcIdentityWindow.cs
//
// The identities of the Enhanced RPCs delivered most recently, so that a
// second delivery of one can be told from the first.  Delivery is meant to be
// once: the sender's reliable ladder re-sends a frame whose acknowledgement
// was late, and the gateway's ARQ window (S4-01) routes each sequence once —
// so a repeat reaching a receiver is the property failing somewhere, and this
// window is the receiver's only way to see it.  It counts; it never refuses.
//
// The identity is the gateway-attested sender session and the request id the
// sender drew for that one call (RequestIdAllocator: 32 random bits, fresh
// per send).  Two calls of one sender collide on the id with probability
// about n²/2³³ over a window holding n of that sender's calls — one in five
// hundred at the window's full depth — which is a rate a diagnostic count
// can carry and a refusal could not; that is the reason it counts.
//
// Cost, paid in every build: the ring is 64 KB from birth and the set grows
// to about 240 KB at full depth — it resizes past 4096 to 8419 slots of 28
// bytes — so about 300 KB per NetworkManager together; each Enhanced RPC
// received is one hash lookup, plus an insert for a new identity and, at
// full depth, the eviction of the oldest.

using System.Collections.Generic;

namespace RTMPE.Core.Rpc
{
    /// <summary>
    /// A bounded record of recently delivered Enhanced RPC identities —
    /// <c>(sender session, request id)</c> — answering whether an identity
    /// has been delivered before within the window.
    /// </summary>
    /// <remarks>
    /// Not thread-safe: driven from the main-thread dispatch of inbound RPCs,
    /// like the dispatcher it serves.  A full window forgets its oldest
    /// identity for each new one, so a repeat further apart than the window's
    /// depth is not seen — the depth is chosen to cover a retransmit ladder
    /// at a busy RPC rate, not a whole session.
    /// </remarks>
    internal sealed class RecentRpcIdentityWindow
    {
        /// <summary>
        /// Identities remembered by default: eight seconds of a session sending
        /// five hundred RPCs a second.  A frame's whole ladder — eight
        /// attempts, the later ones at the 2 s cap — climbs for eleven to
        /// sixteen seconds, so the window spans one at up to about two hundred
        /// and fifty RPCs a second and forty seconds' worth at a hundred; above
        /// that a repeat from the ladder's last rungs falls outside it.
        /// </summary>
        public const int DefaultCapacity = 4096;

        private readonly (ulong Sender, uint Request)[] _ring;
        private readonly HashSet<(ulong Sender, uint Request)> _present;
        private int _next;
        private int _count;

        /// <summary>A window remembering <see cref="DefaultCapacity"/> identities.</summary>
        public RecentRpcIdentityWindow() : this(DefaultCapacity)
        {
        }

        /// <summary>A window remembering the last <paramref name="capacity"/> identities.</summary>
        public RecentRpcIdentityWindow(int capacity)
        {
            if (capacity < 1) throw new System.ArgumentOutOfRangeException(nameof(capacity));
            _ring    = new (ulong, uint)[capacity];
            _present = new HashSet<(ulong, uint)>();
        }

        /// <summary>How many identities the window remembers at most.</summary>
        public int Capacity => _ring.Length;

        /// <summary>How many identities it holds now.</summary>
        public int Count => _count;

        /// <summary>
        /// Record a delivery and answer whether the same identity was
        /// delivered before, within the window.  A repeat is not re-recorded:
        /// its place in the window is the first delivery's.
        /// </summary>
        public bool Repeats(ulong senderId, uint requestId)
        {
            var identity = (senderId, requestId);
            if (_present.Contains(identity)) return true;

            if (_count == _ring.Length)
            {
                // Full: the slot about to be reused holds the oldest identity.
                _present.Remove(_ring[_next]);
            }
            else
            {
                _count++;
            }

            _ring[_next] = identity;
            _next = (_next + 1) % _ring.Length;
            _present.Add(identity);
            return false;
        }

        /// <summary>Forget everything — at a session boundary, where the identities restart.</summary>
        public void Clear()
        {
            _present.Clear();
            System.Array.Clear(_ring, 0, _ring.Length);
            _next  = 0;
            _count = 0;
        }
    }
}
