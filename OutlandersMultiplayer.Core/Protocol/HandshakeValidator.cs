using System;

namespace OutlandersMultiplayer.Core.Protocol;

public static class HandshakeValidator
{
    public static HandshakeResponse ValidateForHost(
        HandshakeRequest request,
        string expectedSessionKey,
        RuntimeCompatibility host,
        string hostSaveHash)
    {
        if (!host.IsComplete)
        {
            return Reject("The host could not determine its runtime compatibility information.");
        }

        if (request.ProtocolVersion != host.ProtocolVersion)
        {
            return Reject($"Multiplayer protocol version {request.ProtocolVersion} does not match host version {host.ProtocolVersion}.");
        }

        if (!StringComparer.Ordinal.Equals(request.ModVersion, host.ModVersion))
        {
            return Reject($"Outlanders Multiplayer mod version {Display(request.ModVersion)} does not match host version {host.ModVersion}.");
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(request.OutlandersBuildGuid, host.OutlandersBuildGuid))
        {
            return Reject($"Outlanders build {Display(request.OutlandersBuildGuid)} does not match host build {host.OutlandersBuildGuid}.");
        }

        if (!StringComparer.Ordinal.Equals(request.UnityVersion, host.UnityVersion))
        {
            return Reject($"Unity runtime {Display(request.UnityVersion)} does not match host runtime {host.UnityVersion}.");
        }

        if (string.IsNullOrWhiteSpace(expectedSessionKey))
        {
            return Reject("The host session key is missing.");
        }

        if (string.IsNullOrWhiteSpace(request.SessionKey) || request.SessionKey != expectedSessionKey)
        {
            return Reject("Session key is incorrect.");
        }

        if (!string.IsNullOrEmpty(request.SaveHash) && !IsSha256(request.SaveHash))
        {
            return Reject("Client save hash is not a valid SHA-256 value.");
        }

        if (!IsSha256(hostSaveHash))
        {
            return Reject("The host snapshot does not have a valid save hash.");
        }

        var snapshotRequired = !StringComparer.OrdinalIgnoreCase.Equals(request.SaveHash, hostSaveHash);
        return new HandshakeResponse
        {
            Accepted = true,
            Reason = snapshotRequired ? "Accepted; save resync required." : "Accepted; save state matches host.",
            HostSaveHash = hostSaveHash,
            SnapshotRequired = snapshotRequired
        };
    }

    private static HandshakeResponse Reject(string reason)
    {
        return new HandshakeResponse { Accepted = false, Reason = reason };
    }

    private static bool IsSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static string Display(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<unavailable>" : value;
    }
}
