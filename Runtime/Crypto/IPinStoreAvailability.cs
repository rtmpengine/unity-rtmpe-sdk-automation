// RTMPE SDK — Runtime/Crypto/IPinStoreAvailability.cs
//
// The distinction IServerKeyPinStore.Load cannot express: whether "no pin"
// is a fact about the endpoint or merely what this process was able to read.
//
// It matters in exactly one place, and only in Trust-On-First-Use.  A store
// whose backing state is momentarily out of reach — a file held open by a
// backup agent or a scanner, permissions changed under the player — returns
// null from Load, which reads as "this endpoint has never been pinned" and
// licenses a fresh first-use capture.  That capture is a trust reset: whatever
// key the network delivers on that flight becomes the durable pin, over an
// intact pin the store still holds.  Refusing instead costs a connect that
// succeeds on the next attempt.
//
// Declared separately rather than added to IServerKeyPinStore so that stores
// outside this package keep compiling.  A store that does not implement it is
// taken at its word, which is correct for backing stores that have no
// unreadable state to distinguish.
//
// ⚠️ The reach of this is narrower than "the pin cannot be taken away".  It
// separates state that is out of reach from state that is absent; it does not
// separate absent from destroyed.  An actor who can write to the backing store
// can delete it, or corrupt it past recognition, and either reads as an
// endpoint that was never pinned — which is the same access needed to hold the
// file open, so refusing on unreadability buys nothing against that actor.
// What it closes is the accident: a scanner, a backup agent, a permissions
// change, a container that moved.  Hardware-backed key storage is the control
// for the adversarial case.

namespace RTMPE.Crypto
{
    /// <summary>
    /// Implemented by an <see cref="IServerKeyPinStore"/> whose backing state
    /// can fail to be read in a way that is distinct from holding no pin.
    /// </summary>
    public interface IPinStoreAvailability
    {
        /// <summary>
        /// Load the pin for <paramref name="endpoint"/> and report whether the
        /// answer describes the endpoint or only this attempt.
        /// </summary>
        /// <param name="endpoint">
        /// Canonical "host:port", exactly as
        /// <see cref="IServerKeyPinStore.Load"/> takes it.  A null or empty
        /// endpoint names nothing, and is answered <see langword="true"/> with
        /// no pin without the backing state being consulted.
        /// </param>
        /// <param name="pin">
        /// The persisted 32-byte pin, or <see langword="null"/>.  When the
        /// return value is <see langword="false"/> this is always
        /// <see langword="null"/> and carries no meaning.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the backing state was read: a
        /// <see langword="null"/> <paramref name="pin"/> then means the
        /// endpoint genuinely has none.  <see langword="false"/> when it could
        /// not be read, which is not evidence that the endpoint is unpinned.
        /// </returns>
        bool TryLoadAuthoritative(string endpoint, out byte[] pin);
    }
}
