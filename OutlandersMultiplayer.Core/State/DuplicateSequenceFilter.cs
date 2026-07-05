using System.Collections.Generic;

namespace OutlandersMultiplayer.Core.State;

public sealed class DuplicateSequenceFilter
{
    private readonly int _capacity;
    private readonly Queue<uint> _order = new();
    private readonly HashSet<uint> _seen = new();

    public DuplicateSequenceFilter(int capacity = 512)
    {
        _capacity = capacity;
    }

    public bool Accept(uint sequence)
    {
        if (_seen.Contains(sequence))
        {
            return false;
        }

        _seen.Add(sequence);
        _order.Enqueue(sequence);
        while (_order.Count > _capacity)
        {
            _seen.Remove(_order.Dequeue());
        }

        return true;
    }
}
