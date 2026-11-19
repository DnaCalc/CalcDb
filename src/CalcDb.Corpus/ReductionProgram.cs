using CalcDb.Evidence;

namespace CalcDb.Corpus;

/// <summary>
/// The object-script transformation vocabulary of the reducer (II-15 §23, T-EVID-31): which top-level operations of a
/// question's program may be removed, and what must stay when they are. An object-script reducer removes calls only if
/// the remaining object construction and ownership stay meaningful, so:
/// <list type="bullet">
/// <item>An operation that binds a name is kept while any kept operation uses that binding (its <em>reaching definition</em>
/// in the original program: the last earlier operation that bound the name).</item>
/// <item>A <c>release</c> follows its object: it is kept exactly when the operation that bound the released name is kept, and
/// is never removed alone.</item>
/// <item>Creation, activation, opening, file and module observations, barriers, native operations and compound operations
/// (<c>loopUntil</c>, <c>forEachItem</c>, <c>enumerate</c>, <c>onThread</c>) always stay; what they use stays with them.</item>
/// <item>Only <c>invoke</c>, <c>describeType</c> and <c>queryInterfaces</c> are removable units.</item>
/// <item>The operation the defining failure falls on (the anchor) always stays.</item>
/// </list>
/// Operation indices are always those of the original program, so a chain stays readable as the program shrinks.
/// </summary>
public sealed class ReductionProgram
{
    private readonly Operation[] _ops;
    private readonly int[][] _needs;
    private readonly int[] _releaseOwner;
    private readonly bool[] _alwaysKept;

    /// <summary>The removable units, as original operation indices in program order.</summary>
    public IReadOnlyList<int> Units { get; }

    public Question Original { get; }

    public ReductionProgram(Question original, int anchor)
    {
        if (original.Peers.Count > 0)
        {
            throw new ArgumentException("A question with further participants cannot be reduced: its barriers order the participants.", nameof(original));
        }
        Original = original;
        _ops = [.. original.Program];
        _needs = new int[_ops.Length][];
        _releaseOwner = new int[_ops.Length];
        _alwaysKept = new bool[_ops.Length];
        var definition = new Dictionary<string, int>(StringComparer.Ordinal);
        var units = new List<int>();
        for (int i = 0; i < _ops.Length; i++)
        {
            _releaseOwner[i] = -1;
            Operation op = _ops[i];
            var needs = new SortedSet<int>();
            foreach (string name in Uses(op))
            {
                if (definition.TryGetValue(name, out int def))
                {
                    needs.Add(def);
                }
            }
            _needs[i] = [.. needs];
            if (op is Release release)
            {
                // Follows its object; an unbound target has no owner and stays.
                if (definition.TryGetValue(release.Target, out int owner))
                {
                    _releaseOwner[i] = owner;
                    _needs[i] = [];
                }
                else
                {
                    _alwaysKept[i] = true;
                }
            }
            else if (op is Invoke or DescribeType or QueryInterfaces)
            {
                if (i == anchor)
                {
                    _alwaysKept[i] = true;
                }
                else
                {
                    units.Add(i);
                }
            }
            else
            {
                _alwaysKept[i] = true;
            }
            foreach (string name in Binds(op))
            {
                definition[name] = i;
            }
        }
        Units = units;
    }

    /// <summary>
    /// The operations a candidate keeps when it keeps these units: every always-kept operation, the units, every operation
    /// a kept operation needs, and the releases of kept objects. Returned as original operation indices, in order.
    /// </summary>
    public IReadOnlyList<int> Closure(IEnumerable<int> keptUnits)
    {
        var kept = new bool[_ops.Length];
        var work = new Stack<int>();
        void Keep(int i)
        {
            if (!kept[i])
            {
                kept[i] = true;
                work.Push(i);
            }
        }
        for (int i = 0; i < _ops.Length; i++)
        {
            if (_alwaysKept[i])
            {
                Keep(i);
            }
        }
        foreach (int unit in keptUnits)
        {
            Keep(unit);
        }
        while (work.Count > 0)
        {
            foreach (int need in _needs[work.Pop()])
            {
                Keep(need);
            }
        }
        // A release is kept exactly when its object's binder is: a kept binder drags its releases in (they bind nothing).
        for (int i = 0; i < _ops.Length; i++)
        {
            if (_releaseOwner[i] >= 0 && kept[_releaseOwner[i]])
            {
                kept[i] = true;
            }
        }
        return [.. Enumerable.Range(0, _ops.Length).Where(i => kept[i])];
    }

    /// <summary>The units a candidate keeps (those of <paramref name="operations"/> that are removable).</summary>
    public IReadOnlyList<int> UnitsOf(IReadOnlyList<int> operations) => [.. Units.Where(u => operations.Contains(u))];

    /// <summary>Why a unit cannot be removed alone: an operation of the program that needs it, or null when it can.</summary>
    public int? RequiredBy(int unit, IReadOnlyList<int> operations)
    {
        foreach (int i in operations)
        {
            if (i != unit && Array.IndexOf(_needs[i], unit) >= 0)
            {
                return i;
            }
        }
        return null;
    }

    /// <summary>The question with only these operations (original indices). Labels stay; the key changes with the program.</summary>
    public Question Build(IReadOnlyList<int> operations) => Original with { Program = [.. operations.Select(i => _ops[i])] };

    private static IEnumerable<string> Binds(Operation op) => op switch
    {
        CreateObject c => [c.Bind],
        Invoke { Bind: { } b } => [b],
        Activate a => [a.Bind],
        TypeCreate t => [t.Bind],
        NativeOpen n => [n.Bind],
        // Names bound inside compound operations are scoped to them; the compound operation is always kept.
        _ => [],
    };

    private static IEnumerable<string> Uses(Operation op)
    {
        switch (op)
        {
            case Invoke invoke:
                yield return invoke.Target;
                foreach (string name in invoke.Args.Concat(invoke.Named?.Select(n => n.Value) ?? []).SelectMany(ObjectNames))
                {
                    yield return name;
                }
                break;
            case Release release:
                yield return release.Target;
                break;
            case DescribeType d:
                yield return d.Target;
                break;
            case QueryInterfaces q:
                yield return q.Target;
                break;
            case LoopUntil loop:
                yield return loop.Target;
                foreach (string name in BodyUses(loop.Body))
                {
                    yield return name;
                }
                break;
            case ForEachItem each:
                yield return each.Target;
                foreach (string name in BodyUses(each.Body).Where(n => n != each.Bind))
                {
                    yield return name;
                }
                break;
            case Enumerate e:
                yield return e.Target;
                foreach (string name in BodyUses(e.Body).Where(n => n != e.Bind))
                {
                    yield return name;
                }
                break;
            case OnThread t:
                foreach (string name in BodyUses(t.Body))
                {
                    yield return name;
                }
                break;
            case TypeCreate tc:
                yield return tc.Target;
                break;
            case Advise a:
                yield return a.Target;
                break;
            case ClaimProcess cp:
                yield return cp.Target;
                break;
            case NativeRead r:
                yield return r.Handle;
                break;
            case NativeLockScan s:
                yield return s.Handle;
                break;
            case NativeClose c:
                yield return c.Handle;
                break;
        }
    }

    private static IEnumerable<string> BodyUses(IReadOnlyList<Operation> body) => body.SelectMany(Uses);

    private static IEnumerable<string> ObjectNames(Arg arg) => arg switch
    {
        ObjectArg o => [o.Name],
        ByRefArg b => ObjectNames(b.Inner),
        _ => [],
    };
}
