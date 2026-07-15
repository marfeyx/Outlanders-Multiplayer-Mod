using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LiteNetLib;
using OutlandersMultiplayer.Core.Protocol;
using OutlandersMultiplayer.Core.Relay;
using OutlandersMultiplayer.Core.Session;
using OutlandersMultiplayer.Core.Snapshots;
using OutlandersMultiplayer.Core.State;
using OutlandersMultiplayer.Mod.Game;
using OutlandersMultiplayer.Mod.Networking;

namespace OutlandersMultiplayer.Mod;

public sealed class MultiplayerController : IDisposable
{
    private readonly SessionState _state;
    private readonly Action<string> _log;
    private readonly RuntimeCompatibility _compatibility;
    private readonly Dictionary<string, SnapshotChunk> _receivedChunks = new();
    private readonly Dictionary<string, string> _relayPlayerNames = new(StringComparer.Ordinal);
    private readonly LiveSyncHost _liveSyncHost = new();
    private readonly LiveSyncClient _liveSyncClient = new();
    private readonly SnapshotHashTracker _snapshotHashes = new();
    private LiteNetLibDirectTransport? _transport;
    private TcpRelayTransport? _relayTransport;
    private SnapshotPackage? _hostSnapshot;
    private SnapshotManifest? _clientManifest;
    private uint _nextSequence = 1;
    private uint _nextPlayerId = 2;
    private string _sessionKey = string.Empty;
    private string _expectedHostSaveHash = string.Empty;
    private uint _localPlayerId;
    private bool _isHost;

    private const string HostSenderId = "host";

    public MultiplayerController(SessionState state, Action<string> log, RuntimeCompatibility? compatibility = null)
    {
        _state = state;
        _log = log;
        _compatibility = compatibility ?? RuntimeCompatibilityProvider.Capture();
    }

    public SessionState State => _state;
    public uint LocalPlayerId => _localPlayerId;

    public event Action<CommandEnvelope>? AcceptedCommandReceived;
    public event Action<long, string, string>? StateDivergenceDetected;
    public event Func<CommandEnvelope, string?>? PlayerIntentValidating;

    public void Host(int port, string sessionKey)
    {
        Disconnect();
        if (!EnsureRuntimeCompatibility()) return;
        _sessionKey = sessionKey ?? string.Empty;

        var savePath = OutlandersSaveLocator.FindLatestSandboxSave();
        if (savePath == null)
        {
            _state.SetError("No Endless/Sandbox save file was found.");
            return;
        }

        var saveBytes = File.ReadAllBytes(savePath);
        _hostSnapshot = SnapshotService.Create(Path.GetFileName(savePath), saveBytes);
        InitializeHostLiveSync();

        _transport = new LiteNetLibDirectTransport();
        _transport.ConnectionRequested += request => request.AcceptIfKey(_sessionKey);
        _transport.PeerConnected += peer =>
        {
            _state.SetPlayers(new[] { "Host", peer.ToString() });
            _log($"Client connected: {peer}");
        };
        _transport.PeerDisconnected += (peer, info) =>
        {
            var senderId = DirectSenderId(peer);
            _liveSyncHost.UnregisterPlayer(senderId);
            _snapshotHashes.UnregisterPeer(senderId);
            _state.SetPlayers(new[] { "Host" });
            _log($"Client disconnected: {peer} ({info.Reason})");
        };
        _transport.MessageReceived += HandleHostMessage;
        _transport.StartServer(port);
        _state.SetPlayers(new[] { "Host" });
        _state.SetStatus(SessionStatus.Hosting, $"Hosting on UDP {port}");
        _log($"Hosting Outlanders multiplayer from {savePath}");
    }

