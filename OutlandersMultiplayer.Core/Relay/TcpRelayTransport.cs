using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OutlandersMultiplayer.Core.Protocol;

namespace OutlandersMultiplayer.Core.Relay;

public sealed class TcpRelayTransport : IDisposable
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<QueuedCallback> _callbacks = new();
    private readonly ConcurrentDictionary<int, Task> _workers = new();
    private readonly object _lifecycleLock = new();
    private readonly object _sendLock = new();
    private readonly TimeSpan _connectTimeout;
    private readonly Func<TcpClient, string, int, Task> _connectAsync;
    private CancellationTokenSource? _lifetime;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private int _generation;
    private volatile bool _connecting;
    private volatile bool _running;
    private bool _disposed;

    public TcpRelayTransport(
        TimeSpan? connectTimeout = null,
        Func<TcpClient, string, int, Task>? connectAsync = null)
    {
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
        if (_connectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        }

        _connectAsync = connectAsync ?? ((client, host, port) => client.ConnectAsync(host, port));
    }

    public event Action? Connected;
    public event Action<string>? ConnectionFailed;
    public event Action<string>? Disconnected;
    public event Action<ProtocolEnvelope>? MessageReceived;
    public event Action<string>? StatusReceived;
    public event Action<string>? Rejected;

    public bool IsConnecting => _connecting;
    public bool IsRunning => _running;
    public int ActiveWorkerCount => _workers.Values.Count(task => !task.IsCompleted);

    public void Connect(string relayHost, int relayPort, RelayJoinRequest joinRequest)
    {
        if (string.IsNullOrWhiteSpace(relayHost)) throw new ArgumentException("Relay host is required.", nameof(relayHost));
        if (relayPort <= 0 || relayPort > 65535) throw new ArgumentOutOfRangeException(nameof(relayPort));
        if (joinRequest == null) throw new ArgumentNullException(nameof(joinRequest));

        Stop();

        TcpClient client;
        CancellationTokenSource lifetime;
        int generation;
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            generation = ++_generation;
            client = new TcpClient { NoDelay = true };
            lifetime = new CancellationTokenSource();
            _client = client;
            _lifetime = lifetime;
            _connecting = true;
        }

        var worker = Task.Run(() => RunConnectionAsync(generation, client, lifetime, relayHost, relayPort, joinRequest));
        _workers[generation] = worker;
        _ = worker.ContinueWith(
            _ => _workers.TryRemove(generation, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Poll()
    {
        while (_callbacks.TryDequeue(out var callback))
        {
            if (callback.Generation == Volatile.Read(ref _generation))
            {
                callback.Body();
            }
        }
    }

    public void Send(ProtocolEnvelope envelope)
    {
        if (envelope == null) throw new ArgumentNullException(nameof(envelope));
        var stream = _stream;
        if (!_running || stream == null)
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
        CancellationTokenSource? lifetime;
        TcpClient? client;
        lock (_lifecycleLock)
        {
            ++_generation;
            _connecting = false;
            _running = false;
            lifetime = _lifetime;
            client = _client;
            _lifetime = null;
            _stream = null;
            _client = null;
        }

        try { lifetime?.Cancel(); } catch { }
        try { client?.Close(); } catch { }
        while (_callbacks.TryDequeue(out _))
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    private async Task RunConnectionAsync(
        int generation,
        TcpClient client,
        CancellationTokenSource lifetime,
        string relayHost,
        int relayPort,
        RelayJoinRequest joinRequest)
    {
        try
        {
            using (var connectPhase = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                connectPhase.CancelAfter(_connectTimeout);
                try
                {
                    await AwaitWithCancellation(_connectAsync(client, relayHost, relayPort), connectPhase.Token).ConfigureAwait(false);
                    var stream = client.GetStream();
                    var joinFrame = SerializeFrame(new RelayFrame(RelayFrameType.Join, joinRequest.ToPayload()));
                    await stream.WriteAsync(joinFrame, 0, joinFrame.Length, connectPhase.Token).ConfigureAwait(false);

                    lock (_lifecycleLock)
                    {
                        if (generation != _generation || lifetime.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(lifetime.Token);
                        }

                        _stream = stream;
                        _connecting = false;
                        _running = true;
                    }
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                {
                    throw new TimeoutException($"Relay connection to {relayHost}:{relayPort} timed out after {_connectTimeout.TotalSeconds:0.#} seconds.");
                }
            }

            Queue(generation, () => Connected?.Invoke());
            await ReadLoopAsync(generation, client.GetStream(), lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrent(generation, lifetime))
            {
                var message = ex is TimeoutException
                    ? ex.Message
                    : $"Relay connection to {relayHost}:{relayPort} failed: {ex.Message}";
                Queue(generation, () => ConnectionFailed?.Invoke(message));
            }
        }
        finally
        {
            lock (_lifecycleLock)
            {
                if (generation == _generation)
                {
                    _connecting = false;
                    _running = false;
                    _stream = null;
                    _client = null;
                    _lifetime = null;
                }
            }

            try { client.Close(); } catch { }
            lifetime.Dispose();
        }
    }

    private async Task ReadLoopAsync(int generation, Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                switch (frame.Type)
                {
                    case RelayFrameType.Protocol:
                    {
                        var envelope = ProtocolSerializer.Unpack(frame.Payload);
                        Queue(generation, () => MessageReceived?.Invoke(envelope));
                        break;
                    }
                    case RelayFrameType.Status:
                    {
                        var status = Encoding.UTF8.GetString(frame.Payload);
                        Queue(generation, () => StatusReceived?.Invoke(status));
                        break;
                    }
                    case RelayFrameType.Rejected:
                    {
                        var reason = frame.GetUtf8Payload();
                        Queue(generation, () => Rejected?.Invoke(reason));
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _generation))
            {
                Queue(generation, () => Disconnected?.Invoke(ex.Message));
            }
        }
    }

    private void Queue(int generation, Action callback)
    {
        _callbacks.Enqueue(new QueuedCallback(generation, callback));
    }

    private bool IsCurrent(int generation, CancellationTokenSource lifetime)
    {
        return generation == Volatile.Read(ref _generation) && !lifetime.IsCancellationRequested;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TcpRelayTransport));
        }
    }

    private static byte[] SerializeFrame(RelayFrame frame)
    {
        using var stream = new MemoryStream();
        RelayFrame.Write(stream, frame);
        return stream.ToArray();
    }

    private static async Task<RelayFrame> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactlyAsync(stream, 4, cancellationToken).ConfigureAwait(false);
        var length = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
        if (length <= 0 || length > 8 * 1024 * 1024)
        {
            throw new InvalidDataException("Relay frame length is invalid.");
        }

        var body = await ReadExactlyAsync(stream, length, cancellationToken).ConfigureAwait(false);
        var bytes = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
        Buffer.BlockCopy(body, 0, bytes, header.Length, body.Length);
        using var frameStream = new MemoryStream(bytes, writable: false);
        return RelayFrame.Read(frameStream);
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var bytes = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var bytesRead = await stream.ReadAsync(bytes, offset, length - offset, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException("Relay connection closed while reading a frame.");
            }

            offset += bytesRead;
        }

        return bytes;
    }

    private static async Task AwaitWithCancellation(Task task, CancellationToken cancellationToken)
    {
        var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (await Task.WhenAny(task, cancellation).ConfigureAwait(false) != task)
        {
            _ = task.ContinueWith(
                completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new OperationCanceledException(cancellationToken);
        }

        await task.ConfigureAwait(false);
    }

    private sealed class QueuedCallback
    {
        public QueuedCallback(int generation, Action body)
        {
            Generation = generation;
            Body = body;
        }

        public int Generation { get; }
        public Action Body { get; }
    }
}
