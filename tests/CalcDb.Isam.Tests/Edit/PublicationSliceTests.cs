using System.Runtime.InteropServices;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Isam.Tests.Edit;

/// <summary>
/// Targeted W-05 publication tests for record-decided field, row, and header rules:
/// ACCDB u16LE commit-counter carry past 255 (<c>evidence/r-q19-growth-open/split-uncovered-right-leaf-below</c>) with
/// Jet 4 MDB low-byte-255 guard, unset/Null ordinary Yes/No materializing as False (<c>r-fmt-2-pres-1/bool-ladder</c>,
/// <c>r-expr-locale/invoke-1031</c>, <c>r-ado-1b/edit-update-args</c>), fixed-width <c>BINARY(n)</c> with right zero
/// padding (<c>r-oledb-odbc</c>), variable-width 16-byte <c>GUID</c> (<c>r-fmt-1/create-accdb-guid</c>,
/// <c>r-oledb-odbc</c>), and trailing Null fixed <c>Decimal</c> (<c>C = MaxColumns</c>).
/// </summary>
public sealed class PublicationSliceTests
{
    private const string AdoMainFixture = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7";
    private const string OledbOdbcFixture = "3616122cce29bd4b057713e17102cbcde53bad9f8f4f28fb479849313e87615e";
    private const string AccdbGuidFixture = "008bbe8b73b45941eb07c50a4491a7a1caa863396ef731fa8f431d4af2f50ac9";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    private static void AssignText(EditFrame frame, ColumnDescriptor column, string text)
    {
        nint bstr = Marshal.StringToBSTR(text);
        try
        {
            Assert.True(frame.Assign(column, new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr }).Succeeded);
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    private static ColumnDescriptor Col(TableDefinition t, string name) => t.Columns.Single(c => c.Name == name);

    private static NativeValue ReadAfterReopen(byte[] image, uint tableDefPage, string keyColumn, long keyValue, string targetColumn)
    {
        using var file = new MemoryDatabaseFile(image, D.PageSize, 19);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 256);
        InspectionReport report = Inspector.Inspect(image);
        TableDefinition table = report.TableDefs.Single(t => t.FirstPage == tableDefPage);
        using TableCursor cursor = TableCursor.Open(cache, D, table, file.PageCount, 0, out _)!;
        ColumnDescriptor key = Col(table, keyColumn);
        ColumnDescriptor target = Col(table, targetColumn);
        for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
        {
            FieldResult k = cursor.Read(key);
            if (k.Succeeded && k.Value.Kind == NativeKind.Long && k.Value.Integer == keyValue)
            {
                FieldResult r = cursor.Read(target);
                Assert.True(r.Succeeded, r.Detail);
                return r.Value;
            }
        }
        throw new Xunit.Sdk.XunitException($"Row {keyColumn}={keyValue} not found in table page {tableDefPage}.");
    }

    private static byte[] ApplyEditsToSnapshot(byte[] snapshot, IReadOnlyList<PageEdit> edits)
    {
        byte[] image = [.. snapshot];
        foreach (PageEdit edit in edits)
        {
            Assert.NotEqual(0u, edit.Page);
            int offset = checked((int)edit.Page * D.PageSize);
            if (offset + D.PageSize > image.Length)
            {
                Array.Resize(ref image, offset + D.PageSize);
            }
            edit.Delta.Apply(image.AsSpan(offset, D.PageSize));
        }
        return image;
    }

