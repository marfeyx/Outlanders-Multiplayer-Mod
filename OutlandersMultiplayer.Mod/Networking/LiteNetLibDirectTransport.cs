using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using OutlandersMultiplayer.Core.Protocol;

namespace OutlandersMultiplayer.Mod.Networking;

public sealed class LiteNetLibDirectTransport : IDisposable
{
    private readonly EventBasedNetListener _listener = new();
    private readonly List<NetPeer> _peers = new();
    private NetManager? _manager;
    private NetPeer? _serverPeer;

    public event Action<NetPeer, ProtocolEnvelope>? MessageReceived;
    public event Action<NetPeer>? PeerConnected;
    public event Action<NetPeer, DisconnectInfo>? PeerDisconnected;
    public event Action<ConnectionRequest>? ConnectionRequested;

    public bool IsRunning => _manager?.IsRunning == true;
    public int ConnectedPeersCount => _manager?.ConnectedPeersCount ?? 0;

    public void StartServer(int port)
    {
        Stop();
        _peers.Clear();
        WireEvents();
        _manager = new NetManager(_listener) { AutoRecycle = true };
        _manager.Start(port);
    }

    public void StartClient(string host, int port, string connectionKey)
    {
        Stop();
        _peers.Clear();
        WireEvents();
        _manager = new NetManager(_listener) { AutoRecycle = true };
        _manager.Start();
        _serverPeer = _manager.Connect(host, port, connectionKey);
    }

    public void Poll()
    {
        _manager?.PollEvents();
    }

    public void Send(NetPeer peer, ProtocolEnvelope envelope)
    {
        var writer = new NetDataWriter();
        writer.Put(ProtocolSerializer.Pack(envelope));
        peer.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendToServer(ProtocolEnvelope envelope)
    {
        if (_serverPeer != null)
        {
            Send(_serverPeer, envelope);
        }
    }

    public void Broadcast(ProtocolEnvelope envelope)
    {
        if (_manager == null) return;
        foreach (var peer in _peers.ToArray())
        {
            Send(peer, envelope);
        }
    }

    public void Stop()
    {
        _manager?.Stop();
        _manager = null;
        _serverPeer = null;
        _peers.Clear();
    }

    public void Dispose()
    {
        Stop();
    }

    private void WireEvents()
    {
        _listener.ClearNetworkReceiveEvent();
        _listener.ClearPeerConnectedEvent();
        _listener.ClearPeerDisconnectedEvent();
        _listener.ClearConnectionRequestEvent();

        _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            try
            {
                MessageReceived?.Invoke(peer, ProtocolSerializer.Unpack(reader.GetRemainingBytes()));
            }
            finally
            {
                reader.Recycle();
            }
        };
        _listener.PeerConnectedEvent += peer =>
        {
            if (!_peers.Contains(peer))
            {
                _peers.Add(peer);
            }

            PeerConnected?.Invoke(peer);
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            _peers.Remove(peer);
            PeerDisconnected?.Invoke(peer, info);
        };
        _listener.ConnectionRequestEvent += request => ConnectionRequested?.Invoke(request);
    }
}
