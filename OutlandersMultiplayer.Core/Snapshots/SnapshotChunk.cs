using System;
using System.IO;
using System.Text;
using OutlandersMultiplayer.Core.Protocol;

namespace OutlandersMultiplayer.Core.Snapshots;

public sealed class SnapshotChunk
{
    public string SnapshotId { get; set; } = string.Empty;
    public int Index { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public byte[] ToPayload()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        ProtocolStrings.WriteBounded(writer, SnapshotId, 64);
        writer.Write(Index);
        writer.Write(Data.Length);
        writer.Write(Data);
        writer.Flush();
        return stream.ToArray();
    }

    public static SnapshotChunk FromPayload(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var snapshotId = ProtocolStrings.ReadBounded(reader, 64);
        var index = reader.ReadInt32();
        var length = reader.ReadInt32();
        if (length < 0 || length > stream.Length - stream.Position)
        {
            throw new InvalidDataException("Snapshot chunk length is invalid.");
        }

        return new SnapshotChunk
        {
            SnapshotId = snapshotId,
            Index = index,
            Data = reader.ReadBytes(length)
        };
    }
}
