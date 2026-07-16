using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using OutlandersMultiplayer.Core.Protocol;

namespace OutlandersMultiplayer.Mod.Game;

public sealed class GameHookBridge : IDisposable
{
    private const string HarmonyId = "marfeyx.outlanders-multiplayer.gameplay";
    private readonly MultiplayerController _controller;
    private readonly Action<string> _log;
    private readonly bool _enableDiagnosticScanning;
    private readonly HarmonyLib.Harmony _harmony = new(HarmonyId);
    private Type? _sitePlacementType;
    private Type? _spawnType;
    private Type? _prefabKeyType;
    private Type? _prefabCategoryType;
    private Type? _float2Type;
    private MethodInfo? _placeOneSite;
    private ReflectionBuildPlacementCodec? _placementCodec;
    private int _frames;
    private bool _installed;
    private bool _diagnosticsRun;

    [ThreadStatic]
    private static int _applyingAcceptedCommand;

    private static GameHookBridge? _active;

    public GameHookBridge(MultiplayerController controller, Action<string> log, bool enableDiagnosticScanning = false)
    {
        _controller = controller;
        _log = log;
        _enableDiagnosticScanning = enableDiagnosticScanning;
    }

    public void InstallInstrumentation()
    {
        _active = this;
        _controller.PlayerIntentValidating += ValidateIntent;
        _controller.AcceptedCommandReceived += ApplyAcceptedCommand;
        if (!TryInstallPlacementHook())
        {
            _log("Outlanders build-placement types are not loaded yet; hook installation will retry during updates.");
        }
    }

    public void Update()
    {
        _frames++;
        if (!_installed && _frames % 60 == 0 && _frames <= 600)
        {
            TryInstallPlacementHook();
        }

        if (_enableDiagnosticScanning && !_diagnosticsRun && _frames >= 600)
        {
            _diagnosticsRun = true;
            RunDiagnosticScan();
        }
    }

    public void Dispose()
    {
        _controller.PlayerIntentValidating -= ValidateIntent;
        _controller.AcceptedCommandReceived -= ApplyAcceptedCommand;
        if (_active == this) _active = null;
        _harmony.UnpatchSelf();
        _installed = false;
    }

    private bool TryInstallPlacementHook()
    {
        if (_installed) return true;
        var gameAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name == "Assembly-CSharp");
        if (gameAssembly == null) return false;

