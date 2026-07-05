using System.IO;
using System.Text;

namespace OutlandersMultiplayer.Core.Protocol;

public sealed class HandshakeRequest
{
    public string PlayerName { get; set; } = "Player";
    public string SessionKey { get; set; } = string.Empty;
    public string OutlandersBuildGuid { get; set; } = ProtocolConstants.ExpectedOutlandersBuildGuid;
    public string UnityVersion { get; set; } = ProtocolConstants.ExpectedUnityVersion;
    public string SaveHash { get; set; } = string.Empty;

    public byte[] ToPayload()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        ProtocolStrings.WriteBounded(writer, PlayerName, ProtocolConstants.MaxPlayerNameBytes);
        ProtocolStrings.WriteBounded(writer, SessionKey, ProtocolConstants.MaxSessionKeyBytes);
        ProtocolStrings.WriteBounded(writer, OutlandersBuildGuid, 128);
        ProtocolStrings.WriteBounded(writer, UnityVersion, 32);
        ProtocolStrings.WriteBounded(writer, SaveHash, 128);
        writer.Flush();
        return stream.ToArray();
    }

    public static HandshakeRequest FromPayload(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        return new HandshakeRequest
        {
            PlayerName = ProtocolStrings.ReadBounded(reader, ProtocolConstants.MaxPlayerNameBytes),
            SessionKey = ProtocolStrings.ReadBounded(reader, ProtocolConstants.MaxSessionKeyBytes),
            OutlandersBuildGuid = ProtocolStrings.ReadBounded(reader, 128),
            UnityVersion = ProtocolStrings.ReadBounded(reader, 32),
            SaveHash = ProtocolStrings.ReadBounded(reader, 128)
        };
    }
}
