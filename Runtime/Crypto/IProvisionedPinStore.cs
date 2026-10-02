// RTMPE SDK — Runtime/Crypto/IProvisionedPinStore.cs
//
// A pin that was PROVISIONED, as distinct from one the store can produce.
//
// The hardened Trust-On-First-Use contract (NetworkSettings.
// requireFirstUseProvisioned) refuses the first flight to an endpoint unless a
// pin for it already exists, placed there out of band — a staged install, an
// MDM push, a signed bootstrap configuration.  The store the SDK installs by
// default is a composite: a hardened, device-bound file in front of the
// platform preferences store earlier versions wrote to, with a lazy migration
// from the second into the first.  Read through the ordinary Load, a pin that
// only the preferences store holds is indistinguishable from a provisioned one
// — and the preferences store is writable by exactly the local actor the
// hardened store exists to keep out.  A record planted there, for an endpoint
// the hardened file has no entry for, would be migrated into the file and
// enforced as though somebody had provisioned it.
//
// So the hardened contract asks a narrower question, through this interface:
// what the store holds in the backing that provisioning writes to, with no
// fallback and no migration.  "Provisioned" names the backing, not the
// provenance — a store cannot tell an out-of-band write from a first-use
// capture made while the contract was off, and both land in the same file —
// so what the flag certifies is that the pin did not arrive through a
// fallback.  Declared separately, as IPinStoreAvailability is, so stores
// outside this package keep compiling; a store that does not implement it is
// taken at its word, which is correct for a store an integrator installed as
// the provisioning channel itself.  Application code that reads the store
// under the flag reads through this interface too: the ordinary Load is the
// migration, and a record it moves into the hardened backing is found there
// by the next hardened read.  A store that wraps the shipped composite must
// implement this interface and delegate to it, or the wrapper is taken at its
// word and the read the flag exists to avoid is made through the wrapper.

namespace RTMPE.Crypto
{
    /// <summary>
    /// Implemented by an <see cref="IServerKeyPinStore"/> that can read a pin
    /// from the storage that provisioning writes to, without falling back to
    /// any other storage it holds.
    /// </summary>
    /// <remarks>
    /// <see cref="Core.NetworkSettings.requireFirstUseProvisioned"/> reads the
    /// pin store through this interface. A store that does not implement it is
    /// taken at its word.
    /// </remarks>
    public interface IProvisionedPinStore
    {
        /// <summary>
        /// Loads the pin provisioned for <paramref name="endpoint"/>, reading
        /// only the storage that provisioning writes to: no fallback storage is
        /// read and nothing is migrated.
        /// </summary>
        /// <param name="endpoint">
        /// Canonical <c>host:port</c> string, as
        /// <see cref="IServerKeyPinStore.Load"/> takes it. A null or empty
        /// endpoint returns <see langword="true"/> with no pin.
        /// </param>
        /// <param name="pin">
        /// The provisioned 32-byte key, or <see langword="null"/> when none was
        /// provisioned or the storage could not be read.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the storage was read;
        /// <see langword="false"/> when it could not be read, in which case a
        /// <see langword="null"/> <paramref name="pin"/> does not mean the
        /// endpoint has no pin (the same distinction
        /// <see cref="IPinStoreAvailability.TryLoadAuthoritative"/> draws).
        /// </returns>
        bool TryLoadProvisioned(string endpoint, out byte[] pin);
    }
}
