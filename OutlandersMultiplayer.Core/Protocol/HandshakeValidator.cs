namespace OutlandersMultiplayer.Core.Protocol;

public static class HandshakeValidator
{
    public static HandshakeResponse ValidateForHost(HandshakeRequest request, string expectedSessionKey)
    {
        if (request.OutlandersBuildGuid != ProtocolConstants.ExpectedOutlandersBuildGuid)
        {
            return Reject("Outlanders build GUID does not match the host.");
        }

        if (request.UnityVersion != ProtocolConstants.ExpectedUnityVersion)
        {
            return Reject("Unity runtime version does not match the host.");
        }

        if (!string.IsNullOrEmpty(expectedSessionKey) && request.SessionKey != expectedSessionKey)
        {
            return Reject("Session key is incorrect.");
        }

        return new HandshakeResponse { Accepted = true, Reason = "Accepted" };
    }

    private static HandshakeResponse Reject(string reason)
    {
        return new HandshakeResponse { Accepted = false, Reason = reason };
    }
}
