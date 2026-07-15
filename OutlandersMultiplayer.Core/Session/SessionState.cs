using System;
using System.Collections.Generic;

namespace OutlandersMultiplayer.Core.Session;

public sealed class SessionState
{
    private readonly object _sync = new();
    private readonly List<string> _players = new();

    public SessionStatus Status { get; private set; } = SessionStatus.Offline;
    public string LastError { get; private set; } = string.Empty;
    public string StatusText { get; private set; } = "Offline";
    public string RequiredAction { get; private set; } = string.Empty;
    public int PingMilliseconds { get; private set; }

    public IReadOnlyList<string> Players
    {
        get
        {
            lock (_sync)
            {
                return _players.ToArray();
            }
        }
    }

    public void SetStatus(SessionStatus status, string text)
    {
        lock (_sync)
        {
            Status = status;
            StatusText = text;
            if (status != SessionStatus.Error)
            {
                LastError = string.Empty;
            }
        }
    }

    public void SetError(string error)
    {
        lock (_sync)
        {
            Status = SessionStatus.Error;
            LastError = error ?? string.Empty;
            StatusText = "Error";
            RequiredAction = string.Empty;
        }
    }

    public void SetRequiredAction(string action)
    {
        lock (_sync)
        {
            RequiredAction = action ?? string.Empty;
        }
    }

    public void ClearRequiredAction()
    {
        SetRequiredAction(string.Empty);
    }

    public void SetPing(int milliseconds)
    {
        lock (_sync)
        {
            PingMilliseconds = Math.Max(0, milliseconds);
        }
    }

    public void SetPlayers(IEnumerable<string> players)
    {
        lock (_sync)
        {
            _players.Clear();
            _players.AddRange(players);
        }
    }
}
