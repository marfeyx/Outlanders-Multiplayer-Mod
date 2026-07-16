using System;
using System.IO;
using System.Linq;
using System.Text;
using OutlandersMultiplayer.Core.Protocol;

namespace OutlandersMultiplayer.Core.Snapshots;

public sealed class SnapshotManifest
{
    public const int MaxSnapshotIdBytes = 64;
    public const int MaxSaveNameBytes = 256;
    public const int Sha256HexLength = 64;

    public string SnapshotId { get; set; } = string.Empty;
    public string SaveName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public int UncompressedBytes { get; set; }
    public int CompressedBytes { get; set; }
    public int ChunkSize { get; set; }
    public int ChunkCount { get; set; }

    public byte[] ToPayload()
    {
        Validate();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        ProtocolStrings.WriteBounded(writer, SnapshotId, MaxSnapshotIdBytes);
        ProtocolStrings.WriteBounded(writer, SaveName, MaxSaveNameBytes);
        ProtocolStrings.WriteBounded(writer, Sha256, Sha256HexLength);
        writer.Write(UncompressedBytes);
        writer.Write(CompressedBytes);
        writer.Write(ChunkSize);
        writer.Write(ChunkCount);
        writer.Flush();
        return stream.ToArray();
    }

    public static SnapshotManifest FromPayload(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var manifest = new SnapshotManifest
        {
            SnapshotId = ProtocolStrings.ReadBounded(reader, MaxSnapshotIdBytes),
            SaveName = ProtocolStrings.ReadBounded(reader, MaxSaveNameBytes),
            Sha256 = ProtocolStrings.ReadBounded(reader, Sha256HexLength),
            UncompressedBytes = reader.ReadInt32(),
            CompressedBytes = reader.ReadInt32(),
            ChunkSize = reader.ReadInt32(),
            ChunkCount = reader.ReadInt32()
        };

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Snapshot manifest contains trailing data.");
        }

        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SnapshotId) || Encoding.UTF8.GetByteCount(SnapshotId) > MaxSnapshotIdBytes)
        {
            throw new InvalidDataException("Snapshot ID is missing or too long.");
        }

        if (string.IsNullOrWhiteSpace(SaveName) || Encoding.UTF8.GetByteCount(SaveName) > MaxSaveNameBytes)
        {
            throw new InvalidDataException("Snapshot save name is missing or too long.");
        }

        if (Sha256.Length != Sha256HexLength || !Sha256.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Snapshot SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        if (UncompressedBytes < 0 || UncompressedBytes > SnapshotLimits.MaxUncompressedBytes)
        {
            throw new InvalidDataException(
                $"Snapshot uncompressed size must be between 0 and {SnapshotLimits.MaxUncompressedBytes} bytes.");
        }

        if (CompressedBytes <= 0 || CompressedBytes > SnapshotLimits.MaxCompressedBytes)
        {
            throw new InvalidDataException(
                $"Snapshot compressed size must be between 1 and {SnapshotLimits.MaxCompressedBytes} bytes.");
        }

        if (ChunkSize <= 0 || ChunkSize > SnapshotLimits.MaxChunkBytes)
        {
            throw new InvalidDataException(
                $"Snapshot chunk size must be between 1 and {SnapshotLimits.MaxChunkBytes} bytes.");
        }

        if (ChunkCount <= 0 || ChunkCount > SnapshotLimits.MaxChunkCount)
        {
            throw new InvalidDataException(
                $"Snapshot chunk count must be between 1 and {SnapshotLimits.MaxChunkCount}.");
        }

        var expectedChunkCount = ((long)CompressedBytes + ChunkSize - 1) / ChunkSize;
        if (expectedChunkCount != ChunkCount)
        {
            throw new InvalidDataException(
                $"Snapshot chunk count {ChunkCount} does not match compressed size {CompressedBytes} and chunk size {ChunkSize}.");
        }
    }
}
