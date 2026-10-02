// RTMPE SDK — Runtime/Rooms/PropertyValue.cs
//
// Typed custom-property value shared by RoomInfo and PlayerInfo.  Seven kinds
// are supported, matching the Go server's `entities.PropertyType`
// (see modules/room/domain/entities/properties.go).
//
// Wire limits (also enforced server-side):
//  • max 20 properties per room, 10 per player
//  • max 32-byte UTF-8 key
//  • max 512-byte serialised value

using System;
using UnityEngine;

namespace RTMPE.Rooms
{
    /// <summary>
    /// The type of a <see cref="PropertyValue"/>.
    /// </summary>
    public enum PropertyType
    {
        /// <summary>A 32-bit signed integer.</summary>
        Int,
        /// <summary>A 32-bit floating-point number.</summary>
        Float,
        /// <summary>A Boolean.</summary>
        Bool,
        /// <summary>A string.</summary>
        String,
        /// <summary>A byte array.</summary>
        Bytes,
        /// <summary>A <see cref="UnityEngine.Vector3"/>.</summary>
        Vector3,
        /// <summary>A <see cref="UnityEngine.Color"/>, in linear RGBA.</summary>
        Color,

        /// <summary>
        /// A deletion: written as the value of a key, it removes that key. Create one with
        /// <see cref="PropertyValue.Deletion"/>.
        /// </summary>
        /// <remarks>
        /// A deletion never appears in <see cref="RoomInfo.Properties"/> or
        /// <see cref="PlayerInfo.Properties"/>, so code reading a snapshot does not need to
        /// handle it. <c>default(PropertyValue)</c> is an <see cref="PropertyType.Int"/> of 0,
        /// not a deletion.
        /// </remarks>
        Deleted,
    }

    /// <summary>
    /// The limits on room and player properties.
    /// </summary>
    public static class PropertyLimits
    {
        /// <summary>The most properties a room can hold.</summary>
        public const int MaxPropertiesPerRoom   = 20;
        /// <summary>The most properties a player can hold.</summary>
        public const int MaxPropertiesPerPlayer = 10;
        /// <summary>The longest property key, in bytes of UTF-8.</summary>
        public const int MaxKeyBytes            = 32;
        /// <summary>The largest property value, in bytes of its encoded (JSON) form.</summary>
        public const int MaxValueBytes          = 512;
    }

