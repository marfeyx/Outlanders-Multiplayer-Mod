using System;
using System.IO;
using System.Text;

namespace OutlandersMultiplayer.Core.Protocol;

public static class ProtocolSerializer
{
    public static byte[] Pack(ProtocolEnvelope envelope)
    {
        if (envelope == null) throw new ArgumentNullException(nameof(envelope));

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(ProtocolConstants.Magic);
        writer.Write(ProtocolConstants.ProtocolVersion);
        writer.Write((byte)envelope.Type);
        writer.Write(envelope.Sequence);
        writer.Write(envelope.Payload.Length);
        writer.Write(envelope.Payload);
        writer.Flush();
        return stream.ToArray();
    }

    public static ProtocolEnvelope Unpack(byte[] bytes)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        var magic = reader.ReadUInt32();
        if (magic != ProtocolConstants.Magic)
        {
            throw new InvalidDataException("Packet magic did not match Outlanders multiplayer protocol.");
        }

        var version = reader.ReadUInt16();
        if (version != ProtocolConstants.ProtocolVersion)
        {
            throw new InvalidDataException($"Unsupported protocol version {version}.");
        }

        var type = (ProtocolMessageType)reader.ReadByte();
        var sequence = reader.ReadUInt32();
        var length = reader.ReadInt32();
        if (length < 0 || length > stream.Length - stream.Position)
        {
            throw new InvalidDataException("Packet payload length is invalid.");
        }

        return new ProtocolEnvelope(type, sequence, reader.ReadBytes(length));
    }
}
