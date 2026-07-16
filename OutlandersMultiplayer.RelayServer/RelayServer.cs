using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using OutlandersMultiplayer.Core.Relay;

namespace OutlandersMultiplayer.RelayServer;

public sealed class RelayServer
{
    public static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultClientReadTimeout = TimeSpan.FromMinutes(2);

    private readonly int _port;
    private readonly TimeSpan _handshakeTimeout;
    private readonly TimeSpan _clientReadTimeout;
    private readonly ConcurrentDictionary<string, RelayRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private int _activeConnectionCount;

    public RelayServer(
        int port,
        TimeSpan? handshakeTimeout = null,
        TimeSpan? clientReadTimeout = null)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        _port = port;
        _handshakeTimeout = ValidateTimeout(handshakeTimeout ?? DefaultHandshakeTimeout, nameof(handshakeTimeout));
        _clientReadTimeout = ValidateTimeout(clientReadTimeout ?? DefaultClientReadTimeout, nameof(clientReadTimeout));
    }

    public int ListeningPort { get; private set; }
    public int ActiveConnectionCount => Volatile.Read(ref _activeConnectionCount);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        ListeningPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.WriteLine(
            $"Outlanders relay listening on TCP {ListeningPort} " +
            $"(handshake timeout {_handshakeTimeout.TotalSeconds:g}s, read timeout {_clientReadTimeout.TotalSeconds:g}s)");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tcpClient = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(tcpClient, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeConnectionCount);
        var connection = new RelayConnection(tcpClient);
        try
        {
            var joinFrame = await RelayFrameReader.ReadAsync(
                connection.Stream,
                _handshakeTimeout,
                cancellationToken,
                "initial Join frame");
            if (joinFrame.Type != RelayFrameType.Join)
            {
                connection.Send(RelayFrame.Rejected("First relay frame must be Join."));
                return;
            }

            var join = RelayJoinRequest.FromPayload(joinFrame.Payload);
            var roomCode = NormalizeRoomCode(join.RoomCode);
            if (string.IsNullOrWhiteSpace(roomCode))
            {
                connection.Send(RelayFrame.Rejected("Room code is required."));
                return;
            }

            connection.PlayerName = join.PlayerName;
            connection.Role = join.Role;
            connection.RoomCode = roomCode;

            var room = _rooms.GetOrAdd(roomCode, code => new RelayRoom(code));
            if (!room.TryJoin(connection, join, out var rejection))
            {
                connection.Send(RelayFrame.Rejected(rejection));
                return;
            }

            Console.WriteLine($"{join.Role} '{join.PlayerName}' joined room {roomCode}");
            connection.Send(new RelayFrame(RelayFrameType.Status, System.Text.Encoding.UTF8.GetBytes("relay-connected")));

            while (connection.IsConnected)
            {
                var frame = await RelayFrameReader.ReadAsync(
                    connection.Stream,
                    _clientReadTimeout,
                    cancellationToken,
                    "client frame");
                if (frame.Type is RelayFrameType.Protocol or RelayFrameType.RoutedProtocol)
                {
                    room.Forward(connection, frame);
                }
            }
        }
        catch (TimeoutException ex)
        {
            Console.WriteLine($"Relay client timed out: {ex.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Relay connection error: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(connection.RoomCode) && _rooms.TryGetValue(connection.RoomCode, out var room))
            {
                room.Leave(connection);
                if (room.IsEmpty)
                {
                    _rooms.TryRemove(connection.RoomCode, out _);
                }
            }

            connection.Dispose();
            Interlocked.Decrement(ref _activeConnectionCount);
        }
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Timeout must be positive and no longer than 24.8 days.");
        }

        return timeout;
    }

    private static string NormalizeRoomCode(string roomCode)
    {
        return new string((roomCode ?? string.Empty)
            .Trim()
            .Where(char.IsLetterOrDigit)
            .Take(32)
            .Select(char.ToUpperInvariant)
            .ToArray());
    }
}

