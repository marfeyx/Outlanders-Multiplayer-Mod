using System.Net;
using System.Security.Cryptography.X509Certificates;
using OutlandersMultiplayer.Core.Protocol;
using OutlandersMultiplayer.RelayServer;

try
{
    var options = RelayServerOptions.Parse(args);
    using var certificate = options.LoadCertificate();
    var server = new RelayServer(
        options.Port,
        options.HandshakeTimeout,
        options.ClientReadTimeout,
        certificate,
        options.ListenAddress,
        options.AllowInsecureLocalhost);
    await server.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Relay server failed to start: {ex.Message}");
    Environment.ExitCode = 2;
}

internal sealed class RelayServerOptions
{
    private const string CertificatePathVariable = "OUTLANDERS_RELAY_CERTIFICATE_PATH";
    private const string CertificatePasswordVariable = "OUTLANDERS_RELAY_CERTIFICATE_PASSWORD";

    public int Port { get; private set; } = ProtocolConstants.DefaultPort + 1;
    public TimeSpan HandshakeTimeout { get; private set; } = RelayServer.DefaultHandshakeTimeout;
    public TimeSpan ClientReadTimeout { get; private set; } = RelayServer.DefaultClientReadTimeout;
    public string CertificatePath { get; private set; } = string.Empty;
    public string CertificatePasswordEnvironmentVariable { get; private set; } = CertificatePasswordVariable;
    public IPAddress ListenAddress { get; private set; } = IPAddress.Any;
    public bool AllowInsecureLocalhost { get; private set; }

    public static RelayServerOptions Parse(string[] args)
    {
        var options = new RelayServerOptions();
        var positional = new List<string>();

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--certificate":
                    options.CertificatePath = ReadValue(args, ref index, argument);
                    break;
                case "--certificate-password-env":
                    options.CertificatePasswordEnvironmentVariable = ReadValue(args, ref index, argument);
                    break;
                case "--listen-address":
                    var address = ReadValue(args, ref index, argument);
                    if (!IPAddress.TryParse(address, out var parsedAddress))
                    {
                        throw new ArgumentException("--listen-address must be an IP address.");
                    }

                    options.ListenAddress = parsedAddress;
                    break;
                case "--allow-insecure-localhost":
                    options.AllowInsecureLocalhost = true;
                    break;
                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown relay option '{argument}'.");
                    }

                    positional.Add(argument);
                    break;
            }
        }

        if (positional.Count > 3)
        {
            throw new ArgumentException("Expected at most port, handshake timeout, and client read timeout positional arguments.");
        }

        if (positional.Count > 0)
        {
            options.Port = ParsePort(positional[0]);
        }

        if (positional.Count > 1)
        {
            options.HandshakeTimeout = ParseSeconds(positional[1], "handshake timeout");
        }

        if (positional.Count > 2)
        {
            options.ClientReadTimeout = ParseSeconds(positional[2], "client read timeout");
        }

        if (string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            options.CertificatePath = Environment.GetEnvironmentVariable(CertificatePathVariable) ?? string.Empty;
        }

        if (options.AllowInsecureLocalhost)
        {
            if (!string.IsNullOrWhiteSpace(options.CertificatePath))
            {
                throw new ArgumentException("Do not combine --allow-insecure-localhost with a TLS certificate.");
            }

            if (options.ListenAddress.Equals(IPAddress.Any) || options.ListenAddress.Equals(IPAddress.IPv6Any))
            {
                options.ListenAddress = IPAddress.Loopback;
            }

            if (!IPAddress.IsLoopback(options.ListenAddress))
            {
                throw new ArgumentException("--allow-insecure-localhost requires a loopback --listen-address.");
            }
        }
        else if (string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            throw new ArgumentException(
                $"Configure a PFX certificate with --certificate or {CertificatePathVariable}. " +
                "For local development only, use --allow-insecure-localhost.");
        }

        return options;
    }

    public X509Certificate2? LoadCertificate()
    {
        if (AllowInsecureLocalhost)
        {
            return null;
        }

        var password = Environment.GetEnvironmentVariable(CertificatePasswordEnvironmentVariable);
        try
        {
            return new X509Certificate2(
                CertificatePath,
                password,
                OperatingSystem.IsWindows()
                    ? X509KeyStorageFlags.UserKeySet
                    : X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException(
                $"Could not load the relay TLS certificate. Check the PFX path and {CertificatePasswordEnvironmentVariable}.",
                ex);
        }
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[index];
    }

    private static int ParsePort(string value)
    {
        if (int.TryParse(value, out var port) && port is >= 0 and <= 65535)
        {
            return port;
        }

        throw new ArgumentException("The port must be between 0 and 65535.");
    }

    private static TimeSpan ParseSeconds(string value, string name)
    {
        if (double.TryParse(value, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        throw new ArgumentException($"The {name} must be a positive number of seconds.");
    }
}
