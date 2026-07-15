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
            ("direct live sync accepts and applies commands once", DirectLiveSyncAcceptsAndAppliesOnce),
            ("relay live sync routes accepted commands once", RelayLiveSyncRoutesAcceptedCommandsOnce),
            ("live sync state hashes detect divergence", LiveSyncStateHashesDetectDivergence),
            ("snapshot chunks reassemble and validate", SnapshotChunksReassembleAndValidate),
            ("snapshot corruption is rejected", SnapshotCorruptionIsRejected),
            ("relay join and protocol frames round-trip", RelayFramesRoundTrip),
            ("relay routing isolates targeted clients", RelayRoutingIsolatesTargetedClients),
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
        Assert(!filter.Accept(9), "out-of-order sequence should be rejected");
        Assert(filter.Accept(11), "newer sequence should be accepted");
    }

    private static void DirectLiveSyncAcceptsAndAppliesOnce()
    {
        var host = new LiveSyncHost();
        var client = new LiveSyncClient();
        host.RegisterPlayer("direct:2", 2);

        var intent = IntentEnvelope(sequence: 10, playerId: 2, tick: 42, commandType: "PlaceRoad");
        Assert(host.TryAcceptIntent("direct:2", intent, out var accepted, out var rejection), rejection);
        Assert(accepted != null, "host should produce an accepted command");
        Assert(accepted!.CommandId == 1, "host should assign the first authoritative command ID");

        var broadcast = new ProtocolEnvelope(ProtocolMessageType.AcceptedCommand, 100, accepted.ToPayload());
        var applied = 0;
        if (client.TryAcceptCommand(broadcast, out var received, out rejection))
        {
            applied++;
            Assert(received!.CommandType == "PlaceRoad", "client received wrong command");
        }

        if (client.TryAcceptCommand(broadcast, out _, out _))
        {
            applied++;
        }

        Assert(applied == 1, "accepted command should be applied exactly once");
        Assert(!host.TryAcceptIntent("direct:2", intent, out _, out rejection), "duplicate intent should be rejected");
        Assert(rejection.Contains("duplicate or out of order", StringComparison.Ordinal), "duplicate rejection should be explicit");

        var older = IntentEnvelope(sequence: 9, playerId: 2, tick: 43, commandType: "PlaceRoad");
        Assert(!host.TryAcceptIntent("direct:2", older, out _, out _), "out-of-order intent should be rejected");
    }

    private static void RelayLiveSyncRoutesAcceptedCommandsOnce()
    {
        const string connectionId = "relay-client-7";
        var host = new LiveSyncHost();
        var client = new LiveSyncClient();
        host.RegisterPlayer(connectionId, 7);

        var intent = IntentEnvelope(sequence: 20, playerId: 7, tick: 90, commandType: "SetWorkArea");
        var clientFrame = new RelayFrame(RelayFrameType.Protocol, ProtocolSerializer.Pack(intent));
        var routedToHost = RelayRouting.FromClient(connectionId, clientFrame);
        var hostRoute = RelayRoute.FromPayload(routedToHost.Payload);
        var hostEnvelope = ProtocolSerializer.Unpack(hostRoute.ProtocolPayload);

        Assert(hostRoute.ConnectionId == connectionId, "relay sender identity was not preserved");
        Assert(host.TryAcceptIntent(hostRoute.ConnectionId, hostEnvelope, out var accepted, out var rejection), rejection);

        var broadcast = new ProtocolEnvelope(ProtocolMessageType.AcceptedCommand, 200, accepted!.ToPayload());
        var broadcastFrame = new RelayFrame(RelayFrameType.Protocol, ProtocolSerializer.Pack(broadcast));
        var recipients = RelayRouting.SelectHostRecipients(broadcastFrame, new[] { connectionId, "relay-client-8" });
        var deliveredFrame = RelayRouting.ForClient(broadcastFrame);
        var delivered = ProtocolSerializer.Unpack(deliveredFrame.Payload);

        Assert(recipients.Count == 2, "accepted relay command should broadcast to every room client");
        Assert(client.TryAcceptCommand(delivered, out var received, out rejection), rejection);
        Assert(received!.CommandType == "SetWorkArea", "relay client received wrong command");
        Assert(!client.TryAcceptCommand(delivered, out _, out _), "relay duplicate should not be applied twice");

        var spoofed = IntentEnvelope(sequence: 21, playerId: 8, tick: 91, commandType: "SetWorkArea");
        Assert(!host.TryAcceptIntent(connectionId, spoofed, out _, out rejection), "spoofed relay player ID should be rejected");
        Assert(rejection.Contains("sender is player 7", StringComparison.Ordinal), "spoof rejection should identify assigned player");
    }

    private static void LiveSyncStateHashesDetectDivergence()
    {
        var host = new LiveSyncHost();
        var client = new LiveSyncClient();
        host.RegisterPlayer("direct:2", 2);
        var hostHash = Hashing.Sha256Hex(Encoding.UTF8.GetBytes("host-state"));
        var clientHash = Hashing.Sha256Hex(Encoding.UTF8.GetBytes("client-state"));
        var authoritative = new SimulationStateHash { PlayerId = 1, SimulationTick = 50, Hash = hostHash };
        var local = new SimulationStateHash { PlayerId = 2, SimulationTick = 50, Hash = clientHash };
        host.SetAuthoritativeStateHash(authoritative);
        client.RecordLocalStateHash(local);

        var report = new ProtocolEnvelope(ProtocolMessageType.StateHash, 30, local.ToPayload());
        Assert(host.TryCheckStateHash(
            "direct:2", report, out var received, out var divergent, out var expected, out var rejection), rejection);
        Assert(received!.SimulationTick == 50, "host received wrong state hash tick");
        Assert(divergent, "host should detect state divergence");
        Assert(expected == hostHash, "host should return authoritative hash");

        var response = new ProtocolEnvelope(ProtocolMessageType.StateHash, 300, authoritative.ToPayload());
        Assert(client.TryCompareAuthoritativeStateHash(
            response, out _, out divergent, out var actual, out rejection), rejection);
        Assert(divergent, "client should detect state divergence");
        Assert(actual == clientHash, "client should report its local hash");

        client.Reset();
        client.RecordLocalStateHash(authoritative);
        Assert(client.TryCompareAuthoritativeStateHash(
            response, out _, out divergent, out _, out rejection), rejection);
        Assert(!divergent, "matching state hashes should not report divergence");
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

    private static ProtocolEnvelope IntentEnvelope(uint sequence, uint playerId, long tick, string commandType)
    {
        var intent = new CommandEnvelope
        {
            CommandId = 0,
            PlayerId = playerId,
            SimulationTick = tick,
            CommandType = commandType,
            JsonPayload = "{\"synthetic\":true}"
        };
        return new ProtocolEnvelope(ProtocolMessageType.PlayerIntent, sequence, intent.ToPayload());
    }

    private static void RelayRoutingIsolatesTargetedClients()
    {
        var clientIds = new[] { "client-a", "client-b" };
        var handshakeRequest = new ProtocolEnvelope(
            ProtocolMessageType.HandshakeRequest,
            10,
            new HandshakeRequest { PlayerName = "Client A" }.ToPayload());
        var clientFrame = new RelayFrame(RelayFrameType.Protocol, ProtocolSerializer.Pack(handshakeRequest));
        var hostFrame = RelayRouting.FromClient("client-a", clientFrame);
        var sourceRoute = RelayRoute.FromPayload(hostFrame.Payload);

        Assert(hostFrame.Type == RelayFrameType.RoutedProtocol, "client frame was not routed to host");
        Assert(sourceRoute.ConnectionId == "client-a", "client source ID was not exposed to host");
        Assert(
            ProtocolSerializer.Unpack(sourceRoute.ProtocolPayload).Type == ProtocolMessageType.HandshakeRequest,
            "routed handshake payload changed");

        var accepted = new ProtocolEnvelope(
            ProtocolMessageType.HandshakeAccepted,
            11,
            new HandshakeResponse { Accepted = true, AssignedPlayerId = 2 }.ToPayload());
        var targetedResponse = RelayRouting.ToClient("client-a", ProtocolSerializer.Pack(accepted));
        var responseRecipients = RelayRouting.SelectHostRecipients(targetedResponse, clientIds);
        Assert(responseRecipients.SequenceEqual(new[] { "client-a" }), "handshake response leaked to another client");

        var snapshot = new ProtocolEnvelope(
            ProtocolMessageType.SnapshotChunk,
            12,
            Encoding.UTF8.GetBytes("client-a-snapshot"));
        var targetedSnapshot = RelayRouting.ToClient("client-a", ProtocolSerializer.Pack(snapshot));
        var snapshotRecipients = RelayRouting.SelectHostRecipients(targetedSnapshot, clientIds);
        Assert(snapshotRecipients.SequenceEqual(new[] { "client-a" }), "snapshot frame leaked to another client");
        var deliveredSnapshot = ProtocolSerializer.Unpack(RelayRouting.ForClient(targetedSnapshot).Payload);
        Assert(deliveredSnapshot.Type == ProtocolMessageType.SnapshotChunk, "targeted frame was not unwrapped for client");

        var clientBResponse = RelayRouting.ToClient("client-b", ProtocolSerializer.Pack(accepted));
        Assert(
            RelayRouting.SelectHostRecipients(clientBResponse, clientIds).SequenceEqual(new[] { "client-b" }),
            "second client response was not independently routed");

        var gameplay = new ProtocolEnvelope(ProtocolMessageType.AcceptedCommand, 13, Array.Empty<byte>());
        var broadcast = new RelayFrame(RelayFrameType.Protocol, ProtocolSerializer.Pack(gameplay));
        Assert(
            RelayRouting.SelectHostRecipients(broadcast, clientIds).SequenceEqual(clientIds),
            "broadcast gameplay did not reach every client");

        var staleTarget = RelayRouting.ToClient("client-c", ProtocolSerializer.Pack(accepted));
        Assert(
            RelayRouting.SelectHostRecipients(staleTarget, clientIds).Count == 0,
            "unknown connection ID escaped the room routing table");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