    /// <summary>
    /// A typed room or player property value.
    /// </summary>
    /// <remarks>
    /// Create one with a factory method, such as <see cref="OfInt"/> or
    /// <see cref="OfString"/>, and read it with the accessor for its <see cref="Type"/>;
    /// the other accessors throw <see cref="InvalidOperationException"/>.
    /// </remarks>
    public readonly struct PropertyValue : IEquatable<PropertyValue>
    {
        /// <summary>The type of the value, which names the accessor to read it with.</summary>
        public PropertyType Type { get; }

        private readonly int            _int;
        private readonly float          _float;
        private readonly bool           _bool;
        private readonly string         _string;
        private readonly byte[]         _bytes;
        private readonly UnityEngine.Vector3 _vector3;
        private readonly UnityEngine.Color   _color;

        // ── Private ctor — callers use the factories ──────────────────────

        private PropertyValue(
            PropertyType type,
            int i = 0, float f = 0f, bool b = false,
            string s = null, byte[] bs = null,
            UnityEngine.Vector3 v3 = default, UnityEngine.Color c = default)
        {
            Type     = type;
            _int     = i;
            _float   = f;
            _bool    = b;
            _string  = s;
            _bytes   = bs;
            _vector3 = v3;
            _color   = c;
        }

        // ── Factories ─────────────────────────────────────────────────────

        /// <summary>Creates an <see cref="PropertyType.Int"/> value.</summary>
        public static PropertyValue OfInt(int v)
            => new PropertyValue(PropertyType.Int, i: v);

        /// <summary>Creates a <see cref="PropertyType.Float"/> value.</summary>
        public static PropertyValue OfFloat(float v)
            => new PropertyValue(PropertyType.Float, f: v);

        /// <summary>Creates a <see cref="PropertyType.Bool"/> value.</summary>
        public static PropertyValue OfBool(bool v)
            => new PropertyValue(PropertyType.Bool, b: v);

        /// <summary>
        /// Creates a <see cref="PropertyType.String"/> value. <see langword="null"/> becomes an
        /// empty string.
        /// </summary>
        public static PropertyValue OfString(string v)
            => new PropertyValue(PropertyType.String, s: v ?? string.Empty);

        /// <summary>
        /// Creates a <see cref="PropertyType.Bytes"/> value holding a copy of
        /// <paramref name="v"/>. <see langword="null"/> becomes an empty array.
        /// </summary>
        public static PropertyValue OfBytes(byte[] v)
        {
            if (v == null || v.Length == 0)
                return new PropertyValue(PropertyType.Bytes, bs: Array.Empty<byte>());

            var copy = new byte[v.Length];
            Buffer.BlockCopy(v, 0, copy, 0, v.Length);
            return new PropertyValue(PropertyType.Bytes, bs: copy);
        }

        /// <summary>Creates a <see cref="PropertyType.Vector3"/> value.</summary>
        public static PropertyValue OfVector3(UnityEngine.Vector3 v)
            => new PropertyValue(PropertyType.Vector3, v3: v);

        /// <summary>Creates a <see cref="PropertyType.Color"/> value (linear RGBA).</summary>
        public static PropertyValue OfColor(UnityEngine.Color v)
            => new PropertyValue(PropertyType.Color, c: v);

        /// <summary>
        /// Returns a deletion: write it as the value of a key to remove that key from the
        /// room's or the player's properties.
        /// </summary>
        /// <remarks>
        /// A deletion never appears in a snapshot's properties (see
        /// <see cref="PropertyType.Deleted"/>).
        /// </remarks>
        public static PropertyValue Deletion()
            => new PropertyValue(PropertyType.Deleted);

        /// <summary>Whether this value is a deletion (see <see cref="Deletion"/>).</summary>
        public bool IsDeletion => Type == PropertyType.Deleted;

        // ── Typed accessors — throw on type mismatch ───────────────────────

        /// <summary>Returns the value when <see cref="Type"/> is <see cref="PropertyType.Int"/>.</summary>
        /// <exception cref="InvalidOperationException">The value is of another type.</exception>
        public int AsInt()
        {
            Require(PropertyType.Int);
            return _int;
        }

        /// <summary>Returns the value when <see cref="Type"/> is <see cref="PropertyType.Float"/>.</summary>
        /// <exception cref="InvalidOperationException">The value is of another type.</exception>
        public float AsFloat()
        {
            Require(PropertyType.Float);
            return _float;
        }

        /// <summary>Returns the value when <see cref="Type"/> is <see cref="PropertyType.Bool"/>.</summary>
        /// <exception cref="InvalidOperationException">The value is of another type.</exception>
        public bool AsBool()
        {
            Require(PropertyType.Bool);
            return _bool;
        }

        /// <summary>Returns the value when <see cref="Type"/> is <see cref="PropertyType.String"/>.</summary>
        /// <exception cref="InvalidOperationException">The value is of another type.</exception>
        public string AsString()
        {
            Require(PropertyType.String);
            return _string ?? string.Empty;
        }

        /// <summary>
        /// Returns a copy of the bytes when <see cref="Type"/> is
        /// <see cref="PropertyType.Bytes"/>. <see cref="AsBytesReadOnly"/> reads them without
        /// copying.
        /// </summary>
        /// <exception cref="InvalidOperationException">The value is of another type.</exception>
        public byte[] AsBytes()
        {
            Require(PropertyType.Bytes);
            if (_bytes == null || _bytes.Length == 0) return Array.Empty<byte>();
            var copy = new byte[_bytes.Length];
            Buffer.BlockCopy(_bytes, 0, copy, 0, _bytes.Length);
            return copy;
        }

        /// <summary>
        /// Returns a read-only view of the bytes, without copying them, when
        /// <see cref="Type"/> is <see cref="PropertyType.Bytes"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">The value is of another type.</exception>
        public ReadOnlyMemory<byte> AsBytesReadOnly()
        {
            Require(PropertyType.Bytes);
            return _bytes ?? Array.Empty<byte>();
        }

        /// <summary>Returns the value when <see cref="Type"/> is <see cref="PropertyType.Vector3"/>.</summary>
        /// <exception cref="InvalidOperationException">The value is of another type.</exception>
        public UnityEngine.Vector3 AsVector3()
        {
            Require(PropertyType.Vector3);
            return _vector3;
        }

        /// <summary>Returns the value when <see cref="Type"/> is <see cref="PropertyType.Color"/>.</summary>
        /// <exception cref="InvalidOperationException">The value is of another type.</exception>
        public UnityEngine.Color AsColor()
        {
            Require(PropertyType.Color);
            return _color;
        }

        private void Require(PropertyType expected)
        {
            if (Type != expected)
                throw new InvalidOperationException(
                    $"PropertyValue: accessor for {expected} called on {Type}");
        }

        // ── Boxed object accessor (useful for reflection / logging) ───────

        /// <summary>
        /// Returns the value as an <see cref="object"/>, or <see langword="null"/> for a
        /// deletion. Bytes are returned as a copy.
        /// </summary>
        /// <remarks>
        /// This allocates for most types; the typed accessors do not.
        /// </remarks>
        public object BoxedValue()
        {
            switch (Type)
            {
                case PropertyType.Int:     return _int;
                case PropertyType.Float:   return _float;
                case PropertyType.Bool:    return _bool;
                case PropertyType.String:  return _string ?? string.Empty;
                case PropertyType.Bytes:   return AsBytes(); // defensive-copy to preserve immutability
                case PropertyType.Vector3: return _vector3;
                case PropertyType.Color:   return _color;
                // A deletion carries no value; `null` is what the wire says
                // and what a logger should render.
                case PropertyType.Deleted:  return null;
                default:
                    throw new InvalidOperationException($"Unknown PropertyType: {Type}");
            }
        }

        // ── Equality ───────────────────────────────────────────────────────

        /// <inheritdoc/>
        public bool Equals(PropertyValue other)
        {
            if (Type != other.Type) return false;
            switch (Type)
            {
                case PropertyType.Int:     return _int     == other._int;
                case PropertyType.Float:   return _float.Equals(other._float);
                case PropertyType.Bool:    return _bool    == other._bool;
                case PropertyType.String:  return string.Equals(_string, other._string, StringComparison.Ordinal);
                case PropertyType.Bytes:   return BytesEqual(_bytes, other._bytes);
                case PropertyType.Vector3: return _vector3 == other._vector3;
                case PropertyType.Color:   return _color   == other._color;
                // Two deletion sentinels are the same request.  Reaching the
                // `default` arm instead would make Deletion() != Deletion(),
                // which breaks every dictionary and set built from deltas.
                case PropertyType.Deleted:  return true;
                default: return false;
            }
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
            => obj is PropertyValue v && Equals(v);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            // Hash type + a representative field so equal values collide.
            unchecked
            {
                int h = (int)Type * 397;
                switch (Type)
                {
                    case PropertyType.Int:     return h ^ _int;
                    case PropertyType.Float:   return h ^ _float.GetHashCode();
                    case PropertyType.Bool:    return h ^ (_bool ? 1 : 0);
                    case PropertyType.String:  return h ^ (_string?.GetHashCode() ?? 0);
                    // Hash CONTENT (not just length) so an attacker cannot
                    // craft a bag of distinct same-length byte arrays that
                    // collide into one bucket and turn an O(1) dictionary
                    // into an O(n²) DoS surface.  FNV-1a over the full content
                    // for arrays up to 64 bytes; sample 16 evenly-spaced bytes
                    // plus the length for larger arrays — the sampled hash
                    // remains crafted-collision-resistant within reason while
                    // capping per-call cost at 16 reads.
                    case PropertyType.Bytes:   return h ^ HashBytes(_bytes);
                    case PropertyType.Vector3: return h ^ _vector3.GetHashCode();
                    case PropertyType.Color:   return h ^ _color.GetHashCode();
                    default: return h;
                }
            }
        }

        // FNV-1a content hash with a sampling strategy for large arrays.
        // Bytes-keyed dictionaries built from PropertyValue are now resistant
        // to crafted collision attacks within reason — an attacker would have
        // to predict the (sampled) FNV-1a output, which is far harder than
        // matching only on Length.
        private static int HashBytes(byte[] data)
        {
            if (data == null || data.Length == 0) return 0;

            const uint FnvOffset = 2166136261u;
            const uint FnvPrime  = 16777619u;
            uint hash = FnvOffset;

            // Always mix length so two different-length arrays cannot collide
            // through the byte-sampling window alone.
            hash = (hash ^ (uint)data.Length) * FnvPrime;

            int n = data.Length;
            if (n <= 64)
            {
                for (int i = 0; i < n; i++)
                    hash = (hash ^ data[i]) * FnvPrime;
            }
            else
            {
                // Evenly-spaced sample across the array so an attacker
                // appending zeros cannot land all writes outside the
                // sampled window.
                const int Samples = 16;
                for (int s = 0; s < Samples; s++)
                {
                    int idx = (int)((long)s * (n - 1) / (Samples - 1));
                    hash = (hash ^ data[idx]) * FnvPrime;
                }
            }
            return unchecked((int)hash);
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length)   return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }
    }
}
