using System.IO;
using System.Text;

namespace OutlandersMultiplayer.Core.Protocol;

internal static class ProtocolStrings
{
    public static void WriteBounded(BinaryWriter writer, string? value, int maxUtf8Bytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length > maxUtf8Bytes)
        {
            throw new InvalidDataException($"String is longer than {maxUtf8Bytes} UTF-8 bytes.");
        }

        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    public static string ReadBounded(BinaryReader reader, int maxUtf8Bytes)
    {
        var length = reader.ReadUInt16();
        if (length > maxUtf8Bytes)
        {
            throw new InvalidDataException($"String length {length} exceeds limit {maxUtf8Bytes}.");
        }

        return Encoding.UTF8.GetString(reader.ReadBytes(length));
    }
}
