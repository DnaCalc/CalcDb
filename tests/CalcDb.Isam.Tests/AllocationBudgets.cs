namespace CalcDb.Isam.Tests;

/// <summary>
/// The allocation budgets (The Plan §3.2) run alone. GC.GetAllocatedBytesForCurrentThread counts this thread's
/// allocation context, and a GC that a test on another thread triggers mid-pass can add up to one allocation quantum
/// (about 8 KB) to it: the resident Text Seek budget read 7.8 B/call in about one full run in eight with collections in
/// parallel and 0.0 in ten runs without (task 58sc, 2026-11-20). The product code allocates nothing there; the
/// measurement needs a quiet process.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationBudgets
{
    public const string Name = "Allocation budgets";
}
