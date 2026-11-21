using CalcDb.Isam.Transactions;
using CalcDb.Values;

namespace CalcDb.Isam.Edit;

/// <summary>The outcome of a referential integrity check before or during an Update, Delete, or DeleteEveryRow.</summary>
public enum RelationshipVerdict : byte
{
    /// <summary>All applicable enforced relationships are satisfied (with zero or more recorded cascades to execute).</summary>
    Admit,

    /// <summary>A child row lacks a matching parent row (3201 naming <see cref="RelationshipOutcome.TableName"/>).</summary>
    MissingParent,

    /// <summary>A parent row has matching child rows in a non-cascading relationship (3200 naming <see cref="RelationshipOutcome.TableName"/>).</summary>
    HasChildren,

    /// <summary>Two enforced relationship failures whose reporting order is not established by the records (qualified.md CAT-D Open).</summary>
    CheckOrder,

    /// <summary>An unrecorded relationship case (unknown grbit bit, self-row referencing itself, unrecorded cascade shape, unencodable key, etc.).</summary>
    NotModelled,
}

/// <summary>
/// One child-row change planned by a recorded cascading UPDATE (dbRelationUpdateCascade 256 / 4352) or DELETE
/// (dbRelationDeleteCascade 4096 / 4352) inside a SQL action statement (<c>r-cat-1b/rel-parent-delete-by-attributes</c>,
/// <c>rel-parent-key-update-by-attributes</c>, <c>r-fix-expr-sql/fk-actions-dao-relations</c>,
/// <c>r-sql-2-3/act-cascade</c>, <c>r-def-cat-txn/txn-cascade-delete-rollback-and-commit</c>).
/// Carries the session's shared <see cref="TableEditContext"/> for the child table so cascaded changes share its
/// <see cref="MutationSemantics"/> and <see cref="SameWidthWriter"/>.
/// </summary>
public sealed record CascadeStep(
    TableEditContext TargetContext,
    RowId TargetRow,
    RowChangeKind Kind,
    NativeValue[]? NewValues);

/// <summary>The verdict of <see cref="IRelationshipProbe"/>, with the table named by 3200/3201, detail, or planned cascades.</summary>
public readonly record struct RelationshipOutcome(
    RelationshipVerdict Verdict,
    string? TableName = null,
    string? Detail = null,
    IReadOnlyList<CascadeStep>? Cascades = null)
{
    public static RelationshipOutcome Admit(IReadOnlyList<CascadeStep>? cascades = null) =>
        new(RelationshipVerdict.Admit, Cascades: cascades);

    public static RelationshipOutcome MissingParent(string parentTable) =>
        new(RelationshipVerdict.MissingParent, TableName: parentTable);

    public static RelationshipOutcome HasChildren(string childTable) =>
        new(RelationshipVerdict.HasChildren, TableName: childTable);

    public static RelationshipOutcome AmbiguousOrder(string detail) =>
        new(RelationshipVerdict.CheckOrder, Detail: detail);

    public static RelationshipOutcome NotModelled(string detail) =>
        new(RelationshipVerdict.NotModelled, Detail: detail);
}

/// <summary>
/// Answers referential integrity checks (3200/3201 and recorded cascades) for one table of an open file, reading related
/// tables through the Workspace's private overlay so uncommitted transaction and statement changes are visible.
/// Implemented above the ISAM by <c>CalcDb.Catalog</c> over the already-opened <c>CatalogSnapshot</c> and
/// <c>MSysRelationships</c> list.
/// </summary>
public interface IRelationshipProbe
{
    /// <summary>Whether this table is the primary (parent) side of at least one enforced relationship.</summary>
    bool HasEnforcedParentRelations { get; }

    /// <summary>
    /// Validate that every parent-side relationship of this table carries admitted <c>Attributes</c> bits (even when the
    /// table has zero rows), returning the refusal detail or null when all are admitted.
    /// </summary>
    string? CheckDeleteAttributes();

    /// <summary>
    /// Check an <c>AddNew</c> or <c>Edit</c> candidate row against all relationships naming this table (child-side 3201
    /// and, on <c>Edit</c> when a referenced parent key changes, parent-side 3200 or recorded update cascade).
    /// </summary>
    RelationshipOutcome CheckUpdate(
        ReadOnlySpan<NativeValue> candidate,
        ReadOnlySpan<NativeValue> originals,
        bool isEdit,
        RowId? self,
        TransactionCoordinator workspace);

    /// <summary>
    /// Check deleting stored row <paramref name="self"/> with values <paramref name="originals"/> against all
    /// relationships where this table is the parent (3200 or recorded delete cascade).
    /// </summary>
    RelationshipOutcome CheckDelete(
        ReadOnlySpan<NativeValue> originals,
        RowId self,
        TransactionCoordinator workspace);
}
