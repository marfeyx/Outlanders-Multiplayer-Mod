using System;
using System.Collections.Generic;
using System.IO;
using OutlandersMultiplayer.Core.Protocol;

namespace OutlandersMultiplayer.Core.State;

public enum SnapshotHashDecision
{
    Rejected,
    Verified,
    Resend,
    RetryExhausted
}

public sealed class SnapshotHashTracker
{
    private readonly HashSet<string> _acceptedPeers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingSnapshot> _pending = new(StringComparer.Ordinal);

    public void RegisterPeer(string senderId)
    {
        if (string.IsNullOrWhiteSpace(senderId)) throw new ArgumentException("Sender ID is required.", nameof(senderId));
        _acceptedPeers.Add(senderId);
        _pending.Remove(senderId);
    }

    public void ExpectSnapshot(string senderId, string snapshotId)
    {
        if (!_acceptedPeers.Contains(senderId))
        {
            throw new InvalidOperationException("The peer must complete the handshake before receiving a snapshot.");
        }

        if (string.IsNullOrWhiteSpace(snapshotId)) throw new ArgumentException("Snapshot ID is required.", nameof(snapshotId));
        _pending[senderId] = new PendingSnapshot(snapshotId);
    }

    public void UnregisterPeer(string senderId)
    {
        _acceptedPeers.Remove(senderId);
        _pending.Remove(senderId);
    }

    public void Reset()
    {
        _acceptedPeers.Clear();
        _pending.Clear();
    }

    public SnapshotHashDecision Evaluate(
        string senderId,
        StateHashReport report,
        string expectedHash,
        out string reason)
    {
        if (!_acceptedPeers.Contains(senderId))
        {
            reason = "Sender has not completed the multiplayer handshake.";
            return SnapshotHashDecision.Rejected;
        }

        if (!_pending.TryGetValue(senderId, out var pending))
        {
            reason = "Sender is not awaiting snapshot verification.";
            return SnapshotHashDecision.Rejected;
        }

        if (!StringComparer.Ordinal.Equals(report.SnapshotId, pending.SnapshotId))
        {
            reason = $"Snapshot ID {report.SnapshotId} does not match pending snapshot {pending.SnapshotId}.";
            return SnapshotHashDecision.Rejected;
        }

        ValidateSha256(report.SaveHash, nameof(report.SaveHash));
        ValidateSha256(expectedHash, nameof(expectedHash));
        if (StringComparer.OrdinalIgnoreCase.Equals(report.SaveHash, expectedHash))
        {
            _pending.Remove(senderId);
            reason = string.Empty;
            return SnapshotHashDecision.Verified;
        }

        if (pending.ResendAttempted)
        {
            _pending.Remove(senderId);
            reason = "Snapshot hash still differs after the single targeted retry.";
            return SnapshotHashDecision.RetryExhausted;
        }

        pending.ResendAttempted = true;
        reason = "Snapshot hash differs from the host; one targeted retry is required.";
        return SnapshotHashDecision.Resend;
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (value.Length != 64)
        {
            throw new InvalidDataException($"{parameterName} must be a SHA-256 value.");
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character)) throw new InvalidDataException($"{parameterName} must be hexadecimal.");
        }
    }

    private sealed class PendingSnapshot
    {
        public PendingSnapshot(string snapshotId)
        {
            SnapshotId = snapshotId;
        }

        public string SnapshotId { get; }
        public bool ResendAttempted { get; set; }
    }
}
