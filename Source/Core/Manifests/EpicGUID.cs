using System.Buffers.Binary;
using System.Globalization;

namespace Unvault.Core.Manifests;

/// <summary>
/// Epic's 128-bit GUID, stored as four little-endian uint32s (UE's FGuid).
/// Rendered as 32 uppercase hex chars, the way chunk file names and EGL's .item files show it.
/// </summary>
public readonly record struct EpicGUID(uint A, uint B, uint C, uint D)
{
    public static readonly EpicGUID Empty = default;

    public bool IsEmpty => this == Empty;

    public override string ToString() => $"{A:X8}{B:X8}{C:X8}{D:X8}";

    public static EpicGUID Parse(string hex)
    {
        if (hex.Length != 32)
            throw new FormatException($"Expected 32 hex characters, got '{hex}'.");
        return new EpicGUID(
            uint.Parse(hex.AsSpan(0, 8), NumberStyles.HexNumber),
            uint.Parse(hex.AsSpan(8, 8), NumberStyles.HexNumber),
            uint.Parse(hex.AsSpan(16, 8), NumberStyles.HexNumber),
            uint.Parse(hex.AsSpan(24, 8), NumberStyles.HexNumber));
    }

    /// <summary>The 16 bytes as stored on disk (four little-endian uint32s).</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), A);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), B);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), C);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), D);
        return bytes;
    }

    internal static EpicGUID Read(BinaryReader reader) =>
        new(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
}
