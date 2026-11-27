using System.Runtime.InteropServices;

using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// ISAM tests for:
/// <list type="bullet">
/// <item><c>evidence/r-q50-jk44-ddl-vis/ddl-multi-constraint-table-accdb</c> &amp;
/// <c>evidence/r-q50-jk44-ddl-vis/ddl-multi-constraint-table-mdb</c>: compound <c>AND</c> CHECK constraint parsing
/// (<c>Score &gt;= 0 AND Score &lt;= 100</c>).</item>
/// <item><c>evidence/r-q54-excl-ddl-ix-cmd/second-32page-extent-accdb</c> &amp;
/// <c>evidence/r-q54-excl-ddl-ix-cmd/second-32page-extent-mdb</c>: crossing the 32-page page-1 reservation boundary in
/// one session (<see cref="PageAllocator"/> and <see cref="TransactionCoordinator"/>) and releasing all unused reserved
/// pages across blocks at Close.</item>
/// <item><c>evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-accdb</c> &amp;
/// <c>evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-mdb</c>: DAO scalar conversions, refusals, and default
/// non-DAO preservation in <see cref="AssignmentConversion"/>.</item>
/// </list>
/// </summary>
public sealed unsafe class CheckConstraintAndExtentTests
{
    private const string EmptyAccdb = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
    private const string EmptyMdb = "99da59149c57559b5658a9a08625236a936808d2be0ead100da977bd3716bdb4";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    [Fact]
    public void CheckConstraintExpression_admits_same_column_long_compound_AND_and_refuses_neighbours()
    {
        // evidence/r-q50-jk44-ddl-vis/ddl-multi-constraint-table-accdb, ddl-multi-constraint-table-mdb
        Assert.False(CheckConstraintExpression.TryParse("Score >= 0 AND Score <= 100", out _, out _));

        Assert.True(CheckConstraintExpression.TryParse("Score >= 0 AND Score <= 100", out CheckConstraintExpression? parsed, out string? reason, allowCompoundAnd: true), reason);
        Assert.NotNull(parsed);
        Assert.Equal("Score >= 0 AND Score <= 100", parsed!.Expression);
        Assert.Equal("Score", parsed.ColumnName);
        Assert.Equal(CheckComparisonOp.Ge, parsed.Op);
        Assert.Equal(0, parsed.LongValue);
        Assert.NotNull(parsed.AndRight);
        Assert.Equal("Score", parsed.AndRight!.ColumnName);
        Assert.Equal(CheckComparisonOp.Le, parsed.AndRight.Op);
        Assert.Equal(100, parsed.AndRight.LongValue);

        // Case-insensitive column match and lowercase 'and'
        Assert.True(CheckConstraintExpression.TryParse("score >= -10 and SCORE <= 200", out CheckConstraintExpression? mixed, out reason, allowCompoundAnd: true), reason);
        Assert.NotNull(mixed!.AndRight);

        // Refused neighbours: cross-column, 3-way AND, text AND, OR
        Assert.False(CheckConstraintExpression.TryParse("A >= 0 AND B <= 100", out _, out _, allowCompoundAnd: true));
        Assert.False(CheckConstraintExpression.TryParse("Score >= 0 AND Score <= 100 AND Score <> 50", out _, out _, allowCompoundAnd: true));
        Assert.False(CheckConstraintExpression.TryParse("Code <> 'a' AND Code <> 'b'", out _, out _, allowCompoundAnd: true));
        Assert.False(CheckConstraintExpression.TryParse("Score < 0 OR Score > 100", out _, out _, allowCompoundAnd: true));
    }

    [Fact]
    public void Second_32page_extent_clears_second_block_on_page_1_and_releases_unused_tail_at_close_accdb()
    {
        // evidence/r-q54-excl-ddl-ix-cmd/second-32page-extent-accdb:
        // Empty ACCDB has 42 pages (0..41); page 1 offset 4037 = fc ff ff ff ...
        // Block 1 reserves pages 42..71 (30 pages). Consuming all 30 (D01..D10) leaves 0 reserved;
        // D11 allocates 72..74 and clears block 2 (72..103); D12 + row consume 75..78;
        // Close releases 79..103 -> page 1 offset 4037..4041 = 00 00 00 00 80.
        using var file = new MemoryDatabaseFile([.. NativeFixtures.Read(EmptyAccdb)], D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 256);
        cache.NoteExtent(file.PageCount);
        var target = new SingleFileTarget(cache, file);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = target, Clock = static () => TimeSpan.Zero };

