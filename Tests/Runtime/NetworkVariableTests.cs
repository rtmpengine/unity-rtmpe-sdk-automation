// RTMPE SDK — Tests/Runtime/NetworkVariableTests.cs
//
// NUnit Edit-Mode tests for NetworkVariable<T> and NetworkVariableTypes.
//
// Test philosophy:
//   • Each test targets one observable behaviour (one reason to fail).
//   • Shared setup: a NetworkManager singleton + one GameObject with a
//     minimal stub NetworkBehaviour.  Both are destroyed in TearDown.
//   • Round-trip serialisation tests use a MemoryStream + BinaryWriter/Reader
//     to verify the byte-layer without a real network connection.
//   • P-1 fix: concrete subtypes (NetworkVariableInt, etc.) are used throughout.
//     The plan's "new NetworkVariable<int>()" is invalid (abstract class).
//   • P-2 fix: Initialize called with correct types (ulong, string).
//   • P-3 fix: StubNetworkBehaviour defined locally (ConcreteNetworkBehaviour
//     in NetworkBehaviourTests.cs is private sealed).
//   • P-4 fix: SetUp creates NetworkManager so NetworkBehaviour.IsOwner works.
//   • P-5 fix: class placed in namespace RTMPE.Tests.
//   • P-8 fix: round-trip serialisation tests added for all concrete types.
//
// Fixtures:
//   Category "NetworkVariable" — value semantics, dirty tracking, events.
//   Category "NetworkVariableSerialization" — BinaryWriter/Reader round-trips.

