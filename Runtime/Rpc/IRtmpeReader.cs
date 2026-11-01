// RTMPE SDK — Runtime/Rpc/IRtmpeReader.cs
//
// Narrow read API exposed to INetworkSerializable implementations.
// Symmetric with IRtmpeWriter — every write method has a corresponding read.
//
// All multi-byte primitives are little-endian.  On truncated input the reader
// returns a default-valued primitive AND raises a sticky failure flag so the
// outer dispatch (RpcSerializer.ReadParam) can short-circuit and discard the
// rest of the parameter stream cleanly.

using UnityEngine;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Reads the values an <see cref="IRtmpeWriter"/> wrote, in
    /// <see cref="INetworkSerializable.NetworkDeserialize"/>. Each read method matches the
    /// write method of the same type.
    /// </summary>
    /// <remarks>
    /// A read past the end of the data sets <see cref="HasFailed"/> and returns a default
    /// value (<c>0</c>, <c>false</c>, an empty string or array). You may keep reading; every
    /// later read also returns a default, and the argument is treated as unreadable, so the
    /// call is dropped.
    /// </remarks>
    public interface IRtmpeReader
    {
        /// <summary>
        /// Whether a read went past the end of the data, or a string was not valid UTF-8.
        /// Once set, it stays set, and every read returns a default value.
        /// </summary>
        bool HasFailed { get; }

        /// <summary>Reads a 32-bit signed integer.</summary>
        int ReadInt32();

        /// <summary>Reads a 32-bit floating-point number.</summary>
        float ReadFloat();

        /// <summary>Reads a Boolean.</summary>
        bool ReadBool();

        /// <summary>Reads a 64-bit unsigned integer.</summary>
        ulong ReadUInt64();

        /// <summary>Reads a 16-bit unsigned integer.</summary>
        ushort ReadUInt16();

        /// <summary>Reads a byte.</summary>
        byte ReadByte();

        /// <summary>
        /// Reads a string written with <see cref="IRtmpeWriter.WriteString"/>. Returns an empty
        /// string when the read fails.
        /// </summary>
        string ReadString();

        /// <summary>
        /// Reads a byte array written with <see cref="IRtmpeWriter.WriteBytes"/>. Returns an empty
        /// array when the read fails.
        /// </summary>
        byte[] ReadBytes();

        /// <summary>Reads a <see cref="Vector3"/>.</summary>
        Vector3 ReadVector3();

        /// <summary>Reads a <see cref="Quaternion"/>.</summary>
        Quaternion ReadQuaternion();

        /// <summary>Reads a <see cref="Color"/>.</summary>
        Color ReadColor();
    }
}
