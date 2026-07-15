using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace OutlandersMultiplayer.Core.Protocol;

[DataContract]
public sealed class BuildPlacementIntent
{
    public const string CommandType = "outlanders.build.place";
    public const int BuildingPrefabCategory = 3;

    [DataMember(Order = 1)]
    public int Category { get; set; }

    [DataMember(Order = 2)]
    public int Key { get; set; }

    [DataMember(Order = 3)]
    public float PositionX { get; set; }

    [DataMember(Order = 4)]
    public float PositionY { get; set; }

    [DataMember(Order = 5)]
    public float Rotation { get; set; }

    [DataMember(Order = 6)]
    public float SizeX { get; set; }

    [DataMember(Order = 7)]
    public float SizeY { get; set; }

    public string ToJson()
    {
        Validate();
        var serializer = new DataContractJsonSerializer(typeof(BuildPlacementIntent));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, this);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static BuildPlacementIntent FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("Build placement payload is required.");
        var serializer = new DataContractJsonSerializer(typeof(BuildPlacementIntent));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var intent = serializer.ReadObject(stream) as BuildPlacementIntent
            ?? throw new InvalidDataException("Build placement payload is invalid.");
        intent.Validate();
        return intent;
    }

    public void Validate()
    {
        if (Category != BuildingPrefabCategory)
        {
            throw new InvalidDataException($"Only building prefab category {BuildingPrefabCategory} is supported.");
        }

        if (Key <= 0) throw new InvalidDataException("Building key must be positive.");
        ValidateFinite(PositionX, nameof(PositionX));
        ValidateFinite(PositionY, nameof(PositionY));
        ValidateFinite(Rotation, nameof(Rotation));
        ValidateFinite(SizeX, nameof(SizeX));
        ValidateFinite(SizeY, nameof(SizeY));
        if (Math.Abs(PositionX) > 1_000_000 || Math.Abs(PositionY) > 1_000_000)
        {
            throw new InvalidDataException("Build position is outside the supported world range.");
        }

        if (SizeX <= 0 || SizeY <= 0 || SizeX > 512 || SizeY > 512)
        {
            throw new InvalidDataException("Build footprint is invalid.");
        }
    }

    private static void ValidateFinite(float value, string name)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            throw new InvalidDataException($"{name} must be finite.");
        }
    }
}
