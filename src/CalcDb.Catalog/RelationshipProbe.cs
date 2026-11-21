using System.Text;

using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Catalog;

/// <summary>
/// Shared catalog-backed referential integrity context for one open database (W-05/fk-cascades; II-06 §3). Reuses the
/// already-opened <see cref="RelationshipCatalog.Read"/> list without opening a second <c>MSysRelationships</c> reader,
/// and resolves related tables through the session's shared <see cref="TableEditContext"/> cache so cascaded child rows
/// share the session's <see cref="MutationSemantics"/> and <see cref="SameWidthWriter"/>.
/// </summary>
public sealed class CatalogRelationshipContext(
    PageCache cache,
    FormatDescriptor d,
    IReadOnlyList<Relationship> relationships,
    ulong pageCount,
    Func<string, TableEditContext?> resolveEditContext)
{
    internal const int DbRelationUnique = 1;
    internal const int DbRelationDontEnforce = 2;
    internal const int DbRelationUpdateCascade = 256;
    internal const int DbRelationDeleteCascade = 4096;

    public IReadOnlyList<Relationship> Relationships => relationships;

    internal PageCache Cache => cache;

    internal FormatDescriptor Format => d;

    internal ulong PageCount => pageCount;

    /// <summary>Whether any relationship in <c>MSysRelationships</c> names <paramref name="tableName"/> as primary or foreign table.</summary>
    public bool HasRelationships(string tableName) =>
        relationships.Any(r => NamesEqual(r.Table, tableName) || NamesEqual(r.ForeignTable, tableName));

    /// <summary>
    /// Create the <see cref="IRelationshipProbe"/> for <paramref name="tableName"/>, or null when no relationship in
    /// <c>MSysRelationships</c> names it. Does not invoke the edit-context resolver during construction.
    /// </summary>
    public IRelationshipProbe? ForTable(string tableName, TableDefinition table)
    {
        List<Relationship> asParent = [];
        List<Relationship> asChild = [];
        foreach (Relationship r in relationships)
        {
            if (NamesEqual(r.Table, tableName))
            {
                asParent.Add(r);
            }
            if (NamesEqual(r.ForeignTable, tableName))
            {
                asChild.Add(r);
            }
        }
        if (asParent.Count == 0 && asChild.Count == 0)
        {
            return null;
        }
        return new CatalogRelationshipProbe(this, tableName, table, asParent, asChild);
    }

    internal TableEditContext? ResolveEditContext(string name) => resolveEditContext(name);

    internal bool HasEnforcedParentRelationships(string tableName) =>
        relationships.Any(r => NamesEqual(r.Table, tableName) && !IsDontEnforce(r.Attributes));

    internal bool HasOtherEnforcedChildRelationshipOnColumn(string tableName, string excludeRelationName, string foreignColumnName) =>
        relationships.Any(r =>
            NamesEqual(r.ForeignTable, tableName)
            && !NamesEqual(r.Name, excludeRelationName)
            && !IsDontEnforce(r.Attributes)
            && r.Columns.Any(c => NamesEqual(c.Foreign, foreignColumnName)));

    /// <summary>Composed case-insensitive identifier comparison matching <see cref="SchemaWriter"/> (R-CAT-1 CAT-B, FormC).</summary>
    public static bool NamesEqual(string a, string b) =>
        string.Equals(a.Normalize(NormalizationForm.FormC), b.Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase);

    internal static bool IsDontEnforce(int attributes) => (attributes & DbRelationDontEnforce) != 0;

    internal static bool IsAdmittedAttributes(int attributes) => attributes is
        0
        or DbRelationUnique
        or DbRelationDontEnforce
        or DbRelationUpdateCascade
        or DbRelationDeleteCascade
        or (DbRelationUpdateCascade | DbRelationDeleteCascade);
}