        _sitePlacementType = gameAssembly.GetType("SitePlacementSystem", throwOnError: false);
        _prefabKeyType = gameAssembly.GetType("PrefabKey", throwOnError: false);
        _prefabCategoryType = gameAssembly.GetType("PrefabCategory", throwOnError: false);
        _float2Type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("Unity.Mathematics.float2", throwOnError: false))
            .FirstOrDefault(type => type != null);
        _placeOneSite = _sitePlacementType?
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(method => method.Name == "PlaceOneSite" && method.GetParameters().Length == 1);
        _spawnType = _placeOneSite?.GetParameters()[0].ParameterType;

        if (_sitePlacementType == null || _prefabKeyType == null || _prefabCategoryType == null ||
            _float2Type == null || _placeOneSite == null || _spawnType == null)
        {
            _log("Outlanders build-placement signature did not match the supported 2022.3.62f2 layout.");
            return false;
        }

        _placementCodec = new ReflectionBuildPlacementCodec(_spawnType, _prefabKeyType, _prefabCategoryType, _float2Type);

        var prefix = typeof(GameHookBridge).GetMethod(nameof(PlaceOneSitePrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
        _harmony.Patch(_placeOneSite, prefix: new HarmonyMethod(prefix));
        _installed = true;
        _log("Installed gameplay hook: SitePlacementSystem.PlaceOneSite(SiteSpawn).");
        return true;
    }

    private static bool PlaceOneSitePrefix(object[] __args)
    {
        var bridge = _active;
        if (bridge == null || _applyingAcceptedCommand > 0 || __args.Length != 1)
        {
            return true;
        }

        return bridge.CaptureLocalPlacement(__args[0]);
    }

    private bool CaptureLocalPlacement(object spawn)
    {
        if (_controller.LocalPlayerId == 0) return true;
        try
        {
            var placement = _placementCodec!.Capture(spawn);
            var command = new CommandEnvelope
            {
                SimulationTick = ReadSimulationTick(),
                CommandType = BuildPlacementIntent.CommandType,
                JsonPayload = placement.ToJson()
            };
            var sent = _controller.SendPlayerIntent(command);
            if (sent)
            {
                _log($"Captured build placement {placement.Key} at ({placement.PositionX}, {placement.PositionY}).");
            }
            else
            {
                _controller.State.SetError("Build placement was not accepted by the multiplayer session.");
            }

            return false;
        }
        catch (Exception ex)
        {
            var message = $"Build placement capture failed; local placement was suppressed: {Unwrap(ex).Message}";
            _controller.State.SetError(message);
            _log(message);
            return false;
        }
    }

    private string? ValidateIntent(CommandEnvelope command)
    {
        if (command.CommandType != BuildPlacementIntent.CommandType) return null;
        BuildPlacementIntent placement;
        try
        {
            placement = BuildPlacementIntent.FromJson(command.JsonPayload);
        }
        catch (Exception ex)
        {
            return $"Build placement payload is invalid: {Unwrap(ex).Message}";
        }

        if (!_installed || _prefabKeyType == null)
        {
            return "Outlanders build-placement hook is not installed on the host.";
        }

        try
        {
            var worldType = _sitePlacementType!.Assembly.GetType("WorldBehaviour", throwOnError: true)!;
            var world = FindSingletonInstance(worldType);
            if (world == null) return "Outlanders world is not initialized.";
            var key = _placementCodec!.CreatePrefabKey(placement);
            var definitionMethod = worldType.GetMethod(
                "PlaceableDefinitionFor",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: new[] { _prefabKeyType },
                modifiers: null);
            var definition = definitionMethod?.Invoke(world, new[] { key });
            if (definition == null) return $"Building key {placement.Key} is not available in the host world.";
            var buildable = definition.GetType().GetProperty("Buildable", BindingFlags.Instance | BindingFlags.Public)?.GetValue(definition);
            if (buildable is not bool allowed) return $"Building key {placement.Key} buildability could not be verified.";
            if (!allowed) return $"Building key {placement.Key} is not currently buildable.";
            return null;
        }
        catch (Exception ex)
        {
            return $"Host could not validate building key {placement.Key}: {Unwrap(ex).Message}";
        }
    }

    private void ApplyAcceptedCommand(CommandEnvelope command)
    {
        if (command.CommandType != BuildPlacementIntent.CommandType) return;
        try
        {
            var placement = BuildPlacementIntent.FromJson(command.JsonPayload);
            var instance = _sitePlacementType?.GetField("instance", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                ?? throw new InvalidOperationException("SitePlacementSystem is not initialized.");
            var spawn = _placementCodec!.CreateSpawn(placement);

            _applyingAcceptedCommand++;
            try
            {
                _placeOneSite!.Invoke(instance, new[] { spawn });
            }
            finally
            {
                _applyingAcceptedCommand--;
            }

            _log($"Applied accepted build command {command.CommandId}: building {placement.Key} at ({placement.PositionX}, {placement.PositionY}).");
        }
        catch (Exception ex)
        {
            var message = $"Accepted build command {command.CommandId} could not be applied: {Unwrap(ex).Message}";
            _controller.State.SetError(message);
            _log(message);
        }
    }

    private long ReadSimulationTick()
    {
        try
        {
            var worldType = _sitePlacementType!.Assembly.GetType("WorldBehaviour", throwOnError: true)!;
            var world = FindSingletonInstance(worldType);
            var clock = worldType.GetProperty("Clock", BindingFlags.Instance | BindingFlags.Public)?.GetValue(world!);
            var timePassed = clock?.GetType().GetField("TimePassed", BindingFlags.Instance | BindingFlags.Public)?.GetValue(clock);
            return Convert.ToInt64(Math.Round(Convert.ToDouble(timePassed) * 1000));
        }
        catch
        {
            return 0;
        }
    }

    private static object? FindSingletonInstance(Type type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var property = current.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property?.GetValue(null) is { } instance) return instance;
            var field = current.GetField("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.GetValue(null) is { } fieldInstance) return fieldInstance;
        }

        return null;
    }

    private void RunDiagnosticScan()
    {
        var gameAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name == "Assembly-CSharp");
        var candidates = gameAssembly?.GetTypes()
            .Where(type => type.Name.Contains("Placement", StringComparison.OrdinalIgnoreCase))
            .Select(type => type.FullName)
            .Take(20)
            .ToArray() ?? Array.Empty<string>();
        _log($"Optional placement diagnostics: {string.Join(", ", candidates)}");
    }

    private static Exception Unwrap(Exception exception)
    {
        return exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
    }
}
