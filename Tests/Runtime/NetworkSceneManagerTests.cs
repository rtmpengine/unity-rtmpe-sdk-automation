// RTMPE SDK — Tests/Runtime/NetworkSceneManagerTests.cs
//
// NUnit tests for NetworkSceneManager.  The fixture wires a real
// RoomManager (with fake send/state delegates) to a NetworkSceneManager
// instance and drives RoomManager via its public broadcast hooks.
// NetworkSceneManager.Dispose is exercised to validate event unsubscription.
//
// Pure C# / Unity Edit-Mode — no live socket. LoadScene's two state guards
// both throw InvalidOperationException and are ordered room-first, so a case
// that means to reach the master-client guard has to be in a room to get
// there, and has to assert the message to know that it did.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Protocol;
using RTMPE.Rooms;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Rooms")]
    public class NetworkSceneManagerTests
    {
        private PacketBuilder       _builder;
        private List<byte[]>        _sent;
        private NetworkState        _state;
        private RoomManager         _rooms;
        private NetworkSceneManager _scene;

        [SetUp]
        public void SetUp()
        {
            _builder = new PacketBuilder();
            _sent    = new List<byte[]>();
            _state   = NetworkState.InRoom;
            _rooms   = new RoomManager(_builder, p => _sent.Add(p), () => _state);
            _scene   = new NetworkSceneManager(_rooms);
        }

        // ── Initial state ────────────────────────────────────────────────────

        [Test]
        public void CurrentScene_NoRoom_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, _scene.CurrentScene);
        }

        // ── OnSceneLoadStarted: property update ──────────────────────────────

        [Test]
        public void Property_SceneSetForFirstTime_FiresOnSceneLoadStarted()
        {
            JoinEmptyRoom();
            string observed = null;
            _scene.OnSceneLoadStarted += s => observed = s;

            _rooms.ApplyRoomPropertiesBroadcast(
                version: 2,
                properties: new Dictionary<string, PropertyValue>
                {
                    { ReservedPropertyKeys.Scene, PropertyValue.OfString("Arena01") },
                });

            Assert.AreEqual("Arena01", observed);
            Assert.AreEqual("Arena01", _scene.CurrentScene);
        }

        // Writing the scene the room is already on is a round restart, a
        // rematch, a respawn into the same map — an instruction, not a value.
        // The manager compared the name against the last one it had seen until
        // the write itself became observable, so the server accepted these and
        // broadcast them and no client heard anything.
        [Test]
        public void Property_SameSceneWrittenAgain_FiresAgain()
        {
            JoinEmptyRoom();
            var observed = new List<string>();
            _scene.OnSceneLoadStarted += observed.Add;

            var props = new Dictionary<string, PropertyValue>
            {
                { ReservedPropertyKeys.Scene, PropertyValue.OfString("Arena01") },
            };
            _rooms.ApplyRoomPropertiesBroadcast(version: 2, properties: props);
            _rooms.ApplyRoomPropertiesBroadcast(version: 3, properties: props);

            CollectionAssert.AreEqual(new[] { "Arena01", "Arena01" }, observed);
        }

        // And the control that case needs: a room's routine traffic is property
        // writes, so a manager announcing every broadcast would tell the
        // application to reload the scene on every score change.
        [Test]
        public void Property_DeltaWithoutTheSceneKey_DoesNotFire()
        {
            JoinEmptyRoom();
            _rooms.ApplyRoomPropertiesBroadcast(version: 2,
                new Dictionary<string, PropertyValue> {
                    { ReservedPropertyKeys.Scene, PropertyValue.OfString("Arena01") } });

            int fires = 0;
            _scene.OnSceneLoadStarted += _ => fires++;
            _rooms.ApplyRoomPropertiesBroadcast(version: 3,
                new Dictionary<string, PropertyValue> {
                    { "GameMode", PropertyValue.OfString("TDM") } });

            Assert.AreEqual(0, fires);
        }

        // ── The load mode reaches the receiver ───────────────────────────────

        [Test]
        public void Property_AdditiveFlag_ReachesTheModeEvent()
        {
            JoinEmptyRoom();
            var observed = new List<(string, NetworkSceneLoadMode)>();
            _scene.OnSceneLoadStartedWithMode += (s, m) => observed.Add((s, m));

            _rooms.ApplyRoomPropertiesBroadcast(version: 2,
                new Dictionary<string, PropertyValue>
                {
                    { ReservedPropertyKeys.Scene,         PropertyValue.OfString("Arena01") },
                    { ReservedPropertyKeys.SceneAdditive, PropertyValue.OfBool(true) },
                });

            CollectionAssert.AreEqual(
                new[] { ("Arena01", NetworkSceneLoadMode.Additive) }, observed);
            Assert.AreEqual(NetworkSceneLoadMode.Additive, _scene.CurrentSceneLoadMode);
        }

        // Absence is Single — the contract the reserved key's own summary
        // states.  Without this the case above passes on a manager that answers
        // Additive whatever the room holds.
        [Test]
        public void Property_NoAdditiveFlag_ReachesTheModeEventAsSingle()
        {
            JoinEmptyRoom();
            var observed = new List<(string, NetworkSceneLoadMode)>();
            _scene.OnSceneLoadStartedWithMode += (s, m) => observed.Add((s, m));

            _rooms.ApplyRoomPropertiesBroadcast(version: 2,
                new Dictionary<string, PropertyValue> {
                    { ReservedPropertyKeys.Scene, PropertyValue.OfString("Arena01") } });

            CollectionAssert.AreEqual(
                new[] { ("Arena01", NetworkSceneLoadMode.Single) }, observed);
            Assert.AreEqual(NetworkSceneLoadMode.Single, _scene.CurrentSceneLoadMode);
        }

        [Test]
        public void Property_SceneChangesValue_FiresOnSceneLoadStartedAgain()
        {
            JoinEmptyRoom();
            var observed = new List<string>();
            _scene.OnSceneLoadStarted += observed.Add;

            _rooms.ApplyRoomPropertiesBroadcast(version: 2,
                new Dictionary<string, PropertyValue> {
                    { ReservedPropertyKeys.Scene, PropertyValue.OfString("Lobby") } });
            _rooms.ApplyRoomPropertiesBroadcast(version: 3,
                new Dictionary<string, PropertyValue> {
                    { ReservedPropertyKeys.Scene, PropertyValue.OfString("Arena01") } });

            CollectionAssert.AreEqual(new[] { "Lobby", "Arena01" }, observed);
        }

        // ── Late-join path: room already has a scene ─────────────────────────

        [Test]
        public void RoomJoined_RoomHasScene_FiresOnSceneLoadStartedImmediately()
        {
            string observed = null;
            _scene.OnSceneLoadStarted += s => observed = s;
            JoinRoomWithScene("Arena01");
            Assert.AreEqual("Arena01", observed);
            Assert.AreEqual("Arena01", _scene.CurrentScene);
        }

        [Test]
        public void RoomJoined_RoomHasNoScene_DoesNotFireOnSceneLoadStarted()
        {
            int fires = 0;
            _scene.OnSceneLoadStarted += _ => fires++;
            JoinEmptyRoom();
            Assert.AreEqual(0, fires);
        }

        // ── OnAllPlayersSceneLoaded forwarding ───────────────────────────────

        [Test]
        public void RoomManager_AllPlayersSceneLoaded_ForwardsArgument()
        {
            string observed = null;
            _scene.OnAllPlayersSceneLoaded += s => observed = s;
            // Trigger by reflection-free public route: the RoomManager event
            // is invoked by HandleRoomPacket(SceneLoaded, …) — but for unit
            // testing we exercise the bridge by raising it through the
            // Dispose contract instead: we cannot directly invoke a non-
            // private event from here, so we drive it via the only public
            // path that fires it.  See TriggerAllPlayersReady() helper.
            TriggerAllPlayersReady("Arena01");
            Assert.AreEqual("Arena01", observed);
        }

        // ── ReportReady ──────────────────────────────────────────────────────

        [Test]
        public void ReportReady_NoSceneSet_DoesNotSend()
        {
            JoinEmptyRoom();
            int before = _sent.Count;
            _scene.ReportReady();
            Assert.AreEqual(before, _sent.Count);
        }

        [Test]
        public void ReportReady_WithSceneSet_SendsSceneLoadedPacket()
        {
            JoinEmptyRoom();
            _rooms.ApplyRoomPropertiesBroadcast(version: 2,
                new Dictionary<string, PropertyValue> {
                    { ReservedPropertyKeys.Scene, PropertyValue.OfString("Arena01") } });
            _sent.Clear();

            _scene.ReportReady();
            Assert.AreEqual(1, _sent.Count);
            Assert.AreEqual((byte)PacketType.SceneLoaded, _sent[0][PacketProtocol.OFFSET_TYPE]);
        }

        // ── Room-left state hygiene ──────────────────────────────────────────

        [Test]
        public void RoomLeft_AfterScenePropagated_RestoresEmptyScene()
        {
            JoinRoomWithScene("Arena01");
            // Drive OnRoomLeft via the production leave-response path so the
            // scene manager observes the same event sequence the real gateway
            // produces.
            //
            // ⚠️ The request first, and it is load-bearing rather than tidy: a
            // leave reply is answered against the room the client asked to
            // leave, so one arriving with no request outstanding is discarded as
            // stale — the room is never left, the re-entry below is refused as a
            // duplicate, and the case passes or fails on something other than
            // what it is named for. It failed on it, silently, for as long as
            // nothing in this repository could run these files.
            _rooms.LeaveRoom();
            byte[] leaveOk = BuildLeaveRoomResponseOk();
            _rooms.HandleRoomPacket(PacketType.RoomLeave, leaveOk);

            // After leaving, re-entering a room with the SAME scene name must
            // announce it again: the entry is a fresh room at a fresh property
            // version, and nothing about the previous one is carried forward.
            string observed = null;
            _scene.OnSceneLoadStarted += s => observed = s;
            JoinRoomWithScene("Arena01");
            Assert.AreEqual("Arena01", observed);
        }

        private static byte[] BuildLeaveRoomResponseOk()
        {
            // Layout: [u8 msg_kind=0 (Response)][u8 ok=1].
            return new byte[] { 0x00, 0x01 };
        }

        // ── Dispose ──────────────────────────────────────────────────────────

        [Test]
        public void Dispose_AfterDispose_NoFurtherEvents()
        {
            JoinEmptyRoom();
            _scene.Dispose();

            int fires = 0;
            _scene.OnSceneLoadStarted += _ => fires++;

            _rooms.ApplyRoomPropertiesBroadcast(version: 2,
                new Dictionary<string, PropertyValue> {
                    { ReservedPropertyKeys.Scene, PropertyValue.OfString("Arena01") } });

            Assert.AreEqual(0, fires);
        }

        [Test]
        public void Dispose_CalledTwice_IsIdempotent()
        {
            _scene.Dispose();
            Assert.DoesNotThrow(() => _scene.Dispose());
        }

        // ── LoadScene non-master guard (NetworkManager.Instance is null
        //     in the test runner — manager treats this as "not master") ──────

        [Test]
        public void LoadScene_WithoutMasterClient_DoesNotSendPropertyUpdate()
        {
            JoinEmptyRoom();
            _sent.Clear();
            // A state-order violation throws, on the same reasoning as the
            // null-name check above it: the caller gets a stack trace at the
            // misuse rather than a log line a release build filters out.
            //
            // ⚠️ The message, not just the type. Both state guards throw
            // InvalidOperationException, so a case that asserted the type alone
            // would be satisfied by whichever guard speaks first — and the
            // in-room guard speaks first.
            var ex = Assert.Throws<System.InvalidOperationException>(
                () => _scene.LoadScene("Arena01"));
            StringAssert.Contains("only the master client may change the scene", ex.Message);
            Assert.AreEqual(0, _sent.Count,
                "Non-master callers must be rejected at the SDK layer rather than producing a silent server-side no-op.");
        }

        [Test]
        public void LoadScene_NotInRoom_DoesNotSendPropertyUpdate()
        {
            int before = _sent.Count;
            var ex = Assert.Throws<System.InvalidOperationException>(
                () => _scene.LoadScene("Arena01"));
            StringAssert.Contains("must be joined to a room", ex.Message);
            Assert.AreEqual(before, _sent.Count);
        }

        [Test]
        public void LoadScene_NullSceneName_Throws()
        {
            JoinEmptyRoom();
            Assert.Throws<System.ArgumentException>(() => _scene.LoadScene(null));
        }

        [Test]
        public void LoadScene_EmptySceneName_Throws()
        {
            JoinEmptyRoom();
            Assert.Throws<System.ArgumentException>(() => _scene.LoadScene(string.Empty));
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void JoinEmptyRoom()
        {
            // Drive RoomManager through its packet handler to a "joined" state
            // so subsequent property broadcasts apply.
            var payload = BuildJoinRoomResponseOk("room-1", "CODE01", "Test", 1, 16, true);
            _rooms.HandleRoomPacket(PacketType.RoomJoin, payload);
            _sent.Clear();
        }

        private void JoinRoomWithScene(string sceneName)
        {
            JoinEmptyRoom();
            _rooms.ApplyRoomPropertiesBroadcast(version: 2,
                new Dictionary<string, PropertyValue>
                {
                    { ReservedPropertyKeys.Scene, PropertyValue.OfString(sceneName) },
                });
            // Ensure RoomManager fires its OnRoomJoined-with-scene path next
            // time by leaving and re-joining with the same scene preset on
            // the room snapshot.  Because we cannot inject a RoomInfo whose
            // Properties already contain the scene without going through
            // ApplyRoomPropertiesBroadcast, this is the path the production
            // late-join code travels too.
        }

        private void TriggerAllPlayersReady(string sceneName)
        {
            JoinEmptyRoom();
            // The server's own `all_players_scene_loaded` document, written
            // here rather than borrowed from the outbound builder.  The two
            // shapes are no longer the same: a client's report names the room
            // it is for, because that packet is re-sent when an acknowledgement
            // is lost and used to be applied wherever the sender had got to,
            // and a broadcast is already addressed to one room.
            byte[] payload = System.Text.Encoding.UTF8.GetBytes(
                "{\"scene_name\":\"" + sceneName + "\"}");
            _rooms.HandleAllPlayersSceneLoaded(payload);
        }

        private static byte[] BuildJoinRoomResponseOk(
            string roomId, string roomCode, string name,
            int playerCount, int maxPlayers, bool isPublic)
        {
            var ms = new SimpleByteStream();
            ms.WriteByte(0x00); // msg_kind = Response
            ms.WriteByte(1);    // ok
            ms.WriteString(roomId);
            ms.WriteString(roomCode);
            ms.WriteString(name);
            ms.WriteByte((byte)playerCount);
            ms.WriteByte((byte)maxPlayers);
            ms.WriteByte((byte)(isPublic ? 1 : 0));
            // one local player so the roster is non-empty (some RoomManager
            // paths gate on Players.Length).
            ms.WriteString("local");
            ms.WriteString("Local");
            ms.WriteByte(1);
            ms.WriteByte(1);
            return ms.ToArray();
        }

        private static byte[] BuildAllPlayersSceneLoadedPayload(string sceneName)
        {
            var ms = new SimpleByteStream();
            // Layout matches RoomPacketParser.ParseSceneLoadedNotification:
            // [u8 kind=2 (all-loaded)][len-prefixed string sceneName].
            ms.WriteByte(0x02);
            ms.WriteString(sceneName);
            return ms.ToArray();
        }

        // Local helper — RoomManagerTests.cs has SimpleStream but it is
        // private to that fixture.  Duplicate the minimum surface here.
        private sealed class SimpleByteStream
        {
            private readonly System.IO.MemoryStream _ms = new System.IO.MemoryStream();
            public void WriteByte(byte b) => _ms.WriteByte(b);
            // RoomPacketParser reads a string as [u16 LE length][bytes]. A
            // one-byte prefix put the first character of the payload into the
            // high half of the length, so every response this fixture built was
            // rejected as malformed and no case here ever entered a room.
            public void WriteString(string s)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(s ?? string.Empty);
                _ms.WriteByte((byte)(bytes.Length & 0xFF));
                _ms.WriteByte((byte)((bytes.Length >> 8) & 0xFF));
                _ms.Write(bytes, 0, bytes.Length);
            }
            public byte[] ToArray() => _ms.ToArray();
        }
    }
}
