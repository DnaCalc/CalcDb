using CalcDb.Evidence;
using CalcDb.Values;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-09 (R-ADO-1, R-WIRE): tests <c>Command.ActiveConnection</c> detachment on <c>Connection.Close</c>,
/// <c>Recordset.Bookmark</c> &amp; <c>CompareBookmarks</c> (server keyset, server static, client static,
/// Filter/Clone/Requery), <c>Record.Open</c> ACE-provider refusals, and <c>VT_DECIMAL</c> zero-sign normalization.
/// Citations:
/// - evidence/r-str-ado-act/ado-session (#50), evidence/r-fix-wire-ado/ado-session-writable (#51)
/// - evidence/r-ado-1b/book-raw-forms, evidence/r-fix-wire-ado/book-position-readback,
///   evidence/r-str-ado-act/ado-view (#49-#50), evidence/r-q2-ado-misc/book-static-exchange
/// - evidence/r-q-ado-misc/record-open-forms (#16, #36, #56), evidence/r-oledb-odbc/stream-record-from-row (#15)
/// - evidence/r-ado-1b/fields-meta-mdb (32[2].1[14].0), evidence/r-str-ado-act/ado-rs-read-close (17[3].1[9].0)
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoSessionBookMetaTests
{
    private const string AccdbFixture = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7"; // r-ado-1b-main.accdb
    private const string MdbFixture = "4a447e7517e51e0ed2b734fd8a20d32ab22e83f427667a4f0e8fb2ff0b544bf7";   // r-ado-1b-mdb.mdb

    private static readonly Guid OwnRecord = new("2CA37905-9986-469E-8E8D-8E14E85E606E");
    private static readonly Guid IidRecordset = new("00001556-0000-0010-8000-00AA006D2EA4");
    private static readonly Guid IidRecord = new("00001562-0000-0010-8000-00AA006D2EA4");
    private static readonly Guid IidOleDbRowset = new("0C733A7C-2A1C-11CE-ADE5-00AA0044773D");

    [Fact]
    public void Command_ActiveConnection_detaches_to_null_on_Connection_Close()
    {
        RunOnSta((store, tempDir) =>
        {
            string dbPath = CopyFixture(store, AccdbFixture, tempDir, "session.accdb");
            nint cn = OpenAceConnection(dbPath, mode: 1);
            nint cmd = New(OwnCommand);
            try
            {
                Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmd, "CommandText", "SELECT ID FROM T ORDER BY ID").Failure);
                nint rs = (nint)Call(cmd, "Execute").Value!;
                Assert.Equal(1, Get(rs, "State"));

                // While open, cmd.ActiveConnection returns the bound Connection.
                nint acBefore = (nint)Get(cmd, "ActiveConnection")!;
                Assert.NotEqual((nint)0, acBefore);
                Release(acBefore);

                // Connection.Close closes dependent Recordsets and detaches cmd.ActiveConnection to null interface pointer
                // (r-str-ado-act/ado-session #50, r-fix-wire-ado/ado-session-writable #51).
                Assert.Null(Call(cn, "Close").Failure);
                Assert.Equal(0, Get(rs, "State"));
                Assert.Equal((nint)0, (nint)Get(cmd, "ActiveConnection")!);
                Assert.Equal(0, Get(cmd, "State"));
                Release(rs);
            }
            finally
            {
                Release(cmd);
                if ((int)Get(cn, "State")! == 1)
                {
                    Call(cn, "Close");
                }
                Release(cn);
            }
        });
    }

    [Fact]
    public void Recordset_Bookmark_and_CompareBookmarks_match_recorded_cursors()
    {
        RunOnSta((store, tempDir) =>
        {
            string dbPath = CopyFixture(store, AccdbFixture, tempDir, "book.accdb");
            nint cn = OpenAceConnection(dbPath, mode: 3);
            try
            {
                // 1. Server Keyset (loc=2, type=1, lock=1): physical RowId bookmarks 512, 513, 518
                // (r-ado-1b/book-raw-forms b2, r-fix-wire-ado/book-position-readback b2).
                nint b2 = New(OwnRecordset);
                Assert.Null(Put(b2, "CursorLocation", 2).Failure);
                Assert.Null(Call(b2, "Open", "SELECT ID, S FROM T ORDER BY ID", new Dispatch(cn), 1, 1, 1).Failure);
                Assert.Equal(true, Call(b2, "Supports", 8192).Value);
                Assert.Equal(512.0, Get(b2, "Bookmark"));
                Assert.Null(Call(b2, "MoveNext").Failure);
                Assert.Equal(513.0, Get(b2, "Bookmark"));
                Assert.Null(Call(b2, "MoveLast").Failure);
                Assert.Equal(518.0, Get(b2, "Bookmark"));
                Assert.Null(Call(b2, "MoveNext").Failure);
                AssertError(TryGet(b2, "Bookmark"), unchecked((int)0x800A0BCD), "ADODB.Recordset", expectedHelpContext: 1240645u, expectedInfoGuid: IidRecordset);
                Assert.Null(Call(b2, "MoveFirst").Failure);
                Assert.Null(Call(b2, "MovePrevious").Failure);
                AssertError(TryGet(b2, "Bookmark"), unchecked((int)0x800A0BCD), "ADODB.Recordset", expectedHelpContext: 1240645u, expectedInfoGuid: IidRecordset);
                Assert.Null(Call(b2, "MoveFirst").Failure);

                // Invalid types -> 0x800A0BB9; invalid R8 or 4-byte array on keyset -> 0x80040E0E (3159, IID_IRowset).
                AssertError(Put(b2, "Bookmark", 2), unchecked((int)0x800A0BB9), "ADODB.Recordset", expectedHelpContext: 1240641u, expectedInfoGuid: IidRecordset);
                AssertError(Put(b2, "Bookmark", "x"), unchecked((int)0x800A0BB9), "ADODB.Recordset", expectedHelpContext: 1240641u, expectedInfoGuid: IidRecordset);
                AssertError(Put(b2, "Bookmark", DBNull.Value), unchecked((int)0x800A0BB9), "ADODB.Recordset", expectedHelpContext: 1240641u, expectedInfoGuid: IidRecordset);
                AssertError(Put(b2, "Bookmark", 2.0), unchecked((int)0x80040E0E), "Microsoft Access Database Engine", expectedHelpContext: 5003159u, expectedInfoGuid: IidOleDbRowset);
                AssertError(Put(b2, "Bookmark", new byte[] { 1, 0, 0, 0 }), unchecked((int)0x80040E0E), "Microsoft Access Database Engine", expectedHelpContext: 5003159u, expectedInfoGuid: IidOleDbRowset);
                Assert.Equal(1, Get(b2, "AbsolutePosition"));
                Assert.Null(Call(b2, "Close").Failure);
                Release(b2);

                // 2. Server Static without RecordCount (r-ado-1b/book-raw-forms b3): lazy bookmarks 1, 2, 3.
                nint b3 = New(OwnRecordset);
                Assert.Null(Put(b3, "CursorLocation", 2).Failure);
                Assert.Null(Call(b3, "Open", "SELECT ID, S FROM T ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                Assert.Equal(1.0, Get(b3, "Bookmark"));
                Assert.Null(Call(b3, "MoveNext").Failure);
                Assert.Equal(2.0, Get(b3, "Bookmark"));
                Assert.Null(Call(b3, "MoveLast").Failure);
                Assert.Equal(3.0, Get(b3, "Bookmark"));
                Assert.Null(Put(b3, "Bookmark", new byte[] { 1, 0, 0, 0 }).Failure);
                Assert.Equal(1, Get(b3, "AbsolutePosition"));
                Assert.Null(Put(b3, "Bookmark", 2.0).Failure);
                Assert.Equal(2, Get(b3, "AbsolutePosition"));
                // Unvisited bookmark 5 -> 0x80040E0E, CompareBookmarks(1, 5) -> 3 (adCompareNotComparable), CompareBookmarks(5, 5) -> 1.
                AssertError(Put(b3, "Bookmark", 5.0), unchecked((int)0x80040E0E), "Microsoft Access Database Engine", expectedHelpContext: 5003159u, expectedInfoGuid: IidOleDbRowset);
                Assert.Equal(3, Call(b3, "CompareBookmarks", 1.0, 5.0).Value);
                Assert.Equal(1, Call(b3, "CompareBookmarks", 5.0, 5.0).Value);
                Assert.Null(Call(b3, "Close").Failure);
                Release(b3);

                // 3. Server Static WITH RecordCount first (r-fix-wire-ado/book-position-readback b3): bookmarks 1, 2, 7.
                nint b3Count = New(OwnRecordset);
                Assert.Null(Put(b3Count, "CursorLocation", 2).Failure);
                Assert.Null(Call(b3Count, "Open", "SELECT ID, S FROM T ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                Assert.Equal(7, Get(b3Count, "RecordCount"));
                Assert.Equal(1.0, Get(b3Count, "Bookmark"));
                Assert.Null(Call(b3Count, "MoveNext").Failure);
                Assert.Equal(2.0, Get(b3Count, "Bookmark"));
                Assert.Null(Call(b3Count, "MoveLast").Failure);
                Assert.Equal(7.0, Get(b3Count, "Bookmark"));
                Assert.Null(Call(b3Count, "Close").Failure);
                Release(b3Count);

                // 4. Client Static with Filter + Clone (r-str-ado-act/ado-view #49-#50, r-ado-1b/book-raw-forms b4, r-q2-ado-misc/book-static-exchange).
                nint b4 = New(OwnRecordset);
                Assert.Null(Put(b4, "CursorLocation", 3).Failure);
                Assert.Null(Call(b4, "Open", "SELECT ID, Code, S FROM T ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                Assert.Equal(1.0, Get(b4, "Bookmark"));
                Assert.Null(Put(b4, "Bookmark", 2.0).Failure);
                Assert.Equal(2, Get(b4, "AbsolutePosition"));
                Assert.Null(Put(b4, "Bookmark", new byte[] { 1, 0, 0, 0 }).Failure);
                Assert.Equal(1, Get(b4, "AbsolutePosition"));
                Assert.Equal(0, Call(b4, "CompareBookmarks", 1.0, 3.0).Value);
                Assert.Equal(1, Call(b4, "CompareBookmarks", 3.0, 3.0).Value);
                Assert.Equal(2, Call(b4, "CompareBookmarks", 3.0, 1.0).Value);

                nint rc = (nint)Call(b4, "Clone").Value!;
                Assert.Null(Put(b4, "Filter", "ID = 2").Failure);
                Assert.Equal(1, Get(b4, "RecordCount"));
                Assert.Equal(2.0, Get(b4, "Bookmark")); // base row 2 keeps bookmark 2 under Filter
                Assert.Equal(1.0, Get(rc, "Bookmark")); // clone starts at row 1
                Assert.Null(Call(rc, "Close").Failure);
                Release(rc);
                Assert.Null(Call(b4, "Close").Failure);
                Release(b4);
            }
            finally
            {
                if ((int)Get(cn, "State")! == 1)
                {
                    Call(cn, "Close");
                }
                Release(cn);
            }
        });
    }

    [Fact]
    public void Record_Open_refuses_ACE_sources_with_recorded_errors()
    {
        RunOnSta((store, tempDir) =>
        {
            string dbPath = CopyFixture(store, AccdbFixture, tempDir, "rec.accdb");
            nint cn = OpenAceConnection(dbPath, mode: 1);
            nint cmd = New(OwnCommand);
            nint rs = New(OwnRecordset);
            nint rect = New(OwnRecord);
            nint recq = New(OwnRecord);
            nint recc = New(OwnRecord);
            nint recr = New(OwnRecord);
            try
            {
                Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmd, "CommandText", "SELECT * FROM T WHERE ID = 1").Failure);
                Assert.Null(Call(rs, "Open", "SELECT * FROM T WHERE ID = 1", new Dispatch(cn), 0, 1, 1).Failure);

                // r-q-ado-misc/record-open-forms #16 (table name), #36 (SQL), #56 (Command).
                AssertError(Call(rect, "Open", "T", new Dispatch(cn)), unchecked((int)0x800A0E7D), "ADODB.Record", expectedHelpContext: 1240658u, expectedInfoGuid: IidRecord);
                Assert.Equal(0, Get(rect, "State"));
                AssertError(Call(recq, "Open", "SELECT * FROM T", new Dispatch(cn)), unchecked((int)0x800A0E7D), "ADODB.Record", expectedHelpContext: 1240658u, expectedInfoGuid: IidRecord);
                Assert.Equal(0, Get(recq, "State"));
                AssertError(Call(recc, "Open", new Dispatch(cmd), new Dispatch(cn)), unchecked((int)0x800A0CB3), "ADODB.Record", expectedHelpContext: 1240648u, expectedInfoGuid: IidRecord);
                Assert.Equal(0, Get(recc, "State"));

                // r-oledb-odbc/stream-record-from-row #15 (open Recordset), #19 (Mode=1).
                AssertError(Call(recr, "Open", new Dispatch(rs)), unchecked((int)0x800A0CB3), "ADODB.Record", expectedHelpContext: 1240648u, expectedInfoGuid: IidRecord);
                Assert.Equal(0, Get(recr, "State"));
                Assert.Equal(1, Get(recr, "Mode"));

                // Connection.Errors remains empty after ADO-level Record.Open refusals.
                nint errs = (nint)Get(cn, "Errors")!;
                Assert.Equal(0, Get(errs, "Count"));
                Release(errs);
            }
            finally
            {
                Release(recr);
                Release(recc);
                Release(recq);
                Release(rect);
                if ((int)Get(rs, "State")! == 1)
                {
                    Call(rs, "Close");
                }
                Release(rs);
                Release(cmd);
                if ((int)Get(cn, "State")! == 1)
                {
                    Call(cn, "Close");
                }
                Release(cn);
            }
        });
    }

    [Fact]
    public void Decimal_negative_zero_normalizes_to_positive_sign_in_MDB()
    {
        RunOnSta((store, tempDir) =>
        {
            string mdbPath = CopyFixture(store, MdbFixture, tempDir, "dec.mdb");
            nint cn = OpenAceConnection(mdbPath, mode: 1);
            nint rs = New(OwnRecordset);
            try
            {
                // 1. Server cursor preserves stored -0 sign (0x80) (r-ado-1b/fields-meta-mdb 21[2].1[14].0).
                nint ms = New(OwnRecordset);
                Assert.Null(Put(ms, "CursorLocation", 2).Failure);
                Assert.Null(Call(ms, "Open", "SELECT * FROM T ORDER BY ID", new Dispatch(cn), 0, 1, 1).Failure);
                Assert.Null(Call(ms, "MoveNext").Failure);
                Assert.Null(Call(ms, "MoveNext").Failure);
                nint msFlds = (nint)Get(ms, "Fields")!;
                nint msFld = (nint)Get(msFlds, "Item", 14)!;
                Outcome msVal = TryGet(msFld, "Value");
                Assert.Null(msVal.Failure);
                int[] msBits = decimal.GetBits(Assert.IsType<decimal>(msVal.Value));
                Assert.Equal(0, msBits[0]);
                Assert.Equal(0, msBits[1]);
                Assert.Equal(0, msBits[2]);
                Assert.Equal(int.MinValue, msBits[3] & int.MinValue); // server cursor preserves 0x80 (-0)
                Release(msFld);
                Release(msFlds);
                Assert.Null(Call(ms, "Close").Failure);
                Release(ms);

                // 2. Client cursor normalizes -0 to +0 (sign 0x00) (r-ado-1b/fields-meta-mdb 32[2].1[14].0).
                Assert.Null(Put(rs, "CursorLocation", 3).Failure);
                Assert.Null(Call(rs, "Open", "SELECT * FROM T ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                Assert.Null(Call(rs, "MoveNext").Failure);
                Assert.Null(Call(rs, "MoveNext").Failure);
                nint flds = (nint)Get(rs, "Fields")!;
                nint fld = (nint)Get(flds, "Item", 14)!;
                Outcome val = TryGet(fld, "Value");
                Assert.Null(val.Failure);
                decimal dv = Assert.IsType<decimal>(val.Value);
                int[] bits = decimal.GetBits(dv);
                Assert.Equal(0, bits[0]);
                Assert.Equal(0, bits[1]);
                Assert.Equal(0, bits[2]);
                Assert.Equal(0, bits[3] & int.MinValue); // sign 0x00, not 0x80 (-0)
                Release(fld);
                Release(flds);
            }
            finally
            {
                if ((int)Get(rs, "State")! == 1)
                {
                    Call(rs, "Close");
                }
                Release(rs);
                if ((int)Get(cn, "State")! == 1)
                {
                    Call(cn, "Close");
                }
                Release(cn);
            }
        });
    }

    private static void RunOnSta(Action<ArtifactStore, string> body)
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-sbm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));
                body(store, tempDir);
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
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
