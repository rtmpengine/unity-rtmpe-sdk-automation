// RTMPE SDK — Runtime/Sync/NetworkVariable.cs
//
// Foundation for synchronising arbitrary typed values over the RTMPE network.
//
// Design decisions:
//  • Two-tier hierarchy:
//      NetworkVariableBase  — non-generic; holds VariableId, IsDirty, Owner.
//                             Enables generic lists/registration without
//                             reflection (List<NetworkVariableBase>).
//      NetworkVariable<T>   — generic; constrained to struct + IEquatable<T>
//                             to guarantee value-equality semantics and inline
//                             storage (no boxing on read).
//  • NetworkVariableString  — extends NetworkVariableBase directly.
//                             Strings are reference types; they cannot satisfy
//                             the struct constraint.  Uses reference equality
//                             (`!=`) plus null normalisation (null treated as "").
//  • IsDirty tracks whether the local value has changed since the last
//    MarkClean() call.  The dirt flag is set by Value setter; cleared by
//    MarkClean().  SetValueWithoutNotify() does NOT set IsDirty, because it
//    is intended for the RECEIVING side (applying an incoming update, not
//    originating a new one).
//  • OnValueChanged(oldValue, newValue) fires AFTER _value is updated so that
//    callbacks can safely read Value without re-entrancy issues.
//  • VariableId (uint) is DERIVED from the declaring type and the member's
//    name, never assigned.  It identifies this variable within its owning
//    OBJECT — every NetworkBehaviour on one GameObject shares the namespace,
//    because the update carries the id and no component discriminator — and
//    the packet serialiser routes incoming updates by it.
//  • Owner (NetworkBehaviour) is stored for the send path — dirty variables
//    are flushed at 30 Hz for owning clients.
//    Not null-checked here — callers must pass a valid instance.
//
// NetworkVariableString.Value setter normalises null to "" on write.
//  A null value assigned via Value = null is stored as "" preventing
//  get→Serialize→Deserialize state divergence.
//
// Security note: no AEAD here.  These objects hold application-layer values;
// the surrounding gateway pipeline handles encryption.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Sync
{
    /// <summary>
    /// Reads a length-prefixed byte block without letting the DECLARED length
    /// decide how much memory is committed.
    /// </summary>
    /// <remarks>
    /// 🔑 <c>BinaryReader.ReadBytes(n)</c> allocates <c>n</c> bytes BEFORE it
    /// reads, and then returns short rather than throwing.  On a receive path
    /// that makes the declared length an allocation primitive for whoever wrote
    /// the packet: an entry whose own <c>value_len</c> is 2 can declare an
    /// inner length of 65535 and cost the receiver 64 KiB.  Measured on the
    /// shipped types before this existed — <b>255 such entries in one 1530-byte
    /// datagram allocated 16,910,200 bytes on the main thread, an amplification
    /// of 11,052×</b>, at whatever rate the sender chooses.
    ///
    /// ⛔ The entry bound (<c>VariableEntryReader</c>) does not close this and
    /// was never able to: it bounds the bytes a Deserialize can READ, and this
    /// is memory committed for bytes that are not there.  Nor does the
    /// per-packet byte ceiling, which counts DECLARED value bytes on the wire —
    /// the batch above is perfectly well-framed and 1530 bytes against a 64 KiB
    /// ceiling.
    ///
    /// 🔑 The SDK already had the right shape in two places and used it in
    /// neither of the two that mattered: <c>RtmpeBinaryReader.ReadBytes</c>
    /// calls <c>Require(len)</c> first, and <c>EncryptedFilePinStore</c> bounds
    /// the declared length against the file before reading.  This is that rule,
    /// written once.
    /// </remarks>
    internal static class WireByteBlock
    {
        // One allocation for the overwhelming majority of legitimate strings,
        // small enough that a hostile declaration costs almost nothing.
        private const int FirstChunkBytes = 512;

        /// <summary>
        /// Read exactly <paramref name="declared"/> bytes, or throw
        /// <see cref="EndOfStreamException"/> having committed memory in
        /// proportion to what was actually available rather than to what was
        /// claimed.
        /// </summary>
        internal static byte[] ReadExactly(BinaryReader reader, int declared, string context)
        {
            if (reader == null)  throw new ArgumentNullException(nameof(reader));
            if (declared < 0)    throw new ArgumentOutOfRangeException(nameof(declared));
            if (declared == 0)   return Array.Empty<byte>();

            // A cheap early refusal when the stream can answer: it costs no
            // allocation at all, and it is where a truncated entry — the common
            // malformed case — is turned away.
            //
            // ⚠️ It is an OPTIMISATION, not the bound. An earlier version read
            // the block with `reader.ReadBytes(declared)` on this path, on the
            // reasoning that `declared <= available` had just been established;
            // that reasoning trusts `Length`. Measured against a seekable stream
            // reporting `long.MaxValue` and delivering nothing, it committed
            // 234 MB for a 100 MB declaration — the very defect this helper
            // exists to remove, reachable because `NetworkVariableString.Deserialize`
            // is `public override` and takes an arbitrary `BinaryReader`. The
            // read below is now the ONLY path, so the guarantee holds whatever
            // a stream claims about itself.
            Stream s = reader.BaseStream;
            if (s != null && s.CanSeek)
            {
                long available = Math.Max(0, s.Length - s.Position);
                if (declared > available)
                    throw new EndOfStreamException(
                        $"{context}: declared {declared} bytes, only {available} available — " +
                        "payload truncated.");
            }

            // Grow with the bytes that actually ARRIVE. ⚠️ Never size the first
            // buffer from `declared` — that is the whole defect. The peak is
            // therefore bounded by the delivered length rather than the declared
            // one: under 2× it plus one chunk, because the buffer doubles.
            byte[] buf = new byte[Math.Min(declared, FirstChunkBytes)];
            int filled = 0;
            while (filled < declared)
            {
                if (filled == buf.Length)
                    Array.Resize(ref buf, (int)Math.Min((long)declared, (long)buf.Length * 2));

                int n = reader.Read(buf, filled, buf.Length - filled);
                if (n <= 0) break;
                filled += n;
            }

            if (filled != declared)
                throw new EndOfStreamException(
                    $"{context}: declared {declared} bytes, only {filled} available — " +
                    "payload truncated.");

            // No trim: the growth above is capped at `declared` and the check
            // above proves `filled == declared`, so the buffer is already
            // exactly the right size. A resize here would be a no-op that reads
            // like insurance.
            return buf;
        }
    }
}

namespace RTMPE.Sync
{
    // ── Base class (non-generic) ───────────────────────────────────────────────

    /// <summary>
    /// The non-generic base class of every network variable, including
    /// <see cref="NetworkVariableList{T}"/>. Use it to hold variables of
    /// different types in one collection.
    /// </summary>
    public abstract class NetworkVariableBase
    {
        // ── Identity ───────────────────────────────────────────────────────────

        /// <summary>
        /// The variable's identity, derived from the owning component's type
        /// name and the member name passed to the constructor. Every client
        /// derives the same identity, so it is never sent or stored.
        /// </summary>
        /// <remarks>
        /// <para>An identity must be unique across the object, not only within
        /// its component. Two components of the same type on one object, or two
        /// constructions of one generic component type, produce the same
        /// identities; the second one's variables are reported as an error and do
        /// not replicate.</para>
        /// <para>Renaming the component type or the member changes the identity,
        /// so every client must be updated together.</para>
        /// </remarks>
        public uint VariableId { get; }

        /// <summary>
        /// The component this variable belongs to.
        /// </summary>
        protected NetworkBehaviour Owner { get; }

        /// <summary>
        /// True when a write made through this variable's public surface would
        /// be dropped by this client's own flush: the object is live on the
        /// network and this client is not its owner.
        /// </summary>
        /// <remarks>
        /// 🔑 The question is <see cref="NetworkBehaviour.IsOwnedByAnotherPlayer"/>
        /// and NOT <c>!IsOwner</c>, which is what
        /// <c>ObjectDispatchOps.FlushAll</c> skips on.  Those two differ on
        /// exactly one state and it is a live one: an object with **no owner at
        /// all** — spawned before this client was told its seat, offline, or
        /// between rooms.  The flush skips it, so a write there is not sent
        /// either; but it is nobody's object rather than somebody else's, which
        /// in practice means purely local, and refusing writes to it takes a
        /// usable local object away from a caller who has one.
        ///
        /// <para>🚨 Mirroring the flush verbatim was the first version of this,
        /// and it was a regression an adversarial pass demonstrated: a spawn
        /// with no seat — which the SDK admits deliberately, and now warns about
        /// — came back with every variable dead, list <c>Count</c> stuck at
        /// zero, and a console line telling the developer they did not own an
        /// object that was theirs.</para>
        ///
        /// <para>⛔ Within the objects this DOES cover, the test is two-sided:
        /// an owner known to be somebody else refuses the write whether or not
        /// this client has been told its own id, because the flush skips that
        /// component just as firmly and the value on the wire is already
        /// somebody else's.</para>
        ///
        /// <para>⛔ That is the opposite of the rule the object-lifecycle
        /// authority states one layer over, and deliberately: there the code
        /// that will refuse the operation is the GATEWAY, which this side can
        /// only predict, so it refuses on positive knowledge alone.  Here the
        /// code that will drop the write is this client's own, so the predicate
        /// is not a prediction and there is nothing to be cautious about.</para>
        ///
        /// <para>An object with no owning behaviour at all, and one that has not
        /// been spawned yet, are both purely local and outside this question —
        /// a list populated from <c>Awake</c> is the case that makes it
        /// load-bearing.</para>
        ///
        /// <para>⛔ <c>Owner.IsSpawned</c> here is redundant against every
        /// caller as they stand today: each one has already answered the
        /// lifecycle question before it asks this one, so removing the clause
        /// changes no observable behaviour and is a mutation this suite cannot
        /// kill. It stays because the predicate is <c>private protected</c> and
        /// has to be true in isolation — a later caller that does not pre-gate
        /// would otherwise inherit an answer about a live object for one that
        /// has never spawned.</para>
        /// </remarks>
        private protected bool WriteWouldNotBeSent =>
            Owner != null && Owner.IsSpawned && Owner.IsOwnedByAnotherPlayer;

        /// <summary>
        /// True when a write through this variable's public surface would not
        /// take effect at all — either because it would not be SENT (the
        /// authority question above) or because the owning behaviour's
        /// lifecycle has closed writes to it.
        /// </summary>
        /// <remarks>
        /// ⚠️ The lifecycle half is the scalars' rule, and a list overrides the
        /// composite because its own is deliberately different:
        /// <c>Owner != null &amp;&amp; !Owner.IsSpawned</c> reads as
        /// <em>after despawn</em> and is equally true
        /// BEFORE the first spawn, which is exactly when a game builds a list by
        /// calling Add.
        ///
        /// <para>🔑 It exists because <c>CanSend</c> has to answer the question
        /// its name asks. Answering only the value half made it claim a write
        /// would land while the setter one screen away refused it on lifecycle —
        /// which is the same silent divergence between a question and its
        /// answer that the whole Can/Try surface was added to end.</para>
        /// </remarks>
        private protected virtual bool WriteWouldNotLand
            => WriteWouldNotBeSent || (Owner != null && !Owner.IsSpawned);

