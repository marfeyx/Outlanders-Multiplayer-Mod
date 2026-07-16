using System;
using OutlandersMultiplayer.Core.Protocol;
using OutlandersMultiplayer.Core.Relay;

namespace OutlandersMultiplayer.Mod.Overlay;

public sealed class MultiplayerOverlay
{
    private readonly MultiplayerController _controller;
    private bool _visible = true;
    private string _host = "127.0.0.1";
    private string _port = ProtocolConstants.DefaultPort.ToString();
    private string _relayHost = "127.0.0.1";
    private string _relayPort = (ProtocolConstants.DefaultPort + 1).ToString();
    private string _roomCode = "OUTLANDERS";
    private string _sessionKey = string.Empty;
    private string _joinCode = string.Empty;
    private string _playerName = Environment.UserName;
    private bool _showAdvanced;
    private readonly ReflectionGui _gui = new();

    public MultiplayerOverlay(MultiplayerController controller)
    {
        _controller = controller;
    }

    public void Draw()
    {
        if (!_visible)
        {
            _gui.SetBackgroundColor(0.22f, 0.32f, 0.26f);
            _gui.SetContentColor(0.96f, 0.93f, 0.84f);
            if (_gui.Button(22, 22, 132, 34, "Multiplayer"))
            {
                _visible = true;
            }

            _gui.ResetColors();
            return;
        }

        DrawPanel();
        _gui.ResetColors();
    }

    private void DrawPanel()
    {
        const float x = 24f;
        const float y0 = 24f;
        const float width = 468f;
        const float pad = 14f;
        var requiredAction = _controller.State.RequiredAction;
        var actionHeight = string.IsNullOrWhiteSpace(requiredAction) ? 0f : 62f;
        var panelHeight = (_showAdvanced ? 616f : 466f) + actionHeight;
        var formHeight = _showAdvanced ? 356f : 208f;
        var y = y0;

        DrawBox(x, y, width, panelHeight, 0.13f, 0.15f, 0.13f, " ");
        DrawBox(x + 4, y + 4, width - 8, panelHeight - 8, 0.29f, 0.31f, 0.25f, " ");
        DrawBox(x + 10, y + 10, width - 20, 62, 0.18f, 0.24f, 0.19f, " ");

        DrawLabel(x + 24, y + 18, 250, 24, "Outlanders Multiplayer", 0.98f, 0.95f, 0.84f);
        DrawLabel(x + 24, y + 42, 350, 20, "Host online, copy one code, and send it to your friend.", 0.76f, 0.81f, 0.68f);
        if (DrawButton(x + width - 78, y + 22, 48, 28, "Hide", 0.27f, 0.24f, 0.21f))
        {
            _visible = false;
        }

        y += 86;
        DrawStatusStrip(x + pad, y, width - pad * 2);
        y += 52;

        DrawHostingSaveStrip(x + pad, y, width - pad * 2);
        y += 68;

        if (actionHeight > 0)
        {
            DrawBox(x + pad, y, width - pad * 2, 50, 0.24f, 0.31f, 0.22f, " ");
            DrawLabel(x + pad + 12, y + 5, width - 52, 18, "Host world ready", 0.92f, 0.91f, 0.76f);
            DrawLabel(x + pad + 12, y + 25, width - 52, 18, requiredAction, 0.82f, 0.86f, 0.72f);
            y += actionHeight;
        }

        DrawBox(x + pad, y, width - pad * 2, formHeight, 0.21f, 0.23f, 0.19f, " ");
        y += 14;
        DrawField(x + 36, y, "Player", ref _playerName, 32);
        y += 34;
        DrawField(x + 36, y, "Join Code", ref _joinCode, 512);
        y += 42;

        if (DrawButton(x + 36, y, 126, 34, "Host Online", 0.40f, 0.54f, 0.36f))
        {
            HostOnline();
        }

        if (DrawButton(x + 170, y, 126, 34, "Join Code", 0.34f, 0.42f, 0.52f))
        {
            JoinOnline();
        }

        if (DrawButton(x + 304, y, 92, 34, "Copy Code", 0.35f, 0.38f, 0.30f))
        {
            _gui.SetClipboard(_joinCode);
        }

        y += 44;
        if (DrawButton(x + 36, y, 126, 30, _showAdvanced ? "Hide Advanced" : "Advanced", 0.26f, 0.29f, 0.24f))
        {
            _showAdvanced = !_showAdvanced;
        }

        if (DrawButton(x + 304, y, 92, 30, "Disconnect", 0.39f, 0.29f, 0.24f))
        {
            _controller.Disconnect();
        }

        y += 42;
        if (_showAdvanced)
        {
            DrawField(x + 36, y, "Relay Host", ref _relayHost, 128);
            y += 34;
            DrawField(x + 36, y, "Relay Port", ref _relayPort, 8);
            y += 34;
            DrawField(x + 36, y, "Direct IP", ref _host, 128);
            y += 34;
            DrawField(x + 36, y, "Direct Port", ref _port, 8);
            y += 38;

            if (DrawButton(x + 36, y, 126, 30, "Host Direct", 0.32f, 0.42f, 0.32f))
            {
                _controller.Host(ParsePort(), _sessionKey);
            }

            if (DrawButton(x + 170, y, 126, 30, "Join Direct", 0.30f, 0.36f, 0.46f))
            {
                _controller.Join(_host, ParsePort(), _sessionKey, _playerName);
            }
        }

        y = y0 + panelHeight - 34;
        var players = _controller.State.Players;
        if (players.Count > 0)
        {
            DrawLabel(x + pad + 10, y, width - 48, 20, "Players: " + string.Join(", ", players), 0.84f, 0.87f, 0.76f);
        }
        else
        {
            DrawLabel(x + pad + 10, y, width - 48, 20, "Your friend only needs the join code. Relay settings are tucked away in Advanced.", 0.70f, 0.74f, 0.66f);
        }
    }

