using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-08 / g4tv ADO half:
/// exercises ADO SQL SELECT reads of base-table Binary (204) and OLE (205) columns through CalcDb.Sql ->
/// <c>AdoFieldValues.FromSqlVariant</c>, <c>Field.Value</c>, <c>Field.ActualSize</c>, <c>Field.GetChunk</c>,
/// and <c>Recordset.GetRows</c> against the published <c>CalcDb.dll</c> under <c>CalcDb.Own.manifest</c>,
/// asserting only what the retained ACE records show and verifying every named <c>NotModelled.*</c> refusal.
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoSelectBinaryChunkGetRowsTests
{
    // Retained ArtifactStore fixtures for the evidence families tested below.
    private const string OledbV2Fixture = "3616122cce29bd4b057713e17102cbcde53bad9f8f4f28fb479849313e87615e"; // r-oledb-odbc-v2-accdb.accdb
    private const string Ado1bMdbFixture = "4a447e7517e51e0ed2b734fd8a20d32ab22e83f427667a4f0e8fb2ff0b544bf7"; // r-ado-1b-mdb.mdb
    private const string OledbV2MdbFixture = "7bd7ceb48b5ea4897446fa758617b0f44b31a4aa4fe744c07dcb05ddbc8a523c"; // r-oledb-odbc-v2-mdb.mdb
    private const string WireDaoFixture = "76ceb0c44e385678689da7e1bdb328c9c25230c28e1c18475ca13e7b01db85b7";  // r-wire-dao.accdb
    private const string BulkAdo4Fixture = "98b792c86d8ea840c1028fd0a067ef6b9917e4dfbeb18ee78cb1acb4309cf310"; // r-str-ado-act-ado4-accdb.accdb

    [Fact]
    public void CalcDb_ADO_Select_Binary_GetChunk_ActualSize_and_GetRows_match_retained_ACE_records()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-g4tv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string oledbPath = CopyFixture(store, OledbV2Fixture, tempDir, "r-oledb-odbc-v2-accdb.accdb");
        string mdbPath = CopyFixture(store, Ado1bMdbFixture, tempDir, "r-ado-1b-mdb.mdb");
        string mdbBinPath = CopyFixture(store, OledbV2MdbFixture, tempDir, "r-oledb-odbc-v2-mdb.mdb");
        string wireDaoPath = CopyFixture(store, WireDaoFixture, tempDir, "r-wire-dao.accdb");
        string ado4Path = CopyFixture(store, BulkAdo4Fixture, tempDir, "r-str-ado-act-ado4-accdb.accdb");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                StreamGetChunk_ServerAndClient(oledbPath);
                SqlSelectBinaryAndOle_RecordedRoutesFormatsAndCursors(oledbPath, mdbPath, ado4Path);
                GetRows_BinaryAndOle_ClientStatic(wireDaoPath);
                NamedRefusals_UnrecordedCursorsFormatsRoutesAndGetChunkArguments(oledbPath, mdbPath, mdbBinPath, ado4Path);

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

        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch
        {
        }

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    /// <summary>
    /// Replays evidence/r-oledb-odbc/stream-getchunk on both server ForwardOnly (2, 0, 1) and client Static (3, 3, 1).
    /// </summary>
    private static void StreamGetChunk_ServerAndClient(string oledbPath)
    {
        nint cn = OpenAceConnection(oledbPath, mode: 1);
        try
        {
            // Part 1: server ForwardOnly ReadOnly (2, 0, 1) on SELECT ID, B, M FROM Items ORDER BY ID.
            nint sf = New(OwnRecordset);
            try
            {
                Assert.Null(Put(sf, "CursorLocation", 2).Failure);
                Assert.Null(Call(sf, "Open", "SELECT ID, B, M FROM Items ORDER BY ID", new Dispatch(cn), 0, 1, 1).Failure);
                nint flds = (nint)Get(sf, "Fields")!;
                nint sfb = (nint)Get(flds, "Item", "B")!;
                nint sfm = (nint)Get(flds, "Item", "M")!;
                try
                {
                    // Row 1: 10-byte OLE 00..09 -> ActualSize 10, GetChunk(3) x 5 -> 3, 3, 3, 1, Null, then Value -> 0x8000FFFF.
                    Assert.Equal(10, Get(sfb, "ActualSize"));
                    Assert.Equal(new byte[] { 0, 1, 2 }, (byte[])Call(sfb, "GetChunk", 3).Value!);
                    Assert.Equal(new byte[] { 3, 4, 5 }, (byte[])Call(sfb, "GetChunk", 3).Value!);
                    Assert.Equal(new byte[] { 6, 7, 8 }, (byte[])Call(sfb, "GetChunk", 3).Value!);
                    Assert.Equal(new byte[] { 9 }, (byte[])Call(sfb, "GetChunk", 3).Value!);
                    Assert.Same(DBNull.Value, Call(sfb, "GetChunk", 3).Value);

                    Outcome postChunkVal = TryGet(sfb, "Value");
                    AssertError(
                        postChunkVal,
                        unchecked((int)0x8000FFFF),
                        expectedSource: "Microsoft Access Database Engine",
                        expectedDescription: "Catastrophic failure",
                        expectedHelpContext: 5000000,
                        expectedInfoGuid: IidRowset);
                    AssertConnectionError0(
                        cn,
                        unchecked((int)0x8000FFFF),
                        expectedSqlState: "",
                        expectedNativeError: 0,
                        expectedSource: "Microsoft Access Database Engine",
                        expectedDescription: "Catastrophic failure");

                    // Row 2: Null OLE -> ActualSize 0, GetChunk(3) -> Null, Value -> Null (not consumed).
                    Assert.Null(Call(sf, "MoveNext").Failure);
                    Assert.Equal(0, Get(sfb, "ActualSize"));
                    Assert.Same(DBNull.Value, Call(sfb, "GetChunk", 3).Value);
                    Assert.Same(DBNull.Value, Get(sfb, "Value"));

                    // Row 3: empty 0-byte OLE -> ActualSize 0, GetChunk(3) -> Null, Value -> Null.
                    Assert.Null(Call(sf, "MoveNext").Failure);
                    Assert.Equal(0, Get(sfb, "ActualSize"));
                    Assert.Same(DBNull.Value, Call(sfb, "GetChunk", 3).Value);
                    Assert.Same(DBNull.Value, Get(sfb, "Value"));

                    // Row 4: 12000-byte OLE and 12000-char Memo -> ActualSize 12000 / 24000;
                    // alternating GetChunk(4096) on B and GetChunk(5000) on M resets chunk offset on every field switch.
                    Assert.Null(Call(sf, "MoveNext").Failure);
                    Assert.Equal(12000, Get(sfb, "ActualSize"));
                    Assert.Equal(24000, Get(sfm, "ActualSize"));

                    byte[] firstB = (byte[])Call(sfb, "GetChunk", 4096).Value!;
                    Assert.Equal(4096, firstB.Length);
                    Assert.Equal(new byte[] { 0x00, 0x07, 0x0e, 0x15, 0x1c, 0x23 }, firstB[..6]);
                    string firstM = (string)Call(sfm, "GetChunk", 5000).Value!;
                    Assert.Equal(5000, firstM.Length);

                    for (int rep = 0; rep < 3; rep++)
                    {
                        Assert.Equal(firstB, (byte[])Call(sfb, "GetChunk", 4096).Value!);
                        Assert.Equal(firstM, (string)Call(sfm, "GetChunk", 5000).Value!);
                    }

                    // GetChunk(0) and GetChunk(-1) on OLE fail 0x800A0BB9 (ADODB.Field, helpContext 1240641) and clear Connection.Errors.
                    AssertError(Call(sfb, "GetChunk", 0), unchecked((int)0x800A0BB9), expectedSource: "ADODB.Field", expectedHelpContext: 1240641, expectedInfoGuid: IidField);
                    Assert.Equal(0, GetErrorsCount(cn));
                    AssertError(Call(sfb, "GetChunk", -1), unchecked((int)0x800A0BB9), expectedSource: "ADODB.Field", expectedHelpContext: 1240641, expectedInfoGuid: IidField);
                    Assert.Equal(0, GetErrorsCount(cn));

                    // GetChunk(65536) after switching back from M returns all 12000 bytes from offset 0.
                    byte[] fullB = (byte[])Call(sfb, "GetChunk", 65536).Value!;
                    Assert.Equal(12000, fullB.Length);
                    Assert.Equal(firstB, fullB[..4096]);

                    // Both B and M were chunk-consumed on row 4 -> Value fails 0x8000FFFF and posts 1 error to Connection.Errors.
                    AssertError(TryGet(sfb, "Value"), unchecked((int)0x8000FFFF), expectedSource: "Microsoft Access Database Engine", expectedDescription: "Catastrophic failure", expectedHelpContext: 5000000, expectedInfoGuid: IidRowset);
                    AssertError(TryGet(sfm, "Value"), unchecked((int)0x8000FFFF), expectedSource: "Microsoft Access Database Engine", expectedDescription: "Catastrophic failure", expectedHelpContext: 5000000, expectedInfoGuid: IidRowset);

                    // Row 5: MoveNext, GetChunk(3) -> Null, ActualSize -> 0 do NOT clear Connection.Errors (Count stays 1).
                    Assert.Null(Call(sf, "MoveNext").Failure);
                    Assert.Same(DBNull.Value, Call(sfb, "GetChunk", 3).Value);
                    Assert.Equal(0, Get(sfb, "ActualSize"));
                    Assert.Equal(1, GetErrorsCount(cn));
                }
                finally
                {
                    Release(sfm);
                    Release(sfb);
                    Release(flds);
                }
                Assert.Null(Call(sf, "Close").Failure);
            }
            finally
            {
                Release(sf);
            }

            // Part 2: client Static ReadOnly (3, 3, 1) on SELECT ID, B, M FROM Items ORDER BY ID.
            nint cf = New(OwnRecordset);
            try
            {
                Assert.Null(Put(cf, "CursorLocation", 3).Failure);
                Assert.Null(Call(cf, "Open", "SELECT ID, B, M FROM Items ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                nint flds = (nint)Get(cf, "Fields")!;
                nint cfb = (nint)Get(flds, "Item", "B")!;
                nint cfm = (nint)Get(flds, "Item", "M")!;
                try
                {
                    // Row 1: GetChunk(3) x 5 -> 3, 3, 3, 1, Null, then Value succeeds with full 10 bytes.
                    Assert.Equal(10, Get(cfb, "ActualSize"));
                    Assert.Equal(new byte[] { 0, 1, 2 }, (byte[])Call(cfb, "GetChunk", 3).Value!);
                    Assert.Equal(new byte[] { 3, 4, 5 }, (byte[])Call(cfb, "GetChunk", 3).Value!);
                    Assert.Equal(new byte[] { 6, 7, 8 }, (byte[])Call(cfb, "GetChunk", 3).Value!);
                    Assert.Equal(new byte[] { 9 }, (byte[])Call(cfb, "GetChunk", 3).Value!);
                    Assert.Same(DBNull.Value, Call(cfb, "GetChunk", 3).Value);
                    Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, (byte[])Get(cfb, "Value")!);

                    // Rows 2 and 3: Null and empty OLE.
                    Assert.Null(Call(cf, "MoveNext").Failure);
                    Assert.Equal(0, Get(cfb, "ActualSize"));
                    Assert.Same(DBNull.Value, Call(cfb, "GetChunk", 3).Value);
                    Assert.Same(DBNull.Value, Get(cfb, "Value"));

                    Assert.Null(Call(cf, "MoveNext").Failure);
                    Assert.Equal(0, Get(cfb, "ActualSize"));
                    Assert.Same(DBNull.Value, Call(cfb, "GetChunk", 3).Value);
                    Assert.Same(DBNull.Value, Get(cfb, "Value"));

                    // Row 4: interleaved GetChunk resets on switch; GetChunk(0)/(-1) fail 0x800A0BB9; Value succeeds with full payload.
                    Assert.Null(Call(cf, "MoveNext").Failure);
                    Assert.Equal(12000, Get(cfb, "ActualSize"));
                    Assert.Equal(24000, Get(cfm, "ActualSize"));

                    byte[] firstB = (byte[])Call(cfb, "GetChunk", 4096).Value!;
                    Assert.Equal(4096, firstB.Length);
                    string firstM = (string)Call(cfm, "GetChunk", 5000).Value!;
                    Assert.Equal(5000, firstM.Length);
                    Assert.Equal(firstB, (byte[])Call(cfb, "GetChunk", 4096).Value!);
                    Assert.Equal(firstM, (string)Call(cfm, "GetChunk", 5000).Value!);

                    AssertError(Call(cfb, "GetChunk", 0), unchecked((int)0x800A0BB9), expectedSource: "ADODB.Field", expectedHelpContext: 1240641, expectedInfoGuid: IidField);
                    AssertError(Call(cfb, "GetChunk", -1), unchecked((int)0x800A0BB9), expectedSource: "ADODB.Field", expectedHelpContext: 1240641, expectedInfoGuid: IidField);

                    byte[] chunkAll = (byte[])Call(cfb, "GetChunk", 65536).Value!;
                    Assert.Equal(12000, chunkAll.Length);
                    Assert.Equal(chunkAll, (byte[])Get(cfb, "Value")!);
                    string fullM = (string)Get(cfm, "Value")!;
                    Assert.Equal(12000, fullM.Length);
                    Assert.Equal(firstM, fullM[..5000]);

                    // Row 5: Null OLE, Connection.Errors.Count == 0.
                    Assert.Null(Call(cf, "MoveNext").Failure);
                    Assert.Same(DBNull.Value, Call(cfb, "GetChunk", 3).Value);
                    Assert.Equal(0, Get(cfb, "ActualSize"));
                    Assert.Equal(0, GetErrorsCount(cn));
                }
                finally
                {
                    Release(cfm);
                    Release(cfb);
                    Release(flds);
                }
                Assert.Null(Call(cf, "Close").Failure);
            }
            finally
            {
                Release(cf);
            }
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    /// <summary>
    /// Verifies recorded SQL SELECT Binary (204) and OLE (205) Value and ActualSize across formats, routes, and cursors:
    /// evidence/r-oledb-odbc/buffer-typed-server, evidence/r-oledb-odbc/buffer-typed-client,
    /// evidence/r-q-wire/str-odd-bstr-into-binary, evidence/r-str-ado-act/ado-persist,
    /// evidence/r-str-ado-act/ado-rs-cursors, and evidence/r-ado-1b/fields-meta-mdb.
    /// </summary>
    private static void SqlSelectBinaryAndOle_RecordedRoutesFormatsAndCursors(string oledbPath, string mdbPath, string ado4Path)
    {
        // 1) ACCDB Connection.Execute (2, 0, 1) Binary + OLE: evidence/r-oledb-odbc/buffer-typed-server.
        nint cn = OpenAceConnection(oledbPath, mode: 1);
        try
        {
            nint execRs = (nint)Call(cn, "Execute", "SELECT ID, B, Bin FROM Items ORDER BY ID", Missing, 1).Value!;
            try
            {
                nint flds = (nint)Get(execRs, "Fields")!;
                nint fb = (nint)Get(flds, "Item", "B")!;
                nint fbin = (nint)Get(flds, "Item", "Bin")!;
                try
                {
                    Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, (byte[])Get(fb, "Value")!);
                    Assert.Equal(10, Get(fb, "ActualSize"));
                    byte[] row1Bin = (byte[])Get(fbin, "Value")!;
                    Assert.Equal(16, row1Bin.Length);
                    Assert.Equal(new byte[] { 0x00, 0x03, 0x06, 0x09, 0x0c, 0x0f, 0x12, 0x00 }, row1Bin[..8]);
                    Assert.Equal(16, Get(fbin, "ActualSize"));

                    // Row 2: Null.
                    Assert.Null(Call(execRs, "MoveNext").Failure);
                    Assert.Same(DBNull.Value, Get(fb, "Value"));
                    Assert.Equal(0, Get(fb, "ActualSize"));
                    Assert.Same(DBNull.Value, Get(fbin, "Value"));
                    Assert.Equal(0, Get(fbin, "ActualSize"));

                    // Row 3: empty -> Null / ActualSize 0.
                    Assert.Null(Call(execRs, "MoveNext").Failure);
                    Assert.Same(DBNull.Value, Get(fb, "Value"));
                    Assert.Equal(0, Get(fb, "ActualSize"));
                    Assert.Same(DBNull.Value, Get(fbin, "Value"));
                    Assert.Equal(0, Get(fbin, "ActualSize"));

                    // Row 4: 12000-byte OLE and 16-byte Bin starting with 0xFF.
                    Assert.Null(Call(execRs, "MoveNext").Failure);
                    Assert.Equal(12000, ((byte[])Get(fb, "Value")!).Length);
                    Assert.Equal(12000, Get(fb, "ActualSize"));
                    byte[] row4Bin = (byte[])Get(fbin, "Value")!;
                    Assert.Equal(16, row4Bin.Length);
                    Assert.Equal(0xFF, row4Bin[0]);
                    Assert.Equal(16, Get(fbin, "ActualSize"));
                }
                finally
                {
                    Release(fbin);
                    Release(fb);
                    Release(flds);
                }
                Assert.Null(Call(execRs, "Close").Failure);
            }
            finally
            {
                Release(execRs);
            }

            // 2) ACCDB Recordset.Open (3, 3, 1) Binary + OLE: evidence/r-oledb-odbc/buffer-typed-client.
            nint clientRs = New(OwnRecordset);
            try
            {
                Assert.Null(Put(clientRs, "CursorLocation", 3).Failure);
                Assert.Null(Call(clientRs, "Open", "SELECT ID, B, Bin FROM Items ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                nint flds = (nint)Get(clientRs, "Fields")!;
                nint fb = (nint)Get(flds, "Item", "B")!;
                nint fbin = (nint)Get(flds, "Item", "Bin")!;
                try
                {
                    Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, (byte[])Get(fb, "Value")!);
                    Assert.Equal(10, Get(fb, "ActualSize"));
                    Assert.Equal(16, ((byte[])Get(fbin, "Value")!).Length);
                    Assert.Equal(16, Get(fbin, "ActualSize"));
                }
                finally
                {
                    Release(fbin);
                    Release(fb);
                    Release(flds);
                }
                Assert.Null(Call(clientRs, "Close").Failure);
            }
            finally
            {
                Release(clientRs);
            }

            // 3) ACCDB Recordset.Open Server Static ReadOnly (2, 3, 1) Binary + OLE Value:
            //    evidence/r-q-wire/str-odd-bstr-into-binary, evidence/r-str-ado-act/ado-rs-cursors.
            nint srvStatic = New(OwnRecordset);
            try
            {
                Assert.Null(Put(srvStatic, "CursorLocation", 2).Failure);
                Assert.Null(Call(srvStatic, "Open", "SELECT ID, B, Bin FROM Items ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                nint flds = (nint)Get(srvStatic, "Fields")!;
                nint fb = (nint)Get(flds, "Item", "B")!;
                nint fbin = (nint)Get(flds, "Item", "Bin")!;
                try
                {
                    Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, (byte[])Get(fb, "Value")!);
                    Assert.Equal(16, ((byte[])Get(fbin, "Value")!).Length);
                    // ActualSize on (2, 3, 1) SQL Binary/OLE is unrecorded -> refused by name.
                    AssertNotModelled(TryGet(fb, "ActualSize"), "NotModelled.AdoActualSizeBinaryCursor");
                    AssertNotModelled(TryGet(fbin, "ActualSize"), "NotModelled.AdoActualSizeBinaryCursor");
                }
                finally
                {
                    Release(fbin);
                    Release(fb);
                    Release(flds);
                }
                Assert.Null(Call(srvStatic, "Close").Failure);
            }
            finally
            {
                Release(srvStatic);
            }
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }

        // 4) ACCDB Recordset.Open Client Static BatchOptimistic (3, 3, 4) OLE open/materialization:
        //    evidence/r-str-ado-act/ado-persist, evidence/r-str-ado-act/ado-rs-cursors (c5).
        nint ado4Cn = OpenAceConnection(ado4Path, mode: 1);
        try
        {
            nint batchRs = New(OwnRecordset);
            try
            {
                Assert.Null(Put(batchRs, "CursorLocation", 3).Failure);
                Assert.Null(Call(batchRs, "Open", "SELECT * FROM K ORDER BY ID", new Dispatch(ado4Cn), 3, 4, 1).Failure);
                Assert.Equal(4, Get(batchRs, "RecordCount"));
                nint flds = (nint)Get(batchRs, "Fields")!;
                nint fb = (nint)Get(flds, "Item", "B")!;
                try
                {
                    // Reading Field.Value or ActualSize of OLE directly on (3, 3, 4) is unrecorded -> refused by name.
                    AssertNotModelled(TryGet(fb, "Value"), "NotModelled.AdoSqlBinaryCursor");
                    AssertNotModelled(TryGet(fb, "ActualSize"), "NotModelled.AdoActualSizeBinaryCursor");
                }
                finally
                {
                    Release(fb);
                    Release(flds);
                }
                Assert.Null(Call(batchRs, "Close").Failure);
            }
            finally
            {
                Release(batchRs);
            }
        }
        finally
        {
            Assert.Null(Call(ado4Cn, "Close").Failure);
            Release(ado4Cn);
        }

        // 5) MDB Recordset.Open (2, 0, 1) and (3, 3, 1) OLE: evidence/r-ado-1b/fields-meta-mdb.
        nint mdbCn = OpenAceConnection(mdbPath, mode: 1);
        try
        {
            foreach ((int loc, int cur) in new[] { (2, 0), (3, 3) })
            {
                nint mrs = New(OwnRecordset);
                try
                {
                    Assert.Null(Put(mrs, "CursorLocation", loc).Failure);
                    Assert.Null(Call(mrs, "Open", "SELECT ID, B FROM T ORDER BY ID", new Dispatch(mdbCn), cur, 1, 1).Failure);
                    nint flds = (nint)Get(mrs, "Fields")!;
                    nint fb = (nint)Get(flds, "Item", "B")!;
                    try
                    {
                        Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0xFF, 0x00 }, (byte[])Get(fb, "Value")!);
                        Assert.Equal(5, Get(fb, "ActualSize"));
                        Assert.Null(Call(mrs, "MoveNext").Failure);
                        Assert.Same(DBNull.Value, Get(fb, "Value"));
                        Assert.Equal(0, Get(fb, "ActualSize"));
                    }
                    finally
                    {
                        Release(fb);
                        Release(flds);
                    }
                    Assert.Null(Call(mrs, "Close").Failure);
                }
                finally
                {
                    Release(mrs);
                }
            }
        }
        finally
        {
            Assert.Null(Call(mdbCn, "Close").Failure);
            Release(mdbCn);
        }
    }

    /// <summary>
    /// Replays Binary and OLE GetRows(-1) on Client Static ReadOnly (3, 3, 1) from evidence/r-wire/ado-getrows-tags.
    /// </summary>
    private static void GetRows_BinaryAndOle_ClientStatic(string wireDaoPath)
    {
        nint cn = OpenAceConnection(wireDaoPath, mode: 1);
        try
        {
            // FBin (Binary 204): 2 x 8 0-based variant array.
            nint rBin = New(OwnRecordset);
            try
            {
                Assert.Null(Put(rBin, "CursorLocation", 3).Failure);
                Assert.Null(Call(rBin, "Open", "SELECT ID, FBin FROM W WHERE ID BETWEEN 1000 AND 1099 ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                object?[,] binGrid = CallGetRows(rBin, -1);
                Assert.Equal(2, binGrid.GetLength(0));
                Assert.Equal(8, binGrid.GetLength(1));
                Assert.Equal(1000, binGrid[0, 0]);
                Assert.Same(DBNull.Value, binGrid[1, 0]);
                Assert.Equal(1001, binGrid[0, 1]);
                byte[] b1001 = (byte[])binGrid[1, 1]!;
                Assert.Equal(255, b1001.Length);
                Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x00 }, b1001[..4]);
                Assert.Equal(1003, binGrid[0, 3]);
                byte[] b1003 = (byte[])binGrid[1, 3]!;
                Assert.Equal(255, b1003.Length);
                for (int i = 0; i < 255; i++)
                {
                    Assert.Equal((byte)i, b1003[i]);
                }
                Assert.Equal(1004, binGrid[0, 4]);
                Assert.Same(DBNull.Value, binGrid[1, 4]);
                Assert.Equal(1005, binGrid[0, 5]);
                Assert.Equal(new byte[] { 0x41, 0x00, 0x42, 0x00 }, ((byte[])binGrid[1, 5]!)[..4]);
                Assert.Equal(true, Get(rBin, "EOF"));
                Assert.Null(Call(rBin, "Close").Failure);
            }
            finally
            {
                Release(rBin);
            }

            // FOle (OLE 205): 2 x 6 0-based variant array.
            nint rOle = New(OwnRecordset);
            try
            {
                Assert.Null(Put(rOle, "CursorLocation", 3).Failure);
                Assert.Null(Call(rOle, "Open", "SELECT ID, FOle FROM W WHERE ID BETWEEN 1100 AND 1199 ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                object?[,] oleGrid = CallGetRows(rOle, -1);
                Assert.Equal(2, oleGrid.GetLength(0));
                Assert.Equal(6, oleGrid.GetLength(1));
                Assert.Equal(1100, oleGrid[0, 0]);
                Assert.Same(DBNull.Value, oleGrid[1, 0]);
                Assert.Equal(1101, oleGrid[0, 1]);
                Assert.Equal(new byte[] { 0x00, 0x00, 0x01 }, (byte[])oleGrid[1, 1]!);
                Assert.Equal(1102, oleGrid[0, 2]);
                byte[] o1102 = (byte[])oleGrid[1, 2]!;
                Assert.Equal(256, o1102.Length);
                for (int i = 0; i < 256; i++)
                {
                    Assert.Equal((byte)i, o1102[i]);
                }
                Assert.Equal(1103, oleGrid[0, 3]);
                byte[] o1103 = (byte[])oleGrid[1, 3]!;
                Assert.Equal(5000, o1103.Length);
                Assert.Equal(new byte[] { 0x00, 0x07, 0x0e, 0x15 }, o1103[..4]);
                Assert.Equal(1104, oleGrid[0, 4]);
                Assert.Equal(new byte[] { 0x41, 0x00, 0x42, 0x00 }, (byte[])oleGrid[1, 4]!);
                Assert.Equal(1105, oleGrid[0, 5]);
                Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, (byte[])oleGrid[1, 5]!);
                Assert.Equal(true, Get(rOle, "EOF"));
                Assert.Null(Call(rOle, "Close").Failure);
            }
            finally
            {
                Release(rOle);
            }
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    /// <summary>
    /// Verifies that every unrecorded format, route, cursor, field type, post-GetChunk order, and argument form
    /// is refused by its exact <c>NotModelled.*</c> name.
    /// </summary>
    private static void NamedRefusals_UnrecordedCursorsFormatsRoutesAndGetChunkArguments(
        string oledbPath,
        string mdbPath,
        string mdbBinPath,
        string ado4Path)
    {
        // --- ACCDB refusals ---
        nint cn = OpenAceConnection(oledbPath, mode: 1);
        try
        {
            // Recordset.Open (2, 0, 1) on Binary (204) is unrecorded -> NotModelled.AdoSqlBinaryCursor.
            nint rsSrvBin = New(OwnRecordset);
            try
            {
                Assert.Null(Put(rsSrvBin, "CursorLocation", 2).Failure);
                Assert.Null(Call(rsSrvBin, "Open", "SELECT ID, Bin FROM Items ORDER BY ID", new Dispatch(cn), 0, 1, 1).Failure);
                nint flds = (nint)Get(rsSrvBin, "Fields")!;
                nint fbin = (nint)Get(flds, "Item", "Bin")!;
                try
                {
                    AssertNotModelled(TryGet(fbin, "Value"), "NotModelled.AdoSqlBinaryCursor");
                }
                finally
                {
                    Release(fbin);
                    Release(flds);
                }
                Assert.Null(Call(rsSrvBin, "Close").Failure);
            }
            finally
            {
                Release(rsSrvBin);
            }

            // Recordset.Open (3, 3, 4) selecting Binary (204) refuses at Open materialization -> NotModelled.AdoSqlBinaryCursor.
            nint rsBatchBin = New(OwnRecordset);
            try
            {
                Assert.Null(Put(rsBatchBin, "CursorLocation", 3).Failure);
                AssertNotModelled(
                    Call(rsBatchBin, "Open", "SELECT ID, Bin FROM Items ORDER BY ID", new Dispatch(cn), 3, 4, 1),
                    "NotModelled.AdoSqlBinaryCursor");
            }
            finally
            {
                Release(rsBatchBin);
            }

            // Connection.Execute on a Client cursor (3, 3, 1) selecting Binary/OLE refuses at materialization -> NotModelled.AdoSqlBinaryCursor.
            nint clientExecCn = OpenAceConnection(oledbPath, mode: 1);
            try
            {
                Assert.Null(Put(clientExecCn, "CursorLocation", 3).Failure);
                AssertNotModelled(
                    Call(clientExecCn, "Execute", "SELECT ID, B FROM Items ORDER BY ID", Missing, 1),
                    "NotModelled.AdoSqlBinaryCursor");
            }
            finally
            {
                Assert.Null(Call(clientExecCn, "Close").Failure);
                Release(clientExecCn);
            }

            // GetChunk route / cursor / field type / argument / state / Memo / ActualSize-after-GetChunk refusals.
            nint execRs = (nint)Call(cn, "Execute", "SELECT ID, B FROM Items ORDER BY ID", Missing, 1).Value!;
            try
            {
                nint flds = (nint)Get(execRs, "Fields")!;
                nint fb = (nint)Get(flds, "Item", "B")!;
                try
                {
                    AssertNotModelled(Call(fb, "GetChunk", 3), "NotModelled.AdoGetChunkRoute");
                }
                finally
                {
                    Release(fb);
                    Release(flds);
                }
                AssertNotModelled(Call(execRs, "GetRows", -1), "NotModelled.AdoGetRowsBinaryCursor");
                Assert.Null(Call(execRs, "Close").Failure);
            }
            finally
            {
                Release(execRs);
            }

            // Server ForwardOnly (2, 0, 1): GetRows with OLE, GetChunk after Value, ActualSize after GetChunk,
            // GetChunk at EOF, and GetChunk on closed/invalidated Field.
            nint sf = New(OwnRecordset);
            try
            {
                Assert.Null(Put(sf, "CursorLocation", 2).Failure);
                Assert.Null(Call(sf, "Open", "SELECT ID, B, M, Txt FROM Items ORDER BY ID", new Dispatch(cn), 0, 1, 1).Failure);
                AssertNotModelled(Call(sf, "GetRows", -1), "NotModelled.AdoGetRowsBinaryCursor");
                nint flds = (nint)Get(sf, "Fields")!;
                nint fid = (nint)Get(flds, "Item", "ID")!;
                nint fb = (nint)Get(flds, "Item", "B")!;
                nint fm = (nint)Get(flds, "Item", "M")!;
                nint ftxt = (nint)Get(flds, "Item", "Txt")!;
                try
                {
                    // Field type refusals.
                    AssertNotModelled(Call(fid, "GetChunk", 3), "NotModelled.AdoGetChunkFieldType");
                    AssertNotModelled(Call(ftxt, "GetChunk", 3), "NotModelled.AdoGetChunkFieldType");

                    // Raw argument count / type refusals.
                    AssertNotModelled(Call(fb, "GetChunk"), "NotModelled.AdoGetChunkArg");
                    AssertNotModelled(Call(fb, "GetChunk", 3, 4), "NotModelled.AdoGetChunkArg");
                    AssertNotModelled(Call(fb, "GetChunk", Missing), "NotModelled.AdoGetChunkArg");
                    AssertNotModelled(Call(fb, "GetChunk", "3"), "NotModelled.AdoGetChunkArg");
                    AssertNotModelled(Call(fb, "GetChunk", 3.0), "NotModelled.AdoGetChunkArg");
                    AssertNotModelled(Call(fb, "GetChunk", (short)3), "NotModelled.AdoGetChunkArg");
                    AssertNotModelled(Call(fb, "GetChunk", DBNull.Value), "NotModelled.AdoGetChunkArg");

                    // Row 1: read Value first -> GetChunk after Value on server ForwardOnly refused.
                    Assert.Equal(10, ((byte[])Get(fb, "Value")!).Length);
                    AssertNotModelled(Call(fb, "GetChunk", 3), "NotModelled.AdoGetChunkAfterValue");

                    // Row 2 (Null Memo) and Row 3 (empty Memo) -> Memo Null/empty GetChunk refused.
                    Assert.Null(Call(sf, "MoveNext").Failure);
                    AssertNotModelled(Call(fm, "GetChunk", 10), "NotModelled.AdoGetChunkMemoNullOrEmpty");
                    Assert.Null(Call(sf, "MoveNext").Failure);
                    AssertNotModelled(Call(fm, "GetChunk", 10), "NotModelled.AdoGetChunkMemoNullOrEmpty");

                    // Row 4: Memo non-positive length, Memo continuation, and ActualSize after non-null GetChunk.
                    Assert.Null(Call(sf, "MoveNext").Failure);
                    AssertNotModelled(Call(fm, "GetChunk", 0), "NotModelled.AdoGetChunkMemoNonPositiveLength");
                    AssertNotModelled(Call(fm, "GetChunk", -1), "NotModelled.AdoGetChunkMemoNonPositiveLength");
                    Assert.Equal(5000, ((string)Call(fm, "GetChunk", 5000).Value!).Length);
                    AssertNotModelled(Call(fm, "GetChunk", 5000), "NotModelled.AdoGetChunkMemoContinuation");
                    AssertNotModelled(TryGet(fm, "ActualSize"), "NotModelled.AdoActualSizeAfterGetChunk");

                    Assert.Equal(3, ((byte[])Call(fb, "GetChunk", 3).Value!).Length);
                    AssertNotModelled(TryGet(fb, "ActualSize"), "NotModelled.AdoActualSizeAfterGetChunk");

                    // Advance to EOF -> GetChunk at EOF refused.
                    Assert.Null(Call(sf, "MoveNext").Failure); // Row 5
                    Assert.Null(Call(sf, "MoveNext").Failure); // EOF
                    Assert.Equal(true, Get(sf, "EOF"));
                    AssertNotModelled(Call(fb, "GetChunk", 3), "NotModelled.AdoGetChunkNoCurrentRecord");

                    // Close Recordset -> GetChunk on invalidated Field wrapper refused.
                    Assert.Null(Call(sf, "Close").Failure);
                    AssertNotModelled(Call(fb, "GetChunk", 3), "NotModelled.AdoGetChunkClosed");
                }
                finally
                {
                    Release(ftxt);
                    Release(fm);
                    Release(fb);
                    Release(fid);
                    Release(flds);
                }
            }
            finally
            {
                Release(sf);
            }

            // Client Static (3, 3, 1): GetChunk on Binary (204) and ActualSize after GetChunk refused.
            nint cf = New(OwnRecordset);
            try
            {
                Assert.Null(Put(cf, "CursorLocation", 3).Failure);
                Assert.Null(Call(cf, "Open", "SELECT ID, B, Bin FROM Items ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                nint flds = (nint)Get(cf, "Fields")!;
                nint fb = (nint)Get(flds, "Item", "B")!;
                nint fbin = (nint)Get(flds, "Item", "Bin")!;
                try
                {
                    AssertNotModelled(Call(fbin, "GetChunk", 3), "NotModelled.AdoGetChunkFieldType");
                    Assert.Equal(3, ((byte[])Call(fb, "GetChunk", 3).Value!).Length);
                    AssertNotModelled(TryGet(fb, "ActualSize"), "NotModelled.AdoActualSizeAfterGetChunk");
                }
                finally
                {
                    Release(fbin);
                    Release(fb);
                    Release(flds);
                }
                Assert.Null(Call(cf, "Close").Failure);
            }
            finally
            {
                Release(cf);
            }

            // Server Static (2, 3, 1) and Client BatchOptimistic (3, 3, 4): GetChunk and GetRows on OLE refused.
            foreach ((int loc, int cur, int lck) in new[] { (2, 3, 1), (3, 3, 4) })
            {
                nint rs = New(OwnRecordset);
                try
                {
                    Assert.Null(Put(rs, "CursorLocation", loc).Failure);
                    Assert.Null(Call(rs, "Open", "SELECT ID, B FROM Items ORDER BY ID", new Dispatch(cn), cur, lck, 1).Failure);
                    nint flds = (nint)Get(rs, "Fields")!;
                    nint fb = (nint)Get(flds, "Item", "B")!;
                    try
                    {
                        AssertNotModelled(Call(fb, "GetChunk", 3), "NotModelled.AdoGetChunkCursor");
                    }
                    finally
                    {
                        Release(fb);
                        Release(flds);
                    }
                    AssertNotModelled(Call(rs, "GetRows", -1), "NotModelled.AdoGetRowsBinaryCursor");
                    Assert.Null(Call(rs, "Close").Failure);
                }
                finally
                {
                    Release(rs);
                }
            }
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }

        // --- Recordset.Open(Command) Binary/OLE route refusal ---
        nint sqCn = OpenAceConnection(ado4Path, mode: 1);
        try
        {
            // Recordset.Open(Command) selecting OLE -> NotModelled.AdoSqlBinaryRoute.
            nint cmd = New(OwnCommand);
            nint cmdRs = New(OwnRecordset);
            try
            {
                Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(sqCn)).Failure);
                Assert.Null(Put(cmd, "CommandText", "SELECT ID, B FROM K ORDER BY ID").Failure);
                Assert.Null(Put(cmd, "CommandType", 1).Failure);
                Assert.Null(Put(cmdRs, "CursorLocation", 3).Failure);
                AssertNotModelled(
                    Call(cmdRs, "Open", new Dispatch(cmd), Missing, 3, 1),
                    "NotModelled.AdoSqlBinaryRoute");
            }
            finally
            {
                Release(cmdRs);
                Release(cmd);
            }
        }
        finally
        {
            Assert.Null(Call(sqCn, "Close").Failure);
            Release(sqCn);
        }

        // --- MDB refusals ---
        nint mdbCn = OpenAceConnection(mdbPath, mode: 1);
        try
        {
            // MDB Connection.Execute OLE -> NotModelled.AdoSqlBinaryMdb.
            nint mExec = (nint)Call(mdbCn, "Execute", "SELECT ID, B FROM T ORDER BY ID", Missing, 1).Value!;
            try
            {
                nint flds = (nint)Get(mExec, "Fields")!;
                nint fb = (nint)Get(flds, "Item", "B")!;
                try
                {
                    AssertNotModelled(TryGet(fb, "Value"), "NotModelled.AdoSqlBinaryMdb");
                }
                finally
                {
                    Release(fb);
                    Release(flds);
                }
                Assert.Null(Call(mExec, "Close").Failure);
            }
            finally
            {
                Release(mExec);
            }

            // MDB Server Static (2, 3, 1) OLE materializes at Open -> NotModelled.AdoSqlBinaryMdb.
            nint mStatic = New(OwnRecordset);
            try
            {
                Assert.Null(Put(mStatic, "CursorLocation", 2).Failure);
                AssertNotModelled(
                    Call(mStatic, "Open", "SELECT ID, B FROM T ORDER BY ID", new Dispatch(mdbCn), 3, 1, 1),
                    "NotModelled.AdoSqlBinaryMdb");
            }
            finally
            {
                Release(mStatic);
            }

            // MDB GetChunk and MDB GetRows on OLE -> NotModelled.AdoGetChunkMdb / NotModelled.AdoGetRowsBinaryMdb.
            nint mClient = New(OwnRecordset);
            try
            {
                Assert.Null(Put(mClient, "CursorLocation", 3).Failure);
                Assert.Null(Call(mClient, "Open", "SELECT ID, B FROM T ORDER BY ID", new Dispatch(mdbCn), 3, 1, 1).Failure);
                nint flds = (nint)Get(mClient, "Fields")!;
                nint fb = (nint)Get(flds, "Item", "B")!;
                try
                {
                    AssertNotModelled(Call(fb, "GetChunk", 3), "NotModelled.AdoGetChunkMdb");
                }
                finally
                {
                    Release(fb);
                    Release(flds);
                }
                AssertNotModelled(Call(mClient, "GetRows", -1), "NotModelled.AdoGetRowsBinaryMdb");
                Assert.Null(Call(mClient, "Close").Failure);
            }
            finally
            {
                Release(mClient);
            }
        }
        finally
        {
            Assert.Null(Call(mdbCn, "Close").Failure);
            Release(mdbCn);
        }

        // MDB Binary (204) even on (3, 3, 1) -> NotModelled.AdoSqlBinaryMdb.
        nint mdbBinCn = OpenAceConnection(mdbBinPath, mode: 1);
        try
        {
            nint rs = New(OwnRecordset);
            try
            {
                Assert.Null(Put(rs, "CursorLocation", 3).Failure);
                AssertNotModelled(
                    Call(rs, "Open", "SELECT ID, Bin FROM Items ORDER BY ID", new Dispatch(mdbBinCn), 3, 1, 1),
                    "NotModelled.AdoSqlBinaryMdb");
            }
            finally
            {
                Release(rs);
            }
        }
        finally
        {
            Assert.Null(Call(mdbBinCn, "Close").Failure);
            Release(mdbBinCn);
        }
    }

    private static void AssertNotModelled(Outcome outcome, string expectedReason)
    {
        Assert.NotNull(outcome.Failure);
        Error err = outcome.Failure!.Value;
        Assert.Equal(HResults.E_NOTIMPL, err.Scode);
        Assert.NotNull(err.Description);
        Assert.Contains(expectedReason, err.Description!, StringComparison.Ordinal);
    }

    private static int GetErrorsCount(nint cn)
    {
        nint errs = (nint)Get(cn, "Errors")!;
        try
        {
            return (int)Get(errs, "Count")!;
        }
        finally
        {
            Release(errs);
        }
    }

    private static object?[,] CallGetRows(nint rs, int numRows)
    {
        int dispId = GetDispId(rs, "GetRows");
        NativeVariant arg = VariantOf(numRows);
        DispParams dp = new() { Args = (nint)(&arg), ArgCount = 1 };
        NativeVariant result = default;
        ExcepInfo ex = default;
        uint argErr;
        nint* vt = *(nint**)rs;
        Guid iidNull = Guid.Empty;
        int hr = ((delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)vt[6])(
            rs, dispId, &iidNull, 1033, 1, &dp, &result, &ex, &argErr);
        Assert.Equal(0, hr);
        Assert.Equal((ushort)(VarType.Array | VarType.Variant), result.Vt);
        nint psa = result.Pointer;
        Assert.NotEqual((nint)0, psa);
        try
        {
            Assert.Equal(0, SafeArrayGetLBound(psa, 1, out int l0));
            Assert.Equal(0, SafeArrayGetUBound(psa, 1, out int u0));
            Assert.Equal(0, SafeArrayGetLBound(psa, 2, out int l1));
            Assert.Equal(0, SafeArrayGetUBound(psa, 2, out int u1));
            Assert.Equal(0, l0);
            Assert.Equal(0, l1);
            int cols = u0 - l0 + 1;
            int rows = u1 - l1 + 1;
            var grid = new object?[cols, rows];
            int* idx = stackalloc int[2];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    idx[0] = l0 + c;
                    idx[1] = l1 + r;
                    NativeVariant elem = default;
                    Assert.Equal(0, SafeArrayGetElement(psa, idx, &elem));
                    grid[c, r] = Value(elem);
                }
            }
            return grid;
        }
        finally
        {
            _ = SafeArrayDestroy(psa);
        }
    }

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetLBound(nint psa, uint nDim, out int plLbound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetUBound(nint psa, uint nDim, out int plUbound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetElement(nint psa, int* rgIndices, void* pv);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(nint psa);
}
