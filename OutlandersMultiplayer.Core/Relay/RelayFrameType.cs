namespace OutlandersMultiplayer.Core.Relay;

public enum RelayFrameType : byte
{
    Join = 1,
    Protocol = 2,
    Rejected = 3,
    Status = 4
}
