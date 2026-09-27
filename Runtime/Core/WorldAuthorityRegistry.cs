// RTMPE SDK — Runtime/Core/WorldAuthorityRegistry.cs
//
// The book of world-authority objects this client holds, one entry per live
// instance, and the four questions RtmpeWorldAuthority asks of it: which
// instance answers a key, whether an answer has changed since it was last
// announced, whether a world of this client's making is to be born — once,
// however many times the room refused it on the way — and what an instance
// owned by this client should do about itself.
//
// 🔑 Everything here is a decision and nothing here is an engine call — the
// same split RtmpeConnectionBootstrap makes with ConnectionBootstrapOps.  The
// component supplies the instances (through IWorldAuthorityInstance, so a test
// can supply its own) and the clock; this decides, and a shard drives every
// rule below without a Unity install.
//
// 🔑 Why a key can name more than one instance.  A world is one object per
// room, but it reaches a client over an unordered channel: a host that spawned
// one while another host's copy was in flight holds two, a peer receiving the
// new host's re-creation before the old copy's despawn holds two, and neither
// case is a fault to refuse — it is a race to SETTLE.  The rule that settles it
// is one every client can evaluate on the same facts: the live instance with
// the smallest object id is the one, and the owner of any other removes it.
// Object ids are unique across senders (ObjectIdMath.Compose), so the answer
// is the same on every client, and only an OWNER acts on it — a peer's view of
// a duplicate is never grounds for a despawn it could not relay anyway.  An
// instance the room refused is no shared fact — it exists on this client
// alone — so it stands aside from the election: it neither wins the key here
// nor makes the instance every other client holds the loser.
//
// ⛔ One exception outranks the id rule, and it is the migration: an instance
// this client did not create and now owns — the previous host's world, handed
// over by ownership reassignment — is kept whatever its id, so long as the room
// holds no world of somebody else's that wins by id (then it is removed like
// any loser, and the room keeps the world it already has).  Kept as it is
// where the gateway says the room's replay follows the owner
// (CapabilityFlags.ReplayFollowsOwner): the room hands a departed owner's
// buffered spawn to its host, so a joiner arriving after the migration is
// replayed this very object under this client, and it keeps its identity.
// Where the gateway does not say so — a gateway that predates plan §10/2 — it
// is re-created under this client's own id space: that replay evicts the
// departed owner's rows, so a joiner would never see the world, and the
// gateway refuses a spawn re-sent under a foreign id space.  The precedence
// travels with the state: the copy such
// an instance is re-created as inherits it, and so does the copy of that
// copy, so a self-created instance sharing the key yields to whichever of
// them is live — whatever the ids, and whichever of the two acted first in
// the frame — and the state the room was playing on survives the handover
// instead of a fresh layout winning by an accident of numbering.  Only the
// client that owns them knows that precedence, so it decides only between
// instances that client owns: against one somebody else owns the id decides
// here exactly as it does there, or two owners would each keep one for ever.
// The shared election comes first, by id, over everything the room holds;
// where it names an instance of this client's, this client chooses among its
// own.  A peer elects by id alone until the owner's act reaches it, which is
// one round trip away.
//
// 🔑 The precedence is the STATE's, not the adoption's.  A copy adopted from
// an owner who had already gone when this client arrived — the host whose
// process ended while it was alone, whose seat outlived it, and whose objects
// the replay handed to the client that entered next — has had no value of its
// variables delivered to it: it stands at the prefab's values, and nothing of
// the room's was ever in it.  Such a copy carries no precedence, and the world
// it is is BORN — the game populates it — rather than inherited empty for the
// rest of the room's life: the adopted instance itself where the replay
// follows the owner, the copy it is re-created as where it does not.
// The instance says which it is (IWorldAuthorityInstance.HoldsOwnersState)
// from what the wire delivered to it, and a world that declares no variables
// cannot say, so it is taken as carrying what its owner had.  ⚠️ A world handed
// to this client before it held it — the promotion catch-up's replay of one it
// never received — carries the room's state only where EVERY variable was
// delivered: its only record is the frames held while it was unknown, the last
// ten seconds of them, and a variable they did not reach stands at the seed.
// Kept and sent, those seeds would overwrite what every peer holds; so such a
// world is born instead, and sent whole once the birth has written it.
//
// 🔑 And one exception to the id rule in the ANSWER: of two instances one other
// client owns, a copy that has received nothing from that owner since it became
// that owner's here stands aside for one that has.  An owner flushes the whole of every object it holds
// to a player who joins, so a copy it never wrote to the joiner is a copy it
// does not hold — the world a lost promotion catch-up handed it, which the
// room's replay keeps delivering, frozen, to everyone who joins after.  Never
// in an owner's verdict: that is taken on facts every client shares, and what
// reached this client is this client's alone.

using System;
using System.Collections.Generic;

namespace RTMPE.Core
{
    /// <summary>
    /// A world-authority instance as the registry needs to see it: its id,
    /// whether it is still a live networked object, and whether this client
    /// owns it.  Implemented by <c>RtmpeWorldAuthority</c>; a test supplies
    /// its own.
    /// </summary>
    internal interface IWorldAuthorityInstance
    {
        /// <summary>The network object id of the instance.</summary>
        ulong ObjectId { get; }

