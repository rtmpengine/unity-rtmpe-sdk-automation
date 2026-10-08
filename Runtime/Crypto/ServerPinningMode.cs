// RTMPE SDK — Runtime/Crypto/ServerPinningMode.cs
//
// Three explicit modes for server-static-key pinning, with a fail-closed
// default.  The literal name "InsecureNoPinning" is deliberate: a developer
// cannot end up in that mode by accident or by leaving a field blank — the
// security-degrading choice has to be typed out in source.

namespace RTMPE.Crypto
{
    /// <summary>
    /// How the SDK checks the server's Ed25519 identity key during the
    /// handshake. Set it with <see cref="Core.NetworkSettings.serverPinningMode"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Strict</b> (default): the key must equal the configured pin,
    /// <see cref="Core.NetworkSettings.pinnedServerPublicKeyHex"/>. With no pin
    /// configured, every connection is refused. Use it for builds you ship.
    /// </para>
    /// <para>
    /// <b>TrustOnFirstUse</b>: a configured pin is enforced when there is one.
    /// Otherwise the first connection to an endpoint stores the server's key
    /// in the pin store, and later connections must present the same key; a
    /// different key is refused as a possible man-in-the-middle attack. The
    /// connection is also refused when the pin store cannot be read, and, with
    /// <see cref="Core.NetworkSettings.requireFirstUseProvisioned"/>, when no
    /// key was provisioned for the endpoint.
    /// </para>
    /// <para>
    /// <b>InsecureNoPinning</b>: any key with a valid signature is accepted,
    /// which leaves the connection open to a man-in-the-middle attack that
    /// substitutes its own key. A warning is logged each session. Use it only
    /// for local testing, or where another layer authenticates the server.
    /// </para>
    /// </remarks>
    public enum ServerPinningMode
    {
        /// <summary>The key must match the configured pin; with no pin configured, connections are refused.</summary>
        Strict = 0,

        /// <summary>The first connection to an endpoint stores the server's key; later connections must present the same key.</summary>
        TrustOnFirstUse = 1,

        /// <summary>
        /// Any key with a valid signature is accepted, and a warning is logged
        /// each session. Not for builds you ship.
        /// </summary>
        InsecureNoPinning = 2,
    }
}
