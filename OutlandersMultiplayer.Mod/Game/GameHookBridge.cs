using System;
using System.Linq;
using System.Reflection;

namespace OutlandersMultiplayer.Mod.Game;

public sealed class GameHookBridge
{
    private readonly Action<string> _log;
    private bool _logged;
    private bool _inspected;
    private int _frames;

    public GameHookBridge(Action<string> log)
    {
        _log = log;
    }

    public void InstallInstrumentation()
    {
        _log("Instrumentation bridge initialized. Exact IL2CPP gameplay hooks are pending MelonLoader interop inspection.");
    }

    public void Update()
    {
        if (!_logged)
        {
            _logged = true;
            _log("Outlanders multiplayer core is active. Use overlay to host or join a direct-IP session.");
        }

        if (!_inspected && ++_frames > 180)
        {
            _inspected = true;
            InspectLoadedIl2CppAssemblies();
        }
    }

    private void InspectLoadedIl2CppAssemblies()
    {
        var keywords = new[]
        {
            "Save", "Sandbox", "Build", "Construction", "Decree", "Time", "Simulation",
            "Resource", "World", "Level", "Session", "Input", "Order", "Command"
        };

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name is "Assembly-CSharp" or "Unity.Entities")
            .ToArray();

        _log($"Instrumentation scan: {assemblies.Length} candidate assemblies loaded.");

        foreach (var assembly in assemblies)
        {
            foreach (var type in SafeTypes(assembly)
                         .Where(type => type.FullName != null && keywords.Any(keyword => type.FullName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                         .Take(120))
            {
                _log($"Candidate type: {type.FullName}");
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                             .Where(method => keywords.Any(keyword => method.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                             .Take(12))
                {
                    _log($"  Method: {method.Name}");
                }
            }
        }
    }

    private static Type[] SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null).Cast<Type>().ToArray();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }
}