        /// <summary>
        /// Whether the instance is still a spawned object this client holds.
        /// A component destroyed by a scene load or by <c>Object.Destroy</c>
        /// reaches no despawn callback, so the registry asks rather than
        /// trusting the last thing it was told.
        /// </summary>
        bool IsLive { get; }

        /// <summary>Whether the local player owns the instance now.</summary>
        bool IsOwnedBySelf { get; }

        /// <summary>The room player id of the instance's owner now, as this client sees it.</summary>
        string OwnerPlayerId { get; }

        /// <summary>
        /// Whether what reached this instance FROM THE WIRE is the room's state
        /// — or the object declares no variable that could say.  For an
        /// instance this client held as a peer, one value delivered is the
        /// answer; for one handed to this client before it held it, every
        /// variable must have received a whole value
        /// (<see cref="WorldAuthorityRegistry.HoldsTheRoomsState"/>).  What it
        /// measures, not what it is for: false while nothing has arrived yet,
        /// which for an instance this client did not create and now owns means
        /// nothing of the room's state was ever in it (its owner was gone
        /// before this client arrived) or only part of it was, and which for an
        /// instance this client created means nothing at all — a re-creation's
        /// copy carries its state by a local copy, not over the wire, and reads
        /// false however much it holds.
        /// </summary>
        /// <remarks>
        /// ⛔ Asked at an adoption, of an instance this client did not create:
        /// by <see cref="WorldAuthorityRegistry.MarkAdopted"/>, which records
        /// the precedence once, and by the component, which decides from the
        /// same reading at the same moment whether to send the world whole.
        /// Every other reading of it would be about an instance for which it
        /// says nothing, or at a moment where it can still change.
        /// <para>
        /// The instance answers it by <see cref="WorldAuthorityRegistry.HoldsTheRoomsState"/>,
        /// which is where the reading is decided.
        /// </para>
        /// </remarks>
        bool HoldsOwnersState { get; }

        /// <summary>
        /// Whether a value of the object's variables has reached this instance
        /// from the wire since it spawned here or its owner last changed —
        /// since it has been the named owner's: <see langword="true"/> when one
        /// has, <see langword="false"/> when none has, and
        /// <see langword="null"/> for an object that declares no variable and
        /// so cannot say.  A copy handed to a new owner arrives carrying the
        /// previous owner's values, which say nothing of the new owner.
        /// </summary>
        /// <remarks>
        /// Asked of an instance another client owns, for the election: an
        /// owner flushes the whole of every object it holds to a player who
        /// joins, so of two instances of one key that one owner holds, the one
        /// its owner has written to this client since it became that owner's is
        /// the one it holds.  See <see cref="WorldAuthorityRegistry.Elected"/>.
        /// </remarks>
        bool? ReceivedValues { get; }
    }

    /// <summary>
    /// What an instance owned by this client should do about itself, decided
    /// by <see cref="WorldAuthorityRegistry.VerdictFor"/>.
    /// </summary>
    internal enum WorldInstanceVerdict
    {
        /// <summary>
        /// Nothing: it is the key's instance, it is not this client's to act
        /// on, or its re-creation is booked and not yet due.
        /// </summary>
        Keep,

        /// <summary>
        /// It lost the election — another live instance answers the key — and
        /// this client, as its owner, is the one party that can remove it.
        /// </summary>
        DespawnSelf,

        /// <summary>
        /// It is a world this client inherited rather than spawned, on a
        /// gateway whose replay does not follow an object's owner, and it must
        /// be spawned again under this client's own id space with its state
        /// carried over, then despawned — on the wire, because the room and
        /// every peer hold it.
        /// </summary>
        Recreate,

        /// <summary>
        /// It is a world this client spawned and the room refused for want of
        /// a slot: spawned again on the same terms as an inherited one, with
        /// its state carried over, then torn down here alone — the room never
        /// held it, so there is nobody to tell.
        /// </summary>
        RecreateRefused,

        /// <summary>
        /// This client's copy goes, and nothing goes on the wire.  Either it
        /// is not this client's, its owner changed hands under it, and that
        /// owner has been running a world of its own beside it for longer
        /// than a migration takes — the owner never held this one and will
        /// never remove it, and a non-owner's despawn would be refused anyway
        /// — or it is a spawn of this client's the room refused, and another
        /// instance of this client's outranks it: one carrying the room's
        /// state when this one does not, or, between equals, one the room
        /// holds, or the refused spawn with the smaller id — so re-creating
        /// this one would only rival that one.
        /// </summary>
        DropLocally,
    }

    /// <summary>
    /// What <see cref="WorldAuthorityRegistry.MarkSuccessor"/> did with a
    /// record: made it, or refused it for the one reason it names — so the
    /// component can say, in its warning, which of the two instances is not
    /// what a re-creation expects.
    /// </summary>
    internal enum SuccessorRecord
    {
        /// <summary>The copy carries what the source had; the record is made.</summary>
        Recorded,

