using System.Security.Cryptography;
using System.Text;

namespace OutlandersMultiplayer.Core.State;

public static class Hashing
{
    public static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(bytes));
    }

    public static ulong Fnv1A64(string value)
    {
        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            foreach (var b in Encoding.UTF8.GetBytes(value ?? string.Empty))
            {
                hash ^= b;
                hash *= prime;
            }

            return hash;
        }
    }

    private static string ToHex(byte[] bytes)
    {
        var chars = new char[bytes.Length * 2];
        const string hex = "0123456789abcdef";
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = hex[bytes[i] >> 4];
            chars[i * 2 + 1] = hex[bytes[i] & 0x0F];
        }

        return new string(chars);
    }
}
