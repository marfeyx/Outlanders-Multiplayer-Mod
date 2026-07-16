using System.IO;
using System.Text;

namespace OutlandersMultiplayer.Core.Protocol;

public sealed class HandshakeResponse
{
    public bool Accepted { get; set; }
    public string Reason { get; set; } = string.Empty;
    public uint AssignedPlayerId { get; set; }
    public string HostSaveHash { get; set; } = string.Empty;
    public bool SnapshotRequired { get; set; }

    public byte[] ToPayload()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Accepted);
        writer.Write(AssignedPlayerId);
        ProtocolStrings.WriteBounded(writer, Reason, 512);
        ProtocolStrings.WriteBounded(writer, HostSaveHash, 128);
        writer.Write(SnapshotRequired);
        writer.Flush();
        return stream.ToArray();
    }

    public static HandshakeResponse FromPayload(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var response = new HandshakeResponse
        {
            Accepted = reader.ReadBoolean(),
            AssignedPlayerId = reader.ReadUInt32(),
            Reason = ProtocolStrings.ReadBounded(reader, 512)
        };

        if (stream.Position < stream.Length)
        {
            response.HostSaveHash = ProtocolStrings.ReadBounded(reader, 128);
            response.SnapshotRequired = reader.ReadBoolean();
        }

        return response;
    }
}