    private void DrawStatusStrip(float x, float y, float width)
    {
        var hasError = !string.IsNullOrWhiteSpace(_controller.State.LastError);
        DrawBox(x, y, width, 38, hasError ? 0.38f : 0.23f, hasError ? 0.18f : 0.30f, hasError ? 0.16f : 0.23f, " ");
        DrawBox(x + 12, y + 11, 14, 14, hasError ? 0.72f : 0.42f, hasError ? 0.28f : 0.62f, hasError ? 0.20f : 0.35f, " ");
        DrawLabel(x + 34, y + 8, width - 44, 22, hasError ? _controller.State.LastError : _controller.State.StatusText, 0.97f, 0.94f, 0.84f);
    }

    private void DrawHostingSaveStrip(float x, float y, float width)
    {
        DrawBox(x, y, width, 54, 0.20f, 0.25f, 0.20f, " ");
        DrawLabel(x + 12, y + 5, width - 156, 20, "Hosting save", 0.78f, 0.82f, 0.70f);
        DrawLabel(x + 12, y + 26, width - 156, 20, _controller.HostingSaveDisplayPath, 0.97f, 0.94f, 0.84f);

        if (DrawButton(x + width - 136, y + 12, 28, 30, "<", 0.30f, 0.36f, 0.28f))
        {
            _controller.SelectPreviousHostingSave();
        }

        if (DrawButton(x + width - 102, y + 12, 28, 30, ">", 0.30f, 0.36f, 0.28f))
        {
            _controller.SelectNextHostingSave();
        }

        if (DrawButton(x + width - 68, y + 12, 58, 30, "Refresh", 0.29f, 0.34f, 0.27f))
        {
            _controller.RefreshHostingSaveSelection();
        }
    }

    private void DrawField(float x, float y, string label, ref string value, int maxLength)
    {
        DrawLabel(x, y + 3, 112, 22, label, 0.78f, 0.82f, 0.70f);
        _gui.SetBackgroundColor(0.80f, 0.78f, 0.64f);
        _gui.SetContentColor(0.12f, 0.14f, 0.11f);
        value = _gui.TextField(x + 118, y, 270, 26, value, maxLength);
        _gui.ResetColors();
    }

    private void DrawBox(float x, float y, float width, float height, float r, float g, float b, string text)
    {
        _gui.SetBackgroundColor(r, g, b);
        _gui.SetContentColor(0.96f, 0.93f, 0.84f);
        _gui.Box(x, y, width, height, text);
        _gui.ResetColors();
    }

    private void DrawLabel(float x, float y, float width, float height, string text, float r, float g, float b)
    {
        _gui.SetContentColor(r, g, b);
        _gui.Label(x, y, width, height, text);
        _gui.ResetColors();
    }

    private bool DrawButton(float x, float y, float width, float height, string text, float r, float g, float b)
    {
        _gui.SetBackgroundColor(r, g, b);
        _gui.SetContentColor(0.98f, 0.96f, 0.86f);
        var clicked = _gui.Button(x, y, width, height, text);
        _gui.ResetColors();
        return clicked;
    }

    private void HostOnline()
    {
        if (IsLocalRelayHost(_relayHost))
        {
            _controller.State.SetError("Set a public relay host in Advanced before hosting online.");
            _showAdvanced = true;
            return;
        }

        _roomCode = JoinCode.CreateRoomCode();
        _sessionKey = JoinCode.CreateSessionKey();
        _joinCode = JoinCode.Encode(_relayHost, ParseRelayPort(), _roomCode, _sessionKey);
        _controller.HostViaRelay(_relayHost, ParseRelayPort(), _roomCode, _sessionKey);
    }

    private void JoinOnline()
    {
        if (!JoinCode.TryDecode(_joinCode, out var code))
        {
            _controller.State.SetError("Join code is invalid.");
            return;
        }

        _relayHost = code.RelayHost;
        _relayPort = code.RelayPort.ToString();
        _roomCode = code.RoomCode;
        _sessionKey = code.SessionKey;
        _controller.JoinViaRelay(code.RelayHost, code.RelayPort, code.RoomCode, code.SessionKey, _playerName);
    }

    private int ParsePort()
    {
        return int.TryParse(_port, out var parsed) && parsed > 0 ? parsed : ProtocolConstants.DefaultPort;
    }

    private int ParseRelayPort()
    {
        return int.TryParse(_relayPort, out var parsed) && parsed > 0 ? parsed : ProtocolConstants.DefaultPort + 1;
    }

    private static bool IsLocalRelayHost(string host)
    {
        var value = (host ?? string.Empty).Trim();
        return value.Length == 0
            || value.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || value.Equals("::1", StringComparison.OrdinalIgnoreCase);
    }
}
