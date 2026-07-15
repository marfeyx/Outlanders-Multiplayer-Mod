using System.IO;
using System.Text;

namespace OutlandersMultiplayer.Core.Protocol;

public sealed class StateHashReport
{
    public string SnapshotId { get; set; } = string.Empty;
    public string SaveHash { get; set; } = string.Empty;

    public byte[] ToPayload()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        ProtocolStrings.WriteBounded(writer, SnapshotId, 128);
        ProtocolStrings.WriteBounded(writer, SaveHash, 128);
        writer.Flush();
        return stream.ToArray();
    }

    public static StateHashReport FromPayload(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        return new StateHashReport
        {
            SnapshotId = ProtocolStrings.ReadBounded(reader, 128),
            SaveHash = ProtocolStrings.ReadBounded(reader, 128)
        };
    }
}