        // One warning per second across the whole variable family.  A replica
        // driving a value from Update() writes at frame rate, and a refused
        // write leaves the stored value untouched, so no equality early-out
        // ever absorbs the repeat.
        private static long _lastUnownedWriteWarnTicks;

        // The flush is per tick, so a Serialize that throws throws thirty times
        // a second for as long as the variable stays dirty — and it does stay
        // dirty, because a failed write is retried. One gate per reason: the
        // fast and growable paths share each of them because they report the
        // same fault, and the buffer an attempt used is not a second one.
        //
        // ⚠️ Per variable, where the inbound gates in this file are static, and
        // the difference is which side chooses the rate. This is the OWNER's
        // flush: what bounds it is how many variables this client owns, not what
        // a sender sends, and the line names the variable whose Serialize is at
        // fault — which is the whole of what an integrator needs from it.
        private long _lastSerializeThrowWarnTicks;
        private long _lastSerializedSizeWarnTicks;

        /// <summary>
        /// Refuse a write this client is not entitled to make, and say so once
        /// per second.  False — and silent — for every other state.
        /// </summary>
        private protected bool RefuseUnownedWrite(string site)
        {
            if (!WriteWouldNotBeSent) return false;

            if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastUnownedWriteWarnTicks))
                UnityEngine.Debug.LogWarning(
                    $"[RTMPE] {GetType().Name} (id {VariableId} on {OwnerLabel}) refused a " +
                    $"write at {site}: this client does not own the object. The variable " +
                    "flush skips every component the local player does not own, so the " +
                    "value would have been stored here, announced to local subscribers, " +
                    "and sent to nobody — diverging permanently from every other client. " +
                    "Only the owner writes; ask for the object with " +
                    "OwnershipManager.RequestOwnershipTransfer, or send the owner an RPC.");
            return true;
        }

        /// <summary>Test seam: reopen the shared unowned-write warning gate.</summary>
        internal static void ResetUnownedWriteWarnGateForTest()
            => _lastUnownedWriteWarnTicks = 0;

        // One second across every variable type, and never the gate the
        // unowned refusal spends: the two report different faults, and a
        // flood of one must not decide whether the other is ever printed.
        private static long _lastLifecycleWriteWarnTicks;

        /// <summary>
        /// Refuse a write made outside the owner's spawned life, and say so
        /// once per second.  True — and reported — while <see cref="Owner"/>
        /// is known and not spawned; false, and silent, for every other state,
        /// including a variable with no owner at all.
        /// </summary>
        /// <remarks>
        /// <para>Before the first <c>OnNetworkSpawn</c> the value belongs in the
        /// constructor or in <c>OnNetworkSpawn</c> itself, because a write here
        /// is not queued; after
        /// <c>OnNetworkDespawn</c> the object's life has ended and the value
        /// would never be published — and a dead variable marked dirty would
        /// re-publish post-despawn state on the next flush and raise
        /// <c>OnValueChanged</c> at subscribers the teardown has cleared.</para>
        ///
        /// <para>⛔ The message does not claim WHICH side of the life the caller
        /// is on.  <c>IsSpawned</c> is false before the first spawn and after
        /// the last despawn alike, and a pooled instance reaches the second and
        /// then the first again — so the developer is told what each case
        /// needs rather than being guessed at.</para>
        ///
        /// <para>⚠️ It names no object id: <c>NetworkObjectId</c> exists on the
        /// real NetworkBehaviour and not on every stand-in the test shards
        /// compile this file against.  A message is not worth a member every
        /// stub must grow.</para>
        /// </remarks>
        private protected bool RefuseLifecycleWrite()
        {
            if (Owner == null || Owner.IsSpawned) return false;

            if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastLifecycleWriteWarnTicks))
                UnityEngine.Debug.LogWarning(
                    $"[RTMPE] {GetType().Name} (id {VariableId} on {OwnerLabel}) dropped " +
                    "an assignment: the object is not spawned. A value the object " +
                    "starts its life with belongs in this variable's constructor, or " +
                    "in OnNetworkSpawn — a write before it is not queued. After " +
                    "OnNetworkDespawn the object's life has ended and the value " +
                    "would never be published.");
            return true;
        }

        /// <summary>Test seam: reopen the shared lifecycle-write warning gate.</summary>
        internal static void ResetLifecycleWriteWarnGateForTest()
            => _lastLifecycleWriteWarnTicks = 0;

        // ── Dirty tracking ─────────────────────────────────────────────────────

        /// <summary>
        /// Whether a change is waiting to be sent. Set when the owner writes a
        /// new value, and cleared by <see cref="MarkClean"/> once the change has
        /// been sent.
        /// </summary>
        public bool IsDirty { get; protected set; }

        // ── Per-variable throttling (Feature: NetworkVariableAttribute) ────────

        /// <summary>
        /// The most updates per second this variable sends. <c>0</c> (the
        /// default) sends a change at every replication tick
        /// (<c>NetworkSettings.tickRate</c>, 30 per second by default). A
        /// negative value is stored as <c>0</c>.
        /// </summary>
        /// <remarks>
        /// <para>Set it after constructing the variable, for example in
        /// <c>OnNetworkSpawn</c>. It can be changed at any time, for example to
        /// send more often during intense play.</para>
        /// <para>Intermediate values are not sent, but the latest value always
        /// is. A rate above the tick rate has no further effect.</para>
        /// </remarks>
        /// <example>
        /// <code>
        /// _health = new NetworkVariableInt(this, nameof(_health), 100);
        /// _health.SendRateHz = 10f;   // at most 10 updates per second
        /// </code>
        /// </example>
        public float SendRateHz
        {
            get => _sendRateHz;
            set => _sendRateHz = value < 0f ? 0f : value;
        }
        private float _sendRateHz;

        /// <summary>
        /// Wall-clock timestamp of the last successful flush, sampled from
        /// <c>Time.unscaledTime</c>.  Updated by the flush loop after the
        /// variable's bytes have been appended to the outbound packet.
        ///
       /// <para>Reset to <c>0f</c> on ownership change and on disconnect so a
        /// freshly owning client can flush its first value immediately rather
        /// than waiting out a stale throttle window inherited from the
        /// previous owner.</para>
        ///
       /// <para>Internal — only the flush path and the
        /// ownership-reset hook should mutate this field.</para>
        /// </summary>
        internal float LastFlushTimeUnscaled { get; set; }

        /// <summary>
        /// A human-readable name for the behaviour that owns this variable, for
        /// diagnostics.
        /// </summary>
        /// <remarks>
        /// ⚠️ A variable id is unique per COMPONENT, so "id 3" names a different
        /// variable on every NetworkBehaviour in the scene and tells a developer
        /// almost nothing. The sibling warning in
        /// <c>NetworkBehaviour.FlushDirtyVariables</c> already prints the owning
        /// type for exactly this reason.
        /// </remarks>
        private protected string OwnerLabel =>
            Owner == null ? "<no owner>" : Owner.GetType().Name;

        /// <summary>
        /// Reset the per-variable throttle book-keeping, so that
        /// <see cref="LastFlushTimeUnscaled"/> does not gate the first flush on
        /// a new owner or a new life with a phantom send-time inherited from
        /// the old one.  Called on an ownership handover, on the spawn cycle
        /// through <see cref="OnOwnerSpawned"/>, and on the pool hand-back
        /// through <see cref="OnOwnerRecycled"/>.
        /// </summary>
        internal void ResetThrottleState()
        {
            LastFlushTimeUnscaled = 0f;
        }

        /// <summary>
        /// The owning behaviour's owner changed under this variable — the
        /// handover a host migration or an ownership transfer performs, with
        /// the new owner already recorded.  The base keeps nothing that is the
        /// sender's: a scalar's value is the state itself and belongs to the
        /// object.  A subclass that holds outbound state — the list's log of
        /// edits it has yet to send — sheds it when this client is no longer
        /// the one sending.
        /// </summary>
        internal virtual void OnOwnerChanged() { }

        // ── What the wire has delivered ────────────────────────────────────────

        /// <summary>
        /// Whether the wire has delivered this variable a whole value during
        /// the owning behaviour's current life — its owner's value, where the
        /// variable would otherwise hold the seed the prefab declares.  Every
        /// value a scalar receives is whole; a list's is whole from the first
        /// payload that states the whole list, because a delta applied to a
        /// list this client never held the rest of describes nothing.
        /// </summary>
        /// <remarks>
        /// Read by the world object about a world handed to this client before
        /// it held it: whether the frames held for it carried the room's value
        /// of every variable, or of some.  Cleared with the life
        /// (<see cref="OnOwnerSpawned"/>, <see cref="OnOwnerRecycled"/>) and
        /// never by a handover, which is when it is read.
        /// </remarks>
        internal bool HoldsDeliveredValue { get; private protected set; }

        // Whether the value the entry being applied carried was refused rather
        // than applied: presumed on the way in (ApplyInbound, for a type that
        // reports its refusals; ApplyFromWire, on entry), lifted only past the
        // variable's own checks where a value is applied (ApplyFromWire,
        // SetValueWithoutNotify), and spent by NoteInboundDelivery, which then
        // records nothing — a value that stands at the seed was not delivered,
        // whatever arrived.
        private protected bool LastInboundRefused;

        /// <summary>
        /// The inbound path has applied a value the wire delivered to this
        /// variable (<c>NetworkBehaviour.TryApplyVariableUpdate</c>, after
        /// <see cref="Deserialize"/> returned).  A scalar's value is whole, so
        /// the base records it — unless the variable refused it; a list records
        /// only a payload that stated the whole list.
        /// </summary>
        /// <remarks>
        /// A variable type of the game's own that derives from this class
        /// directly, rather than from <see cref="NetworkVariable{T}"/>, cannot
        /// say it refused a value: what arrives for it is recorded
        /// (<see cref="ReportsRefusals"/>).
        /// </remarks>
        internal virtual void NoteInboundDelivery()
        {
            if (LastInboundRefused)
            {
                LastInboundRefused = false;
                return;
            }
            HoldsDeliveredValue = true;
        }

        /// <summary>
        /// Whether this type says when it refuses an inbound value — lifting
        /// the refusal <see cref="ApplyInbound"/> presumes only where a value is
        /// applied (<c>ApplyFromWire</c>, <c>SetValueWithoutNotify</c>).  The
        /// SDK's scalars and strings do, and so does a game's type derived from
        /// <see cref="NetworkVariable{T}"/>; one derived from this class
        /// directly has no such path, and what arrives for it is taken as
        /// applied.  Sealed on <see cref="NetworkVariable{T}"/>: the SDK's
        /// scalars refuse a value in their own <see cref="Deserialize"/>, before
        /// it reaches <c>ApplyFromWire</c>, and one that said otherwise would
        /// record that refusal as a delivery.
        /// </summary>
        internal virtual bool ReportsRefusals => false;

        /// <summary>
        /// Apply one inbound entry to this variable — <see cref="Deserialize"/>
        /// it and note the delivery — the one inbound sequence, run by
        /// <c>NetworkBehaviour.TryApplyVariableUpdate</c> once the tick gate
        /// has admitted the entry.
        /// </summary>
        /// <remarks>
        /// The refusal is presumed on the way in, for a type that reports its
        /// refusals, and lifted where a value is applied: a type that refuses a
        /// value inside its own <see cref="Deserialize"/> — a non-finite
        /// coordinate, a rotation that is not one — before it reaches
        /// <c>ApplyFromWire</c> has delivered nothing, and is not recorded as
        /// having done so.
        /// </remarks>
        internal void ApplyInbound(BinaryReader reader)
        {
            LastInboundRefused = ReportsRefusals;
            Deserialize(reader);
            NoteInboundDelivery();
        }

        /// <summary>
        /// Whether every variable in <paramref name="variables"/> holds a whole
        /// value the wire delivered this life — the reading a component gives
        /// <c>NetworkBehaviour.EveryVariableHoldsADeliveredValue</c> over its
        /// tracked variables.  True for none.
        /// </summary>
        internal static bool EveryHoldsADeliveredValue(List<NetworkVariableBase> variables)
        {
            for (int i = 0; i < variables.Count; i++)
            {
                if (!variables[i].HoldsDeliveredValue) return false;
            }
            return true;
        }

        /// <summary>
        /// The owning behaviour has entered a new spawn.  One named event, so a
        /// caller walking the tracked list does not have to know which pieces of
        /// per-life state each subclass keeps.
        /// </summary>
        /// <remarks>
        /// Both resets belong to the same event and were previously applied
        /// together only on handover.  A pooled instance reaches its next life
        /// through the spawn cycle instead, carrying the previous occupant's
        /// tick watermark and a <c>LastFlushTimeUnscaled</c> sample from a
        /// clock reading that no longer means anything.
        /// </remarks>
        internal virtual void OnOwnerSpawned()
        {
            ResetInboundTickGate();
            ResetThrottleState();
            HoldsDeliveredValue = false;
        }

        /// <summary>
        /// The owning behaviour's life has ended and the instance is going back
        /// to an object pool, to be handed out again as a different networked
        /// object without ever being reconstructed.  A variable registered
        /// outside <c>OnNetworkSpawn</c> — a field initialiser, <c>Awake</c>,
        /// <c>OnEnable</c> — survives that hand-back, and what it holds is the
        /// life that just ended: the value the owner last wrote, or the value
        /// the wire last delivered to a replica.  Neither describes the object
        /// the instance will be next, and nothing on the next spawn re-sends
        /// it, so every peer that reuses an instance would start the new life
        /// from a different old one.  Each subclass sheds its life's state here
        /// and returns to what its constructor established, announcing the
        /// change to its subscribers as it would any other — a subscriber that
        /// outlives the life (a HUD bound in <c>Awake</c> on the pooled prefab)
        /// is otherwise left showing the ended life's value, because the next
        /// life's first write, when it equals the seed, changes nothing and
        /// says nothing; the base sheds the send-side bookkeeping and the
        /// inbound tick gate.  The announcement fires with the owner despawned
        /// (<c>IsSpawned</c> false) and the ended life's identity still set.
        /// </summary>
        /// <remarks>
        /// Reached from the pool hand-back only, never from a despawn that ends
        /// in <c>Destroy</c>: a destroyed instance is not written again, and
        /// the post-despawn refusals stay in force on it.  A subclass outside
        /// this assembly cannot override this (it is internal), so a
        /// user-authored variable type gets the base's reset alone and keeps
        /// its own value across lives.  On a pooled
        /// instance the next life's pre-spawn writes follow this call — a list
        /// populated in <c>OnEnable</c> is the pattern that admits them — so a
        /// subclass that closes its writes on having been spawned reopens them
        /// here.
        /// </remarks>
        internal virtual void OnOwnerRecycled()
        {
            IsDirty = false;
            ResetInboundTickGate();
            ResetThrottleState();
            HoldsDeliveredValue = false;
        }

        // ── Constructor ────────────────────────────────────────────────────────

        /// <summary>
        /// Registers the variable with <paramref name="owner"/> and derives its
        /// <see cref="VariableId"/>. Construct variables in
        /// <c>OnNetworkSpawn</c>, on every client.
        /// </summary>
        /// <param name="owner">The component the variable belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the variable is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        /// <exception cref="ArgumentException">
        /// <paramref name="memberName"/> is <see langword="null"/>, empty or
        /// whitespace.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Another variable on the same component already has this identity, for
        /// example because two variables were constructed with the same name.
        /// </exception>
        protected NetworkVariableBase(NetworkBehaviour owner, string memberName)
        {
            if (string.IsNullOrWhiteSpace(memberName))
                throw new ArgumentException(
                    "[RTMPE] a NetworkVariable needs the name of the member it is " +
                    "assigned to — pass nameof(<field>). The wire identity is derived " +
                    "from it, so an unnamed variable has no identity of its own.",
                    nameof(memberName));

            Owner      = owner;
            // The CONCRETE type, not the declaring one. Two components of one
            // object always differ by concrete type, so folding it in is what
            // makes a cross-component collision unreachable; folding the
            // declaring type instead would give two siblings that derive from
            // one base the same identity for the base's own members.
            VariableId = WireIdHash.Of(WireIdHash.ScopeOf(owner?.GetType()), memberName);
            // Register with the owning NetworkBehaviour so the 30 Hz flush loop
            // can discover and serialize dirty values. Create variables inside
            // OnNetworkSpawn() where the object is guaranteed to be fully initialized.
            owner?.TrackVariable(this);
        }

        // ── API ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Clears <see cref="IsDirty"/>. The SDK calls it after the variable's
        /// change has been sent.
        /// </summary>
        public virtual void MarkClean() => IsDirty = false;

        /// <summary>
        /// Force the dirty flag to <see langword="true"/> without changing the
        /// stored value.  Used by the SDK when a late joiner enters the room
        /// and needs a full state snapshot — every variable on every owned
        /// object is re-flagged so the next 30 Hz flush retransmits its
        /// current value.
        /// <para>
        /// Does NOT fire <c>OnValueChanged</c> — the value is unchanged; only
        /// the send-queue state is reset.  Safe to call multiple times.
        /// </para>
        /// </summary>
        internal virtual void MarkDirtyForResync() => IsDirty = true;

        /// <summary>
        /// Whether enough time has passed that this variable's replicas should
        /// be refreshed from scratch.  Asks nothing about room and changes
        /// nothing — the flush's fast path uses it to decide whether the main
        /// loop is worth running at all.
        /// </summary>
        /// <remarks>
        /// A scalar carries its whole value in every update, so a lost one is
        /// repaired by the next write and there is nothing to refresh: the base
        /// answers <see langword="false"/> and the flush skips it exactly as
        /// before.  <see cref="NetworkVariableList{T}"/> is the type that needs
        /// it — its steady-state payload is a delta against a state the receiver
        /// is assumed to hold, so a single lost update leaves owner and replica
        /// permanently apart with nothing that would ever notice.
        /// </remarks>
        internal virtual bool IsDueForPeriodicResync(float nowUnscaled) => false;

        /// <summary>
        /// Re-flag a variable that is <em>clean</em> for a periodic refresh,
        /// when one is due <em>and</em> its bytes fit the room left in this
        /// tick's payload, and report whether that happened.
        /// </summary>
        /// <param name="nowUnscaled">Unscaled time, as the flush loop measures it.</param>
        /// <param name="roomBytes">
        /// What is left of the datagram after everything already written to this
        /// tick's payload.
        /// </param>
        /// <param name="payloadIsEmpty">
        /// True when nothing has been written to this tick's payload yet, which
        /// is what separates <em>this list cannot be snapshotted</em> from
        /// <em>this list came second</em>.  The same distinction
        /// <c>VariableFlushBudget</c> draws between <c>TooLargeAlone</c> and
        /// <c>Deferred</c>, and for the same reason.
        /// </param>
        /// <returns>
        /// <see langword="true"/> only when this call left the variable dirty,
        /// so a caller may treat it as the whole of the decision.
        /// </returns>
        /// <remarks>
        /// ⛔ The room is a parameter and not an afterthought.  A refresh is a
        /// re-send of state the replica is assumed already to hold, so an
        /// unsendable one is worth nothing — and worse than nothing for a
        /// <see cref="NetworkVariableList{T}"/>, whose queued snapshot displaces
        /// the delta log until a flush succeeds.  A list large enough that its
        /// snapshot does not fit a datagram is replicating perfectly well by
        /// deltas; arming one there would end that, permanently, on a timer.
        ///
        /// <para>The answer is produced here rather than at the call site
        /// because the flush loop lives in a <c>MonoBehaviour</c> partial that no
        /// test project compiles.  Returning the postcondition — rather than
        /// leaving the caller to ask twice — is what keeps the guarantee inside
        /// a compiled method.</para>
        /// </remarks>
        internal virtual bool TryBeginPeriodicResync(
            float nowUnscaled, int roomBytes, bool payloadIsEmpty) => false;

        /// <summary>
        /// Writes the current value to <paramref name="writer"/>, in the format
        /// <see cref="Deserialize"/> reads.
        /// </summary>
        /// <param name="writer">The writer to write the value to.</param>
        /// <remarks>
        /// Write the value only: the SDK adds the variable's identity and the
        /// value's length.
        /// </remarks>
        public abstract void Serialize(BinaryWriter writer);

        /// <summary>
        /// Write the variable's whole current state, in the format
        /// <see cref="Deserialize"/> recovers, without consulting or touching
        /// what is pending on the wire.  For a scalar that is
        /// <see cref="Serialize"/>; a list overrides it to write a full sync,
        /// because its <c>Serialize</c> writes the op log — and an empty op
        /// log says nothing about the elements it holds.
        /// </summary>
        /// <remarks>
        /// The read half of a state transfer between two objects on one
        /// client — the world-authority migration copies the previous host's
        /// instance into the one it spawns — so it has no side effect on the
        /// source: no op is queued, no dirty flag moves.
        /// </remarks>
        internal virtual void SerializeSnapshot(BinaryWriter writer) => Serialize(writer);

        // GC Round 2 (2026-05-02) — cached fast-path serializer state.
        //
        // The fast path needs a non-growable MemoryStream + BinaryWriter to
        // detect "value too big for the pool buffer" via NotSupportedException
        // and fall back to the growable slow path.  Pre-Round-2, both objects
        // were `using var` locals — one MemoryStream + one BinaryWriter
        // allocated per SerializeWithId call (≈ N variables × M objects ×
        // 30 Hz = several hundred allocs/sec on a busy game).
        //
        // Caching strategy:
        //   • The rented byte[] still comes from ArrayPool<byte>.Shared per
        //     call.  This is required because the buffer must be *cleared*
        //     before return to avoid leaking the prior tick's variable
        //     payload to the next renter; a per-instance buffer would leak
        //     the same payload across ticks of the SAME variable.
        //   • The MemoryStream + BinaryWriter wrappers are allocated once
        //     per NetworkVariable instance (stored in _fastMs / _fastBw)
        //     and re-targeted onto each new rented buffer via reflection-
        //     free APIs: SetLength(0)+TrySetBuffer.  .NET Standard 2.1 does
        //     not expose SetBuffer publicly, so we wrap a fresh
        //     MemoryStream around the rented buffer the first time and
        //     leave the wrapper objects alive afterwards.
        //   • Threading: NetworkVariableBase is touched only from the Unity
        //     main thread (FlushDirtyVariables runs on Update); the cached
        //     fields are NOT thread-safe.  See NetworkVariable threading
        //     notes at the file header.
        private MemoryStream _fastMs;
        private BinaryWriter _fastBw;
        private byte[]       _fastMsBuffer;  // Cached reference for fast identity check (avoids GetBuffer's UnauthorizedAccessException risk path)
        private MemoryStream _slowMs;
        private BinaryWriter _slowBw;

        /// <summary>
        /// Writes the variable's identity and the length of its value, followed
        /// by the value <see cref="Serialize"/> writes. Used by the SDK when it
        /// sends the variable; not intended to be called from game code.
        /// </summary>
        /// <param name="writer">The writer to write the entry to.</param>
        /// <returns>
        /// <see langword="true"/> when the entry was written;
        /// <see langword="false"/> when <see cref="Serialize"/> threw or wrote
        /// more than 65535 bytes. An error is then logged, at most once a
        /// second, and the bytes already written must be discarded.
        /// </returns>
        public bool SerializeWithId(BinaryWriter writer)
        {
            // Write var_id first.
            writer.Write(VariableId);

            // Write a 2-byte LE length prefix for the value payload.  The
            // prefix lets the receiver skip entries with unknown variable
            // IDs instead of stopping mid-packet and losing all subsequent
            // variables.
            //
           // Fast path: rent a pool-backed byte[] large enough for every
            // struct value (Quaternion = 16 B) and most strings.  This is
            // zero-heap for the common case (30 Hz × N objects × M vars).
            //
           // Slow path: NetworkVariableString may emit up to 65,537 bytes.
            // Writing past the rented buffer throws NotSupportedException on
            // a non-growable MemoryStream; we detect this and fall through
            // to a growable stream.  The slow path executes only for the
            // long-string edge case — ≈ 0 % of gameplay traffic.
            //
           // Hard cap: the wire format encodes the per-value length as a
            // ushort, so any value longer than ushort.MaxValue would silently
            // truncate and desync receiver state.  Detect and skip such
            // values before writing anything to the outer writer.
            const int PoolBufferSize = 1024;
            const int MaxValueLen = ushort.MaxValue;
            var pool = System.Buffers.ArrayPool<byte>.Shared;
            byte[] rented = pool.Rent(PoolBufferSize);
            bool overflowed = false;
            try
            {
                // Cached MemoryStream + BinaryWriter (lazy-init).  The
                // MemoryStream is bound to the rented buffer on construction;
                // since rented buffers vary in length call-to-call (ArrayPool
                // returns the same bucket size for a given Rent request, but
                // .Length may exceed the requested size), we always
                // construct a fresh MemoryStream around the new rented
                // buffer — but we cache the BinaryWriter against the cached
                // stream.  In practice ArrayPool's bucket logic means the
                // rented array reference is usually identical across calls
                // on a given instance, so the MemoryStream allocation is
                // O(1) but its internal buffer reference does not change.
                if (_fastMs == null || !ReferenceEquals(_fastMsBuffer, rented))
                {
                    // (Re)bind: the rented buffer reference changed since
                    // last call (or first call).  Allocate a new
                    // MemoryStream over the new buffer.  We use the 5-arg
                    // constructor with publiclyVisible: true so the cached
                    // stream's GetBuffer() call (used elsewhere) does not
                    // throw UnauthorizedAccessException — the buffer is
                    // ours to expose since we rented it locally.  We also
                    // store the buffer reference in _fastMsBuffer for the
                    // ReferenceEquals check above; calling GetBuffer() on
                    // a non-rebound stream is allowed but is only invoked
                    // for the buffer-identity check, never for slicing.
                    // Disposing the BinaryWriter would close the
                    // underlying stream, so we deliberately do NOT dispose
                    // either object — they are root-rooted by _fastMs /
                    // _fastBw and reclaimed when this NetworkVariable is
                    // finalized.
                    _fastMs = new MemoryStream(rented, 0, rented.Length,
                                               writable: true, publiclyVisible: true);
                    _fastBw = new BinaryWriter(_fastMs, Encoding.UTF8, leaveOpen: true);
                    _fastMsBuffer = rented;
                }
                else
                {
                    // Same buffer reference — just reset the position.
                    _fastMs.SetLength(0);
                    _fastMs.Position = 0;
                }
                var fast = _fastMs;
                var bw   = _fastBw;
                try
                {
                    Serialize(bw);
                    bw.Flush();
                }
                catch (NotSupportedException)
                {
                    // Rented buffer is too small — the growable fallback
                    // below will be used instead.
                    overflowed = true;
                }
                catch (Exception ex)
                {
                    // A buggy custom Serialize() must not abort the entire
                    // flush cycle for sibling NetworkVariables: this variable is
                    // withheld and the rest of the payload is unaffected.
                    if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastSerializeThrowWarnTicks))
                        Debug.LogError(
                            $"[RTMPE] NetworkVariable '{GetType().Name}' (id {VariableId}): Serialize() threw " +
                            $"{ex.GetType().Name}: {ex.Message}.  The variable is NOT being sent and stays " +
                            "dirty — it is offered again on the next tick, and this line repeats at most " +
                            "once a second until the serializer stops throwing.");
                    return false;
                }

                if (!overflowed)
                {
                    int valueLen = (int)fast.Position;
                    if (valueLen > MaxValueLen)
                    {
                        if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastSerializedSizeWarnTicks))
                            Debug.LogError(
                                $"[RTMPE] NetworkVariable '{GetType().Name}' (id {VariableId}): serialized " +
                                $"size {valueLen} exceeds the {MaxValueLen}-byte value_len field.  The " +
                                "variable is NOT being sent and stays dirty; no later tick makes it fit, " +
                                "so reduce what it serialises or split it across several variables.");
                        return false;
                    }
                    writer.Write((ushort)valueLen);
                    writer.Write(rented, 0, valueLen);
                    return true;
                }
            }
            finally
            {
                // Clear before return so subsequent renters cannot read
                // residual variable payloads from the shared pool — a peer
                // app component that rents the same buffer next would
                // otherwise observe the previous owner's serialized state.
                pool.Return(rented, clearArray: true);
            }

            // Slow path: cache a growable stream + writer the first time
            // it's needed.  The slow path is rare (long-string variables
            // only) so the cache pays for itself slowly, but it costs
            // nothing on instances that never hit it.  We must reset
            // length/position on each entry because the stream is reused.
            if (_slowMs == null)
            {
                _slowMs = new MemoryStream(PoolBufferSize);
                _slowBw = new BinaryWriter(_slowMs, Encoding.UTF8, leaveOpen: true);
            }
            else
            {
                _slowMs.SetLength(0);
                _slowMs.Position = 0;
            }
            {
                var growable = _slowMs;
                var bw       = _slowBw;
                try
                {
                    Serialize(bw);
                    bw.Flush();
                }
                catch (Exception ex)
                {
                    if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastSerializeThrowWarnTicks))
                        Debug.LogError(
                            $"[RTMPE] NetworkVariable '{GetType().Name}' (id {VariableId}): Serialize() threw " +
                            $"{ex.GetType().Name} on growable path: {ex.Message}.  The variable is NOT being " +
                            "sent and stays dirty — it is offered again on the next tick, and this line " +
                            "repeats at most once a second until the serializer stops throwing.");
                    return false;
                }
                int valueLen = (int)growable.Length;
                if (valueLen > MaxValueLen)
                {
                    if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastSerializedSizeWarnTicks))
                        Debug.LogError(
                            $"[RTMPE] NetworkVariable '{GetType().Name}' (id {VariableId}): serialized " +
                            $"size {valueLen} exceeds the {MaxValueLen}-byte value_len field.  The " +
                            "variable is NOT being sent and stays dirty; no later tick makes it fit, " +
                            "so reduce what it serialises or split it across several variables.");
                    return false;
                }
                writer.Write((ushort)valueLen);
                writer.Write(growable.GetBuffer(), 0, valueLen);
                return true;
            }
        }


        /// <summary>
        /// Reads a value sent by the owner from <paramref name="reader"/> and
        /// applies it.
        /// </summary>
        /// <param name="reader">The reader positioned at the value.</param>
        /// <remarks>
        /// <para>
        /// In a custom type derived from <see cref="NetworkVariable{T}"/>, store
        /// the value with <see cref="NetworkVariable{T}.ApplyFromWire"/>, which
        /// stores it and raises <see cref="NetworkVariable{T}.OnValueChanged"/>
        /// on the receiving client. Do not use
        /// <see cref="NetworkVariable{T}.SetValueWithoutNotify"/> here: it raises
        /// no event, so subscribers on the other clients never hear of the
        /// change.
        /// </para>
        /// <para>
        /// If the data does not form a usable value, discard it instead of
        /// applying it, and override
        /// <see cref="NetworkVariable{T}.IsSendableValue"/> so the owner refuses
        /// the same values. <see cref="NetworkVariableQuaternion"/>, for example,
        /// refuses a rotation with a NaN or infinite component and keeps its
        /// previous value.
        /// </para>
        /// </remarks>
        public abstract void Deserialize(BinaryReader reader);

        /// <summary>
        /// Reads a variable identity written by <see cref="SerializeWithId"/>.
        /// Used by the SDK when it receives variable updates; not intended to be
        /// called from game code.
        /// </summary>
        /// <param name="reader">The reader positioned at the identity.</param>
        /// <returns>The identity read.</returns>
        public static uint ReadVariableId(BinaryReader reader)
        {
            return reader.ReadUInt32();
        }

        /// <summary>
        /// The variable in <paramref name="tracked"/> already holding
        /// <paramref name="variableId"/>, or <see langword="null"/> when the id
        /// is free on this behaviour.
        /// </summary>
        /// <remarks>
        /// 🔑 Extracted so the refusal is a decision something can DRIVE.
        /// <c>NetworkBehaviour</c> is a MonoBehaviour partial that no test
        /// project compiles, so while this scan lived inside
        /// <c>TrackVariable</c> the only thing holding it was a text rule over
        /// that method's body — and a text rule can say a condition is present,
        /// never that it is right. A decoy conjunct in front of the comparison
        /// (<c>i &lt; 0 &amp;&amp;</c>) turns the refusal off with every assertion still
        /// matching, which is exactly what an audit demonstrated.
        /// <para>The sibling across two components of one object is
        /// <c>ObjectDispatchOps.FindVariableIdClaimant</c>, extracted for the
        /// same reason and driven the same way.</para>
        /// </remarks>
        internal static NetworkVariableBase FindIdHolder(
            IReadOnlyList<NetworkVariableBase> tracked, uint variableId)
        {
            if (tracked == null) return null;

            for (int i = 0; i < tracked.Count; i++)
            {
                var candidate = tracked[i];
                if (candidate != null && candidate.VariableId == variableId)
                {
                    return candidate;
                }
            }

            return null;
        }

        // ── Per-variable inbound tick gate ─────────────────────────────────────
        //
        // Tracks the highest tick this variable has applied from a server
        // VariableUpdate so a re-ordered datagram cannot silently roll the
        // value back.  Comparison is RFC 1982 modular so a uint32 wrap during
        // a long-running session does not wedge the gate.

        private uint _lastAppliedTick;
        private bool _hasLastAppliedTick;

        // The bounded re-arm below, and the three conditions that bound it.
        //
        // A watermark drawn from the wrong clock is created by a reset spent by
        // a datagram the PREVIOUS sender had already put on the wire: the reset
        // an ownership change performs, and the one a departure books for the
        // objects that outlived their sender (SpawnManager, a moment after the
        // departure, so its frames in flight have landed first).  Everything
        // here follows from that.
        //
        // 🔑 It is offered only for a short period after the gate is anchored,
        // and at no other time.  That is what keeps an ordinary retransmit
        // ladder — which runs against a watermark many updates old — out of
        // reach of it STRUCTURALLY rather than by a test on the ladder's shape.
        // Two earlier versions tried to characterise the ladder instead, and
        // both were defeated by the traffic a real handover delivers: the
        // previous owner has as many datagrams in flight as the reordering
        // window holds, not one, and they arrive in whatever order the network
        // chose.
        private const double AdoptionArmedForSeconds = 5.0;

        // 🔑 And only across a gap no ladder could have opened inside that
        // window.  A replayed frame sits a few frames below the watermark; a
        // different client's counter sits wherever its own session put it.  The
        // margin only has to exceed what a sender can emit during the arming
        // window, which is bounded — 5 s at the 128 Hz ceiling NetworkSettings
        // permits is 640 ticks — and NOT by the retransmit ladder's own
        // configurable depth, which has no useful bound at all
        // (ReliableChannel permits 64 attempts at a 60 s ceiling).
        private const uint MinDifferentClockGapTicks = 1024;

        // 🔑 And only after the candidate has persisted.  A run is measured in
        // TIME rather than in updates, because a variable is offered only when
        // it changes: the same number of rejections is a quarter of a second on
        // a variable that moves every tick and several minutes on one that moves
        // three times a minute — and the slow variable is the class where a
        // stale value is most visible.
        private const double ReArmAfterRejectingForSeconds = 1.0;

        private double _adoptionArmedUntil;
        private bool   _hasRejectRun;
        private double _rejectRunStartedUnscaled;
        private uint   _rejectRunLowTick;
        private bool   _rejectRunSawASecondTick;

        /// <summary>
        /// Returns <see langword="true"/> when an inbound update stamped with
        /// <paramref name="incomingTick"/> should be applied — i.e. when the
        /// tick is strictly greater than the highest tick already applied to
        /// this variable.  The first call (no prior tick) always accepts.
        ///
        /// ⚠️ With one bounded exception, described on the constants above: for
        /// a short period after the gate is anchored, a run of refusals far
        /// below the watermark and lasting longer than the re-arm window
        /// re-bases the watermark onto the lowest tick that run saw.  Outside
        /// that period, and across any smaller gap, the comparison is strict.
        /// </summary>
        internal bool TryAcceptInboundTick(uint incomingTick, double nowUnscaled)
        {
            if (!_hasLastAppliedTick)
            {
                _hasLastAppliedTick = true;
                _lastAppliedTick    = incomingTick;
                _hasRejectRun       = false;
                _adoptionArmedUntil = nowUnscaled + AdoptionArmedForSeconds;
                return true;
            }

            // (int)(a - b) > 0 iff a is strictly greater than b on the
            // 32-bit ring; matches InputBuffer.SeqGreater so the whole SDK
            // observes the same wrap semantics.
            int delta = (int)(incomingTick - _lastAppliedTick);

            // The same tick again is a retransmit of the frame the watermark was
            // drawn from, and says nothing about who sent it.
            if (delta == 0) return false;

            if (delta > 0)
            {
                _hasRejectRun    = false;
                _lastAppliedTick = incomingTick;
                return true;
            }

            // Below the watermark.  Outside the arming window there is no
            // question to answer: the watermark has survived long enough to be
            // the one this object is replicating against, and anything under it
            // is a replay or a reordering.
            if (nowUnscaled >= _adoptionArmedUntil) return false;

            if (!_hasRejectRun)
            {
                _hasRejectRun             = true;
                _rejectRunStartedUnscaled = nowUnscaled;
                _rejectRunLowTick         = incomingTick;
                _rejectRunSawASecondTick  = false;
                return false;
            }

            // The run's identity is its LOWEST tick, not its most recent one.
            // A previous owner's frames arrive interleaved with the new owner's
            // and are numerically far above them, so a run that tracked its
            // high-water was pinned by the first such frame and the new owner
            // could never reach it.
            if (incomingTick != _rejectRunLowTick) _rejectRunSawASecondTick = true;
            if ((int)(incomingTick - _rejectRunLowTick) < 0)
                _rejectRunLowTick = incomingTick;

            // One tick, however often repeated, is a replayed frame rather than
            // a clock that is running.
            if (!_rejectRunSawASecondTick) return false;

            if ((uint)(_lastAppliedTick - _rejectRunLowTick) < MinDifferentClockGapTicks)
                return false;

            if (nowUnscaled - _rejectRunStartedUnscaled < ReArmAfterRejectingForSeconds)
                return false;

            // Re-base onto the lowest tick the run saw, and refuse THIS update.
            // Re-basing onto the incoming one instead would adopt whichever
            // sender happened to arrive last, which at a handover is as likely
            // to be the owner that is going away.  The cost is one further
            // update from the new owner; everything it sends after that is
            // above the new watermark and flows.
            _hasRejectRun    = false;
            _lastAppliedTick = _rejectRunLowTick;
            return false;
        }

        /// <summary>
        /// Reset the inbound tick gate.  Called on ownership change, on the
        /// spawn cycle through <see cref="OnOwnerSpawned"/>, and on the pool
        /// hand-back through <see cref="OnOwnerRecycled"/>, so the gate cannot
        /// block the first update from a sender whose tick clock is not the one
        /// the watermark was drawn from.
        /// </summary>
        /// <remarks>
        /// ⚠️ This summary read "on ownership change and on disconnect" while a
        /// handover was its only caller.  The disconnect wording named the
        /// symptom — a fresh session whose tick counter restarts at zero — and
        /// not a site; the site that actually carries a variable across that
        /// boundary is the spawn cycle of a pooled instance.
        /// </remarks>
        internal void ResetInboundTickGate()
        {
            // Clearing the anchor is what re-arms the gate; the two below
            // restate the state that goes with it.  Neither is observable on its
            // own — the run and the corroboration are read only past the anchor
            // check, and the next accepted update re-establishes both — so they
            // are here to leave the object in one describable state rather than
            // to be load-bearing.
            _hasLastAppliedTick = false;
            _lastAppliedTick    = 0u;
            _hasRejectRun       = false;
        }
    }

    // ── Generic typed variable ─────────────────────────────────────────────────

    /// <summary>
    /// A value of type <typeparamref name="T"/> that the object's owner writes
    /// and every client in the room receives. Use a built-in type such as
    /// <see cref="NetworkVariableInt"/>, or derive from this class to replicate
    /// another value type.
    /// </summary>
    /// <typeparam name="T">
    /// The value type. The <see cref="Value"/> setter uses its
    /// <see cref="IEquatable{T}"/> implementation to ignore a write of the value
    /// already held.
    /// </typeparam>
    /// <remarks>
    /// A custom type implements <see cref="NetworkVariableBase.Serialize"/> and
    /// <see cref="NetworkVariableBase.Deserialize"/>, stores received values with
    /// <see cref="ApplyFromWire"/>, and overrides <see cref="IsSendableValue"/>
    /// to refuse the values its <c>Deserialize</c> refuses.
    /// </remarks>
    public abstract class NetworkVariable<T> : NetworkVariableBase
        where T : struct, IEquatable<T>
    {
        // The value the constructor established, restored when a pooled owner
        // is handed back so the next life begins where the prefab says it does.
        private readonly T _initialValue;

        // ── Stored value ───────────────────────────────────────────────────────

        private T _value;

        // ── Events ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Raised with <c>(previous, current)</c> when the value changes: on the
        /// owner when it writes <see cref="Value"/>, and on every other client
        /// when a different value arrives. Not raised by
        /// <see cref="SetValueWithoutNotify"/>.
        /// </summary>
        /// <remarks>
        /// An exception thrown by a handler is caught and logged. When a pooled
        /// object is returned to its pool, the variable goes back to its initial
        /// value and raises the event if that changes the value.
        /// </remarks>
        public event Action<T, T> OnValueChanged;

        // ── Value property ─────────────────────────────────────────────────────

        /// <summary>
        /// Whether <paramref name="value"/> may be written to this variable. The
        /// default accepts every value; override it in a custom type to refuse
        /// the values its own <c>Deserialize</c> refuses.
        /// </summary>
        /// <param name="value">The candidate value.</param>
        /// <param name="reason">
        /// When the method returns <see langword="false"/>, why; the SDK includes
        /// it in the warning it logs.
        /// </param>
        /// <returns><see langword="true"/> to accept the value.</returns>
        /// <remarks>
        /// <para>A value the receivers refuse would be seen by the owner only, so
        /// refusing it when it is written keeps every client holding the same
        /// value.</para>
        /// <para>Keep it a pure function of its argument: it is also called from
        /// the base constructor, before the derived class's fields are set.</para>
        /// </remarks>
        // ⛔ The two policies buy different things, and the difference is worth
        // writing down rather than glossing: refusing the write leaves owner and
        // replicas holding the SAME value, while substituting on send leaves
        // them holding different ones — a quaternion variable assigned
        // `default` reads as the zero quaternion locally and as identity
        // everywhere else. That is accepted for a rotation, because the
        // alternative is a variable that never propagates at all; it is not
        // accepted for a coordinate, because there is no substitute that is not
        // a lie about where the object is.
        //
        // ⚠️ The `///` block above is what an integrator reads in IntelliSense.
        // It carries the contract; this carries the reasoning. An earlier
        // version put both there — and left a <para> unclosed, which is a
        // CS1570 nothing in this repository would have seen, because
        // GenerateDocumentationFile is set nowhere.
        protected virtual bool IsSendableValue(T value, out string reason)
        {
            reason = null;
            return true;
        }

        /// <summary>
        /// Whether assigning <paramref name="value"/> now would be accepted.
        /// </summary>
        /// <param name="value">The candidate value.</param>
        /// <returns>
        /// <see langword="false"/> when the write would be refused: the value is
        /// one this variable refuses, the object belongs to another player, or
        /// the object is not spawned. A refused write changes nothing and raises
        /// no event.
        /// </returns>
        public bool CanSend(T value)
            => IsSendableValue(value, out _) && !WriteWouldNotLand;

        /// <summary>
        /// Assigns <paramref name="value"/> as the <see cref="Value"/> setter
        /// does, and reports whether it was accepted.
        /// </summary>
        /// <param name="value">The value to store and send.</param>
        /// <returns>
        /// <see langword="true"/> when the value was stored or was already held;
        /// <see langword="false"/> when the write was refused (see
        /// <see cref="CanSend"/>).
        /// </returns>
        /// <remarks>
        /// A refusal because of ownership or the object's spawn state is not
        /// logged; a value this variable refuses is still reported with a
        /// warning.
        /// </remarks>
        public bool TrySetValue(T value)
        {
            if (!IsSendableValue(value, out _)) { RefuseUnsendable(value, "TrySetValue"); return false; }
            if (_value.Equals(value)) return true;

            // Both remaining refusals in one question, and asked SILENTLY: the
            // caller is being handed the answer, and a console line beside a
            // returned false is noise the caller did not ask for.
            if (WriteWouldNotLand) return false;

            Value = value;
            return true;
        }

        // One warning per second per closed generic type, as the raw-quaternion
        // writers do: a refused write leaves _value unchanged, so the equality
        // early-out below never absorbs a repeat and an Update() loop assigning
        // NaN would otherwise log every frame.
        private static long _lastUnsendableWriteWarnTicks;

        // ⚠️ Static, per closed generic. A per-variable budget reads as the
        // kinder choice — the line names the variable, so bounding it per
        // variable keeps every name — and it bounds nothing: one update batch
        // names many variable ids, each a different instance with a gate that
        // has never been spent.
        // The hand-back's own budget: a subscriber that throws on every reset
        // must not silence the report of one that throws on every write.
        private static long _lastRecycleValueChangedThrowWarnTicks;
        private static long _lastValueChangedThrowWarnTicks;

        // ⚠️ A SECOND gate, not a reuse of the one above. The two raise sites
        // have different floods: the owning path fires on local assignment, the
        // inbound path on every packet a replica receives. Sharing one budget
        // lets a flood of either decide whether the other is ever reported —
        // and the inbound one is the quieter, so it is the one that would be
        // lost.
        private static long _lastInboundValueChangedThrowWarnTicks;

        /// <summary>
        /// Reopen the subscriber-throw gate.  Internal, and needed because the
        /// gate it clears is static: a case that reads the rule rather than the
        /// residue of whatever ran before it has to say where its window starts.
        /// </summary>
        internal static void ResetValueChangedGateForTest()
            => System.Threading.Interlocked.Exchange(ref _lastValueChangedThrowWarnTicks, 0);

        // ⚠️ Static, where the two above are not, and the constructor is the
        // reason: both conditions below are observed while the variable is being
        // built, so an instance gate would be freshly open for every one of them
        // and bound nothing. A room admitting a hundred spawned objects builds a
        // hundred variables from the wire, and a subclass whose initial value is
        // inadmissible would report each of them.
        private static long _lastInitialValueDesignWarnTicks;
        private static long _lastNoAdmissibleValueWarnTicks;

        /// <summary>
        /// The value a refused <c>initialValue</c> falls back to, checked on the
        /// same terms as the value it replaces.
        /// </summary>
        /// <remarks>
        /// ⚠️ Two hazards live in this one line, and both were found by review
        /// rather than by design.
        ///
        /// <para>🔴 The fallback is itself a write. <c>default(T)</c> is
        /// sendable for every shipped type, but <see cref="NetworkVariable{T}"/>
        /// is a documented extension point, and a subclass whose reader refuses
        /// its own <c>default</c> would have been handed exactly the state this
        /// guard exists to prevent — silently. It is admitted too, and a
        /// subclass that refuses both is a design error in the subclass, said so
        /// once rather than papered over.</para>
        ///
        /// <para>🔴 <see cref="IsSendableValue"/> is virtual and this runs from
        /// the base constructor, before a subclass's own fields exist. The XML
        /// doc asks for a pure function — and a warning is not a defence, which
        /// is the exact sentence this file uses elsewhere about
        /// <c>default(Quaternion)</c>. An override that reads its own state
        /// throws a NullReferenceException out of <c>new</c>, inside
        /// <c>Awake</c>. Contained here: the value is accepted, the design error
        /// is reported, and the setter still refuses the value on every write
        /// once the object exists.</para>
        /// </remarks>
        private T AdmitInitialValue(T initialValue, string context)
        {
            bool refused;
            try
            {
                refused = RefuseUnsendable(initialValue, context);
            }
            catch (Exception ex)
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastInitialValueDesignWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] {GetType().Name}.IsSendableValue threw {ex.GetType().Name} " +
                        "during construction. It is called from the base constructor, before your " +
                        "subclass's fields are assigned, so it must be a pure function of its " +
                        "argument. The initial value is being accepted unchecked; every later " +
                        "write is still checked.");
                return initialValue;
            }

            if (!refused) return initialValue;

            T fallback = default;
            if (IsSendableValue(fallback, out string reason))
                return fallback;

            if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastNoAdmissibleValueWarnTicks))
                Debug.LogWarning(
                    $"[RTMPE] {GetType().Name} refused its initial value AND refuses " +
                    $"default({typeof(T).Name}) ({reason}) — so there is no value this variable " +
                    "can legally hold. Give IsSendableValue an admissible default, or accept " +
                    "default(T). Holding the refused fallback anyway.");
            return fallback;
        }

        private bool RefuseUnsendable(T value, string site)
        {
            if (IsSendableValue(value, out string reason)) return false;

            if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastUnsendableWriteWarnTicks))
                Debug.LogWarning(
                    $"[RTMPE] NetworkVariable<{typeof(T).Name}> (id {VariableId} on " +
                    $"{OwnerLabel}) refused a value at {site}: {reason} The prior value is " +
                    "kept. This variable's own Deserialize refuses that value and keeps the " +
                    "receiver's prior one, so accepting it here would show it to the owner " +
                    "and to nobody else.");
            return true;
        }

        /// <summary>
        /// The current value. Only the owner writes it; every client reads it.
        /// </summary>
        /// <remarks>
        /// <para>Setting a different value stores it, marks the variable for
        /// sending and raises <see cref="OnValueChanged"/>. Setting the value
        /// already held does nothing.</para>
        /// <para>A write is refused, and changes nothing, when the value is one
        /// this variable refuses (for example NaN in a
        /// <see cref="NetworkVariableFloat"/>), when the object is not spawned,
        /// or when the object belongs to another player. The setter cannot
        /// report a refusal, so it logs a warning at most once a second. Use
        /// <see cref="CanSend"/> to ask first, or <see cref="TrySetValue"/> to
        /// learn the outcome.</para>
        /// </remarks>
        public T Value
        {
            get => _value;
            set
            {
                // Ahead of the equality test and of the lifecycle guard: an
                // unsendable value is a caller error whatever state the object
                // is in, and it is the caller who needs to hear about it.
                if (RefuseUnsendable(value, "assignment")) return;

                // IEquatable<T>.Equals — no boxing, no allocation.
                if (_value.Equals(value)) return;

                // A write outside the owner's spawned life is refused, and
                // reported like every other refusal on this setter: before the
                // first spawn the value belongs in the constructor or in
                // OnNetworkSpawn, and after the last despawn a dirty variable
                // would re-publish dead state on the next flush and raise
                // OnValueChanged at subscribers the teardown has cleared.  (The
                // list type admits pre-spawn writes through its own
                // WriteWouldNotLand and says why beside _hasBeenSpawned.)
                if (RefuseLifecycleWrite()) return;

                // And reject writes this client is not entitled to make. The
                // flush skips a component the local player does not own, so
                // storing the value here and raising OnValueChanged would show
                // it to this client and to nobody else, for good — and leave
                // IsDirty set, because nothing ever serialises it to clear it.
                if (RefuseUnownedWrite("assignment")) return;

                T oldValue = _value;
                _value     = value;
                IsDirty    = true;

                // Fire AFTER _value is updated so callbacks can safely read Value.
                // A user-supplied OnValueChanged subscriber that throws must
                // not abort the calling code path — for the owning side this
                // is the variable-flush hot loop, where one bad subscriber
                // would otherwise stop every sibling NetworkVariable on the
                // same object from publishing this tick.  Catch and surface;
                // the dirty flag was already set, so the next flush retries.
                try
                {
                    OnValueChanged?.Invoke(oldValue, value);
                }
                catch (Exception ex)
                {
                    // A replica takes its value from the wire, so a subscriber
                    // that throws on one update throws on every update — once
                    // per inbound entry, with a stack trace, on the main thread.
                    if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastValueChangedThrowWarnTicks))
                        Debug.LogError(
                            $"[RTMPE] NetworkVariable<{typeof(T).Name}>.OnValueChanged threw " +
                            $"{ex.GetType().Name}: {ex.Message}.  Subscriber exception isolated; " +
                            "the new value is already stored locally.");
                }
            }
        }

        // ── Constructor ────────────────────────────────────────────────────────

        /// <summary>
        /// Registers the variable with <paramref name="owner"/> and sets its
        /// initial value without raising <see cref="OnValueChanged"/>.
        /// </summary>
        /// <param name="owner">The component the variable belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the variable is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">
        /// The starting value. A value this variable refuses is replaced by
        /// <c>default(T)</c>, with a warning.
        /// </param>
        /// <exception cref="ArgumentException">
        /// <paramref name="memberName"/> is <see langword="null"/>, empty or
        /// whitespace.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Another variable on the same component already has this identity.
        /// </exception>
        protected NetworkVariable(
            NetworkBehaviour owner,
            string           memberName,
            T                initialValue = default)
            : base(owner, memberName)
        {
            // Assign directly (bypassing the setter) so no event fires and
            // IsDirty remains false at construction.  Still admitted, because a
            // seeded value is written to the wire by the first flush exactly as
            // an assigned one is: `new NetworkVariableFloat(this, nameof(_speed), float.NaN)`
            // is the same defect as assigning it a frame later.
            _value        = AdmitInitialValue(initialValue, "construction");
            _initialValue = _value;
        }

        internal override void OnOwnerRecycled()
        {
            // Assigned as the constructor assigns it — through the same
            // admission, which on a seed the constructor already admitted
            // refuses nothing and says nothing — and announced when it differs,
            // so a subscriber that outlives the life sees the seed and not the
            // ended life's value.  No dirty flag: the base clears it, and the
            // next life announces to the room what it writes.
            T oldValue = _value;
            _value = AdmitInitialValue(_initialValue, "recycle");
            base.OnOwnerRecycled();
            if (oldValue.Equals(_value)) return;
            try
            {
                OnValueChanged?.Invoke(oldValue, _value);
            }
            catch (Exception ex)
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastRecycleValueChangedThrowWarnTicks))
                    Debug.LogError(
                        $"[RTMPE] NetworkVariable<{typeof(T).Name}>.OnValueChanged threw " +
                        $"{ex.GetType().Name}: {ex.Message} on the pool hand-back.  Subscriber " +
                        "exception isolated; the seed is already stored locally.");
            }
        }

        // ── Receive-side API ───────────────────────────────────────────────────

        /// <summary>
        /// Stores <paramref name="value"/> without raising
        /// <see cref="OnValueChanged"/> and without marking the variable for
        /// sending.
        /// </summary>
        /// <param name="value">The value to store.</param>
        /// <remarks>
        /// Ignored when the object is not spawned. A value this variable refuses
        /// is not stored, and a warning is logged. To apply a received value in
        /// a custom type's <c>Deserialize</c>, use <see cref="ApplyFromWire"/>
        /// instead.
        /// </remarks>
        public void SetValueWithoutNotify(T value)
        {
            // Drop late inbound updates that arrive after OnNetworkDespawn:
            // the owning NetworkBehaviour may have already cleared subscribers
            // and torn down its game-side state, and the GameObject itself
            // may be mid-Destroy.  Mutating _value here would let user code
            // observe the post-despawn value through whatever callbacks
            // remain in flight, and re-anchor the variable's last-applied
            // tick gate against a packet whose target object is gone.
            if (Owner == null || !Owner.IsSpawned) return;

            // The third write path, and public.  Deserialize reaches this only
            // with a value it has already admitted, so for the wire this is
            // redundant — but the method is on the public surface, so without
            // it an integrator can seed the exact state the reader refuses and
            // the rule would hold on two paths of three.
            if (RefuseUnsendable(value, "SetValueWithoutNotify")) return;

            LastInboundRefused = false;
            _value = value;
            // Intentionally does NOT set IsDirty or fire OnValueChanged.
        }

        /// <inheritdoc/>
        internal sealed override bool ReportsRefusals => true;

        /// <summary>
        /// Stores a value received from the owner and raises
        /// <see cref="OnValueChanged"/> when it differs from the value held. Call
        /// it from a custom type's <c>Deserialize</c>.
        /// </summary>
        /// <param name="value">The value read from the network.</param>
        /// <remarks>
        /// It does not mark the variable for sending. It is ignored when the
        /// object is not spawned, and a value this variable refuses (see
        /// <see cref="IsSendableValue"/>) is not stored. An exception thrown by a
        /// handler is caught and logged.
        /// </remarks>
        protected internal void ApplyFromWire(T value)
        {
            LastInboundRefused = true;
            if (Owner == null || !Owner.IsSpawned) return;
            if (RefuseUnsendable(value, "inbound update")) return;
            LastInboundRefused = false;

            // IEquatable<T>.Equals — no boxing, no allocation.
            if (_value.Equals(value)) return;

            T oldValue = _value;
            _value     = value;

            // ⚠️ The caller is the inbound dispatch loop, which walks every
            // entry in the datagram and every variable on the object.  A
            // subscriber that throws here would abort the entries after it —
            // and because a replica takes its value from the wire, it would do
            // so on every packet, for ever.  Isolated and rate-gated, exactly
            // as the owning path isolates the flush loop.
            try
            {
                OnValueChanged?.Invoke(oldValue, value);
            }
            catch (Exception ex)
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastInboundValueChangedThrowWarnTicks))
                    Debug.LogError(
                        $"[RTMPE] NetworkVariable<{typeof(T).Name}>.OnValueChanged threw " +
                        $"{ex.GetType().Name}: {ex.Message} applying an inbound update.  " +
                        "Subscriber exception isolated; the new value is already stored locally.");
            }
        }
    }

    // ── String variable (reference type — not struct) ─────────────────────────

    /// <summary>
    /// A replicated <see cref="string"/> value. It has the same members as
    /// <see cref="NetworkVariable{T}"/>, and stores <see langword="null"/> as
    /// <see cref="string.Empty"/>.
    /// </summary>
    /// <remarks>
    /// <para>Keep the value under about 1,100 bytes of UTF-8 (fewer characters
    /// when the text is not ASCII), and split longer text across several
    /// variables: a value must fit in one datagram to be sent. A longer value is
    /// accepted — it is stored, <see cref="OnValueChanged"/> is raised on the
    /// owner, and <see cref="CanSend"/> and <see cref="TrySetValue"/> return
    /// <see langword="true"/> — but it is not sent, and a warning is logged once
    /// a second until a shorter value is written.</para>
    /// <para>A string that is not valid UTF-8 (for example one a
    /// <c>Substring</c> cut in the middle of a surrogate pair), or that is longer
    /// than 65533 bytes of UTF-8, is refused.</para>
    /// </remarks>
    public sealed class NetworkVariableString : NetworkVariableBase
    {
        // Strict UTF-8 codec — the lax decoder silently substitutes U+FFFD
        // for malformed byte sequences, letting a hostile peer smuggle bytes
        // that survive the decode but mutate downstream string-equality
        // invariants (display names, scene keys, room tags, reserved-key
        // checks).  Symmetric with the RPC stack (M19-RPC-04/05) and the
        // RoomPacketParser (M18-UTF8-01).
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // ── Stored value ───────────────────────────────────────────────────────

        private string _value;

        // The seed the constructor admitted, restored when a pooled owner is
        // handed back.
        private readonly string _initialValue;

        // ── Events ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Raised with <c>(previous, current)</c> when the value changes: on the
        /// owner when it writes <see cref="Value"/>, and on every other client
        /// when a different value arrives. Both arguments are non-null. Not
        /// raised by <see cref="SetValueWithoutNotify"/>.
        /// </summary>
        /// <remarks>
        /// An exception thrown by a handler is caught and logged. When a pooled
        /// object is returned to its pool, the variable goes back to its initial
        /// value and raises the event if that changes the value.
        /// </remarks>
        public event Action<string, string> OnValueChanged;

        // ── Value property ─────────────────────────────────────────────────────

        /// <summary>
        /// Whether assigning <paramref name="value"/> now would be accepted.
        /// </summary>
        /// <param name="value">The candidate value.</param>
        /// <returns>
        /// <see langword="false"/> when the write would be refused: the string is
        /// not valid UTF-8 or is longer than 65533 bytes of UTF-8, the object
        /// belongs to another player, or the object is not spawned. A value too
        /// long for one datagram is accepted but not sent; see
        /// <see cref="NetworkVariableString"/>.
        /// </returns>
        public bool CanSend(string value)
            => IsSendableString(value ?? string.Empty, out _) && !WriteWouldNotLand;

        /// <summary>
        /// Assigns <paramref name="value"/> as the <see cref="Value"/> setter
        /// does, and reports whether it was accepted.
        /// </summary>
        /// <param name="value">The value to store and send.</param>
        /// <returns>
        /// <see langword="true"/> when the value was stored or was already held;
        /// <see langword="false"/> when the write was refused (see
        /// <see cref="CanSend"/>).
        /// </returns>
        public bool TrySetValue(string value)
        {
            string normalized = value ?? string.Empty;
            if (!IsSendableString(normalized, out _))
            {
                RefuseUnsendableString(normalized, "TrySetValue");
                return false;
            }
            if (_value == normalized) return true;
            if (WriteWouldNotLand) return false;

            Value = normalized;
            return true;
        }

        /// <summary>
        /// Whether <paramref name="value"/> is one this variable's own
        /// <see cref="Serialize"/> can put on the wire.
        /// </summary>
        /// <remarks>
        /// 🔴 The same rule as <c>NetworkVariable{T}.IsSendableValue</c>, and a
        /// sharper failure than the one it was written for. A scalar that its
        /// reader refuses costs one update; a string this codec cannot encode
        /// costs the whole flush, permanently.
        ///
        /// <para><c>StrictUtf8</c> is constructed with
        /// <c>throwOnInvalidBytes: true</c>, which sets the ENCODER fallback as
        /// well as the decoder's, so <c>GetBytes</c> throws on a lone surrogate
        /// — and <see cref="Serialize"/> is called from the flush loop, which
        /// has no catch: <c>VariableFlushBudget.TryAppend</c>,
        /// <c>NetworkBehaviour.FlushDirtyVariables</c> and
        /// <c>ObjectDispatchOps.FlushAll</c> all pass it through. The throw
        /// therefore aborts the flush for every component after this one, and
        /// because <c>IsDirty</c> is never cleared it does so again on every
        /// tick, for ever. A truncating <c>Substring</c> on a player name is
        /// the ordinary way to produce a lone surrogate.</para>
        /// </remarks>
        private bool IsSendableString(string value, out string reason)
        {
            string s = value ?? string.Empty;

            int byteCount;
            try
            {
                byteCount = StrictUtf8.GetByteCount(s);
            }
            catch (EncoderFallbackException ex)
            {
                reason = "the value is not encodable as UTF-8 (" + ex.Message +
                         "), typically a lone surrogate left by a Substring that " +
                         "split a surrogate pair.";
                return false;
            }

            // ⛔ The frame, not the string.  `Serialize` writes a 2-byte length
            // ahead of the bytes and `SerializeWithId` frames the whole of that
            // under a ushort `value_len`, so 65534 and 65535 bytes were admitted
            // here and could never be framed: the write was accepted, the entry
            // was withheld, and the variable stayed dirty — a full 64 KB encode
            // every tick, for ever, reported against a bound the value was
            // under.
            if (byteCount > MaxSendableStringBytes)
            {
                reason = $"the value is {byteCount} UTF-8 bytes, above the " +
                         $"{MaxSendableStringBytes} a framed entry can carry " +
                         $"({ushort.MaxValue} less the length this codec writes ahead of them).";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// The most UTF-8 bytes a value can hold and still be framed: the
        /// <c>value_len</c> field's range less the 2-byte length
        /// <see cref="Serialize"/> writes ahead of the bytes.
        /// </summary>
        internal const int MaxSendableStringBytes = ushort.MaxValue - sizeof(ushort);

        private static long _lastUnsendableStringWarnTicks;
        private static long _lastStringValueChangedThrowWarnTicks;

        // See the symmetric second gate on NetworkVariable<T>.
        private static long _lastRecycleStringValueChangedThrowWarnTicks;
        private static long _lastInboundStringValueChangedThrowWarnTicks;

        private bool RefuseUnsendableString(string value, string site)
        {
            if (IsSendableString(value, out string reason)) return false;

            if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastUnsendableStringWarnTicks))
                Debug.LogWarning(
                    $"[RTMPE] NetworkVariableString (id {VariableId} on {OwnerLabel}) refused " +
                    $"a value at {site}: {reason} The prior value is kept. Accepting it would " +
                    "throw out of Serialize on the next flush, which has no catch — taking " +
                    "every variable behind it with it, on every tick, for the rest of the " +
                    "session.");
            return true;
        }

        /// <summary>
        /// The current value, never <see langword="null"/>: assigning
        /// <see langword="null"/> stores <see cref="string.Empty"/>. Only the
        /// owner writes it; every client reads it.
        /// </summary>
        /// <remarks>
        /// <para>Setting a different value stores it, marks the variable for
        /// sending and raises <see cref="OnValueChanged"/>. Setting the value
        /// already held does nothing.</para>
        /// <para>A write is refused, and changes nothing, when the string is not
        /// valid UTF-8 or is longer than 65533 bytes of UTF-8, when the object is
        /// not spawned, or when the object belongs to another player. The setter
        /// cannot report a refusal, so it logs a warning at most once a second.
        /// Use <see cref="CanSend"/> to ask first, or <see cref="TrySetValue"/>
        /// to learn the outcome. A value too long for one datagram is stored but
        /// not sent; see <see cref="NetworkVariableString"/>.</para>
        /// </remarks>
        public string Value
        {
            get => _value;
            set
            {
                // Normalise null to "" before comparison and storage to ensure
                // consistent behaviour when null is assigned as a value.
                string normalized = value ?? string.Empty;

                // ⚠️ The equality test FIRST, and only here. Every other
                // admission in this file runs ahead of its early-out, because
                // for a struct `Equals(NaN, NaN)` is true and a refused value
                // could otherwise be absorbed. A string cannot be absorbed that
                // way: an unsendable value is never stored, so `_value` is
                // always sendable, so equality implies the incoming value is
                // sendable too. Running the admission first cost a full UTF-8
                // scan of the whole string on every assignment — including the
                // per-frame re-assignment of an unchanged name, which used to
                // early-out on an ordinal compare.
                if (_value == normalized) return;

                // An unsendable value is a caller error whatever lifecycle state
                // the object is in, so this precedes the despawn guard.
                if (RefuseUnsendableString(normalized, "assignment")) return;

                // Symmetric with NetworkVariable<T>.Value: a write outside the
                // owner's spawned life is refused and reported — a torn-down
                // object must not re-emit post-despawn state or raise callbacks
                // at cleared subscribers, and a pre-spawn value belongs in the
                // constructor or in OnNetworkSpawn.
                if (RefuseLifecycleWrite()) return;

                // Symmetric with NetworkVariable<T>.Value: a write this client
                // does not own is skipped by the flush, so accepting it here
                // shows it to this client alone, permanently.
                if (RefuseUnownedWrite("assignment")) return;

                string oldValue = _value;
                _value          = normalized;
                IsDirty         = true;

                // Subscriber-isolation: see the symmetric guard on
                // NetworkVariable<T>.Value.  The new value is already in
                // _value; a throwing subscriber must not stop the flush.
                try
                {
                    OnValueChanged?.Invoke(oldValue, normalized);
                }
                catch (Exception ex)
                {
                    // See the symmetric gate on NetworkVariable<T>.Value.
                    if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastStringValueChangedThrowWarnTicks))
                        Debug.LogError(
                            $"[RTMPE] NetworkVariableString.OnValueChanged threw " +
                            $"{ex.GetType().Name}: {ex.Message}.  Subscriber exception isolated; " +
                            "the new value is already stored locally.");
                }
            }
        }

        // ── Constructor ────────────────────────────────────────────────────────

        /// <summary>
        /// Registers the variable with <paramref name="owner"/> and sets its
        /// initial value without raising <see cref="OnValueChanged"/>.
        /// </summary>
        /// <param name="owner">The component the variable belongs to.</param>
        /// <param name="memberName">
        /// The name of the field or property the variable is assigned to. Pass
        /// <c>nameof(_field)</c>.
        /// </param>
        /// <param name="initialValue">
        /// The starting value. <see langword="null"/> is stored as an empty
        /// string, and a value this variable refuses is replaced by an empty
        /// string, with a warning.
        /// </param>
        /// <exception cref="ArgumentException">
        /// <paramref name="memberName"/> is <see langword="null"/>, empty or
        /// whitespace.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Another variable on the same component already has this identity.
        /// </exception>
        public NetworkVariableString(
            NetworkBehaviour owner,
            string           memberName,
            string           initialValue = "")
            : base(owner, memberName)
        {
            // Seeded values reach the wire on the first flush exactly as
            // assigned ones do, so they are admitted on the same terms.
            string seed = initialValue ?? string.Empty;
            _value        = RefuseUnsendableString(seed, "construction") ? string.Empty : seed;
            _initialValue = _value;
        }

        internal override void OnOwnerRecycled()
        {
            // Through the same admission the constructor used — on the seed it
            // already admitted this refuses nothing and says nothing — and
            // announced when it differs, for the subscriber that outlives the
            // life.
            string oldValue = _value;
            _value = RefuseUnsendableString(_initialValue, "recycle") ? string.Empty : _initialValue;
            base.OnOwnerRecycled();
            if (oldValue == _value) return;
            try
            {
                OnValueChanged?.Invoke(oldValue, _value);
            }
            catch (Exception ex)
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastRecycleStringValueChangedThrowWarnTicks))
                    Debug.LogError(
                        "[RTMPE] NetworkVariableString.OnValueChanged threw " +
                        $"{ex.GetType().Name}: {ex.Message} on the pool hand-back.  Subscriber " +
                        "exception isolated; the seed is already stored locally.");
            }
        }

        // ── Receive-side API ───────────────────────────────────────────────────

        /// <summary>
        /// Stores <paramref name="value"/> without raising
        /// <see cref="OnValueChanged"/> and without marking the variable for
        /// sending. <see langword="null"/> is stored as
        /// <see cref="string.Empty"/>.
        /// </summary>
        /// <param name="value">The value to store.</param>
        /// <remarks>
        /// Ignored when the object is not spawned. A value this variable refuses
        /// is not stored, and a warning is logged.
        /// </remarks>
        public void SetValueWithoutNotify(string value)
        {
            // See NetworkVariable<T>.SetValueWithoutNotify for the rationale.
            if (Owner == null || !Owner.IsSpawned) return;

            // The third write path, and public.  Deserialize reaches this only
            // with bytes it decoded, so for the wire it is redundant; the method
            // is on the public surface, so without it the rule holds on two
            // paths of three.
            string normalized = value ?? string.Empty;
            if (RefuseUnsendableString(normalized, "SetValueWithoutNotify")) return;

            LastInboundRefused = false;
            _value = normalized;
        }

        /// <inheritdoc/>
        internal override bool ReportsRefusals => true;

        /// <summary>
        /// Apply a string that arrived on the wire: store it, and raise
        /// <see cref="OnValueChanged"/> for the receiving client.
        /// </summary>
        /// <remarks>
        /// The symmetric counterpart of
        /// <c>NetworkVariable&lt;T&gt;.ApplyFromWire</c>, and it exists for the
        /// same reason: <see cref="SetValueWithoutNotify"/> is a public
        /// contract meaning "do not echo", and routing the wire through it left
        /// every receiving client silent.  Comparison is ordinal, as the
        /// setter's is; <see cref="NetworkVariableBase.IsDirty"/> is not set,
        /// so a replica never re-publishes what it was told; and a throwing
        /// subscriber is isolated because the caller is the inbound dispatch
        /// loop, where an escape would abort every entry after it in the
        /// datagram — on every packet, since a replica's value always comes
        /// from the wire.
        /// <para>
        /// 🔑 <c>internal</c>, and NOT <c>protected internal</c> like its scalar
        /// counterpart — because <see cref="NetworkVariableString"/> is
        /// <c>sealed</c>. There is no derived type for a wider modifier to
        /// reach, the compiler says so (<c>CS0628</c>), and the extension gap
        /// that made <see cref="NetworkVariable{T}.ApplyFromWire"/> widen does
        /// not exist here: an integrator writing a reference-typed variable
        /// derives <see cref="NetworkVariableBase"/>, which declares no event,
        /// so their type owns its own <c>OnValueChanged</c> and can raise it.
        /// </para>
        /// </remarks>
        internal void ApplyFromWire(string value)
        {
            string normalized = value ?? string.Empty;

            // ⚠️ `Owner == null || !Owner.IsSpawned`, NOT the setter's
            // `Owner != null && !Owner.IsSpawned`. The two differ on exactly one
            // state and it is the one that matters here: a null Owner, which in
            // Unity includes a NetworkBehaviour whose GameObject is mid-Destroy.
            // The setter can use the weaker form because a local assignment to a
            // variable with no owner is a programming error the caller can see;
            // an INBOUND update arrives for an object the wire still believes in,
            // and applying it would raise against subscribers the teardown has
            // already cleared.
            //
            // 🔑 This is the predicate `SetValueWithoutNotify` uses, and it is
            // the path this method replaced — the guard has to come from the
            // method being replaced, not from the neighbour that looks similar.
            LastInboundRefused = true;
            if (Owner == null || !Owner.IsSpawned) return;
            if (RefuseUnsendableString(normalized, "inbound update")) return;
            LastInboundRefused = false;
            if (_value == normalized) return;

            string oldValue = _value;
            _value          = normalized;

            try
            {
                OnValueChanged?.Invoke(oldValue, normalized);
            }
            catch (Exception ex)
            {
                if (RTMPE.Core.WarnGate.ShouldEmit(ref _lastInboundStringValueChangedThrowWarnTicks))
                    Debug.LogError(
                        "[RTMPE] NetworkVariableString.OnValueChanged threw " +
                        $"{ex.GetType().Name}: {ex.Message} applying an inbound update.  " +
                        "Subscriber exception isolated; the new value is already stored locally.");
            }
        }

        // ── Serialisation ──────────────────────────────────────────────────────

        /// <inheritdoc/>
        /// <exception cref="ArgumentException">
        /// The value is longer than 65535 bytes of UTF-8.
        /// </exception>
        public override void Serialize(BinaryWriter writer)
        {
            // Encode as a 2-byte LE uint16 length prefix followed by raw UTF-8
            // bytes.  Wire-compatible with the Go server (binary.LittleEndian.Uint16).
            // `BinaryWriter.Write(string)` would emit a .NET 7-bit variable-length
            // integer prefix which is NOT compatible with the Go wire format.
            byte[] bytes = StrictUtf8.GetBytes(_value ?? string.Empty);
            if (bytes.Length > ushort.MaxValue)
            {
                throw new ArgumentException(
                    $"NetworkVariableString value is {bytes.Length} UTF-8 bytes, " +
                    $"which exceeds the wire-format maximum of {ushort.MaxValue}.  " +
                    "Shorten the string, split across multiple variables, or use an RPC for bulk data.",
                    nameof(_value));
            }

            // 2-byte LE length prefix (compatible with Go binary.LittleEndian.Uint16).
            // BinaryWriter.Write(ushort) is little-endian per .NET Standard 2.1 spec.
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        /// <inheritdoc/>
        public override void Deserialize(BinaryReader reader)
        {
            // Read the 2-byte LE length prefix, then read exactly that many
            // UTF-8 bytes — matches the Serialize path above and the Go server format.
            ushort len = reader.ReadUInt16();
            // ⚠️ NOT `reader.ReadBytes(len)`.  That commits `len` bytes before
            // it reads and then returns short instead of throwing, so the
            // declared length — a number the sender chose — sizes an allocation
            // on the receiver's main thread.  A 2-byte entry declaring 65535
            // cost 64 KiB; 255 of them in one 1530-byte datagram cost 16.7 MB,
            // measured.  `WireByteBlock.ReadExactly` answers the same question
            // and raises the same `EndOfStreamException` for the same truncated
            // packet, in proportion to the bytes that are actually there.
            byte[] bytes = WireByteBlock.ReadExactly(
                reader, len, "NetworkVariableString.Deserialize");
            try
            {
                ApplyFromWire(StrictUtf8.GetString(bytes));
            }
            catch (DecoderFallbackException ex)
            {
                // Surface malformed UTF-8 as the same truncation contract the
                // dispatcher already handles — the packet is dropped and the
                // prior value is preserved.
                throw new EndOfStreamException(
                    "NetworkVariableString.Deserialize: malformed UTF-8 in payload.",
                    ex);
            }
        }
    }
}
