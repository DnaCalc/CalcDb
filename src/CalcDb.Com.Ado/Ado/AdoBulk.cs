using System.Text;
using CalcDb.Com.Com;
using CalcDb.Foundation;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Com.Ado;

/// <summary>
/// ADO <c>Recordset.GetRows</c> and <c>Recordset.GetString</c>
/// (evidence/r-str-ado-act/ado-bulk, evidence/r-fix-wire-ado/ado-bulk-elements,
/// evidence/r-str-ado-act/str-ado-read, evidence/r-wire/ado-getrows-tags).
/// </summary>
internal static unsafe class AdoBulk
{
    public static CallResult GetRows(RecordsetObject rs, ReadOnlySpan<nint> args)
    {
        if (!rs.IsOpen)
        {
            return rs.FailClosed();
        }
        if (rs.HasActiveChunkState)
        {
            return rs.FailChunkNavigation("GetRows");
        }

        nint rowsArg = args.Length > 0 ? args[0] : 0;
        nint startArg = args.Length > 1 ? args[1] : 0;
        nint fieldsArg = args.Length > 2 ? args[2] : 0;

        int maxRows = -1;
        if (rowsArg != 0 && Args.TypeOf(rowsArg) != VarType.Empty)
        {
            if (AdoArgs.Int32(rowsArg) is not int r)
            {
                return rs.FailWrongType();
            }
            if (r != -1 && r <= 0)
            {
                return CallResult.Fail(Failure.NotImplemented($"Recordset.GetRows(Rows={r}) (not recorded)"));
            }
            maxRows = r;
        }

        if (!TryResolveStartIndex(rs, startArg, out int startIndex, out CallResult? startFailure))
        {
            return startFailure!.Value;
        }

        if (!TryResolveFieldOrdinals(rs, fieldsArg, out int[]? colOrdinals, out CallResult? fieldsFailure))
        {
            return fieldsFailure!.Value;
        }

        // Base-table Binary (204) and OLE (205) GetRows is recorded only on ACCDB SQL Recordset.Open Client Static ReadOnly (3, 3, 1)
        // with 1-argument GetRows(-1) and omitted Start and Fields
        // (evidence/r-wire/ado-getrows-tags #270/#296, evidence/r-fix-wire-ado/ado-write-values-text-binary #1378/#1404).
        AdoFieldDescriptor[] allCols = rs.ColumnDescriptors;
        for (int i = 0; i < colOrdinals!.Length; i++)
        {
            AdoFieldDescriptor colDesc = allCols[colOrdinals[i]];
            if (colDesc.IsBaseTableColumn && colDesc.AdoType is 204 or 205)
            {
                if (rs.IsMdb)
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.AdoGetRowsBinaryMdb",
                        $"Recordset.GetRows over MDB field '{colDesc.Name}' (type {colDesc.AdoType}) is not recorded"));
                }
                if (rs.OpenedSelectContext is not { Route: AdoSelectRoute.RecordsetOpenSql, IsMdb: false, CursorLocation: 3, CursorType: 3, LockType: 1 })
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.AdoGetRowsBinaryCursor",
                        $"Recordset.GetRows over field '{colDesc.Name}' (type {colDesc.AdoType}) is recorded only on ACCDB SQL Recordset.Open Client Static ReadOnly (3, 3, 1)"));
                }
                if (!rs.IsRecordedGetRowsBinaryArgShape)
                {
                    return CallResult.Fail(Failure.NotModelled(
                        "NotModelled.AdoGetRowsBinaryArgs",
                        $"Recordset.GetRows over field '{colDesc.Name}' (type {colDesc.AdoType}) is recorded only with 1-arg GetRows(-1) and omitted Start and Fields"));
                }
            }
        }

        int colCount = colOrdinals.Length;
        int limit = maxRows == -1 ? int.MaxValue : maxRows;

        // Direct SAFEARRAY fill when the remaining view row count is known upfront (zero intermediate buffer allocations).
        if (rs.TryGetKnownViewCount(out int knownTotal))
        {
            int remaining = knownTotal - startIndex;
            int rowCount = remaining <= 0 ? 0 : Math.Min(remaining, limit);
            if (rowCount == 0)
            {
                return CallResult.Fail(Failure.NotImplemented("Recordset.GetRows when 0 rows remain (not recorded)"));
            }

            nint psaDirect = SafeArrays.Create(VarType.Variant, (0, colCount), (0, rowCount));
            try
            {
                for (int r = 0; r < rowCount; r++)
                {
                    if (!rs.TrySeekView(startIndex + r, out Failure? seekFail))
                    {
                        OleAut.SafeArrayDestroy(psaDirect);
                        return CallResult.Fail(seekFail ?? Failure.NotImplemented("Recordset.GetRows seek failure"));
                    }
                    for (int c = 0; c < colCount; c++)
                    {
                        OwnedVariant? val = rs.ReadCurrentRowCell(colOrdinals[c], out Failure? readFail);
                        if (val is null)
                        {
                            OleAut.SafeArrayDestroy(psaDirect);
                            return CallResult.Fail(readFail!);
                        }
                        NativeVariant* dst = (NativeVariant*)SafeArrays.ElementPointer(psaDirect, c, r);
                        val.TransferTo(dst);
                        val.Dispose();
                    }
                }
            }
            catch
            {
                OleAut.SafeArrayDestroy(psaDirect);
                throw;
            }

            rs.AdvancePositionAfterBulk(startIndex + rowCount);
            rs.ClearConnectionErrors();
            return CallResult.Of(OwnedVariant.TakeArray(psaDirect, VarType.Variant));
        }

        // Unknown-count cursor (e.g. server ForwardOnly): flat cell buffer avoids per-row OwnedVariant[] allocations.
        var flatCells = new List<OwnedVariant>();
        int current = startIndex;
        int fetchedRows = 0;

        try
        {
            while (fetchedRows < limit)
            {
                if (!rs.TrySeekView(current, out Failure? seekFail))
                {
                    if (seekFail is not null)
                    {
                        DisposeCells(flatCells);
                        return CallResult.Fail(seekFail);
                    }
                    break;
                }

                for (int c = 0; c < colCount; c++)
                {
                    OwnedVariant? val = rs.ReadCurrentRowCell(colOrdinals[c], out Failure? readFail);
                    if (val is null)
                    {
                        DisposeCells(flatCells);
                        return CallResult.Fail(readFail!);
                    }
                    flatCells.Add(val);
                }
                fetchedRows++;
                current++;
            }

            if (fetchedRows == 0)
            {
                return CallResult.Fail(Failure.NotImplemented("Recordset.GetRows when 0 rows remain (not recorded)"));
            }

            nint psa = SafeArrays.Create(VarType.Variant, (0, colCount), (0, fetchedRows));
            try
            {
                int idx = 0;
                for (int r = 0; r < fetchedRows; r++)
                {
                    for (int c = 0; c < colCount; c++)
                    {
                        NativeVariant* dst = (NativeVariant*)SafeArrays.ElementPointer(psa, c, r);
                        flatCells[idx++].TransferTo(dst);
                    }
                }
            }
            catch
            {
                OleAut.SafeArrayDestroy(psa);
                throw;
            }
            finally
            {
                DisposeCells(flatCells);
            }

            rs.AdvancePositionAfterBulk(current);
            rs.ClearConnectionErrors();
            return CallResult.Of(OwnedVariant.TakeArray(psa, VarType.Variant));
        }
        catch
        {
            DisposeCells(flatCells);
            throw;
        }
    }

    public static CallResult GetString(RecordsetObject rs, ReadOnlySpan<nint> args)
    {
        if (!rs.IsOpen)
        {
            return rs.FailClosed();
        }

        nint formatArg = args.Length > 0 ? args[0] : 0;
        nint numRowsArg = args.Length > 1 ? args[1] : 0;
        nint colDelimArg = args.Length > 2 ? args[2] : 0;
        nint rowDelimArg = args.Length > 3 ? args[3] : 0;
        nint nullExprArg = args.Length > 4 ? args[4] : 0;

        if (formatArg != 0 && Args.TypeOf(formatArg) != VarType.Empty)
        {
            if (AdoArgs.Int32(formatArg) is not int fmt || fmt != 2)
            {
                return CallResult.Fail(Failure.NotImplemented("Recordset.GetString with StringFormat other than adClipString (2) (not recorded)"));
            }
        }

        int numRows = -1;
        if (numRowsArg != 0 && Args.TypeOf(numRowsArg) != VarType.Empty)
        {
            if (AdoArgs.Int32(numRowsArg) is not int nr)
            {
                return rs.FailWrongType();
            }
            if (nr != -1 && nr <= 0)
            {
                return CallResult.Fail(Failure.NotImplemented($"Recordset.GetString(NumRows={nr}) (not recorded)"));
            }
            numRows = nr;
        }

        if (rs.HasGetStringNonBstrDelimiter)
        {
            return CallResult.Fail(Failure.NotImplemented("Recordset.GetString with a non-BSTR delimiter argument (not recorded)"));
        }

        if (!TryReadOptionalBstr(colDelimArg, "\t", "ColumnDelimiter", out string colDelim, out Failure? delimFail)
            || !TryReadOptionalBstr(rowDelimArg, "\r", "RowDelimiter", out string rowDelim, out delimFail)
            || !TryReadOptionalBstr(nullExprArg, "", "NullExpr", out string nullExpr, out delimFail))
        {
            return CallResult.Fail(delimFail!);
        }

        if (rs.IsAtBofOrEof)
        {
            return CallResult.Fail(Failure.NotImplemented("Recordset.GetString at BOF/EOF (not recorded)"));
        }
        if (rs.IsClientCursor && rs.HasActiveChunkState)
        {
            return rs.FailChunkNavigation("GetString");
        }

        int startIndex = rs.CurrentViewPosition;
        int limit = numRows == -1 ? int.MaxValue : numRows;
        int colCount = rs.ColumnDescriptors.Length;
        var sb = new StringBuilder();
        int current = startIndex;
        int count = 0;

        while (count < limit)
        {
            if (!rs.TrySeekView(current, out Failure? seekFail))
            {
                if (seekFail is not null)
                {
                    return CallResult.Fail(seekFail);
                }
                break;
            }

            for (int c = 0; c < colCount; c++)
            {
                if (c > 0)
                {
                    sb.Append(colDelim);
                }
                OwnedVariant? val = rs.ReadCurrentRowCell(c, out Failure? readFail);
                if (val is null)
                {
                    return CallResult.Fail(readFail!);
                }
                using (val)
                {
                    if (!AppendFormattedCell(sb, val, nullExpr, out Failure? fmtFail))
                    {
                        return CallResult.Fail(fmtFail!);
                    }
                }
            }
            sb.Append(rowDelim);
            current++;
            count++;
        }

        if (rs.HasActiveChunkState)
        {
            return rs.FailChunkNavigation("GetString");
        }
        rs.AdvancePositionAfterBulk(current);
        rs.ClearConnectionErrors();

        nint bstr = Bstrs.FromUnits(sb.Length, sb, static (dest, src) => src.CopyTo(0, dest, src.Length));
        return CallResult.OfOwnedBstr(bstr);
    }

    /// <summary>
    /// Format one cell for <c>Recordset.GetString</c> (evidence/r-str-ado-act/ado-bulk,
    /// evidence/r-fix-wire-ado/ado-bulk-elements, evidence/r-str-ado-act/str-ado-read):
    /// both <c>VT_NULL</c>/<c>VT_EMPTY</c> AND zero-length <c>VT_BSTR</c> (<c>""</c>) emit <paramref name="nullExpr"/>;
    /// non-empty <c>VT_BSTR</c> emits exact UTF-16 code units preserving embedded NUL (<c>\0</c>);
    /// <c>VT_I4</c> formats as invariant base-10 digits. Other cell types are not recorded and refuse by name.
    /// </summary>
    private static bool AppendFormattedCell(StringBuilder sb, OwnedVariant cell, string nullExpr, out Failure? refusal)
    {
        refusal = null;
        NativeVariant* v = cell.Pointer;
        VarType vt = (VarType)v->Vt;
        switch (vt)
        {
            case VarType.Null:
            case VarType.Empty:
                sb.Append(nullExpr);
                return true;
            case VarType.Bstr:
                if (v->Pointer == 0)
                {
                    sb.Append(nullExpr);
                    return true;
                }
                ReadOnlySpan<char> chars = Bstrs.Chars(v->Pointer);
                if (chars.Length == 0)
                {
                    sb.Append(nullExpr);
                }
                else
                {
                    sb.Append(chars);
                }
                return true;
            case VarType.I4:
                sb.Append((int)v->Int64);
                return true;
            default:
                refusal = Failure.NotImplemented($"Recordset.GetString cell formatting for VarType {vt} (only Null, I4, and BSTR are recorded)");
                return false;
        }
    }

    private static bool TryReadOptionalBstr(nint arg, string defaultValue, string paramName, out string value, out Failure? refusal)
    {
        refusal = null;
        value = defaultValue;
        if (arg == 0)
        {
            return true;
        }
        NativeVariant* cell = Args.Cell(arg);
        VarType vt = (VarType)cell->Vt;
        if (vt == VarType.Empty)
        {
            return true;
        }
        if (vt == VarType.Bstr)
        {
            value = cell->Pointer == 0 ? "" : Bstrs.Chars(cell->Pointer).ToString();
            return true;
        }
        refusal = Failure.NotImplemented($"Recordset.GetString({paramName} of VarType {vt}) (only omitted or BSTR is recorded)");
        return false;
    }

    private static bool TryResolveStartIndex(RecordsetObject rs, nint startArg, out int startIndex, out CallResult? failure)
    {
        startIndex = 0;
        failure = null;
        if (startArg == 0 || Args.TypeOf(startArg) == VarType.Empty)
        {
            if (rs.IsAtBofOrEof)
            {
                failure = CallResult.Fail(Failure.NotImplemented("Recordset.GetRows at BOF/EOF with default Start (not recorded)"));
                return false;
            }
            startIndex = rs.CurrentViewPosition;
            return true;
        }

        // evidence/r-fix-wire-ado/ado-bulk-elements (#27) and evidence/r-str-ado-act/ado-bulk (#17): only omitted or adBookmarkFirst (1) is recorded.
        if (AdoArgs.Int32(startArg) is int bmEnum && bmEnum == 1)
        {
            startIndex = 0;
            return true;
        }

        failure = CallResult.Fail(Failure.NotImplemented("Recordset.GetRows with Start other than omitted or adBookmarkFirst (1) (not recorded)"));
        return false;
    }

    private static bool TryResolveFieldOrdinals(RecordsetObject rs, nint fieldsArg, out int[]? ordinals, out CallResult? failure)
    {
        ordinals = null;
        failure = null;
        AdoFieldDescriptor[] cols = rs.ColumnDescriptors;

        if (fieldsArg == 0 || Args.TypeOf(fieldsArg) == VarType.Empty)
        {
            ordinals = new int[cols.Length];
            for (int i = 0; i < cols.Length; i++)
            {
                ordinals[i] = i;
            }
            return true;
        }

        // evidence/r-fix-wire-ado/ado-bulk-elements and evidence/r-str-ado-act/ado-bulk: only omitted or a single BSTR column name is recorded.
        NativeVariant* cell = Args.Cell(fieldsArg);
        VarType vt = (VarType)cell->Vt;
        if (vt != VarType.Bstr)
        {
            failure = CallResult.Fail(Failure.NotImplemented($"Recordset.GetRows with Fields of VarType {vt} (only omitted or a single BSTR column name is recorded)"));
            return false;
        }

        string name = cell->Pointer == 0 ? "" : Bstrs.Chars(cell->Pointer).ToString();
        int ordinal = AdoFilterExpression.FindColumnOrdinal(cols, name);
        if (ordinal < 0)
        {
            failure = rs.FailItemNotFound();
            return false;
        }
        ordinals = [ordinal];
        return true;
    }

    private static void DisposeCells(List<OwnedVariant> cells)
    {
        foreach (OwnedVariant cell in cells)
        {
            cell?.Dispose();
        }
        cells.Clear();
    }
}
