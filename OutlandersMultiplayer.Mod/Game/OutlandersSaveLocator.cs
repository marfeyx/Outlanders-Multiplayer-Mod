using System;
using System.IO;
using System.Linq;
using OutlandersMultiplayer.Core.State;

namespace OutlandersMultiplayer.Mod.Game;

public static class OutlandersSaveLocator
{
    public static string? FindUserFolder()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData",
            "LocalLow",
            "Pomelo Games",
            "Outlanders");

        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.GetDirectories(root, "user-*")
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static string? FindLatestSandboxSave()
    {
        var userFolder = FindUserFolder();
        if (userFolder == null)
        {
            return null;
        }

        return Directory.GetFiles(userFolder, "Endless_*.dat", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".backup", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static string HashSaveFile(string path)
    {
        return Hashing.Sha256Hex(File.ReadAllBytes(path));
    }
}
