using System;
using System.IO;
using System.Linq;

namespace OutlandersMultiplayer.Core.Snapshots;

public sealed class RegisteredMultiplayerSave
{
    public RegisteredMultiplayerSave(string path, int slotIndex)
    {
        Path = path;
        SlotIndex = slotIndex;
    }

    public string Path { get; }
    public int SlotIndex { get; }
}

public static class MultiplayerSaveRegistrar
{
    public static RegisteredMultiplayerSave Register(string outlandersUserFolder, byte[] saveBytes)
    {
        if (string.IsNullOrWhiteSpace(outlandersUserFolder))
        {
            throw new ArgumentException("Outlanders user folder is required.", nameof(outlandersUserFolder));
        }

        if (saveBytes == null) throw new ArgumentNullException(nameof(saveBytes));
        if (!Directory.Exists(outlandersUserFolder))
        {
            throw new DirectoryNotFoundException("Outlanders user save folder was not found.");
        }

        var saveGameFolders = Directory.EnumerateDirectories(outlandersUserFolder, "*", SearchOption.TopDirectoryOnly)
            .Where(path => IsRegisteredSaveGameFolder(outlandersUserFolder, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (saveGameFolders.Length == 0)
        {
            throw new InvalidOperationException("No registered Outlanders save game was found. Create or load a local game first.");
        }

        if (saveGameFolders.Length > 1)
        {
            throw new InvalidOperationException("Multiple Outlanders save games were found; refusing to guess which profile should receive the multiplayer world.");
        }

        var endlessFolder = System.IO.Path.Combine(saveGameFolders[0], "Endless");
        Directory.CreateDirectory(endlessFolder);
        var slotIndex = FindAvailableSlot(endlessFolder);
        var targetPath = System.IO.Path.Combine(endlessFolder, $"Endless_{slotIndex}.dat");
        var temporaryPath = targetPath + $".omp-{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, saveBytes);
            File.Move(temporaryPath, targetPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new RegisteredMultiplayerSave(targetPath, slotIndex);
    }

    private static bool IsRegisteredSaveGameFolder(string userFolder, string candidateFolder)
    {
        var id = System.IO.Path.GetFileName(candidateFolder);
        return id.Length > 0
            && id.All(char.IsDigit)
            && File.Exists(System.IO.Path.Combine(userFolder, $"savegame-{id}"));
    }

    private static int FindAvailableSlot(string endlessFolder)
    {
        for (var slotIndex = 0; slotIndex < int.MaxValue; slotIndex++)
        {
            var prefix = $"Endless_{slotIndex}.dat";
            if (!Directory.EnumerateFiles(endlessFolder, prefix + "*", SearchOption.TopDirectoryOnly).Any())
            {
                return slotIndex;
            }
        }

        throw new IOException("No available Endless save slot was found.");
    }
}
