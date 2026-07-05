using System;
using System.IO;
using System.Text;

namespace OutlandersMultiplayer.Core.Relay;

public sealed class RelayFrame
{
    public RelayFrame(RelayFrameType type, byte[] payload)
    {
        Type = type;
        Payload = payload ?? Array.Empty<byte>();
    }

    public RelayFrameType Type { get; }
    public byte[] Payload { get; }

    public static void Write(Stream stream, RelayFrame frame)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        if (frame == null) throw new ArgumentNullException(nameof(frame));

        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)frame.Type);
            writer.Write(frame.Payload.Length);
            writer.Write(frame.Payload);
        }

        var bytes = payload.ToArray();
        using var streamWriter = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        streamWriter.Write(bytes.Length);
        streamWriter.Write(bytes);
        streamWriter.Flush();
    }

    public static RelayFrame Read(Stream stream)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var length = reader.ReadInt32();
        if (length <= 0 || length > 8 * 1024 * 1024)
        {
            throw new InvalidDataException("Relay frame length is invalid.");
        }

        using var payload = new MemoryStream(reader.ReadBytes(length), writable: false);
        using var payloadReader = new BinaryReader(payload, Encoding.UTF8, leaveOpen: false);
        var type = (RelayFrameType)payloadReader.ReadByte();
        var payloadLength = payloadReader.ReadInt32();
        if (payloadLength < 0 || payloadLength > payload.Length - payload.Position)
        {
            throw new InvalidDataException("Relay payload length is invalid.");
        }

        return new RelayFrame(type, payloadReader.ReadBytes(payloadLength));
    }

    public static RelayFrame Rejected(string reason)
    {
        return new RelayFrame(RelayFrameType.Rejected, Encoding.UTF8.GetBytes(reason ?? string.Empty));
    }

    public string GetUtf8Payload()
    {
        return Encoding.UTF8.GetString(Payload);
    }
}