        /// <summary>The key does not hold the source: its entry is gone.</summary>
        SourceUnknown,

        /// <summary>
        /// The key does not hold the copy: it registered under another key —
        /// the prefab's key edited while the source was live — or under none.
        /// </summary>
        CopyUnknown,

        /// <summary>The copy and the source are one instance.</summary>
        SameInstance,

        /// <summary>
        /// The copy was not created by this client — spawned with no seat to
        /// own it — so it is held here as a peer's copy would be.
        /// </summary>
        CopyNotOfThisClientsMaking,
    }

    /// <summary>
    /// The live world-authority instances this client holds, keyed by world
    /// key, with the election and the announcement bookkeeping over them.
    /// Main-thread only, like everything the spawn path touches.
    /// </summary>
    internal sealed class WorldAuthorityRegistry
    {
        private sealed class Entry
        {
            public IWorldAuthorityInstance Instance;
            public bool CreatedBySelf;
            public bool Adopted;
            public bool Refused;
            public bool Ready;
            // Carries the state the room was playing on: adopted from a
            // departed host with that state delivered to it, or re-created —
            // however many times — from an instance that was.  Outranks one
            // that does not, between instances this client owns.  An adopted
            // copy that received none of its owner's state does not carry it,
            // and the copy re-created from it is born.
            public bool Inherited;
            // Whether the world this instance is — the lineage, not the
            // object — has had its contents generated on this client: born
            // once, and carried to every copy re-created from it, so a copy
            // of a world that was born is not born again and a copy of one
            // that never was (a spawn the room refused before its first
            // frame) is born in its place.
            public bool Born;
            // When this client, not owning the instance, saw its owner change
            // — the reassignment a host migration performs on every peer.
            // Zero when it never has.
            public long ReassignedAtMillis;
            // When the next re-creation of this instance may be attempted.
            // Zero means now; carried to the copy an attempt spawns, so a copy
            // the room refuses in turn waits out the interval booked for the
            // attempt that spawned it rather than starting a clock of its own.
            public long NextRecreateAttemptMillis;
            // The interval the next attempt books: the first one at the
            // spawn, doubling with each attempt on the same lineage up to the
            // ceiling, and carried to the copy with the booking.
            public long RecreateIntervalMillis;
        }

        /// <summary>
        /// How long a peer waits, after an inherited instance's owner changed
        /// under it, before concluding that the owner who has meanwhile spawned
        /// a world of its own never held this one — and dropping it locally.
        /// A migration that reaches the peer takes one frame plus a round trip;
        /// a re-creation the new host keeps retrying at the room's ceiling can
        /// take longer, and dropping early costs nothing but a frame of no
        /// world, since the world the new host keeps or re-creates arrives with
        /// its state.
        /// </summary>
        internal const long ShadowedOrphanGraceMillis = 5000L;

        /// <summary>
        /// How long after a spawn the first re-creation attempt may be made,
        /// in milliseconds — whether the spawn was refused by the room or the
        /// attempt failed here.  A room at its object ceiling frees a slot when
        /// anything despawns; asking after an interval finds it without
        /// spending a spawn and a full flush of the world's state, for an
        /// object the room will refuse, on every round trip in between.  Each
        /// further attempt on the same lineage waits twice as long as the one
        /// before, up to <see cref="RecreateRetryMaxMillis"/>: a room that
        /// stays full for minutes is asked a few times a minute, not sixty.
        /// </summary>
        internal const long RecreateRetryMillis = 1000L;

        /// <summary>
        /// The longest interval between two re-creation attempts of one
        /// lineage, in milliseconds.  A slot freed while the world waits is
        /// found within this; a world that already is not there can wait it.
        /// </summary>
        internal const long RecreateRetryMaxMillis = 8000L;

        private readonly Dictionary<string, List<Entry>> _byKey =
            new Dictionary<string, List<Entry>>(StringComparer.Ordinal);

        // The object id last announced for a key.  Kept apart from the entries
        // because an announcement outlives the instance it named: a key whose
        // answer moves from one instance to another must announce the second,
        // and a key emptied by a despawn forgets its last answer so the next
        // instance is announced whatever it is called.
        private readonly Dictionary<string, ulong> _announced =
            new Dictionary<string, ulong>(StringComparer.Ordinal);

