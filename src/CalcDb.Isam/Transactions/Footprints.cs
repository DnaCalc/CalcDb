using CalcDb.Format;
using CalcDb.Isam.Edit;
using CalcDb.Storage;

namespace CalcDb.Isam.Transactions;

/// <summary>
/// The candidate lock footprint of a row change over one table's stored pages (II-09 §24, W-05/local-lockers-rebase):
/// ACE's in-process conflict resource is the page. A row's data page conflicts for every row on it (R-ERR-1
/// ERR-PARTIAL: about 18 rows of the 2048-row table; CUR-EDIT conflicts: another row of the 8-row page), and a change of
/// an indexed column conflicts on the index leaf holding the row's entry (R-TXN-1 TXN-LOCAL: an update of the indexed N
/// on a far row is refused 3188, of the unindexed V admitted). The pages a compiled change writes are added by the
/// physical compiler (<see cref="SameWidthWriter"/>, W-05/same-width-mutation; II-09 §24 "The physical compiler returns
/// this closure to the local manager"): the overflow target of a moved row; in this slice the new key stays in the old
/// key's leaf, so no further leaf is added. A new row's landing page and the maps and descriptors of growth are
/// W-05/growth-structural-closure's.
/// </summary>
public sealed class TableFootprints(PageCache file, FormatDescriptor d, TableDefinition table)
{
    public PageCache File { get; } = file;

    public FormatDescriptor Format { get; } = d;

    public TableDefinition Table { get; } = table;

    /// <summary>The data page of <paramref name="row"/>'s root.</summary>
    public static LockResource Row(RowId row) => new(row.Page, LockPurpose.DataPage);

    /// <summary>The leaf holding <paramref name="row"/>'s entry of <paramref name="index"/>, or null when the tree does not show it.</summary>
    public LockResource? KeyLeaf(IndexCheck index, RowId row)
    {
        var navigator = new IndexNavigator(File, Format, index.Physical, Table.FirstPage);
        return navigator.Find(row) is { } at ? new LockResource(at.Leaf, LockPurpose.IndexLeaf) : null;
    }
}
