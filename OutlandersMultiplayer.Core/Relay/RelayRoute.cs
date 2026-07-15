using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OutlandersMultiplayer.Core.Relay;

public sealed class RelayRoute
{
    public const int MaxConnectionIdBytes = 64;
    public const int MaxProtocolPayloadBytes = 8 * 1024 * 1024 - 128;

    public RelayRoute(string connectionId, byte[] protocolPayload)
    {
        ConnectionId = connectionId ?? string.Empty;
        ProtocolPayload = protocolPayload ?? Array.Empty<byte>();
    }

    public string ConnectionId { get; }
    public byte[] ProtocolPayload { get; }

    public byte[] ToPayload()
    {
        if (string.IsNullOrWhiteSpace(ConnectionId))
        {
            throw new InvalidDataException("Relay connection ID is required.");
        }

        if (ProtocolPayload.Length > MaxProtocolPayloadBytes)
        {
            throw new InvalidDataException("Routed protocol payload is too large.");
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        RelayStrings.WriteBounded(writer, ConnectionId, MaxConnectionIdBytes);
        writer.Write(ProtocolPayload.Length);
        writer.Write(ProtocolPayload);
        writer.Flush();
        return stream.ToArray();
    }

    public static RelayRoute FromPayload(byte[] payload)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));

        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var connectionId = RelayStrings.ReadBounded(reader, MaxConnectionIdBytes);
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new InvalidDataException("Relay connection ID is required.");
        }

        var length = reader.ReadInt32();
        if (length < 0 || length > MaxProtocolPayloadBytes || length != stream.Length - stream.Position)
        {
            throw new InvalidDataException("Routed protocol payload length is invalid.");
        }

        var protocolPayload = reader.ReadBytes(length);
        if (protocolPayload.Length != length)
        {
            throw new EndOfStreamException("Routed protocol payload is truncated.");
        }

        return new RelayRoute(connectionId, protocolPayload);
    }
}

public static class RelayRouting
{
    public static RelayFrame FromClient(string connectionId, RelayFrame frame)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (frame.Type != RelayFrameType.Protocol)
        {
            throw new InvalidDataException("Clients may only send Protocol frames after joining.");
        }

        return new RelayFrame(
            RelayFrameType.RoutedProtocol,
            new RelayRoute(connectionId, frame.Payload).ToPayload());
    }

    public static IReadOnlyList<string> SelectHostRecipients(
        RelayFrame frame,
        IEnumerable<string> activeClientIds)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (activeClientIds == null) throw new ArgumentNullException(nameof(activeClientIds));

        var clients = activeClientIds.ToArray();
        if (frame.Type == RelayFrameType.Protocol)
        {
            return clients;
        }

        if (frame.Type != RelayFrameType.RoutedProtocol)
        {
            return Array.Empty<string>();
        }

        var route = RelayRoute.FromPayload(frame.Payload);
        return clients.Contains(route.ConnectionId, StringComparer.Ordinal)
            ? new[] { route.ConnectionId }
            : Array.Empty<string>();
    }

    public static RelayFrame ForClient(RelayFrame hostFrame)
    {
        if (hostFrame == null) throw new ArgumentNullException(nameof(hostFrame));
        if (hostFrame.Type == RelayFrameType.Protocol)
        {
            return hostFrame;
        }

        if (hostFrame.Type != RelayFrameType.RoutedProtocol)
        {
            throw new InvalidDataException("Host routing requires a Protocol or RoutedProtocol frame.");
        }

        var route = RelayRoute.FromPayload(hostFrame.Payload);
        return new RelayFrame(RelayFrameType.Protocol, route.ProtocolPayload);
    }

    public static RelayFrame ToClient(string connectionId, byte[] protocolPayload)
    {
        return new RelayFrame(
            RelayFrameType.RoutedProtocol,
            new RelayRoute(connectionId, protocolPayload).ToPayload());
    }
}