        /// <summary>
        /// Record a live instance under its key.  <paramref name="createdBySelf"/>
        /// is whether this client spawned it — read as <c>IsOwner</c> at the
        /// spawn, which is true for a local spawn and false for one received
        /// from the wire, whose owner is always the sender.
        /// </summary>
        /// <remarks>
        /// An instance already registered under the same id is REPLACED: the
        /// spawn registry evicts the previous holder of a colliding id rather
        /// than refusing the new one, so the newer instance is the one the
        /// room routes to.  The evicted one is unspawned and leaves by its own
        /// despawn callback — except where the newer one's registration is
        /// then rolled back, when it outlives its entry still spawned.  Every
        /// question an instance asks about itself is keyed by reference, so
        /// one with no entry of its own acts on nothing.
        /// </remarks>
        /// <returns>False when the key or instance is null, or the instance is already registered under the key.</returns>
        public bool Register(string key, IWorldAuthorityInstance instance, bool createdBySelf)
        {
            if (string.IsNullOrEmpty(key) || instance == null) return false;

            if (!_byKey.TryGetValue(key, out var entries))
            {
                entries = new List<Entry>(2);
                _byKey[key] = entries;
            }
            else
            {
                if (IndexOf(entries, instance) >= 0) return false;
                int colliding = IndexOfId(entries, instance.ObjectId);
                if (colliding >= 0) entries.RemoveAt(colliding);
            }

            entries.Add(new Entry
            {
                Instance               = instance,
                CreatedBySelf          = createdBySelf,
                RecreateIntervalMillis = RecreateRetryMillis,
            });
            return true;
        }

        /// <summary>Forget an instance.  A no-op for one the key does not hold.</summary>
        public bool Unregister(string key, IWorldAuthorityInstance instance)
        {
            if (string.IsNullOrEmpty(key) || instance == null || !_byKey.TryGetValue(key, out var entries)) return false;
            int i = IndexOf(entries, instance);
            if (i < 0) return false;
            entries.RemoveAt(i);
            // A key with nothing left owes nothing: the next instance under
            // it is announced whatever it is called.
            if (entries.Count == 0) _announced.Remove(key);
            return true;
        }

        /// <summary>
        /// Whether an instance this client did not create holds the room's
        /// state, from what the wire delivered to it — the reading behind
        /// <see cref="IWorldAuthorityInstance.HoldsOwnersState"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A client that held the world as a peer — from its spawn, or from its
        /// own entry, which the owner answers by flushing every variable — has
        /// every value its owner sent it, and for a variable the owner never
        /// wrote, the prefab's seed, which is the room's value too.  One value
        /// delivered is therefore enough: it says the owner was writing while
        /// this client held the world, and none says the owner was gone before
        /// this client arrived.
        /// </para>
        /// <para>
        /// ⛔ Not for a world handed to this client before it held it — the
        /// promotion catch-up's replay of a world it never received.  Its only
        /// record of the room's state is the frames held for it while it was
        /// unknown, and the hold keeps the last ten seconds, sixty-four frames
        /// at most: a scalar the owner last changed before that, and a list
        /// whose last whole state went with it, stand at the prefab's seed.
        /// Kept and sent to every peer, they would overwrite the values each
        /// peer holds.  Such a world holds the room's state only when every
        /// variable received a whole value — the owner's flush for this
        /// client's own entry still inside the hold — and is born otherwise.
        /// </para>
        /// </remarks>
        /// <param name="declaresState">Whether the object declares any variable.</param>
        /// <param name="handedBeforeHeld">
        /// Whether the instance reached this client already owned by it — a
        /// wire spawn naming this client, which it did not make.
        /// </param>
        /// <param name="valuesDelivered">How many values the wire delivered to it this life.</param>
        /// <param name="everyVariableDelivered">
        /// Whether every variable of the object holds a whole value the wire
        /// delivered this life.
        /// </param>
        internal static bool HoldsTheRoomsState(
            bool declaresState, bool handedBeforeHeld, long valuesDelivered, bool everyVariableDelivered)
        {
            if (!declaresState) return true;
            return handedBeforeHeld ? everyVariableDelivered : valuesDelivered > 0;
        }

        /// <summary>
        /// Record that this client now owns an instance it did not create — the
        /// ownership reassignment a host migration performs.  The instance is
        /// kept or re-created either way, as the replay it is under requires; it
        /// carries the room's state — the precedence over a self-created
        /// instance, and a world that is not born again — only when it
        /// <see cref="IWorldAuthorityInstance.HoldsOwnersState">holds what its
        /// owner had</see>, read at the adoption.  Ignored for an instance this
        /// client created, whose ownership never moved.
        /// </summary>
        public bool MarkAdopted(string key, IWorldAuthorityInstance instance)
        {
            var entry = Find(key, instance);
            if (entry == null || entry.CreatedBySelf || entry.Adopted) return false;
            entry.Adopted   = true;
            // Read here and recorded, never asked again.  ⛔ The premise that
            // makes one reading sound is the owner gate on the receive path:
            // an object this client owns takes no inbound value (unless the
            // project turns reconcileOwnedObjects on), and the handover
            // records the new owner before it raises the change this call
            // answers — so nothing can reach the instance after this moment
            // that would change the answer.
            entry.Inherited = instance.HoldsOwnersState;
            return true;
        }

