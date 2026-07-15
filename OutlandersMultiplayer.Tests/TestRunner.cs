using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OutlandersMultiplayer.Core.Protocol;
using OutlandersMultiplayer.Core.Relay;
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
            ("relay join and protocol frames round-trip", RelayFramesRoundTrip),
            ("relay disconnects idle clients and remains available", RelayDisconnectsIdleClientsAndRemainsAvailable),
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