        byte[] p1Before = file.Snapshot()[(D.PageSize)..(2 * D.PageSize)];
        Assert.Equal([0xFC, 0xFF, 0xFF, 0xFF, 0xFF], p1Before[4037..4042]);

        var dummySemantics = DummySemantics(cache, file.PageCount);

        // Statement 1: allocate 42..44, reserve 45..71 (block 1)
        AcceptOutcome s1 = PublishAllocatedPages(ws, cache, dummySemantics, pagesToAllocate: 3, reserveExtent: true);
        Assert.True(s1.Accepted && s1.Error is null && s1.Open == MutationOpen.None, s1.Detail);
        Assert.Equal(27, ws.SessionReservedPages(cache).Count);
        byte[] p1AfterS1 = file.Snapshot()[(D.PageSize)..(2 * D.PageSize)];
        Assert.Equal([0x00, 0x00, 0x00, 0x00, 0xFF], p1AfterS1[4037..4042]);

        // Statements 2..10: consume remaining 27 pages (45..71) without editing page 1
        for (int i = 0; i < 9; i++)
        {
            AcceptOutcome si = PublishAllocatedPages(ws, cache, dummySemantics, pagesToAllocate: 3, reserveExtent: false);
            Assert.True(si.Accepted && si.Error is null && si.Open == MutationOpen.None, si.Detail);
        }
        Assert.Empty(ws.SessionReservedPages(cache));
        Assert.True(ws.HasReusableExtentSession(cache));

        // Unreserved single-page global-map allocation after block 1 exhaustion refuses by name
        AcceptOutcome unreserved = PublishAllocatedPages(ws, cache, dummySemantics, pagesToAllocate: 1, reserveExtent: false);
        Assert.False(unreserved.Accepted);
        Assert.Equal(MutationOpen.Publication, unreserved.Open);

        // Statement 11 (D11): crosses page 32 -> allocates 72..74 and clears 2nd 32-page block (72..103)
        AcceptOutcome s11 = PublishAllocatedPages(ws, cache, dummySemantics, pagesToAllocate: 3, reserveExtent: true);
        Assert.True(s11.Accepted && s11.Error is null && s11.Open == MutationOpen.None, s11.Detail);
        Assert.Equal(29, ws.SessionReservedPages(cache).Count);
        byte[] p1AfterS11 = file.Snapshot()[(D.PageSize)..(2 * D.PageSize)];
        Assert.Equal([0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], p1AfterS11[4037..4045]);

        // Statement 12 (D12 = 3 pages: 75..77) + Statement 13 (row = 1 page: 78)
        AcceptOutcome s12 = PublishAllocatedPages(ws, cache, dummySemantics, pagesToAllocate: 3, reserveExtent: false);
        Assert.True(s12.Accepted && s12.Error is null && s12.Open == MutationOpen.None, s12.Detail);
        AcceptOutcome s13 = PublishAllocatedPages(ws, cache, dummySemantics, pagesToAllocate: 1, reserveExtent: false);
        Assert.True(s13.Accepted && s13.Error is null && s13.Open == MutationOpen.None, s13.Detail);
        Assert.Equal(25, ws.SessionReservedPages(cache).Count);

