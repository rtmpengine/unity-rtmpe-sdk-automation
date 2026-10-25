// RTMPE SDK — Runtime/Crypto/MigratingPinStore.cs
//
// Composite IServerKeyPinStore that reads through a hardened primary store
// (EncryptedFilePinStore) and lazily migrates pins from a legacy fallback
// store (PlayerPrefsPinStore).
//
// MOTIVATION (SDK-H1)
// -------------------
// PlayerPrefsPinStore persists each pin to the platform preferences store
// (Android SharedPreferences, iOS user defaults, Windows registry).  The
// raw hex value is recoverable through `adb pull` on a debuggable Android
// build or cloud-backup blobs on older Androids, and an attacker with that
// read+write surface can swap the persisted pin for one whose private key
// they control.  Pure pin tampering is not by itself sufficient for MITM
// (the attacker still needs a network position and a signed Challenge that
// matches their substituted key), but combined with a malicious gateway
// proxy it removes the strict-mode safety net that pinning is meant to
// provide.
//
// EncryptedFilePinStore raises the bar by binding each record to an
// HMAC-SHA256 tag derived from SystemInfo.deviceUniqueIdentifier (see its
// header for the full threat-model comparison).  Making it the default —
// instead of leaving it opt-in behind SetPinStore — closes the gap for the
// default integration path without breaking the public API.
//
// MIGRATION CONTRACT
// ------------------
// Existing installs already hold pins inside PlayerPrefs from prior
// versions of the SDK.  A hard switch would force a fresh TrustOnFirstUse
// capture against every previously-trusted endpoint, which both downgrades
// the local trust state (the user trusts the key again because the SDK
// "forgot" it) and surfaces as a TOFU "first connect" warning when in
// fact this endpoint is a long-known one.
//
// MigratingPinStore avoids that regression by:
//   1. Reading the primary store first.  A pin written via the new path
//      is the authoritative answer.
//   2. Falling back to the legacy store when the primary has no record.
//      A non-null legacy pin is immediately persisted to the primary AND
//      deleted from the legacy store, so subsequent reads bypass the
//      fallback path entirely.
//   3. Treating any failure to write the migrated pin as a soft error —
//      the legacy pin is still returned to the caller, and the migration
//      retries on the next Load.  A persistently-failing primary store
//      (e.g. read-only filesystem) therefore degrades to the prior
//      behaviour rather than silently breaking the handshake.
//   4. On Save, writing to the primary and clearing the legacy entry only
//      after a successful primary write.  A primary throw preserves the
//      legacy fallback (the migration retries on the next Load); a primary
//      success scrubs the legacy entry so the two stores cannot diverge
//      after an explicit rotation.
//   5. On Clear, clearing both stores so a legitimate pin-forget cannot
//      leave a stale legacy entry behind that the next Load would
//      mistakenly resurrect.
//
// THREAD-SAFETY
// -------------
// Same as the underlying IServerKeyPinStore contract: callers serialise
// access on the main thread.  The internal migration step performs at
// most one extra Save + one Clear per endpoint, both of which delegate
// to the inner stores' own thread-safety properties.

using UnityEngine;

namespace RTMPE.Crypto
{
    /// <summary>
    /// The default <see cref="IServerKeyPinStore"/> installed by
    /// <see cref="Core.NetworkManager"/>. Stores pins in an
    /// <see cref="EncryptedFilePinStore"/>, and moves a pin found only in a
    /// <see cref="PlayerPrefsPinStore"/> into it the first time it is read.
    /// </summary>
    /// <remarks>
    /// <see cref="Save"/> and <see cref="Clear"/> do not throw: a failure in
    /// either store is logged as a warning.
    /// </remarks>
    public sealed class MigratingPinStore : IServerKeyPinStore, IPinStoreAvailability, IProvisionedPinStore
    {
        private readonly IServerKeyPinStore _primary;
        private readonly IServerKeyPinStore _legacy;

        /// <summary>
        /// Creates the default store: a new <see cref="EncryptedFilePinStore"/>,
        /// with a new <see cref="PlayerPrefsPinStore"/> as the fallback it
        /// migrates pins from.
        /// </summary>
        public MigratingPinStore()
            : this(new EncryptedFilePinStore(), new PlayerPrefsPinStore())
        {
        }