        /// <summary>
        /// Record that <paramref name="successor"/> — an instance this client
        /// spawned — is the copy a re-creation of <paramref name="source"/>
        /// produced.  The copy takes what the source had that must outlive it:
        /// its precedence over a self-created instance, when it carried the
        /// room's state; whether the world has been born, so a copy is born
        /// exactly when its source never was; and the schedule — the attempt
        /// already booked and the interval the next one takes — so a copy the
        /// room refuses waits that booking out rather than retrying on
        /// receipt, and the wait keeps widening down the chain.  Refused,
        /// naming the reason, when either is unknown to the key, when they are
        /// one instance, or when the copy was not created by this client.
        /// </summary>
        public SuccessorRecord MarkSuccessor(string key, IWorldAuthorityInstance source, IWorldAuthorityInstance successor)
        {
            var from = Find(key, source);
            var to   = Find(key, successor);
            if (from == null)                return SuccessorRecord.SourceUnknown;
            if (to == null)                  return SuccessorRecord.CopyUnknown;
            if (ReferenceEquals(from, to))   return SuccessorRecord.SameInstance;
            if (!to.CreatedBySelf)           return SuccessorRecord.CopyNotOfThisClientsMaking;
            to.Inherited                 |= from.Inherited;
            to.Born                      |= from.Born;
            to.NextRecreateAttemptMillis  = from.NextRecreateAttemptMillis;
            to.RecreateIntervalMillis     = from.RecreateIntervalMillis;
            return SuccessorRecord.Recorded;
        }

        /// <summary>
        /// Record that a re-creation of <paramref name="instance"/> was
        /// attempted at <paramref name="nowMillis"/>: the next one is due one
        /// interval later, whatever this attempt comes to — a spawn manager
        /// that is gone, a prefab id nobody holds, a copy the room refuses in
        /// turn — and the interval after that is twice as long, up to
        /// <see cref="RecreateRetryMaxMillis"/>.  Booked before the attempt
        /// is made, so no outcome can leave it unbooked.
        /// </summary>
        /// <returns>The interval booked, in milliseconds; zero when the key does not hold the instance.</returns>
        public long RecreateAttempted(string key, IWorldAuthorityInstance instance, long nowMillis)
        {
            var entry = Find(key, instance);
            if (entry == null) return 0L;
            long interval = entry.RecreateIntervalMillis;
            entry.NextRecreateAttemptMillis = nowMillis + interval;
            entry.RecreateIntervalMillis    = Math.Min(interval * 2L, RecreateRetryMaxMillis);
            return interval;
        }

        /// <summary>
        /// Whether <paramref name="instance"/> is to be born now: a world of
        /// this client's making that carries no state yet — neither inherited
        /// nor born, on this instance or on the one it was re-created from —
        /// on a frame on which it is the key's answer.  False for a world
        /// that is somebody else's, that arrived with its state, that was
        /// born already, or that is not the answer: a spawn the room refused
        /// before its first frame never answers and is not born, and the copy
        /// re-created from it is, on its first frame as the answer — as is the
        /// copy re-created from an adopted instance that had received none of
        /// its owner's state.
        /// </summary>
        /// <param name="key">The world key.</param>
        /// <param name="instance">The instance asking.</param>
        /// <param name="replayFollowsOwner">
        /// Whether the gateway said the room's replay follows an object's owner
        /// (<c>CapabilityFlags.ReplayFollowsOwner</c>).  Then an adopted
        /// instance is kept rather than copied, so one adopted with none of the
        /// room's state is the instance that is born — the world the copy would
        /// have been.
        /// </param>
        public bool ShouldBeBorn(string key, IWorldAuthorityInstance instance, bool replayFollowsOwner = false)
        {
            var entry = Find(key, instance);
            if (entry == null || entry.Inherited || entry.Born || !entry.Instance.IsOwnedBySelf)
                return false;
            if (!entry.CreatedBySelf && !(replayFollowsOwner && entry.Adopted))
                return false;
            return ReferenceEquals(ElectedEntry(key), entry);
        }

        /// <summary>
        /// Record that the world <paramref name="instance"/> is — the lineage,
        /// not the object — has been born: its contents generated on this
        /// client.  Carried to every copy re-created from it.  Ignored for an
        /// instance the key does not hold and for one born already.
        /// </summary>
        public bool MarkBorn(string key, IWorldAuthorityInstance instance)
        {
            var entry = Find(key, instance);
            if (entry == null || entry.Born) return false;
            entry.Born = true;
            return true;
        }

        /// <summary>
        /// Record that the room refused the spawn of an instance this client
        /// created for want of a slot: it exists here alone, stands aside from
        /// the election from now on, and is re-created on the same terms as an
        /// inherited one until the room takes a copy — waiting while the room
        /// holds a world of somebody else's, dropped beside a world of this
        /// client's own.  Ignored for an instance this client did not create.
        /// </summary>
        public bool MarkRefused(string key, IWorldAuthorityInstance instance)
        {
            var entry = Find(key, instance);
            if (entry == null || !entry.CreatedBySelf || entry.Refused) return false;
            entry.Refused = true;
            return true;
        }

        /// <summary>
        /// Whether the room holds the instance, as far as this registry knows:
        /// it is registered under the key and the room did not refuse it.
        /// False for a refused spawn — it exists here alone, and its removal is
        /// nobody else's business — and for an instance the key does not hold,
        /// which is what a colliding id's replacement leaves behind when its
        /// own registration is rolled back: not the one the room routes to,
        /// with nothing to tell the room.
        /// </summary>
        public bool IsHeldForTheRoom(string key, IWorldAuthorityInstance instance)
        {
            var entry = Find(key, instance);
            return entry != null && !entry.Refused;
        }

