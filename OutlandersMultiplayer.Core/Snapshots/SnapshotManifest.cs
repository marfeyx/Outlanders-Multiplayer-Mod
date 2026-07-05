using System.IO;
using System.Text;
using OutlandersMultiplayer.Core.Protocol;

namespace OutlandersMultiplayer.Core.Snapshots;

public sealed class SnapshotManifest
{
    public string SnapshotId { get; set; } = string.Empty;
    public string SaveName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public int UncompressedBytes { get; set; }
    public int CompressedBytes { get; set; }
    public int ChunkSize { get; set; }
    public int ChunkCount { get; set; }

    public byte[] ToPayload()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        ProtocolStrings.WriteBounded(writer, SnapshotId, 64);
        ProtocolStrings.WriteBounded(writer, SaveName, 256);
        ProtocolStrings.WriteBounded(writer, Sha256, 128);
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
        return new SnapshotManifest
        {
            SnapshotId = ProtocolStrings.ReadBounded(reader, 64),
            SaveName = ProtocolStrings.ReadBounded(reader, 256),
            Sha256 = ProtocolStrings.ReadBounded(reader, 128),
            UncompressedBytes = reader.ReadInt32(),
            CompressedBytes = reader.ReadInt32(),
            ChunkSize = reader.ReadInt32(),
            ChunkCount = reader.ReadInt32()
        };
    }
}
