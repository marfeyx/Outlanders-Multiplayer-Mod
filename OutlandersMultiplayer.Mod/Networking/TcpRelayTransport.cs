using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using OutlandersMultiplayer.Core.Protocol;
using OutlandersMultiplayer.Core.Relay;

namespace OutlandersMultiplayer.Mod.Networking;

public sealed class TcpRelayTransport : IDisposable
{
    private readonly ConcurrentQueue<ProtocolEnvelope> _incoming = new();
    private readonly object _sendLock = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _readerThread;
    private volatile bool _running;

    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<ProtocolEnvelope>? MessageReceived;
    public event Action<string>? StatusReceived;
    public event Action<string>? Rejected;

    public bool IsRunning => _running;

    public void Connect(string relayHost, int relayPort, RelayJoinRequest joinRequest)
    {
        Stop();

        _client = new TcpClient();
        _client.NoDelay = true;
        _client.Connect(relayHost, relayPort);
        _stream = _client.GetStream();
        RelayFrame.Write(_stream, new RelayFrame(RelayFrameType.Join, joinRequest.ToPayload()));

        _running = true;
        _readerThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "OutlandersMultiplayerRelayReader"
        };
        _readerThread.Start();
        Connected?.Invoke();
    }

    public void Poll()
    {
        while (_incoming.TryDequeue(out var envelope))
        {
            MessageReceived?.Invoke(envelope);
        }
    }

    public void Send(ProtocolEnvelope envelope)
    {
        var stream = _stream;
        if (stream == null)
        {
            return;
        }

        lock (_sendLock)
        {
            RelayFrame.Write(stream, new RelayFrame(RelayFrameType.Protocol, ProtocolSerializer.Pack(envelope)));
        }
    }

    public void Stop()
    {
        _running = false;
        try { _client?.Close(); } catch { }
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private void ReadLoop()
    {
        try
        {
            while (_running && _stream != null)
            {
                var frame = RelayFrame.Read(_stream);
                switch (frame.Type)
                {
                    case RelayFrameType.Protocol:
                        _incoming.Enqueue(ProtocolSerializer.Unpack(frame.Payload));
                        break;
                    case RelayFrameType.Status:
                        StatusReceived?.Invoke(Encoding.UTF8.GetString(frame.Payload));
                        break;
                    case RelayFrameType.Rejected:
                        Rejected?.Invoke(frame.GetUtf8Payload());
                        Stop();
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            if (_running)
            {
                Disconnected?.Invoke(ex.Message);
            }
        }
        finally
        {
            _running = false;
        }
    }
}
