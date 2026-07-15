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
            ("relay join and protocol frames round-trip", RelayFramesRoundTrip),
            ("relay connection times out without blocking", RelayConnectionTimesOutWithoutBlocking),
            ("relay connection sends join frame asynchronously", RelayConnectionSendsJoinFrameAsynchronously),
            ("join code contains relay room and secret", JoinCodeRoundTrips)
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