/// <summary>
/// Per-table <see cref="IRelationshipProbe"/> backed by <see cref="CatalogRelationshipContext"/>.
/// Rules and citations:
/// <list type="bullet">
/// <item><b>3201 child check</b>: <c>r-cat-1b/rel-orphan-insert</c>, <c>r-q-cursor-dao/edit-fk-violation</c>,
/// <c>r-fix-cursor-dao/con-fk-orphan-insert-failonerror</c>, <c>r-def-cat-txn/cat-rel-compound-key-enforcement</c>
/// (MATCH FULL on INSERT: all-Null FK admitted, partial-Null FK 3201, all-non-Null LONG FK must match parent PK).</item>
/// <item><b>3200 parent check</b>: <c>r-cat-1b/rel-parent-delete-by-attributes</c>,
/// <c>rel-parent-key-update-by-attributes</c>, <c>r-q-cursor-dao/edit-fk-violation</c>,
/// <c>r-err-1b/con-fk-parent-delete</c>.</item>
/// <item><b>Attributes (grbit)</b>: 0 (plain), 1 (dbRelationUnique — orphan INSERT only; parent DELETE/UPDATE with
/// matching child refused by name), 2 (dbRelationDontEnforce — skipped), 256 (dbRelationUpdateCascade), 4096
/// (dbRelationDeleteCascade), 4352 (both); any other bitmask is refused by name before any skip or empty-table shortcut.</item>
/// <item><b>Cascades</b>: single-column LONG leaf-child cascades into at most one child table (with no blocking child on
/// the same parent row) inside SQL action statements (<c>workspace.InStatement</c>; cascade DELETE also at explicit
/// transaction depth 1 per <c>r-def-cat-txn/txn-cascade-delete-rollback-and-commit</c>; cascade UPDATE outside explicit
/// transactions only), per <c>r-cat-1b/rel-parent-delete-by-attributes</c>, <c>rel-parent-key-update-by-attributes</c>,
/// <c>r-fix-expr-sql/fk-actions-dao-relations</c>, <c>r-sql-2-3/act-cascade</c>.</item>
/// </list>
/// </summary>
internal sealed class CatalogRelationshipProbe(
    CatalogRelationshipContext context,
    string tableName,
    TableDefinition table,
    IReadOnlyList<Relationship> asParent,
    IReadOnlyList<Relationship> asChild) : IRelationshipProbe
{
    public bool HasEnforcedParentRelations { get; } = asParent.Any(r => !CatalogRelationshipContext.IsDontEnforce(r.Attributes));

    public string? CheckDeleteAttributes()
    {
        foreach (Relationship r in asParent)
        {
            if (!CatalogRelationshipContext.IsAdmittedAttributes(r.Attributes))
            {
                return $"relationship '{r.Name}' has unrecorded Attributes 0x{r.Attributes:X}";
            }
        }
        return null;
    }

    public RelationshipOutcome CheckUpdate(
        ReadOnlySpan<NativeValue> candidate,
        ReadOnlySpan<NativeValue> originals,
        bool isEdit,
        RowId? self,
        TransactionCoordinator workspace)
    {
        List<string> childFailures = [];
        List<string> parentFailures = [];
        List<string> cascadingTables = [];
        List<CascadeStep>? cascades = null;
        string? openDetail = null;

        // Part A: Child-side checks (3201 when FK is partial-Null on INSERT or missing in parent PK).
        foreach (Relationship r in asChild)
        {
            if (!CatalogRelationshipContext.IsAdmittedAttributes(r.Attributes))
            {
                openDetail ??= $"relationship '{r.Name}' has unrecorded Attributes 0x{r.Attributes:X}";
                continue;
            }
            if (CatalogRelationshipContext.IsDontEnforce(r.Attributes))
            {
                continue;
            }
            bool isSelfRef = CatalogRelationshipContext.NamesEqual(r.Table, r.ForeignTable);
            if (isEdit && isSelfRef)
            {
                openDetail ??= $"UPDATE on self-referencing table '{tableName}' (relationship '{r.Name}') is not recorded";
                continue;
            }
            if (!TryResolveColumns(table, r.Columns, foreign: true, out ColumnDescriptor[]? fkCols))
            {
                openDetail ??= $"relationship '{r.Name}' references missing foreign column on '{tableName}'";
                continue;
            }
            if (isEdit && !AnyChanged(fkCols!, candidate, originals))
            {
                continue;
            }
            if (!AllLongColumns(fkCols!, out ColumnDescriptor? nonLongFk))
            {
                openDetail ??= $"relationship '{r.Name}' foreign-key column '{tableName}.{nonLongFk!.Name}' (type {nonLongFk.TypeCode}) is not LONG and is not recorded";
                continue;
            }

            var fkValues = new NativeValue[fkCols!.Length];
            int nullCount = 0;
            bool anyOrigNull = false;
            for (int i = 0; i < fkCols.Length; i++)
            {
                fkValues[i] = candidate[fkCols[i].Ordinal];
                if (fkValues[i].Kind == NativeKind.Null)
                {
                    nullCount++;
                }
                if (isEdit && originals[fkCols[i].Ordinal].Kind == NativeKind.Null)
                {
                    anyOrigNull = true;
                }
            }
            // Updating a child FK column to or from Null is not recorded (only INSERT all-Null/partial-Null and
            // non-Null -> non-Null UPDATE are recorded: rel-orphan-insert, edit-fk-violation).
            if (isEdit && (anyOrigNull || nullCount > 0))
            {
                openDetail ??= $"updating a child foreign-key column to or from Null on relationship '{r.Name}' is not recorded";
                continue;
            }
            // ACE uses MATCH FULL on INSERT (r-def-cat-txn/cat-rel-compound-key-enforcement, r-cat-1b/rel-orphan-insert):
            // all-Null admits without parent lookup; partial-Null fails 3201.
            if (nullCount == fkCols.Length)
            {
                continue;
            }
            if (nullCount > 0)
            {
                AddDistinct(childFailures, r.Table);
                continue;
            }
            if (!AllLongValues(fkValues, out NativeKind badFkKind))
            {
                openDetail ??= $"relationship '{r.Name}' foreign-key value kind {badFkKind} is not LONG and is not recorded";
                continue;
            }

            TableEditContext? parentCtx = context.ResolveEditContext(r.Table);
            if (parentCtx is null)
            {
                openDetail ??= $"parent table '{r.Table}' of relationship '{r.Name}' could not be opened";
                continue;
            }
            TableDefinition parentTable = parentCtx.Semantics.Table;
            string[] refColNames = [.. r.Columns.Select(c => c.Referenced)];
            if (!TryFindIndex(parentTable, refColNames, preferredName: null, requirePrimaryKey: true, out LogicalIndex? parentIndex, out ColumnDescriptor[]? parentIndexCols))
            {
                openDetail ??= $"parent table '{r.Table}' has no matching primary-key index in relationship column order for '{r.Name}' (non-PK unique targets and permuted compound orders are not recorded)";
                continue;
            }
            if (!AllLongColumns(parentIndexCols!, out ColumnDescriptor? nonLongRef))
            {
                openDetail ??= $"relationship '{r.Name}' referenced column '{r.Table}.{nonLongRef!.Name}' (type {nonLongRef.TypeCode}) is not LONG and is not recorded";
                continue;
            }

            if (isSelfRef)
            {
                if (!TryResolveColumns(table, r.Columns, foreign: false, out ColumnDescriptor[]? selfParentCols))
                {
                    openDetail ??= $"self-referencing relationship '{r.Name}' references missing column on '{tableName}'";
                    continue;
                }
                bool matchesSelf = true;
                for (int i = 0; i < selfParentCols!.Length; i++)
                {
                    NativeValue parentVal = candidate[selfParentCols[i].Ordinal];
                    if (parentVal.Kind == NativeKind.Null || !NativeValue.SameStored(fkValues[i], parentVal))
                    {
                        matchesSelf = false;
                        break;
                    }
                }
                if (matchesSelf)
                {
                    openDetail ??= $"self-referencing row referencing itself on relationship '{r.Name}' is not recorded";
                    continue;
                }
            }

            if (!TryFindMatchingRows(parentTable, parentIndex!, parentIndexCols!, fkValues, workspace, isSelfRef ? self : null, stopAfterFirst: true, out List<RowId> matches, out bool hitSelfOnly, out string? probeFail))
            {
                openDetail ??= $"relationship '{r.Name}' parent lookup failed: {probeFail}";
                continue;
            }
            if (hitSelfOnly)
            {
                openDetail ??= $"self-referencing row referencing itself on relationship '{r.Name}' is not recorded";
                continue;
            }
            if (matches.Count == 0)
            {
                AddDistinct(childFailures, r.Table);
            }
        }

        // Part B: Parent-side checks on Edit when a referenced parent key column changes.
        if (isEdit)
        {
            foreach (Relationship r in asParent)
            {
                if (!CatalogRelationshipContext.IsAdmittedAttributes(r.Attributes))
                {
                    openDetail ??= $"relationship '{r.Name}' has unrecorded Attributes 0x{r.Attributes:X}";
                    continue;
                }
                if (CatalogRelationshipContext.IsDontEnforce(r.Attributes))
                {
                    continue;
                }
                bool isSelfRef = CatalogRelationshipContext.NamesEqual(r.Table, r.ForeignTable);
                if (isSelfRef)
                {
                    openDetail ??= $"UPDATE on self-referencing table '{tableName}' (relationship '{r.Name}') is not recorded";
                    continue;
                }
                if (!TryResolveColumns(table, r.Columns, foreign: false, out ColumnDescriptor[]? refCols))
                {
                    openDetail ??= $"relationship '{r.Name}' references missing primary column on '{tableName}'";
                    continue;
                }
                if (!AnyChanged(refCols!, candidate, originals))
                {
                    continue;
                }
                if (!AllLongColumns(refCols!, out ColumnDescriptor? nonLongRef))
                {
                    openDetail ??= $"relationship '{r.Name}' referenced column '{tableName}.{nonLongRef!.Name}' (type {nonLongRef.TypeCode}) is not LONG and is not recorded";
                    continue;
                }
                string[] refColNames = [.. r.Columns.Select(c => c.Referenced)];
                if (!TryFindIndex(table, refColNames, preferredName: null, requirePrimaryKey: true, out _, out _))
                {
                    openDetail ??= $"parent table '{tableName}' has no matching primary-key index in relationship column order for '{r.Name}' (non-PK unique targets and permuted compound orders are not recorded)";
                    continue;
                }

                var oldValues = new NativeValue[refCols!.Length];
                var newValues = new NativeValue[refCols.Length];
                bool anyOldNull = false;
                for (int i = 0; i < refCols.Length; i++)
                {
                    oldValues[i] = originals[refCols[i].Ordinal];
                    newValues[i] = candidate[refCols[i].Ordinal];
                    anyOldNull |= oldValues[i].Kind == NativeKind.Null;
                }
                if (anyOldNull)
                {
                    openDetail ??= $"updating a parent row with Null in referenced column of relationship '{r.Name}' is not recorded";
                    continue;
                }
                if (!AllLongValues(oldValues, out NativeKind badOldKind))
                {
                    openDetail ??= $"relationship '{r.Name}' referenced key value kind {badOldKind} is not LONG and is not recorded";
                    continue;
                }

                bool isUpdateCascade = (r.Attributes & CatalogRelationshipContext.DbRelationUpdateCascade) != 0;
                TableEditContext? childCtx = context.ResolveEditContext(r.ForeignTable);
                if (childCtx is null)
                {
                    openDetail ??= $"child table '{r.ForeignTable}' of relationship '{r.Name}' could not be opened";
                    continue;
                }
                TableDefinition childTable = childCtx.Semantics.Table;
                string[] fkColNames = [.. r.Columns.Select(c => c.Foreign)];
                if (!TryFindIndex(childTable, fkColNames, preferredName: r.Name, requirePrimaryKey: false, out LogicalIndex? childIndex, out ColumnDescriptor[]? childIndexCols))
                {
                    openDetail ??= $"child table '{r.ForeignTable}' has no matching index in relationship column order for '{r.Name}'";
                    continue;
                }
                if (!AllLongColumns(childIndexCols!, out ColumnDescriptor? nonLongFk))
                {
                    openDetail ??= $"relationship '{r.Name}' foreign-key column '{r.ForeignTable}.{nonLongFk!.Name}' (type {nonLongFk.TypeCode}) is not LONG and is not recorded";
                    continue;
                }
                if (!TryFindMatchingRows(childTable, childIndex!, childIndexCols!, oldValues, workspace, excludeSelf: null, stopAfterFirst: !isUpdateCascade, out List<RowId> childMatches, out _, out string? probeFail))
                {
                    openDetail ??= $"relationship '{r.Name}' child lookup failed: {probeFail}";
                    continue;
                }
                if (childMatches.Count == 0)
                {
                    continue;
                }

                // Parent key UPDATE with matching child under dbRelationUnique (Attributes = 1) is not recorded
                // (rel-parent-key-update-by-attributes only runs while Child6 is empty).
                if (r.Attributes == CatalogRelationshipContext.DbRelationUnique)
                {
                    openDetail ??= $"parent key UPDATE with matching child row under dbRelationUnique (Attributes = 1) on '{r.Name}' is not recorded";
                    continue;
                }
                // Updating a referenced parent key to Null is not recorded (even without update cascade).
                if (newValues.Any(v => v.Kind == NativeKind.Null))
                {
                    openDetail ??= $"updating referenced parent key to Null on relationship '{r.Name}' is not recorded";
                    continue;
                }

                if (!isUpdateCascade)
                {
                    AddDistinct(parentFailures, r.ForeignTable);
                    continue;
                }

                if (!TryPlanUpdateCascades(r, childCtx, childMatches, newValues, workspace, ref cascades, out string? cascadeOpen))
                {
                    openDetail ??= cascadeOpen;
                }
                else
                {
                    AddDistinct(cascadingTables, r.ForeignTable);
                }
            }
        }

        return CombineOutcome(childFailures, parentFailures, cascadingTables, openDetail, cascades);
    }

    public RelationshipOutcome CheckDelete(
        ReadOnlySpan<NativeValue> originals,
        RowId self,
        TransactionCoordinator workspace)
    {
        List<string> parentFailures = [];
        List<string> cascadingTables = [];
        List<CascadeStep>? cascades = null;
        string? openDetail = null;

        foreach (Relationship r in asParent)
        {
            if (!CatalogRelationshipContext.IsAdmittedAttributes(r.Attributes))
            {
                openDetail ??= $"relationship '{r.Name}' has unrecorded Attributes 0x{r.Attributes:X}";
                continue;
            }
            if (CatalogRelationshipContext.IsDontEnforce(r.Attributes))
            {
                continue;
            }
            if (!TryResolveColumns(table, r.Columns, foreign: false, out ColumnDescriptor[]? refCols))
            {
                openDetail ??= $"relationship '{r.Name}' references missing primary column on '{tableName}'";
                continue;
            }
            if (!AllLongColumns(refCols!, out ColumnDescriptor? nonLongRef))
            {
                openDetail ??= $"relationship '{r.Name}' referenced column '{tableName}.{nonLongRef!.Name}' (type {nonLongRef.TypeCode}) is not LONG and is not recorded";
                continue;
            }
            string[] refColNames = [.. r.Columns.Select(c => c.Referenced)];
            if (!TryFindIndex(table, refColNames, preferredName: null, requirePrimaryKey: true, out _, out _))
            {
                openDetail ??= $"parent table '{tableName}' has no matching primary-key index in relationship column order for '{r.Name}' (non-PK unique targets and permuted compound orders are not recorded)";
                continue;
            }

            var oldValues = new NativeValue[refCols!.Length];
            bool anyOldNull = false;
            for (int i = 0; i < refCols.Length; i++)
            {
                oldValues[i] = originals[refCols[i].Ordinal];
                anyOldNull |= oldValues[i].Kind == NativeKind.Null;
            }
            if (anyOldNull)
            {
                openDetail ??= $"deleting a parent row with Null in referenced column of relationship '{r.Name}' is not recorded";
                continue;
            }
            if (!AllLongValues(oldValues, out NativeKind badOldKind))
            {
                openDetail ??= $"relationship '{r.Name}' referenced key value kind {badOldKind} is not LONG and is not recorded";
                continue;
            }

            bool isSelfRef = CatalogRelationshipContext.NamesEqual(r.Table, r.ForeignTable);
            bool isDeleteCascade = (r.Attributes & CatalogRelationshipContext.DbRelationDeleteCascade) != 0;
            TableEditContext? childCtx = context.ResolveEditContext(r.ForeignTable);
            if (childCtx is null)
            {
                openDetail ??= $"child table '{r.ForeignTable}' of relationship '{r.Name}' could not be opened";
                continue;
            }
            TableDefinition childTable = childCtx.Semantics.Table;
            string[] fkColNames = [.. r.Columns.Select(c => c.Foreign)];
            if (!TryFindIndex(childTable, fkColNames, preferredName: r.Name, requirePrimaryKey: false, out LogicalIndex? childIndex, out ColumnDescriptor[]? childIndexCols))
            {
                openDetail ??= $"child table '{r.ForeignTable}' has no matching index in relationship column order for '{r.Name}'";
                continue;
            }
            if (!AllLongColumns(childIndexCols!, out ColumnDescriptor? nonLongFk))
            {
                openDetail ??= $"relationship '{r.Name}' foreign-key column '{r.ForeignTable}.{nonLongFk!.Name}' (type {nonLongFk.TypeCode}) is not LONG and is not recorded";
                continue;
            }
            if (!TryFindMatchingRows(childTable, childIndex!, childIndexCols!, oldValues, workspace, isSelfRef ? self : null, stopAfterFirst: !isDeleteCascade, out List<RowId> childMatches, out bool hitSelfOnly, out string? probeFail))
            {
                openDetail ??= $"relationship '{r.Name}' child lookup failed: {probeFail}";
                continue;
            }
            if (hitSelfOnly)
            {
                openDetail ??= $"deleting a self-referencing row referencing itself on '{r.Name}' is not recorded";
                continue;
            }
            if (childMatches.Count == 0)
            {
                continue;
            }

            // Parent DELETE with matching child under dbRelationUnique (Attributes = 1) is not recorded
            // (rel-parent-delete-by-attributes only runs while Child6 is empty).
            if (r.Attributes == CatalogRelationshipContext.DbRelationUnique)
            {
                openDetail ??= $"parent DELETE with matching child row under dbRelationUnique (Attributes = 1) on '{r.Name}' is not recorded";
                continue;
            }

            if (!isDeleteCascade)
            {
                AddDistinct(parentFailures, r.ForeignTable);
                continue;
            }
            if (!TryPlanDeleteCascades(r, isSelfRef, childCtx, childMatches, workspace, ref cascades, out string? cascadeOpen))
            {
                openDetail ??= cascadeOpen;
            }
            else
            {
                AddDistinct(cascadingTables, r.ForeignTable);
            }
        }

        return CombineOutcome([], parentFailures, cascadingTables, openDetail, cascades);
    }

    private bool TryPlanUpdateCascades(
        Relationship r,
        TableEditContext childCtx,
        List<RowId> childMatches,
        NativeValue[] newValues,
        TransactionCoordinator workspace,
        ref List<CascadeStep>? cascades,
        out string? openDetail)
    {
        openDetail = null;
        string childName = childCtx.Semantics.TableName;
        TableDefinition childTable = childCtx.Semantics.Table;
        if (!workspace.InStatement)
        {
            openDetail = $"update cascade on '{r.Name}' outside a SQL action statement (recordset Edit/Update) is not recorded";
            return false;
        }
        if (workspace.Depth > 0)
        {
            openDetail = $"update cascade on '{r.Name}' inside an explicit transaction (depth {workspace.Depth}) is not recorded";
            return false;
        }
        if (r.Columns.Count != 1)
        {
            openDetail = $"compound-key update cascade on '{r.Name}' is not recorded";
            return false;
        }
        if (context.HasEnforcedParentRelationships(childName))
        {
            openDetail = $"multi-level update cascade through child table '{childName}' on '{r.Name}' is not recorded";
            return false;
        }
        ColumnDescriptor? fkCol = childTable.Columns.FirstOrDefault(c => CatalogRelationshipContext.NamesEqual(c.Name, r.Columns[0].Foreign));
        if (fkCol is null)
        {
            openDetail = $"child column '{r.Columns[0].Foreign}' not found on '{childName}'";
            return false;
        }
        if (fkCol.IsAutoNumber)
        {
            openDetail = $"update cascade into AutoNumber column '{childName}.{fkCol.Name}' is not recorded";
            return false;
        }
        if (fkCol.TypeCode != NativeTypes.Long || newValues[0].Kind != NativeKind.Long)
        {
            openDetail = $"update cascade on non-LONG key '{childName}.{fkCol.Name}' (type {fkCol.TypeCode}, value {newValues[0].Kind}) is not recorded";
            return false;
        }
        if (context.HasOtherEnforcedChildRelationshipOnColumn(childName, r.Name, fkCol.Name))
        {
            openDetail = $"update cascade into shared foreign-key column '{childName}.{fkCol.Name}' is not recorded";
            return false;
        }
        if (childCtx.Semantics.TableRule is not null || childCtx.Semantics.Columns[fkCol.Ordinal].Rule is not null)
        {
            openDetail = $"update cascade into child table '{childName}' with validation rules is not recorded";
            return false;
        }
        if (childCtx.Semantics.Indexes.Any(ix => ix.Unique && ix.Columns.Any(c => c.Ordinal == fkCol.Ordinal)))
        {
            openDetail = $"update cascade into unique-indexed column '{childName}.{fkCol.Name}' is not recorded";
            return false;
        }

        PrivateOverlay? overlay = workspace.OverlayOf(context.Cache);
        var source = new PageSource(context.Cache, overlay);
        cascades ??= [];
        foreach (RowId childRow in childMatches)
        {
            if (workspace.HasAcceptedChange(childCtx.Semantics, childRow))
            {
                openDetail = $"update cascade on '{r.Name}' meets child row {childRow} already changed by this Workspace (chained-key or overlapping cascade is not recorded)";
                return false;
            }
            using RowView? resolved = RowResolver.Resolve(source, context.Format, childRow, out TraversalFailure? resolveFailure);
            if (resolved is not { } rowView)
            {
                openDetail = $"child row {childRow} on '{childName}' could not be read: {resolveFailure?.Predicate ?? "deleted"}";
                return false;
            }
            var childValues = new NativeValue[childTable.Columns.Count];
            for (int i = 0; i < childValues.Length; i++)
            {
                childValues[i] = FieldDecoder.Read(rowView, childTable, childTable.Columns[i], source, context.Format);
                if (childValues[i].Kind == NativeKind.Failed)
                {
                    openDetail = $"child row {childRow} column '{childTable.Columns[i].Name}' could not be read: {childValues[i].Failure}";
                    return false;
                }
            }
            childValues[fkCol.Ordinal] = newValues[0];
            cascades.Add(new CascadeStep(childCtx, childRow, RowChangeKind.Modify, childValues));
        }
        return true;
    }

    private bool TryPlanDeleteCascades(
        Relationship r,
        bool isSelfRef,
        TableEditContext childCtx,
        List<RowId> childMatches,
        TransactionCoordinator workspace,
        ref List<CascadeStep>? cascades,
        out string? openDetail)
    {
        openDetail = null;
        string childName = childCtx.Semantics.TableName;
        if (!workspace.InStatement)
        {
            openDetail = $"delete cascade on '{r.Name}' outside a SQL action statement (recordset Delete) is not recorded";
            return false;
        }
        if (workspace.Depth > 1)
        {
            openDetail = $"delete cascade on '{r.Name}' inside a nested explicit transaction (depth {workspace.Depth}) is not recorded";
            return false;
        }
        if (isSelfRef)
        {
            openDetail = $"self-referencing delete cascade on '{r.Name}' is not recorded";
            return false;
        }
        if (r.Columns.Count != 1)
        {
            openDetail = $"compound-key delete cascade on '{r.Name}' is not recorded";
            return false;
        }
        if (context.HasEnforcedParentRelationships(childName))
        {
            openDetail = $"multi-level delete cascade through child table '{childName}' on '{r.Name}' is not recorded";
            return false;
        }
        cascades ??= [];
        foreach (RowId childRow in childMatches)
        {
            if (workspace.HasAcceptedChange(childCtx.Semantics, childRow))
            {
                openDetail = $"delete cascade on '{r.Name}' meets child row {childRow} already changed by this Workspace";
                return false;
            }
            cascades.Add(new CascadeStep(childCtx, childRow, RowChangeKind.Delete, null));
        }
        return true;
    }

    /// <summary>
    /// Probe <paramref name="logicalIndex"/> on <paramref name="targetTable"/> through the one ISAM <see cref="TableCursor.Seek"/>
    /// path (the same key encoder as <see cref="StoredUniqueProbe"/>), reading through the Workspace's private overlay.
    /// </summary>
    private bool TryFindMatchingRows(
        TableDefinition targetTable,
        LogicalIndex logicalIndex,
        ColumnDescriptor[] indexCols,
        ReadOnlySpan<NativeValue> keyValues,
        TransactionCoordinator workspace,
        RowId? excludeSelf,
        bool stopAfterFirst,
        out List<RowId> matches,
        out bool hitSelfOnly,
        out string? failure)
    {
        matches = [];
        hitSelfOnly = false;
        failure = null;

        var keys = new SeekKey[keyValues.Length];
        for (int i = 0; i < keyValues.Length; i++)
        {
            if (EntryKeys.ValueOf(keyValues[i]) is not { } kv)
            {
                failure = $"key value kind {keyValues[i].Kind} cannot be encoded";
                return false;
            }
            keys[i] = new SeekKey(kv);
        }

        PrivateOverlay? overlay = workspace.OverlayOf(context.Cache);
        var source = new PageSource(context.Cache, overlay);
        ulong extent = PageAllocator.ExtentOf(context.Cache, context.PageCount, overlay);
        using TableCursor? cursor = TableCursor.Open(source, context.Format, targetTable, extent, 0, out TraversalFailure? openFailure);
        if (cursor is null)
        {
            failure = openFailure?.Predicate ?? "cursor could not be opened";
            return false;
        }
        IndexRefusal setIdx = cursor.SetIndex(logicalIndex.Name);
        if (setIdx != IndexRefusal.None)
        {
            failure = $"SetIndex '{logicalIndex.Name}' refused ({setIdx})";
            return false;
        }
        IndexRefusal seek = cursor.Seek(SeekOperator.Equal, keys, out bool found);
        if (seek != IndexRefusal.None)
        {
            failure = $"Seek on '{logicalIndex.Name}' refused ({seek})";
            return false;
        }
        if (!found)
        {
            return true;
        }

        bool hitSelf = false;
        bool first = true;
        while (!cursor.Eof)
        {
            if (!first)
            {
                bool same = true;
                for (int i = 0; i < indexCols.Length; i++)
                {
                    FieldResult read = cursor.Read(indexCols[i]);
                    if (!read.Succeeded)
                    {
                        failure = $"column '{indexCols[i].Name}' could not be read during index walk: {read.Detail}";
                        return false;
                    }
                    if (!NativeValue.SameStored(read.Value, keyValues[i]))
                    {
                        same = false;
                        break;
                    }
                }
                if (!same)
                {
                    break;
                }
            }
            first = false;

            if (cursor.CurrentRow is not { } row)
            {
                failure = "cursor had no current row after Seek";
                return false;
            }
            if (excludeSelf is { } selfRow && row == selfRow)
            {
                hitSelf = true;
            }
            else
            {
                matches.Add(row);
                if (stopAfterFirst)
                {
                    break;
                }
            }

            MoveOutcome moved = cursor.MoveNext();
            if (moved is not MoveOutcome.Moved and not MoveOutcome.ReachedBoundary)
            {
                failure = cursor.LastTraversalFailure?.Predicate ?? $"MoveNext failed ({moved})";
                return false;
            }
        }

        hitSelfOnly = matches.Count == 0 && hitSelf;
        return true;
    }

    private bool TryFindIndex(
        TableDefinition targetTable,
        IReadOnlyList<string> colNames,
        string? preferredName,
        bool requirePrimaryKey,
        out LogicalIndex? matched,
        out ColumnDescriptor[]? indexCols)
    {
        matched = null;
        indexCols = null;
        IndexDescriptorRules rules = context.Format.IndexDescriptor;

        if (!requirePrimaryKey
            && preferredName is not null
            && targetTable.FindIndex(preferredName) is { } prefLogical
            && targetTable.PhysicalOf(prefLogical) is { } prefPhysical
            && TryMatchColumnsExact(targetTable, prefPhysical, colNames, out indexCols))
        {
            matched = prefLogical;
            return true;
        }

        foreach (LogicalIndex logical in targetTable.LogicalIndexes)
        {
            if (requirePrimaryKey && logical.IndexType != rules.PrimaryKeyType)
            {
                continue;
            }
            if (targetTable.PhysicalOf(logical) is not { } physical)
            {
                continue;
            }
            if (TryMatchColumnsExact(targetTable, physical, colNames, out indexCols))
            {
                matched = logical;
                return true;
            }
        }
        return false;
    }

    private static bool TryMatchColumnsExact(
        TableDefinition targetTable,
        PhysicalIndex physical,
        IReadOnlyList<string> colNames,
        out ColumnDescriptor[]? indexCols)
    {
        indexCols = null;
        if (physical.Columns.Count != colNames.Count)
        {
            return false;
        }
        var cols = new ColumnDescriptor[colNames.Count];
        for (int i = 0; i < physical.Columns.Count; i++)
        {
            ColumnDescriptor? col = targetTable.Columns.FirstOrDefault(c => c.PhysicalNumber == physical.Columns[i].PhysicalNumber);
            if (col is null || !CatalogRelationshipContext.NamesEqual(col.Name, colNames[i]))
            {
                return false;
            }
            cols[i] = col;
        }
        indexCols = cols;
        return true;
    }

    private static bool TryResolveColumns(TableDefinition tbl, IReadOnlyList<RelationshipColumn> cols, bool foreign, out ColumnDescriptor[]? resolved)
    {
        var list = new ColumnDescriptor[cols.Count];
        for (int i = 0; i < cols.Count; i++)
        {
            string colName = foreign ? cols[i].Foreign : cols[i].Referenced;
            ColumnDescriptor? col = tbl.Columns.FirstOrDefault(c => CatalogRelationshipContext.NamesEqual(c.Name, colName));
            if (col is null)
            {
                resolved = null;
                return false;
            }
            list[i] = col;
        }
        resolved = list;
        return true;
    }

    private static bool AllLongColumns(ColumnDescriptor[] cols, out ColumnDescriptor? nonLong)
    {
        for (int i = 0; i < cols.Length; i++)
        {
            if (cols[i].TypeCode != NativeTypes.Long)
            {
                nonLong = cols[i];
                return false;
            }
        }
        nonLong = null;
        return true;
    }

    private static bool AllLongValues(ReadOnlySpan<NativeValue> values, out NativeKind badKind)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i].Kind != NativeKind.Long)
            {
                badKind = values[i].Kind;
                return false;
            }
        }
        badKind = NativeKind.Null;
        return true;
    }

    private static bool AnyChanged(ColumnDescriptor[] cols, ReadOnlySpan<NativeValue> candidate, ReadOnlySpan<NativeValue> originals)
    {
        for (int i = 0; i < cols.Length; i++)
        {
            int ord = cols[i].Ordinal;
            if (!NativeValue.SameStored(candidate[ord], originals[ord]))
            {
                return true;
            }
        }
        return false;
    }

    private static void AddDistinct(List<string> list, string name)
    {
        if (!list.Any(existing => CatalogRelationshipContext.NamesEqual(existing, name)))
        {
            list.Add(name);
        }
    }

    private static RelationshipOutcome CombineOutcome(
        List<string> childFailures,
        List<string> parentFailures,
        List<string> cascadingTables,
        string? openDetail,
        List<CascadeStep>? cascades)
    {
        if (childFailures.Count > 0 && parentFailures.Count > 0)
        {
            return RelationshipOutcome.AmbiguousOrder($"both child (3201: {string.Join(", ", childFailures)}) and parent (3200: {string.Join(", ", parentFailures)}) referential checks failed on the same row");
        }
        if (childFailures.Count > 1)
        {
            return RelationshipOutcome.AmbiguousOrder($"multiple enforced relationships failed 3201 ({string.Join(", ", childFailures)}): reporting order is open (qualified.md CAT-D Open)");
        }
        if (parentFailures.Count > 1)
        {
            return RelationshipOutcome.AmbiguousOrder($"multiple enforced relationships failed 3200 ({string.Join(", ", parentFailures)}): reporting order is open");
        }
        if (parentFailures.Count > 0 && cascadingTables.Count > 0)
        {
            return RelationshipOutcome.NotModelled($"a parent mutation with both a blocking child ({string.Join(", ", parentFailures)}) and a cascading child ({string.Join(", ", cascadingTables)}) on the same row is not recorded");
        }
        if (cascadingTables.Count > 1)
        {
            return RelationshipOutcome.NotModelled($"a single parent mutation cascading into multiple child tables ({string.Join(", ", cascadingTables)}) is not recorded");
        }
        if (openDetail is not null)
        {
            return RelationshipOutcome.NotModelled(openDetail);
        }
        if (childFailures.Count == 1)
        {
            return RelationshipOutcome.MissingParent(childFailures[0]);
        }
        if (parentFailures.Count == 1)
        {
            return RelationshipOutcome.HasChildren(parentFailures[0]);
        }
        return RelationshipOutcome.Admit(cascades is { Count: > 0 } ? cascades : null);
    }
}
