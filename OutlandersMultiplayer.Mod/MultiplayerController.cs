using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteNetLib;
using OutlandersMultiplayer.Core.Protocol;
using OutlandersMultiplayer.Core.Relay;
using OutlandersMultiplayer.Core.Session;
using OutlandersMultiplayer.Core.Snapshots;
using OutlandersMultiplayer.Mod.Game;
using OutlandersMultiplayer.Mod.Networking;

namespace OutlandersMultiplayer.Mod;

public sealed class MultiplayerController : IDisposable
{
    private readonly SessionState _state;
    private readonly Action<string> _log;
    private readonly Dictionary<string, SnapshotChunk> _receivedChunks = new();
    private readonly Dictionary<string, string> _relayPlayerNames = new(StringComparer.Ordinal);
    private LiteNetLibDirectTransport? _transport;
    private TcpRelayTransport? _relayTransport;
    private SnapshotPackage? _hostSnapshot;
    private SnapshotManifest? _clientManifest;
    private uint _nextSequence = 1;
    private uint _nextPlayerId = 2;
    private string _sessionKey = string.Empty;

    public MultiplayerController(SessionState state, Action<string> log)
    {
        _state = state;
        _log = log;
    }

    public SessionState State => _state;

    public void Host(int port, string sessionKey)
    {
        Disconnect();
        _sessionKey = sessionKey ?? string.Empty;

        var savePath = OutlandersSaveLocator.FindLatestSandboxSave();
        if (savePath == null)
        {
            _state.SetError("No Endless/Sandbox save file was found.");
            return;
        }

        var saveBytes = File.ReadAllBytes(savePath);
        _hostSnapshot = SnapshotService.Create(Path.GetFileName(savePath), saveBytes);

        _transport = new LiteNetLibDirectTransport();
        _transport.ConnectionRequested += request => request.AcceptIfKey(_sessionKey);
        _transport.PeerConnected += peer =>
        {
            _state.SetPlayers(new[] { "Host", peer.ToString() });
            _log($"Client connected: {peer}");
        };
        _transport.PeerDisconnected += (peer, info) =>
        {
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
        _sessionKey = sessionKey ?? string.Empty;

        var savePath = OutlandersSaveLocator.FindLatestSandboxSave();
        if (savePath == null)
        {
            _state.SetError("No Endless/Sandbox save file was found.");
            return;
        }

        _hostSnapshot = SnapshotService.Create(Path.GetFileName(savePath), File.ReadAllBytes(savePath));
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
        _receivedChunks.Clear();
        _relayPlayerNames.Clear();
        _state.SetStatus(SessionStatus.Offline, "Offline");
        _state.SetPlayers(Array.Empty<string>());
    }

    public void Dispose()
    {
        Disconnect();
    }

    private void SendHandshake(string playerName)
    {
        var request = new HandshakeRequest
        {
            PlayerName = string.IsNullOrWhiteSpace(playerName) ? Environment.UserName : playerName,
            SessionKey = _sessionKey,
            SaveHash = string.Empty
        };

        _transport?.SendToServer(new ProtocolEnvelope(ProtocolMessageType.HandshakeRequest, NextSequence(), request.ToPayload()));
        _relayTransport?.Send(new ProtocolEnvelope(ProtocolMessageType.HandshakeRequest, NextSequence(), request.ToPayload()));
    }

    private void HandleHostMessage(NetPeer peer, ProtocolEnvelope envelope)
    {
        if (envelope.Type != ProtocolMessageType.HandshakeRequest)
        {
            return;
        }

        var request = HandshakeRequest.FromPayload(envelope.Payload);
        var response = HandshakeValidator.ValidateForHost(request, _sessionKey);
        if (response.Accepted)
        {
            response.AssignedPlayerId = _nextPlayerId++;
        }

        var responseType = response.Accepted ? ProtocolMessageType.HandshakeAccepted : ProtocolMessageType.HandshakeRejected;
        _transport?.Send(peer, new ProtocolEnvelope(responseType, NextSequence(), response.ToPayload()));
        if (!response.Accepted || _hostSnapshot == null)
        {
            return;
        }

        _transport?.Send(peer, new ProtocolEnvelope(ProtocolMessageType.SnapshotManifest, NextSequence(), _hostSnapshot.Manifest.ToPayload()));
        foreach (var chunk in _hostSnapshot.Chunks)
        {
            _transport?.Send(peer, new ProtocolEnvelope(ProtocolMessageType.SnapshotChunk, NextSequence(), chunk.ToPayload()));
        }

        _log($"Sent snapshot {_hostSnapshot.Manifest.SnapshotId} to {request.PlayerName} ({_hostSnapshot.Manifest.ChunkCount} chunks).");
    }

    private void HandleHostRelayMessage(string connectionId, ProtocolEnvelope envelope)
    {
        if (envelope.Type != ProtocolMessageType.HandshakeRequest || string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        var request = HandshakeRequest.FromPayload(envelope.Payload);
        var response = HandshakeValidator.ValidateForHost(request, _sessionKey);
        if (response.Accepted)
        {
            response.AssignedPlayerId = _nextPlayerId++;
        }

        var responseType = response.Accepted ? ProtocolMessageType.HandshakeAccepted : ProtocolMessageType.HandshakeRejected;
        _relayTransport?.SendToClient(connectionId, new ProtocolEnvelope(responseType, NextSequence(), response.ToPayload()));
        if (!response.Accepted || _hostSnapshot == null)
        {
            return;
        }

        _relayTransport?.SendToClient(connectionId, new ProtocolEnvelope(ProtocolMessageType.SnapshotManifest, NextSequence(), _hostSnapshot.Manifest.ToPayload()));
        foreach (var chunk in _hostSnapshot.Chunks)
        {
            _relayTransport?.SendToClient(connectionId, new ProtocolEnvelope(ProtocolMessageType.SnapshotChunk, NextSequence(), chunk.ToPayload()));
        }

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
                _state.SetStatus(SessionStatus.Connected, $"Accepted as player {response.AssignedPlayerId}");
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

    private uint NextSequence()
    {
        return _nextSequence++;
    }
}