        /// <summary>
        /// Record that an instance this client does not own changed owner
        /// under it — the reassignment a host migration performs on every
        /// peer — at <paramref name="nowMillis"/>.  Every change is recorded,
        /// the latest in place of the one before: the grace a peer gives the
        /// new owner is about that owner's migration.
        /// </summary>
        /// <remarks>
        /// A world kept across migrations — the room's replay follows its
        /// owner — changes owner once per host that leaves, and a grace
        /// measured from the first change would already be spent at the
        /// second: a peer would drop its copy the moment the next host spawned
        /// a world of its own while its catch-up was still on the way, and a
        /// catch-up that then carried the room's state would leave that peer
        /// with no world at all.  Against an older gateway the new owner
        /// re-creates the instance rather than re-owning it, so there too the
        /// latest change is the one the grace belongs to.
        /// </remarks>
        public bool MarkReassigned(string key, IWorldAuthorityInstance instance, long nowMillis)
        {
            var entry = Find(key, instance);
            if (entry == null || entry.CreatedBySelf) return false;
            entry.ReassignedAtMillis = nowMillis == 0L ? 1L : nowMillis;
            return true;
        }

        /// <summary>
        /// Record that an instance has had its first frame — every component's
        /// spawn callback has run, so a sibling that subscribed from its own
        /// is listening — so it may now answer a key, be born, and be
        /// announced, in that order.
        /// </summary>
        public bool MarkReady(string key, IWorldAuthorityInstance instance)
        {
            var entry = Find(key, instance);
            if (entry == null || entry.Ready) return false;
            entry.Ready = true;
            return true;
        }

        /// <summary>
        /// Whether any live instance is registered under the key — ready or
        /// not, refused or not: a spawner asks this before spawning, and a
        /// refused instance is one that is re-creating itself, not a gap to
        /// fill with another.
        /// </summary>
        public bool HasLive(string key)
        {
            if (string.IsNullOrEmpty(key) || !_byKey.TryGetValue(key, out var entries)) return false;
            PruneDead(entries);
            return entries.Count > 0;
        }

        /// <summary>
        /// The instance that answers the key: among the live, ready ones the
        /// room holds, the one with the smallest object id — unless that one
        /// is this client's, in which case the one of this client's that
        /// carries the room's state, if any, answers instead.  A copy another
        /// client owns that has received nothing from that owner since it
        /// became that owner's here stands aside for one of the same owner's
        /// that has.  Null when none is live
        /// and ready, or the only one is a spawn the room refused.
        /// </summary>
        public IWorldAuthorityInstance Elected(string key)
        {
            var entry = ElectedEntry(key);
            return entry == null ? null : entry.Instance;
        }

        /// <summary>
        /// The elected instance if it differs from the one last announced for
        /// the key, recording it as announced; null otherwise.  A key whose
        /// answer has gone from an instance to none records that too, so the
        /// next instance is announced even if it carries the same id — which
        /// a pooled object can.
        /// </summary>
        public IWorldAuthorityInstance TakeAnnouncement(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var elected = ElectedEntry(key);
            ulong now = elected == null ? 0UL : elected.Instance.ObjectId;
            _announced.TryGetValue(key, out ulong last);
            if (now == last) return null;
            _announced[key] = now;
            return elected == null ? null : elected.Instance;
        }