    [Fact]
    public void FieldEncoder_TryEncodeFixed_encodes_fixed_BINARY_with_right_zero_padding_and_refuses_bad_shapes()
    {
        var fixedBin16 = new ColumnDescriptor
        {
            Name = "Bin",
            Ordinal = 0,
            PhysicalNumber = 0,
            VariableIndex = 0,
            ColumnId = 0,
            TypeCode = NativeTypes.Binary,
            TypeInfo = 0,
            Flags = 0x01,
            ExtendedFlags = 0,
            Length = 16,
            FixedOffset = 0,
            IsFixed = true,
        };

        // Shorter payload (7 bytes) is right-zero-padded to 16 bytes, matching r-oledb-odbc-v2-accdb.accdb Items.Bin row 1.
        byte[] prefix = [0x00, 0x03, 0x06, 0x09, 0x0C, 0x0F, 0x12];
        byte[] buf = new byte[16];
        buf.AsSpan().Fill(0xAA);
        var shortVal = new NativeValue(NativeKind.Binary) { Bytes = prefix };
        Assert.True(FieldEncoder.TryEncodeFixed(shortVal, fixedBin16, D, buf, out string? err), err);
        Assert.Equal([0x00, 0x03, 0x06, 0x09, 0x0C, 0x0F, 0x12, 0, 0, 0, 0, 0, 0, 0, 0, 0], buf);

        // Exact-width 16-byte payload copies cleanly.
        byte[] full = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        Assert.True(FieldEncoder.TryEncodeFixed(new NativeValue(NativeKind.Binary) { Bytes = full }, fixedBin16, D, buf, out err), err);
        Assert.Equal(full, buf);

        // Empty, overlong, wrong span width, and non-fixed binary columns stay refused.
        Assert.False(FieldEncoder.TryEncodeFixed(new NativeValue(NativeKind.Binary) { Bytes = [] }, fixedBin16, D, buf, out _));
        Assert.False(FieldEncoder.TryEncodeFixed(new NativeValue(NativeKind.Binary) { Bytes = new byte[17] }, fixedBin16, D, buf, out _));
        Assert.False(FieldEncoder.TryEncodeFixed(shortVal, fixedBin16, D, new byte[8], out _));

        ColumnDescriptor varBin = fixedBin16 with { Flags = 0x00, IsFixed = false };
        Assert.False(FieldEncoder.TryEncodeFixed(shortVal, varBin, D, buf, out _));
    }

    [Fact]
    public void FieldEncoder_TryEncodeVariable_encodes_variable_GUID_and_refuses_bad_length()
    {
        var varGuid = new ColumnDescriptor
        {
            Name = "G",
            Ordinal = 0,
            PhysicalNumber = 0,
            VariableIndex = 0,
            ColumnId = 0,
            TypeCode = NativeTypes.Guid,
            TypeInfo = 0,
            Flags = 0x00,
            ExtendedFlags = 0,
            Length = 16,
            FixedOffset = 0,
            IsFixed = false,
        };

        byte[] raw = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00").ToByteArray();
        Assert.True(FieldEncoder.TryEncodeVariable(new NativeValue(NativeKind.Guid) { Bytes = raw }, varGuid, D, out byte[]? encoded, out string? err), err);
        Assert.Equal(raw, encoded);

        // Bad length or wrong kind stays refused.
        Assert.False(FieldEncoder.TryEncodeVariable(new NativeValue(NativeKind.Guid) { Bytes = new byte[15] }, varGuid, D, out _, out _));
        Assert.False(FieldEncoder.TryEncodeVariable(new NativeValue(NativeKind.Binary) { Bytes = raw }, varGuid, D, out _, out _));
    }

