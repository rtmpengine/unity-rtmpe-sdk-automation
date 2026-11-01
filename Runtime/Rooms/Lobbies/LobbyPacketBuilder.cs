// RTMPE SDK — Runtime/Rooms/Lobbies/LobbyPacketBuilder.cs
//
// Builds binary lobby packets (0x27–0x29) in the RTMPE wire format.
// Payloads are JSON objects serialised to UTF-8 bytes, consistent with the
// existing RoomPacketBuilder pattern.

using System;
using System.Text;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Builds the JSON payloads for LobbyJoin (0x27), LobbyLeave (0x28),
    /// and LobbyList (0x29) packets.  All serialisation is done without
    /// external JSON libraries so the SDK has no additional dependencies.
    /// </summary>
    internal static class LobbyPacketBuilder
    {
        // ── LobbyJoin (0x27) ─────────────────────────────────────────────────

        /// <summary>
        /// Builds the JSON payload for a LobbyJoin request.
        /// Server responds with the versioned room-list envelope
        /// <c>{"version":1,"rooms":[…]}</c> — see <see cref="LobbyPacketParser"/>.
        /// </summary>
        public static byte[] BuildLobbyJoinPayload(string lobbyName)
        {
            var json = $"{{\"lobby_name\":{JsonString(lobbyName ?? string.Empty)}}}";
            return Encoding.UTF8.GetBytes(json);
        }

        // ── LobbyLeave (0x28) ────────────────────────────────────────────────

        /// <summary>
        /// Builds the JSON payload for a LobbyLeave fire-and-forget message.
        /// </summary>
        public static byte[] BuildLobbyLeavePayload(string lobbyName)
        {
            var json = $"{{\"lobby_name\":{JsonString(lobbyName ?? string.Empty)}}}";
            return Encoding.UTF8.GetBytes(json);
        }

        // ── LobbyList (0x29) ─────────────────────────────────────────────────

        /// <summary>
        /// Builds the JSON payload for a LobbyList request.
        /// </summary>
        public static byte[] BuildLobbyListPayload(LobbyQueryOptions opts)
        {
            if (opts == null) opts = new LobbyQueryOptions();

            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append($"\"lobby_name\":{JsonString(opts.LobbyName ?? string.Empty)}");
            sb.Append($",\"max_results\":{opts.MaxResults}");
            sb.Append($",\"sort_by\":{(byte)opts.SortBy}");

            if (opts.Filters != null && opts.Filters.Count > 0)
            {
                sb.Append(",\"filters\":[");
                for (int i = 0; i < opts.Filters.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var f = opts.Filters[i];
                    sb.Append('{');
                    sb.Append($"\"key\":{JsonString(f.Key ?? string.Empty)}");
                    sb.Append($",\"op\":{(byte)f.Op}");
                    sb.Append($",\"value\":{JsonValue(f.Value)}");
                    sb.Append('}');
                }
                sb.Append(']');
            }

            sb.Append('}');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Serialises <paramref name="s"/> as a JSON string, delegating to
        /// <see cref="PropertyJson.AppendJsonString"/> which escapes backslash,
        /// double-quote, AND all control characters (&#x3c; 0x20) as \uXXXX.
        /// The previous hand-rolled implementation escaped only \\ and \",
        /// producing malformed JSON for any lobby name or key containing a
        /// tab, newline, or other control character (SDKR-03).
        /// </summary>
        private static string JsonString(string s)
        {
            var sb = new StringBuilder();
            PropertyJson.AppendJsonString(sb, s ?? string.Empty);
            return sb.ToString();
        }

        /// <summary>
        /// Serialise a filter's comparison target.
        /// </summary>
        /// <remarks>
        /// There is no fallback rendering. Anything this method cannot express
        /// is refused, because the value a serialiser reaches for when it has
        /// nothing to write — <c>null</c> — was read by the Room Service as the
        /// number zero, and the query then ran a comparison the caller never
        /// asked for that the result set gave no sign of. The service refuses a
        /// null now as well; this side does not depend on that, because a
        /// deployed service is a thing an SDK finds out about late.
        /// </remarks>
        private static string JsonValue(object v)
        {
            string reason = LobbyFilterValue.Describe(v);
            if (reason != null)
                throw new ArgumentException("[RTMPE] " + reason, nameof(v));

            if (v is bool b)     return b ? "true" : "false";
            if (v is string s)   return JsonString(s);
            if (v is int i)      return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (v is float f)    return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (v is double d)   return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

            // Not a fall-through cast. A kind admitted above and unhandled here
            // would be unboxed as a double and throw an InvalidCastException
            // from inside a builder whose contract is an ArgumentException, so
            // the two lists are made to disagree loudly instead.
            throw new ArgumentException(
                $"[RTMPE] filter value of type {v.GetType().Name} is admitted by " +
                "LobbyFilterValue and cannot be written here.", nameof(v));
        }
    }
}
