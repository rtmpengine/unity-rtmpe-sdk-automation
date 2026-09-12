// RTMPE SDK — Runtime/Rooms/Lobbies/LobbyQueryOptions.cs
//
// Options for LobbyManager.ListRooms — controls sort order, filters,
// and result cap.  Mirrors the server-side LobbyListPayload JSON.

using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Sort order for <see cref="LobbyManager.ListRooms"/>.
    /// Values MUST match <c>LobbySort</c> in <c>modules/room/domain/ports/room_repository.go</c>.
    /// </summary>
    public enum LobbySort : byte
    {
        /// <summary>Fullest rooms first (most common matchmaking use case).</summary>
        PlayerCount = 0,
        /// <summary>Oldest rooms first.</summary>
        Age         = 1,
        /// <summary>Alphabetical by room name.</summary>
        Name        = 2,
    }

    /// <summary>
    /// Comparison operator for a <see cref="LobbyFilter"/>.
    /// Values MUST match <c>LobbyFilterOp</c> in <c>modules/room/domain/ports/room_repository.go</c>.
    /// </summary>
    public enum LobbyFilterOp : byte
    {
        Eq    = 0,
        NotEq = 1,
        Lt    = 2,
        Gt    = 3,
        LtEq  = 4,
        GtEq  = 5,
    }

    /// <summary>
    /// A single property filter applied server-side to the room list.
    /// Only rooms whose <c>CustomProperties[Key]</c> satisfies the operator
    /// comparison against <see cref="Value"/> are included.
    /// </summary>
    public sealed class LobbyFilter
    {
        /// <summary>CustomProperties key to filter on (max 32 bytes).</summary>
        public string       Key   { get; set; }
        /// <summary>Comparison operator.</summary>
        public LobbyFilterOp Op   { get; set; }
        /// <summary>
        /// Comparison target.  Must be a string, int, float, double, or bool —
        /// the JSON scalars the Room Service decodes.  A double is narrowed to
        /// single precision on arrival, as any real number in a filter is.
        /// </summary>
        public object       Value { get; set; }
    }

    /// <summary>
    /// The comparison targets a <see cref="LobbyFilter"/> may carry.
    /// </summary>
    /// <remarks>
    /// The filter travels as JSON and is compared server-side against a stored
    /// custom property, so the set of things it can express is the set of JSON
    /// scalars the Room Service decodes: a string, a whole number, a real
    /// number, or a boolean.
    ///
    /// A value outside that set has no JSON scalar to become, and the shape a
    /// serialiser reaches for in its absence — <c>null</c> — was not an absence
    /// on the far side: the service decoded it as the number zero and ran the
    /// query with a comparison nobody wrote. It refuses a null now, and this
    /// side refuses one regardless, because which build of a service an SDK is
    /// talking to is not something the SDK knows.
    /// </remarks>
    public static class LobbyFilterValue
    {
        /// <summary>
        /// Why <paramref name="key"/> could not be sent as a filter key, or
        /// <c>null</c> when it can.
        /// </summary>
        /// <remarks>
        /// Two rules, and the second is not one the Room Service states — it is
        /// one it makes unanswerable. A property key is refused above
        /// <see cref="PropertyLimits.MaxKeyBytes"/> everywhere it is written,
        /// so no stored key can be longer than that; a filter naming a longer
        /// one matches nothing on every room and comes back as an empty list,
        /// which is also what "no rooms match" looks like. The same reasoning
        /// the join code rests on: what makes a local refusal safe is the
        /// writer, not the validator.
        /// </remarks>
        public static string DescribeKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return "filter key must not be empty — a filter names the property it compares";

            int bytes = System.Text.Encoding.UTF8.GetByteCount(key);
            if (bytes > PropertyLimits.MaxKeyBytes)
                return $"filter key is {bytes} bytes; no stored property key exceeds " +
                       $"{PropertyLimits.MaxKeyBytes}, so a longer one can match nothing";

            return null;
        }

        /// <summary>
        /// Why <paramref name="op"/> could not be sent, or <c>null</c> when it
        /// can.
        /// </summary>
        /// <remarks>
        /// A C# enum does not bound its own values — <c>(LobbyFilterOp)9</c> is
        /// a legal expression — and the byte travels to a server whose
        /// comparison has an arm per declared operator and no default. An
        /// undeclared one is not answered with an error there: it fails every
        /// comparison, so every room is excluded and the query returns an empty
        /// list indistinguishable from "nothing matched". That is the same
        /// silence a null filter value used to produce, one field over.
        /// </remarks>
        public static string DescribeOp(LobbyFilterOp op)
            => System.Enum.IsDefined(typeof(LobbyFilterOp), op)
                ? null
                : $"filter operator {(byte)op} is not one of the comparisons a filter can ask for";

        /// <summary>
        /// Why <paramref name="value"/> could not be sent as a filter target,
        /// or <c>null</c> when it can.
        /// </summary>
        public static string Describe(object value)
        {
            if (value == null)
                return "filter value must not be null — a filter names a value to " +
                       "compare against, and there is no such thing as comparing " +
                       "against nothing";

            if (value is string || value is bool || value is int)
                return null;

            // A real number has to be one JSON can spell. Neither NaN nor an
            // infinity has a JSON form, and .NET writes them as the bare words
            // — which the Room Service does not read as a number, or as
            // anything: the parse fails at the first letter and the whole query
            // is refused, not merely this one comparison.
            if (value is float f)
                return float.IsNaN(f) || float.IsInfinity(f)
                    ? $"filter value is {f}, which JSON cannot express"
                    : null;

            if (value is double d)
                return double.IsNaN(d) || double.IsInfinity(d)
                    ? $"filter value is {d}, which JSON cannot express"
                    : null;

            return $"filter value is a {value.GetType().Name}; a filter compares against " +
                   "a string, int, float, double or bool";
        }

        /// <summary>Whether <paramref name="value"/> can be sent as a filter target.</summary>
        public static bool IsValid(object value) => Describe(value) == null;
    }

    /// <summary>
    /// Options for <see cref="LobbyManager.ListRooms"/>.
    /// </summary>
    public sealed class LobbyQueryOptions
    {
        /// <summary>
        /// Name of the lobby to query.  Required: the Room Service has no
        /// default lobby and refuses an empty name, so
        /// <see cref="LobbyManager.ListRooms"/> throws rather than sending one.
        /// See <see cref="RTMPE.Rooms.LobbyName"/> for the accepted form.
        /// </summary>
        public string LobbyName { get; set; } = string.Empty;

        /// <summary>
        /// Maximum number of rooms to return (1–100; 0 = server default = 100).
        /// </summary>
        public int MaxResults { get; set; } = 0;

        /// <summary>Sort order for the result set (default = PlayerCount desc).</summary>
        public LobbySort SortBy { get; set; } = LobbySort.PlayerCount;

        /// <summary>
        /// Optional server-side property filters.
        /// All filters must be satisfied for a room to appear in results.
        /// Null or empty = no filter (all matching rooms returned).
        /// </summary>
        public List<LobbyFilter> Filters { get; set; }
    }
}
