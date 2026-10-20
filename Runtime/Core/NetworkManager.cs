// RTMPE SDK — Runtime/Core/NetworkManager.cs
//
// Central entry point for all RTMPE networking. Singleton MonoBehaviour that wires
// together NetworkSettings, NetworkThread, and MainThreadDispatcher.
//
// Threading model:
// • NetworkManager lives on the Unity main thread.
// • NetworkThread runs on a dedicated background thread.
// • Packets received on the background thread are delivered via MainThreadDispatcher
//   so that all state mutations and Unity API calls occur on the main thread.
//
// Singleton contract:
// • [DefaultExecutionOrder(-1000)] — Awake runs before all other components.
// • Instance getter returns the scene-placed NetworkManager if one exists,
//   otherwise null. It does NOT auto-create a stand-in with empty defaults —
//   silent auto-creation hid configuration bugs and produced sessions with
//   blank crypto material when an unrelated component touched Instance early.
// • _applicationIsQuitting flag guards against Unity's destroy-order issues.
//
// Protocol note:
// • All header field constants use PacketProtocol.* from NetworkConstants.cs.
//   Do NOT introduce magic numbers here — sync failures with the Rust gateway are
//   silent and extremely difficult to debug.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
using RTMPE.Crypto;
using RTMPE.Crypto.Internal;
using RTMPE.Protocol;
using RTMPE.Rooms;
using RTMPE.Rpc;
using RTMPE.Sync;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Core
{
    /// <summary>
    /// The entry point of the SDK. It owns the connection, the encrypted session, the
    /// heartbeat and the managers for rooms, lobbies, matchmaking, spawning and scenes.
    /// </summary>
    /// <remarks>
    /// <para>Put one <c>NetworkManager</c> on a GameObject in your boot scene (Add Component →
    /// RTMPE → NetworkManager, or let the Setup Wizard add it) and assign a
    /// <see cref="NetworkSettings"/> asset to it. It persists across scene loads, and a second
    /// <c>NetworkManager</c> destroys itself. The SDK never creates one for you:
    /// <see cref="NetworkManager.Instance"/> is <see langword="null"/> until one exists.</para>
    /// <para>Call its members from the Unity main thread unless a member's documentation
    /// says otherwise. Events are raised on the main thread.</para>
    /// </remarks>
    [DefaultExecutionOrder(-1000)]
    [AddComponentMenu("RTMPE/NetworkManager")]
    public sealed partial class NetworkManager : MonoBehaviour
    {
        // ── Implementation split across NetworkManager.*.cs partial files: ──
        //
        //   • NetworkManager.Singleton.cs        — singleton + transport factory
        //   • NetworkManager.Fields.cs           — fields, runtime state, properties
        //   • NetworkManager.Events.cs           — public event surface
        //   • NetworkManager.Lifecycle.cs        — Awake/Start/Update/OnDestroy + scene
        //   • NetworkManager.Connection.cs       — Connect/Reconnect/Disconnect + InitialiseNetwork
        //   • NetworkManager.HandshakeHandlers.cs — Challenge/SessionAck handlers
        //   • NetworkManager.ReceivePath.cs      — ProcessPacket dispatch + state machine
        //   • NetworkManager.AeadPipeline.cs     — EncryptAndSend / DecryptInbound
        //   • NetworkManager.GameData.cs         — StateSync / Variables / RPC handlers
        //   • NetworkManager.Jwt.cs              — JWT validation + auth helpers
        //   • NetworkManager.TestSeams.cs        — internal test hooks + SafeRaise
        //
        // The C# compiler merges these into a single sealed type at build time;
        // there is no runtime cost to the partial split.  All public API surface,
        // threading semantics, and AEAD wire format are maintained intact.
    }


    // ── Connection state ──────────────────────────────────────────────────────

    /// <summary>
    /// The connection state of a <see cref="NetworkManager"/>, read from
    /// <see cref="NetworkManager.State"/>.
    /// </summary>
    public enum NetworkState
    {
        /// <summary>
        /// No connection. <see cref="NetworkManager.Connect(string)"/> and
        /// <see cref="NetworkManager.Reconnect"/> are accepted in this state. Reconnect
        /// attempts also wait in this state between tries.
        /// </summary>
        Disconnected,

        /// <summary>A <see cref="NetworkManager.Connect(string)"/> is in progress.</summary>
        Connecting,

        /// <summary>Connected, not in a room.</summary>
        Connected,

        /// <summary>Connected and in a room.</summary>
        InRoom,

        /// <summary>The connection is closing.</summary>
        Disconnecting,

        /// <summary>
        /// A reconnect attempt started by <see cref="NetworkManager.Reconnect"/> is in
        /// progress. It ends in <see cref="Connected"/> or <see cref="Disconnected"/>.
        /// </summary>
        Reconnecting
    }

    // DisconnectReason moved to DisconnectReason.cs — its ordinals are a wire
    // contract, and the policy reading them (DisconnectSignal) has to be
    // compilable without UnityEngine so it can be driven by tests.  Nothing
    // about the enum changed: same namespace, same members, same ordinals.
}
