// RTMPE SDK — Runtime/Crypto/ServerKeyPinning.cs
//
// Pure logic that drives the per-session pinning decision.  Has no Unity
// dependencies so it runs unchanged inside the test runner.
//
// The resolver runs in two phases:
//
//  1. Pre-Challenge (PreparePin):
//       Decide whether to refuse outright (Strict + no pin), and otherwise
//       compute the byte[] that NetworkManager will pass to
//       HandshakeHandler.ValidateChallenge as `pinnedServerStaticPub`.
//       TOFU with no persisted pin returns null here — the cryptographic
//       verification in ValidateChallenge is still the gate; the captured
//       key is persisted only AFTER verification succeeds.
//
//  2. Post-Challenge (PersistFirstUse):
//       Called only on the success path, with the staticPub returned by
//       ValidateChallenge.  Writes the pin in TOFU mode if and only if no
//       pin was previously persisted for this endpoint.

namespace RTMPE.Crypto
{
    /// <summary>
    /// Outcome of <see cref="ServerKeyPinning.PreparePin(ServerPinningMode, byte[], IServerKeyPinStore, string, int, bool)"/>.
    /// </summary>
    public enum PinDecision
    {
        /// <summary>Proceed without enforcing a pin (<see cref="ServerPinningMode.InsecureNoPinning"/>).</summary>
        ProceedUnpinned = 0,

        /// <summary>Proceed and enforce <see cref="PinResolution.PinToEnforce"/>.</summary>
        ProceedWithPin = 1,

        /// <summary>
        /// <see cref="ServerPinningMode.TrustOnFirstUse"/> with no stored pin:
        /// proceed, and store the server's key once the handshake has verified
        /// it (<see cref="ServerKeyPinning.PersistFirstUse"/>).
        /// </summary>
        ProceedCaptureFirstUse = 2,

        /// <summary>
        /// Refuse the handshake: no pin is available and first-use capture is
        /// not allowed. Returned for <see cref="ServerPinningMode.Strict"/> with
        /// no configured pin; for <see cref="ServerPinningMode.TrustOnFirstUse"/>
        /// when the pin store could not be read, or when
        /// <c>requireFirstUseProvisioned</c> is set and no pin was provisioned;
        /// and for an unrecognised mode.
        /// </summary>
        RefuseStrictNoPin = 3,
    }

    /// <summary>
    /// The pinning decision for one handshake, returned by
    /// <see cref="ServerKeyPinning.PreparePin(ServerPinningMode, byte[], IServerKeyPinStore, string, int, bool)"/>.
    /// </summary>
    public readonly struct PinResolution
    {
        /// <summary>What the handshake should do.</summary>
        public PinDecision Decision { get; }

        /// <summary>Pin to enforce in <see cref="HandshakeHandler.ValidateChallenge"/>, or <see langword="null"/>.</summary>
        public byte[] PinToEnforce { get; }

        /// <summary>The canonical <c>host:port</c> string the pin is stored under.</summary>
        public string Endpoint { get; }

        /// <summary>Creates a resolution from its three values.</summary>
        /// <param name="decision">What the handshake should do.</param>
        /// <param name="pinToEnforce">Pin to enforce, or <see langword="null"/>.</param>
        /// <param name="endpoint">Canonical <c>host:port</c> string.</param>
        public PinResolution(PinDecision decision, byte[] pinToEnforce, string endpoint)
        {
            Decision     = decision;
            PinToEnforce = pinToEnforce;
            Endpoint     = endpoint;
        }
    }

