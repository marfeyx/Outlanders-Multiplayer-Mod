using System;
using System.Reflection;

namespace OutlandersMultiplayer.Core.Protocol;

public sealed class ReflectionBuildPlacementCodec
{
    private readonly Type _spawnType;
    private readonly Type _prefabKeyType;
    private readonly Type _prefabCategoryType;
    private readonly Type _float2Type;

    public ReflectionBuildPlacementCodec(Type spawnType, Type prefabKeyType, Type prefabCategoryType, Type float2Type)
    {
        _spawnType = spawnType ?? throw new ArgumentNullException(nameof(spawnType));
        _prefabKeyType = prefabKeyType ?? throw new ArgumentNullException(nameof(prefabKeyType));
        _prefabCategoryType = prefabCategoryType ?? throw new ArgumentNullException(nameof(prefabCategoryType));
        _float2Type = float2Type ?? throw new ArgumentNullException(nameof(float2Type));
    }

    public BuildPlacementIntent Capture(object spawn)
    {
        if (spawn == null) throw new ArgumentNullException(nameof(spawn));
        if (!_spawnType.IsInstanceOfType(spawn)) throw new ArgumentException("Spawn has the wrong runtime type.", nameof(spawn));
        var key = GetField(spawn, "Key");
        var position = GetField(spawn, "Position");
        var size = GetField(spawn, "size");
        var placement = new BuildPlacementIntent
        {
            Category = Convert.ToInt32(GetField(key, "Category")),
            Key = Convert.ToInt32(GetField(key, "Key")),
            PositionX = Convert.ToSingle(GetField(position, "x")),
            PositionY = Convert.ToSingle(GetField(position, "y")),
            Rotation = Convert.ToSingle(GetField(spawn, "Rotation")),
            SizeX = Convert.ToSingle(GetField(size, "x")),
            SizeY = Convert.ToSingle(GetField(size, "y"))
        };
        placement.Validate();
        return placement;
    }

    public object CreateSpawn(BuildPlacementIntent placement)
    {
        if (placement == null) throw new ArgumentNullException(nameof(placement));
        placement.Validate();
        var spawn = Activator.CreateInstance(_spawnType)
            ?? throw new InvalidOperationException("Could not create the placement spawn type.");
        SetField(spawn, "Key", CreatePrefabKey(placement));
        SetField(spawn, "Position", CreateFloat2(placement.PositionX, placement.PositionY));
        SetField(spawn, "Rotation", placement.Rotation);
        SetField(spawn, "size", CreateFloat2(placement.SizeX, placement.SizeY));
        return spawn;
    }

    public object CreatePrefabKey(BuildPlacementIntent placement)
    {
        if (placement == null) throw new ArgumentNullException(nameof(placement));
        placement.Validate();
        var category = Enum.ToObject(_prefabCategoryType, placement.Category);
        return Activator.CreateInstance(_prefabKeyType, new[] { category, (object)placement.Key })
            ?? throw new InvalidOperationException("Could not create the placement prefab key.");
    }

    private object CreateFloat2(float x, float y)
    {
        return Activator.CreateInstance(_float2Type, new object[] { x, y })
            ?? throw new InvalidOperationException("Could not create the placement vector.");
    }

    private static object GetField(object instance, string name)
    {
        return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new MissingFieldException(instance.GetType().FullName, name);
    }

    private static void SetField(object instance, string name, object value)
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, name);
        field.SetValue(instance, value);
    }
}
