using OutlandersMultiplayer.Core.Protocol;
using OutlandersMultiplayer.RelayServer;

var port = ParseIntArgument(0, ProtocolConstants.DefaultPort + 1, "port");
var handshakeTimeout = TimeSpan.FromSeconds(ParseDoubleArgument(
    1,
    RelayServer.DefaultHandshakeTimeout.TotalSeconds,
    "handshake timeout"));
var clientReadTimeout = TimeSpan.FromSeconds(ParseDoubleArgument(
    2,
    RelayServer.DefaultClientReadTimeout.TotalSeconds,
    "client read timeout"));

var server = new RelayServer(port, handshakeTimeout, clientReadTimeout);
await server.RunAsync();

int ParseIntArgument(int index, int defaultValue, string name)
{
    if (args.Length <= index)
    {
        return defaultValue;
    }

    if (int.TryParse(args[index], out var value) && value is >= 0 and <= 65535)
    {
        return value;
    }

    throw new ArgumentException($"The {name} must be between 0 and 65535.");
}

double ParseDoubleArgument(int index, double defaultValue, string name)
{
    if (args.Length <= index)
    {
        return defaultValue;
    }

    if (double.TryParse(args[index], out var value) && value > 0)
    {
        return value;
    }

    throw new ArgumentException($"The {name} must be a positive number of seconds.");
}
