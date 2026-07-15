using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OutlandersMultiplayer.Core.Protocol;
using OutlandersMultiplayer.Core.Relay;
using OutlandersMultiplayer.Core.Session;
using OutlandersMultiplayer.Core.Snapshots;
using OutlandersMultiplayer.Core.State;
using RelayServerHost = OutlandersMultiplayer.RelayServer.RelayServer;

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
            ("relay disconnects idle clients and remains available", RelayDisconnectsIdleClientsAndRemainsAvailable),
            ("relay connection times out without blocking", RelayConnectionTimesOutWithoutBlocking),
            ("relay connection sends join frame asynchronously", RelayConnectionSendsJoinFrameAsynchronously),
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

    private static void RelayConnectionTimesOutWithoutBlocking()
    {
        var neverCompletes = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new TcpRelayTransport(
            TimeSpan.FromMilliseconds(75),
            (_, _, _) => neverCompletes.Task);
        var state = new SessionState();
        var callbackThread = -1;
        string? failure = null;
        transport.ConnectionFailed += message =>
        {
            callbackThread = Thread.CurrentThread.ManagedThreadId;
            failure = message;
            state.SetError(message);
        };

        for (var attempt = 0; attempt < 3; attempt++)
        {
            failure = null;
            state.SetStatus(SessionStatus.Joining, "Connecting to relay...");
            var stopwatch = Stopwatch.StartNew();
            transport.Connect("unreachable.test", 17668, CreateRelayJoinRequest());
            stopwatch.Stop();

            Assert(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), "relay Connect should return immediately");
            Assert(transport.IsConnecting, "relay transport should report connection progress");
            if (attempt == 0)
            {
                Thread.Sleep(150);
                Assert(failure == null, "relay failure callbacks should wait for Poll");
                Assert(state.Status == SessionStatus.Joining, "session should show connection progress until Poll handles failure");
            }

            WaitUntil(() => failure != null, TimeSpan.FromSeconds(2), transport.Poll, "relay timeout callback was not delivered");
            Assert(failure!.Contains("timed out", StringComparison.OrdinalIgnoreCase), "relay timeout should be actionable");
            Assert(state.Status == SessionStatus.Error, "relay timeout should become a recoverable session error");
            Assert(callbackThread == Thread.CurrentThread.ManagedThreadId, "relay failure callback should run from Poll");
            WaitUntil(() => transport.ActiveWorkerCount == 0, TimeSpan.FromSeconds(2), () => { }, "relay timeout worker did not terminate");
            Assert(!transport.IsConnecting && !transport.IsRunning, "failed relay attempt should release its connection state");
        }
    }

    private static void RelayConnectionSendsJoinFrameAsynchronously()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();
        using var transport = new TcpRelayTransport(TimeSpan.FromSeconds(2));
        var connected = false;
        string? failure = null;
        transport.Connected += () => connected = true;
        transport.ConnectionFailed += message => failure = message;

        transport.Connect("127.0.0.1", port, CreateRelayJoinRequest());
        WaitUntil(() => connected || failure != null, TimeSpan.FromSeconds(2), transport.Poll, "relay connection callback was not delivered");
        Assert(connected, $"loopback relay should connect successfully: {failure}");

        using var serverClient = acceptTask.GetAwaiter().GetResult();
        var joinFrame = RelayFrame.Read(serverClient.GetStream());
        var joinRequest = RelayJoinRequest.FromPayload(joinFrame.Payload);
        Assert(joinFrame.Type == RelayFrameType.Join, "relay connection should send a join frame first");
        Assert(joinRequest.Role == RelayRole.Client, "relay join role mismatch");
        Assert(joinRequest.RoomCode == "ROOM123", "relay join room mismatch");

        transport.Stop();
        WaitUntil(() => transport.ActiveWorkerCount == 0, TimeSpan.FromSeconds(2), () => { }, "stopped relay worker did not terminate");
        Assert(!transport.IsConnecting && !transport.IsRunning, "stopped relay should release its socket state");
    }

    private static RelayJoinRequest CreateRelayJoinRequest()
    {
        return new RelayJoinRequest
        {
            Role = RelayRole.Client,
            RoomCode = "ROOM123",
            SessionKey = "secret",
            PlayerName = "Test Player"
        };
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, Action tick, string failureMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < timeout)
        {
            tick();
            Thread.Sleep(5);
        }

        tick();
        Assert(condition(), failureMessage);
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

    private static void RelayDisconnectsIdleClientsAndRemainsAvailable()
    {
        RelayDisconnectsIdleClientsAndRemainsAvailableAsync().GetAwaiter().GetResult();
    }

    private static async Task RelayDisconnectsIdleClientsAndRemainsAvailableAsync()
    {
        using var shutdown = new CancellationTokenSource();
        var server = new RelayServerHost(
            port: 0,
            handshakeTimeout: TimeSpan.FromMilliseconds(200),
            clientReadTimeout: TimeSpan.FromMilliseconds(250));
        var serverTask = server.RunAsync(shutdown.Token);

        try
        {
            Assert(server.ListeningPort > 0, "relay did not bind a test port");

            using (var idleClient = new TcpClient())
            {
                await idleClient.ConnectAsync(IPAddress.Loopback, server.ListeningPort);
                Assert(
                    await WaitForDisconnectAsync(idleClient, TimeSpan.FromSeconds(3)),
                    "client that sent no Join frame remained connected");
            }

            using (var validClient = new TcpClient())
            {
                validClient.ReceiveTimeout = 3_000;
                await validClient.ConnectAsync(IPAddress.Loopback, server.ListeningPort);
                var stream = validClient.GetStream();
                var join = new RelayJoinRequest
                {
                    Role = RelayRole.Host,
                    RoomCode = "TIMEOUT1",
                    SessionKey = "secret",
                    PlayerName = "Timeout Test Host"
                };

                RelayFrame.Write(stream, new RelayFrame(RelayFrameType.Join, join.ToPayload()));
                var status = RelayFrame.Read(stream);
                Assert(status.Type == RelayFrameType.Status, "valid client was not accepted after idle timeout");
                Assert(status.GetUtf8Payload() == "relay-connected", "valid client received unexpected relay status");
                Assert(
                    await WaitForDisconnectAsync(validClient, TimeSpan.FromSeconds(3)),
                    "joined client remained connected after its read timeout");
            }

            await WaitUntilAsync(
                () => server.ActiveConnectionCount == 0,
                TimeSpan.FromSeconds(3),
                "timed-out relay connections were not cleaned up");
        }
        finally
        {
            shutdown.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static async Task<bool> WaitForDisconnectAsync(TcpClient client, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        var buffer = new byte[1];
        try
        {
            return await client.GetStream().ReadAsync(buffer, timeoutSource.Token) == 0;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(failureMessage);
            }

            await Task.Delay(25);
        }
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
