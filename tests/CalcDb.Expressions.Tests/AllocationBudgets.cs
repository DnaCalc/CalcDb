namespace CalcDb.Expressions.Tests;

/// <summary>
/// The allocation budgets (The Plan §3.2) run alone, as Isam's do (tests/CalcDb.Isam.Tests/AllocationBudgets.cs, task
/// 58sc): GC.GetAllocatedBytesForCurrentThread counts this thread's allocation context, and work on other threads can add
/// to it mid-pass. InStrStrCompTests' warm text compare read 680 B and 3,712 B in two landing checks run beside other
/// jobs (2026-11-21) and 0 in every run alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationBudgets
{
    public const string Name = "Allocation budgets";
}
