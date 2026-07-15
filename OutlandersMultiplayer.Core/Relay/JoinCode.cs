using System;
using System.Security.Cryptography;
using System.Text;

namespace OutlandersMultiplayer.Core.Relay;

public sealed class JoinCode
{
    private const string Prefix = "OMP1:";

    public string RelayHost { get; set; } = string.Empty;
    public int RelayPort { get; set; }
    public string RoomCode { get; set; } = string.Empty;
    public string SessionKey { get; set; } = string.Empty;

    public static string Encode(string relayHost, int relayPort, string roomCode, string sessionKey)
    {
        var raw = $"{Escape(relayHost)}|{relayPort}|{Escape(roomCode)}|{Escape(sessionKey)}";
        return Prefix + ToBase64Url(Encoding.UTF8.GetBytes(raw));
    }

    public static bool TryDecode(string code, out JoinCode joinCode)
    {
        joinCode = new JoinCode();
        if (string.IsNullOrWhiteSpace(code) || !code.Trim().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var encoded = code.Trim().Substring(Prefix.Length);
            var raw = Encoding.UTF8.GetString(FromBase64Url(encoded));
            var parts = SplitAndUnescape(raw);
            if (parts.Length != 4 || !int.TryParse(parts[1], out var port) || port <= 0)
            {
                return false;
            }

            joinCode = new JoinCode
            {
                RelayHost = parts[0],
                RelayPort = port,
                RoomCode = parts[2],
                SessionKey = parts[3]
            };
            return !string.IsNullOrWhiteSpace(joinCode.RelayHost)
                && !string.IsNullOrWhiteSpace(joinCode.RoomCode)
                && !string.IsNullOrWhiteSpace(joinCode.SessionKey);
        }
        catch
        {
            return false;
        }
    }

    public static string CreateRoomCode()
    {
        return CreateToken(5);
    }

    public static string CreateSessionKey()
    {
        return CreateToken(12);
    }

    private static string CreateToken(int bytes)
    {
        var buffer = new byte[bytes];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(buffer);
        }

        return ToBase32(buffer);
    }

    private static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }

    private static string Escape(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("|", "\\p");
    }

    private static string[] SplitAndUnescape(string value)
    {
        var parts = new System.Collections.Generic.List<string>();
        var current = new StringBuilder();
        var escaped = false;
        foreach (var ch in value)
        {
            if (escaped)
            {
                if (ch == 'p')
                {
                    current.Append('|');
                }
                else if (ch == '\\')
                {
                    current.Append('\\');
                }
                else
                {
                    current.Append('\\');
                    current.Append(ch);
                }

                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (ch == '|')
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        if (escaped)
        {
            current.Append('\\');
        }

        parts.Add(current.ToString());
        return parts.ToArray();
    }

    private static string ToBase32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var output = new StringBuilder();
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                output.Append(alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            output.Append(alphabet[(buffer << (5 - bitsLeft)) & 31]);
        }

        return output.ToString();
    }
}
