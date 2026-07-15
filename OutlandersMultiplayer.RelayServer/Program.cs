using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using OutlandersMultiplayer.Core.Protocol;
using OutlandersMultiplayer.Core.Relay;

var port = args.Length > 0 && int.TryParse(args[0], out var parsed)
    ? parsed
    : ProtocolConstants.DefaultPort + 1;

var server = new RelayServer(port);
await server.RunAsync();

internal sealed class RelayServer
{
    private readonly int _port;
    private readonly ConcurrentDictionary<string, RelayRoom> _rooms = new(StringComparer.OrdinalIgnoreCase);

    public RelayServer(int port)
    {
        _port = port;
    }

    public async Task RunAsync()
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        Console.WriteLine($"Outlanders relay listening on TCP {_port}");

        while (true)
        {
            var tcpClient = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleClientAsync(tcpClient));
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient)
    {
        var connection = new RelayConnection(tcpClient);
        try
        {
            var joinFrame = RelayFrame.Read(connection.Stream);
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
                var frame = RelayFrame.Read(connection.Stream);
                if (frame.Type is RelayFrameType.Protocol or RelayFrameType.RoutedProtocol)
                {
                    room.Forward(connection, frame);
                }
            }
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
        }
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