        // Close releases 79..103 -> page 1 offset 4037..4044 = 00 00 00 00 80 ff ff ff
        (MutationOpen closeOpen, string? closeDetail) = ws.PublishCloseCommit(cache);
        Assert.Equal(MutationOpen.None, closeOpen);
        Assert.Null(closeDetail);
        byte[] p1AfterClose = file.Snapshot()[(D.PageSize)..(2 * D.PageSize)];
        Assert.Equal([0x00, 0x00, 0x00, 0x00, 0x80, 0xFF, 0xFF, 0xFF], p1AfterClose[4037..4045]);
    }

    [Fact]
    public void Second_32page_extent_crosses_mid_statement_on_mdb_and_releases_unused_tail_at_close()
    {
        // evidence/r-q54-excl-ddl-ix-cmd/second-32page-extent-mdb:
        // Empty MDB has 19 pages (0..18); page 1 offset 4034 = f8 ff ff ff ...
        // Block 1 reserves 19..47 (29 pages). After 9 * 3 = 27 pages, 2 pages (46, 47) remain.
        // D10 needs 3 pages: consumes 46, 47 from block 1, then calls TryAllocateNextExtent for 48 -> clears 48..79!
        using var file = new MemoryDatabaseFile([.. NativeFixtures.Read(EmptyMdb)], D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 256);
        cache.NoteExtent(file.PageCount);
        var target = new SingleFileTarget(cache, file);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = target, Clock = static () => TimeSpan.Zero };

        var dummySemantics = DummySemantics(cache, file.PageCount);

        // D01: allocates 19..21, reserves 22..47 (26 pages)
        AcceptOutcome s1 = PublishAllocatedPages(ws, cache, dummySemantics, pagesToAllocate: 3, reserveExtent: true);
        Assert.True(s1.Accepted && s1.Error is null && s1.Open == MutationOpen.None, s1.Detail);
        Assert.Equal(26, ws.SessionReservedPages(cache).Count);

        // D02..D09: 8 * 3 = 24 pages (22..45), leaving 2 pages (46, 47)
        for (int i = 0; i < 8; i++)
        {
            AcceptOutcome si = PublishAllocatedPages(ws, cache, dummySemantics, pagesToAllocate: 3, reserveExtent: false);
            Assert.True(si.Accepted && si.Error is null && si.Open == MutationOpen.None, si.Detail);
        }
        Assert.Equal([46u, 47u], ws.SessionReservedPages(cache));

        // D10: consumes 46, 47 from block 1, crosses mid-statement to allocate 48 and reserve 49..79 (31 pages)!
        AcceptOutcome s10 = PublishAllocatedPages(ws, cache, dummySemantics, pagesToAllocate: 3, reserveExtent: false);
        Assert.True(s10.Accepted && s10.Error is null && s10.Open == MutationOpen.None, s10.Detail);
        Assert.Equal(31, ws.SessionReservedPages(cache).Count);
        byte[] p1AfterD10 = file.Snapshot()[(D.PageSize)..(2 * D.PageSize)];
        Assert.Equal([0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00], p1AfterD10[4034..4042]);

        // D11 (49..51), D12 (52..54), row (55) -> 24 reserved pages remain (56..79)
        Assert.True(PublishAllocatedPages(ws, cache, dummySemantics, 3, false).Accepted);
        Assert.True(PublishAllocatedPages(ws, cache, dummySemantics, 3, false).Accepted);
        Assert.True(PublishAllocatedPages(ws, cache, dummySemantics, 1, false).Accepted);
        Assert.Equal(24, ws.SessionReservedPages(cache).Count);

        // Close releases 56..79 -> bytes 4034..4038 = 00 00 00 00 00 (16..55 used), 4039..4041 = ff ff ff (56..79 free)
        (MutationOpen closeOpen, string? closeDetail) = ws.PublishCloseCommit(cache);
        Assert.Equal(MutationOpen.None, closeOpen);
        Assert.Null(closeDetail);
        byte[] p1AfterClose = file.Snapshot()[(D.PageSize)..(2 * D.PageSize)];
        Assert.Equal([0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF], p1AfterClose[4034..4042]);
    }

    [Fact]
    public void AssignmentConversion_dao_mode_maps_empty_null_bool_decimal_guid_and_missing_while_preserving_default()
    {
        // evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-accdb, conv-dao-scalars-mdb
        ColumnDescriptor Col(byte typeCode, ushort length = 0, byte prec = 0, byte scale = 0, bool isFixed = true) => new()
        {
            Name = "C", TypeCode = typeCode, PhysicalNumber = 1, VariableIndex = 0, ColumnId = 1,
            TypeInfo = 0, Flags = isFixed ? (byte)0x01 : (byte)0x00, ExtendedFlags = 0, FixedOffset = 0,
            Length = length, IsFixed = isFixed, NumericPrecision = prec, NumericScale = scale,
        };

        var colBool = Col(NativeTypes.Boolean, 1);
        var colLong = Col(NativeTypes.Long, 4);
        var colDec = Col(NativeTypes.Numeric, 17, prec: 18, scale: 4);
        var colText = Col(NativeTypes.Text, 80, isFixed: false);
        var colGuid = Col(NativeTypes.Guid, 16);

        // VT_EMPTY -> False on Yes/No, Null on Long/Text/Decimal/GUID (DAO mode only)
        var empty = new NativeVariant { Vt = (ushort)VarType.Empty };
        Assert.Equal(MutationOpen.ConversionNotRecorded, AssignmentConversion.Convert(empty, colLong, 7177).Open);
        ConversionResult emptyBool = AssignmentConversion.Convert(empty, colBool, 7177, allowDaoConversions: true);
        Assert.True(emptyBool.Succeeded);
        Assert.Equal(NativeKind.Boolean, emptyBool.Value.Kind);
        Assert.False(emptyBool.Value.Boolean);
        Assert.Equal(NativeKind.Null, AssignmentConversion.Convert(empty, colLong, 7177, allowDaoConversions: true).Value.Kind);
        Assert.Equal(NativeKind.Null, AssignmentConversion.Convert(empty, colDec, 7177, allowDaoConversions: true).Value.Kind);
        Assert.Equal(NativeKind.Null, AssignmentConversion.Convert(empty, colText, 7177, allowDaoConversions: true).Value.Kind);
        Assert.Equal(NativeKind.Null, AssignmentConversion.Convert(empty, colGuid, 7177, allowDaoConversions: true).Value.Kind);

        // VT_NULL -> False on Yes/No (DAO mode only)
        var nullVar = new NativeVariant { Vt = (ushort)VarType.Null };
        Assert.Equal(MutationOpen.ConversionNotRecorded, AssignmentConversion.Convert(nullVar, colBool, 7177).Open);
        ConversionResult nullBool = AssignmentConversion.Convert(nullVar, colBool, 7177, allowDaoConversions: true);
        Assert.True(nullBool.Succeeded);
        Assert.False(nullBool.Value.Boolean);

        // Missing (VT_ERROR 0x80020004) -> MutationError.MissingArgument
        var missing = new NativeVariant { Vt = (ushort)VarType.Error, Int64 = unchecked((int)0x80020004) };
        Assert.Equal(MutationError.MissingArgument, AssignmentConversion.Convert(missing, colLong, 7177, allowDaoConversions: true).Error?.Number);

        // Bool -> Text formats as "-1" / "0"
        var boolTrue = new NativeVariant { Vt = (ushort)VarType.Bool, Int64 = -1 };
        var boolFalse = new NativeVariant { Vt = (ushort)VarType.Bool, Int64 = 0 };
        Assert.Equal("-1", AssignmentConversion.Convert(boolTrue, colText, 7177, allowDaoConversions: true).Value.Text);
        Assert.Equal("0", AssignmentConversion.Convert(boolFalse, colText, 7177, allowDaoConversions: true).Value.Text);

        // GUID <- I4 fails 3421 in DAO mode
        var i4 = new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 1 };
        Assert.Equal(MutationError.Conversion, AssignmentConversion.Convert(i4, colGuid, 7177, allowDaoConversions: true).Error?.Number);

        // DECIMAL(18,4): valid BSTR succeeds; over-scale fails 3759; over-precision fails 3761; bad text fails 3421;
        // unprobed Currency -> DECIMAL stays ColumnType; unprobed UI1/I2 -> Text stays ConversionNotRecorded.
        using OwnedVariant decOk = OwnedVariant.FromText("12.3456");
        using OwnedVariant decScale = OwnedVariant.FromText("12.34565");
        using OwnedVariant decPrec = OwnedVariant.FromText("999999999999999.0");
        using OwnedVariant decBad = OwnedVariant.FromText("abc");
        Assert.True(AssignmentConversion.Convert(*decOk.Pointer, colDec, 7177, allowDaoConversions: true).Succeeded);
        Assert.Equal(MutationError.DecimalScaleTruncation, AssignmentConversion.Convert(*decScale.Pointer, colDec, 7177, allowDaoConversions: true).Error?.Number);
        Assert.Equal(MutationError.DecimalPrecisionTooSmall, AssignmentConversion.Convert(*decPrec.Pointer, colDec, 7177, allowDaoConversions: true).Error?.Number);
        Assert.Equal(MutationError.Conversion, AssignmentConversion.Convert(*decBad.Pointer, colDec, 7177, allowDaoConversions: true).Error?.Number);
        var cyVar = new NativeVariant { Vt = (ushort)VarType.Currency, Int64 = 10000 };
        Assert.Equal(MutationOpen.ColumnType, AssignmentConversion.Convert(cyVar, colDec, 7177, allowDaoConversions: true).Open);
        var ui1Var = new NativeVariant { Vt = (ushort)VarType.UI1, Int64 = 7 };
        Assert.Equal(MutationOpen.ConversionNotRecorded, AssignmentConversion.Convert(ui1Var, colText, 7177, allowDaoConversions: true).Open);
    }

    private static MutationSemantics DummySemantics(PageCache cache, ulong pageCount)
    {
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, pageCount, out CatalogFailure? failure)!;
        Assert.Null(failure);
        return MutationSemantics.Compile("MSysObjects", catalog.ObjectTable, null, EmptyRuleBinder.Instance, D.IndexDescriptor);
    }

    private sealed class EmptyRuleBinder : IMutationRuleBinder
    {
        public static EmptyRuleBinder Instance { get; } = new();
        public IFieldDefault? BindDefault(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted) { notAdmitted = null; return null; }
        public IMutationRule? BindFieldRule(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted) { notAdmitted = null; return null; }
        public IMutationRule? BindTableRule(TableDefinition table, string text, out string? notAdmitted) { notAdmitted = null; return null; }
    }

    private static AcceptOutcome PublishAllocatedPages(
        TransactionCoordinator ws,
        PageCache cache,
        MutationSemantics semantics,
        int pagesToAllocate,
        bool reserveExtent)
    {
        IReadOnlyList<uint> sessionReserved = ws.SessionReservedPages(cache);
        return ws.AcceptSchemaChange(cache, semantics, p =>
        {
            var allocator = new PageAllocator(cache, D, cache.KnownExtentPages, sessionReserved);
            List<PageEdit> edits = [];
            List<LockResource> footprint = [];
            uint firstFreeBefore = 0;
            byte[] globalMapBefore = [];
            bool captureForExtent = reserveExtent && !allocator.HasSessionReservedPages;
            if (captureForExtent && !allocator.TryCaptureGlobalFreeMap(p.Overlay, out firstFreeBefore, out globalMapBefore, out string? capFail))
            {
                return new CompiledChange(null, capFail);
            }
            for (int i = 0; i < pagesToAllocate; i++)
            {
                uint allocatedPage;
                if (allocator.HasSessionReservedPages)
                {
                    if (!allocator.TryAllocateFromReserved(p.Overlay, out allocatedPage, out string? fail))
                    {
                        return new CompiledChange(null, fail);
                    }
                }
                else if (allocator.HadSessionReservedPages)
                {
                    List<PageEdit> extentEdits = [];
                    if (!allocator.TryAllocateNextExtent(p.Overlay, extentEdits, out allocatedPage, out string? fail))
                    {
                        return new CompiledChange(null, fail);
                    }
                    foreach (PageEdit ge in extentEdits)
                    {
                        p.Edit(ge.Page, ge.Delta);
                        edits.Add(ge);
                    }
                    footprint.Add(new LockResource(D.Map.GlobalMapPage, LockPurpose.Map));
                }
                else
                {
                    if (allocator.Allocate(p.Overlay, out PageEdit mapEdit, out string? fail) is not { } directPage)
                    {
                        return new CompiledChange(null, fail);
                    }
                    allocatedPage = directPage;
                    p.Edit(mapEdit.Page, mapEdit.Delta);
                    edits.Add(mapEdit);
                    footprint.Add(new LockResource(D.Map.GlobalMapPage, LockPurpose.Map));
                }
                var pageEdit = new PageEdit(allocatedPage, new WholePage(new byte[D.PageSize]), PageRole.Data);
                p.Edit(pageEdit.Page, pageEdit.Delta);
                edits.Add(pageEdit);
                footprint.Add(new LockResource(allocatedPage, LockPurpose.DataPage));
            }
            List<uint> reservedExtentPages = [.. allocator.NewlyReservedExtentPages];
            List<uint> releasedAtClose = [.. allocator.NewlyReservedExtentPages];
            if (captureForExtent && allocator.AllocatedCount > 0)
            {
                List<PageEdit> reserveEdits = [];
                List<uint> reserved = [];
                if (!allocator.TryReserveExtent(firstFreeBefore, globalMapBefore, reserveEdits, reserved, out string? resFail, allowCrossByteWithinBlock: true))
                {
                    return new CompiledChange(null, resFail);
                }
                foreach (PageEdit re in reserveEdits)
                {
                    p.Edit(re.Page, re.Delta);
                    edits.Add(re);
                }
                reservedExtentPages.AddRange(reserved);
                releasedAtClose.AddRange(reserved);
            }
            return new CompiledChange(new PhysicalChange(cache, edits, footprint)
            {
                HasReservedExtent = reservedExtentPages.Count > 0,
                ReservedExtentPages = reservedExtentPages,
                ConsumedReservedPages = [.. allocator.ConsumedReservedPages],
                ReleasedAtClose = releasedAtClose,
            }, null);
        });
    }

    private sealed class SingleFileTarget(PageCache cache, IPositionedFile file) : IPublicationTarget
    {
        public IPositionedFile? Writable(PageCache target, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            failure = null;
            return ReferenceEquals(target, cache) ? file : null;
        }
    }
}