    public void Join(string host, int port, string sessionKey, string playerName)
    {
        Disconnect();
        if (!EnsureRuntimeCompatibility()) return;
        _isHost = false;
        _sessionKey = sessionKey ?? string.Empty;
        _receivedChunks.Clear();
        _clientManifest = null;

        _transport = new LiteNetLibDirectTransport();
        _transport.PeerConnected += peer =>
        {
            _state.SetStatus(SessionStatus.Connected, $"Connected to {host}:{port}");
            SendHandshake(playerName);
        };
        _transport.PeerDisconnected += (peer, info) =>
        {
            _state.SetStatus(SessionStatus.Offline, $"Disconnected: {info.Reason}");
        };
        _transport.MessageReceived += HandleClientMessage;
        _transport.StartClient(host, port, _sessionKey);
        _state.SetStatus(SessionStatus.Joining, $"Joining {host}:{port}");
    }

    public void HostViaRelay(string relayHost, int relayPort, string roomCode, string sessionKey)
    {
        Disconnect();
        if (!EnsureRuntimeCompatibility()) return;
        _sessionKey = sessionKey ?? string.Empty;

        var savePath = OutlandersSaveLocator.FindLatestSandboxSave();
        if (savePath == null)
        {
            _state.SetError("No Endless/Sandbox save file was found.");
            return;
        }

        _hostSnapshot = SnapshotService.Create(Path.GetFileName(savePath), File.ReadAllBytes(savePath));
        InitializeHostLiveSync();
        _relayTransport = new TcpRelayTransport();
        _relayTransport.Connected += () => _state.SetStatus(SessionStatus.Hosting, $"Relay host room {roomCode}");
        _relayTransport.StatusReceived += status => _log($"Relay status: {status}");
        _relayTransport.Rejected += reason => _state.SetError(reason);
        _relayTransport.Disconnected += reason => _state.SetStatus(SessionStatus.Offline, $"Relay disconnected: {reason}");
        _relayTransport.MessageReceived += HandleHostRelayMessage;
        _relayTransport.Connect(relayHost, relayPort, new RelayJoinRequest
        {
            Role = RelayRole.Host,
            RoomCode = roomCode,
            SessionKey = _sessionKey,
            PlayerName = "Host"
        });

        _state.SetPlayers(new[] { "Host" });
        _state.SetStatus(SessionStatus.Hosting, $"Relay host {roomCode} via {relayHost}:{relayPort}");
        _log($"Hosting Outlanders multiplayer via relay from {savePath}");
    }

    public void JoinViaRelay(string relayHost, int relayPort, string roomCode, string sessionKey, string playerName)
    {
        Disconnect();
        if (!EnsureRuntimeCompatibility()) return;
        _isHost = false;
        _sessionKey = sessionKey ?? string.Empty;
        _receivedChunks.Clear();
        _clientManifest = null;

        _relayTransport = new TcpRelayTransport();
        _relayTransport.Connected += () =>
        {
            _state.SetStatus(SessionStatus.Connected, $"Connected to relay room {roomCode}");
            SendHandshake(playerName);
        };
        _relayTransport.StatusReceived += status => _log($"Relay status: {status}");
        _relayTransport.Rejected += reason => _state.SetError(reason);
        _relayTransport.Disconnected += reason => _state.SetStatus(SessionStatus.Offline, $"Relay disconnected: {reason}");
        _relayTransport.MessageReceived += (_, envelope) => HandleClientRelayMessage(envelope);
        _relayTransport.Connect(relayHost, relayPort, new RelayJoinRequest
        {
            Role = RelayRole.Client,
            RoomCode = roomCode,
            SessionKey = _sessionKey,
            PlayerName = string.IsNullOrWhiteSpace(playerName) ? Environment.UserName : playerName
        });

        _state.SetStatus(SessionStatus.Joining, $"Joining relay room {roomCode}");
    }

    public void Poll()
    {
        try
        {
            _transport?.Poll();
            _relayTransport?.Poll();
        }
        catch (Exception ex)
        {
            _state.SetError(ex.Message);
            _log(ex.ToString());
        }
    }