        // Test / advanced-callers seam.  Internal so production callers
        // cannot accidentally swap the production stores; exposed via
        // [InternalsVisibleTo("RTMPE.PinStore.Tests")] in AssemblyInfo.
        // Either argument may be null, in which case the corresponding
        // side of the migration is silently skipped — useful for callers
        // that want a primary-only store without re-implementing the
        // migrate-on-read logic in their own composite.
        internal MigratingPinStore(IServerKeyPinStore primary, IServerKeyPinStore legacy)
        {
            _primary = primary;
            _legacy  = legacy;
        }

        /// <summary>
        /// Returns the pin for <paramref name="endpoint"/>, or
        /// <see langword="null"/>. A pin found only in the
        /// <see cref="PlayerPrefsPinStore"/> is copied into the
        /// <see cref="EncryptedFilePinStore"/> and then removed from
        /// PlayerPrefs; if the copy fails, the PlayerPrefs entry is kept and
        /// the move is retried on the next read.
        /// </summary>
        /// <remarks>
        /// To ask which pin was provisioned, as
        /// <see cref="Core.NetworkSettings.requireFirstUseProvisioned"/> does,
        /// call <see cref="TryLoadProvisioned"/> instead: this method would
        /// move a PlayerPrefs pin into the file store, where the next
        /// provisioned read would find it.
        /// </remarks>
        /// <param name="endpoint">Canonical <c>host:port</c> string.</param>
        public byte[] Load(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return null;

            // 1. The primary store is the source of truth once a pin has
            //    been written via this composite.  Return it verbatim.
            var fromPrimary = _primary?.Load(endpoint);
            if (fromPrimary != null) return fromPrimary;

            // 2. Fall through to the legacy store.  A null result here is
            //    the steady-state for an endpoint that has never been
            //    pinned on this device.
            var fromLegacy = _legacy?.Load(endpoint);
            if (fromLegacy == null) return null;

            // 3. Lazy migration: copy the legacy pin into the primary
            //    store and clear it from the legacy side so the next Load
            //    short-circuits at step 1.  When _primary is null the
            //    composite is a legacy-only configuration (advanced seam
            //    used by tests / future opt-outs) and the legacy entry
            //    MUST NOT be cleared — there is nowhere to migrate the
            //    pin to, so clearing would silently lose it.  A Save
            //    failure on the primary is treated the same way: the
            //    legacy entry stays so the next Load retries.
            if (_primary == null) return fromLegacy;

            try
            {
                _primary.Save(endpoint, fromLegacy);
            }
            catch (System.Exception ex)
            {
                // Soft-fail: log and continue.  A persistently-failing
                // primary leaves the legacy pin in place and the next
                // Load will retry the migration.
                Debug.LogWarning(
                    $"[RTMPE] MigratingPinStore: failed to migrate pin for endpoint {endpoint}: " +
                    ex.GetType().Name + " — legacy pin retained, will retry on next Load.");
                return fromLegacy;
            }

            // Only clear the legacy entry once the primary Save has
            // actually committed — Save throwing above returns early and
            // therefore skips this step.
            _legacy?.Clear(endpoint);
            Debug.Log(
                $"[RTMPE] MigratingPinStore: migrated pin for endpoint {endpoint} " +
                "from PlayerPrefs to the hardened file-backed store.");

            return fromLegacy;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Returns <see langword="false"/> when the file store cannot be read,
        /// or when neither store holds a pin and the PlayerPrefs store cannot
        /// be read. Reads through <see cref="Load"/>, so a PlayerPrefs pin is
        /// migrated as it is found.
        /// </remarks>
        public bool TryLoadAuthoritative(string endpoint, out byte[] pin)
        {
            pin = null;
            if (string.IsNullOrEmpty(endpoint)) return true;

            // The primary is the source of truth, so a primary this process
            // cannot read makes the composite's answer non-authoritative
            // whatever the legacy side holds: the pin that matters may be the
            // one behind the unreadable file, and every pin written through
            // this composite lives there.
            if (_primary is IPinStoreAvailability primaryAvailability)
            {
                if (!primaryAvailability.TryLoadAuthoritative(endpoint, out pin))
                    return false;
                if (pin != null) return true;
            }

            // The ordinary read owns the legacy fall-through and the lazy
            // migration.  Reading the primary a second time is the price of
            // keeping that sequence in one place rather than in two that can
            // drift — and it is also what answers for a primary that is not
            // one of the stores able to speak for itself, which the block
            // above skips entirely.
            pin = Load(endpoint);
            if (pin != null) return true;

            // Only once nothing has been found anywhere does it matter whether
            // the legacy side was in a position to answer.  Asking earlier
            // would let an unreadable legacy store veto a pin the primary is
            // holding — two methods on this object disagreeing about the same
            // endpoint in the same instant, and the composite refusing a
            // connection it has the pin for.
            //
            // ⛔ Refusing here is the fail-closed reading and it is not a new
            // outage: before the legacy side could report this at all, a
            // backing store that raised on read unwound through this method
            // into the handshake.  What the refusal replaces is that, not a
            // working connection.
            if (_legacy is IPinStoreAvailability legacyAvailability
                && !legacyAvailability.TryLoadAuthoritative(endpoint, out _))
            {
                pin = null;
                return false;
            }

            return true;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Reads only the <see cref="EncryptedFilePinStore"/>; the PlayerPrefs
        /// fallback is neither read nor migrated. "Provisioned" describes where
        /// the pin is stored, not how it got there: a key captured on first use
        /// while <c>requireFirstUseProvisioned</c> was off is stored in the
        /// same file.
        /// </remarks>
        public bool TryLoadProvisioned(string endpoint, out byte[] pin)
        {
            pin = null;
            if (string.IsNullOrEmpty(endpoint)) return true;
            if (_primary == null) return true;

            if (_primary is IProvisionedPinStore primaryProvisioned)
                return primaryProvisioned.TryLoadProvisioned(endpoint, out pin);

            if (_primary is IPinStoreAvailability primaryAvailability)
                return primaryAvailability.TryLoadAuthoritative(endpoint, out pin);

            pin = _primary.Load(endpoint);
            return true;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Writes to the <see cref="EncryptedFilePinStore"/> and, when that
        /// succeeds, removes the endpoint's PlayerPrefs entry. A failure is
        /// logged as a warning instead of thrown.
        /// </remarks>
        public void Save(string endpoint, byte[] pin)
        {
            if (string.IsNullOrEmpty(endpoint)) return;

            // Authoritative write goes to the primary.  Soft-fail mirrors
            // Load's contract: the inner stores can throw (PlayerPrefs.Save
            // surfaces IOException / SecurityException on some platforms,
            // File.WriteAllBytes can surface UnauthorizedAccessException on
            // sandboxed mobile filesystems), and an unhandled throw here
            // would propagate up through ServerKeyPinning.PersistFirstUse →
            // HandshakeHandlers.OnChallenge and tear down the handshake even
            // when the new pin has nothing to do with the failure.  Wrapping
            // the call keeps the handshake alive; a forensic warning records
            // the failure so an operator can correlate it with disk-pressure
            // or permission-revocation metrics.
            bool primarySucceeded = false;
            try
            {
                _primary?.Save(endpoint, pin);
                primarySucceeded = true;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning(
                    $"[RTMPE] MigratingPinStore: primary Save failed for endpoint {endpoint}: " +
                    ex.GetType().Name + ": " + ex.Message + " — legacy entry left untouched as fallback.");
            }

            // Legacy clear runs ONLY after a successful primary write.
            // Clearing on primary failure would drop the only persistent
            // copy of the pin; preserving it lets the next Load retry the
            // migration via the existing Load-time path.  Clear failures
            // are softened too — at worst the legacy entry persists and is
            // ignored on next Load (primary precedence), which is strictly
            // better than crashing the handshake.
            if (primarySucceeded)
            {
                try
                {
                    _legacy?.Clear(endpoint);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning(
                        $"[RTMPE] MigratingPinStore: legacy Clear failed for endpoint {endpoint} " +
                        "after a successful primary Save: " + ex.GetType().Name + ": " + ex.Message +
                        " — pin is persisted; legacy entry will be ignored on next Load via " +
                        "primary precedence.");
                }
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Clears both stores. A failure in either is logged as a warning
        /// instead of thrown.
        /// </remarks>
        public void Clear(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return;

            // Both sides are cleared on a best-effort basis to honour the
            // explicit pin-forget contract.  Each clear is independently
            // soft-failed so a transient prefs / file-system error on one
            // side does not prevent the other side from being scrubbed —
            // partial forgetting is preferable to crashing a legitimate
            // ClearPinnedKey() invocation from NetworkManager.
            try { _primary?.Clear(endpoint); }
            catch (System.Exception ex)
            {
                Debug.LogWarning(
                    $"[RTMPE] MigratingPinStore: primary Clear failed for endpoint {endpoint}: " +
                    ex.GetType().Name + " — legacy clear will still be attempted.");
            }

            try { _legacy?.Clear(endpoint); }
            catch (System.Exception ex)
            {
                Debug.LogWarning(
                    $"[RTMPE] MigratingPinStore: legacy Clear failed for endpoint {endpoint}: " +
                    ex.GetType().Name + ".");
            }
        }
    }
}
