using System;
using System.Linq;
using System.Reflection;
using OutlandersMultiplayer.Core.Protocol;

namespace OutlandersMultiplayer.Mod.Game;

public static class RuntimeCompatibilityProvider
{
    public static RuntimeCompatibility Capture()
    {
        return new RuntimeCompatibility
        {
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            OutlandersBuildGuid = ReadApplicationProperty("buildGUID"),
            UnityVersion = ReadApplicationProperty("unityVersion"),
            ModVersion = ReadModVersion()
        };
    }

    private static string ReadApplicationProperty(string name)
    {
        var applicationType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("UnityEngine.Application", throwOnError: false))
            .FirstOrDefault(type => type != null);

        return applicationType?
            .GetProperty(name, BindingFlags.Public | BindingFlags.Static)?
            .GetValue(null)?
            .ToString() ?? string.Empty;
    }

    private static string ReadModVersion()
    {
        var assembly = typeof(RuntimeCompatibilityProvider).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var metadataStart = informationalVersion.IndexOf('+');
            return metadataStart < 0 ? informationalVersion : informationalVersion.Substring(0, metadataStart);
        }

        return assembly.GetName().Version?.ToString() ?? string.Empty;
    }
}
