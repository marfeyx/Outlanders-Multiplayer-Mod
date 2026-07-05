using System;
using System.IO;

namespace OutlandersMultiplayer.Core.Snapshots;

public static class TempSaveWriter
{
    public const string MultiplayerSlotFolder = "OutlandersMultiplayerTemp";

    public static string WriteTempSave(string outlandersUserFolder, string saveName, byte[] saveBytes)
    {
        if (string.IsNullOrWhiteSpace(outlandersUserFolder))
        {
            throw new ArgumentException("Outlanders user folder is required.", nameof(outlandersUserFolder));
        }

        var safeName = MakeSafeFileName(saveName);
        var targetFolder = Path.Combine(outlandersUserFolder, MultiplayerSlotFolder);
        Directory.CreateDirectory(targetFolder);
        var targetPath = Path.Combine(targetFolder, safeName);
        File.WriteAllBytes(targetPath, saveBytes);
        return targetPath;
    }

    private static string MakeSafeFileName(string name)
    {
        var fallback = string.IsNullOrWhiteSpace(name) ? "snapshot.dat" : name;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fallback = fallback.Replace(invalid, '_');
        }

        if (!fallback.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
        {
            fallback += ".dat";
        }

        return fallback;
    }
}
