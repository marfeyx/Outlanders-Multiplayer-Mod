namespace OutlandersMultiplayer.Core.Protocol;

public enum ProtocolMessageType : byte
{
    HandshakeRequest = 1,
    HandshakeAccepted = 2,
    HandshakeRejected = 3,
    SnapshotManifest = 4,
    SnapshotChunk = 5,
    PlayerIntent = 6,
    AcceptedCommand = 7,
    StateHash = 8,
    TextStatus = 9,
    DisconnectNotice = 10,
    SnapshotStateHash = 11
}
