using System;
using System.Collections.Generic;
using System.IO;
using OutlandersMultiplayer.Core.Protocol;

namespace OutlandersMultiplayer.Core.State;

public sealed class LiveSyncHost
{
    private const int HashHistoryCapacity = 256;
    private readonly Dictionary<string, PlayerState> _players = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _authoritativeHashes = new();
    private readonly Queue<long> _hashOrder = new();
    private ulong _nextCommandId = 1;

    public void RegisterPlayer(string senderId, uint playerId)
    {
        if (string.IsNullOrWhiteSpace(senderId)) throw new ArgumentException("Sender ID is required.", nameof(senderId));
        if (playerId == 0) throw new ArgumentOutOfRangeException(nameof(playerId));
        _players[senderId] = new PlayerState(playerId);
    }

    public void UnregisterPlayer(string senderId)
    {
        if (!string.IsNullOrWhiteSpace(senderId))
        {
            _players.Remove(senderId);
        }
    }

    public void Reset()
    {
        _players.Clear();
        _authoritativeHashes.Clear();
        _hashOrder.Clear();
        _nextCommandId = 1;
    }

    public bool TryAcceptIntent(
        string senderId,
        ProtocolEnvelope envelope,
        out CommandEnvelope? acceptedCommand,
        out string rejection)
    {
        acceptedCommand = null;
        if (!TryBegin(senderId, envelope, ProtocolMessageType.PlayerIntent, out var player, out rejection))
        {
            return false;
        }

        CommandEnvelope intent;
        try
        {
            intent = CommandEnvelope.FromPayload(envelope.Payload);
        }
        catch (Exception ex)
        {
            rejection = $"Player intent payload is invalid: {ex.Message}";
            return false;
        }

        if (intent.PlayerId != player.PlayerId)
        {
            rejection = $"Player intent claimed player {intent.PlayerId}, but sender is player {player.PlayerId}.";
            return false;
        }

        if (intent.CommandId != 0)
        {
            rejection = "Player intents must not assign authoritative command IDs.";
            return false;
        }

        if (intent.SimulationTick < 0 || intent.SimulationTick < player.LastCommandTick)
        {
            rejection = $"Player intent tick {intent.SimulationTick} is older than accepted tick {player.LastCommandTick}.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(intent.CommandType) || string.IsNullOrWhiteSpace(intent.JsonPayload))
        {
            rejection = "Player intent requires a command type and JSON payload.";
            return false;
        }

        player.LastCommandTick = intent.SimulationTick;
        acceptedCommand = new CommandEnvelope
        {
            CommandId = _nextCommandId++,
            PlayerId = player.PlayerId,
            SimulationTick = intent.SimulationTick,
            CommandType = intent.CommandType,
            JsonPayload = intent.JsonPayload
        };
        rejection = string.Empty;
        return true;
    }

    public void SetAuthoritativeStateHash(SimulationStateHash stateHash)
    {
        stateHash.Validate();
        if (stateHash.PlayerId != 1)
        {
            throw new InvalidDataException("Authoritative state hashes must belong to host player 1.");
        }

        if (!_authoritativeHashes.ContainsKey(stateHash.SimulationTick))
        {
            _hashOrder.Enqueue(stateHash.SimulationTick);
        }

        _authoritativeHashes[stateHash.SimulationTick] = stateHash.Hash;
        while (_hashOrder.Count > HashHistoryCapacity)
        {
            _authoritativeHashes.Remove(_hashOrder.Dequeue());
        }
    }

    public bool TryCheckStateHash(
        string senderId,
        ProtocolEnvelope envelope,
        out SimulationStateHash? stateHash,
        out bool divergent,
        out string expectedHash,
        out string rejection)
    {
        stateHash = null;
        divergent = false;
        expectedHash = string.Empty;
        if (!TryBegin(senderId, envelope, ProtocolMessageType.StateHash, out var player, out rejection))
        {
            return false;
        }

        try
        {
            stateHash = SimulationStateHash.FromPayload(envelope.Payload);
            stateHash.Validate();
        }
        catch (Exception ex)
        {
            rejection = $"State hash payload is invalid: {ex.Message}";
            return false;
        }

        if (stateHash.PlayerId != player.PlayerId)
        {
            rejection = $"State hash claimed player {stateHash.PlayerId}, but sender is player {player.PlayerId}.";
            return false;
        }

        if (_authoritativeHashes.TryGetValue(stateHash.SimulationTick, out expectedHash))
        {
            divergent = !StringComparer.OrdinalIgnoreCase.Equals(stateHash.Hash, expectedHash);
        }

        rejection = string.Empty;
        return true;
    }

    private bool TryBegin(
        string senderId,
        ProtocolEnvelope envelope,
        ProtocolMessageType expectedType,
        out PlayerState player,
        out string rejection)
    {
        if (envelope.Type != expectedType)
        {
            player = null!;
            rejection = $"Expected {expectedType}, received {envelope.Type}.";
            return false;
        }

        if (!_players.TryGetValue(senderId, out player!))
        {
            rejection = "Sender has not completed the multiplayer handshake.";
            return false;
        }

        if (!player.Sequences.Accept(envelope.Sequence))
        {
            rejection = $"Envelope sequence {envelope.Sequence} is duplicate or out of order.";
            return false;
        }

        rejection = string.Empty;
        return true;
    }

    private sealed class PlayerState
    {
        public PlayerState(uint playerId)
        {
            PlayerId = playerId;
        }

        public uint PlayerId { get; }
        public DuplicateSequenceFilter Sequences { get; } = new();
        public long LastCommandTick { get; set; } = -1;
    }
}
