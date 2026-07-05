using System.IO;
using System.Text;

namespace OutlandersMultiplayer.Core.Protocol;

public sealed class CommandEnvelope
{
    public ulong CommandId { get; set; }
    public uint PlayerId { get; set; }
    public long SimulationTick { get; set; }
    public string CommandType { get; set; } = string.Empty;
    public string JsonPayload { get; set; } = "{}";

    public byte[] ToPayload()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(CommandId);
        writer.Write(PlayerId);
        writer.Write(SimulationTick);
        ProtocolStrings.WriteBounded(writer, CommandType, 128);
        ProtocolStrings.WriteBounded(writer, JsonPayload, 32 * 1024);
        writer.Flush();
        return stream.ToArray();
    }

    public static CommandEnvelope FromPayload(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        return new CommandEnvelope
        {
            CommandId = reader.ReadUInt64(),
            PlayerId = reader.ReadUInt32(),
            SimulationTick = reader.ReadInt64(),
            CommandType = ProtocolStrings.ReadBounded(reader, 128),
            JsonPayload = ProtocolStrings.ReadBounded(reader, 32 * 1024)
        };
    }
}