    public void Disconnect()
    {
        _transport?.Stop();
        _transport?.Dispose();
        _transport = null;
        _relayTransport?.Stop();
        _relayTransport?.Dispose();
        _relayTransport = null;
        _hostSnapshot = null;
        _clientManifest = null;
        _expectedHostSaveHash = string.Empty;
        _receivedChunks.Clear();
        _relayPlayerNames.Clear();
        _liveSyncHost.Reset();
        _liveSyncClient.Reset();
        _snapshotHashes.Reset();
        _localPlayerId = 0;
        _isHost = false;
        _nextPlayerId = 2;
        _nextSequence = 1;
        _state.SetStatus(SessionStatus.Offline, "Offline");
        _state.SetPlayers(Array.Empty<string>());
    }

    public void Dispose()
    {
        Disconnect();
    }

    public bool SendPlayerIntent(CommandEnvelope intent)
    {
        if (intent == null) throw new ArgumentNullException(nameof(intent));
        if (_localPlayerId == 0)
        {
            _state.SetError("Complete the multiplayer handshake before sending player intents.");
            return false;
        }

        var normalized = new CommandEnvelope
        {
            CommandId = 0,
            PlayerId = _localPlayerId,
            SimulationTick = intent.SimulationTick,
            CommandType = intent.CommandType,
            JsonPayload = intent.JsonPayload
        };
        ProtocolEnvelope envelope;
        try
        {
            envelope = new ProtocolEnvelope(ProtocolMessageType.PlayerIntent, NextSequence(), normalized.ToPayload());
        }
        catch (Exception ex)
        {
            _state.SetError($"Player intent is invalid: {ex.Message}");
            return false;
        }

        if (_isHost)
        {
            return AcceptAndBroadcastIntent(HostSenderId, envelope, reason => _state.SetError(reason));
        }

        _transport?.SendToServer(envelope);
        _relayTransport?.Send(envelope);
        return _transport != null || _relayTransport != null;
    }

    public bool PublishStateHash(long simulationTick, string hash)
    {
        if (_localPlayerId == 0)
        {
            _state.SetError("Complete the multiplayer handshake before publishing state hashes.");
            return false;
        }

        var stateHash = new SimulationStateHash
        {
            PlayerId = _localPlayerId,
            SimulationTick = simulationTick,
            Hash = hash ?? string.Empty
        };
        try
        {
            _liveSyncClient.RecordLocalStateHash(stateHash);
            if (_isHost)
            {
                _liveSyncHost.SetAuthoritativeStateHash(stateHash);
            }
        }
        catch (Exception ex)
        {
            _state.SetError(ex.Message);
            return false;
        }

        if (_isHost)
        {
            var authoritative = new ProtocolEnvelope(ProtocolMessageType.StateHash, NextSequence(), stateHash.ToPayload());
            _transport?.Broadcast(authoritative);
            _relayTransport?.Send(authoritative);
            return true;
        }

        var report = new ProtocolEnvelope(ProtocolMessageType.StateHash, NextSequence(), stateHash.ToPayload());
        _transport?.SendToServer(report);
        _relayTransport?.Send(report);
        return _transport != null || _relayTransport != null;
    }

    private void SendHandshake(string playerName)
    {
        var localSaveHash = GetLocalSaveHash();
        var request = new HandshakeRequest
        {
            PlayerName = string.IsNullOrWhiteSpace(playerName) ? Environment.UserName : playerName,
            SessionKey = _sessionKey,
            ProtocolVersion = _compatibility.ProtocolVersion,
            OutlandersBuildGuid = _compatibility.OutlandersBuildGuid,
            UnityVersion = _compatibility.UnityVersion,
            ModVersion = _compatibility.ModVersion,
            SaveHash = localSaveHash
        };

        _transport?.SendToServer(new ProtocolEnvelope(ProtocolMessageType.HandshakeRequest, NextSequence(), request.ToPayload()));
        _relayTransport?.Send(new ProtocolEnvelope(ProtocolMessageType.HandshakeRequest, NextSequence(), request.ToPayload()));
    }

