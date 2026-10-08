using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using UnVault.Core.Manifests;

namespace UnVault.Core.Chunks;

/// <summary>A chunk file that isn't what the manifest describes: cut off, damaged on the way, or another chunk.</summary>
public sealed class ChunkFormatException(string message) : Exception(message);

/// <summary>
/// Turns a downloaded .chunk file into the raw data files are rebuilt from.
/// </summary>
/// <remarks>
/// Header, all little-endian:
/// <code>
/// v1 (41 B): magic u32 0xB1FE3AA2, header version u32, header size u32, data size u32,
///            GUID u32×4, rolling hash u64, stored-as u8 (1 = zlib, 2 = AES-GCM)
/// v2 (62 B): + SHA-1 of the uncompressed data [20], hash type u8
/// v3 (66 B): + uncompressed size u32
/// v4 (98 B): + secret GUID u32×4 (picks the AES key), AES-GCM tag [16]
/// </code>
/// Data starts at header size. Decrypt first (nonce = first 12 bytes of the SHA-1), then inflate.
/// </remarks>
public static class ChunkDecoder
{
    public const uint Magic = 0xB1FE3AA2;

    private const byte StoredCompressed = 0x1;
    private const byte StoredEncrypted = 0x2;

    /// <summary>
    /// Decodes <paramref name="file"/> into <paramref name="destination"/> (at least
    /// <see cref="ChunkInfo.WindowSize"/> bytes) and verifies it against the manifest's SHA-1.
    /// Returns the number of data bytes.
    /// </summary>
    public static int Decode(byte[] file, ChunkInfo expected, byte[] destination, IReadOnlyDictionary<string, string>? secrets = null) =>
        Decode(file, file.Length, expected, destination, secrets);

    /// <summary>
    /// The same, for a chunk file in the first <paramref name="length"/> bytes of <paramref name="file"/> (a pooled buffer).
    /// Anything wrong with the file, however it got damaged, is a <see cref="ChunkFormatException"/>.
    /// </summary>
    public static int Decode(byte[] file, int length, ChunkInfo expected, byte[] destination, IReadOnlyDictionary<string, string>? secrets = null)
    {
        var span = file.AsSpan(0, length);
        if (span.Length < 41 || ReadU32(span, 0) != Magic)
            throw new ChunkFormatException($"Chunk {expected.GUID}: not a chunk file.");

        uint headerVersion = ReadU32(span, 4);
        long headerSize = ReadU32(span, 8);
        long dataSize = ReadU32(span, 12);
        int fieldsEnd = headerVersion >= 4 ? 98 : headerVersion >= 3 ? 66 : headerVersion >= 2 ? 62 : 41;
        if (span.Length < fieldsEnd || headerSize < fieldsEnd)
            throw new ChunkFormatException($"Chunk {expected.GUID}: its header is cut off or damaged.");

        var guid = new EpicGUID(ReadU32(span, 16), ReadU32(span, 20), ReadU32(span, 24), ReadU32(span, 28));
        byte storedAs = span[40];
        var headerSHA1 = headerVersion >= 2 ? span.Slice(41, 20) : default;
        long uncompressedSize = headerVersion >= 3 ? ReadU32(span, 62) : expected.WindowSize;

        if (guid != expected.GUID)
            throw new ChunkFormatException($"Chunk {expected.GUID}: file contains chunk {guid}.");
        if (headerSize + dataSize > span.Length)
            throw new ChunkFormatException($"Chunk {expected.GUID}: truncated ({span.Length} of {headerSize + dataSize} bytes).");
        if (uncompressedSize > destination.Length)
            throw new ChunkFormatException($"Chunk {expected.GUID}: {uncompressedSize} bytes don't fit the {destination.Length}-byte buffer.");

        try
        {
            return DecodeData(file, span, (int)headerSize, (int)dataSize, (int)uncompressedSize, headerVersion, storedAs, headerSHA1, expected, destination, secrets);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or CryptographicException)
        {
            // Inflating or decrypting damaged data: like a hash mismatch, it's the file, not this program.
            throw new ChunkFormatException($"Chunk {expected.GUID}: damaged data ({ex.Message})");
        }
    }

    private static int DecodeData(
        byte[] file, ReadOnlySpan<byte> span, int headerSize, int dataSize, int uncompressedSize, uint headerVersion, byte storedAs,
        ReadOnlySpan<byte> headerSHA1, ChunkInfo expected, byte[] destination, IReadOnlyDictionary<string, string>? secrets)
    {
        byte[] payload = file;
        int payloadOffset = headerSize;
        if ((storedAs & StoredEncrypted) != 0)
        {
            if (headerVersion < 4)
                throw new ChunkFormatException($"Chunk {expected.GUID}: encrypted, but header v{headerVersion} has no key reference.");
            var secretGUID = new EpicGUID(ReadU32(span, 66), ReadU32(span, 70), ReadU32(span, 74), ReadU32(span, 78));
            payload = Decrypt(span.Slice(headerSize, dataSize), secretGUID, headerSHA1, span.Slice(82, 16), secrets, expected.GUID);
            payloadOffset = 0;
        }

        int length;
        if ((storedAs & StoredCompressed) != 0)
        {
            using var compressed = new MemoryStream(payload, payloadOffset, dataSize, writable: false);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            zlib.ReadExactly(destination, 0, uncompressedSize);
            length = uncompressedSize;
        }
        else
        {
            payload.AsSpan(payloadOffset, dataSize).CopyTo(destination);
            length = dataSize;
        }

        var expectedSHA1 = expected.SHA1.Length == 20 ? expected.SHA1.AsSpan() : headerSHA1;
        if (expectedSHA1.Length == 20 && !SHA1.HashData(destination.AsSpan(0, length)).AsSpan().SequenceEqual(expectedSHA1))
            throw new ChunkFormatException($"Chunk {expected.GUID}: data hash mismatch.");

        return length;
    }

    private static byte[] Decrypt(
        ReadOnlySpan<byte> ciphertext, EpicGUID secretGUID, ReadOnlySpan<byte> sha1, ReadOnlySpan<byte> tag,
        IReadOnlyDictionary<string, string>? secrets, EpicGUID chunkGUID)
    {
        string keyName = secretGUID.ToString();
        string? keyHex = null;
        if (secrets is not null && !secrets.TryGetValue(keyName, out keyHex))
            keyHex = secrets.FirstOrDefault(kv => kv.Key.Equals(keyName, StringComparison.OrdinalIgnoreCase)).Value;
        if (keyHex is null)
            throw new ChunkFormatException($"Chunk {chunkGUID}: encrypted with key {keyName}, which the build info didn't provide.");

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(Convert.FromHexString(keyHex), tagSizeInBytes: 16);
        aes.Decrypt(sha1[..12], ciphertext, tag, plaintext);
        return plaintext;
    }

    private static uint ReadU32(ReadOnlySpan<byte> span, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
}
