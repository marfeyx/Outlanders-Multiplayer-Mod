using System;

namespace OutlandersMultiplayer.Core.Protocol;

public sealed class ProtocolEnvelope
{
    public ProtocolEnvelope(ProtocolMessageType type, uint sequence, byte[] payload)
    {
        Type = type;
        Sequence = sequence;
        Payload = payload ?? Array.Empty<byte>();
    }

    public ProtocolMessageType Type { get; }

    public uint Sequence { get; }

    public byte[] Payload { get; }
}
