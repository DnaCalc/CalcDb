using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam;

/// <summary>
/// VisitExternalValue (II-04 §7.4): reads a long value into one completely admitted owned result. Each LVAL row is
/// pinned, checked (subtype, slot, extent) and released before the next; the payload length excludes locators; a chain
/// must make progress; (page, row) cycles, early termination and overlong chunks are typed failures, never a shorter
/// value. The declared length is admitted against the budget before anything is allocated.
/// </summary>
public static class LongValueReader
{
    private const int ChainOnStack = 16;

    /// <summary>
    /// Read the long value <paramref name="plan"/> names; its LVAL rows are the current images, or under
    /// <paramref name="view"/> the images that view's serial selects (a row resolved under a view, <see cref="RowView.View"/>).
    /// </summary>
    public static byte[]? Read(LongValuePlan plan, ReadOnlySpan<byte> field, PageCache cache, FormatDescriptor d, long budgetBytes, out TraversalFailure? failure, ReadView? view = null)
    {
        failure = null;
        if (plan.Length > budgetBytes)
        {
            failure = new TraversalFailure($"long value of {plan.Length} bytes exceeds the {budgetBytes}-byte budget", RowId.From(plan.First), ResourceLimit: true);
            return null;
        }
        if (plan.Form == LongValueForm.Inline)
        {
            return field.Slice(plan.InlineStart, (int)plan.Length).ToArray();
        }
        byte[] result = new byte[plan.Length];
        int filled = 0;
        // The chain's visited rows: the first ones on the stack, a long chain's on the heap (task 7jdb).
        var seen = new VisitedSet<RowId>(stackalloc RowId[ChainOnStack], int.MaxValue);
        RowId at = RowId.From(plan.First);
        while (true)
        {
            if (!seen.Add(at))
            {
                failure = new TraversalFailure("long-value chain cycle", at);
                return null;
            }
            using PagePin? pin = (view is null ? cache.Pin(at.Page) : view.Pin(at.Page)).Pin;
            if (pin is null)
            {
                failure = new TraversalFailure($"LVAL page {at.Page} could not be pinned", at);
                return null;
            }
            ReadOnlySpan<byte> page = pin.Bytes;
            if (!DataPages.IsLvalPage(page, d))
            {
                failure = new TraversalFailure($"page {at.Page} is not an LVAL page", at);
                return null;
            }
            SlotView? slot = DataPages.CheckedSlot(page, at.Row, d, out string? slotFailure);
            if (slot is not { } s)
            {
                failure = new TraversalFailure(slotFailure!, at);
                return null;
            }
            ReadOnlySpan<byte> row = page.Slice(s.Start, s.Length);
            RowLocator? next = null;
            ReadOnlySpan<byte> payload = row;
            if (plan.Form == LongValueForm.Chain)
            {
                if (row.Length < 4)
                {
                    failure = new TraversalFailure($"chained LVAL row of {row.Length} bytes has no next locator", at);
                    return null;
                }
                next = RowLocator.Decode(row[..4]);
                payload = row[4..];
            }
            int remaining = result.Length - filled;
            if (payload.Length > remaining)
            {
                failure = new TraversalFailure($"LVAL row carries {payload.Length} payload bytes; only {remaining} remain of the declared {plan.Length}", at);
                return null;
            }
            payload.CopyTo(result.AsSpan(filled));
            filled += payload.Length;
            if (filled == result.Length)
            {
                // The terminal locator's accepted shapes are R-FMT-2 evidence; the declared length decides the end.
                return result;
            }
            if (plan.Form == LongValueForm.SingleRow)
            {
                failure = new TraversalFailure($"single-row long value holds {filled} of {plan.Length} bytes", at);
                return null;
            }
            if (payload.Length == 0)
            {
                failure = new TraversalFailure("a chained LVAL row with no payload makes no progress", at);
                return null;
            }
            if (next is not { } n || n.Page == 0)
            {
                failure = new TraversalFailure($"the chain ends after {filled} of {plan.Length} bytes", at);
                return null;
            }
            at = RowId.From(n);
        }
    }
}
