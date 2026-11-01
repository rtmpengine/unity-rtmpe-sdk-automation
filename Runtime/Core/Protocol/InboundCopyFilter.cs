// RTMPE SDK — Runtime/Core/Protocol/InboundCopyFilter.cs
//
// Recognises an encrypted datagram identical to one received just before it.

using System.Threading;

namespace RTMPE.Core.Protocol
{
    /// <summary>
    /// Recognises an encrypted datagram identical, byte for byte, to one of the
    /// last few received.
    /// </summary>
    /// <remarks>
    /// The gateway sends each catch-up spawn more than once, unchanged, so that
    /// one lost datagram does not lose the object.  The replay window refuses
    /// every copy after the first — but only once it is decrypted, on the main
    /// thread, so until then each copy held a place in the dispatcher's queue
    /// and a token of the inbound budget, and a room near its object limit
    /// filled both with copies before its last objects arrived.  Recognised on
    /// the network thread, a copy costs neither, and it is counted where the
    /// main thread's copies are.  Only an encrypted datagram is judged: a
    /// cleartext handshake frame sent again can be one to answer again.  Nor a
    /// SessionAck, which is sealed under the handshake's key rather than the
    /// session's, so no replay window refuses its copy, and the gateway answers a
    /// repeated round two with it again byte for byte.
    /// </remarks>
    internal sealed class InboundCopyFilter
    {
        /// <summary>How many recent datagrams a copy is looked for among.</summary>
        public const int Remembered = 64;

        private readonly object _gate = new object();
        private readonly ulong[] _fingerprints = new ulong[Remembered];
        private readonly int[] _lengths = new int[Remembered];
        private int _next;
        private long _copies;

        /// <summary>How many copies have been recognised.</summary>
        public long CopiesRecognised => Interlocked.Read(ref _copies);

        /// <summary>
        /// Whether the <paramref name="length"/> bytes at
        /// <paramref name="offset"/> are an encrypted datagram identical to one
        /// of the last <see cref="Remembered"/>; one that is not is remembered.
        /// </summary>
        public bool IsCopy(byte[] data, int offset, int length)
        {
            if (data == null || length <= PacketProtocol.OFFSET_FLAGS) return false;
            if ((data[offset + PacketProtocol.OFFSET_FLAGS] & (byte)PacketFlags.Encrypted) == 0) return false;
            if (data[offset + PacketProtocol.OFFSET_TYPE] == (byte)PacketType.SessionAck) return false;

            ulong fingerprint = Fingerprint(data, offset, length);
            // Two receive threads can overlap while a connection is replaced.
            lock (_gate)
            {
                for (int i = 0; i < Remembered; i++)
                {
                    if (_lengths[i] == length && _fingerprints[i] == fingerprint)
                    {
                        Interlocked.Increment(ref _copies);
                        return true;
                    }
                }
                _fingerprints[_next] = fingerprint;
                _lengths[_next] = length;
                _next = (_next + 1) % Remembered;
            }
            return false;
        }

        // FNV-1a, 64 bits: the bytes judged are ciphertext, which nobody but the
        // gateway can choose, so a fingerprint needs no defence against crafted
        // collisions.
        private static ulong Fingerprint(byte[] data, int offset, int length)
        {
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < length; i++)
            {
                hash ^= data[offset + i];
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }
}