        /// <summary>
        /// What <paramref name="instance"/> should do about itself at
        /// <paramref name="nowMillis"/>.  An instance this client owns — and
        /// holds an entry for — may be told to remove or re-create itself, or,
        /// refused by the room and outranked by another instance of this
        /// client's, to drop itself locally; one it does not own may be told to
        /// drop its local copy, and nothing else.  A re-creation is told only
        /// when it is due: never before the interval booked by the last attempt
        /// has passed, and never while the room holds a world of somebody
        /// else's.
        /// </summary>
        /// <param name="key">The world key.</param>
        /// <param name="instance">The instance asking.</param>
        /// <param name="nowMillis">The clock the re-creation schedule is read against.</param>
        /// <param name="replayFollowsOwner">
        /// Whether the gateway said the room's replay follows an object's owner
        /// (<c>CapabilityFlags.ReplayFollowsOwner</c>).  Then an adopted
        /// instance that is the key's answer is kept: the room replays it under
        /// this client already, and a copy would only change the world's
        /// identity — its object id, and every reference a game held to it.
        /// A spawn the room refused is re-created either way; that is about a
        /// slot, not about who the replay names.
        /// </param>
        public WorldInstanceVerdict VerdictFor(
            string key, IWorldAuthorityInstance instance, long nowMillis, bool replayFollowsOwner = false)
        {
            if (string.IsNullOrEmpty(key) || instance == null || !_byKey.TryGetValue(key, out var entries))
                return WorldInstanceVerdict.Keep;
            PruneDead(entries);

            var self = Find(key, instance);
            if (self == null) return WorldInstanceVerdict.Keep;
            if (!self.Instance.IsOwnedBySelf) return ShadowedOrphanVerdict(entries, self, nowMillis);

            // The election over every live instance, a refused one of this
            // client's competing among its own: it carries state too, and an
            // inherited one the room refused outranks a fresh one the room took.
            var winner = Winner(entries, readyOnly: false, refusedOwnCompete: true);

            // A spawn the room refused for want of a slot exists here alone:
            // outside the shared election, re-created on the same terms as an
            // inherited one until the room takes a copy, and torn down here
            // alone.  Not while the room holds a world of somebody else's — a
            // copy could only rival it, attempt after attempt, for as long as
            // the room is full — so it waits, neither re-creating nor
            // answering; should that world go, the wait ends and the room gets
            // this one's state.  Beside a world of this client's own it is
            // dropped instead: one the room holds is the room's world already,
            // and a refused one that outranks it carries the state a copy
            // would — the one carrying the room's state re-creates, whichever
            // was booked earlier, and the rest go.  Nothing is left waiting on
            // a world its own client keeps.
            if (self.Refused)
            {
                if (winner == null) winner = BestRefusedOwn(entries);
                if (!ReferenceEquals(winner, self))
                    return winner.Instance.IsOwnedBySelf ? WorldInstanceVerdict.DropLocally : WorldInstanceVerdict.Keep;
                return nowMillis < self.NextRecreateAttemptMillis ? WorldInstanceVerdict.Keep : WorldInstanceVerdict.RecreateRefused;
            }

            // The election, then the migration: an instance that is not the
            // key's — the room holds another that wins, or this client owns
            // a better one — is removed, inherited or not, because its owner
            // is the one party that can remove it and whatever it carried is
            // outranked.  The inherited instance that IS the key's is kept
            // where the room's replay follows the owner — it already names
            // this client — and re-created whatever its id where it does not,
            // so the replay names a host who is present.
            if (!ReferenceEquals(winner, self)) return WorldInstanceVerdict.DespawnSelf;
            if (!self.Adopted || replayFollowsOwner) return WorldInstanceVerdict.Keep;
            return nowMillis < self.NextRecreateAttemptMillis ? WorldInstanceVerdict.Keep : WorldInstanceVerdict.Recreate;
        }

        /// <summary>Forget everything: play-mode entry with domain reload off.</summary>
        public void Clear()
        {
            _byKey.Clear();
            _announced.Clear();
        }

        // ── Internals ──────────────────────────────────────────────────────────

        // A peer's copy of an inherited world whose owner — the new host — has
        // been running a world spawned by itself beside it for longer than a
        // migration takes.  That host never held the inherited one (it joined
        // after the replay had passed, or its replay was cut short) and so
        // will never remove it; every other client keeps electing the orphan,
        // which nobody can write.  The same shape is a despawn lost on the way
        // to this peer, and the same answer heals it.
        private static WorldInstanceVerdict ShadowedOrphanVerdict(List<Entry> entries, Entry self, long nowMillis)
        {
            if (self.ReassignedAtMillis == 0L) return WorldInstanceVerdict.Keep;
            if (nowMillis - self.ReassignedAtMillis < ShadowedOrphanGraceMillis) return WorldInstanceVerdict.Keep;
            string owner = self.Instance.OwnerPlayerId;
            if (string.IsNullOrEmpty(owner)) return WorldInstanceVerdict.Keep;
            for (int i = 0; i < entries.Count; i++)
            {
                var other = entries[i];
                if (ReferenceEquals(other, self) || other.ReassignedAtMillis != 0L) continue;
                if (other.Instance.IsOwnedBySelf) continue;
                if (string.Equals(other.Instance.OwnerPlayerId, owner, StringComparison.Ordinal))
                    return WorldInstanceVerdict.DropLocally;
            }
            return WorldInstanceVerdict.Keep;
        }

        private Entry ElectedEntry(string key)
        {
            if (string.IsNullOrEmpty(key) || !_byKey.TryGetValue(key, out var entries)) return null;
            PruneDead(entries);
            var elected = Winner(entries, readyOnly: true, refusedOwnCompete: false);
            // An instance this client owns that the election the verdict holds
            // — over EVERY live instance — does not name is one this client
            // removes on its next verdict, the same frame.  Handing it out, or
            // announcing it, is handing out an object that is gone by the end
            // of the frame; the answer is nothing until the winner has had its
            // first frame, which is the null a migration passes through anyway.
            if (elected != null && elected.Instance.IsOwnedBySelf
                && !ReferenceEquals(Winner(entries, readyOnly: false, refusedOwnCompete: true), elected))
                return null;
            return elected;
        }

