namespace OutlandersMultiplayer.Core.Snapshots;

public static class SnapshotLimits
{
    public const int MaxCompressedBytes = 32 * 1024 * 1024;
    public const int MaxUncompressedBytes = 64 * 1024 * 1024;
    public const int MaxChunkBytes = 64 * 1024;
    public const int MaxChunkCount = 2048;
}
