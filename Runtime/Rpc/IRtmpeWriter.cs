// RTMPE SDK — Runtime/Rpc/IRtmpeWriter.cs
//
// Narrow write API exposed to INetworkSerializable implementations.
//
// Why a dedicated interface (and not BinaryWriter):
//  • BinaryWriter writes strings with a 7-bit-encoded length prefix that is
//    incompatible with the rest of the RTMPE wire format (Go server expects
//    2-byte LE ushort length).  Forcing implementers to use BinaryWriter
//    would either invite that mismatch or require a manual byte-by-byte
//    encoding everywhere.
//  • Allows the SDK to swap the backing store (byte[] / Span<byte> /
//    pre-rented pool buffer) without breaking author-written serializers.
//
// All multi-byte primitives are little-endian to match the rest of RpcSerializer.

using UnityEngine;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Writes the values of an <see cref="INetworkSerializable"/> type, in
    /// <see cref="INetworkSerializable.NetworkSerialize"/>. Read them back, in the same order,
    /// with the matching <see cref="IRtmpeReader"/> methods.
    /// </summary>
    public interface IRtmpeWriter
    {
        /// <summary>Writes a 32-bit signed integer.</summary>
        void WriteInt32(int value);

        /// <summary>Writes a 32-bit floating-point number.</summary>
        void WriteFloat(float value);

        /// <summary>Writes a Boolean.</summary>
        void WriteBool(bool value);

        /// <summary>Writes a 64-bit unsigned integer.</summary>
        void WriteUInt64(ulong value);

        /// <summary>Writes a 16-bit unsigned integer.</summary>
        void WriteUInt16(ushort value);

        /// <summary>Writes a byte.</summary>
        void WriteByte(byte value);

        /// <summary>
        /// Writes a string of up to 65535 bytes of UTF-8. <see langword="null"/> is written as
        /// an empty string.
        /// </summary>
        /// <exception cref="System.ArgumentException">The string is longer than 65535 bytes of
        /// UTF-8.</exception>
        void WriteString(string value);

        /// <summary>
        /// Writes a byte array of up to 65535 bytes. <see langword="null"/> is written as an
        /// empty array.
        /// </summary>
        /// <exception cref="System.ArgumentException"><paramref name="value"/> is longer than
        /// 65535 bytes.</exception>
        void WriteBytes(byte[] value);

        /// <summary>Writes a <see cref="Vector3"/>.</summary>
        void WriteVector3(Vector3 value);

        /// <summary>Writes a <see cref="Quaternion"/>.</summary>
        void WriteQuaternion(Quaternion value);

        /// <summary>Writes a <see cref="Color"/>.</summary>
        void WriteColor(Color value);
    }
}
