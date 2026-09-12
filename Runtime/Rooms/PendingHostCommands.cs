// RTMPE SDK — Runtime/Rooms/PendingHostCommands.cs
//
// The watch behind `TransferMasterClient` and `KickPlayer`.
//
// Both are host-only, and the Room Service authorises them exactly the way it
// authorises a room-property write: `sqlLockHostPlayerForSession` against the
// caller's seat, `ports.ErrUnauthorized` on a mismatch, and a rejection arm that
// writes to its own stderr and publishes nothing.  A client that is no longer the
// master therefore sends the packet, is refused, and hears the same silence it
// hears while a successful command's broadcast is still on the way.
//
// ⛔ The answer here is a REPORT, not a refusal, and that is the same conclusion
// ROOM-RD-13 reached one file over.  This side holds `RoomInfo.MasterId`, but it
// is derived from `Players[].IsHost` — a local roster copy, edited by whichever
// broadcasts have arrived.  `RehostRoom` returns the room unchanged when the
// promoted master is absent from that copy, a pruned host leaves it empty, and
// the `MasterClientChanged` that would correct either is fanned out as three
// unacknowledged copies with no retransmit ladder.  A stale id would then refuse
// the actual host, permanently, for a command the server would have accepted.
// Depth must not refuse what the authority would admit.
//
// 🔑 Unlike a property write these commands carry no version, so there is no
// gate to compare: the broadcast naming the target IS the answer, and the entry
// is cleared when it arrives.  What that costs is an ambiguity worth stating —
// a `PlayerKicked` for the same target raised by ANOTHER host, or a promotion
// the server made on its own, settles a watch whose own command was refused.
// The outcome the caller asked for did happen; which request produced it is not
// a question this side can answer, and reporting a failure for a command whose
// effect is on the roster would be worse than staying quiet about which of two
// requests caused it.

using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>Which host-only command a pending entry is waiting on.</summary>
    internal enum HostCommandKind
    {
        /// <summary>A <c>MasterClientTransfer</c> (0x2D) awaiting its broadcast.</summary>
        MasterTransfer,

        /// <summary>A <c>KickPlayer</c> (0x2E) awaiting its broadcast.</summary>
        Kick,
    }

    /// <summary>A host-only command sent and not yet answered.</summary>
    internal readonly struct HostCommand
    {
        internal readonly HostCommandKind Kind;
        internal readonly string          TargetPlayerId;

        internal HostCommand(HostCommandKind kind, string targetPlayerId)
        {
            Kind           = kind;
            TargetPlayerId = targetPlayerId;
        }
    }

    /// <summary>
    /// Host-only commands awaiting the broadcast that would prove the server
    /// acted on them.
    /// </summary>
    internal sealed class PendingHostCommands
    {
        /// <summary>
        /// Ceiling on watched commands.
        ///
        /// <para>A command names one player, and a room's capacity is what
        /// bounds the distinct players there are to name — but the target is the
        /// caller's own argument and nothing here refuses an id that holds no
        /// seat, so an application looping over ids it invented would otherwise
        /// grow this without limit. ⛔ Reaching the ceiling costs a WATCH, never
        /// a COMMAND: the packet still goes, it is simply not waited on.</para>
        /// </summary>
        internal const int MaxCommands = 128;

        private readonly PendingAcks<HostCommand> _acks =
            new PendingAcks<HostCommand>(MaxCommands);

        internal int Count => _acks.Count;

        /// <summary>
        /// Record that <paramref name="kind"/> naming
        /// <paramref name="targetPlayerId"/> has been sent, and is owed a
        /// broadcast by <paramref name="deadlineTicks"/>.
        /// </summary>
        /// <returns>
        /// False when the command was not watched — an empty target, or a full
        /// table. The command itself is unaffected either way.
        /// </returns>
        internal bool Arm(HostCommandKind kind, string targetPlayerId, long deadlineTicks)
        {
            if (string.IsNullOrEmpty(targetPlayerId)) return false;
            return _acks.Arm(
                Subject(kind, targetPlayerId),
                new HostCommand(kind, targetPlayerId),
                NoGate,
                deadlineTicks);
        }

        /// <summary>
        /// The broadcast for <paramref name="kind"/> naming
        /// <paramref name="targetPlayerId"/> arrived.
        /// </summary>
        internal void Resolve(HostCommandKind kind, string targetPlayerId)
        {
            if (string.IsNullOrEmpty(targetPlayerId)) return;
            _acks.Resolve(Subject(kind, targetPlayerId), NoGate);
        }

        /// <summary>Forget everything — a room change, or a disconnect.</summary>
        internal void Clear() => _acks.Clear();

        /// <summary>
        /// The commands whose deadline passed with no broadcast, removed from
        /// the table as they are returned.
        /// </summary>
        internal IReadOnlyList<HostCommand> SweepExpired(long nowTicks)
            => _acks.SweepExpired(nowTicks);

        // These commands carry no version, so every arm declares the same gate
        // and every resolution meets it: arrival is the whole answer.
        private const int NoGate = 0;

        // A transfer and a kick naming the same player are two commands with two
        // answers, so the kind is part of the key rather than the target alone.
        private static string Subject(HostCommandKind kind, string targetPlayerId)
            => (kind == HostCommandKind.Kick ? "k:" : "m:") + targetPlayerId;
    }
}
