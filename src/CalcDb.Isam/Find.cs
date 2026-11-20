namespace CalcDb.Isam;

/// <summary>The four Find methods (R-CURSOR-1 CUR-FIND).</summary>
public enum FindDirection
{
    First,
    Last,
    Next,
    Previous,
}

/// <summary>A predicate's verdict on the cursor's current row.</summary>
public enum RowTest
{
    Match,
    NoMatch,

    /// <summary>The test failed; the predicate keeps the failure for its caller.</summary>
    Failed,
}

/// <summary>
/// A criteria tested row by row by a Find (W-06/restricted-scalar: the bound expression's Find adapter). It reads the
/// cursor's current row through the cursor's own field reads and never moves the cursor.
/// </summary>
public interface IRowPredicate
{
    RowTest Test(IRecordCursor cursor);
}

/// <summary>How a Find ended.</summary>
public enum FindOutcome
{
    /// <summary>A row matched: it is current and NoMatch is False.</summary>
    Found,

    /// <summary>No row matched: NoMatch is True and the position follows CUR-FIND's miss rule.</summary>
    NotFound,

    /// <summary>The criteria failed (before the search or on a row): NoMatch is unchanged and the position follows CUR-FIND's failure rule.</summary>
    Failed,

    /// <summary>A position the records do not decide (<see cref="FindOpen"/>); the cursor is left as it was before the Find.</summary>
    NotModelled,
}

/// <summary>The cases of CUR-FIND that no record decides, each an open item of the qualified.md note.</summary>
public enum FindOpen
{
    None,

    /// <summary>Where a FindLast miss leaves the cursor (its search start, the last row, or where it was).</summary>
    FindLastMissPosition,

    /// <summary>Where a failing FindNext, FindPrevious or FindLast leaves the cursor away from the first row.</summary>
    FailurePosition,

    /// <summary>Whether a criteria failure is reported on an empty recordset, or at BOF or EOF where FindNext and FindPrevious do not search.</summary>
    FailureWithoutSearch,

    /// <summary>A snapshot's Find tested a value no record decides after a later publication (<see cref="SnapshotValues"/>, CUR-POP Open F).</summary>
    UndecidedSnapshotValue,
}

/// <summary>A Find's outcome and, for <see cref="FindOutcome.NotModelled"/>, the case.</summary>
public readonly record struct FindResult(FindOutcome Outcome, FindOpen Open = FindOpen.None);
