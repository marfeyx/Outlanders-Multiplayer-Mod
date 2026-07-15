using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OutlandersMultiplayer.Core.Protocol;
using OutlandersMultiplayer.Core.Relay;
using OutlandersMultiplayer.Core.Snapshots;
using OutlandersMultiplayer.Core.State;

namespace OutlandersMultiplayer.Tests;

public static class TestRunner
{
    public static void Main()
    {
        var tests = new List<(string Name, Action Body)>
        {
            ("protocol envelope round-trips", ProtocolEnvelopeRoundTrips),
            ("handshake rejects wrong build", HandshakeRejectsWrongBuild),
            ("duplicate sequence filter rejects duplicates", DuplicateSequenceFilterRejectsDuplicates),
            ("snapshot chunks reassemble and validate", SnapshotChunksReassembleAndValidate),
            ("snapshot corruption is rejected", SnapshotCorruptionIsRejected),
            ("hosting save selection excludes unsafe paths", HostingSaveSelectionExcludesUnsafePaths),
            ("relay join and protocol frames round-trip", RelayFramesRoundTrip),
            ("relay frames tolerate fragmented reads", RelayFramesTolerateFragmentedReads),
            ("relay frames reject truncated bodies", RelayFramesRejectTruncatedBodies),
            ("join code contains relay room and secret", JoinCodeRoundTrips),
            ("join code escapes delimiters and backslashes", JoinCodeSpecialCharactersRoundTrip)
        };

        var failed = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Body();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"FAIL {test.Name}: {ex.Message}");
            }
        }

        if (failed > 0)
        {
            Environment.ExitCode = 1;
        }
    }

    private static void ProtocolEnvelopeRoundTrips()
    {
        var original = new ProtocolEnvelope(ProtocolMessageType.TextStatus, 42, Encoding.UTF8.GetBytes("hello"));
        var unpacked = ProtocolSerializer.Unpack(ProtocolSerializer.Pack(original));

        Assert(unpacked.Type == original.Type, "type mismatch");
        Assert(unpacked.Sequence == original.Sequence, "sequence mismatch");
        Assert(Encoding.UTF8.GetString(unpacked.Payload) == "hello", "payload mismatch");
    }

    private static void HandshakeRejectsWrongBuild()
    {
        var response = HandshakeValidator.ValidateForHost(new HandshakeRequest
        {
            OutlandersBuildGuid = "wrong",
            UnityVersion = ProtocolConstants.ExpectedUnityVersion
        }, expectedSessionKey: string.Empty);

        Assert(!response.Accepted, "wrong build should be rejected");
    }

    private static void DuplicateSequenceFilterRejectsDuplicates()
    {
        var filter = new DuplicateSequenceFilter();
        Assert(filter.Accept(10), "first sequence should be accepted");
        Assert(!filter.Accept(10), "duplicate sequence should be rejected");
    }

    private static void SnapshotChunksReassembleAndValidate()
    {
        var bytes = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        var package = SnapshotService.Create("Endless_0.dat", bytes, chunkSize: 4096);
        var restored = SnapshotService.Reassemble(package.Manifest, package.Chunks.Reverse());

        Assert(restored.SequenceEqual(bytes), "restored snapshot does not match original");
        Assert(package.Manifest.Sha256 == Hashing.Sha256Hex(bytes), "hash mismatch");
    }

    private static void SnapshotCorruptionIsRejected()
    {
        var bytes = Encoding.UTF8.GetBytes("save-data");
        var package = SnapshotService.Create("Endless_0.dat", bytes, chunkSize: 4);
        package.Chunks[0].Data[0] ^= 0x7F;

        var rejected = false;
        try
        {
            SnapshotService.Reassemble(package.Manifest, package.Chunks);
        }
        catch
        {
            rejected = true;
        }

        Assert(rejected, "corrupt snapshot should be rejected");
    }

    private static void HostingSaveSelectionExcludesUnsafePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"OutlandersMultiplayer.SaveSelection.{Guid.NewGuid():N}");
        try
        {
            var firstUser = Directory.CreateDirectory(Path.Combine(root, "user-first")).FullName;
            var secondUser = Directory.CreateDirectory(Path.Combine(root, "user-second")).FullName;
            var activeSave = WriteSave(firstUser, "Endless_Active.dat", "active", DateTime.UtcNow.AddHours(-4));
            var otherUserSave = WriteSave(secondUser, "Endless_Other.dat", "other", DateTime.UtcNow.AddHours(-1));
            var staleFolder = Directory.CreateDirectory(Path.Combine(firstUser, "Backups")).FullName;
            var staleSave = WriteSave(staleFolder, "Endless_Stale.dat", "stale", DateTime.UtcNow);
            var tempFolder = Directory.CreateDirectory(Path.Combine(firstUser, TempSaveWriter.MultiplayerSlotFolder)).FullName;
            var tempSave = WriteSave(tempFolder, "Endless_Temp.dat", "temp", DateTime.UtcNow.AddHours(1));
            WriteSave(firstUser, "Endless_Active.dat.backup", "backup", DateTime.UtcNow.AddHours(2));

            var activeSelection = HostingSaveSelector.Discover(root, activeSave);
            Assert(activeSelection.SelectedPath == activeSave, "active normal save should be preferred across users");
            Assert(activeSelection.Candidates.Count == 2, "only top-level normal saves should be eligible");
            Assert(activeSelection.Candidates.Contains(activeSave), "active save is missing from candidates");
            Assert(activeSelection.Candidates.Contains(otherUserSave), "other user's normal save should remain explicitly selectable");
            Assert(!activeSelection.Candidates.Contains(staleSave), "backup-folder save should not be eligible");
            Assert(!activeSelection.Candidates.Contains(tempSave), "multiplayer temp save should not be eligible");

            var ambiguousSelection = HostingSaveSelector.Discover(root);
            Assert(ambiguousSelection.SelectedPath == null, "multiple saves should require explicit selection");
            Assert(ambiguousSelection.Error.Contains("Select the exact save", StringComparison.Ordinal), "ambiguous selection should explain how to proceed");

            var tempSelection = HostingSaveSelector.Discover(root, tempSave);
            Assert(tempSelection.SelectedPath == null, "temp save must not be accepted as active");
            Assert(tempSelection.Error.Contains("not an eligible", StringComparison.Ordinal), "invalid active save should produce a clear error");

            var missingSelection = HostingSaveSelector.Discover(root, Path.Combine(firstUser, "Endless_Missing.dat"));
            Assert(missingSelection.SelectedPath == null, "missing active save must not be accepted");
            Assert(missingSelection.Error.Contains("not an eligible", StringComparison.Ordinal), "missing active save should produce a clear error");

            var singleRoot = Directory.CreateDirectory(Path.Combine(root, "single-root")).FullName;
            var singleUser = Directory.CreateDirectory(Path.Combine(singleRoot, "user-only")).FullName;
            var onlySave = WriteSave(singleUser, "Endless_Only.dat", "only", DateTime.UtcNow);
            var singleSelection = HostingSaveSelector.Discover(singleRoot);
            Assert(singleSelection.SelectedPath == onlySave, "one normal save should be selected without extra confirmation");
            Assert(string.IsNullOrEmpty(singleSelection.Error), "single-save selection should not report an error");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string WriteSave(string folder, string fileName, string contents, DateTime lastWriteUtc)
    {
        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return Path.GetFullPath(path);
    }

    private static void RelayFramesRoundTrip()
    {
        var join = new RelayJoinRequest
        {
            Role = RelayRole.Host,
            RoomCode = "WORLD123",
            SessionKey = "secret",
            PlayerName = "Host"
        };

        var joinAgain = RelayJoinRequest.FromPayload(join.ToPayload());
        Assert(joinAgain.Role == RelayRole.Host, "relay role mismatch");
        Assert(joinAgain.RoomCode == "WORLD123", "room code mismatch");
        Assert(joinAgain.SessionKey == "secret", "session key mismatch");

        using var stream = new MemoryStream();
        var envelope = new ProtocolEnvelope(ProtocolMessageType.TextStatus, 7, Encoding.UTF8.GetBytes("ok"));
        RelayFrame.Write(stream, new RelayFrame(RelayFrameType.Protocol, ProtocolSerializer.Pack(envelope)));
        stream.Position = 0;
        var frame = RelayFrame.Read(stream);
        var restored = ProtocolSerializer.Unpack(frame.Payload);

        Assert(frame.Type == RelayFrameType.Protocol, "relay frame type mismatch");
        Assert(restored.Sequence == 7, "relay protocol sequence mismatch");
    }

    private static void RelayFramesTolerateFragmentedReads()
    {
        var payload = Encoding.UTF8.GetBytes("fragmented relay payload");
        var bytes = SerializeRelayFrame(new RelayFrame(RelayFrameType.Protocol, payload));
        using var stream = new ChunkedReadStream(bytes, maxChunkSize: 2);

        var restored = RelayFrame.Read(stream);

        Assert(restored.Type == RelayFrameType.Protocol, "fragmented relay frame type mismatch");
        Assert(restored.Payload.SequenceEqual(payload), "fragmented relay frame payload mismatch");
    }

    private static void RelayFramesRejectTruncatedBodies()
    {
        var bytes = SerializeRelayFrame(new RelayFrame(RelayFrameType.Protocol, Encoding.UTF8.GetBytes("complete payload")));
        var declaredLength = BitConverter.ToInt32(bytes, 0);
        BitConverter.GetBytes(declaredLength + 5).CopyTo(bytes, 0);

        var rejected = false;
        using var stream = new ChunkedReadStream(bytes, maxChunkSize: 3);
        try
        {
            RelayFrame.Read(stream);
        }
        catch (EndOfStreamException ex)
        {
            rejected = ex.Message.Contains("relay frame body", StringComparison.Ordinal);
        }

        Assert(rejected, "relay frame with a truncated body should fail with a deterministic EOF error");
    }

    private static byte[] SerializeRelayFrame(RelayFrame frame)
    {
        using var stream = new MemoryStream();
        RelayFrame.Write(stream, frame);
        return stream.ToArray();
    }

    private static void JoinCodeRoundTrips()
    {
        var code = JoinCode.Encode("relay.example.net", 17668, "ROOM123", "SECRET456");
        Assert(code.StartsWith("OMP1:"), "join code prefix mismatch");
        Assert(JoinCode.TryDecode(code, out var decoded), "join code should decode");
        Assert(decoded.RelayHost == "relay.example.net", "relay host mismatch");
        Assert(decoded.RelayPort == 17668, "relay port mismatch");
        Assert(decoded.RoomCode == "ROOM123", "room code mismatch");
        Assert(decoded.SessionKey == "SECRET456", "session key mismatch");
    }

    private static void JoinCodeSpecialCharactersRoundTrip()
    {
        const string relayHost = @"relay|host\edge";
        const string roomCode = @"ROOM\1|A";
        const string sessionKey = @"SECRET|456\p";

        var code = JoinCode.Encode(relayHost, 17668, roomCode, sessionKey);
        Assert(JoinCode.TryDecode(code, out var decoded), "join code with special characters should decode");
        Assert(decoded.RelayHost == relayHost, "escaped relay host mismatch");
        Assert(decoded.RelayPort == 17668, "escaped join code relay port mismatch");
        Assert(decoded.RoomCode == roomCode, "escaped room code mismatch");
        Assert(decoded.SessionKey == sessionKey, "escaped session key mismatch");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed class ChunkedReadStream : Stream
{
    private readonly MemoryStream _inner;
    private readonly int _maxChunkSize;

    public ChunkedReadStream(byte[] bytes, int maxChunkSize)
    {
        _inner = new MemoryStream(bytes, writable: false);
        _maxChunkSize = maxChunkSize;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _inner.Read(buffer, offset, Math.Min(count, _maxChunkSize));
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