internal static class RelayFrameReader
{
    private const int MaximumFrameLength = 8 * 1024 * 1024;

    public static async Task<RelayFrame> ReadAsync(
        Stream stream,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string operation)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            var lengthBytes = new byte[sizeof(int)];
            await ReadExactlyAsync(stream, lengthBytes, timeoutSource.Token);
            var length = BitConverter.ToInt32(lengthBytes, 0);
            if (length <= 0 || length > MaximumFrameLength)
            {
                throw new InvalidDataException("Relay frame length is invalid.");
            }

            var body = new byte[length];
            await ReadExactlyAsync(stream, body, timeoutSource.Token);

            using var completeFrame = new MemoryStream(sizeof(int) + length);
            completeFrame.Write(lengthBytes);
            completeFrame.Write(body);
            completeFrame.Position = 0;
            return RelayFrame.Read(completeFrame);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for {operation} after {timeout.TotalSeconds:g} seconds.");
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[bytesRead..], cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            bytesRead += count;
        }
    }
}

internal sealed class RelayRoom
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RelayConnection> _clients = new(StringComparer.Ordinal);
    private RelayConnection? _host;
    private string _sessionKey = string.Empty;

    public RelayRoom(string code)
    {
        Code = code;
    }

    public string Code { get; }

    public bool IsEmpty
    {
        get
        {
            lock (_sync)
            {
                return _host == null && _clients.Count == 0;
            }
        }
    }

    public bool TryJoin(RelayConnection connection, RelayJoinRequest join, out string rejection)
    {
        lock (_sync)
        {
            if (join.Role == RelayRole.Host)
            {
                if (_host != null)
                {
                    rejection = "Room already has a host.";
                    return false;
                }

                _host = connection;
                _sessionKey = join.SessionKey ?? string.Empty;
                rejection = string.Empty;
                return true;
            }

            if (_host == null)
            {
                rejection = "Room host is not connected yet.";
                return false;
            }

            if (_sessionKey != (join.SessionKey ?? string.Empty))
            {
                rejection = "Session key is incorrect.";
                return false;
            }

            _clients.Add(connection.ConnectionId, connection);
            rejection = string.Empty;
            return true;
        }
    }

    public void Forward(RelayConnection sender, RelayFrame frame)
    {
        lock (_sync)
        {
            if (sender == _host)
            {
                var recipients = RelayRouting.SelectHostRecipients(frame, _clients.Keys);
                var clientFrame = RelayRouting.ForClient(frame);
                foreach (var connectionId in recipients)
                {
                    if (_clients.TryGetValue(connectionId, out var client))
                    {
                        client.Send(clientFrame);
                    }
                }
            }
            else
            {
                if (frame.Type != RelayFrameType.Protocol)
                {
                    sender.Send(RelayFrame.Rejected("Clients may not supply relay routing metadata."));
                    return;
                }

                _host?.Send(RelayRouting.FromClient(sender.ConnectionId, frame));
            }
        }
    }

    public void Leave(RelayConnection connection)
    {
        lock (_sync)
        {
            if (connection == _host)
            {
                _host = null;
                foreach (var client in _clients.Values.ToArray())
                {
                    client.Send(RelayFrame.Rejected("Host disconnected."));
                    client.Dispose();
                }

                _clients.Clear();
            }
            else
            {
                _clients.Remove(connection.ConnectionId);
            }
        }
    }
}

internal sealed class RelayConnection : IDisposable
{
    private readonly TcpClient _client;
    private readonly object _sendLock = new();

    public RelayConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        Stream = client.GetStream();
    }

    public NetworkStream Stream { get; }
    public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
    public RelayRole Role { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public string RoomCode { get; set; } = string.Empty;
    public bool IsConnected => _client.Connected;

    public void Send(RelayFrame frame)
    {
        lock (_sendLock)
        {
            RelayFrame.Write(Stream, frame);
        }
    }

    public void Dispose()
    {
        try { _client.Close(); } catch { }
    }
}
