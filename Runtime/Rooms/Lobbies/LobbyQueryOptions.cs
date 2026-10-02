// RTMPE SDK — Runtime/Rooms/Lobbies/LobbyQueryOptions.cs
//
// Options for LobbyManager.ListRooms — controls sort order, filters,
// and result cap.  Mirrors the server-side LobbyListPayload JSON.

using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>
    /// The sort order of a room list from <see cref="LobbyManager.ListRooms"/>.
    /// </summary>
    public enum LobbySort : byte
    {
        /// <summary>Fullest rooms first. The default.</summary>
        PlayerCount = 0,
        /// <summary>Oldest rooms first.</summary>
        Age         = 1,
        /// <summary>By room name, alphabetically.</summary>
        Name        = 2,
    }

    /// <summary>
    /// How a <see cref="LobbyFilter"/> compares a room's property with
    /// <see cref="LobbyFilter.Value"/>.
    /// </summary>
    public enum LobbyFilterOp : byte
    {
        /// <summary>Equal to the value.</summary>
        Eq    = 0,
        /// <summary>Not equal to the value.</summary>
        NotEq = 1,
        /// <summary>Less than the value.</summary>
        Lt    = 2,
        /// <summary>Greater than the value.</summary>
        Gt    = 3,
        /// <summary>Less than or equal to the value.</summary>
        LtEq  = 4,
        /// <summary>Greater than or equal to the value.</summary>
        GtEq  = 5,
    }

    /// <summary>
    /// A condition on a room property, applied by the server: a room is listed only when its
    /// property <see cref="Key"/>, compared with <see cref="Value"/> using <see cref="Op"/>,
    /// holds.
    /// </summary>
    public sealed class LobbyFilter
    {
        /// <summary>
        /// The room property to compare. Required, and at most
        /// <see cref="PropertyLimits.MaxKeyBytes"/> bytes of UTF-8.
        /// </summary>
        public string       Key   { get; set; }
        /// <summary>The comparison.</summary>
        public LobbyFilterOp Op   { get; set; }
        /// <summary>
        /// The value to compare with: a <c>string</c>, <c>int</c>, <c>float</c>,
        /// <c>double</c> or <c>bool</c>; not <see langword="null"/>, NaN or infinite. A
        /// <c>double</c> is compared at <c>float</c> precision.
        /// </summary>
        public object       Value { get; set; }
    }

    /// <summary>
    /// Checks lobby filters the way <see cref="LobbyManager.ListRooms"/> does, so you can
    /// check one before you build a query.
    /// </summary>
    /// <remarks>
    /// Each <c>Describe…</c> method returns the reason a part would be refused, or
    /// <see langword="null"/> when it is acceptable.
    /// </remarks>
    public static class LobbyFilterValue
    {
        /// <summary>
        /// Returns why <paramref name="key"/> cannot be a filter key, or
        /// <see langword="null"/> when it can.
        /// </summary>
        /// <remarks>
        /// A key must not be empty and must be at most <see cref="PropertyLimits.MaxKeyBytes"/>
        /// bytes of UTF-8; no room property has a longer key, so a longer one would match no
        /// room.
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
        /// Returns why <paramref name="op"/> cannot be sent, or <see langword="null"/> when it
        /// can: it must be one of the declared <see cref="LobbyFilterOp"/> values.
        /// </summary>
        public static string DescribeOp(LobbyFilterOp op)
            => System.Enum.IsDefined(typeof(LobbyFilterOp), op)
                ? null
                : $"filter operator {(byte)op} is not one of the comparisons a filter can ask for";

        /// <summary>
        /// Returns why <paramref name="value"/> cannot be a filter value, or
        /// <see langword="null"/> when it can: a <c>string</c>, <c>int</c>, <c>float</c>,
        /// <c>double</c> or <c>bool</c>, and not <see langword="null"/>, NaN or infinite.
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

        /// <summary>
        /// Whether <paramref name="value"/> can be a filter value (see <see cref="Describe"/>).
        /// </summary>
        public static bool IsValid(object value) => Describe(value) == null;
    }

    /// <summary>
    /// Options for <see cref="LobbyManager.ListRooms"/>.
    /// </summary>
    public sealed class LobbyQueryOptions
    {
        /// <summary>
        /// The lobby to query. Required: there is no default lobby, and
        /// <see cref="LobbyManager.ListRooms"/> throws <see cref="System.ArgumentException"/>
        /// for a name that breaks the <see cref="RTMPE.Rooms.LobbyName"/> rules.
        /// </summary>
        public string LobbyName { get; set; } = string.Empty;

        /// <summary>
        /// The most rooms to return, from 1 to 100. Default 0: the server's default of 100.
        /// </summary>
        public int MaxResults { get; set; } = 0;

        /// <summary>
        /// The sort order. Default <see cref="LobbySort.PlayerCount"/>: fullest rooms first.
        /// </summary>
        public LobbySort SortBy { get; set; } = LobbySort.PlayerCount;

        /// <summary>
        /// Conditions every listed room must meet. <see langword="null"/> or empty: no filter.
        /// </summary>
        public List<LobbyFilter> Filters { get; set; }
    }
}
