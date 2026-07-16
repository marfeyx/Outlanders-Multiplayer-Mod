namespace OutlandersMultiplayer.Core.Relay;

public enum RelayFrameType : byte
{
    Join = 1,
    Protocol = 2,
    Rejected = 3,
    Status = 4,
    // Client-to-host frames carry their source ID; host-to-client frames carry their target ID.
    RoutedProtocol = 5
}
