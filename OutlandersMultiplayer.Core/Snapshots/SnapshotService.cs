using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using OutlandersMultiplayer.Core.Protocol;
using OutlandersMultiplayer.Core.State;

namespace OutlandersMultiplayer.Core.Snapshots;

public static class SnapshotService
{
    public static SnapshotPackage Create(string saveName, byte[] uncompressedSaveBytes, int chunkSize = ProtocolConstants.SnapshotChunkSize)
    {
        if (uncompressedSaveBytes == null) throw new ArgumentNullException(nameof(uncompressedSaveBytes));
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));

        var compressed = Compress(uncompressedSaveBytes);
        var snapshotId = Guid.NewGuid().ToString("N");
        var chunks = new List<SnapshotChunk>();

        for (var offset = 0; offset < compressed.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, compressed.Length - offset);
            var data = new byte[length];
            Buffer.BlockCopy(compressed, offset, data, 0, length);
            chunks.Add(new SnapshotChunk { SnapshotId = snapshotId, Index = chunks.Count, Data = data });
        }

        var manifest = new SnapshotManifest
        {
            SnapshotId = snapshotId,
            SaveName = saveName,
            Sha256 = Hashing.Sha256Hex(uncompressedSaveBytes),
            UncompressedBytes = uncompressedSaveBytes.Length,
            CompressedBytes = compressed.Length,
            ChunkSize = chunkSize,
            ChunkCount = chunks.Count
        };

        return new SnapshotPackage(manifest, chunks);
    }

    public static byte[] Reassemble(SnapshotManifest manifest, IEnumerable<SnapshotChunk> chunks)
    {
        if (manifest == null) throw new ArgumentNullException(nameof(manifest));
        if (chunks == null) throw new ArgumentNullException(nameof(chunks));

        var ordered = new byte[manifest.ChunkCount][];
        foreach (var chunk in chunks)
        {
            if (chunk.SnapshotId != manifest.SnapshotId)
            {
                throw new InvalidDataException("Snapshot chunk belongs to a different snapshot.");
            }

            if (chunk.Index < 0 || chunk.Index >= manifest.ChunkCount)
            {
                throw new InvalidDataException("Snapshot chunk index is out of range.");
            }

            ordered[chunk.Index] = chunk.Data;
        }

        using var compressed = new MemoryStream(manifest.CompressedBytes);
        for (var i = 0; i < ordered.Length; i++)
        {
            if (ordered[i] == null)
            {
                throw new InvalidDataException($"Snapshot chunk {i} is missing.");
            }

            compressed.Write(ordered[i], 0, ordered[i].Length);
        }

        var saveBytes = Decompress(compressed.ToArray(), manifest.UncompressedBytes);
        var actualHash = Hashing.Sha256Hex(saveBytes);
        if (!StringComparer.OrdinalIgnoreCase.Equals(actualHash, manifest.Sha256))
        {
            throw new InvalidDataException("Snapshot hash validation failed.");
        }

        return saveBytes;
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] bytes, int expectedSize)
    {
        using var input = new MemoryStream(bytes, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(Math.Max(expectedSize, 0));
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