    private void HandleHostMessage(NetPeer peer, ProtocolEnvelope envelope)
    {
        var senderId = DirectSenderId(peer);
        if (envelope.Type == ProtocolMessageType.PlayerIntent)
        {
            AcceptAndBroadcastIntent(senderId, envelope, reason => SendDirectRejection(peer, reason));
            return;
        }

        if (envelope.Type == ProtocolMessageType.StateHash)
        {
            CheckClientStateHash(senderId, envelope, authoritative => _transport?.Send(peer, authoritative));
            return;
        }

        if (envelope.Type == ProtocolMessageType.SnapshotStateHash)
        {
            CheckSnapshotStateHash(senderId, envelope, () => SendSnapshot(peer, senderId));
            return;
        }

        if (envelope.Type != ProtocolMessageType.HandshakeRequest)
        {
            return;
        }

        var request = HandshakeRequest.FromPayload(envelope.Payload);
        var response = ValidateHandshake(request);
        if (response.Accepted)
        {
            response.AssignedPlayerId = _nextPlayerId++;
            _liveSyncHost.RegisterPlayer(senderId, response.AssignedPlayerId);
            _snapshotHashes.RegisterPeer(senderId);
        }

        var responseType = response.Accepted ? ProtocolMessageType.HandshakeAccepted : ProtocolMessageType.HandshakeRejected;
        _transport?.Send(peer, new ProtocolEnvelope(responseType, NextSequence(), response.ToPayload()));
        if (!response.Accepted || !response.SnapshotRequired || _hostSnapshot == null)
        {
            return;
        }

        _snapshotHashes.ExpectSnapshot(senderId, _hostSnapshot.Manifest.SnapshotId);
        SendSnapshot(peer, senderId);

        _log($"Sent snapshot {_hostSnapshot.Manifest.SnapshotId} to {request.PlayerName} ({_hostSnapshot.Manifest.ChunkCount} chunks).");
    }

    private void HandleHostRelayMessage(string connectionId, ProtocolEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        if (envelope.Type == ProtocolMessageType.PlayerIntent)
        {
            AcceptAndBroadcastIntent(connectionId, envelope, reason => SendRelayRejection(connectionId, reason));
            return;
        }

        if (envelope.Type == ProtocolMessageType.StateHash)
        {
            CheckClientStateHash(connectionId, envelope, authoritative => _relayTransport?.SendToClient(connectionId, authoritative));
            return;
        }

        if (envelope.Type == ProtocolMessageType.SnapshotStateHash)
        {
            CheckSnapshotStateHash(connectionId, envelope, () => SendRelaySnapshot(connectionId));
            return;
        }

        if (envelope.Type != ProtocolMessageType.HandshakeRequest)
        {
            return;
        }

        var request = HandshakeRequest.FromPayload(envelope.Payload);
        var response = ValidateHandshake(request);
        if (response.Accepted)
        {
            response.AssignedPlayerId = _nextPlayerId++;
            _liveSyncHost.RegisterPlayer(connectionId, response.AssignedPlayerId);
            _snapshotHashes.RegisterPeer(connectionId);
        }

        var responseType = response.Accepted ? ProtocolMessageType.HandshakeAccepted : ProtocolMessageType.HandshakeRejected;
        _relayTransport?.SendToClient(connectionId, new ProtocolEnvelope(responseType, NextSequence(), response.ToPayload()));
        if (!response.Accepted || !response.SnapshotRequired || _hostSnapshot == null)
        {
            return;
        }

        _snapshotHashes.ExpectSnapshot(connectionId, _hostSnapshot.Manifest.SnapshotId);
        SendRelaySnapshot(connectionId);

        _relayPlayerNames[connectionId] = request.PlayerName;
        _state.SetPlayers(new[] { "Host" }.Concat(_relayPlayerNames.Values));
        _log($"Sent relay snapshot {_hostSnapshot.Manifest.SnapshotId} to {request.PlayerName} ({_hostSnapshot.Manifest.ChunkCount} chunks).");
    }

