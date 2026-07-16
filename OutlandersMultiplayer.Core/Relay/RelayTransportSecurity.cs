using System;
using System.Net;

namespace OutlandersMultiplayer.Core.Relay;

public sealed class RelayTransportSecurity
{
    private RelayTransportSecurity(bool allowInsecureLocalhost)
    {
        AllowInsecureLocalhost = allowInsecureLocalhost;
    }

    public static RelayTransportSecurity Tls { get; } = new(false);
    public static RelayTransportSecurity InsecureLocalhost { get; } = new(true);

    public bool AllowInsecureLocalhost { get; }

    public static bool IsLiteralLoopback(string host)
    {
        var value = (host ?? string.Empty).Trim();
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(value, out var address) && IPAddress.IsLoopback(address);
    }
}
