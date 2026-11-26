using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Foundation;
using CalcDb.Values;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-09 (R-ADO-1, R-OLEDB-1 OLE-PARAM, OLE-BUFFER): tests <c>ADODB.Command.Execute</c> (non-parameterized &amp;
/// parameterized) and <c>ADODB.Connection.OpenSchema</c> (adSchemaTables/Columns/Indexes/PrimaryKeys/ForeignKeys)
/// on both ACCDB and MDB through the published CalcDb.dll under CalcDb.Own.manifest.
/// Citations: evidence/r-q54-excl-ddl-ix-cmd/ado-cmd-exec-noparam-accdb, ado-cmd-exec-noparam-mdb,
/// ado-cmd-exec-params-accdb, ado-cmd-exec-params-mdb, ado-openschema-accdb, ado-openschema-mdb;
/// evidence/r-oledb-odbc/param-order-binding, param-edge, schema-columns;
/// evidence/r-q-ado-misc/param-markers-names, param-saved-declared-order, param-saved-names-swapped,
/// param-saved-wrong-names, schema-ids-no-restrictions.
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoCommandAndOpenSchemaTests
{
    private static readonly int[] NonInputDirections = [2, 3, 4];

    private static readonly (string Name, int Type, int Size, int Attr)[] ExpectedTableSchemaFields =
    [
        ("TABLE_CATALOG", 202, 255, 70),
        ("TABLE_SCHEMA", 202, 255, 70),
        ("TABLE_NAME", 202, 255, 70),
        ("TABLE_TYPE", 202, 255, 70),
        ("TABLE_GUID", 72, 16, 86),
        ("DESCRIPTION", 203, 536870910, 198),
        ("TABLE_PROPID", 19, 4, 86),
        ("DATE_CREATED", 7, 8, 86),
        ("DATE_MODIFIED", 7, 8, 86),
    ];

    private static readonly Guid IidCommand = new("986761E8-7269-4890-AA65-AD7C03697A6D");
    private static readonly Guid IidConnection = new("00001550-0000-0010-8000-00AA006D2EA4");
    private static readonly Guid IidOleDbCommand = new("0C733A63-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidOleDbCommandText = new("0C733A26-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidOleDbAccessor = new("0C733A8C-2A1C-11CE-ADE5-00AA0044773D");

    [Theory]
    [InlineData(128, ".accdb")]
    [InlineData(64, ".mdb")]
    public void Command_Execute_non_parameterized_matches_r_q54_records(int dbVersion, string extension)
    {
        RunOnSta(tempDir =>
        {
            string dbPath = Path.Combine(tempDir, "cmd-noparam" + extension);
            CreateItemsDb(dbPath, dbVersion);

            // No ActiveConnection -> 0x800A0E7D (ado-cmd-exec-noparam-* #28).
            nint cmd0 = New(OwnCommand);
            Assert.Null(Put(cmd0, "CommandText", "SELECT ID FROM Items").Failure);
            AssertError(Call(cmd0, "Execute"), unchecked((int)0x800A0E7D), "ADODB.Command", 1240658, IidCommand);
            Release(cmd0);

            nint cn = OpenAceConnection(dbPath, mode: 3);
            try
            {
                if (dbVersion == 64)
                {
                    // Jet 4 MDB Mode=3 is recorded in ado-cmd-exec-{noparam,params}-mdb without reading open
                    // ConnectionString or Properties; both stay refused by name (LockingMode == null).
                    Assert.Equal(HResults.E_NOTIMPL, Invoke(cn, "ConnectionString", 2).Failure!.Value.Scode);
                    Assert.Equal(HResults.E_NOTIMPL, Invoke(cn, "Properties", 2).Failure!.Value.Scode);
                }

                // Empty CommandText -> Parameters.Count == 0 (no regression), Execute -> 0x80040E0C (ado-cmd-exec-noparam-* #38).
                nint cmdEmpty = New(OwnCommand);
                Assert.Null(Put(cmdEmpty, "ActiveConnection", new Dispatch(cn)).Failure);
                nint pEmpty = (nint)Get(cmdEmpty, "Parameters")!;
                Assert.Equal(0, Get(pEmpty, "Count"));
                Release(pEmpty);
                AssertError(Call(cmdEmpty, "Execute"), unchecked((int)0x80040E0C), "Microsoft Access Database Engine", 5000000, IidOleDbCommand);
                AssertConnectionError(cn, unchecked((int)0x80040E0C), "", 0);
                Release(cmdEmpty);

                nint cmd = New(OwnCommand);
                Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmd, "CommandText", "SELECT ID, Name, Qty FROM Items ORDER BY ID").Failure);

                // SELECT with ByRef RecordsAffected (omitted Options -> 0 written; #49).
                int ra = -99;
                Outcome r1 = CallByRefInt32(cmd, "Execute", ref ra);
                Assert.Null(r1.Failure);
                Assert.Equal(0, ra);
                nint rs1 = (nint)r1.Value!;
                Assert.Equal(1, Get(rs1, "State"));
                Assert.Equal(2, ReadAllRows(rs1).Count);
                Assert.Null(Call(rs1, "Close").Failure);
                Release(rs1);

                // SELECT with Options = 129 (adCmdText | adExecuteNoRecords) -> null dispatch, ra = 0 (#77).
                ra = -99;
                Outcome r3 = CallByRefInt32(cmd, "Execute", ref ra, Missing, 129);
                Assert.Null(r3.Failure);
                Assert.Equal(0, ra);
                Assert.Equal((nint)0, (nint)r3.Value!);

                // adCmdTable (2) -> opens Items (#85-87).
                Assert.Null(Put(cmd, "CommandType", 2).Failure);
                Assert.Null(Put(cmd, "CommandText", "Items").Failure);
                nint rsTbl = (nint)Call(cmd, "Execute").Value!;
                Assert.Equal(1, Get(rsTbl, "State"));
                Assert.Equal(2, ReadAllRows(rsTbl).Count);
                Assert.Null(Call(rsTbl, "Close").Failure);
                Release(rsTbl);

                // CommandType = 512 (adCmdTableDirect) fails 0x800A0BB9 and keeps CommandType=2 (#101-102).
                AssertError(Put(cmd, "CommandType", 512), unchecked((int)0x800A0BB9), "ADODB.Command", 1240641, IidCommand);
                Assert.Equal(2, Get(cmd, "CommandType"));
                nint rsTbl2 = (nint)Call(cmd, "Execute").Value!;
                Assert.Equal(2, ReadAllRows(rsTbl2).Count);
                Assert.Null(Call(rsTbl2, "Close").Failure);
                Release(rsTbl2);

                // adCmdStoredProc (4) -> opens saved query VItems (#116-118).
                Assert.Null(Put(cmd, "CommandType", 4).Failure);
                Assert.Null(Put(cmd, "CommandText", "VItems").Failure);
                nint rsSp = (nint)Call(cmd, "Execute").Value!;
                Assert.Equal(2, ReadAllRows(rsSp).Count);
                Assert.Null(Call(rsSp, "Close").Failure);
                Release(rsSp);

                // Prepared + INSERT (Options=129, ra=1), UPDATE (omitted Options -> closed rs State=0, ra=2), DELETE (#132-161).
                Assert.Null(Put(cmd, "CommandType", 1).Failure);
                Assert.Null(Put(cmd, "Prepared", true).Failure);
                Assert.Null(Put(cmd, "CommandText", "INSERT INTO Items (ID, Name, Qty) VALUES (3, 'Gamma', 30)").Failure);
                ra = -99;
                Outcome rIns = CallByRefInt32(cmd, "Execute", ref ra, Missing, 129);
                Assert.Null(rIns.Failure);
                Assert.Equal(1, ra);
                Assert.Equal((nint)0, (nint)rIns.Value!);

                Assert.Null(Put(cmd, "CommandText", "UPDATE Items SET Qty = Qty + 5 WHERE ID >= 2").Failure);
                ra = -99;
                Outcome rUpd = CallByRefInt32(cmd, "Execute", ref ra);
                Assert.Null(rUpd.Failure);
                Assert.Equal(2, ra);
                nint rsUpd = (nint)rUpd.Value!;
                Assert.Equal(0, Get(rsUpd, "State"));
                Release(rsUpd);

                Assert.Null(Put(cmd, "CommandText", "DELETE FROM Items WHERE ID = 3").Failure);
                Outcome rDel = Call(cmd, "Execute", Missing, Missing, 129);
                Assert.Null(rDel.Failure);
                Assert.Equal((nint)0, (nint)rDel.Value!);

                // Syntax error -> 0x80040E14 (3129; #174).
                Assert.Null(Put(cmd, "CommandText", "SELEC ID FROM Items").Failure);
                AssertError(Call(cmd, "Execute"), unchecked((int)0x80040E14), "Microsoft Access Database Engine", 5003129, IidOleDbCommandText);
                AssertConnectionError(cn, unchecked((int)0x80040E14), "3000", -533138860);

                // Duplicate PK -> 0x80004005 (3022), ByRef ra untouched (-99; #182).
                Assert.Null(Put(cmd, "CommandText", "INSERT INTO Items (ID, Name, Qty) VALUES (1, 'Dup', 1)").Failure);
                ra = -99;
                Outcome rDup = CallByRefInt32(cmd, "Execute", ref ra, Missing, 129);
                AssertError(rDup, unchecked((int)0x80004005), "Microsoft Access Database Engine", 5003022, IidOleDbCommand);
                Assert.Equal(-99, ra);
                AssertConnectionError(cn, unchecked((int)0x80004005), "3022", -105121349);

                Release(cmd);
            }
            finally
            {
                Assert.Null(Call(cn, "Close").Failure);
                Release(cn);
            }
        });
    }

    [Theory]
    [InlineData(128, ".accdb")]
    [InlineData(64, ".mdb")]
    public void Command_Execute_parameterized_and_positional_binding_match_records(int dbVersion, string extension)
    {
        RunOnSta(tempDir =>
        {
            string dbPath = Path.Combine(tempDir, "cmd-params" + extension);
            CreateParamDb(dbPath, dbVersion);

            nint cn = OpenAceConnection(dbPath, mode: 3);
            try
            {
                // 1. Parameterized INSERT with adInteger, adVarWChar, adDouble, adDate, adCurrency, adBoolean, adNumeric (ado-cmd-exec-params-* #22-50).
                nint cmdIns = New(OwnCommand);
                Assert.Null(Put(cmdIns, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmdIns, "CommandType", 1).Failure);
                Assert.Null(Put(cmdIns, "CommandText", "INSERT INTO ParamT (ID, S, D, DT, C, Y, DC) VALUES (?, ?, ?, ?, ?, ?, ?)").Failure);
                nint pins = (nint)Get(cmdIns, "Parameters")!;
                AppendParam(cmdIns, pins, "pId", 3, 1, Missing, 1);
                AppendParam(cmdIns, pins, "pS", 202, 1, 30, "Alpha");
                AppendParam(cmdIns, pins, "pD", 5, 1, Missing, 12.75);
                AppendParam(cmdIns, pins, "pDt", 7, 1, Missing, new DateTime(2026, 4, 10));
                AppendParam(cmdIns, pins, "pC", 6, 1, Missing, new CurrencyValue(45.5m));
                AppendParam(cmdIns, pins, "pY", 11, 1, Missing, true);

                nint pDc = (nint)Call(cmdIns, "CreateParameter", "pDc", 131, 1).Value!;
                Assert.Null(Put(pDc, "Precision", (byte)18).Failure);
                Assert.Null(Put(pDc, "NumericScale", (byte)4).Failure);
                Assert.Null(Put(pDc, "Value", "123.4567").Failure);
                Assert.Equal((byte)18, Get(pDc, "Precision"));
                Assert.Equal((byte)4, Get(pDc, "NumericScale"));
                Assert.Null(Call(pins, "Append", new Dispatch(pDc)).Failure);

                int ra = -99;
                Outcome rIns1 = CallByRefInt32(cmdIns, "Execute", ref ra, Missing, 129);
                Assert.Null(rIns1.Failure);
                Assert.Equal(1, ra);

                // Setting adNumeric (131) to VT_NULL -> 0x80040E07 (3464) on Execute, ra untouched (#62-81).
                Assert.Null(Put(pDc, "Value", DBNull.Value).Failure);
                ra = -99;
                Outcome rInsNull = CallByRefInt32(cmdIns, "Execute", ref ra, Missing, 129);
                AssertError(rInsNull, unchecked((int)0x80040E07), "Microsoft Access Database Engine", 5003071, IidOleDbCommand);
                Assert.Equal(-99, ra);
                AssertConnectionError(cn, unchecked((int)0x80040E07), "3464", -539888598);

                Release(pDc);
                Release(pins);
                Release(cmdIns);

                // 2. Scalar Parameters arg: I4 1 -> 1 row, I4 2 -> 0 rows, Null -> 0 rows, Empty -> 0x80040E10 (3748), Missing -> reuses prior (#106-166).
                nint cmdSel = New(OwnCommand);
                Assert.Null(Put(cmdSel, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmdSel, "CommandType", 1).Failure);
                Assert.Null(Put(cmdSel, "CommandText", "SELECT ID, S, D, C, Y, DC FROM ParamT WHERE ID = ?").Failure);

                ra = -99;
                Outcome rs1Out = CallByRefInt32(cmdSel, "Execute", ref ra, 1, 1);
                Assert.Null(rs1Out.Failure);
                Assert.Equal(0, ra);
                nint rs1 = (nint)rs1Out.Value!;
                List<object?[]> rows1 = ReadAllRows(rs1);
                Assert.Single(rows1);
                Assert.Equal(1, rows1[0][0]);
                Assert.Equal("Alpha", rows1[0][1]);
                Assert.Equal(12.75, rows1[0][2]);
                Assert.Equal(123.4567m, rows1[0][5]);
                Assert.Null(Call(rs1, "Close").Failure);
                Release(rs1);

                ra = -99;
                nint rsNull = (nint)CallByRefInt32(cmdSel, "Execute", ref ra, DBNull.Value, 1).Value!;
                Assert.Equal(0, ra);
                Assert.Empty(ReadAllRows(rsNull));
                Assert.Null(Call(rsNull, "Close").Failure);
                Release(rsNull);

                // VT_EMPTY -> 0x80040E10 "Parameter ?_1 has no default value." (3748).
                ra = -99;
                Outcome rEmpty = CallByRefInt32(cmdSel, "Execute", ref ra, null, 1);
                AssertError(rEmpty, unchecked((int)0x80040E10), "Microsoft Access Database Engine", 5003747, IidOleDbCommand);
                Assert.Equal(-99, ra);
                AssertConnectionError(cn, unchecked((int)0x80040E10), "3748", -542379020);

                // Omitted Parameters after prior scalar binding succeeds (#166).
                nint rsReuse = (nint)Call(cmdSel, "Execute", Missing, Missing, 1).Value!;
                Assert.Equal(1, Get(rsReuse, "State"));
                Assert.Null(Call(rsReuse, "Close").Failure);
                Release(rsReuse);

                // Empty Parameters.Count on an open adCmdText ?-command still refuses by name (implicit refresh; r-ado-1b/param-refresh-after-text-change).
                nint psel = (nint)Get(cmdSel, "Parameters")!;
                Assert.Equal(HResults.E_NOTIMPL, TryGet(psel, "Count").Failure!.Value.Scode);
                Release(psel);

                // CreateParameter wrong type -> 0x800A0D5D (#174).
                AssertError(Call(cmdSel, "CreateParameter", "pId", 3, 1, Missing, "notanumber"), unchecked((int)0x800A0D5D), "ADODB.Command", 1240652, IidCommand);
                Release(cmdSel);

                // Unset appended Parameter (VT_EMPTY) on ? command -> 0x80040E10 "Parameter ?_1 has no default value." (r-fix-wire-ado/ado-params-manual-append #38).
                nint cmdUnset = New(OwnCommand);
                Assert.Null(Put(cmdUnset, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmdUnset, "CommandType", 1).Failure);
                Assert.Null(Put(cmdUnset, "CommandText", "SELECT ID FROM ParamT WHERE ID = ?").Failure);
                nint pUnsetCol = (nint)Get(cmdUnset, "Parameters")!;
                nint pUnset = (nint)Call(cmdUnset, "CreateParameter", "p1", 3, 1).Value!;
                Assert.Null(Call(pUnsetCol, "Append", new Dispatch(pUnset)).Failure);
                Outcome rUnset = Call(cmdUnset, "Execute");
                AssertError(rUnset, unchecked((int)0x80040E10), "Microsoft Access Database Engine", 5003747, IidOleDbCommand);
                Assert.Equal("Parameter ?_1 has no default value.", rUnset.Failure!.Value.Description);
                AssertConnectionError(cn, unchecked((int)0x80040E10), "3748", -542379020);
                Release(pUnset);
                Release(pUnsetCol);
                Release(cmdUnset);

                // 3. Output / InputOutput / ReturnValue direction on ? fails 0x80040E21 on IID_IAccessor (r-oledb-odbc/param-edge #55/#77/#99).
                foreach (int dir in NonInputDirections)
                {
                    nint cmdDir = New(OwnCommand);
                    Assert.Null(Put(cmdDir, "ActiveConnection", new Dispatch(cn)).Failure);
                    Assert.Null(Put(cmdDir, "CommandType", 1).Failure);
                    Assert.Null(Put(cmdDir, "CommandText", "SELECT ID FROM ParamT WHERE S = ?").Failure);
                    nint pdir = (nint)Get(cmdDir, "Parameters")!;
                    AppendParam(cmdDir, pdir, "p", 202, dir, 10, "Alpha");
                    AssertError(Call(cmdDir, "Execute"), unchecked((int)0x80040E21), "Microsoft Access Database Engine", 5000000, IidOleDbAccessor);
                    Release(pdir);
                    Release(cmdDir);
                }

                // 4. Saved PARAMETERS query positional binding: wrong names ok, missing -> 3061, surplus ignored (r-q-ado-misc/param-saved-*).
                nint cmdSp = New(OwnCommand);
                Assert.Null(Put(cmdSp, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmdSp, "CommandType", 4).Failure);
                Assert.Null(Put(cmdSp, "CommandText", "QRange").Failure);
                nint psp = (nint)Get(cmdSp, "Parameters")!;
                Assert.Equal(0, Get(psp, "Count"));
                AppendParam(cmdSp, psp, "wrong1", 3, 1, Missing, 1);
                AssertError(Call(cmdSp, "Execute"), unchecked((int)0x80040E10), "Microsoft Access Database Engine", 5003061, IidOleDbCommand);
                AssertConnectionError(cn, unchecked((int)0x80040E10), "3061", -531565506);

                AppendParam(cmdSp, psp, "wrong2", 3, 1, Missing, 5);
                AppendParam(cmdSp, psp, "surplus", 3, 1, Missing, 999);
                nint rsSp = (nint)Call(cmdSp, "Execute").Value!;
                Assert.Single(ReadAllRows(rsSp));
                Assert.Null(Call(rsSp, "Close").Failure);
                Release(rsSp);
                Release(psp);
                Release(cmdSp);

                // 5. Inline PARAMETERS type mismatch -> 0x80004005 "Unspecified error" (r-oledb-odbc/param-order-binding #141).
                nint cmdMis = New(OwnCommand);
                Assert.Null(Put(cmdMis, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmdMis, "CommandType", 1).Failure);
                Assert.Null(Put(cmdMis, "CommandText", "PARAMETERS pB Text(10), pA Long; SELECT ID FROM ParamT WHERE ID = pA AND S = pB;").Failure);
                nint pmis = (nint)Get(cmdMis, "Parameters")!;
                AppendParam(cmdMis, pmis, "pA", 3, 1, Missing, 1);
                AppendParam(cmdMis, pmis, "pB", 202, 1, 10, "notalong");
                AssertError(Call(cmdMis, "Execute"), unchecked((int)0x80004005), "Microsoft Access Database Engine", 5000000, IidOleDbCommand);
                Release(pmis);
                Release(cmdMis);
            }
            finally
            {
                Assert.Null(Call(cn, "Close").Failure);
                Release(cn);
            }
        });
    }

    [Theory]
    [InlineData(128, ".accdb")]
    [InlineData(64, ".mdb")]
    public void Connection_OpenSchema_matches_r_q54_records(int dbVersion, string extension)
    {
        RunOnSta(tempDir =>
        {
            string dbPath = Path.Combine(tempDir, "openschema" + extension);
            CreateSchemaDb(dbPath, dbVersion);

            nint cn = OpenAceConnection(dbPath, mode: 1);
            try
            {
                // 1. adSchemaTables (20): 9 columns, TABLE / SYSTEM TABLE / VIEW ordered by TABLE_NAME.
                nint rsT = (nint)Call(cn, "OpenSchema", 20).Value!;
                Assert.Equal(1, Get(rsT, "State"));
                Assert.Equal(3, Get(rsT, "CursorType"));
                Assert.Equal(1, Get(rsT, "LockType"));
                if (dbVersion == 128)
                {
                    Assert.Equal(-1, Get(rsT, "RecordCount"));
                }
                else
                {
                    Assert.Equal(HResults.E_NOTIMPL, TryGet(rsT, "RecordCount").Failure!.Value.Scode);
                }
                AssertSchemaFields(rsT, ExpectedTableSchemaFields);
                List<object?[]> tableRows = ReadAllRows(rsT);
                Assert.Contains(tableRows, r => Equals(r[2], "Parent") && Equals(r[3], "TABLE"));
                Assert.Contains(tableRows, r => Equals(r[2], "Child") && Equals(r[3], "TABLE"));
                Assert.Contains(tableRows, r => Equals(r[2], "MSysObjects") && Equals(r[3], "SYSTEM TABLE"));
                Assert.Contains(tableRows, r => Equals(r[2], "V_ActiveChild") && Equals(r[3], "VIEW"));
                Assert.Null(Call(rsT, "Close").Failure);
                Release(rsT);

                // 2. adSchemaColumns (4): 28 columns; ORDINAL_POSITION & COLUMN_FLAGS declared 19, runtime VT_R8 (double).
                nint rsC = (nint)Call(cn, "OpenSchema", 4).Value!;
                nint fC = (nint)Get(rsC, "Fields")!;
                Assert.Equal(28, Get(fC, "Count"));
                Release(fC);
                List<object?[]> colRows = ReadAllRows(rsC);
                object?[] childCid = Assert.Single(colRows, r => Equals(r[2], "Child") && Equals(r[3], "CID"));
                Assert.IsType<double>(childCid[6]);  // ORDINAL_POSITION -> VT_R8
                Assert.Equal(1.0, childCid[6]);
                Assert.IsType<double>(childCid[9]);  // COLUMN_FLAGS -> VT_R8
                Assert.Equal(90.0, childCid[9]);     // AutoNumber non-nullable
                Assert.Equal(false, childCid[10]);   // IS_NULLABLE
                Assert.Equal(3, childCid[11]);       // DATA_TYPE = 3 (VT_I4)
                Assert.Equal(10, childCid[15]);      // NUMERIC_PRECISION = 10

                object?[] viewPid = Assert.Single(colRows, r => Equals(r[2], "V_ActiveChild") && Equals(r[3], "PID"));
                Assert.Equal(118.0, viewPid[9]);     // View fixed nullable COLUMN_FLAGS = 118
                Assert.Null(Call(rsC, "Close").Failure);
                Release(rsC);

                // 3. adSchemaIndexes (12): 25 columns; PKs first, then unique, then non-unique; CARDINALITY is VT_DECIMAL.
                nint rsI = (nint)Call(cn, "OpenSchema", 12).Value!;
                nint fI = (nint)Get(rsI, "Fields")!;
                Assert.Equal(25, Get(fI, "Count"));
                Release(fI);
                List<object?[]> ixRows = ReadAllRows(rsI);
                Assert.True(ixRows.Count >= 4);
                Assert.Equal(true, ixRows[0][6]);    // PRIMARY_KEY first
                Assert.IsType<decimal>(ixRows[0][21]); // CARDINALITY -> VT_DECIMAL
                Assert.Null(Call(rsI, "Close").Failure);
                Release(rsI);

                // 4. adSchemaPrimaryKeys (28): 8 columns.
                nint rsPk = (nint)Call(cn, "OpenSchema", 28).Value!;
                List<object?[]> pkRows = ReadAllRows(rsPk);
                Assert.Contains(pkRows, r => Equals(r[2], "Child") && Equals(r[3], "CID") && Equals(r[6], 1.0));
                Assert.Contains(pkRows, r => Equals(r[2], "Parent") && Equals(r[3], "PID") && Equals(r[6], 1.0));
                Assert.Null(Call(rsPk, "Close").Failure);
                Release(rsPk);

                // 5. adSchemaForeignKeys (27): 18 columns.
                nint rsFk = (nint)Call(cn, "OpenSchema", 27).Value!;
                List<object?[]> fkRows = ReadAllRows(rsFk);
                object?[] fk = Assert.Single(fkRows);
                Assert.Equal("Parent", fk[2]);
                Assert.Equal("PID", fk[3]);
                Assert.Equal("Child", fk[8]);
                Assert.Equal("PID", fk[9]);
                Assert.Equal(1.0, fk[12]);
                Assert.Equal("NO ACTION", fk[13]);
                Assert.Equal("NO ACTION", fk[14]);
                Assert.Equal("FK_Child_Parent", fk[16]);
                Assert.Null(Call(rsFk, "Close").Failure);
                Release(rsFk);

                // 6. Empty/Null Restrictions succeed; scalar Restrictions & unsupported Schema -> 0x800A0CB3; invalid Schema -> 0x800A0BB9.
                nint rsEmptyR = (nint)Call(cn, "OpenSchema", 20, null).Value!;
                Assert.Equal(1, Get(rsEmptyR, "State"));
                Assert.Null(Call(rsEmptyR, "Close").Failure);
                Release(rsEmptyR);

                nint rsNullR = (nint)Call(cn, "OpenSchema", 20, DBNull.Value).Value!;
                Assert.Equal(1, Get(rsNullR, "State"));
                Assert.Null(Call(rsNullR, "Close").Failure);
                Release(rsNullR);

                AssertError(Call(cn, "OpenSchema", 20, "Parent"), unchecked((int)0x800A0CB3), "ADODB.Connection", 1240648, IidConnection);
                AssertError(Call(cn, "OpenSchema", 20, 0), unchecked((int)0x800A0CB3), "ADODB.Connection", 1240648, IidConnection);
                AssertError(Call(cn, "OpenSchema", 4, "Parent"), unchecked((int)0x800A0CB3), "ADODB.Connection", 1240648, IidConnection);
                AssertError(Call(cn, "OpenSchema", 1), unchecked((int)0x800A0CB3), "ADODB.Connection", 1240648, IidConnection);
                AssertError(Call(cn, "OpenSchema", 42), unchecked((int)0x800A0CB3), "ADODB.Connection", 1240648, IidConnection);
                AssertError(Call(cn, "OpenSchema", 43), unchecked((int)0x800A0CB3), "ADODB.Connection", 1240648, IidConnection);
                AssertError(Call(cn, "OpenSchema", -1), unchecked((int)0x800A0BB9), "ADODB.Connection", 1240641, IidConnection);
                AssertError(Call(cn, "OpenSchema", 9999), unchecked((int)0x800A0BB9), "ADODB.Connection", 1240641, IidConnection);
            }
            finally
            {
                Assert.Null(Call(cn, "Close").Failure);
            }

            // Closed connection -> 0x800A0E78 (ado-openschema-* #153).
            AssertError(Call(cn, "OpenSchema", 20), unchecked((int)0x800A0E78), "ADODB.Connection", 1240653, IidConnection);
            Release(cn);
        });
    }

    private static void RunOnSta(Action<string> body)
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: {PublishedModule} is missing");
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-cmd-sch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));
                body(tempDir);
                Assert.False(IsLoaded("msado15.dll"));
                Assert.False(IsLoaded("ACEOLEDB.DLL"));
                Assert.False(IsLoaded("ACECORE.DLL"));
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        try { Directory.Delete(tempDir, recursive: true); } catch { }
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static void CreateItemsDb(string dbPath, int dbVersion)
    {
        nint eng = New(OwnEngine);
        nint db = (nint)Call(eng, "CreateDatabase", dbPath, ";LANGID=0x0409;CP=1252;COUNTRY=0", dbVersion).Value!;
        Assert.Null(Call(db, "Execute", "CREATE TABLE Items (ID LONG CONSTRAINT PK_Items PRIMARY KEY, Name TEXT(20), Qty LONG)", 128).Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO Items VALUES (1, 'Alpha', 10)", 128).Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO Items VALUES (2, 'Beta', 20)", 128).Failure);
        nint qv = (nint)Call(db, "CreateQueryDef", "VItems", "SELECT ID, Name FROM Items ORDER BY ID").Value!;
        Release(qv);
        Assert.Null(Call(db, "Close").Failure);
        Release(db);
        Release(eng);
    }

    private static void CreateParamDb(string dbPath, int dbVersion)
    {
        nint eng = New(OwnEngine);
        nint db = (nint)Call(eng, "CreateDatabase", dbPath, ";LANGID=0x0409;CP=1252;COUNTRY=0", dbVersion).Value!;
        Assert.Null(Call(db, "Close").Failure);
        Release(db);
        Release(eng);

        // DECIMAL(18,4) DDL is supported via ADO Connection.Execute (matching ado-cmd-exec-params-{accdb,mdb}).
        nint cn = OpenAceConnection(dbPath, mode: 3);
        Assert.Null(Call(cn, "Execute", "CREATE TABLE ParamT (ID LONG CONSTRAINT PK_ParamT PRIMARY KEY, S TEXT(30), D DOUBLE, DT DATETIME, C CURRENCY, Y BIT, DC DECIMAL(18,4))", Missing, 129).Failure);
        Assert.Null(Call(cn, "Close").Failure);
        Release(cn);

        eng = New(OwnEngine);
        db = (nint)Call(eng, "OpenDatabase", dbPath).Value!;
        nint qr = (nint)Call(db, "CreateQueryDef", "QRange", "PARAMETERS pLo LONG, pHi LONG; SELECT ID, S FROM ParamT WHERE ID >= pLo AND ID <= pHi ORDER BY ID").Value!;
        Release(qr);
        Assert.Null(Call(db, "Close").Failure);
        Release(db);
        Release(eng);
    }

    private static void CreateSchemaDb(string dbPath, int dbVersion)
    {
        nint eng = New(OwnEngine);
        nint db = (nint)Call(eng, "CreateDatabase", dbPath, ";LANGID=0x0409;CP=1252;COUNTRY=0", dbVersion).Value!;
        Assert.Null(Call(db, "Execute", "CREATE TABLE Parent (PID LONG CONSTRAINT PK_Parent PRIMARY KEY, Code TEXT(20), Label TEXT(30))", 128).Failure);
        Assert.Null(Call(db, "Execute", "CREATE UNIQUE INDEX UX_Parent_Code ON Parent (Code)", 128).Failure);
        Assert.Null(Call(db, "Close").Failure);
        Release(db);
        Release(eng);

        nint cn = OpenAceConnection(dbPath, mode: 3);
        Assert.Null(Call(cn, "Execute", "CREATE TABLE Child (CID COUNTER CONSTRAINT PK_Child PRIMARY KEY, PID LONG, Name TEXT(40) NOT NULL, Amt DECIMAL(18,4))", Missing, 129).Failure);
        Assert.Null(Call(cn, "Close").Failure);
        Release(cn);

        eng = New(OwnEngine);
        db = (nint)Call(eng, "OpenDatabase", dbPath).Value!;
        Assert.Null(Call(db, "Execute", "ALTER TABLE Child ADD CONSTRAINT FK_Child_Parent FOREIGN KEY (PID) REFERENCES Parent(PID)", 128).Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO Parent VALUES (1, 'P1', 'One')", 128).Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO Child (PID, Name, Amt) VALUES (1, 'C1', 10.5)", 128).Failure);
        nint qv = (nint)Call(db, "CreateQueryDef", "V_ActiveChild", "SELECT CID, PID, Name, Amt FROM Child ORDER BY CID").Value!;
        Release(qv);
        Assert.Null(Call(db, "Close").Failure);
        Release(db);
        Release(eng);
    }

    private static void AppendParam(nint cmd, nint parameters, string name, int type, int dir, object? size, object? value)
    {
        nint p = (nint)Call(cmd, "CreateParameter", name, type, dir, size, value).Value!;
        Assert.Null(Call(parameters, "Append", new Dispatch(p)).Failure);
        Release(p);
    }

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(NativeVariant* pvarg);

    private static Outcome CallByRefInt32(nint target, string name, ref int byRefArg0, params object?[] rest)
    {
        int total = 1 + rest.Length;
        NativeVariant* rev = stackalloc NativeVariant[total];
        int localVal = byRefArg0;
        rev[total - 1] = new NativeVariant { Vt = (ushort)(VarType.ByRef | VarType.I4), Pointer = (nint)(&localVal) };
        for (int i = 0; i < rest.Length; i++)
        {
            int slot = total - 2 - i;
            if (ReferenceEquals(rest[i], Missing))
            {
                rev[slot] = new NativeVariant { Vt = (ushort)VarType.Error, Int64 = unchecked((int)0x80020004) };
            }
            else if (rest[i] is null)
            {
                rev[slot] = new NativeVariant { Vt = (ushort)VarType.Empty };
            }
            else
            {
                Marshal.GetNativeVariantForObject(rest[i], (nint)(&rev[slot]));
            }
        }
        try
        {
            int dispId = GetDispId(target, name);
            Outcome result = InvokeDispId(target, dispId, 1, rev, (uint)total, 0, null);
            byRefArg0 = localVal;
            return result;
        }
        finally
        {
            for (int i = 0; i < total - 1; i++)
            {
                _ = VariantClear(&rev[i]);
            }
        }
    }

    private static List<object?[]> ReadAllRows(nint rs)
    {
        var rows = new List<object?[]>();
        nint fields = (nint)Get(rs, "Fields")!;
        int count = (int)Get(fields, "Count")!;
        while (!(bool)Get(rs, "EOF")!)
        {
            var row = new object?[count];
            for (int i = 0; i < count; i++)
            {
                nint fld = (nint)Get(fields, "Item", i)!;
                row[i] = Get(fld, "Value");
                Release(fld);
            }
            rows.Add(row);
            Assert.Null(Call(rs, "MoveNext").Failure);
        }
        Release(fields);
        return rows;
    }

    private static void AssertSchemaFields(nint rs, (string Name, int Type, int Size, int Attr)[] expected)
    {
        nint fields = (nint)Get(rs, "Fields")!;
        Assert.Equal(expected.Length, Get(fields, "Count"));
        for (int i = 0; i < expected.Length; i++)
        {
            nint fld = (nint)Get(fields, "Item", i)!;
            Assert.Equal(expected[i].Name, Get(fld, "Name"));
            Assert.Equal(expected[i].Type, Get(fld, "Type"));
            Assert.Equal(expected[i].Size, Get(fld, "DefinedSize"));
            Assert.Equal(expected[i].Attr, Get(fld, "Attributes"));
            Release(fld);
        }
        Release(fields);
    }

    private static void AssertConnectionError(nint cn, int expectedNumber, string expectedSqlState, int expectedNativeError)
    {
        nint errs = (nint)Get(cn, "Errors")!;
        Assert.Equal(1, Get(errs, "Count"));
        nint e0 = (nint)Get(errs, "Item", 0)!;
        Assert.Equal(expectedNumber, Get(e0, "Number"));
        Assert.Equal(expectedSqlState, Get(e0, "SQLState") ?? "");
        Assert.Equal(expectedNativeError, Get(e0, "NativeError"));
        Release(e0);
        Assert.Null(Call(errs, "Clear").Failure);
        Release(errs);
    }

    private static void AssertError(Outcome outcome, int expectedScode, string expectedSource, int expectedHelpContext, Guid expectedIid)
    {
        Assert.NotNull(outcome.Failure);
        Error f = outcome.Failure!.Value;
        Assert.Equal(expectedScode, f.Scode);
        Assert.Equal(expectedSource, f.Source);
        Assert.Equal((uint)expectedHelpContext, f.HelpContext);
        Assert.Equal(expectedIid, f.InfoGuid);
    }
}
