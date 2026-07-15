using System.Collections.Generic;

namespace OutlandersMultiplayer.Core.State;

public sealed class DuplicateSequenceFilter
{
    private readonly int _capacity;
    private readonly Queue<uint> _order = new();
    private readonly HashSet<uint> _seen = new();
    private uint _highest;
    private bool _hasHighest;

    public DuplicateSequenceFilter(int capacity = 512)
    {
        _capacity = capacity;
    }

    public bool Accept(uint sequence)
    {
        if ((_hasHighest && !IsNewer(sequence, _highest)) || _seen.Contains(sequence))
        {
            return false;
        }

        _highest = sequence;
        _hasHighest = true;
        _seen.Add(sequence);
        _order.Enqueue(sequence);
        while (_order.Count > _capacity)
        {
            _seen.Remove(_order.Dequeue());
        }

        return true;
    }

    public void Reset()
    {
        _order.Clear();
        _seen.Clear();
        _highest = 0;
        _hasHighest = false;
    }

    private static bool IsNewer(uint candidate, uint current)
    {
        var distance = unchecked(candidate - current);
        return distance != 0 && distance < 0x80000000U;
    }
}
