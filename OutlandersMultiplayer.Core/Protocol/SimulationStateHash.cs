using System;
using System.IO;
using System.Text;

namespace OutlandersMultiplayer.Core.Protocol;

public sealed class SimulationStateHash
{
    public uint PlayerId { get; set; }
    public long SimulationTick { get; set; }
    public string Hash { get; set; } = string.Empty;

    public void Validate()
    {
        if (PlayerId == 0) throw new InvalidDataException("State hash player ID is required.");
        if (SimulationTick < 0) throw new InvalidDataException("Simulation tick cannot be negative.");
        if (Hash.Length != 64) throw new InvalidDataException("State hash must be a SHA-256 value.");
        foreach (var character in Hash)
        {
            if (!Uri.IsHexDigit(character)) throw new InvalidDataException("State hash must be hexadecimal.");
        }
    }

    public byte[] ToPayload()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(PlayerId);
        writer.Write(SimulationTick);
        ProtocolStrings.WriteBounded(writer, Hash, 128);
        writer.Flush();
        return stream.ToArray();
    }

    public static SimulationStateHash FromPayload(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        return new SimulationStateHash
        {
            PlayerId = reader.ReadUInt32(),
            SimulationTick = reader.ReadInt64(),
            Hash = ProtocolStrings.ReadBounded(reader, 128)
        };
    }
}
