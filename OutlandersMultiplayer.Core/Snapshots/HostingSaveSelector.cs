using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OutlandersMultiplayer.Core.Snapshots;

public sealed class HostingSaveSelection
{
    public HostingSaveSelection(IReadOnlyList<string> candidates, string? selectedPath, string error)
    {
        Candidates = candidates;
        SelectedPath = selectedPath;
        Error = error;
    }

    public IReadOnlyList<string> Candidates { get; }
    public string? SelectedPath { get; }
    public string Error { get; }
}

public static class HostingSaveSelector
{
    public static HostingSaveSelection Discover(string outlandersRoot, string? activeSavePath = null)
    {
        if (string.IsNullOrWhiteSpace(outlandersRoot) || !Directory.Exists(outlandersRoot))
        {
            return new HostingSaveSelection(Array.Empty<string>(), null, "Outlanders user save root was not found.");
        }

        var candidates = Directory.EnumerateDirectories(outlandersRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path).StartsWith("user-", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
            .Where(IsSandboxSaveName)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFullPath)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(activeSavePath))
        {
            var activePath = Path.GetFullPath(activeSavePath);
            var selected = candidates.FirstOrDefault(path => string.Equals(path, activePath, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
            {
                return new HostingSaveSelection(candidates, null, $"Active save '{activePath}' is not an eligible top-level Endless save.");
            }

            return new HostingSaveSelection(candidates, selected, string.Empty);
        }

        if (candidates.Length == 0)
        {
            return new HostingSaveSelection(candidates, null, "No eligible top-level Endless/Sandbox save was found.");
        }

        if (candidates.Length > 1)
        {
            return new HostingSaveSelection(candidates, null, "Multiple eligible saves were found. Select the exact save before hosting.");
        }

        return new HostingSaveSelection(candidates, candidates[0], string.Empty);
    }

    private static bool IsSandboxSaveName(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith("Endless_", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase);
    }
}