    [Fact]
    public void Unset_YesNo_and_trailing_Null_Decimal_insert_and_edit_on_r_ado_1b_main()
    {
        // Table T in r-ado-1b-main.accdb has ordinary Yes/No column Y and trailing fixed Decimal column N (phys 14 of 15).
        using var file = new ReplayFixture(AdoMainFixture);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = file, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
        (TableDefinition table, TableEditContext ctx) = file.Table("T")!.Value;
        using var cursor = (TableCursor)file.Open(table, 1)!;
        using var frame = new EditFrame(ctx, cursor, ws);

        // 1. AddNew leaving Y and trailing fixed Decimal N unset succeeds; Y materializes as False, N stays Null.
        Assert.True(frame.AddNew().Succeeded);
        AssignText(frame, Col(table, "Code"), "n9");
        UpdateOutcome add = frame.Update();
        Assert.True(add.Succeeded, add.ToString());
        long newId = add.Change!.Values[Col(table, "ID").Ordinal].Integer;
        Assert.Equal(NativeKind.Boolean, add.Change.Values[Col(table, "Y").Ordinal].Kind);
        Assert.False(add.Change.Values[Col(table, "Y").Ordinal].Boolean);
        Assert.Equal(NativeKind.Null, add.Change.Values[Col(table, "N").Ordinal].Kind);

        byte[] afterAdd = file.Image.Snapshot();
        NativeValue reopenedY = ReadAfterReopen(afterAdd, table.FirstPage, "ID", newId, "Y");
        Assert.Equal(NativeKind.Boolean, reopenedY.Kind);
        Assert.False(reopenedY.Boolean);
        NativeValue reopenedN = ReadAfterReopen(afterAdd, table.FirstPage, "ID", newId, "N");
        Assert.Equal(NativeKind.Null, reopenedN.Kind);

        // 2. Direct SameWidthWriter.Compile with Null for Y clears the Yes/No mask bit (False) on a row whose Y is True,
        // while a non-Null mismatched kind (Long) is still refused.
        ColumnDescriptor idCol = Col(table, "ID");
        ColumnDescriptor yCol = Col(table, "Y");
        RowId? trueRow = null;
        NativeValue[]? rowValues = null;
        for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
        {
            FieldResult y = cursor.Read(yCol);
            if (y.Succeeded && y.Value.Kind == NativeKind.Boolean && y.Value.Boolean)
            {
                trueRow = cursor.CurrentRow;
                rowValues = table.Columns.Select(c => cursor.Read(c).Value).ToArray();
                break;
            }
        }
        Assert.NotNull(trueRow);
        long trueRowId = rowValues![idCol.Ordinal].Integer;
        NativeValue[] withNullY = [.. rowValues];
        withNullY[yCol.Ordinal] = NativeValue.Null;
        CompiledChange compiled = ctx.Writer!.Compile(ctx.Semantics, trueRow!.Value, withNullY);
        Assert.NotNull(compiled.Change);
        Assert.Contains(compiled.Change.Edits, e => e.Delta is BitDelta);
        byte[] afterClear = ApplyEditsToSnapshot(afterAdd, compiled.Change.Edits);
        NativeValue clearedY = ReadAfterReopen(afterClear, table.FirstPage, "ID", trueRowId, "Y");
        Assert.Equal(NativeKind.Boolean, clearedY.Kind);
        Assert.False(clearedY.Boolean);

        NativeValue[] withBadY = [.. rowValues];
        withBadY[yCol.Ordinal] = new NativeValue(NativeKind.Long) { Integer = 1 };
        CompiledChange refused = ctx.Writer.Compile(ctx.Semantics, trueRow.Value, withBadY);
        Assert.Null(refused.Change);
        Assert.Contains("Yes/No field Y", refused.NotWritten, StringComparison.Ordinal);

        // Also check CompileInsert refuses non-Null mismatched kind on Yes/No.
        NativeValue[] insertBadY = [.. add.Change.Values];
        insertBadY[idCol.Ordinal] = new NativeValue(NativeKind.Long) { Integer = newId + 10 };
        insertBadY[Col(table, "Code").Ordinal] = new NativeValue(NativeKind.Text) { Text = "n10" };
        insertBadY[yCol.Ordinal] = new NativeValue(NativeKind.Long) { Integer = 1 };
        CompiledChange refusedInsert = ctx.Writer.CompileInsert(ctx.Semantics, insertBadY, autoNumberFloor: null);
        Assert.Null(refusedInsert.Change);
        Assert.Contains("Yes/No field Y", refusedInsert.NotWritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixed_BINARY16_variable_GUID_and_trailing_Null_Decimal_edit_and_insert_on_r_oledb_odbc()
    {
        // Table Items in r-oledb-odbc-v2-accdb.accdb has fixed BINARY(16) Bin, variable GUID G, and trailing fixed Decimal N.
        using var file = new ReplayFixture(OledbOdbcFixture);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = file, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
        (TableDefinition table, TableEditContext ctx) = file.Table("Items")!.Value;
        using var cursor = (TableCursor)file.Open(table, 1)!;
        using var frame = new EditFrame(ctx, cursor, ws);

        // 1. Edit row 1 Txt -> "tx3" leaving fixed BINARY(16) Bin, variable GUID G, and Null Decimal N intact.
        ColumnDescriptor idCol = Col(table, "ID");
        for (cursor.MoveFirst(); !cursor.Eof && cursor.Read(idCol).Value.Integer != 1; cursor.MoveNext())
        {
        }
        Assert.False(cursor.Eof);
        byte[] origBin = cursor.Read(Col(table, "Bin")).Value.Bytes!.ToArray();
        byte[] origGuid = cursor.Read(Col(table, "G")).Value.Bytes!.ToArray();
        Assert.Equal(16, origBin.Length);
        Assert.Equal(16, origGuid.Length);

        Assert.True(frame.Edit().Succeeded);
        AssignText(frame, Col(table, "Txt"), "tx3");
        UpdateOutcome edit = frame.Update();
        Assert.True(edit.Succeeded, edit.ToString());

        byte[] afterEdit = file.Image.Snapshot();
        Assert.Equal("tx3", ReadAfterReopen(afterEdit, table.FirstPage, "ID", 1, "Txt").Text);
        Assert.Equal(origBin, ReadAfterReopen(afterEdit, table.FirstPage, "ID", 1, "Bin").Bytes!.ToArray());
        Assert.Equal(origGuid, ReadAfterReopen(afterEdit, table.FirstPage, "ID", 1, "G").Bytes!.ToArray());
        Assert.Equal(NativeKind.Null, ReadAfterReopen(afterEdit, table.FirstPage, "ID", 1, "N").Kind);

        // 2. Insert a new row with a 7-byte Bin (right-zero-padded to 16), a 16-byte variable GUID G, and Null N.
        byte[] shortBin = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x01, 0x02];
        byte[] newGuid = Guid.Parse("a1b2c3d4-e5f6-4718-9a0b-1c2d3e4f5a6b").ToByteArray();
        var insertValues = new NativeValue[table.Columns.Count];
        Array.Fill(insertValues, NativeValue.Null);
        insertValues[Col(table, "ID").Ordinal] = new NativeValue(NativeKind.Long) { Integer = 99 };
        insertValues[Col(table, "Code").Ordinal] = new NativeValue(NativeKind.Text) { Text = "z99" };
        insertValues[Col(table, "Txt").Ordinal] = new NativeValue(NativeKind.Text) { Text = "inserted" };
        insertValues[Col(table, "Bin").Ordinal] = new NativeValue(NativeKind.Binary) { Bytes = shortBin };
        insertValues[Col(table, "G").Ordinal] = new NativeValue(NativeKind.Guid) { Bytes = newGuid };
        CompiledChange inserted = ctx.Writer!.CompileInsert(ctx.Semantics, insertValues, autoNumberFloor: 99);
        Assert.NotNull(inserted.Change);

        byte[] afterInsert = ApplyEditsToSnapshot(afterEdit, inserted.Change.Edits);
        byte[] expectedPaddedBin = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x01, 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(expectedPaddedBin, ReadAfterReopen(afterInsert, table.FirstPage, "ID", 99, "Bin").Bytes!.ToArray());
        NativeValue reopenedGuid = ReadAfterReopen(afterInsert, table.FirstPage, "ID", 99, "G");
        Assert.Equal(NativeKind.Guid, reopenedGuid.Kind);
        Assert.Equal(newGuid, reopenedGuid.Bytes!.ToArray());
        Assert.Equal("inserted", ReadAfterReopen(afterInsert, table.FirstPage, "ID", 99, "Txt").Text);
        Assert.Equal(NativeKind.Null, ReadAfterReopen(afterInsert, table.FirstPage, "ID", 99, "N").Kind);
    }

    [Fact]
    public void Variable_GUID_insert_round_trips_on_r_fmt_1_accdb_guid()
    {
        // Table T in r-fmt-1-accdb-guid.accdb has a variable GUID column V (type 15, IsFixed=false).
        using var file = new ReplayFixture(AccdbGuidFixture);
        using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = file, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
        (TableDefinition table, TableEditContext ctx) = file.Table("T")!.Value;
        ColumnDescriptor vCol = Col(table, "V");
        Assert.Equal(NativeTypes.Guid, vCol.TypeCode);
        Assert.False(vCol.IsFixed);

        using var cursor = (TableCursor)file.Open(table, 1)!;
        using var frame = new EditFrame(ctx, cursor, ws);

        Guid g = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        Assert.True(frame.AddNew().Succeeded);
        Assert.True(frame.Assign(Col(table, "ID"), I4(100)).Succeeded);
        AssignText(frame, vCol, "{" + g.ToString("D") + "}");
        UpdateOutcome add = frame.Update();
        Assert.True(add.Succeeded, add.ToString());

        byte[] after = file.Image.Snapshot();
        NativeValue reopened = ReadAfterReopen(after, table.FirstPage, "ID", 100, "V");
        Assert.Equal(NativeKind.Guid, reopened.Kind);
        Assert.Equal(g.ToByteArray(), reopened.Bytes!.ToArray());
    }

    [Fact]
    public void CommitCounterDelta_carries_accdb_u16LE_and_guards_jet4_mdb_low_byte_255()
    {
        // ACCDB carries across 255 as u16LE (evidence/r-q19-growth-open/split-uncovered-right-leaf-below: 0x05b5 -> ... -> 0x093b).
        byte[] accdb = new byte[D.PageSize];
        D.Header.MagicBytes.CopyTo(accdb);
        D.Header.AceSignature.CopyTo(accdb.AsSpan(D.Header.SignatureOffset));
        D.Header.Version.Write(accdb, 2);
        D.Header.CommitCounter.Write(accdb, 0x05FF);

        var accdbDelta = new CommitCounterDelta(D);
        accdbDelta.Apply(accdb);
        Assert.Equal(0x0600UL, D.Header.CommitCounter.Read(accdb));
        Assert.Equal((byte)0x00, accdb[D.Header.CommitCounter.Offset]);
        Assert.Equal((byte)0x06, accdb[D.Header.CommitCounter.Offset + 1]);

        D.Header.CommitCounter.Write(accdb, ushort.MaxValue);
        Assert.Throws<FormatViolationException>(() => accdbDelta.Apply(accdb));

        // Jet 4 MDB (allowJet4Mdb=true): increments low byte while preserving byte 3587 (0x0108 -> 0x0109),
        // and still refuses when low byte is 255 (0x01FF) since MDB carry past 255 is unobserved.
        byte[] mdb = new byte[D.PageSize];
        D.Header.MagicBytes.CopyTo(mdb);
        D.Header.JetSignature.CopyTo(mdb.AsSpan(D.Header.SignatureOffset));
        D.Header.Version.Write(mdb, 1);
        D.Header.CommitCounter.Write(mdb, 0x0108);

        var mdbDelta = new CommitCounterDelta(D, allowJet4Mdb: true);
        mdbDelta.Apply(mdb);
        Assert.Equal(0x0109UL, D.Header.CommitCounter.Read(mdb));
        Assert.Equal((byte)0x09, mdb[D.Header.CommitCounter.Offset]);
        Assert.Equal((byte)0x01, mdb[D.Header.CommitCounter.Offset + 1]);

        D.Header.CommitCounter.Write(mdb, 0x01FF);
        Assert.Throws<FormatViolationException>(() => mdbDelta.Apply(mdb));
        Assert.Equal(0x01FFUL, D.Header.CommitCounter.Read(mdb));
    }
}
