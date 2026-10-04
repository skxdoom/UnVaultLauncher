using System.Buffers.Binary;
using System.Globalization;
using System.Text;

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

    /// <summary>The same, from UTF-8 text (e.g. straight out of a JSON manifest, without making a string first).</summary>
    public static EpicGUID Parse(ReadOnlySpan<byte> utf8Hex)
    {
        if (utf8Hex.Length != 32)
            throw new FormatException($"Expected 32 hex characters, got '{Encoding.UTF8.GetString(utf8Hex)}'.");
        return new EpicGUID(Hex(utf8Hex[..8]), Hex(utf8Hex[8..16]), Hex(utf8Hex[16..24]), Hex(utf8Hex[24..]));

        static uint Hex(ReadOnlySpan<byte> part) => uint.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
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
