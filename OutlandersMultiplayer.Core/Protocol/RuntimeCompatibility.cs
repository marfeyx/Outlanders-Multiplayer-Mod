namespace OutlandersMultiplayer.Core.Protocol;

public sealed class RuntimeCompatibility
{
    public ushort ProtocolVersion { get; set; } = ProtocolConstants.ProtocolVersion;
    public string OutlandersBuildGuid { get; set; } = string.Empty;
    public string UnityVersion { get; set; } = string.Empty;
    public string ModVersion { get; set; } = string.Empty;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(OutlandersBuildGuid) &&
        !string.IsNullOrWhiteSpace(UnityVersion) &&
        !string.IsNullOrWhiteSpace(ModVersion);
}
