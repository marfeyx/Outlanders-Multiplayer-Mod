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
        if (uncompressedSaveBytes.Length > SnapshotLimits.MaxUncompressedBytes)
        {
            throw new InvalidDataException(
                $"Snapshot save exceeds the {SnapshotLimits.MaxUncompressedBytes}-byte uncompressed limit.");
        }

        if (chunkSize <= 0 || chunkSize > SnapshotLimits.MaxChunkBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkSize),
                $"Chunk size must be between 1 and {SnapshotLimits.MaxChunkBytes} bytes.");
        }

        var compressed = Compress(uncompressedSaveBytes);
        if (compressed.Length > SnapshotLimits.MaxCompressedBytes)
        {
            throw new InvalidDataException(
                $"Snapshot save exceeds the {SnapshotLimits.MaxCompressedBytes}-byte compressed limit.");
        }

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

        manifest.Validate();

        return new SnapshotPackage(manifest, chunks);
    }

    public static byte[] Reassemble(SnapshotManifest manifest, IEnumerable<SnapshotChunk> chunks)
    {
        if (manifest == null) throw new ArgumentNullException(nameof(manifest));
        if (chunks == null) throw new ArgumentNullException(nameof(chunks));
        manifest.Validate();

        var ordered = new byte[manifest.ChunkCount][];
        long accumulatedBytes = 0;
        foreach (var chunk in chunks)
        {
            ValidateChunk(manifest, chunk);

            if (ordered[chunk.Index] != null)
            {
                throw new InvalidDataException($"Snapshot chunk {chunk.Index} was received more than once.");
            }

            accumulatedBytes = checked(accumulatedBytes + chunk.Data.Length);
            if (accumulatedBytes > manifest.CompressedBytes ||
                accumulatedBytes > SnapshotLimits.MaxCompressedBytes)
            {
                throw new InvalidDataException("Accumulated snapshot chunks exceed the declared compressed size.");
            }

            ordered[chunk.Index] = chunk.Data;
        }

        if (accumulatedBytes != manifest.CompressedBytes)
        {
            throw new InvalidDataException("Snapshot chunks do not match the declared compressed size.");
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

        compressed.Position = 0;
        var saveBytes = Decompress(compressed, manifest.UncompressedBytes);
        var actualHash = Hashing.Sha256Hex(saveBytes);
        if (!StringComparer.OrdinalIgnoreCase.Equals(actualHash, manifest.Sha256))
        {
            throw new InvalidDataException("Snapshot hash validation failed.");
        }

        return saveBytes;
    }

    public static void ValidateChunk(SnapshotManifest manifest, SnapshotChunk chunk)
    {
        if (manifest == null) throw new ArgumentNullException(nameof(manifest));
        if (chunk == null) throw new ArgumentNullException(nameof(chunk));
        manifest.Validate();
        chunk.ValidateBasic();

        if (!StringComparer.Ordinal.Equals(chunk.SnapshotId, manifest.SnapshotId))
        {
            throw new InvalidDataException("Snapshot chunk belongs to a different snapshot.");
        }

        if (chunk.Index < 0 || chunk.Index >= manifest.ChunkCount)
        {
            throw new InvalidDataException("Snapshot chunk index is out of range.");
        }

        var expectedLength = chunk.Index == manifest.ChunkCount - 1
            ? manifest.CompressedBytes - (long)manifest.ChunkSize * (manifest.ChunkCount - 1)
            : manifest.ChunkSize;
        if (expectedLength <= 0 || expectedLength > SnapshotLimits.MaxChunkBytes || chunk.Data.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"Snapshot chunk {chunk.Index} has {chunk.Data.Length} bytes; expected {expectedLength}.");
        }
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

    private static byte[] Decompress(Stream input, int expectedSize)
    {
        if (input.Length > SnapshotLimits.MaxCompressedBytes)
        {
            throw new InvalidDataException("Compressed snapshot exceeds the configured limit.");
        }

        if (expectedSize < 0 || expectedSize > SnapshotLimits.MaxUncompressedBytes)
        {
            throw new InvalidDataException("Declared uncompressed snapshot size exceeds the configured limit.");
        }

        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(Math.Min(expectedSize, 1024 * 1024));
        var buffer = new byte[32 * 1024];
        var totalBytes = 0;
        while (true)
        {
            var bytesRead = gzip.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes = checked(totalBytes + bytesRead);
            if (totalBytes > expectedSize || totalBytes > SnapshotLimits.MaxUncompressedBytes)
            {
                throw new InvalidDataException(
                    "Decompressed snapshot exceeds its declared size or the configured uncompressed limit.");
            }

            output.Write(buffer, 0, bytesRead);
        }

        if (totalBytes != expectedSize)
        {
            throw new InvalidDataException(
                $"Decompressed snapshot has {totalBytes} bytes; expected {expectedSize}.");
        }

        return output.ToArray();
    }
}
