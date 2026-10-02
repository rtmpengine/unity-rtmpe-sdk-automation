// RTMPE SDK — Runtime/Crypto/IServerKeyPinStore.cs
//
// Persistent storage abstraction for server static-public-key pins.
//
// A pin is keyed by the canonical "host:port" string of the gateway endpoint.
// The store is consulted on every Challenge to enforce strict equality, and
// is written exactly once per endpoint after a TOFU first-connect succeeds.
//
// Default implementation: PlayerPrefsPinStore (UnityEngine.PlayerPrefs).
// Test code injects an in-memory implementation via NetworkManager.

using System;

namespace RTMPE.Crypto
{
    /// <summary>
    /// Persistent storage for pinned server keys: one 32-byte Ed25519 identity
    /// key per endpoint.
    /// </summary>
    /// <remarks>
    /// Endpoints are the canonical <c>host:port</c> strings produced by
    /// <see cref="ServerKeyPinning.CanonicalEndpoint"/>. The SDK calls a store
    /// from the Unity main thread, so an implementation does not need to be
    /// thread-safe. Install your own store with
    /// <see cref="Core.NetworkManager.SetPinStore"/>.
    /// </remarks>
    public interface IServerKeyPinStore
    {
        /// <summary>
        /// Returns the pin stored for <paramref name="endpoint"/>, or
        /// <see langword="null"/> when none has been stored.
        /// </summary>
        /// <param name="endpoint">
        /// Canonical <c>host:port</c> string, exactly as produced by
        /// <see cref="ServerKeyPinning.CanonicalEndpoint"/>. Do not normalise it
        /// further (for example by resolving DNS): a pin is bound to the
        /// configured address, so a resolver that returns a different IP
        /// address cannot move the endpoint away from its pin.
        /// </param>
        byte[] Load(string endpoint);

        /// <summary>
        /// Stores the 32-byte <paramref name="pin"/> for
        /// <paramref name="endpoint"/>, replacing any previous value.
        /// </summary>
        /// <remarks>
        /// When the SDK captures a key on first use, it saves the key only
        /// after the handshake has verified it. Code that provisions pins
        /// should likewise save only keys obtained from a trusted source.
        /// </remarks>
        /// <param name="endpoint">Canonical <c>host:port</c> string.</param>
        /// <param name="pin">The server's 32-byte Ed25519 identity key.</param>
        void Save(string endpoint, byte[] pin);

        /// <summary>
        /// Removes the pin stored for <paramref name="endpoint"/>; does nothing
        /// when there is none. <see cref="Core.NetworkManager.ClearPinnedKey"/>
        /// calls this, for example after the server's key has been rotated.
        /// </summary>
        /// <param name="endpoint">Canonical <c>host:port</c> string.</param>
        void Clear(string endpoint);
    }
}
