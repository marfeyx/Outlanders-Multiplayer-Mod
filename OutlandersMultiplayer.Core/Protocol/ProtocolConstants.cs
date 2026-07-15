namespace OutlandersMultiplayer.Core.Protocol;

public static class ProtocolConstants
{
    public const uint Magic = 0x4F4D5031; // OMP1
    public const ushort ProtocolVersion = 1;
    public const int DefaultPort = 17667;
    public const int SnapshotChunkSize = 32 * 1024;
    public const int MaxPlayerNameBytes = 64;
    public const int MaxSessionKeyBytes = 128;
}
