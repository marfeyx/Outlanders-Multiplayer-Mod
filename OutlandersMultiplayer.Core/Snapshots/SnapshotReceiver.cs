using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OutlandersMultiplayer.Core.Snapshots;

public sealed class SnapshotReceiver
{
    private readonly Dictionary<int, SnapshotChunk> _chunks = new();
    private SnapshotManifest? _manifest;
    private long _compressedBytes;

    public SnapshotManifest? Manifest => _manifest;
    public int ReceivedChunkCount => _chunks.Count;
    public long ReceivedCompressedBytes => _compressedBytes;

    public void Begin(SnapshotManifest manifest)
    {
        if (manifest == null) throw new ArgumentNullException(nameof(manifest));

        if (_manifest != null)
        {
            Reset();
            throw new InvalidDataException("A snapshot manifest was received while another snapshot transfer was in progress.");
        }

        try
        {
            manifest.Validate();
            _manifest = manifest;
        }
        catch
        {
            Reset();
            throw;
        }
    }

    public SnapshotPackage? Add(SnapshotChunk chunk)
    {
        if (chunk == null) throw new ArgumentNullException(nameof(chunk));

        try
        {
            var manifest = _manifest ?? throw new InvalidDataException(
                "A snapshot chunk was received before its manifest.");
            SnapshotService.ValidateChunk(manifest, chunk);

            if (_chunks.ContainsKey(chunk.Index))
            {
                throw new InvalidDataException($"Snapshot chunk {chunk.Index} was received more than once.");
            }

            var nextCompressedBytes = checked(_compressedBytes + chunk.Data.Length);
            if (nextCompressedBytes > manifest.CompressedBytes ||
                nextCompressedBytes > SnapshotLimits.MaxCompressedBytes)
            {
                throw new InvalidDataException("Accumulated snapshot chunks exceed the declared compressed size.");
            }

            _chunks.Add(chunk.Index, chunk);
            _compressedBytes = nextCompressedBytes;
            if (_chunks.Count < manifest.ChunkCount)
            {
                return null;
            }

            if (_compressedBytes != manifest.CompressedBytes)
            {
                throw new InvalidDataException("Snapshot chunks do not match the declared compressed size.");
            }

            var package = new SnapshotPackage(
                manifest,
                _chunks.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray());
            Reset();
            return package;
        }
        catch
        {
            Reset();
            throw;
        }
    }

    public void Reset()
    {
        _manifest = null;
        _chunks.Clear();
        _compressedBytes = 0;
    }
}
