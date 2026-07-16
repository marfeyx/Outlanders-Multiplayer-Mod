using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
            ("handshake payload carries runtime compatibility", HandshakePayloadCarriesRuntimeCompatibility),
            ("handshake rejects incompatible runtime metadata", HandshakeRejectsIncompatibleRuntimeMetadata),
            ("handshake save hash controls resync", HandshakeSaveHashControlsResync),
            ("snapshot hash gate requires handshake and exact snapshot", SnapshotHashGateRequiresHandshakeAndExactSnapshot),
            ("snapshot hash retry is targeted and bounded", SnapshotHashRetryIsTargetedAndBounded),
            ("duplicate sequence filter rejects duplicates", DuplicateSequenceFilterRejectsDuplicates),
            ("direct live sync accepts and applies commands once", DirectLiveSyncAcceptsAndAppliesOnce),
            ("relay live sync routes accepted commands once", RelayLiveSyncRoutesAcceptedCommandsOnce),
            ("live sync state hashes detect divergence", LiveSyncStateHashesDetectDivergence),
            ("build placement payload validates and round-trips", BuildPlacementPayloadRoundTrips),
            ("build placement reflection codec preserves game fields", BuildPlacementReflectionCodecRoundTrips),
            ("snapshot chunks reassemble and validate", SnapshotChunksReassembleAndValidate),
            ("snapshot corruption is rejected", SnapshotCorruptionIsRejected),
            ("multiplayer snapshots register without overwriting saves", MultiplayerSnapshotsRegisterWithoutOverwritingSaves),
            ("hosting save selection excludes unsafe paths", HostingSaveSelectionExcludesUnsafePaths),
            ("relay join and protocol frames round-trip", RelayFramesRoundTrip),
            ("relay routing isolates targeted clients", RelayRoutingIsolatesTargetedClients),
            ("relay disconnects idle clients and remains available", RelayDisconnectsIdleClientsAndRemainsAvailable),
            ("relay connection times out without blocking", RelayConnectionTimesOutWithoutBlocking),
            ("relay connection sends join frame asynchronously", RelayConnectionSendsJoinFrameAsynchronously),
            ("relay TLS encrypts and routes host client traffic", RelayTlsEncryptsAndRoutesTraffic),
            ("relay TLS rejects an untrusted certificate", RelayTlsRejectsUntrustedCertificate),
            ("relay plaintext requires explicit loopback mode", RelayPlaintextRequiresExplicitLoopbackMode),
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
        Assert(mismatch.Accepted && mismatch.SnapshotRequired, "different save hash should require resync");
        Assert(mismatch.HostSaveHash == hostHash, "host hash should be returned");

        var matching = Validate(CompatibleRequest(hostHash), hostHash);
        Assert(matching.Accepted && !matching.SnapshotRequired, "matching save hash should skip transfer");
    }

    private static void SnapshotHashGateRequiresHandshakeAndExactSnapshot()
    {
        var tracker = new SnapshotHashTracker();
        var hash = Hashing.Sha256Hex(Encoding.UTF8.GetBytes("host-save"));
        var report = new StateHashReport { SnapshotId = "snapshot-1", SaveHash = hash };

        Assert(tracker.Evaluate("peer-a", report, hash, out var reason) == SnapshotHashDecision.Rejected,
            "pre-handshake snapshot report should be rejected");
        Assert(reason.Contains("handshake", StringComparison.OrdinalIgnoreCase), "pre-handshake reason should be explicit");

        tracker.RegisterPeer("peer-a");
        tracker.ExpectSnapshot("peer-a", "snapshot-1");
        var wrongSnapshot = new StateHashReport { SnapshotId = "snapshot-2", SaveHash = hash };
        Assert(tracker.Evaluate("peer-a", wrongSnapshot, hash, out _) == SnapshotHashDecision.Rejected,
            "wrong snapshot ID should be rejected");
        Assert(tracker.Evaluate("peer-a", report, hash, out _) == SnapshotHashDecision.Verified,
            "exact pending snapshot should verify");
    }

    private static void SnapshotHashRetryIsTargetedAndBounded()
    {
        var tracker = new SnapshotHashTracker();
        var hostHash = Hashing.Sha256Hex(Encoding.UTF8.GetBytes("host-save"));
        var clientHash = Hashing.Sha256Hex(Encoding.UTF8.GetBytes("corrupt-save"));
        var report = new StateHashReport { SnapshotId = "snapshot-1", SaveHash = clientHash };

        tracker.RegisterPeer("peer-a");
        tracker.RegisterPeer("peer-b");
        tracker.ExpectSnapshot("peer-a", "snapshot-1");
        Assert(tracker.Evaluate("peer-b", report, hostHash, out _) == SnapshotHashDecision.Rejected,
            "a peer without the pending transfer must not trigger another peer's retry");
        Assert(tracker.Evaluate("peer-a", report, hostHash, out _) == SnapshotHashDecision.Resend,
            "first mismatch should allow one targeted retry");
        Assert(tracker.Evaluate("peer-a", report, hostHash, out _) == SnapshotHashDecision.RetryExhausted,
            "second mismatch must not amplify snapshot transfers");
        Assert(tracker.Evaluate("peer-a", report, hostHash, out _) == SnapshotHashDecision.Rejected,
            "exhausted transfer must remain closed");
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

    private static void BuildPlacementPayloadRoundTrips()
    {
        var original = Placement();
        var restored = BuildPlacementIntent.FromJson(original.ToJson());

        Assert(restored.Category == BuildPlacementIntent.BuildingPrefabCategory, "placement category mismatch");
        Assert(restored.Key == 13, "placement building key mismatch");
        Assert(restored.PositionX == 24.5f && restored.PositionY == -7.25f, "placement position mismatch");
        Assert(restored.Rotation == 90f, "placement rotation mismatch");
        Assert(restored.SizeX == 3f && restored.SizeY == 2f, "placement footprint mismatch");

        restored.Category = 4;
        var rejected = false;
        try
        {
            restored.ToJson();
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Assert(rejected, "non-building placement category should be rejected");
    }

    private static void BuildPlacementReflectionCodecRoundTrips()
    {
        var codec = new ReflectionBuildPlacementCodec(
            typeof(FakeSiteSpawn),
            typeof(FakePrefabKey),
            typeof(FakePrefabCategory),
            typeof(FakeFloat2));
        var placement = Placement();
        var originalSpawn = new FakeSiteSpawn
        {
            Key = new FakePrefabKey(FakePrefabCategory.Building, placement.Key),
            Position = new FakeFloat2(placement.PositionX, placement.PositionY),
            Rotation = placement.Rotation,
            size = new FakeFloat2(placement.SizeX, placement.SizeY)
        };
        var originalCaptured = codec.Capture(originalSpawn);
        var spawn = codec.CreateSpawn(placement);
        var captured = codec.Capture(spawn);

        Assert(originalCaptured.ToJson() == placement.ToJson(), "reflection codec did not capture the game fields");
        Assert(captured.ToJson() == placement.ToJson(), "reflection codec changed the placement payload");
        var fakeSpawn = (FakeSiteSpawn)spawn;
        Assert(fakeSpawn.Key.Category == FakePrefabCategory.Building, "reflection codec set wrong prefab category");
        Assert(fakeSpawn.Key.Key == placement.Key, "reflection codec set wrong prefab key");
        Assert(fakeSpawn.Position.x == placement.PositionX && fakeSpawn.Position.y == placement.PositionY,
            "reflection codec set wrong position");
        Assert(fakeSpawn.size.x == placement.SizeX && fakeSpawn.size.y == placement.SizeY,
            "reflection codec set wrong footprint");
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
            var tempFolder = Directory.CreateDirectory(Path.Combine(firstUser, "OutlandersMultiplayerTemp")).FullName;
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

    private static void MultiplayerSnapshotsRegisterWithoutOverwritingSaves()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "OutlandersMultiplayerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var userFolder = Path.Combine(testRoot, "user-test");
            var saveGameFolder = Path.Combine(userFolder, "123456789");
            var endlessFolder = Path.Combine(saveGameFolder, "Endless");
            Directory.CreateDirectory(endlessFolder);
            File.WriteAllBytes(Path.Combine(userFolder, "savegame-123456789"), new byte[] { 1 });
            var normalSavePath = Path.Combine(endlessFolder, "Endless_0.dat");
            var normalSaveBytes = Encoding.UTF8.GetBytes("normal-save");
            File.WriteAllBytes(normalSavePath, normalSaveBytes);
            File.WriteAllBytes(normalSavePath + ".meta", new byte[] { 2 });

            var firstSnapshot = Encoding.UTF8.GetBytes("host-world-a");
            var first = MultiplayerSaveRegistrar.Register(userFolder, firstSnapshot);
            Assert(first.SlotIndex == 1, "existing Endless slot was not skipped");
            Assert(Path.GetFileName(first.Path) == "Endless_1.dat", "registered slot name was not loadable");
            Assert(File.ReadAllBytes(first.Path).SequenceEqual(firstSnapshot), "registered snapshot bytes changed");
            Assert(File.ReadAllBytes(normalSavePath).SequenceEqual(normalSaveBytes), "normal save was overwritten");

            var secondSnapshot = Encoding.UTF8.GetBytes("host-world-b");
            var second = MultiplayerSaveRegistrar.Register(userFolder, secondSnapshot);
            Assert(second.SlotIndex == 2, "second snapshot replaced an existing multiplayer slot");
            Assert(File.ReadAllBytes(first.Path).SequenceEqual(firstSnapshot), "first multiplayer slot was overwritten");

            var secondSaveGameFolder = Path.Combine(userFolder, "987654321");
            Directory.CreateDirectory(secondSaveGameFolder);
            File.WriteAllBytes(Path.Combine(userFolder, "savegame-987654321"), new byte[] { 3 });
            var rejectedAmbiguousProfile = false;
            try
            {
                MultiplayerSaveRegistrar.Register(userFolder, Encoding.UTF8.GetBytes("must-not-be-written"));
            }
            catch (InvalidOperationException ex)
            {
                rejectedAmbiguousProfile = ex.Message.Contains("Multiple Outlanders save games", StringComparison.Ordinal);
            }

            Assert(rejectedAmbiguousProfile, "ambiguous profile selection was not rejected");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
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
        using var transport = new TcpRelayTransport(
            TimeSpan.FromSeconds(2),
            security: RelayTransportSecurity.InsecureLocalhost);
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

    private static void RelayTlsEncryptsAndRoutesTraffic()
    {
        RelayTlsEncryptsAndRoutesTrafficAsync().GetAwaiter().GetResult();
    }

    private static async Task RelayTlsEncryptsAndRoutesTrafficAsync()
    {
        using var certificate = CreateLocalhostCertificate();
        using var shutdown = new CancellationTokenSource();
        var server = new RelayServerHost(
            port: 0,
            serverCertificate: certificate,
            listenAddress: IPAddress.Loopback);
        var serverTask = server.RunAsync(shutdown.Token);

        bool ValidateTestCertificate(
            object sender,
            System.Security.Cryptography.X509Certificates.X509Certificate? presented,
            X509Chain? chain,
            SslPolicyErrors errors)
        {
            return presented != null
                && presented.GetCertHashString().Equals(certificate.GetCertHashString(), StringComparison.OrdinalIgnoreCase)
                && (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0;
        }

        using var host = new TcpRelayTransport(
            TimeSpan.FromSeconds(3),
            null,
            RelayTransportSecurity.Tls,
            ValidateTestCertificate);
        using var client = new TcpRelayTransport(
            TimeSpan.FromSeconds(3),
            null,
            RelayTransportSecurity.Tls,
            ValidateTestCertificate);
        string? hostStatus = null;
        string? clientStatus = null;
        string? failure = null;
        ProtocolEnvelope? received = null;
        host.StatusReceived += value => hostStatus = value;
        client.StatusReceived += value => clientStatus = value;
        host.ConnectionFailed += value => failure = value;
        client.ConnectionFailed += value => failure = value;
        host.MessageReceived += (_, envelope) => received = envelope;

        try
        {
            host.Connect("localhost", server.ListeningPort, new RelayJoinRequest
            {
                Role = RelayRole.Host,
                RoomCode = "TLSROOM",
                SessionKey = "join-secret-not-visible-on-the-wire",
                PlayerName = "TLS Host"
            });
            WaitUntil(
                () => hostStatus == "relay-connected" || failure != null,
                TimeSpan.FromSeconds(5),
                host.Poll,
                "TLS relay host did not join");
            Assert(failure == null, $"TLS relay host failed: {failure}");

            client.Connect("localhost", server.ListeningPort, new RelayJoinRequest
            {
                Role = RelayRole.Client,
                RoomCode = "TLSROOM",
                SessionKey = "join-secret-not-visible-on-the-wire",
                PlayerName = "TLS Client"
            });
            WaitUntil(
                () => clientStatus == "relay-connected" || failure != null,
                TimeSpan.FromSeconds(5),
                () => { host.Poll(); client.Poll(); },
                "TLS relay client did not join");
            Assert(failure == null, $"TLS relay client failed: {failure}");

            var payload = Encoding.UTF8.GetBytes("authenticated encrypted gameplay payload");
            client.Send(new ProtocolEnvelope(ProtocolMessageType.TextStatus, 901, payload));
            WaitUntil(
                () => received != null,
                TimeSpan.FromSeconds(5),
                () => { host.Poll(); client.Poll(); },
                "TLS relay did not route client traffic to the host");
            Assert(received!.Sequence == 901, "TLS relay changed the gameplay sequence");
            Assert(received.Payload.SequenceEqual(payload), "TLS relay changed the gameplay payload");
        }
        finally
        {
            host.Stop();
            client.Stop();
            shutdown.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static void RelayTlsRejectsUntrustedCertificate()
    {
        RelayTlsRejectsUntrustedCertificateAsync().GetAwaiter().GetResult();
    }

    private static async Task RelayTlsRejectsUntrustedCertificateAsync()
    {
        using var certificate = CreateLocalhostCertificate();
        using var shutdown = new CancellationTokenSource();
        var server = new RelayServerHost(
            port: 0,
            serverCertificate: certificate,
            listenAddress: IPAddress.Loopback);
        var serverTask = server.RunAsync(shutdown.Token);
        using var transport = new TcpRelayTransport(TimeSpan.FromSeconds(3));
        string? failure = null;
        transport.ConnectionFailed += value => failure = value;

        try
        {
            transport.Connect("localhost", server.ListeningPort, CreateRelayJoinRequest());
            WaitUntil(
                () => failure != null,
                TimeSpan.FromSeconds(5),
                transport.Poll,
                "untrusted relay certificate was not rejected");
            Assert(
                failure!.Contains("TLS certificate validation failed", StringComparison.Ordinal),
                $"certificate rejection was not actionable: {failure}");
        }
        finally
        {
            transport.Stop();
            shutdown.Cancel();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static void RelayPlaintextRequiresExplicitLoopbackMode()
    {
        using var transport = new TcpRelayTransport(security: RelayTransportSecurity.InsecureLocalhost);
        var rejectedPublicHost = false;
        try
        {
            transport.Connect("relay.example.com", 17668, CreateRelayJoinRequest());
        }
        catch (InvalidOperationException ex)
        {
            rejectedPublicHost = ex.Message.Contains("loopback", StringComparison.OrdinalIgnoreCase);
        }

        Assert(rejectedPublicHost, "plaintext client mode accepted a public relay host");

        var rejectedPublicListener = false;
        try
        {
            _ = new RelayServerHost(
                port: 0,
                listenAddress: IPAddress.Any,
                allowInsecureLocalhost: true);
        }
        catch (InvalidOperationException ex)
        {
            rejectedPublicListener = ex.Message.Contains("loopback", StringComparison.OrdinalIgnoreCase);
        }

        Assert(rejectedPublicListener, "plaintext relay server accepted a public listener");
    }

    private static X509Certificate2 CreateLocalhostCertificate()
    {
        RSA key;
        if (OperatingSystem.IsWindows())
        {
            key = new RSACryptoServiceProvider(2048, new CspParameters(24)
            {
                KeyNumber = (int)KeyNumber.Exchange,
                Flags = CspProviderFlags.CreateEphemeralKey
            });
        }
        else
        {
            key = RSA.Create(2048);
        }
        var request = new CertificateRequest(
            "CN=localhost",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(1));
        const string password = "outlanders-test-certificate";
        var pfx = generated.Export(X509ContentType.Pfx, password);
        key.Dispose();
        return new X509Certificate2(
            pfx,
            password,
            OperatingSystem.IsWindows()
                ? X509KeyStorageFlags.UserKeySet
                : X509KeyStorageFlags.EphemeralKeySet);
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

    private static BuildPlacementIntent Placement()
    {
        return new BuildPlacementIntent
        {
            Category = BuildPlacementIntent.BuildingPrefabCategory,
            Key = 13,
            PositionX = 24.5f,
            PositionY = -7.25f,
            Rotation = 90f,
            SizeX = 3f,
            SizeY = 2f
        };
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
            clientReadTimeout: TimeSpan.FromMilliseconds(250),
            listenAddress: IPAddress.Loopback,
            allowInsecureLocalhost: true);
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

    private enum FakePrefabCategory
    {
        None = 0,
        Building = 3
    }

    private struct FakePrefabKey
    {
        public FakePrefabKey(FakePrefabCategory category, int key)
        {
            Category = category;
            Key = key;
        }

        public FakePrefabCategory Category;
        public int Key;
    }

    private struct FakeFloat2
    {
        public FakeFloat2(float x, float y)
        {
            this.x = x;
            this.y = y;
        }

        public float x;
        public float y;
    }

    private struct FakeSiteSpawn
    {
        public FakePrefabKey Key;
        public FakeFloat2 Position;
        public float Rotation;
        public FakeFloat2 size;
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
