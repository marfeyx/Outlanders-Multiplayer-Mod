using System.Collections.Generic;

namespace OutlandersMultiplayer.Core.Snapshots;

public sealed class SnapshotPackage
{
    public SnapshotPackage(SnapshotManifest manifest, IReadOnlyList<SnapshotChunk> chunks)
    {
        Manifest = manifest;
        Chunks = chunks;
    }

    public SnapshotManifest Manifest { get; }

    public IReadOnlyList<SnapshotChunk> Chunks { get; }
}
