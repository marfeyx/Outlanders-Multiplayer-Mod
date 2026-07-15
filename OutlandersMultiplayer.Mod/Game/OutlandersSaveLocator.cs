using System;
using System.IO;
using System.Linq;
using OutlandersMultiplayer.Core.Snapshots;
using OutlandersMultiplayer.Core.State;

namespace OutlandersMultiplayer.Mod.Game;

public static class OutlandersSaveLocator
{
    public static string? FindUserFolder()
    {
        var root = GetOutlandersRoot();

        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.GetDirectories(root, "user-*")
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static HostingSaveSelection DiscoverHostingSaves(string? activeSavePath = null)
    {
        return HostingSaveSelector.Discover(GetOutlandersRoot(), activeSavePath);
    }

    public static string GetHostingSaveDisplayPath(string path)
    {
        var userFolder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        return Path.Combine(userFolder, Path.GetFileName(path));
    }

    private static string GetOutlandersRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData",
            "LocalLow",
            "Pomelo Games",
            "Outlanders");
    }

    public static string HashSaveFile(string path)
    {
        return Hashing.Sha256Hex(File.ReadAllBytes(path));
    }
}
