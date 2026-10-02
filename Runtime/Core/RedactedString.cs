// RTMPE SDK — Runtime/Core/RedactedString.cs
//
// Opaque wrapper for session-bearer credentials exposed on the SDK's
// public surface (JWT bearer, reconnect token).  The type defangs the
// dominant credential-leak vector: a stray `Debug.Log(token)` or a
// string-interpolated analytics breadcrumb that ends up in a crash
// report or a third-party SDK's log shipper.
//
// Wire-format / persistence behaviour is unchanged — the underlying
// string travels through the SDK exactly as before; the only difference
// is what the public accessor returns and how it renders in a log line.
//
// No UnityEngine dependency, so this file compiles into both Unity
// builds and the xunit projects under tests/unit/ without stubs.

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// Wraps a secret, such as <see cref="NetworkManager.JwtToken"/> or
    /// <see cref="NetworkManager.ReconnectToken"/>, so it is not printed by
    /// accident: <see cref="ToString"/>, string interpolation and
    /// <c>Debug.Log</c> show <c>&lt;redacted&gt;</c> instead of the value.
    /// </summary>
    /// <remarks>
    /// Call <see cref="Reveal"/> where you need the value, for example to send it
    /// to your own backend. There is no implicit conversion to
    /// <see cref="string"/>, so every use of the value is visible in your code.
    /// The value is not serialised.
    /// </remarks>
    [Serializable]
    public readonly struct RedactedString : IEquatable<RedactedString>
    {
        /// <summary>
        /// The text <see cref="ToString"/> returns in place of a non-empty value:
        /// <c>&lt;redacted&gt;</c>.
        /// </summary>
        public const string Placeholder = "<redacted>";

        /// <summary>
        /// Backing field for the wrapped secret.  Marked
        /// <see cref="NonSerializedAttribute"/> so that
        /// <c>BinaryFormatter</c>, <c>DataContractSerializer</c>,
        /// <c>XmlSerializer</c>, and any other reflection-based wire
        /// serialiser that walks private state cannot exfiltrate the
        /// underlying value.  Consumers that legitimately need to persist
        /// a token must do so through their own typed path (storing the
        /// result of <see cref="Reveal"/> after a conscious decision),
        /// not by serialising the wrapper as opaque data.
        /// </summary>
        [NonSerialized]
        private readonly string _value;

        /// <summary>
        /// Wrap a sensitive string.  Internal so the SDK retains sole
        /// authority over which strings are tagged as sensitive — app
        /// code cannot construct one to bypass the wrapper.
        /// </summary>
        internal RedactedString(string value)
        {
            _value = value;
        }

        /// <summary>
        /// Whether there is no value (null or empty). Use it to check whether a
        /// token is held without revealing it.
        /// </summary>
        public bool IsEmpty => string.IsNullOrEmpty(_value);

        /// <summary>
        /// Returns the value. Call it where you use the value, and do not store the
        /// result in a long-lived field or write it to a log.
        /// </summary>
        /// <returns>The wrapped string, which may be null or empty.</returns>
        public string Reveal() => _value;

        /// <summary>
        /// Returns <c>&lt;redacted&gt;</c> for a non-empty value and an empty string
        /// otherwise, so logging or interpolating the wrapper never shows the value.
        /// </summary>
        /// <returns><see cref="Placeholder"/>, or an empty string.</returns>
        public override string ToString() => IsEmpty ? string.Empty : Placeholder;

        /// <summary>
        /// Returns the value, like <see cref="Reveal"/>.
        /// </summary>
        /// <param name="s">The wrapper to reveal.</param>
        public static explicit operator string(RedactedString s) => s._value;

        /// <inheritdoc/>
        public bool Equals(RedactedString other) => string.Equals(_value, other._value, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is RedactedString other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => _value == null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

        /// <summary>Whether the two wrapped values are equal (ordinal comparison).</summary>
        /// <param name="a">The first wrapper.</param>
        /// <param name="b">The second wrapper.</param>
        public static bool operator ==(RedactedString a, RedactedString b) => a.Equals(b);

        /// <summary>Whether the two wrapped values differ (ordinal comparison).</summary>
        /// <param name="a">The first wrapper.</param>
        /// <param name="b">The second wrapper.</param>
        public static bool operator !=(RedactedString a, RedactedString b) => !a.Equals(b);
    }
}