    /// <summary>
    /// Server key pinning helpers: the endpoint string pins are stored under,
    /// the pin to enforce for a handshake, and storage of a key captured on
    /// first use.
    /// </summary>
    public static class ServerKeyPinning
    {
        /// <summary>
        /// Returns the canonical <c>host:port</c> string that pins are stored
        /// under: the host Unicode-normalised (NFC), trimmed and lower-cased,
        /// followed by a colon and the port.
        /// </summary>
        /// <remarks>
        /// No DNS lookup is made: a pin is bound to the configured address, so
        /// a resolver that returns a different IP address cannot bypass it. A
        /// <see langword="null"/> or empty host yields <c>":port"</c>.
        /// </remarks>
        /// <param name="host">The server host, as configured.</param>
        /// <param name="port">The server port.</param>
        /// <exception cref="System.ArgumentException"><paramref name="host"/> contains a NUL character.</exception>
        public static string CanonicalEndpoint(string host, int port)
        {
            if (string.IsNullOrEmpty(host)) host = "";
            // Reject embedded NUL bytes in the host string.  PlayerPrefs and
            // platform-specific keystores (iOS Keychain, Android
            // SharedPreferences) routinely round-trip strings through
            // C-style APIs that truncate at the first NUL — a host of the
            // shape "good.example\0evil" would persist under "good.example"
            // on those backends, breaking pin-retrieval on the next
            // connection and silently demoting a previously-pinned
            // endpoint back into TOFU capture.
            if (host.IndexOf('\0') >= 0)
                throw new System.ArgumentException(
                    "host must not contain embedded NUL bytes.", nameof(host));
            // Unicode-normalise the host before the lowercase fold.  Two
            // visually-equivalent forms — e.g. U+212B (Å) versus the
            // canonically-equivalent U+00C5 (Å), or any combining-mark
            // composition versus its precomposed counterpart — would
            // otherwise hash into distinct pin slots, splitting a single
            // logical endpoint across multiple PlayerPrefs entries and
            // silently demoting a previously-pinned endpoint to a fresh
            // TOFU capture whenever the host is re-typed in a different
            // Unicode form.  NFC is the standard form for IDN compatibility.
            string normalised = host.Normalize(System.Text.NormalizationForm.FormC);
            return normalised.Trim().ToLowerInvariant() + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns <see langword="true"/> when the mode is
        /// <see cref="ServerPinningMode.Strict"/> and no pin is configured
        /// (<paramref name="pinnedKeyHex"/> is empty or whitespace), a
        /// configuration in which every handshake is refused
        /// (<see cref="PinDecision.RefuseStrictNoPin"/>).
        /// </summary>
        /// <remarks>
        /// <see cref="Core.NetworkSettings"/> uses this to warn about the
        /// configuration in the Editor.
        /// </remarks>
        /// <param name="effectiveMode">
        /// The pinning mode in force. Pass
        /// <see cref="Core.NetworkSettings.EffectivePinningMode"/>, which also
        /// accounts for <see cref="Core.NetworkSettings.requirePinnedServerPublicKey"/>.
        /// </param>
        /// <param name="pinnedKeyHex">The configured pin, possibly empty.</param>
        public static bool StrictModeRequiresPinButNoneConfigured(
            ServerPinningMode effectiveMode, string pinnedKeyHex)
            => effectiveMode == ServerPinningMode.Strict
               && string.IsNullOrWhiteSpace(pinnedKeyHex);

        /// <summary>
        /// Returns <see langword="true"/> when the sealed-box key and the pin
        /// are set to the same value, which is never a valid configuration.
        /// </summary>
        /// <remarks>
        /// The sealed-box key (<c>apiKeySealServerPublicKeyHex</c>) is the
        /// server's X25519 key; the pin (<c>pinnedServerPublicKeyHex</c>) is its
        /// Ed25519 identity key. Equal values mean the pin was pasted into the
        /// sealed-box field, and an API key sealed to it can never be opened by
        /// the server. The comparison ignores case and surrounding whitespace,
        /// and returns <see langword="false"/> when either value is empty. The
        /// Editor and the connect path use it to report the mistake.
        /// </remarks>
        /// <param name="sealKeyHex">The configured sealed-box key, possibly empty.</param>
        /// <param name="pinnedKeyHex">The configured pin, possibly empty.</param>
        public static bool ApiKeySealKeyMatchesPinnedKey(string sealKeyHex, string pinnedKeyHex)
        {
            if (string.IsNullOrWhiteSpace(sealKeyHex)
             || string.IsNullOrWhiteSpace(pinnedKeyHex))
                return false;

            return sealKeyHex.Trim().ToLowerInvariant()
                == pinnedKeyHex.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="publicKey"/> is
        /// 32 bytes long with the top bit of its last byte clear, as it is in
        /// every X25519 public key.
        /// </summary>
        /// <remarks>
        /// About half of all Ed25519 keys set that bit, so a set bit usually
        /// means the server's Ed25519 identity key was pasted into the
        /// sealed-box field. A clear bit does not prove the value is an X25519
        /// key; where a pin is configured, <see cref="ApiKeySealKeyMatchesPinnedKey"/>
        /// detects the same mistake exactly.
        /// </remarks>
        /// <param name="publicKey">Decoded 32-byte key, or <see langword="null"/>.</param>
        public static bool IsWellFormedX25519PublicKey(byte[] publicKey)
        {
            return publicKey != null
                && publicKey.Length == 32
                && (publicKey[31] & 0x80) == 0;
        }

        /// <summary>
        /// Decides which pin, if any, to enforce for the next handshake. The
        /// same as the six-parameter overload with
        /// <c>requireFirstUseProvisioned</c> set to <see langword="false"/>.
        /// </summary>
        /// <param name="mode">Configured pinning mode.</param>
        /// <param name="configuredPin">
        /// The pin decoded from
        /// <see cref="Core.NetworkSettings.pinnedServerPublicKeyHex"/> (32 bytes),
        /// or <see langword="null"/> when none is configured. Required in
        /// <see cref="ServerPinningMode.Strict"/>; in
        /// <see cref="ServerPinningMode.TrustOnFirstUse"/> it takes precedence
        /// over a stored pin.
        /// </param>
        /// <param name="store">Pin storage; used only in TrustOnFirstUse mode.</param>
        /// <param name="host">Server host, as configured.</param>
        /// <param name="port">Server port.</param>
        /// <returns>The decision, the pin to enforce and the canonical endpoint.</returns>
        public static PinResolution PreparePin(
            ServerPinningMode mode,
            byte[] configuredPin,
            IServerKeyPinStore store,
            string host,
            int port)
        {
            // Backwards-compatible default: first-use capture is permitted.
            // Callers that want to refuse first-flight TOFU MUST opt in via
            // the overload below.
            return PreparePin(mode, configuredPin, store, host, port,
                requireFirstUseProvisioned: false);
        }

        /// <summary>
        /// Decides which pin, if any, to enforce for the next handshake, with
        /// control over first-use capture.
        /// </summary>
        /// <remarks>
        /// In <see cref="ServerPinningMode.TrustOnFirstUse"/> mode the
        /// configured pin is used when there is one, and otherwise the stored
        /// pin. When the store implements <see cref="IPinStoreAvailability"/>
        /// and reports that it could not be read, the result is
        /// <see cref="PinDecision.RefuseStrictNoPin"/> rather than a new
        /// capture. An unrecognised mode is refused too.
        /// </remarks>
        /// <param name="mode">Configured pinning mode.</param>
        /// <param name="configuredPin">The configured pin (32 bytes), or <see langword="null"/>.</param>
        /// <param name="store">Pin storage; used only in TrustOnFirstUse mode.</param>
        /// <param name="host">Server host, as configured.</param>
        /// <param name="port">Server port.</param>
        /// <param name="requireFirstUseProvisioned">
        /// When <see langword="true"/>, an endpoint with no pin is refused
        /// instead of storing the key its first connection presents, which an
        /// attacker on the network could substitute. The pin must then be
        /// provisioned beforehand through a trusted channel (for example a
        /// signed configuration file or a staged install) and saved with
        /// <see cref="IServerKeyPinStore.Save"/>. A configured pin counts as
        /// provisioned. A store that implements
        /// <see cref="IProvisionedPinStore"/> is asked only for a provisioned
        /// pin, never for one found in a fallback.
        /// </param>
        /// <returns>The decision, the pin to enforce and the canonical endpoint.</returns>
        public static PinResolution PreparePin(
            ServerPinningMode mode,
            byte[] configuredPin,
            IServerKeyPinStore store,
            string host,
            int port,
            bool requireFirstUseProvisioned)
        {
            var endpoint = CanonicalEndpoint(host, port);

            switch (mode)
            {
                case ServerPinningMode.Strict:
                    if (configuredPin == null || configuredPin.Length != 32)
                        return new PinResolution(PinDecision.RefuseStrictNoPin, null, endpoint);
                    return new PinResolution(PinDecision.ProceedWithPin, configuredPin, endpoint);

                case ServerPinningMode.TrustOnFirstUse:
                    // An explicit configured pin in TOFU mode is honoured
                    // (treats TOFU as "use configured pin if you have one,
                    // otherwise capture on first use").  This avoids a
                    // surprise downgrade where an operator who provided a
                    // pin discovers it was silently ignored.
                    if (configuredPin != null && configuredPin.Length == 32)
                        return new PinResolution(PinDecision.ProceedWithPin, configuredPin, endpoint);

                    // A store that can tell "this endpoint has no pin" from "I
                    // could not read my own state" is asked the question that
                    // way.  The two answers look identical to Load, and only one
                    // of them licenses what follows: capturing whatever key this
                    // flight delivers, over pins the store still holds.
                    //
                    // Under the hardened contract the question narrows again,
                    // to what was PROVISIONED: the default store reads through
                    // a hardened file into the platform preferences an earlier
                    // version wrote, and a pin found only there was either
                    // captured by a first flight or written by the local actor
                    // the hardened file exists to keep out.  Neither is the
                    // out-of-band placement the flag demands, so a store able
                    // to draw the line is asked for the provisioned side of it
                    // alone; the fallback is not consulted and nothing migrates.
                    // Both answers a provisioned read can give besides a pin —
                    // "none" and "could not read" — end in the same refusal
                    // under the flag, by the two arms below; the distinction is
                    // kept for the store's own reporting and for any caller of
                    // the interface that is not this one.
                    byte[] persisted;
                    bool authoritative;
                    if (requireFirstUseProvisioned && store is IProvisionedPinStore provisioned)
                    {
                        authoritative = provisioned.TryLoadProvisioned(endpoint, out persisted);
                    }
                    else if (store is IPinStoreAvailability availability)
                    {
                        authoritative = availability.TryLoadAuthoritative(endpoint, out persisted);
                    }
                    else
                    {
                        persisted     = store?.Load(endpoint);
                        authoritative = true;
                    }

                    if (persisted != null && persisted.Length == 32)
                        return new PinResolution(PinDecision.ProceedWithPin, persisted, endpoint);

                    // Unreadable state is not an unpinned endpoint.  Refusing
                    // costs a connect that succeeds once the store is reachable
                    // again; proceeding would spend the pin.  This is the same
                    // stance the store takes on the write side, where an
                    // unreadable file refuses to be overwritten.
                    if (!authoritative)
                        return new PinResolution(PinDecision.RefuseStrictNoPin, null, endpoint);

                    // No pin known for this endpoint.  Under the hardened
                    // contract, refuse rather than capturing whatever the
                    // network delivers on this first flight — the pin MUST
                    // arrive via a trusted out-of-band channel.  The refuse
                    // verdict reuses RefuseStrictNoPin because the operator-
                    // visible remediation is identical: provision a pin.
                    if (requireFirstUseProvisioned)
                        return new PinResolution(PinDecision.RefuseStrictNoPin, null, endpoint);

                    return new PinResolution(PinDecision.ProceedCaptureFirstUse, null, endpoint);

                case ServerPinningMode.InsecureNoPinning:
                    return new PinResolution(PinDecision.ProceedUnpinned, null, endpoint);

                default:
                    // Unknown enum value — fail closed.  An attacker who can
                    // poison the settings asset to a bogus enum value must
                    // not slide into "no pinning"; refuse instead.
                    return new PinResolution(PinDecision.RefuseStrictNoPin, null, endpoint);
            }
        }

        /// <summary>
        /// Stores the server's verified key when <paramref name="resolution"/>
        /// is <see cref="PinDecision.ProceedCaptureFirstUse"/>, then reads it
        /// back to confirm that it was stored.
        /// </summary>
        /// <remarks>
        /// Call it only after <see cref="HandshakeHandler.ValidateChallenge"/>
        /// has returned <see langword="true"/> and
        /// <see cref="HandshakeHandler.DeriveSessionKeys(out byte[])"/> has
        /// succeeded, so that only a verified key is stored. Exceptions from
        /// the store are caught and reported through
        /// <paramref name="failure"/>.
        /// </remarks>
        /// <param name="resolution">The result of <c>PreparePin</c> for this handshake.</param>
        /// <param name="store">The pin store.</param>
        /// <param name="verifiedServerStaticPub">The server's verified 32-byte identity key.</param>
        /// <param name="failure">
        /// Why the key could not be stored or confirmed, when a write was
        /// attempted; otherwise <see langword="null"/>. Report it: if the key
        /// was not stored, the next connection to the endpoint captures a key
        /// again.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the key was written and read back.
        /// <see langword="false"/> when nothing was written (the decision was
        /// not a first-use capture, <paramref name="store"/> is
        /// <see langword="null"/>, or the key is not 32 bytes), with
        /// <paramref name="failure"/> <see langword="null"/>; or when the write
        /// failed or could not be confirmed, with <paramref name="failure"/>
        /// set.
        /// </returns>
        public static bool PersistFirstUse(
            PinResolution resolution,
            IServerKeyPinStore store,
            byte[] verifiedServerStaticPub,
            out System.Exception failure)
        {
            failure = null;
            if (resolution.Decision != PinDecision.ProceedCaptureFirstUse) return false;
            if (store == null) return false;
            if (verifiedServerStaticPub == null || verifiedServerStaticPub.Length != 32) return false;

            // A store that cannot persist must not cost the session.  The
            // implementations throw deliberately — an atomic write that did not
            // land, a file whose existing contents could not be read — and that
            // signal is owed to the layer deciding whether a fallback copy may
            // be discarded, not to the handshake, which has already verified
            // this key and can proceed on it either way.  Reported rather than
            // logged: this file carries no Unity dependency, so the caller
            // renders it.
            try
            {
                store.Save(resolution.Endpoint, verifiedServerStaticPub);
            }
            catch (System.Exception ex)
            {
                failure = ex;
                return false;
            }

            // Confirmed by reading it back, because a store is free to absorb
            // its own failure and most of them do: the composite the SDK
            // installs by default catches everything its inner stores raise so
            // that a full disk cannot end a session.  Taking Save's silence for
            // success would have this method report a capture on every connect
            // while no pin was ever written — the same silent downgrade the
            // caller is being told about, one layer up.
            byte[] readBack;
            bool readBackAuthoritative = true;
            try
            {
                // A store that can say whether it read its own state is asked
                // that way.  Otherwise a store whose read failed answers null,
                // which is indistinguishable here from a write that did not
                // land — and the diagnosis below would name the wrong fault.
                if (store is IPinStoreAvailability availability)
                    readBackAuthoritative =
                        availability.TryLoadAuthoritative(resolution.Endpoint, out readBack);
                else
                    readBack = store.Load(resolution.Endpoint);
            }
            catch (System.Exception ex)
            {
                failure = ex;
                return false;
            }

            if (!readBackAuthoritative)
            {
                failure = new System.IO.IOException(
                    "the pin store could not be read back after the write, so whether " +
                    "the key was persisted is unknown");
                return false;
            }

            if (readBack == null || !FixedTimeEquals(readBack, verifiedServerStaticPub))
            {
                failure = new System.IO.IOException(
                    "the pin store accepted the write and does not hold the key afterwards");
                return false;
            }

            return true;
        }

        // Length-checked, data-independent comparison.  Both operands are
        // public keys rather than secrets, so this is for consistency with the
        // rest of the pinning path rather than for a timing property.
        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
