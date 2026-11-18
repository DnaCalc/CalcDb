namespace CalcDb.Isam;

/// <summary>
/// What a bounded walk has visited (index descents and peer walks, long-value chains): on the stack up to the span's
/// length, then on the heap when the budget is larger (tasks x8d9, 7jdb). Add fails on a revisit and once the budget is
/// used, as a HashSet with a count check did, so a short walk allocates nothing.
/// </summary>
internal ref struct VisitedSet<T>(Span<T> stack, int budget = -1)
    where T : unmanaged, IEquatable<T>
{
    private readonly Span<T> _stack = stack;
    private readonly int _budget = budget < 0 ? stack.Length : budget;
    private int _count;
    private HashSet<T>? _spill;

    public bool Add(T item)
    {
        if (_stack[..Math.Min(_count, _stack.Length)].Contains(item) || (_spill is not null && _spill.Contains(item)) || _count >= _budget)
        {
            return false;
        }
        if (_count < _stack.Length)
        {
            _stack[_count] = item;
        }
        else
        {
            (_spill ??= []).Add(item);
        }
        _count++;
        return true;
    }
}
