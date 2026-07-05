using OutlandersMultiplayer.Core.Session;
using OutlandersMultiplayer.Mod.Game;
using OutlandersMultiplayer.Mod.Overlay;
using MelonLoader;

[assembly: MelonInfo(typeof(OutlandersMultiplayer.Mod.OutlandersMultiplayerMelon), "Outlanders Multiplayer", "0.1.0", "Codex")]
[assembly: MelonGame("Pomelo Games", "Outlanders")]

namespace OutlandersMultiplayer.Mod;

public sealed class OutlandersMultiplayerMelon : MelonMod
{
    private SessionState? _state;
    private MultiplayerController? _controller;
    private MultiplayerOverlay? _overlay;
    private GameHookBridge? _hooks;

    public override void OnInitializeMelon()
    {
        _state = new SessionState();
        _controller = new MultiplayerController(_state, Log);
        _overlay = new MultiplayerOverlay(_controller);
        _hooks = new GameHookBridge(Log);
        _hooks.InstallInstrumentation();
        Log("Outlanders Multiplayer initialized.");
    }

    public override void OnUpdate()
    {
        _hooks?.Update();
        _controller?.Poll();
    }

    public override void OnGUI()
    {
        _overlay?.Draw();
    }

    public override void OnApplicationQuit()
    {
        _controller?.Dispose();
    }

    private void Log(string message)
    {
        LoggerInstance.Msg(message);
    }
}
