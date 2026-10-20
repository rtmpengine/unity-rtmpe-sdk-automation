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
    /// Implemented by an <see cref="IServerKeyPinStore"/> whose storage can be
    /// unreadable, so that "the storage could not be read" is not mistaken for
    /// "the endpoint has no pin".
    /// </summary>
    /// <remarks>
    /// Under <see cref="ServerPinningMode.TrustOnFirstUse"/>, a store that
    /// reports its storage unreadable makes the SDK refuse the connection
    /// instead of storing whatever key the server presents. A store that does
    /// not implement this interface is taken at its word.
    /// </remarks>
    public interface IPinStoreAvailability
    {
        /// <summary>
        /// Loads the pin for <paramref name="endpoint"/> and reports whether
        /// the storage could be read.
        /// </summary>
        /// <param name="endpoint">
        /// Canonical <c>host:port</c> string, as
        /// <see cref="IServerKeyPinStore.Load"/> takes it. A null or empty
        /// endpoint returns <see langword="true"/> with no pin, without reading
        /// the storage.
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
