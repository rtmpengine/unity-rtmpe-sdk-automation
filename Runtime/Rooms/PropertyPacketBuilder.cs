// RTMPE SDK — Runtime/Rooms/PropertyPacketBuilder.cs
//
// Builds the payload bytes for custom property packets:
//
//  RoomPropertyUpdate   (0x24): client → server → all room clients
//  PlayerPropertyUpdate (0x25): client → server → all room clients
//
// Payload encoding is UTF-8 JSON (not binary), matching the Room Service's
// existing `json.Unmarshal(envelope.Payload, &payload)` handler contract.
// The caller passes the returned bytes to PacketBuilder.Build with the
// appropriate PacketType and FLAG_RELIABLE.

using System;
using System.Collections.Generic;
using System.Text;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Produces the JSON-encoded payload bytes for
    /// <c>RoomPropertyUpdate</c> (0x24) and <c>PlayerPropertyUpdate</c> (0x25)
    /// packets.  Enforces client-side size caps up-front — a malformed request
    /// is rejected with an <see cref="ArgumentException"/> before it leaves
    /// the SDK, so the server never has to see it.
    /// </summary>
    public static class PropertyPacketBuilder
    {
        /// <summary>
        /// Build the payload for a <c>RoomPropertyUpdate</c> (0x24) packet.
        /// </summary>
        /// <param name="roomId">The room this write is for.  The packet is
        /// sent reliably, so a lost acknowledgement re-sends these bytes; the
        /// gateway compares this against the room the session occupies when
        /// the copy arrives and refuses one that has moved on.</param>
        /// <param name="expectedVersion">
        /// The version number the client expects AFTER the update commits.
        /// Server rejects anything other than <c>currentVersion + 1</c>.
        /// </param>
        /// <param name="properties">The properties to set.  A value of
        /// <see cref="PropertyValue.Deletion"/> removes its key instead of
        /// setting it — that is the server's own sentinel (an empty type tag),
        /// not a local convention.  <c>default(PropertyValue)</c> is NOT a
        /// deletion: it is a zero-valued <see cref="PropertyType.Int"/>.</param>
        public static byte[] BuildRoomPayload(
            string roomId,
            int expectedVersion,
            IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (expectedVersion < 1)
                throw new ArgumentOutOfRangeException(nameof(expectedVersion),
                    "expectedVersion must be >= 1 (monotonic, 1-based).");
            ValidateProperties(properties, PropertyLimits.MaxPropertiesPerRoom,
                ReservedPropertyKeys.RoomAllowlist);

            string json = PropertyJson.EncodeRoomPayload(roomId, expectedVersion, properties);
            return Encoding.UTF8.GetBytes(json);
        }

        /// <summary>
        /// Build the payload for a <c>PlayerPropertyUpdate</c> (0x25) packet.
        /// </summary>
        /// <param name="roomId">The room this write is for; see
        /// <see cref="BuildRoomPayload"/>.  ⚠️ The expected-version check does
        /// not stand in for it here — a player's version restarts at zero in
        /// each room, so a stale write lines up with the new room's version and
        /// commits.</param>
        /// <param name="playerId">
        /// The authenticated player's UUID.  The server rejects the packet
        /// when this does not match the session's player (self-only invariant).
        /// </param>
        /// <param name="expectedVersion">See <see cref="BuildRoomPayload"/>.</param>
        /// <param name="properties">The properties to set.</param>
        public static byte[] BuildPlayerPayload(
            string roomId,
            string playerId,
            int expectedVersion,
            IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (string.IsNullOrEmpty(playerId))
                throw new ArgumentException("playerId must not be null or empty.", nameof(playerId));
            if (expectedVersion < 1)
                throw new ArgumentOutOfRangeException(nameof(expectedVersion),
                    "expectedVersion must be >= 1 (monotonic, 1-based).");
            ValidateProperties(properties, PropertyLimits.MaxPropertiesPerPlayer,
                ReservedPropertyKeys.PlayerAllowlist);

            string json = PropertyJson.EncodePlayerPayload(roomId, playerId, expectedVersion, properties);
            return Encoding.UTF8.GetBytes(json);
        }

        // ─── Shared validation ─────────────────────────────────────────────

        private static void ValidateProperties(
            IReadOnlyDictionary<string, PropertyValue> properties,
            int maxCount,
            ISet<string> reservedAllowlist)
        {
            if (properties == null)
                throw new ArgumentNullException(nameof(properties));
            if (properties.Count == 0)
                throw new ArgumentException(
                    "At least one property must be supplied.", nameof(properties));
            if (properties.Count > maxCount)
                throw new ArgumentException(
                    $"Too many properties: {properties.Count} > limit {maxCount}.",
                    nameof(properties));

            foreach (var kv in properties)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    throw new ArgumentException("Property key must not be null or empty.", nameof(properties));

                int keyBytes = Encoding.UTF8.GetByteCount(kv.Key);
                if (keyBytes > PropertyLimits.MaxKeyBytes)
                    throw new ArgumentException(
                        $"Property key '{kv.Key}' exceeds max {PropertyLimits.MaxKeyBytes} UTF-8 bytes (got {keyBytes}).",
                        nameof(properties));

                if (!ReservedPropertyKeys.MayBeWritten(kv.Key, reservedAllowlist))
                    throw new ArgumentException(
                        $"Property key '{kv.Key}' is in the server-managed '{ReservedPropertyKeys.Prefix}' "
                        + "namespace and is not writable from a client here. The server would reject the "
                        + "update; use the dedicated helper for this key instead.",
                        nameof(properties));

                EnsureValueRepresentable(kv.Key, kv.Value);
                EnsureValueWithinLimit(kv.Key, kv.Value);
            }
        }

        /// <summary>
        /// Refuses a value JSON cannot carry.
        /// <para>
        /// <c>NaN</c> and the infinities have no JSON literal, and .NET renders
        /// them as the bare words <c>NaN</c> and <c>Infinity</c> — which is not
        /// a document.  The server does not reject the property; its decoder
        /// stops at the first bad token, so the <em>entire</em> update is
        /// discarded, and every other property in it with the same silence.
        /// </para>
        /// </summary>
        private static void EnsureValueRepresentable(string key, PropertyValue v)
        {
            bool finite;
            switch (v.Type)
            {
                case PropertyType.Float:
                    finite = IsFinite(v.AsFloat());
                    break;
                case PropertyType.Vector3:
                {
                    var vec = v.AsVector3();
                    finite = IsFinite(vec.x) && IsFinite(vec.y) && IsFinite(vec.z);
                    break;
                }
                case PropertyType.Color:
                {
                    var c = v.AsColor();
                    finite = IsFinite(c.r) && IsFinite(c.g) && IsFinite(c.b) && IsFinite(c.a);
                    break;
                }
                default:
                    return;
            }

            if (!finite)
                throw new ArgumentException(
                    $"Property '{key}' holds NaN or an infinity, which JSON cannot represent. "
                    + "The server would discard the whole update, not just this property.",
                    nameof(v));
        }

        private static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        /// <summary>
        /// Enforces the 512-byte value cap, measured the way the Room Service
        /// measures it.
        /// <para>
        /// The cap belongs to the server: it marshals the decoded value on its
        /// own and compares the result, so the only measurement that predicts
        /// acceptance is that one.  The tuple the SDK writes around the value
        /// is not part of it, and the server's encoder escapes characters this
        /// one does not — so a count taken from the local encoding is wrong in
        /// both directions and refuses values that would be stored while
        /// admitting values that would be discarded.
        /// </para>
        /// </summary>
        private static void EnsureValueWithinLimit(string key, PropertyValue v)
        {
            int valueBytes = PropertyJsonSizing.ServerValueBytes(v);
            if (valueBytes > PropertyLimits.MaxValueBytes)
                throw new ArgumentException(
                    $"Property '{key}' value exceeds max {PropertyLimits.MaxValueBytes} UTF-8 bytes "
                    + $"(measured {valueBytes}).",
                    nameof(v));
        }
    }

    /// <summary>
    /// Internal sizing helper for the server's value cap.  The count it
    /// returns is the one <c>entities.PropertyValue.Validate</c> compares:
    /// Go's JSON encoding of the decoded value, not the bytes this SDK
    /// writes around it.  The two differ in both directions, so a count
    /// taken from the local encoder either refuses values the server
    /// stores or admits values it discards.
    /// </summary>
    internal static class PropertyJsonSizing
    {
        /// <summary>
        /// Returns the UTF-8 byte count of the canonical JSON encoding of
        /// <c>{"type":"&lt;tag&gt;","value":&lt;value&gt;}</c> for the
        /// supplied <paramref name="v"/> — i.e. the inner tuple wrapped
        /// around each property's value in the wire payload.
        /// </summary>
        internal static int EstimateValueTupleBytes(PropertyValue v)
        {
            // Fixed framing of the {"type":"<T>","value":<V>} wrapper:
            //     `{`        →  1
            //     `"type":"` →  8
            //     <T>        →  T
            //     `","value":` → 10
            //     <V>        →  V
            //     `}`        →  1
            //   Total = 20 + T + V.
            int typeTagLen = TagFor(v.Type).Length;
            int valueChars = ValueByteCount(v);
            return 20 + typeTagLen + valueChars;
        }

        private static string TagFor(PropertyType t)
        {
            switch (t)
            {
                case PropertyType.Int:     return PropertyJson.TagInt;
                case PropertyType.Float:   return PropertyJson.TagFloat;
                case PropertyType.Bool:    return PropertyJson.TagBool;
                case PropertyType.String:  return PropertyJson.TagString;
                case PropertyType.Bytes:   return PropertyJson.TagBytes;
                case PropertyType.Vector3: return PropertyJson.TagVector3;
                case PropertyType.Color:   return PropertyJson.TagColor;
                case PropertyType.Deleted: return PropertyJson.TagDeleted;
                default: throw new InvalidOperationException($"Unknown PropertyType: {t}");
            }
        }

        /// <summary>
        /// UTF-8 byte count of the value as the Room Service sizes it: the
        /// JSON encoding Go's <c>json.Marshal</c> produces for the decoded
        /// value, which is what <c>entities.PropertyValue.Validate</c>
        /// compares against the cap.
        /// <para>
        /// Only strings and byte arrays can approach 512 bytes; the numeric
        /// and boolean kinds are bounded by their own formatting well below
        /// it, so they are measured by the local encoding, which agrees.
        /// </para>
        /// </summary>
        internal static int ServerValueBytes(PropertyValue v)
        {
            switch (v.Type)
            {
                case PropertyType.String:
                    return MarshalledStringBytes(v.AsString());
                case PropertyType.Bytes:
                    // Base64 travels as a JSON string and needs no escaping:
                    // its alphabet is A–Z a–z 0–9 + / =.
                    return Base64Bytes(v.AsBytesReadOnly().Length) + 2;
                default:
                    return ValueByteCount(v);
            }
        }

        private static int Base64Bytes(int rawLength) => ((rawLength + 2) / 3) * 4;

        /// <summary>
        /// Byte length of Go's JSON encoding of <paramref name="s"/>, quotes
        /// included.
        /// <para>
        /// Go escapes <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c> to six-byte
        /// <c>\u00XX</c> forms by default, and every control character outside
        /// the seven that have short forms — <c>"</c>, <c>\\</c>, backspace,
        /// form feed, newline, carriage return and tab.  A string of six-byte
        /// characters reaches the cap six times sooner than its own length
        /// suggests; a string of short-form ones reaches it three times later,
        /// so guessing high is no safer than reading the local encoding.
        /// </para>
        /// </summary>
        private static int MarshalledStringBytes(string s)
        {
            int n = 2; // the enclosing quotes
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"':
                    case '\\':
                    case '\b':
                    case '\f':
                    case '\n':
                    case '\r':
                    case '\t':
                        n += 2;
                        break;
                    case '<':
                    case '>':
                    case '&':
                        n += 6;
                        break;
                    default:
                        if (c < 0x20 || c == '\u2028' || c == '\u2029')
                        {
                            n += 6;
                        }
                        else if (char.IsHighSurrogate(c)
                                 && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        {
                            n += 4;  // one astral code point
                            i++;
                        }
                        else if (char.IsSurrogate(c))
                        {
                            n += 3;  // unpaired — Go substitutes U+FFFD
                        }
                        else if (c < 0x80) n += 1;
                        else if (c < 0x800) n += 2;
                        else n += 3;
                        break;
                }
            }
            return n;
        }

        /// <summary>
        /// UTF-8 byte count of the JSON encoding of the value alone (without
        /// the `"value":` key prefix or surrounding tuple braces).  Mirrors
        /// <see cref="PropertyJson.AppendValue"/> exactly.
        /// </summary>
        private static int ValueByteCount(PropertyValue v)
        {
            switch (v.Type)
            {
                case PropertyType.Int:
                    return v.AsInt().ToString(System.Globalization.CultureInfo.InvariantCulture).Length;

                case PropertyType.Float:
                    // Round-trip "R" formatting matches PropertyJson exactly;
                    // ASCII-only, so .Length == UTF-8 byte count.
                    return v.AsFloat().ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;

                case PropertyType.Bool:
                    return v.AsBool() ? 4 /* true */ : 5 /* false */;

                case PropertyType.String:
                    {
                        // Two surrounding quotes plus the JSON-escaped UTF-8
                        // body — escape rules MUST match PropertyJson.AppendJsonString.
                        var s = v.AsString();
                        return 2 + EscapedJsonStringByteCount(s);
                    }

                case PropertyType.Bytes:
                    {
                        // Base64: 4 chars per 3 input bytes, padded.  Two
                        // surrounding quotes added on top.  Base64 is ASCII so
                        // char count == byte count.
                        int n = v.AsBytesReadOnly().Length;
                        return 2 + ((n + 2) / 3) * 4;
                    }

                case PropertyType.Vector3:
                    {
                        var vec = v.AsVector3();
                        // [x,y,z]  — three R-formatted floats, two commas, two brackets.
                        int len = 2 /* [] */ + 2 /* commas */;
                        len += vec.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
                        len += vec.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
                        len += vec.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
                        return len;
                    }

                case PropertyType.Color:
                    {
                        var c = v.AsColor();
                        int len = 2 /* [] */ + 3 /* commas */;
                        len += c.r.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
                        len += c.g.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
                        len += c.b.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
                        len += c.a.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
                        return len;
                    }

                case PropertyType.Deleted:
                    // `null` — the only value the deletion sentinel carries.
                    return 4;

                default:
                    throw new InvalidOperationException($"Unknown PropertyType: {v.Type}");
            }
        }

        /// <summary>
        /// UTF-8 byte count of the JSON-escaped string body (excluding the
        /// surrounding quotes).  Replicates the escape decisions made by
        /// <see cref="PropertyJson.AppendJsonString"/>.
        /// </summary>
        private static int EscapedJsonStringByteCount(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int total = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"':
                    case '\\':
                    case '\b':
                    case '\f':
                    case '\n':
                    case '\r':
                    case '\t':
                        total += 2;  // backslash escape
                        break;
                    default:
                        if (c < 0x20)
                        {
                            total += 6;  // \uXXXX
                        }
                        else
                        {
                            // Multi-byte UTF-8 widths.  Mirror System.Text.Encoding.UTF8
                            // exactly:
                            //   U+0000..U+007F    →  1 byte
                            //   U+0080..U+07FF    →  2 bytes
                            //   U+0800..U+FFFF    →  3 bytes
                            //   U+10000..U+10FFFF →  4 bytes (surrogate pair)
                            if (c < 0x80)
                            {
                                total += 1;
                            }
                            else if (c < 0x800)
                            {
                                total += 2;
                            }
                            else if (char.IsHighSurrogate(c)
                                && i + 1 < s.Length
                                && char.IsLowSurrogate(s[i + 1]))
                            {
                                // Surrogate pair = single Unicode scalar in
                                // the supplementary plane → 4 UTF-8 bytes.
                                total += 4;
                                i++;
                            }
                            else
                            {
                                // BMP (or unpaired surrogate, which UTF-8
                                // emitter encodes as 3-byte replacement).
                                total += 3;
                            }
                        }
                        break;
                }
            }
            return total;
        }
    }
}
