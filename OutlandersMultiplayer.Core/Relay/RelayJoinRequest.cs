using System.IO;
using System.Text;
using OutlandersMultiplayer.Core.Protocol;

namespace OutlandersMultiplayer.Core.Relay;

public sealed class RelayJoinRequest
{
    public const int MaxRoomCodeBytes = 48;

    public RelayRole Role { get; set; }
    public string RoomCode { get; set; } = string.Empty;
    public string SessionKey { get; set; } = string.Empty;
    public string PlayerName { get; set; } = "Player";

    public byte[] ToPayload()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((byte)Role);
        RelayStrings.WriteBounded(writer, RoomCode, MaxRoomCodeBytes);
        RelayStrings.WriteBounded(writer, SessionKey, ProtocolConstants.MaxSessionKeyBytes);
        RelayStrings.WriteBounded(writer, PlayerName, ProtocolConstants.MaxPlayerNameBytes);
        writer.Flush();
        return stream.ToArray();
    }

    public static RelayJoinRequest FromPayload(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        return new RelayJoinRequest
        {
            Role = (RelayRole)reader.ReadByte(),
            RoomCode = RelayStrings.ReadBounded(reader, MaxRoomCodeBytes),
            SessionKey = RelayStrings.ReadBounded(reader, ProtocolConstants.MaxSessionKeyBytes),
            PlayerName = RelayStrings.ReadBounded(reader, ProtocolConstants.MaxPlayerNameBytes)
        };
    }
}
