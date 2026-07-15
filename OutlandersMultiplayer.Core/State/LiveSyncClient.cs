using System;
using System.Collections.Generic;
using OutlandersMultiplayer.Core.Protocol;

namespace OutlandersMultiplayer.Core.State;

public sealed class LiveSyncClient
{
    private const int HashHistoryCapacity = 256;
    private readonly DuplicateSequenceFilter _sequences = new();
    private readonly Dictionary<long, string> _localHashes = new();
    private readonly Queue<long> _hashOrder = new();
    private ulong _lastCommandId;

    public void Reset()
    {
        _sequences.Reset();
        _localHashes.Clear();
        _hashOrder.Clear();
        _lastCommandId = 0;
    }

    public bool TryAcceptCommand(ProtocolEnvelope envelope, out CommandEnvelope? command, out string rejection)
    {
        command = null;
        if (!TryBegin(envelope, ProtocolMessageType.AcceptedCommand, out rejection))
        {
            return false;
        }

        try
        {
            command = CommandEnvelope.FromPayload(envelope.Payload);
        }
        catch (Exception ex)
        {
            rejection = $"Accepted command payload is invalid: {ex.Message}";
            return false;
        }

        if (command.CommandId == 0 || command.CommandId <= _lastCommandId)
        {
            rejection = $"Accepted command ID {command.CommandId} is duplicate or out of order.";
            return false;
        }

        if (command.PlayerId == 0 || command.SimulationTick < 0 || string.IsNullOrWhiteSpace(command.CommandType))
        {
            rejection = "Accepted command metadata is invalid.";
            return false;
        }

        _lastCommandId = command.CommandId;
        rejection = string.Empty;
        return true;
    }

    public void RecordLocalStateHash(SimulationStateHash stateHash)
    {
        stateHash.Validate();
        if (!_localHashes.ContainsKey(stateHash.SimulationTick))
        {
            _hashOrder.Enqueue(stateHash.SimulationTick);
        }

        _localHashes[stateHash.SimulationTick] = stateHash.Hash;
        while (_hashOrder.Count > HashHistoryCapacity)
        {
            _localHashes.Remove(_hashOrder.Dequeue());
        }
    }

    public bool TryCompareAuthoritativeStateHash(
        ProtocolEnvelope envelope,
        out SimulationStateHash? authoritative,
        out bool divergent,
        out string localHash,
        out string rejection)
    {
        authoritative = null;
        divergent = false;
        localHash = string.Empty;
        if (!TryBegin(envelope, ProtocolMessageType.StateHash, out rejection))
        {
            return false;
        }

        try
        {
            authoritative = SimulationStateHash.FromPayload(envelope.Payload);
            authoritative.Validate();
        }
        catch (Exception ex)
        {
            rejection = $"Authoritative state hash payload is invalid: {ex.Message}";
            return false;
        }

        if (authoritative.PlayerId != 1)
        {
            rejection = $"Authoritative state hash claimed non-host player {authoritative.PlayerId}.";
            return false;
        }

        if (_localHashes.TryGetValue(authoritative.SimulationTick, out localHash))
        {
            divergent = !StringComparer.OrdinalIgnoreCase.Equals(authoritative.Hash, localHash);
        }

        rejection = string.Empty;
        return true;
    }

    private bool TryBegin(ProtocolEnvelope envelope, ProtocolMessageType expectedType, out string rejection)
    {
        if (envelope.Type != expectedType)
        {
            rejection = $"Expected {expectedType}, received {envelope.Type}.";
            return false;
        }

        if (!_sequences.Accept(envelope.Sequence))
        {
            rejection = $"Envelope sequence {envelope.Sequence} is duplicate or out of order.";
            return false;
        }

        rejection = string.Empty;
        return true;
    }
}
