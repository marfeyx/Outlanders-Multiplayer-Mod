using System;
using OutlandersMultiplayer.Core.Protocol;

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
    private string _playerName = Environment.UserName;
    private bool _relayMode;
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
        var y = y0;

        DrawBox(x, y, width, 452, 0.13f, 0.15f, 0.13f, " ");
        DrawBox(x + 4, y + 4, width - 8, 444, 0.29f, 0.31f, 0.25f, " ");
        DrawBox(x + 10, y + 10, width - 20, 62, 0.18f, 0.24f, 0.19f, " ");

        DrawLabel(x + 24, y + 18, 250, 24, "Outlanders Multiplayer", 0.98f, 0.95f, 0.84f);
        DrawLabel(x + 24, y + 42, 350, 20, _relayMode ? "Internet relay room" : "Direct connection", 0.76f, 0.81f, 0.68f);
        if (DrawButton(x + width - 78, y + 22, 48, 28, "Hide", 0.27f, 0.24f, 0.21f))
        {
            _visible = false;
        }

        y += 86;
        DrawStatusStrip(x + pad, y, width - pad * 2);
        y += 52;

        if (DrawButton(x + pad, y, 214, 34, "Direct", !_relayMode ? 0.35f : 0.22f, !_relayMode ? 0.45f : 0.25f, !_relayMode ? 0.34f : 0.22f))
        {
            _relayMode = false;
        }

        if (DrawButton(x + pad + 222, y, 214, 34, "Relay", _relayMode ? 0.35f : 0.22f, _relayMode ? 0.45f : 0.25f, _relayMode ? 0.34f : 0.22f))
        {
            _relayMode = true;
        }
        y += 48;

        DrawBox(x + pad, y, width - pad * 2, 186, 0.21f, 0.23f, 0.19f, " ");
        y += 14;
        DrawField(x + 36, y, "Player", ref _playerName, 32);
        y += 34;
        DrawField(x + 36, y, "Session Key", ref _sessionKey, 64);
        y += 42;

        if (_relayMode)
        {
            DrawField(x + 36, y, "Relay Host", ref _relayHost, 128);
            y += 34;
            DrawField(x + 36, y, "Relay Port", ref _relayPort, 8);
            y += 34;
            DrawField(x + 36, y, "Room Code", ref _roomCode, 32);
            y += 42;
        }
        else
        {
            DrawField(x + 36, y, "Host/IP", ref _host, 128);
            y += 34;
            DrawField(x + 36, y, "Port", ref _port, 8);
            y += 76;
        }

        if (DrawButton(x + pad, y, 146, 36, "Host", 0.40f, 0.54f, 0.36f))
        {
            if (_relayMode)
            {
                _controller.HostViaRelay(_relayHost, ParseRelayPort(), _roomCode, _sessionKey);
            }
            else
            {
                _controller.Host(ParsePort(), _sessionKey);
            }
        }

        if (DrawButton(x + pad + 154, y, 146, 36, "Join", 0.34f, 0.42f, 0.52f))
        {
            if (_relayMode)
            {
                _controller.JoinViaRelay(_relayHost, ParseRelayPort(), _roomCode, _sessionKey, _playerName);
            }
            else
            {
                _controller.Join(_host, ParsePort(), _sessionKey, _playerName);
            }
        }

        if (DrawButton(x + pad + 308, y, 128, 36, "Disconnect", 0.39f, 0.29f, 0.24f))
        {
            _controller.Disconnect();
        }

        y += 50;
        var players = _controller.State.Players;
        if (players.Count > 0)
        {
            DrawLabel(x + pad + 10, y, width - 48, 20, "Players: " + string.Join(", ", players), 0.84f, 0.87f, 0.76f);
        }
        else
        {
            DrawLabel(x + pad + 10, y, width - 48, 20, _relayMode ? "Share relay host, room code, and session key with your friend." : "Use direct mode for LAN, VPN, or port-forwarded hosts.", 0.70f, 0.74f, 0.66f);
        }
    }

    private void DrawStatusStrip(float x, float y, float width)
    {
        var hasError = !string.IsNullOrWhiteSpace(_controller.State.LastError);
        DrawBox(x, y, width, 38, hasError ? 0.38f : 0.23f, hasError ? 0.18f : 0.30f, hasError ? 0.16f : 0.23f, " ");
        DrawBox(x + 12, y + 11, 14, 14, hasError ? 0.72f : 0.42f, hasError ? 0.28f : 0.62f, hasError ? 0.20f : 0.35f, " ");
        DrawLabel(x + 34, y + 8, width - 44, 22, hasError ? _controller.State.LastError : _controller.State.StatusText, 0.97f, 0.94f, 0.84f);
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

    private int ParsePort()
    {
        return int.TryParse(_port, out var parsed) && parsed > 0 ? parsed : ProtocolConstants.DefaultPort;
    }

    private int ParseRelayPort()
    {
        return int.TryParse(_relayPort, out var parsed) && parsed > 0 ? parsed : ProtocolConstants.DefaultPort + 1;
    }
}
