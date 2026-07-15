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
            ("handshake payload carries runtime compatibility", HandshakePayloadCarriesRuntimeCompatibility),
            ("handshake rejects incompatible runtime metadata", HandshakeRejectsIncompatibleRuntimeMetadata),
            ("handshake save hash controls resync", HandshakeSaveHashControlsResync),
            ("state hash report round-trips", StateHashReportRoundTrips),
            ("duplicate sequence filter rejects duplicates", DuplicateSequenceFilterRejectsDuplicates),
            ("snapshot chunks reassemble and validate", SnapshotChunksReassembleAndValidate),
            ("snapshot corruption is rejected", SnapshotCorruptionIsRejected),
            ("relay join and protocol frames round-trip", RelayFramesRoundTrip),
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

    private static void HandshakePayloadCarriesRuntimeCompatibility()
    {
        var original = CompatibleRequest(Hashing.Sha256Hex(Encoding.UTF8.GetBytes("save")));
        original.PlayerName = "Client";
        original.SessionKey = "secret";
        var restored = HandshakeRequest.FromPayload(original.ToPayload());

        Assert(restored.PlayerName == original.PlayerName, "player name mismatch");
        Assert(restored.SessionKey == original.SessionKey, "session key mismatch");
        Assert(restored.ProtocolVersion == original.ProtocolVersion, "protocol version mismatch");
        Assert(restored.OutlandersBuildGuid == original.OutlandersBuildGuid, "build GUID mismatch");
        Assert(restored.UnityVersion == original.UnityVersion, "Unity version mismatch");
        Assert(restored.ModVersion == original.ModVersion, "mod version mismatch");
        Assert(restored.SaveHash == original.SaveHash, "save hash mismatch");
    }

    private static void HandshakeRejectsIncompatibleRuntimeMetadata()
    {
        var hostHash = Hashing.Sha256Hex(Encoding.UTF8.GetBytes("host-save"));

        var wrongProtocol = CompatibleRequest(hostHash);
        wrongProtocol.ProtocolVersion++;
        AssertRejected(wrongProtocol, hostHash, "protocol version");

        var wrongMod = CompatibleRequest(hostHash);
        wrongMod.ModVersion = "9.9.9";
        AssertRejected(wrongMod, hostHash, "mod version");

        var wrongBuild = CompatibleRequest(hostHash);
        wrongBuild.OutlandersBuildGuid = "different-build";
        AssertRejected(wrongBuild, hostHash, "does not match host build");

        var wrongUnity = CompatibleRequest(hostHash);
        wrongUnity.UnityVersion = "different-unity";
        AssertRejected(wrongUnity, hostHash, "does not match host runtime");
    }

    private static void HandshakeSaveHashControlsResync()
    {
        var hostHash = Hashing.Sha256Hex(Encoding.UTF8.GetBytes("host-save"));
        var clientHash = Hashing.Sha256Hex(Encoding.UTF8.GetBytes("client-save"));

        var mismatch = Validate(CompatibleRequest(clientHash), hostHash);
        Assert(mismatch.Accepted, "a compatible client with a different save should be accepted for resync");
        Assert(mismatch.SnapshotRequired, "different save hash should require a snapshot");
        Assert(mismatch.HostSaveHash == hostHash, "host hash should be returned to the client");
        Assert(mismatch.Reason.Contains("resync", StringComparison.OrdinalIgnoreCase), "resync reason should be explicit");

        var matching = Validate(CompatibleRequest(hostHash), hostHash);
        Assert(matching.Accepted, "matching client should be accepted");
        Assert(!matching.SnapshotRequired, "matching save hash should skip snapshot transfer");

        var responseAgain = HandshakeResponse.FromPayload(mismatch.ToPayload());
        Assert(responseAgain.SnapshotRequired, "snapshot requirement should round-trip");
        Assert(responseAgain.HostSaveHash == hostHash, "response host hash should round-trip");
    }

    private static void StateHashReportRoundTrips()
    {
        var original = new StateHashReport
        {
            SnapshotId = "snapshot-1",
            SaveHash = Hashing.Sha256Hex(Encoding.UTF8.GetBytes("save"))
        };
        var restored = StateHashReport.FromPayload(original.ToPayload());

        Assert(restored.SnapshotId == original.SnapshotId, "snapshot ID mismatch");
        Assert(restored.SaveHash == original.SaveHash, "state hash mismatch");
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

    private static RuntimeCompatibility CompatibleRuntime()
    {
        return new RuntimeCompatibility
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            OutlandersBuildGuid = "runtime-build-guid",
            UnityVersion = "2022.3.test",
            ModVersion = "0.1.0"
        };
    }

    private static HandshakeRequest CompatibleRequest(string saveHash)
    {
        var runtime = CompatibleRuntime();
        return new HandshakeRequest
        {
            ProtocolVersion = runtime.ProtocolVersion,
            OutlandersBuildGuid = runtime.OutlandersBuildGuid,
            UnityVersion = runtime.UnityVersion,
            ModVersion = runtime.ModVersion,
            SaveHash = saveHash
        };
    }

    private static HandshakeResponse Validate(HandshakeRequest request, string hostHash)
    {
        return HandshakeValidator.ValidateForHost(request, string.Empty, CompatibleRuntime(), hostHash);
    }

    private static void AssertRejected(HandshakeRequest request, string hostHash, string reasonFragment)
    {
        var response = Validate(request, hostHash);
        Assert(!response.Accepted, $"{reasonFragment} mismatch should be rejected");
        Assert(response.Reason.Contains(reasonFragment, StringComparison.OrdinalIgnoreCase),
            $"rejection reason should mention {reasonFragment}: {response.Reason}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