    private void HandleClientMessage(NetPeer peer, ProtocolEnvelope envelope)
    {
        HandleClientEnvelope(envelope);
    }

    private void HandleClientRelayMessage(ProtocolEnvelope envelope)
    {
        HandleClientEnvelope(envelope);
    }

    private void HandleClientEnvelope(ProtocolEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case ProtocolMessageType.HandshakeAccepted:
            {
                var response = HandshakeResponse.FromPayload(envelope.Payload);
                _localPlayerId = response.AssignedPlayerId;
                _expectedHostSaveHash = response.HostSaveHash;
                var message = response.SnapshotRequired
                    ? $"Accepted as player {response.AssignedPlayerId}; save differs, resyncing"
                    : $"Accepted as player {response.AssignedPlayerId}; save hash verified";
                _state.SetStatus(SessionStatus.Connected, message);
                break;
            }
            case ProtocolMessageType.HandshakeRejected:
            {
                var response = HandshakeResponse.FromPayload(envelope.Payload);
                _state.SetError(response.Reason);
                break;
            }
            case ProtocolMessageType.SnapshotManifest:
            {
                _clientManifest = SnapshotManifest.FromPayload(envelope.Payload);
                _receivedChunks.Clear();
                _state.SetStatus(SessionStatus.Connected, $"Receiving snapshot 0/{_clientManifest.ChunkCount}");
                break;
            }
            case ProtocolMessageType.SnapshotChunk:
            {
                var chunk = SnapshotChunk.FromPayload(envelope.Payload);
                _receivedChunks[$"{chunk.SnapshotId}:{chunk.Index}"] = chunk;
                TryFinishSnapshot();
                break;
            }
            case ProtocolMessageType.AcceptedCommand:
                ApplyAcceptedCommand(envelope);
                break;
            case ProtocolMessageType.StateHash:
                CompareAuthoritativeStateHash(envelope);
                break;
            case ProtocolMessageType.TextStatus:
            {
                var message = Encoding.UTF8.GetString(envelope.Payload);
                if (message.StartsWith("live-sync-rejected: ", StringComparison.Ordinal))
                {
                    _state.SetError(message.Substring("live-sync-rejected: ".Length));
                }
                else
                {
                    _log($"Host status: {message}");
                }

                break;
            }
        }
    }

    private void TryFinishSnapshot()
    {
        if (_clientManifest == null || _receivedChunks.Count < _clientManifest.ChunkCount)
        {
            if (_clientManifest != null)
            {
                _state.SetStatus(SessionStatus.Connected, $"Receiving snapshot {_receivedChunks.Count}/{_clientManifest.ChunkCount}");
            }

            return;
        }

        var saveBytes = SnapshotService.Reassemble(_clientManifest, _receivedChunks.Values);
        var actualHash = Hashing.Sha256Hex(saveBytes);
        SendSnapshotStateHash(new StateHashReport
        {
            SnapshotId = _clientManifest.SnapshotId,
            SaveHash = actualHash
        });

        if (!StringComparer.OrdinalIgnoreCase.Equals(actualHash, _expectedHostSaveHash))
        {
            _state.SetError($"Snapshot save hash {actualHash} does not match the host hash {_expectedHostSaveHash}; awaiting one targeted retry.");
            return;
        }

        var userFolder = OutlandersSaveLocator.FindUserFolder();
        if (userFolder == null)
        {
            _state.SetError("Outlanders user save folder was not found.");
            return;
        }

        var path = TempSaveWriter.WriteTempSave(userFolder, _clientManifest.SaveName, saveBytes);
        _state.SetStatus(SessionStatus.Connected, $"Snapshot saved to temp slot: {Path.GetFileName(path)}");
        _log($"Received snapshot {_clientManifest.SnapshotId}; wrote temp save {path}");
    }

    private HandshakeResponse ValidateHandshake(HandshakeRequest request)
    {
        return HandshakeValidator.ValidateForHost(
            request,
            _sessionKey,
            _compatibility,
            _hostSnapshot?.Manifest.Sha256 ?? string.Empty);
    }

    private void SendSnapshot(NetPeer peer, string senderId)
    {
        if (_hostSnapshot == null) return;
        _transport?.Send(peer, new ProtocolEnvelope(ProtocolMessageType.SnapshotManifest, NextSequence(), _hostSnapshot.Manifest.ToPayload()));
        foreach (var chunk in _hostSnapshot.Chunks)
        {
            _transport?.Send(peer, new ProtocolEnvelope(ProtocolMessageType.SnapshotChunk, NextSequence(), chunk.ToPayload()));
        }

        _log($"Sent snapshot {_hostSnapshot.Manifest.SnapshotId} to {senderId}.");
    }

    private void SendRelaySnapshot(string connectionId)
    {
        if (_hostSnapshot == null) return;
        _relayTransport?.SendToClient(connectionId, new ProtocolEnvelope(ProtocolMessageType.SnapshotManifest, NextSequence(), _hostSnapshot.Manifest.ToPayload()));
        foreach (var chunk in _hostSnapshot.Chunks)
        {
            _relayTransport?.SendToClient(connectionId, new ProtocolEnvelope(ProtocolMessageType.SnapshotChunk, NextSequence(), chunk.ToPayload()));
        }

        _log($"Sent snapshot {_hostSnapshot.Manifest.SnapshotId} to relay client {connectionId}.");
    }

    private void CheckSnapshotStateHash(string senderId, ProtocolEnvelope envelope, Action resendSnapshot)
    {
        StateHashReport report;
        try
        {
            report = StateHashReport.FromPayload(envelope.Payload);
            var expectedHash = _hostSnapshot?.Manifest.Sha256 ?? string.Empty;
            var decision = _snapshotHashes.Evaluate(senderId, report, expectedHash, out var reason);
            switch (decision)
            {
                case SnapshotHashDecision.Verified:
                    _log($"Client {senderId} verified snapshot {report.SnapshotId} with save hash {report.SaveHash}.");
                    return;
                case SnapshotHashDecision.Resend:
                    _log($"Client {senderId} reported a mismatched save hash; sending one targeted retry.");
                    resendSnapshot();
                    return;
                case SnapshotHashDecision.RetryExhausted:
                case SnapshotHashDecision.Rejected:
                    _log($"Rejected snapshot hash from {senderId}: {reason}");
                    return;
            }
        }
        catch (Exception ex)
        {
            _log($"Rejected snapshot hash from {senderId}: {ex.Message}");
        }
    }

    private void SendSnapshotStateHash(StateHashReport report)
    {
        var envelope = new ProtocolEnvelope(ProtocolMessageType.SnapshotStateHash, NextSequence(), report.ToPayload());
        _transport?.SendToServer(envelope);
        _relayTransport?.Send(envelope);
    }

    private string GetLocalSaveHash()
    {
        try
        {
            var path = OutlandersSaveLocator.FindLatestSandboxSave();
            return path == null ? string.Empty : OutlandersSaveLocator.HashSaveFile(path);
        }
        catch (Exception ex)
        {
            _log($"Could not hash the local save before joining: {ex.Message}");
            return string.Empty;
        }
    }

    private bool EnsureRuntimeCompatibility()
    {
        if (_compatibility.IsComplete) return true;
        _state.SetError("Could not determine the running Outlanders build, Unity runtime, or mod version.");
        return false;
    }

    private bool AcceptAndBroadcastIntent(string senderId, ProtocolEnvelope envelope, Action<string> reject)
    {
        if (!_liveSyncHost.TryAcceptIntent(senderId, envelope, out var command, out var rejection) || command == null)
        {
            _log($"Rejected player intent from {senderId}: {rejection}");
            reject(rejection);
            return false;
        }

        foreach (var validator in PlayerIntentValidating?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try
            {
                var validationError = ((Func<CommandEnvelope, string?>)validator)(command);
                if (!string.IsNullOrWhiteSpace(validationError))
                {
                    _log($"Rejected player intent from {senderId}: {validationError}");
                    reject(validationError);
                    return false;
                }
            }
            catch (Exception ex)
            {
                var validationError = $"Gameplay validation failed: {ex.Message}";
                _log($"Rejected player intent from {senderId}: {validationError}");
                reject(validationError);
                return false;
            }
        }

        var accepted = new ProtocolEnvelope(ProtocolMessageType.AcceptedCommand, NextSequence(), command.ToPayload());
        _transport?.Broadcast(accepted);
        _relayTransport?.Send(accepted);
        ApplyAcceptedCommand(accepted);
        return true;
    }

    private void ApplyAcceptedCommand(ProtocolEnvelope envelope)
    {
        if (!_liveSyncClient.TryAcceptCommand(envelope, out var command, out var rejection) || command == null)
        {
            _log($"Ignored accepted command: {rejection}");
            return;
        }

        AcceptedCommandReceived?.Invoke(command);
        _log($"Applied accepted command {command.CommandId} ({command.CommandType}) at tick {command.SimulationTick}.");
    }

    private void CheckClientStateHash(string senderId, ProtocolEnvelope envelope, Action<ProtocolEnvelope> sendAuthoritative)
    {
        if (!_liveSyncHost.TryCheckStateHash(
                senderId,
                envelope,
                out var report,
                out var divergent,
                out var expectedHash,
                out var rejection))
        {
            _log($"Rejected state hash from {senderId}: {rejection}");
            return;
        }

        if (!divergent || report == null)
        {
            return;
        }

        _log($"State divergence for player {report.PlayerId} at tick {report.SimulationTick}: client {report.Hash}, host {expectedHash}.");
        StateDivergenceDetected?.Invoke(report.SimulationTick, report.Hash, expectedHash);
        var authoritative = new SimulationStateHash
        {
            PlayerId = 1,
            SimulationTick = report.SimulationTick,
            Hash = expectedHash
        };
        sendAuthoritative(new ProtocolEnvelope(ProtocolMessageType.StateHash, NextSequence(), authoritative.ToPayload()));
    }

    private void CompareAuthoritativeStateHash(ProtocolEnvelope envelope)
    {
        if (!_liveSyncClient.TryCompareAuthoritativeStateHash(
                envelope,
                out var authoritative,
                out var divergent,
                out var localHash,
                out var rejection))
        {
            _log($"Ignored authoritative state hash: {rejection}");
            return;
        }

        if (!divergent || authoritative == null)
        {
            return;
        }

        var message = $"State divergence at tick {authoritative.SimulationTick}: local {localHash}, host {authoritative.Hash}.";
        _state.SetError(message);
        StateDivergenceDetected?.Invoke(authoritative.SimulationTick, localHash, authoritative.Hash);
    }

    private void InitializeHostLiveSync()
    {
        _isHost = true;
        _localPlayerId = 1;
        _liveSyncHost.RegisterPlayer(HostSenderId, _localPlayerId);
    }

    private void SendDirectRejection(NetPeer peer, string reason)
    {
        _transport?.Send(peer, RejectionEnvelope(reason));
    }

    private void SendRelayRejection(string connectionId, string reason)
    {
        _relayTransport?.SendToClient(connectionId, RejectionEnvelope(reason));
    }

    private ProtocolEnvelope RejectionEnvelope(string reason)
    {
        return new ProtocolEnvelope(
            ProtocolMessageType.TextStatus,
            NextSequence(),
            Encoding.UTF8.GetBytes($"live-sync-rejected: {reason}"));
    }

    private static string DirectSenderId(NetPeer peer)
    {
        return $"direct:{peer.Id}";
    }

    private uint NextSequence()
    {
        return _nextSequence++;
    }
}