        // The election, in two stages.  First the one every client can hold on
        // the same facts: the smallest object id among the instances the room
        // holds — a spawn the room refused is held here alone and stands aside.
        // That answer is final when it names an instance somebody else owns:
        // this client knows nothing that owner does not, and a rule that let
        // its own instance keep beside a foreign one would leave two owners
        // each keeping one for ever.  When it names an instance of this
        // client's own, this client chooses among its own — the one carrying
        // the room's state first, the smallest id between equals — and its
        // removal of the others reaches every peer within a round trip.  For
        // the verdict, a refused instance of this client's competes in that
        // choice: it carries state, and an inherited one the room refused
        // outranks a fresh one the room took, which then yields the slot the
        // re-creation needs.  For the answer game code is given it never does:
        // the room does not hold it.  Readiness gates that answer, never who
        // a rival is: a copy that arrived this frame is a rival this frame.
        private static Entry Winner(List<Entry> entries, bool readyOnly, bool refusedOwnCompete)
        {
            Entry shared = null;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Refused || (readyOnly && !e.Ready)) continue;
                if (readyOnly && StandsAside(entries, e)) continue;
                if (shared == null || e.Instance.ObjectId < shared.Instance.ObjectId) shared = e;
            }
            if (shared == null || !shared.Instance.IsOwnedBySelf) return shared;

            Entry own = null;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (!e.Instance.IsOwnedBySelf || (readyOnly && !e.Ready)) continue;
                if (e.Refused && !refusedOwnCompete) continue;
                if (own == null || Outranks(e, own)) own = e;
            }
            return own;
        }

        // An instance another client owns that has heard nothing from that
        // owner since it became that owner's here, beside one of the same
        // owner's that has:
        // the owner holds the one it writes.  An owner flushes every variable
        // of every object it holds when a player joins, so a copy it never
        // wrote to a joiner is one it does not hold — a world the promotion
        // catch-up handed it and never delivered, which the room's replay
        // carries, frozen, to every player who joins after.  Elected by id it
        // would answer about half the time, and nobody would ever write it.
        // ⛔ Read for the ANSWER alone — the instance game code is given — and
        // never for an owner's verdict: an owner removes or keeps its own on
        // facts every client shares, and whether a copy has heard from its
        // owner is this client's alone.  ⚠️ The compound case is a stated
        // limit, not a repair: a frozen copy beside two hosts' raced worlds
        // wins a verdict by id on the client that holds it, while its owner,
        // not holding it, decides between the other two — so both can go.
        // Counting the stand-aside in the verdict ends the same way once the
        // owner's written copy is gone and the frozen one stops standing
        // aside; only the owner learning of the frozen copy would close it.
        // The rival must be ready, so a copy does not stand aside for one that
        // cannot answer yet.  An instance that declares no variable cannot say,
        // and neither stands aside nor makes another do so.
        private static bool StandsAside(List<Entry> entries, Entry candidate)
        {
            if (candidate.Instance.IsOwnedBySelf || candidate.Instance.ReceivedValues != false) return false;
            string owner = candidate.Instance.OwnerPlayerId;
            if (string.IsNullOrEmpty(owner)) return false;
            for (int i = 0; i < entries.Count; i++)
            {
                var other = entries[i];
                if (ReferenceEquals(other, candidate) || other.Refused || !other.Ready) continue;
                if (other.Instance.IsOwnedBySelf || other.Instance.ReceivedValues != true) continue;
                if (string.Equals(other.Instance.OwnerPlayerId, owner, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        // When nothing the room holds is live, the refused instances of this
        // client's own settle among themselves on the same terms.
        private static Entry BestRefusedOwn(List<Entry> entries)
        {
            Entry best = null;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (!e.Refused || !e.Instance.IsOwnedBySelf) continue;
                if (best == null || Outranks(e, best)) best = e;
            }
            return best;
        }

        // Between two instances this client owns: the room's state first, then
        // the one the room holds over one it refused, then one already born
        // here over one that is not — its contents are the ones this client
        // generated, and electing the other would be a second birth that
        // throws them away (a world handed over by the promotion catch-up,
        // carrying nothing, beside the one the new host spawned while it
        // waited) — the smaller id between equals.
        private static bool Outranks(Entry candidate, Entry incumbent)
        {
            if (candidate.Inherited != incumbent.Inherited) return candidate.Inherited;
            if (candidate.Refused != incumbent.Refused) return !candidate.Refused;
            if (candidate.Born != incumbent.Born) return candidate.Born;
            return candidate.Instance.ObjectId < incumbent.Instance.ObjectId;
        }

        private Entry Find(string key, IWorldAuthorityInstance instance)
        {
            if (string.IsNullOrEmpty(key) || instance == null || !_byKey.TryGetValue(key, out var entries)) return null;
            int i = IndexOf(entries, instance);
            return i < 0 ? null : entries[i];
        }

        private static int IndexOf(List<Entry> entries, IWorldAuthorityInstance instance)
        {
            for (int i = 0; i < entries.Count; i++)
                if (ReferenceEquals(entries[i].Instance, instance)) return i;
            return -1;
        }

        private static int IndexOfId(List<Entry> entries, ulong objectId)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Instance.ObjectId == objectId) return i;
            return -1;
        }

        // An instance destroyed without a despawn callback — a scene load, a
        // user's Object.Destroy — is evicted the next time its key is read.
        private static void PruneDead(List<Entry> entries)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
                if (!entries[i].Instance.IsLive) entries.RemoveAt(i);
        }
    }
}