using System.IO;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    // ── Minimal stub — defined locally because ConcreteNetworkBehaviour ──────
    // in NetworkBehaviourTests is private sealed and not accessible here.
    // Only needs a no-op body; NetworkBehaviour is abstract with no abstract methods.
    // internal (not private) so both test fixtures in this file can reference it.
    // The name StubNB is unique in the RTMPE.Tests assembly (assembly-internal only).
    internal sealed class StubNB : NetworkBehaviour { }

    // ═════════════════════════════════════════════════════════════════════════
    // VALUE SEMANTICS, DIRTY TRACKING, EVENTS
    // ═════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("NetworkVariable")]
    public class NetworkVariableTests
    {
        // ── Shared state ──────────────────────────────────────────────────────

        private GameObject     _nmGo;
        private NetworkManager _manager;
        private GameObject     _ownerGo;
        private StubNB         _owner;

        // ── SetUp / TearDown ──────────────────────────────────────────────────

        [SetUp]
        public void SetUp()
        {
            // NetworkManager singleton required by NetworkBehaviour.IsOwner.
            _nmGo    = new GameObject("TestNetworkManager");
            _manager = _nmGo.AddComponent<NetworkManager>();

            // ⚠️ Two gates gate a write, and opening one arms the other. The
            // setter returns on `!Owner.IsSpawned`, and then again on
            // `WriteWouldNotBeSent` — which is `IsSpawned && IsOwnedByAnotherPlayer`,
            // so it is inert until the object is spawned. `IsOwnedByAnotherPlayer`
            // compares the owner id against the seat this client believes it
            // holds, and that seat is empty until somebody names it. An owner
            // spawned without one is therefore SOMEBODY ELSE'S object, and every
            // assignment below is refused a second time.
            _manager.SetLocalPlayerStringId("test-owner");

            _ownerGo = new GameObject("OwnerObject");
            _owner   = _ownerGo.AddComponent<StubNB>();

            // P-2 fix: Initialize(ulong, string) — second arg is string.
            _owner.Initialize(1UL, "test-owner");
            _owner.SetSpawned(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_ownerGo != null) { Object.DestroyImmediate(_ownerGo); _ownerGo = null; }
            if (_nmGo    != null) { Object.DestroyImmediate(_nmGo);    _nmGo    = null; }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // Create a NetworkVariableInt with varid=0 and given initial value.
        private NetworkVariableInt  MakeInt(int initial = 0)
            => new NetworkVariableInt(_owner, "v0", initial);

        private NetworkVariableFloat MakeFloat(float initial = 0f)
            => new NetworkVariableFloat(_owner, "v0", initial);

        private NetworkVariableBool MakeBool(bool initial = false)
            => new NetworkVariableBool(_owner, "v0", initial);

        private NetworkVariableString MakeString(string initial = "")
            => new NetworkVariableString(_owner, "v0", initial);

        // ── 1: IsDirty false at construction ─────────────────────────────────

        [Test]
        [Description("A freshly created NetworkVariable is not dirty.")]
        public void IsDirty_AtConstruction_IsFalse()
        {
            var v = MakeInt(42);

            Assert.IsFalse(v.IsDirty);
        }

        // ── 2: VariableId is derived, never assigned ─────────────────────────

        [Test]
        [Description("VariableId is derived from the owner's concrete type and the member " +
                     "name, so one type and one name always give one id, and two members " +
                     "of a type never share one.")]
        public void VariableId_IsDerivedFromTheTypeAndTheMemberName()
        {
            // ⚠️ The two same-named variables are on two INSTANCES of the type,
            // not on one behaviour. Registering both on one behaviour is refused
            // by TrackVariable — which is this derivation's own guarantee, so
            // writing the case that way would throw before it could assert
            // anything. The property is about the TYPE and the name.
            var secondGo = new GameObject("SecondOwnerOfTheSameType");
            try
            {
                var second = secondGo.AddComponent<StubNB>();

                var health = new NetworkVariableInt(_owner, "_health", 0);
                var sameElsewhere = new NetworkVariableInt(second, "_health", 0);
                var ammo   = new NetworkVariableInt(_owner, "_ammo", 0);

                Assert.AreEqual(health.VariableId, sameElsewhere.VariableId,
                                "one type and one member name derive one id, with nothing recorded");
                Assert.AreNotEqual(health.VariableId, ammo.VariableId,
                                   "two members of one type must not address one identity");
            }
            finally
            {
                Object.DestroyImmediate(secondGo);
            }
        }

        // ── 3: Value initialised ──────────────────────────────────────────────

        [Test]
        [Description("Value property returns the initial value passed to the constructor.")]
        public void Value_Initial_MatchesConstructorArg()
        {
            var v = MakeInt(99);

            Assert.AreEqual(99, v.Value);
        }

        // ── 4: OnValueChanged fires on change ─────────────────────────────────

        [Test]
        [Description("OnValueChanged fires exactly once when Value is set to a new value.")]
        public void Value_SetNewValue_FiresOnValueChangedOnce()
        {
            var v = MakeInt(0);
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.Value = 10;

            Assert.AreEqual(1, callCount);
        }

        // ── 5: OnValueChanged does NOT fire for same value ────────────────────

        [Test]
        [Description("Setting Value to its current value is a no-op: no event, no dirty.")]
        public void Value_SetSameValue_DoesNotFireEvent()
        {
            var v = MakeInt(5);
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.Value = 5; // same

            Assert.AreEqual(0, callCount);
            Assert.IsFalse(v.IsDirty);
        }

        // ── 6: IsDirty set after change ───────────────────────────────────────

        [Test]
        [Description("Setting Value to a new value marks IsDirty = true.")]
        public void Value_SetNewValue_MarksDirty()
        {
            var v = MakeInt(0);

            v.Value = 1;

            Assert.IsTrue(v.IsDirty);
        }

        // ── 7: MarkClean clears IsDirty ───────────────────────────────────────

        [Test]
        [Description("MarkClean() resets IsDirty to false.")]
        public void MarkClean_AfterDirty_ResetsDirty()
        {
            var v = MakeInt(0);
            v.Value = 1;
            Assert.IsTrue(v.IsDirty, "Pre-condition: must be dirty after value change.");

            v.MarkClean();

            Assert.IsFalse(v.IsDirty);
        }

        // ── 8: Old and new values passed to callback ──────────────────────────

        [Test]
        [Description("Callback receives (oldValue, newValue) in the correct order.")]
        public void OnValueChanged_PassesOldAndNewValue()
        {
            var v = MakeInt(10);
            int receivedOld = -1, receivedNew = -1;
            v.OnValueChanged += (old, nw) => { receivedOld = old; receivedNew = nw; };

            v.Value = 20;

            Assert.AreEqual(10, receivedOld, "old value must be 10");
            Assert.AreEqual(20, receivedNew, "new value must be 20");
        }

        // ── 9: Value readable inside callback ─────────────────────────────────

        [Test]
        [Description("Value is already updated when OnValueChanged fires (callback can read Value).")]
        public void OnValueChanged_ValueUpdatedBeforeCallback()
        {
            var v = MakeInt(0);
            int valueInsideCallback = -1;
            v.OnValueChanged += (_, __) => valueInsideCallback = v.Value;

            v.Value = 42;

            Assert.AreEqual(42, valueInsideCallback);
        }

        // ── 10: SetValueWithoutNotify does not fire event ─────────────────────

        [Test]
        [Description("SetValueWithoutNotify updates Value without firing OnValueChanged.")]
        public void SetValueWithoutNotify_DoesNotFireEvent()
        {
            var v = MakeInt(0);
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.SetValueWithoutNotify(99);

            Assert.AreEqual(0, callCount, "No event must fire.");
            Assert.AreEqual(99, v.Value, "Value must be updated.");
        }

        // ── 11: SetValueWithoutNotify does not mark dirty ─────────────────────

        [Test]
        [Description("SetValueWithoutNotify does not set IsDirty (receive-side use case).")]
        public void SetValueWithoutNotify_DoesNotMarkDirty()
        {
            var v = MakeInt(0);

            v.SetValueWithoutNotify(50);

            Assert.IsFalse(v.IsDirty);
        }

        // ── 12: Multiple consecutive changes accumulate dirty ─────────────────

        [Test]
        [Description("Multiple changes keep IsDirty = true until MarkClean is called.")]
        public void Value_MultipleChanges_StaysDirtyUntilMarkClean()
        {
            var v = MakeInt(0);
            v.Value = 1;
            v.Value = 2;
            v.Value = 3;
            Assert.IsTrue(v.IsDirty, "Still dirty after 3 changes.");

            v.MarkClean();
            Assert.IsFalse(v.IsDirty);
        }

        // ── 13: a non-finite float never reaches the variable at all ─────────

        [Test]
        [Description("float.NaN is refused on write, so it never becomes the value and " +
                     "OnValueChanged never fires: this variable's own Deserialize refuses a " +
                     "non-finite float and keeps the receiver's prior value, so accepting it " +
                     "here would show it to the owner and to nobody else (SYNC-RD-10).")]
        public void NetworkVariableFloat_NonFinite_IsRefusedOnWrite()
        {
            var v = MakeFloat(3.5f);
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.Value = float.NaN;
            v.Value = float.PositiveInfinity;

            Assert.AreEqual(0, callCount, "a refused write must fire no change event.");
            Assert.AreEqual(3.5f, v.Value, "a refused write must keep the prior value.");
            Assert.IsFalse(v.IsDirty, "a refused write must not mark the variable dirty.");
        }

        // The equality semantics the previous version of this case was really
        // about are still true and still worth pinning — on a value the
        // variable accepts, which NaN no longer is.
        [Test]
        [Description("Assigning the same finite value twice fires once: the second write is " +
                     "absorbed by the equality check on IEquatable<float>.")]
        public void NetworkVariableFloat_SameValueTwice_FiresOnce()
        {
            var v = MakeFloat(0f);
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.Value = 1.5f;
            v.Value = 1.5f;

            Assert.AreEqual(1, callCount);
        }

        // ── 14: Bool change from false → true ─────────────────────────────────

        [Test]
        [Description("NetworkVariableBool fires event when toggling false → true.")]
        public void NetworkVariableBool_Toggle_FiresEvent()
        {
            var v = MakeBool(false);
            bool received = false;
            v.OnValueChanged += (_, nw) => received = nw;

            v.Value = true;

            Assert.IsTrue(received);
            Assert.AreEqual(true, v.Value);
        }

        // ── 15: String null normalised to empty ───────────────────────────────

        [Test]
        [Description("Assigning null to NetworkVariableString.Value stores \"\" (P-6 fix).")]
        public void NetworkVariableString_NullValue_TreatedAsEmptyString()
        {
            var v = MakeString("hello");

            v.Value = null; // should normalise to ""

            Assert.AreEqual(string.Empty, v.Value, "null must be stored as \"\".");
        }

        // ── 16: String event fires for null-as-empty change ───────────────────

        [Test]
        [Description("Assigning null fires OnValueChanged with newValue = \"\" when old != \"\".")]
        public void NetworkVariableString_NullChange_FiresEventWithEmptyString()
        {
            var v = MakeString("hello");
            string eventNewValue = "NOT_SET";
            v.OnValueChanged += (_, nw) => eventNewValue = nw;

            v.Value = null;

            Assert.AreEqual(string.Empty, eventNewValue);
        }

        // ── 17: String no-event when already empty and assigned null ──────────

        [Test]
        [Description("Assigning null when value is already \"\" is a no-op (no event).")]
        public void NetworkVariableString_NullOnEmpty_NoEvent()
        {
            var v = MakeString("");  // already empty
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.Value = null; // normalises to "" — same as current

            Assert.AreEqual(0, callCount);
        }

        // ── 17b: the two-dimensional pair, inside the engine that owns the types ──
        //
        // 🔑 Here as well as in the repository's shards because these two are the
        // only shipped variables whose value types Unity itself defines with
        // DIFFERENT member kinds — Vector2 carries fields and Vector2Int carries
        // properties — and a stand-in outside the editor can only claim that.

        [Test]
        [Description("A Vector2 survives its own wire form unchanged.")]
        public void NetworkVariableVector2_RoundTrips()
        {
            var sender = new NetworkVariableVector2(_owner, "v0", new Vector2(1.5f, -2.25f));
            var receiver = new NetworkVariableVector2(_owner, "v1");

            var stream = new System.IO.MemoryStream();
            sender.Serialize(new System.IO.BinaryWriter(stream));
            stream.Position = 0;
            receiver.Deserialize(new System.IO.BinaryReader(stream));

            Assert.AreEqual(new Vector2(1.5f, -2.25f), receiver.Value);
            Assert.AreEqual(8, stream.Length);
        }

        [Test]
        [Description("A Vector2Int is int32 on the wire, so a far tile comes back exact.")]
        public void NetworkVariableVector2Int_KeepsACoordinateAFloatWouldRound()
        {
            const int far = 20000001;                 // not representable as float32
            var sender = new NetworkVariableVector2Int(_owner, "v0", new Vector2Int(far, -far));
            var receiver = new NetworkVariableVector2Int(_owner, "v1");

            var stream = new System.IO.MemoryStream();
            sender.Serialize(new System.IO.BinaryWriter(stream));
            stream.Position = 0;
            receiver.Deserialize(new System.IO.BinaryReader(stream));

            Assert.AreEqual(far, receiver.Value.x);
            Assert.AreEqual(-far, receiver.Value.y);
            Assert.AreNotEqual(far, (int)(float)far);  // the premise of the case
        }

        [Test]
        [Description("A non-finite Vector2 component is refused on write, as Vector3's is.")]
        public void NetworkVariableVector2_RefusesANonFiniteComponent()
        {
            var v = new NetworkVariableVector2(_owner, "v0", new Vector2(1f, 2f));

            v.Value = new Vector2(float.NaN, 2f);

            Assert.AreEqual(new Vector2(1f, 2f), v.Value);
            Assert.IsFalse(v.CanSend(new Vector2(float.NaN, 2f)));
        }

        [Test]
        [Description("A Vector2Int has no value to refuse: every pair is a point.")]
        public void NetworkVariableVector2Int_AcceptsEveryPair()
        {
            var v = new NetworkVariableVector2Int(_owner, "v0", new Vector2Int(0, 0));

            v.Value = new Vector2Int(int.MinValue, int.MaxValue);

            Assert.AreEqual(new Vector2Int(int.MinValue, int.MaxValue), v.Value);
        }

        // ── 18: Vector3 no event for same value ───────────────────────────────

        [Test]
        [Description("Setting Vector3 to its current value does not fire the event.")]
        public void NetworkVariableVector3_SameValue_NoEvent()
        {
            var initial = new Vector3(1f, 2f, 3f);
            var v = new NetworkVariableVector3(_owner, "v0", initial);
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.Value = initial; // exact same struct

            Assert.AreEqual(0, callCount);
        }

        // ── 19: Vector3 event fires on change ─────────────────────────────────

        [Test]
        [Description("Changing Vector3 fires the event with correct old/new values.")]
        public void NetworkVariableVector3_Change_FiresEvent()
        {
            var oldVec = new Vector3(0f, 0f, 0f);
            var newVec = new Vector3(1f, 2f, 3f);
            var v = new NetworkVariableVector3(_owner, "v0", oldVec);
            Vector3 receivedOld = Vector3.one * -1f, receivedNew = Vector3.one * -1f;
            v.OnValueChanged += (old, nw) => { receivedOld = old; receivedNew = nw; };

            v.Value = newVec;

            Assert.AreEqual(oldVec, receivedOld);
            Assert.AreEqual(newVec, receivedNew);
        }

        // ── 20: NetworkVariableString multiple events ─────────────────────────

        [Test]
        [Description("NetworkVariableString fires correctly for two sequential changes.")]
        public void NetworkVariableString_TwoChanges_FiresTwice()
        {
            var v = MakeString("a");
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.Value = "b";
            v.Value = "c";

            Assert.AreEqual(2, callCount);
            Assert.AreEqual("c", v.Value);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SERIALISATION ROUND-TRIPS (BinaryWriter / BinaryReader)
    // ═════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("NetworkVariableSerialization")]
    public class NetworkVariableSerializationTests
    {
        private GameObject     _nmGo;
        private NetworkManager _manager;
        private GameObject     _ownerGo;
        private StubNB         _owner;
        // The receiving end. A round-trip has two of them, and giving both
        // variables the same behaviour is not a shortcut: ids dispatch inbound
        // updates, so TrackVariable throws on the second registration of an id —
        // out of the constructor, before the case reaches an assertion.
        private GameObject     _receiverGo;
        private StubNB         _receiver;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("TestNetworkManager");
            _manager = _nmGo.AddComponent<NetworkManager>();
            _manager.SetLocalPlayerStringId("test-owner");

            _ownerGo = new GameObject("OwnerObject");
            _owner   = _ownerGo.AddComponent<StubNB>();
            _owner.Initialize(1UL, "test-owner");
            _owner.SetSpawned(true);

            _receiverGo = new GameObject("ReceiverObject");
            _receiver   = _receiverGo.AddComponent<StubNB>();
            _receiver.Initialize(2UL, "test-owner");
            _receiver.SetSpawned(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_receiverGo != null) { Object.DestroyImmediate(_receiverGo); _receiverGo = null; }
            if (_ownerGo    != null) { Object.DestroyImmediate(_ownerGo);    _ownerGo    = null; }
            if (_nmGo       != null) { Object.DestroyImmediate(_nmGo);       _nmGo       = null; }
        }

        // ── Helper: serialize v, create a new instance, deserialize into it ──

        private static void RoundTrip<TVar>(TVar src, TVar dst)
            where TVar : NetworkVariableBase
        {
            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            src.Serialize(writer);
            writer.Flush();

            ms.Position = 0;
            using var reader = new BinaryReader(ms);
            dst.Deserialize(reader);
        }

        // ── 1: int round-trip ─────────────────────────────────────────────────

        [Test]
        [Description("NetworkVariableInt: Serialize then Deserialize recovers the original value.")]
        public void NetworkVariableInt_RoundTrip()
        {
            var src = new NetworkVariableInt(_owner, "v0", -12345678);
            var dst = new NetworkVariableInt(_receiver, "v0", 0);

            RoundTrip(src, dst);

            Assert.AreEqual(-12345678, dst.Value);
        }

        // ── 2: an inbound update notifies, and does not mark dirty ────────────

        [Test]
        [Description("Deserialize raises OnValueChanged on the receiving side and does not mark dirty.")]
        public void NetworkVariableInt_Deserialize_FiresEventWithoutDirtying()
        {
            var src = new NetworkVariableInt(_owner, "v0", 42);
            var dst = new NetworkVariableInt(_receiver, "v0", 0);
            int eventCount = 0;
            int seenOld = -1, seenNew = -1;
            dst.OnValueChanged += (o, n) => { eventCount++; seenOld = o; seenNew = n; };

            RoundTrip(src, dst);

            // The replication callback exists for the receiving side, and until
            // v8 it fired on no receiving client: every concrete Deserialize
            // routed through the PUBLIC SetValueWithoutNotify, whose contract is
            // "apply without echoing a callback" — right for the integrator it
            // was written for, exactly wrong for the wire.
            Assert.AreEqual(1, eventCount, "An inbound update must notify subscribers.");
            Assert.AreEqual(0,  seenOld,   "The callback must carry the prior value.");
            Assert.AreEqual(42, seenNew,   "The callback must carry the new value.");

            // Unchanged, and it is the half a naive repair breaks: a replica
            // that marked itself dirty would re-publish what it was just told
            // and send the owner its own value back.
            Assert.IsFalse(dst.IsDirty, "An inbound update must not mark the replica dirty.");
        }

        // ── 3: float round-trip ───────────────────────────────────────────────

        [Test]
        [Description("NetworkVariableFloat: round-trip preserves IEEE 754 bit pattern.")]
        public void NetworkVariableFloat_RoundTrip()
        {
            const float value = -3.1415927f;
            var src = new NetworkVariableFloat(_owner, "v0", value);
            var dst = new NetworkVariableFloat(_receiver, "v0", 0f);

            RoundTrip(src, dst);

            Assert.AreEqual(value, dst.Value, 0f, "Exact IEEE 754 equality expected after round-trip.");
        }

        // ── 4: bool true round-trip ───────────────────────────────────────────

        [Test]
        [Description("NetworkVariableBool(true): Serialize writes 0x01, Deserialize reads true.")]
        public void NetworkVariableBool_True_RoundTrip()
        {
            var src = new NetworkVariableBool(_owner, "v0", true);
            var dst = new NetworkVariableBool(_receiver, "v0", false);

            RoundTrip(src, dst);

            Assert.AreEqual(true, dst.Value);
        }

        // ── 5: bool false round-trip ──────────────────────────────────────────

        [Test]
        [Description("NetworkVariableBool(false): Serialize writes 0x00, Deserialize reads false.")]
        public void NetworkVariableBool_False_RoundTrip()
        {
            var src = new NetworkVariableBool(_owner, "v0", false);
            var dst = new NetworkVariableBool(_receiver, "v0", true);

            RoundTrip(src, dst);

            Assert.AreEqual(false, dst.Value);
        }

        // ── 6: Vector3 round-trip ─────────────────────────────────────────────

        [Test]
        [Description("NetworkVariableVector3: all three components survive the round-trip.")]
        public void NetworkVariableVector3_RoundTrip()
        {
            var value = new Vector3(1.5f, -2.25f, 3.75f);
            var src   = new NetworkVariableVector3(_owner, "v0", value);
            var dst   = new NetworkVariableVector3(_receiver, "v0", Vector3.zero);

            RoundTrip(src, dst);

            Assert.AreEqual(value.x, dst.Value.x, 0f, "X");
            Assert.AreEqual(value.y, dst.Value.y, 0f, "Y");
            Assert.AreEqual(value.z, dst.Value.z, 0f, "Z");
        }

        // ── 7: Quaternion round-trip ──────────────────────────────────────────

        [Test]
        [Description("NetworkVariableQuaternion: all four components (XYZW) survive the round-trip.")]
        public void NetworkVariableQuaternion_RoundTrip()
        {
            // A 90° rotation around Y: Quaternion.Euler(0, 90, 0).
            var value = Quaternion.Euler(0f, 90f, 0f);
            var src   = new NetworkVariableQuaternion(_owner, "v0", value);
            var dst   = new NetworkVariableQuaternion(_receiver, "v0", Quaternion.identity);

            RoundTrip(src, dst);

            Assert.AreEqual(value.x, dst.Value.x, 0.0001f, "X");
            Assert.AreEqual(value.y, dst.Value.y, 0.0001f, "Y");
            Assert.AreEqual(value.z, dst.Value.z, 0.0001f, "Z");
            Assert.AreEqual(value.w, dst.Value.w, 0.0001f, "W");
        }

        // ── 8: String round-trip ──────────────────────────────────────────────

        [Test]
        [Description("NetworkVariableString: Unicode + ASCII string survives the round-trip.")]
        public void NetworkVariableString_RoundTrip()
        {
            const string text = "Hello, 世界! \u0041\u0042";
            var src = new NetworkVariableString(_owner, "v0", text);
            var dst = new NetworkVariableString(_receiver, "v0", "");

            RoundTrip(src, dst);

            Assert.AreEqual(text, dst.Value);
        }

        // ── 9: String null source serialises as empty ─────────────────────────

        [Test]
        [Description("A NetworkVariableString with Value=\"\" serialises and deserialises as \"\".")]
        public void NetworkVariableString_Empty_RoundTrip()
        {
            var src = new NetworkVariableString(_owner, "v0", "");
            var dst = new NetworkVariableString(_receiver, "v0", "initial");

            RoundTrip(src, dst);

            Assert.AreEqual(string.Empty, dst.Value);
        }

        // ── 10: int serialises 4 bytes ────────────────────────────────────────

        [Test]
        [Description("NetworkVariableInt.Serialize writes exactly 4 bytes.")]
        public void NetworkVariableInt_Serialize_Writes4Bytes()
        {
            var v = new NetworkVariableInt(_owner, "v0", 1);

            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            v.Serialize(writer);
            writer.Flush();

            Assert.AreEqual(4L, ms.Length, "int must occupy exactly 4 bytes.");
        }

        // ── 11: bool serialises 1 byte ────────────────────────────────────────

        [Test]
        [Description("NetworkVariableBool.Serialize writes exactly 1 byte.")]
        public void NetworkVariableBool_Serialize_Writes1Byte()
        {
            var v = new NetworkVariableBool(_owner, "v0", true);

            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            v.Serialize(writer);
            writer.Flush();

            Assert.AreEqual(1L, ms.Length, "bool must occupy exactly 1 byte.");
        }

        // ── 12: Vector3 serialises 12 bytes ───────────────────────────────────

        [Test]
        [Description("NetworkVariableVector3.Serialize writes exactly 12 bytes (3 × float).")]
        public void NetworkVariableVector3_Serialize_Writes12Bytes()
        {
            var v = new NetworkVariableVector3(_owner, "v0", Vector3.one);

            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            v.Serialize(writer);
            writer.Flush();

            Assert.AreEqual(12L, ms.Length, "Vector3 must occupy exactly 12 bytes.");
        }

        // ── 13: Quaternion serialises 16 bytes ────────────────────────────────

        [Test]
        [Description("NetworkVariableQuaternion.Serialize writes exactly 16 bytes (4 × float).")]
        public void NetworkVariableQuaternion_Serialize_Writes16Bytes()
        {
            var v = new NetworkVariableQuaternion(_owner, "v0", Quaternion.identity);

            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            v.Serialize(writer);
            writer.Flush();

            Assert.AreEqual(16L, ms.Length, "Quaternion must occupy exactly 16 bytes.");
        }

        // ── Defensive deserialization — reject hostile / corrupt quaternions ──

        /// <summary>
        /// A unit quaternion (identity rotation) deserialises cleanly and the
        /// new value is observable via Value.
        /// </summary>
        [Test]
        [Description("NetworkVariableQuaternion.Deserialize accepts a well-formed unit quaternion.")]
        public void NetworkVariableQuaternion_Deserialize_AcceptsUnit()
        {
            var v   = new NetworkVariableQuaternion(_owner, "v0", Quaternion.identity);
            var bytes = SerializeQuaternion(0.0f, 0.0f, 0.0f, 1.0f); // identity

            using var ms     = new MemoryStream(bytes);
            using var reader = new BinaryReader(ms);
            v.Deserialize(reader);

            Assert.AreEqual(1.0f, v.Value.w, 1e-5f);
        }

        /// <summary>
        /// A non-unit quaternion outside the [0.9, 1.1] magnitude band is
        /// rejected and the prior value is preserved.  Without this guard a
        /// hostile client could inject arbitrary non-rotations that would
        /// later corrupt transform.rotation in consumer code.
        /// </summary>
        [Test]
        [Description("NetworkVariableQuaternion.Deserialize rejects non-unit quaternions; prior value preserved.")]
        public void NetworkVariableQuaternion_Deserialize_RejectsNonUnit()
        {
            var prior = new Quaternion(0f, 0f, 0f, 1f); // identity
            var v     = new NetworkVariableQuaternion(_owner, "v0", prior);

            // Magnitude squared = 4 — far outside [0.81, 1.21].
            var bytes = SerializeQuaternion(2.0f, 0.0f, 0.0f, 0.0f);

            using var ms     = new MemoryStream(bytes);
            using var reader = new BinaryReader(ms);
            v.Deserialize(reader);

            Assert.AreEqual(prior, v.Value,
                "non-unit quaternion must NOT be applied; prior value must be preserved");
        }

        /// <summary>
        /// NaN components are rejected.  A NaN quaternion would silently
        /// propagate into transform.rotation and break downstream physics /
        /// rendering.
        /// </summary>
        [Test]
        [Description("NetworkVariableQuaternion.Deserialize rejects NaN components.")]
        public void NetworkVariableQuaternion_Deserialize_RejectsNaN()
        {
            var prior = Quaternion.identity;
            var v     = new NetworkVariableQuaternion(_owner, "v0", prior);

            var bytes = SerializeQuaternion(float.NaN, 0f, 0f, 1f);

            using var ms     = new MemoryStream(bytes);
            using var reader = new BinaryReader(ms);
            v.Deserialize(reader);

            Assert.AreEqual(prior, v.Value, "NaN quaternion must NOT be applied");
        }

        /// <summary>
        /// Infinity components are rejected.
        /// </summary>
        [Test]
        [Description("NetworkVariableQuaternion.Deserialize rejects infinite components.")]
        public void NetworkVariableQuaternion_Deserialize_RejectsInfinity()
        {
            var prior = Quaternion.identity;
            var v     = new NetworkVariableQuaternion(_owner, "v0", prior);

            var bytes = SerializeQuaternion(0f, float.PositiveInfinity, 0f, 1f);

            using var ms     = new MemoryStream(bytes);
            using var reader = new BinaryReader(ms);
            v.Deserialize(reader);

            Assert.AreEqual(prior, v.Value, "Infinity quaternion must NOT be applied");
        }

        /// <summary>
        /// A quaternion with small accumulated rounding error (magnitude
        /// slightly off-unit but inside the tolerance band) is accepted and
        /// renormalised to exact unit length on read.
        /// </summary>
        [Test]
        [Description("NetworkVariableQuaternion.Deserialize renormalises near-unit quaternions.")]
        public void NetworkVariableQuaternion_Deserialize_RenormalisesNearUnit()
        {
            var v = new NetworkVariableQuaternion(_owner, "v0", Quaternion.identity);

            // magSq = 1.05² + 0² + 0² + 0² = 1.1025 — inside [0.81, 1.21]
            var bytes = SerializeQuaternion(1.05f, 0f, 0f, 0f);

            using var ms     = new MemoryStream(bytes);
            using var reader = new BinaryReader(ms);
            v.Deserialize(reader);

            float magSq = v.Value.x * v.Value.x + v.Value.y * v.Value.y
                        + v.Value.z * v.Value.z + v.Value.w * v.Value.w;
            Assert.AreEqual(1.0f, magSq, 1e-4f,
                "near-unit quaternion must be renormalised to exact unit length on deserialise");
        }

        // Helper: little-endian 16-byte quaternion payload (matches Serialize wire format).
        private static byte[] SerializeQuaternion(float x, float y, float z, float w)
        {
            using var ms     = new MemoryStream(16);
            using var writer = new BinaryWriter(ms);
            writer.Write(x);
            writer.Write(y);
            writer.Write(z);
            writer.Write(w);
            writer.Flush();
            return ms.ToArray();
        }

        // ── 14: SerializeWithId — wire layout for int ─────────────────────────

        [Test]
        [Description("SerializeWithId for NetworkVariableInt emits exactly 10 bytes: " +
                     "[var_id:4][value_len:2][int_bytes:4], and the var_id and value_len fields " +
                     "have the correct little-endian values.")]
        public void SerializeWithId_Int_WireLayout()
        {
            var v = new NetworkVariableInt(_owner, "v5", 0x01020304);

            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            v.SerializeWithId(writer);
            writer.Flush();

            Assert.AreEqual(10L, ms.Length,
                "Wire frame: 4 (var_id) + 2 (value_len) + 4 (int) = 10 bytes");

            ms.Position = 0;
            using var reader = new BinaryReader(ms);
            Assert.AreEqual(v.VariableId, reader.ReadUInt32(), "var_id must be the derived id");
            Assert.AreEqual((ushort)4, reader.ReadUInt16(), "value_len must be 4");
        }

        // ── 15: SerializeWithId — long string does not throw ──────────────────

        [Test]
        [Description("SerializeWithId must not throw NotSupportedException for a 300-character " +
                     "string. Regression: a fixed-size 64-byte MemoryStream was overflowed.")]
        public void SerializeWithId_LongString_DoesNotThrow()
        {
            var longValue = new string('A', 300);
            var v = new NetworkVariableString(_owner, "v0", longValue);

            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            Assert.DoesNotThrow(
                () => v.SerializeWithId(writer),
                "SerializeWithId must not throw for a 300-character string.");
        }

        // ── 16: SerializeWithId — long string round-trip ──────────────────────

        [Test]
        [Description("A 300-character string serialised with SerializeWithId round-trips " +
                     "correctly: wire format is [var_id:4][value_len:2][utf8_prefix:2][utf8_bytes:300] " +
                     "= 308 bytes total; Deserialize recovers the original string.")]
        public void SerializeWithId_LongString_RoundTrip()
        {
            var longValue = new string('Z', 300);
            var src = new NetworkVariableString(_owner, "v3", longValue);
            var dst = new NetworkVariableString(_receiver, "v3", "");

            // --- Serialise ---
            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            src.SerializeWithId(writer);
            writer.Flush();

            // Total expected: 4 (var_id) + 2 (value_len) + 2 (ushort prefix in Serialize) + 300 (ASCII) = 308
            Assert.AreEqual(308L, ms.Length, "Total wire size must be 308 bytes.");

            // --- Deserialise ---
            ms.Position = 0;
            using var reader = new BinaryReader(ms);

            uint   varId    = reader.ReadUInt32();
            ushort valueLen = reader.ReadUInt16();
            Assert.AreEqual(src.VariableId, varId, "var_id must be the derived id");
            Assert.AreEqual((ushort)302, valueLen, "value_len = 2 (ushort prefix) + 300 (content)");

            // Deserialize reads the inner format: [ushort len][utf8 bytes]
            byte[] valueBytes = reader.ReadBytes(valueLen);
            using var valueMs     = new MemoryStream(valueBytes);
            using var valueReader = new BinaryReader(valueMs);
            dst.Deserialize(valueReader);

            Assert.AreEqual(longValue, dst.Value, "Round-tripped value must match the original.");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // INBOUND TICK GATE  (Tier-1, finding #3 — uniform per-variable enforcement)
    // ═════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("NetworkVariable")]
    public class NetworkVariableInboundTickGateTests
    {
        private GameObject     _nmGo;
        private NetworkManager _manager;
        private GameObject     _ownerGo;
        private StubNB         _owner;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("NM_Gate");
            _manager = _nmGo.AddComponent<NetworkManager>();
            _manager.SetLocalPlayerStringId("owner");

            _ownerGo = new GameObject("Owner_Gate");
            _owner   = _ownerGo.AddComponent<StubNB>();
            _owner.Initialize(42UL, "owner");
            _owner.SetSpawned(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_ownerGo != null) { Object.DestroyImmediate(_ownerGo); _ownerGo = null; }
            if (_nmGo    != null) { Object.DestroyImmediate(_nmGo);    _nmGo    = null; }
        }

        private static byte[] BuildIntValueBytes(int value)
        {
            // Wire layout matches NetworkVariableInt.Serialize: 4 bytes LE int.
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(value);
            return ms.ToArray();
        }

        // Drive ApplyVariableUpdate the way HandleVariableUpdatePacket does.
        private void ApplyTick(NetworkVariableInt v, int newValue, uint tick)
        {
            byte[] bytes = BuildIntValueBytes(newValue);
            using var ms = new MemoryStream(bytes);
            using var br = new BinaryReader(ms);
            _owner.ApplyVariableUpdate(v.VariableId, br, valueLen: (ushort)bytes.Length,
                                       packetTick: tick, hasPacketTick: true);
        }

        [Test]
        [Description("First inbound update is always accepted regardless of tick value.")]
        public void FirstUpdate_Accepted()
        {
            var v = new NetworkVariableInt(_owner, "v1", initialValue: 0);

            ApplyTick(v, 7, tick: 100u);

            Assert.AreEqual(7, v.Value, "First inbound update must apply.");
        }

        [Test]
        [Description("A re-ordered older tick is rejected after a newer tick has been applied.")]
        public void OutOfOrderUpdate_Rejected()
        {
            var v = new NetworkVariableInt(_owner, "v1", initialValue: 0);

            ApplyTick(v, 99, tick: 200u);
            // Stale datagram arrives late — must NOT roll value back.
            ApplyTick(v, -1, tick: 199u);

            Assert.AreEqual(99, v.Value, "Older tick must be dropped by the gate.");
        }

        [Test]
        [Description("A duplicate tick (same as last applied) is rejected.")]
        public void DuplicateTick_Rejected()
        {
            var v = new NetworkVariableInt(_owner, "v1", initialValue: 0);

            ApplyTick(v, 1, tick: 5u);
            ApplyTick(v, 2, tick: 5u);

            Assert.AreEqual(1, v.Value, "Duplicate tick must not overwrite.");
        }

        [Test]
        [Description("Tick wrap (uint.MaxValue → 0) is treated as a forward step under modular arithmetic.")]
        public void WrapForward_Accepted()
        {
            var v = new NetworkVariableInt(_owner, "v1", initialValue: 0);

            ApplyTick(v, 10, tick: uint.MaxValue);
            ApplyTick(v, 11, tick: 0u);

            Assert.AreEqual(11, v.Value, "Forward across the uint wrap must apply.");
        }

        [Test]
        [Description("Per-variable gates are independent: gating one variable does not stall a sibling.")]
        public void IndependentVariables_DoNotShareGate()
        {
            var a = new NetworkVariableInt(_owner, "v1", initialValue: 0);
            var b = new NetworkVariableInt(_owner, "v2", initialValue: 0);

            ApplyTick(a, 1, tick: 100u);
            ApplyTick(b, 2, tick: 50u); // older tick than a — but gate is per-variable

            Assert.AreEqual(1, a.Value);
            Assert.AreEqual(2, b.Value, "A sibling variable must run its own gate, " +
                                        "not inherit the high-water tick of another.");
        }

        [Test]
        [Description("ResetInboundTickGate clears the watermark so a fresh session restarts cleanly.")]
        public void ResetGate_AllowsLowerTickAfterReset()
        {
            var v = new NetworkVariableInt(_owner, "v1", initialValue: 0);

            ApplyTick(v, 10, tick: 1_000u);
            v.ResetInboundTickGate();
            ApplyTick(v, 20, tick: 1u);

            Assert.AreEqual(20, v.Value, "After reset, any tick must be acceptable on the first call.");
        }
    }
}
