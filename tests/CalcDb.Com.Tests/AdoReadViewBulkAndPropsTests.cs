using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-09/native-read-query (7k56 read-side stretch):
/// exercises the published CalcDb.dll under CalcDb.Own.manifest through raw IDispatch on an STA
/// against the retained ArtifactStore fixtures for each evidence record.
/// Covers read-only / closed / BOF Field.Value and edit semantics, Recordset.Filter, Recordset.Sort,
/// Recordset.Find, Recordset.GetRows, Recordset.GetString, Recordset/Command/Field Properties inventories
/// and invalidation, Command.ActiveConnection/CommandTimeout/Parameters/Recordset.Open(Command),
/// Decimal expression Field metadata, and named refusals for unrecorded behaviors.
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoReadViewBulkAndPropsTests
{
    private static readonly Guid OwnEngine = new("D44A3A3E-4FD9-4426-A7B7-8351E8421EBF");
    private static readonly Guid OwnConnection = new("B29F5595-8BF1-4AA8-9397-1F2F10F3A101");
    private static readonly Guid OwnCommand = new("7AF35BF5-1686-4FB7-AA3E-9DC4EF68ECD5");
    private static readonly Guid OwnRecordset = new("892D19AC-84CA-4F73-8A3E-00B7C066245A");
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";

    // Retained ArtifactStore fixtures for the evidence families tested below.
    private const string Ado1bMainFixture = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7"; // r-ado-1b-main.accdb
    private const string QMiscMainFixture = "8ce44b486c3496e0903cdeae890bbe83fc1709ca82f353ca7c0175c34a91fbea"; // r-q-ado-misc-main.accdb
    private const string WireDaoFixture = "76ceb0c44e385678689da7e1bdb328c9c25230c28e1c18475ca13e7b01db85b7";   // r-wire-dao.accdb
    private const string BulkAdo4Fixture = "98b792c86d8ea840c1028fd0a067ef6b9917e4dfbeb18ee78cb1acb4309cf310";  // r-str-ado-act-ado4-accdb.accdb
    private const string StrAdoFixture = "22b81465269ae9b0dd729590d416c3778eb3385aaa2721a160665537a2cbc5a7";    // r-str-ado-act-str-accdb.accdb
    private const string DecimalFixture = "f0e5a1384126593d7cd78696de7102db28c8d805a628e7789fe3b9ec4fa81cb3";   // r-fix-wire-ado-ado5.accdb

    [Fact]
    public void CalcDb_ADO_read_view_bulk_properties_command_and_decimal_match_retained_ACE_records()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-read-7k56-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string ado1bPath = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-main.accdb");
        string qMiscPath = CopyFixture(store, QMiscMainFixture, tempDir, "r-q-ado-misc-main.accdb");
        string wireDaoPath = CopyFixture(store, WireDaoFixture, tempDir, "r-wire-dao.accdb");
        string bulkAdo4Path = CopyFixture(store, BulkAdo4Fixture, tempDir, "r-str-ado-act-ado4-accdb.accdb");
        string strAdoPath = CopyFixture(store, StrAdoFixture, tempDir, "r-str-ado-act-str-accdb.accdb");
        string decPath = CopyFixture(store, DecimalFixture, tempDir, "r-fix-wire-ado-ado5.accdb");
        string exactPrefixPath = Path.Combine(tempDir, "exact-prefix.accdb");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));
                CreateExactPrefixDatabase(exactPrefixPath);

                ReadOnlyClosedBofAndWritableFieldPutSemantics(ado1bPath);
                FilterOperatorsLiteralsConstantsAndServerCursors(ado1bPath, qMiscPath, exactPrefixPath);
                SortGrammarClientOrderingAndTypeRefusals(ado1bPath, exactPrefixPath);
                FindOnClientServerAndViewRefusals(ado1bPath);
                GetRowsAndGetStringAcrossRetainedFixtures(bulkAdo4Path, strAdoPath, wireDaoPath, ado1bPath, decPath);
                PropertiesInventoriesAndInvalidation(ado1bPath, qMiscPath);
                CommandActiveConnectionTimeoutParametersAndRecordsetSource(ado1bPath);
                DecimalExpressionMetadata(decPath);

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

    private static string CopyFixture(ArtifactStore store, string sha256, string dir, string fileName)
    {
        string path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, store.ReadAll(sha256));
        return path;
    }

    private static void CreateExactPrefixDatabase(string dbPath)
    {
        nint eng = New(OwnEngine);
        Outcome created = Call(eng, "CreateDatabase", dbPath, ";LANGID=0x0409;CP=1252;COUNTRY=0", 128);
        Assert.Null(created.Failure);
        nint db = (nint)created.Value!;
        Assert.Null(Call(db, "Execute", "CREATE TABLE P (ID COUNTER, T TEXT(20))").Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO P (T) VALUES ('alph')").Failure);
        Assert.Null(Call(db, "Execute", "CREATE TABLE BinT (B BINARY(4), ID COUNTER)").Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO BinT (B) VALUES (NULL)").Failure);
        Assert.Null(Call(db, "Close").Failure);
        Release(db);
        Release(eng);
    }

    private static nint OpenAceConnection(string dbPath, int? mode = null)
    {
        nint cn = New(OwnConnection);
        Assert.Null(Put(cn, "Provider", Ace).Failure);
        nint props = (nint)Get(cn, "Properties")!;
        nint ds = (nint)Get(props, "Item", "Data Source")!;
        Assert.Null(Put(ds, "Value", dbPath).Failure);
        Release(ds);
        Release(props);
        if (mode is { } m)
        {
            Assert.Null(Put(cn, "Mode", m).Failure);
        }
        Assert.Null(Call(cn, "Open").Failure);
        Assert.Equal(1, Get(cn, "State"));
        return cn;
    }

    private static int GetErrorsCount(nint cn)
    {
        nint errs = (nint)Get(cn, "Errors")!;
        int count = (int)Get(errs, "Count")!;
        Release(errs);
        return count;
    }

    private static void ReadOnlyClosedBofAndWritableFieldPutSemantics(string ado1bPath)
    {
        // Mode=Read downgrades server Keyset+Optimistic to ForwardOnly+ReadOnly -> 0x800A0CB3 on edit, S_OK on CancelUpdate
        // (evidence/r-ado-1b/rs-readonly-mode-edit).
        nint roCn = OpenAceConnection(ado1bPath, mode: 1);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Call(rs, "Open", "SELECT ID, S FROM T ORDER BY ID", new Dispatch(roCn), 1, 3, 1).Failure);
            Assert.Equal(0, Get(rs, "CursorType"));
            Assert.Equal(1, Get(rs, "LockType"));

            nint flds = (nint)Get(rs, "Fields")!;
            nint fS = (nint)Get(flds, "Item", "S")!;
            Outcome putVal = Put(fS, "Value", "ro-ka");
            Assert.Equal(unchecked((int)0x800A0CB3), putVal.Failure!.Value.Scode);
            Assert.Equal(1240648u, putVal.Failure!.Value.HelpContext);
            Assert.Equal("ADODB.Field", putVal.Failure!.Value.Source);

            Outcome upd = Call(rs, "Update");
            Assert.Equal(unchecked((int)0x800A0CB3), upd.Failure!.Value.Scode);
            Assert.Equal("ADODB.Recordset", upd.Failure!.Value.Source);

            Assert.Null(Call(rs, "CancelUpdate").Failure);
            Assert.Equal(unchecked((int)0x800A0CB3), Call(rs, "AddNew").Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0CB3), Call(rs, "Delete").Failure!.Value.Scode);
            Assert.Equal(0, GetErrorsCount(roCn));

            // Advance to EOF: Field.Value put at BOF/EOF is unrecorded and refused by name.
            while (!(bool)Get(rs, "EOF")!)
            {
                Assert.Null(Call(rs, "MoveNext").Failure);
            }
            Assert.Equal(HResults.E_NOTIMPL, Put(fS, "Value", "at-eof").Failure!.Value.Scode);

            // Close Recordset:
            // - held Field wrapper (fS) is invalidated -> 0x800A0D5C (evidence/r-str-ado-act/ado-rs-read-close #281)
            // - fresh Field from retained rs.Fields on closed Recordset -> 0x800A0E78 ObjectClosed (evidence/r-str-ado-act/ado-rs-read-close #284-285).
            Assert.Null(Call(rs, "Close").Failure);
            Assert.Equal(unchecked((int)0x800A0D5C), Put(fS, "Value", "held-after-close").Failure!.Value.Scode);
            nint freshF = (nint)Get(flds, "Item", "S")!;
            Outcome closedPut = Put(freshF, "Value", "closed-put");
            Assert.Equal(unchecked((int)0x800A0E78), closedPut.Failure!.Value.Scode);
            Assert.Equal("ADODB.Field", closedPut.Failure!.Value.Source);

            Release(freshF);
            Release(fS);
            Release(flds);
            Release(rs);
        }
        finally
        {
            Assert.Null(Call(roCn, "Close").Failure);
            Release(roCn);
        }

        // Writable client batch-optimistic cursor stages edits locally without modifying disk until UpdateBatch
        // (W-09/wzq4, r-q48-ado-batch-edits/client-deferred-vs-immediate-accdb).
        nint rwCn = OpenAceConnection(ado1bPath);
        try
        {
            nint kc = New(OwnRecordset);
            Assert.Null(Put(kc, "CursorLocation", 3).Failure);
            Assert.Null(Call(kc, "Open", "SELECT ID, S FROM T ORDER BY ID", new Dispatch(rwCn), 3, 4, 1).Failure);
            Assert.Equal(4, Get(kc, "LockType"));

            nint flds = (nint)Get(kc, "Fields")!;
            nint fS = (nint)Get(flds, "Item", "S")!;
            object? origVal = Get(fS, "Value");
            Assert.Null(Put(fS, "Value", "batch").Failure);
            Assert.Equal(1, Get(kc, "EditMode"));
            Assert.Equal(2, Get(kc, "Status"));
            Assert.Null(Call(kc, "Update").Failure);
            Assert.Equal(1, Get(kc, "EditMode"));
            Assert.Equal(2, Get(kc, "Status"));
            Assert.Equal("batch", Get(fS, "Value"));
            Assert.Equal(origVal, Get(fS, "OriginalValue"));
            Assert.Null(Call(kc, "CancelBatch", 3).Failure);
            Assert.Equal(8, Get(kc, "Status"));
            Assert.Equal(origVal, Get(fS, "Value"));
            Release(fS);
            Release(flds);

            Assert.Null(Call(kc, "Close").Failure);
            Release(kc);
        }
        finally
        {
            Assert.Null(Call(rwCn, "Close").Failure);
            Release(rwCn);
        }
    }

    private static void FilterOperatorsLiteralsConstantsAndServerCursors(string ado1bPath, string qMiscPath, string exactPrefixPath)
    {
        nint cn = OpenAceConnection(ado1bPath, mode: 1);
        try
        {
            // Never-opened Recordset Filter/Sort storage (evidence/r-str-ado-act/ado-view).
            nint neverOpened = New(OwnRecordset);
            Assert.Equal(0, Get(neverOpened, "Filter"));
            Assert.Null(Get(neverOpened, "Sort"));
            Assert.Null(Put(neverOpened, "Filter", "L = 20").Failure);
            Assert.Equal("L = 20", Get(neverOpened, "Filter"));
            Assert.Null(Put(neverOpened, "Filter", 0).Failure);
            Assert.Equal(0, Get(neverOpened, "Filter"));
            Assert.Null(Put(neverOpened, "Sort", "ID DESC").Failure);
            Assert.Equal("ID DESC", Get(neverOpened, "Sort"));
            Assert.Null(Put(neverOpened, "Sort", "").Failure);
            Assert.Null(Get(neverOpened, "Sort"));
            // Never-opened Sort(VT_NULL) is unrecorded and refused by name.
            Assert.Equal(HResults.E_NOTIMPL, Put(neverOpened, "Sort", DBNull.Value).Failure!.Value.Scode);
            Release(neverOpened);

            // Client static read-only cursor Filter grammar, literals, wildcards, errors
            // (evidence/r-ado-1b/view-filter-operators, view-filter-literals, view-filter-constants).
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 3).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, L, D, C, Dt, S, Y FROM T ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
            Assert.Equal(7, Get(rs, "RecordCount"));
            Assert.Equal(0, Get(rs, "Filter"));

            Assert.Null(Put(rs, "Filter", "L > 15").Failure);
            Assert.Equal(4, Get(rs, "RecordCount"));
            Assert.Equal("L > 15", Get(rs, "Filter"));

            Assert.Null(Put(rs, "Filter", "L = 20 OR L = 30").Failure);
            Assert.Equal(3, Get(rs, "RecordCount"));

            Assert.Null(Put(rs, "Filter", "L >= 20 AND S = 'alpha'").Failure);
            Assert.Equal(1, Get(rs, "RecordCount"));

            Assert.Null(Put(rs, "Filter", "L = NULL").Failure);
            Assert.Equal(1, Get(rs, "RecordCount"));

            Assert.Null(Put(rs, "Filter", "L <> NULL").Failure);
            Assert.Equal(6, Get(rs, "RecordCount"));

            Assert.Null(Put(rs, "Filter", "S = ''").Failure);
            Assert.Equal(1, Get(rs, "RecordCount"));

            Assert.Null(Put(rs, "Filter", "S = 'it''s'").Failure);
            Assert.Equal(1, Get(rs, "RecordCount"));

            // evidence/r-ado-1b/view-filter-literals #155-157: S = "alpha" keeps quotes as literal text -> 0 rows.
            Assert.Null(Put(rs, "Filter", "S = \"alpha\"").Failure);
            Assert.Equal(0, Get(rs, "RecordCount"));

            // Date literals parsed via shared ScalarLiterals.TryDate (evidence/r-ado-1b/view-filter-literals #160-186).
            Assert.Null(Put(rs, "Filter", "Dt = #2024-01-02#").Failure);
            Assert.Equal(1, Get(rs, "RecordCount"));

            Assert.Null(Put(rs, "Filter", "Dt = #1/2/2024#").Failure);
            Assert.Equal(1, Get(rs, "RecordCount"));

            Assert.Null(Put(rs, "Filter", "Dt > #2024-03-01#").Failure);
            Assert.True((int)Get(rs, "RecordCount")! >= 1);

            // Boolean equality with True, 1, -1.
            Assert.Null(Put(rs, "Filter", "Y = True").Failure);
            int trueCount = (int)Get(rs, "RecordCount")!;
            Assert.True(trueCount >= 1);
            Assert.Null(Put(rs, "Filter", "Y = -1").Failure);
            Assert.Equal(trueCount, Get(rs, "RecordCount"));

            // LIKE contains, exact, prefix, and leading-only wildcard failure (evidence/r-ado-1b/view-filter-operators #230-303).
            Assert.Null(Put(rs, "Filter", "S LIKE '*ha*'").Failure);
            Assert.Equal(2, Get(rs, "RecordCount"));

            Assert.Null(Put(rs, "Filter", "S LIKE 'alpha'").Failure);
            Assert.Equal(2, Get(rs, "RecordCount"));

            Assert.Equal(unchecked((int)0x800A0BB9), Put(rs, "Filter", "S LIKE '%a'").Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0BB9), Put(rs, "Filter", "S LIKE '*a'").Failure!.Value.Scode);

            // Syntax errors -> 0x800A0BB9; unknown column -> 0x800A0CC1 (evidence/r-ado-1b/view-filter-operators).
            foreach (string badSyntax in new[] { "L IS NULL", "(L = 20 OR L = 30) AND S <> 'alpha'", "NOT L = 20", "L IN (10, 20)", "L == 10", "UPPER(S) = 'ALPHA'" })
            {
                Assert.Equal(unchecked((int)0x800A0BB9), Put(rs, "Filter", badSyntax).Failure!.Value.Scode);
            }
            Assert.Equal(unchecked((int)0x800A0CC1), Put(rs, "Filter", "NoSuchCol = 1").Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0CC1), Put(rs, "Filter", "T.S = 'alpha'").Failure!.Value.Scode);

            // Unrecorded Filter cases refused by name (E_NOTIMPL):
            // unquoted literal, unquoted LIKE RHS, wildcard-only LIKE, LIKE on non-text, unparseable literal,
            // ordering against NULL, Bool ordering and False/0.
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Filter", "S = alpha").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Filter", "S LIKE alpha").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Filter", "S LIKE '*'").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Filter", "L LIKE '1*'").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Filter", "L = 'abc'").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Filter", "L < NULL").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Filter", "Y > True").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Filter", "Y = False").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Filter", "Y = 0").Failure!.Value.Scode);

            // Reset with "" -> reads back vt3:0 (evidence/r-ado-1b/view-filter-constants #103-104).
            Assert.Null(Put(rs, "Filter", "").Failure);
            Assert.Equal(0, Get(rs, "Filter"));
            Assert.Equal(7, Get(rs, "RecordCount"));

            // Invalid numeric / non-string Filter values -> 0x800A0BB9; edit-status constants -> E_NOTIMPL.
            Assert.Equal(unchecked((int)0x800A0BB9), Put(rs, "Filter", 99).Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0BB9), Put(rs, "Filter", -1).Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0BB9), Put(rs, "Filter", DBNull.Value).Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Filter", 1).Failure!.Value.Scode);

            // Combining Filter + Sort is unrecorded -> refused by name (both orders).
            Assert.Null(Put(rs, "Filter", "L = 20").Failure);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Sort", "ID DESC").Failure!.Value.Scode);
            Assert.Null(Put(rs, "Filter", 0).Failure);
            Assert.Null(Put(rs, "Sort", "ID DESC").Failure);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Filter", "L = 20").Failure!.Value.Scode);
            Assert.Null(Put(rs, "Sort", "").Failure);

            // After Close(), Filter/Sort get and put are unrecorded -> refused by name.
            Assert.Null(Call(rs, "Close").Failure);
            Assert.Equal(HResults.E_NOTIMPL, TryGet(rs, "Filter").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Filter", "L = 20").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, TryGet(rs, "Sort").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Sort", "ID ASC").Failure!.Value.Scode);
            Release(rs);

            // Server cursors (evidence/r-ado-1b/view-sort-server): Sort -> 0x800A0CB3, Sort(VT_NULL) -> E_NOTIMPL, Filter works.
            foreach ((int cur, int expectedCount) in new[] { (0, -1), (1, 2), (3, 2), (2, -1) })
            {
                nint srv = New(OwnRecordset);
                Assert.Null(Call(srv, "Open", "SELECT ID, L FROM T ORDER BY ID", new Dispatch(cn), cur, 1, 1).Failure);
                Outcome sortOut = Put(srv, "Sort", "L ASC");
                Assert.Equal(unchecked((int)0x800A0CB3), sortOut.Failure!.Value.Scode);
                Assert.Contains("sorting or filtering", sortOut.Failure!.Value.Description);
                Assert.Equal(HResults.E_NOTIMPL, Put(srv, "Sort", DBNull.Value).Failure!.Value.Scode);

                Assert.Null(Put(srv, "Filter", "L = 20").Failure);
                Assert.Equal(expectedCount, Get(srv, "RecordCount"));
                Assert.Equal(false, Get(srv, "EOF"));
                int rows = 0;
                while (!(bool)Get(srv, "EOF")!)
                {
                    rows++;
                    Assert.Null(Call(srv, "MoveNext").Failure);
                }
                Assert.Equal(2, rows);
                Assert.Null(Call(srv, "Close").Failure);
                Release(srv);
            }
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }

        // Trailing '_' wildcard on retained r-q-ado-misc-main.accdb (evidence/r-q-ado-misc/view-like-percent-sort-clear #107-123).
        nint qCn = OpenAceConnection(qMiscPath, mode: 1);
        try
        {
            nint qRs = New(OwnRecordset);
            Assert.Null(Put(qRs, "CursorLocation", 3).Failure);
            Assert.Null(Call(qRs, "Open", "SELECT ID, Code, T, L FROM K ORDER BY ID", new Dispatch(qCn), 3, 1, 1).Failure);
            Assert.Null(Put(qRs, "Filter", "T LIKE 'alph_'").Failure);
            Assert.Equal(2, Get(qRs, "RecordCount"));
            Assert.Null(Put(qRs, "Filter", 0).Failure);
            Assert.Null(Put(qRs, "Filter", "Code LIKE 'a*'").Failure);
            Assert.Equal(2, Get(qRs, "RecordCount"));
            Assert.Null(Call(qRs, "Close").Failure);
            Release(qRs);
        }
        finally
        {
            Assert.Null(Call(qCn, "Close").Failure);
            Release(qCn);
        }

        // Trailing '_' against an exact-prefix row ('alph' vs 'alph_') is unrecorded -> refused by name.
        nint pCn = OpenAceConnection(exactPrefixPath, mode: 1);
        try
        {
            nint pRs = New(OwnRecordset);
            Assert.Null(Put(pRs, "CursorLocation", 3).Failure);
            Assert.Null(Call(pRs, "Open", "SELECT ID, T FROM P ORDER BY ID", new Dispatch(pCn), 3, 1, 1).Failure);
            Assert.Equal(HResults.E_NOTIMPL, Put(pRs, "Filter", "T LIKE 'alph_'").Failure!.Value.Scode);
            Assert.Null(Call(pRs, "Close").Failure);
            Release(pRs);
        }
        finally
        {
            Assert.Null(Call(pCn, "Close").Failure);
            Release(pCn);
        }
    }

    private static void SortGrammarClientOrderingAndTypeRefusals(string ado1bPath, string exactPrefixPath)
    {
        nint cn = OpenAceConnection(ado1bPath, mode: 1);
        try
        {
            // evidence/r-ado-1b/view-sort-grammar #16 opens via adCmdText SQL projecting LONGBINARY column B;
            // exercise Sort grammar, ordering, MEMO refusal, LONGBINARY refusal, and argument checks on that exact SQL open.
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 3).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, L, S, Dt, Y, M, B FROM T", new Dispatch(cn), 3, 1, 1).Failure);

            // NULL sorts first in ASC, last in DESC; tie-break reverses on DESC (evidence/r-ado-1b/view-sort-grammar #18-208).
            Assert.Null(Put(rs, "Sort", "L ASC").Failure);
            Assert.Equal("L ASC", Get(rs, "Sort"));
            Assert.Same(DBNull.Value, ReadObjField(rs, "L")); // ID=5, L=NULL first
            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Equal(5, ReadIntField(rs, "L"));           // ID=7, L=5
            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Equal(10, ReadIntField(rs, "L"));          // ID=1, L=10

            Assert.Null(Put(rs, "Sort", "L DESC").Failure);
            Assert.Equal(50, ReadIntField(rs, "L"));
            Assert.Null(Call(rs, "MoveLast").Failure);
            Assert.Same(DBNull.Value, ReadObjField(rs, "L")); // NULL last

            // Bracketed column and trailing comma allowed (evidence/r-ado-1b/view-sort-grammar #683, #778).
            Assert.Null(Put(rs, "Sort", "[S] DESC, ").Failure);
            Assert.Equal("[S] DESC, ", Get(rs, "Sort"));

            // Boolean: False (0) sorts before True (-1) in ASC (evidence/r-ado-1b/view-sort-grammar #1348).
            Assert.Null(Put(rs, "Sort", "Y ASC").Failure);
            Assert.Equal(false, ReadObjField(rs, "Y"));

            // MEMO (M) and LONGBINARY (B) fail 0x80040E60 from Microsoft Cursor Engine and leave Sort unchanged
            // (evidence/r-ado-1b/view-sort-grammar #1443, #1538).
            Outcome memoSort = Put(rs, "Sort", "M");
            Assert.Equal(unchecked((int)0x80040E60), memoSort.Failure!.Value.Scode);
            Assert.Equal("Microsoft Cursor Engine", memoSort.Failure!.Value.Source);
            Assert.Equal(Guid.Empty, memoSort.Failure!.Value.ErrorInfoGuid);
            Assert.Equal("Y ASC", Get(rs, "Sort"));

            Outcome sqlBinSort = Put(rs, "Sort", "B");
            Assert.Equal(unchecked((int)0x80040E60), sqlBinSort.Failure!.Value.Scode);
            Assert.Equal("Microsoft Cursor Engine", sqlBinSort.Failure!.Value.Source);
            Assert.Equal(Guid.Empty, sqlBinSort.Failure!.Value.ErrorInfoGuid);
            Assert.Equal("Y ASC", Get(rs, "Sort"));

            // Bad column -> 0x800A0CC1; bad syntax -> 0x800A0BB9; whitespace-only Sort -> E_NOTIMPL.
            Assert.Equal(unchecked((int)0x800A0CC1), Put(rs, "Sort", "NoSuch").Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0BB9), Put(rs, "Sort", "L DESC DESC").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Sort", "   ").Failure!.Value.Scode);

            // Open client Sort(VT_NULL) -> 0x80020005 + Connection.Errors(0) Provider Type mismatch
            // (evidence/r-ado-1b/view-sort-grammar #1728); other non-string conversion failure -> E_NOTIMPL.
            Outcome nullSort = Put(rs, "Sort", DBNull.Value);
            Assert.Equal(unchecked((int)0x80020005), nullSort.Failure!.Value.Scode);
            Assert.Equal("Provider", nullSort.Failure!.Value.Source);
            Assert.Equal(1, GetErrorsCount(cn));
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "Sort", new Dispatch(0)).Failure!.Value.Scode);

            // Clear Sort with "" (evidence/r-ado-1b/view-sort-grammar #1633).
            Assert.Null(Put(rs, "Sort", "").Failure);
            Assert.Equal(1, ReadIntField(rs, "ID"));

            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);

            // Direct TableRowSource (adCmdTable=2) unit check for AdoSortSpec's adLongVarBinary (205) -> 0x80040E60 check
            // (distinct from view-sort-grammar #16's SQL open above).
            nint tblT = New(OwnRecordset);
            Assert.Null(Put(tblT, "CursorLocation", 3).Failure);
            Assert.Null(Call(tblT, "Open", "T", new Dispatch(cn), 3, 1, 2).Failure);
            Outcome binSort = Put(tblT, "Sort", "B");
            Assert.Equal(unchecked((int)0x80040E60), binSort.Failure!.Value.Scode);
            Assert.Equal("Microsoft Cursor Engine", binSort.Failure!.Value.Source);
            Assert.Equal(Guid.Empty, binSort.Failure!.Value.ErrorInfoGuid);
            Assert.Null(Call(tblT, "Close").Failure);
            Release(tblT);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }

        // Direct TableRowSource (adCmdTable=2) unit check for AdoSortSpec refusing unrecorded adVarBinary (204) by name.
        nint bCn = OpenAceConnection(exactPrefixPath, mode: 1);
        try
        {
            nint bRs = New(OwnRecordset);
            Assert.Null(Put(bRs, "CursorLocation", 3).Failure);
            Assert.Null(Call(bRs, "Open", "BinT", new Dispatch(bCn), 3, 1, 2).Failure);
            Assert.Equal(HResults.E_NOTIMPL, Put(bRs, "Sort", "B ASC").Failure!.Value.Scode);
            Assert.Null(Call(bRs, "Close").Failure);
            Release(bRs);
        }
        finally
        {
            Assert.Null(Call(bCn, "Close").Failure);
            Release(bCn);
        }
    }

    private static void FindOnClientServerAndViewRefusals(string ado1bPath)
    {
        nint cn = OpenAceConnection(ado1bPath, mode: 1);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 3).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, L, S FROM T ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);

            // Forward Find, SkipRecords=1, and miss to EOF (evidence/r-ado-1b/view-find-client #19-52).
            Assert.Null(Call(rs, "Find", "L = 20").Failure);
            Assert.Equal(2, Get(rs, "AbsolutePosition"));
            Assert.Equal(2, ReadIntField(rs, "ID"));

            Assert.Null(Call(rs, "Find", "L = 20", 1).Failure);
            Assert.Equal(4, Get(rs, "AbsolutePosition"));
            Assert.Equal(4, ReadIntField(rs, "ID"));

            Assert.Null(Call(rs, "Find", "L = 20", 1).Failure);
            Assert.Equal(true, Get(rs, "EOF"));
            Assert.Equal(false, Get(rs, "BOF"));
            Assert.Equal(-3, Get(rs, "AbsolutePosition"));

            // Backward Find from MoveLast (evidence/r-ado-1b/view-find-client #65-83).
            Assert.Null(Call(rs, "MoveLast").Failure);
            Assert.Null(Call(rs, "Find", "L = 20", 0, -1).Failure);
            Assert.Equal(4, ReadIntField(rs, "ID"));
            Assert.Null(Call(rs, "Find", "L = 20", 1, -1).Failure);
            Assert.Equal(2, ReadIntField(rs, "ID"));

            // Start = adBookmarkFirst (1) and adBookmarkLast (2) (evidence/r-ado-1b/view-find-client #96-115).
            Assert.Null(Call(rs, "MoveLast").Failure);
            Assert.Null(Call(rs, "Find", "ID = 2", 0, 1, 1).Failure);
            Assert.Equal(2, ReadIntField(rs, "ID"));
            Assert.Null(Call(rs, "Find", "ID = 2", 0, 1, 2).Failure);
            Assert.Equal(true, Get(rs, "EOF"));

            // SkipRecords = -1 from first row lands at EOF (evidence/r-ado-1b/view-find-client #190-194).
            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Null(Call(rs, "Find", "L = 20", -1).Failure);
            Assert.Equal(true, Get(rs, "EOF"));
            Assert.Equal(false, Get(rs, "BOF"));
            Assert.Equal(-3, Get(rs, "AbsolutePosition"));

            // Criteria errors keep position unchanged.
            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Equal(unchecked((int)0x800A0BB9), Call(rs, "Find", "L IS NULL").Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0BB9), Call(rs, "Find", "").Failure!.Value.Scode);
            Assert.Equal(1, Get(rs, "AbsolutePosition"));

            // Open client Find(VT_NULL) -> 0x80020005 + Connection.Errors(0) Provider Type mismatch;
            // non-VT_NULL conversion failures (e.g. bad SkipRecords or Dispatch criteria) -> E_NOTIMPL.
            Outcome nullFind = Call(rs, "Find", DBNull.Value);
            Assert.Equal(unchecked((int)0x80020005), nullFind.Failure!.Value.Scode);
            Assert.Equal("Provider", nullFind.Failure!.Value.Source);
            Assert.Equal(1, GetErrorsCount(cn));
            Assert.Equal(1, Get(rs, "AbsolutePosition"));
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Find", "L = 20", "not-a-number").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Find", new Dispatch(0)).Failure!.Value.Scode);

            // Find on a filtered or sorted client view is unrecorded -> refused by name.
            Assert.Null(Put(rs, "Filter", "L = 20").Failure);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Find", "ID = 2").Failure!.Value.Scode);
            Assert.Null(Put(rs, "Filter", 0).Failure);
            Assert.Null(Put(rs, "Sort", "ID DESC").Failure);
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "Find", "ID = 2").Failure!.Value.Scode);
            Assert.Null(Put(rs, "Sort", "").Failure);

            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void GetRowsAndGetStringAcrossRetainedFixtures(string bulkAdo4Path, string strAdoPath, string wireDaoPath, string ado1bPath, string decPath)
    {
        // 1) evidence/r-str-ado-act/ado-bulk on r-str-ado-act-ado4-accdb.accdb:
        //    Client Static GetRows, GetString (custom & default delimiters, partial NumRows), and Server ForwardOnly GetRows.
        nint bCn = OpenAceConnection(bulkAdo4Path, mode: 1);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 3).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, T, M FROM K ORDER BY ID", new Dispatch(bCn), 3, 1, 1).Failure);

            // GetRows(-1) returns [4, 4] and moves to EOF (#15-16).
            object[,] allGrid = (object[,])Call(rs, "GetRows", -1).Value!;
            Assert.Equal(4, allGrid.GetLength(0));
            Assert.Equal(4, allGrid.GetLength(1));
            Assert.Equal(1, allGrid[0, 0]);
            Assert.Equal("a1", allGrid[1, 0]);
            Assert.Equal("alpha", allGrid[2, 0]);
            Assert.Equal("memo one", allGrid[3, 0]);
            Assert.Equal(true, Get(rs, "EOF"));

            // GetRows(2, 1, "T") starts at adBookmarkFirst (1), selects column "T", and leaves AbsolutePosition = 3 (#17-19).
            Assert.Null(Call(rs, "MoveFirst").Failure);
            object[,] tGrid = (object[,])Call(rs, "GetRows", 2, 1, "T").Value!;
            Assert.Equal(1, tGrid.GetLength(0));
            Assert.Equal(2, tGrid.GetLength(1));
            Assert.Equal("alpha", tGrid[0, 0]);
            Assert.Same(DBNull.Value, tGrid[0, 1]);
            Assert.Equal(3, Get(rs, "AbsolutePosition"));

            // GetString custom delimiters (#20-22) and default delimiters (#23-25).
            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Equal(
                "1|a1|alpha|memo one\n2|a2|<null>|<null>\n3|b3|<null>|<null>\n4|b4|beta|x\n",
                Call(rs, "GetString", 2, -1, "|", "\n", "<null>").Value);
            Assert.Equal(true, Get(rs, "EOF"));

            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Equal(
                "1\ta1\talpha\tmemo one\r2\ta2\t\t\r3\tb3\t\t\r4\tb4\tbeta\tx\r",
                Call(rs, "GetString", 2).Value);
            Assert.Equal(true, Get(rs, "EOF"));

            // Partial GetString(2, 2) advances cursor to row 3.
            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Equal(
                "1\ta1\talpha\tmemo one\r2\ta2\t\t\r",
                Call(rs, "GetString", 2, 2).Value);
            Assert.Equal(false, Get(rs, "EOF"));
            Assert.Equal(3, Get(rs, "AbsolutePosition"));

            // Unrecorded GetRows / GetString arguments refused by name (E_NOTIMPL).
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "GetRows", 2, 0).Failure!.Value.Scode); // adBookmarkCurrent
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "GetRows", 2, 2).Failure!.Value.Scode); // adBookmarkLast
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "GetRows", 2, Missing, 0).Failure!.Value.Scode); // integer ordinal
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "GetString", 1).Failure!.Value.Scode); // StringFormat != 2
            Assert.Equal(HResults.E_NOTIMPL, Call(rs, "GetString", 2, 1, 123).Failure!.Value.Scode); // non-BSTR delimiter

            // GetRows with a SAFEARRAY Fields argument refused by name (E_NOTIMPL).
            Outcome arrFieldsOut = CallGetRowsWithFieldArray(rs, "ID", "Code");
            Assert.Equal(HResults.E_NOTIMPL, arrFieldsOut.Failure!.Value.Scode);

            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);

            // Server ForwardOnly cursor GetRows(2) then GetRows(-1) (unknown-count path).
            nint fwd = New(OwnRecordset);
            Assert.Null(Call(fwd, "Open", "SELECT ID, Code FROM K ORDER BY ID", new Dispatch(bCn), 0, 1, 1).Failure);
            object[,] f1 = (object[,])Call(fwd, "GetRows", 2).Value!;
            Assert.Equal(2, f1.GetLength(0));
            Assert.Equal(2, f1.GetLength(1));
            Assert.Equal(1, f1[0, 0]);
            Assert.Equal(2, f1[0, 1]);
            Assert.Equal(false, Get(fwd, "EOF"));

            object[,] f2 = (object[,])Call(fwd, "GetRows", -1).Value!;
            Assert.Equal(2, f2.GetLength(0));
            Assert.Equal(2, f2.GetLength(1));
            Assert.Equal(3, f2[0, 0]);
            Assert.Equal(4, f2[0, 1]);
            Assert.Equal(true, Get(fwd, "EOF"));
            Assert.Null(Call(fwd, "Close").Failure);
            Release(fwd);
        }
        finally
        {
            Assert.Null(Call(bCn, "Close").Failure);
            Release(bCn);
        }

        // 2) evidence/r-str-ado-act/str-ado-read (#403-405) on r-str-ado-act-str-accdb.accdb:
        //    GetString preserves embedded NULs and maps both empty string "" (ID=1) and NULL (ID=2) to NullExpr.
        nint sCn = OpenAceConnection(strAdoPath, mode: 1);
        try
        {
            nint sRs = New(OwnRecordset);
            Assert.Null(Put(sRs, "CursorLocation", 3).Failure);
            Assert.Null(Call(sRs, "Open", "SELECT ID, T FROM S ORDER BY ID", new Dispatch(sCn), 3, 1, 1).Failure);
            string gs = (string)Call(sRs, "GetString", 2, -1, "|", "\n", "<null>").Value!;
            Assert.Equal(844, gs.Length);
            Assert.StartsWith("1|<null>\n2|<null>\n3|a\0b\n4|\0\n5|abc\0\n6|alpha\n", gs);
            Assert.Equal(3, gs.Count(c => c == '\0'));
            Assert.Null(Call(sRs, "Close").Failure);
            Release(sRs);
        }
        finally
        {
            Assert.Null(Call(sCn, "Close").Failure);
            Release(sCn);
        }

        // 3) evidence/r-wire/ado-getrows-tags on r-wire-dao.accdb:
        //    Exact recorded SQL opens across all 13 typed bands; 12 succeed with exact VarTypes and byte arrays
        //    (including FBin native type 9 and FOle native type 11), and FDec (precision-38) hits NotModelled.AdoPrecision38Decimal.
        nint wCn = OpenAceConnection(wireDaoPath, mode: 1);
        try
        {
            (string Col, int Lo, int Hi, int ExpectedRows, Type ExpectedClrType, bool HasNull)[] sqlBands =
            [
                ("FByte", 100, 199, 6, typeof(byte), true),
                ("FInt", 200, 299, 6, typeof(short), true),
                ("FLong", 300, 399, 7, typeof(int), true),
                ("FSingle", 400, 499, 15, typeof(float), true),
                ("FDouble", 500, 599, 15, typeof(double), false),
                ("FCurr", 600, 699, 8, typeof(decimal), true),
                ("FDate", 700, 799, 14, typeof(OleDate), false),
                ("FText", 800, 899, 15, typeof(string), true),
                ("FMemo", 900, 999, 10, typeof(string), false),
                ("FBin", 1000, 1099, 8, typeof(byte[]), true),
                ("FOle", 1100, 1199, 6, typeof(byte[]), true),
                ("FBool", 1200, 1299, 9, typeof(bool), false),
            ];

            foreach (var band in sqlBands)
            {
                nint wRs = New(OwnRecordset);
                Assert.Null(Put(wRs, "CursorLocation", 3).Failure);
                Assert.Null(Call(wRs, "Open", $"SELECT ID, {band.Col} FROM W WHERE ID BETWEEN {band.Lo} AND {band.Hi} ORDER BY ID", new Dispatch(wCn), 3, 1, 1).Failure);
                if (band.Col == "FBin")
                {
                    nint flds = (nint)Get(wRs, "Fields")!;
                    AssertFieldMeta(flds, "FBin", 204, 255, 255, 255, 106);
                    Release(flds);
                }
                else if (band.Col == "FOle")
                {
                    nint flds = (nint)Get(wRs, "Fields")!;
                    AssertFieldMeta(flds, "FOle", 205, 1073741823, 255, 255, 234);
                    Release(flds);
                }
                object[,] grid = (object[,])Call(wRs, "GetRows", -1).Value!;
                Assert.Equal(2, grid.GetLength(0));
                Assert.Equal(band.ExpectedRows, grid.GetLength(1));

                bool sawType = false;
                bool sawNull = false;
                for (int r = 0; r < band.ExpectedRows; r++)
                {
                    Assert.IsType<int>(grid[0, r]);
                    object? cell = grid[1, r];
                    if (ReferenceEquals(cell, DBNull.Value))
                    {
                        sawNull = true;
                    }
                    else
                    {
                        Assert.NotNull(cell);
                        Assert.Equal(band.ExpectedClrType, cell!.GetType());
                        sawType = true;
                    }
                }
                Assert.True(sawType, $"Expected non-null {band.ExpectedClrType.Name} for {band.Col}");
                Assert.Equal(band.HasNull, sawNull);
                if (band.Col == "FBin")
                {
                    // evidence/r-wire/ado-getrows-tags #270: rows 1000 & 1004 Null; non-null rows are 255-byte arrays.
                    Assert.Same(DBNull.Value, grid[1, 0]);
                    Assert.Same(DBNull.Value, grid[1, 4]);
                    byte[] r1001 = (byte[])grid[1, 1]!;
                    Assert.Equal(255, r1001.Length);
                    Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x00 }, r1001[..4]);
                    byte[] r1003 = (byte[])grid[1, 3]!;
                    Assert.Equal(255, r1003.Length);
                    Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03 }, r1003[..4]);
                    byte[] r1005 = (byte[])grid[1, 5]!;
                    Assert.Equal(255, r1005.Length);
                    Assert.Equal(new byte[] { 0x41, 0x00, 0x42, 0x00 }, r1005[..4]);
                    byte[] r1007 = (byte[])grid[1, 7]!;
                    Assert.Equal(255, r1007.Length);
                    Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x02 }, r1007[..4]);
                }
                else if (band.Col == "FOle")
                {
                    // evidence/r-wire/ado-getrows-tags #296: row 1100 Null; rows 1101..1105 are 3, 256, 5000, 4, 3 bytes.
                    Assert.Same(DBNull.Value, grid[1, 0]);
                    Assert.Equal(new byte[] { 0x00, 0x00, 0x01 }, (byte[])grid[1, 1]!);
                    byte[] r1102 = (byte[])grid[1, 2]!;
                    Assert.Equal(256, r1102.Length);
                    Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03 }, r1102[..4]);
                    byte[] r1103 = (byte[])grid[1, 3]!;
                    Assert.Equal(5000, r1103.Length);
                    Assert.Equal(new byte[] { 0x00, 0x07, 0x0E, 0x15 }, r1103[..4]);
                    Assert.Equal(new byte[] { 0x41, 0x00, 0x42, 0x00 }, (byte[])grid[1, 4]!);
                    Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, (byte[])grid[1, 5]!);
                }
                Assert.Null(Call(wRs, "Close").Failure);
                Release(wRs);
            }

            // evidence/r-wire/ado-getrows-tags opens FDec (precision 38/scale 0, row 1304 with non-zero High32/Mid32) via adCmdText SQL;
            // AdoFields currently refuses non-Lo32 precision-38 Decimal values by name (NotModelled.AdoPrecision38Decimal).
            nint sqlDecRs = New(OwnRecordset);
            Assert.Null(Put(sqlDecRs, "CursorLocation", 3).Failure);
            Outcome decOpen = Call(sqlDecRs, "Open", "SELECT ID, FDec FROM W WHERE ID BETWEEN 1300 AND 1399 ORDER BY ID", new Dispatch(wCn), 3, 1, 1);
            Assert.Equal(HResults.E_NOTIMPL, decOpen.Failure!.Value.Scode);
            Assert.Contains("NotModelled.AdoPrecision38Decimal", decOpen.Failure!.Value.Description);
            Release(sqlDecRs);
        }
        finally
        {
            Assert.Null(Call(wCn, "Close").Failure);
            Release(wCn);
        }

        // 4) Additional GetRows type coverage: VT_DECIMAL + VT_NULL (r-fix-wire-ado-ado5.accdb) and
        //    VT_ARRAY|VT_UI1 + VT_NULL via recorded SQL SELECT (3, 3, 1) 1-arg GetRows(-1) (evidence/r-wire/ado-getrows-tags),
        //    plus adCmdTable OLE GetRows refusal and GetString non-I4/BSTR refusal.
        nint decCn = OpenAceConnection(decPath, mode: 1);
        try
        {
            nint decRs = New(OwnRecordset);
            Assert.Null(Put(decRs, "CursorLocation", 3).Failure);
            Assert.Null(Call(decRs, "Open", "SELECT ID, N FROM K ORDER BY ID", new Dispatch(decCn), 3, 1, 1).Failure);
            object[,] decGrid = (object[,])Call(decRs, "GetRows", -1).Value!;
            Assert.Equal(2, decGrid.GetLength(0));
            Assert.Equal(3.1250m, decGrid[1, 0]);
            Assert.Null(Call(decRs, "Close").Failure);
            Release(decRs);
        }
        finally
        {
            Assert.Null(Call(decCn, "Close").Failure);
            Release(decCn);
        }

        nint aCn = OpenAceConnection(ado1bPath, mode: 1);
        try
        {
            nint binRs = New(OwnRecordset);
            Assert.Null(Put(binRs, "CursorLocation", 3).Failure);
            Assert.Null(Call(binRs, "Open", "SELECT B FROM T ORDER BY ID", new Dispatch(aCn), 3, 1, 1).Failure);
            object[,] bGrid = (object[,])Call(binRs, "GetRows", -1).Value!;
            Assert.Equal(1, bGrid.GetLength(0));
            bool sawBytes = false;
            bool sawNull = false;
            for (int r = 0; r < bGrid.GetLength(1); r++)
            {
                if (ReferenceEquals(bGrid[0, r], DBNull.Value))
                {
                    sawNull = true;
                }
                else
                {
                    Assert.IsType<byte[]>(bGrid[0, r]);
                    sawBytes = true;
                }
            }
            Assert.True(sawBytes);
            Assert.True(sawNull);
            Assert.Null(Call(binRs, "Close").Failure);
            Release(binRs);

            // adCmdTable GetRows over Binary/OLE is unrecorded -> refused by name (Finding 3).
            nint tblBinRs = New(OwnRecordset);
            Assert.Null(Put(tblBinRs, "CursorLocation", 3).Failure);
            Assert.Null(Call(tblBinRs, "Open", "T", new Dispatch(aCn), 3, 1, 2).Failure);
            Outcome tblGetRows = Call(tblBinRs, "GetRows", -1, 1, "B");
            Assert.NotNull(tblGetRows.Failure);
            Assert.Equal(HResults.E_NOTIMPL, tblGetRows.Failure!.Value.Scode);
            Assert.Contains("NotModelled.AdoGetRowsBinaryCursor", tblGetRows.Failure!.Value.Description!, StringComparison.Ordinal);
            Assert.Null(Call(tblBinRs, "Close").Failure);
            Release(tblBinRs);

            nint dRs = New(OwnRecordset);
            Assert.Null(Call(dRs, "Open", "SELECT D FROM T WHERE D IS NOT NULL", new Dispatch(aCn), 0, 1, 1).Failure);
            Assert.Equal(HResults.E_NOTIMPL, Call(dRs, "GetString", 2, 1).Failure!.Value.Scode);
            Assert.Null(Call(dRs, "Close").Failure);
            Release(dRs);
        }
        finally
        {
            Assert.Null(Call(aCn, "Close").Failure);
            Release(aCn);
        }
    }

    private static void PropertiesInventoriesAndInvalidation(string ado1bPath, string qMiscPath)
    {
        // 1) Server Recordset (Keyset + Optimistic (1, 3)) and Command Properties on r-q-ado-misc-main.accdb
        //    (evidence/r-q-ado-misc/props-lookup-refresh #34-43): 88-name ordinal-only inventory, plus invalidation on Close / ActiveConnection change.
        nint qCn = OpenAceConnection(qMiscPath);
        nint qCn2 = OpenAceConnection(qMiscPath);
        try
        {
            nint srv = New(OwnRecordset);
            Assert.Null(Call(srv, "Open", "SELECT ID, Code, T, L FROM K ORDER BY ID", new Dispatch(qCn), 1, 3, 1).Failure);
            nint srvProps = (nint)Get(srv, "Properties")!;
            Assert.Equal(88, Get(srvProps, "Count"));
            nint p0 = (nint)Get(srvProps, "Item", 0)!;
            Assert.Equal("Preserve on Abort", Get(p0, "Name"));
            Assert.Equal(HResults.E_NOTIMPL, TryGet(p0, "Value").Failure!.Value.Scode);
            // Unrecorded on rs.Properties: Refresh, string Item lookup, out-of-range ordinal -> E_NOTIMPL.
            Assert.Equal(HResults.E_NOTIMPL, Call(srvProps, "Refresh").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, TryGet(srvProps, "Item", "Bookmarkable").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, TryGet(srvProps, "Item", "NoSuchProp").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, TryGet(srvProps, "Item", 88).Failure!.Value.Scode);

            // Closing rs invalidates held srvProps and p0 with 0x800A0D5C (publishesErrorInfo: false).
            Assert.Null(Call(srv, "Close").Failure);
            Outcome deadCount = TryGet(srvProps, "Count");
            Assert.Equal(unchecked((int)0x800A0D5C), deadCount.Failure!.Value.Scode);
            Assert.Equal(Guid.Empty, deadCount.Failure!.Value.ErrorInfoGuid);
            Outcome deadProp = TryGet(p0, "Name");
            Assert.Equal(unchecked((int)0x800A0D5C), deadProp.Failure!.Value.Scode);
            Assert.Equal(Guid.Empty, deadProp.Failure!.Value.ErrorInfoGuid);
            Release(p0);
            Release(srvProps);
            Release(srv);

            // Unrecorded server cursor combinations refuse rs.Properties by name.
            foreach ((int cur, int lck) in new[] { (0, 1), (1, 1), (3, 1) })
            {
                nint otherSrv = New(OwnRecordset);
                Assert.Null(Call(otherSrv, "Open", "SELECT ID FROM K", new Dispatch(qCn), cur, lck, 1).Failure);
                Assert.Equal(HResults.E_NOTIMPL, TryGet(otherSrv, "Properties").Failure!.Value.Scode);
                Assert.Null(Call(otherSrv, "Close").Failure);
                Release(otherSrv);
            }

            // Command.Properties: refused when unbound or with empty CommandText; 88-name ordinal-only inventory when bound with CommandText.
            nint cmd = New(OwnCommand);
            Assert.Equal(HResults.E_NOTIMPL, TryGet(cmd, "Properties").Failure!.Value.Scode);
            Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(qCn)).Failure);
            Assert.Equal(HResults.E_NOTIMPL, TryGet(cmd, "Properties").Failure!.Value.Scode); // empty CommandText
            Assert.Null(Put(cmd, "CommandText", "SELECT ID FROM K").Failure);

            nint cmdProps = (nint)Get(cmd, "Properties")!;
            Assert.Equal(88, Get(cmdProps, "Count"));
            nint cp87 = (nint)Get(cmdProps, "Item", 87)!;
            Assert.Equal("Bookmarkable", Get(cp87, "Name"));
            Assert.Equal(HResults.E_NOTIMPL, Call(cmdProps, "Refresh").Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, TryGet(cmdProps, "Item", "NoSuchProp").Failure!.Value.Scode);

            // Rebinding Command.ActiveConnection invalidates held cmdProps and cp87 with 0x800A0D5C.
            Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(qCn2)).Failure);
            Assert.Equal(unchecked((int)0x800A0D5C), TryGet(cmdProps, "Count").Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0D5C), TryGet(cp87, "Name").Failure!.Value.Scode);
            Release(cp87);
            Release(cmdProps);

            // Fresh Command.Properties after rebind works, then Connection.Close invalidates it with 0x800A0D5C.
            nint cmdProps2 = (nint)Get(cmd, "Properties")!;
            nint cp0 = (nint)Get(cmdProps2, "Item", 0)!;
            Assert.Equal("Preserve on Abort", Get(cp0, "Name"));
            Assert.Null(Call(qCn2, "Close").Failure);
            Assert.Equal(unchecked((int)0x800A0D5C), TryGet(cmdProps2, "Count").Failure!.Value.Scode);
            Assert.Equal(unchecked((int)0x800A0D5C), TryGet(cp0, "Name").Failure!.Value.Scode);
            Release(cp0);
            Release(cmdProps2);
            Release(cmd);
        }
        finally
        {
            if ((int)Get(qCn, "State")! == 1) Assert.Null(Call(qCn, "Close").Failure);
            if ((int)Get(qCn2, "State")! == 1) Assert.Null(Call(qCn2, "Close").Failure);
            Release(qCn2);
            Release(qCn);
        }

        // 2) Client Recordset Properties on r-ado-1b-main.accdb:
        //    Client BatchOptimistic (3, 4) exposes all 103 dynamic properties, Update Criteria (0..3) and Update Resync (0..15)
        //    with 0x80040E21 out-of-range validation (evidence/r-q48-ado-batch-edits/client-props-inventory-accdb,
        //    evidence/r-ado-1b/batch-update-criteria, batch-update-resync); client ReadOnly (3, 1) refuses Properties by name.
        nint aCn = OpenAceConnection(ado1bPath);
        try
        {
            nint cliRo = New(OwnRecordset);
            Assert.Null(Put(cliRo, "CursorLocation", 3).Failure);
            Assert.Null(Call(cliRo, "Open", "SELECT ID FROM T", new Dispatch(aCn), 3, 1, 1).Failure);
            Assert.Equal(HResults.E_NOTIMPL, TryGet(cliRo, "Properties").Failure!.Value.Scode);
            Assert.Null(Call(cliRo, "Close").Failure);
            Release(cliRo);

            nint cli = New(OwnRecordset);
            Assert.Null(Put(cli, "CursorLocation", 3).Failure);
            Assert.Null(Call(cli, "Open", "SELECT ID FROM T", new Dispatch(aCn), 3, 4, 1).Failure);
            nint cliProps = (nint)Get(cli, "Properties")!;
            Assert.Equal(103, Get(cliProps, "Count"));
            nint pBm = (nint)Get(cliProps, "Item", "Bookmarkable")!;
            Assert.Equal(true, Get(pBm, "Value"));
            Release(pBm);

            nint pUc = (nint)Get(cliProps, "Item", "Update Criteria")!;
            Assert.Equal(2, Get(pUc, "Value"));
            foreach (int v in new[] { 0, 1, 2, 3 })
            {
                Assert.Null(Put(pUc, "Value", v).Failure);
                Assert.Equal(v, Get(pUc, "Value"));
            }
            Assert.Equal(unchecked((int)0x80040E21), Put(pUc, "Value", 4).Failure!.Value.Scode);
            Release(pUc);

            nint pUr = (nint)Get(cliProps, "Item", "Update Resync")!;
            Assert.Equal(1, Get(pUr, "Value"));
            foreach (int v in new[] { 0, 1, 2, 3, 4, 8, 15 })
            {
                Assert.Null(Put(pUr, "Value", v).Failure);
                Assert.Equal(v, Get(pUr, "Value"));
            }
            Assert.Equal(unchecked((int)0x80040E21), Put(pUr, "Value", 16).Failure!.Value.Scode);
            Release(pUr);

            Release(cliProps);
            Assert.Null(Call(cli, "Close").Failure);
            Release(cli);
        }
        finally
        {
            Assert.Null(Call(aCn, "Close").Failure);
            Release(aCn);
        }
    }

    private static void CommandActiveConnectionTimeoutParametersAndRecordsetSource(string ado1bPath)
    {
        nint cn = OpenAceConnection(ado1bPath, mode: 1);
        try
        {
            // CommandTimeout independence & unrecorded negative value refusal (evidence/r-q-ado-misc/results-timeout-values).
            Assert.Null(Put(cn, "CommandTimeout", 20).Failure);
            nint cmd = New(OwnCommand);
            Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
            Assert.Equal(30, Get(cmd, "CommandTimeout"));
            Assert.Null(Put(cn, "CommandTimeout", 7).Failure);
            Assert.Equal(30, Get(cmd, "CommandTimeout"));
            Assert.Null(Put(cmd, "CommandTimeout", 3).Failure);
            Assert.Equal(3, Get(cmd, "CommandTimeout"));
            Assert.Equal(7, Get(cn, "CommandTimeout"));
            Assert.Equal(HResults.E_NOTIMPL, Put(cmd, "CommandTimeout", -1).Failure!.Value.Scode);
            Assert.Equal(HResults.E_NOTIMPL, Put(cmd, "ActiveConnection", new Dispatch(0)).Failure!.Value.Scode);

            // Recordset.Open(cmd, Missing, 1, 1) with adCmdStoredProc (4) against saved query "QAll" on r-ado-1b-main.accdb;
            // Recordset.Open(cmd, cn, 1, 1) fails 0x800A0E7B (evidence/r-ado-1b/rs-source-kinds #254-273).
            Assert.Null(Put(cmd, "CommandText", "QAll").Failure);
            Assert.Null(Put(cmd, "CommandType", 4).Failure);
            // r-q54-excl-ddl-ix-cmd/ado-cmd-exec-noparam-accdb #116-125: Command.Execute adCmdStoredProc returns open Recordset.
            Outcome spExec = Call(cmd, "Execute");
            Assert.Null(spExec.Failure);
            nint rsSp = (nint)spExec.Value!;
            Assert.Equal(1, Get(rsSp, "State"));
            Assert.Equal(false, Get(rsSp, "EOF"));
            Assert.Null(Call(rsSp, "Close").Failure);
            Release(rsSp);

            nint rc1 = New(OwnRecordset);
            Assert.Null(Call(rc1, "Open", new Dispatch(cmd), Missing, 1, 1).Failure);
            Assert.Equal(1, Get(rc1, "State"));
            Assert.Equal("exec QAll", Get(rc1, "Source"));
            Assert.Equal(false, Get(rc1, "EOF"));
            Outcome rc1Count = TryGet(rc1, "RecordCount");
            Assert.NotNull(rc1Count.Failure);
            Assert.Equal(HResults.E_NOTIMPL, rc1Count.Failure!.Value.Scode);
            Assert.StartsWith("NotModelled.AdoRecordCount.CommandRoute", rc1Count.Failure.Value.Description, StringComparison.Ordinal);
            Assert.Null(Call(rc1, "Close").Failure);
            Release(rc1);

            nint rc2 = New(OwnRecordset);
            Outcome conflict = Call(rc2, "Open", new Dispatch(cmd), new Dispatch(cn), 1, 1);
            Assert.Equal(unchecked((int)0x800A0E7B), conflict.Failure!.Value.Scode);
            Assert.Equal(1240656u, conflict.Failure!.Value.HelpContext);
            Assert.Equal(0, Get(rc2, "State"));
            Assert.Equal("exec QAll", Get(rc2, "Source"));
            nint rc2Cn = (nint)Get(rc2, "ActiveConnection")!;
            Assert.NotEqual(0, rc2Cn);
            Release(rc2Cn);
            Release(rc2);

            // Empty Parameters on an open adCmdStoredProc Command: Count returns 0 (r-q-ado-misc/param-saved-declared-order #17);
            // Parameters.Refresh() succeeds and leaves Count == 0 (evidence/r-q47-bigint-expr-edges/bi-params-ado-cmd-accdb #43,
            // evidence/r-q49-dt2-draftprops/dt2-params-ado-cmd-accdb #41).
            nint prs = (nint)Get(cmd, "Parameters")!;
            Assert.Equal(0, Get(prs, "Count"));
            Assert.Null(Call(prs, "Refresh").Failure);
            Assert.Equal(0, Get(prs, "Count"));

            // Append a Parameter on an open adCmdStoredProc connection:
            // - Parameters.Refresh() on adCmdStoredProc clears appended parameters and leaves Count == 0
            //   (evidence/r-fix-wire-ado/ado-params-saved-query #19, evidence/r-ado-1b/param-refresh-after-text-change #44/#79).
            // - Re-append a Parameter, then Connection.Close() clears the bound Command's non-empty Parameters so Count drops from 1 to 0
            //   (evidence/r-ado-1b/param-connection-change #48-49), Parameters.Refresh succeeds, and Command.Execute fails 0x800A0E7D.
            nint p = (nint)Call(cmd, "CreateParameter", "p1", 3, 1, 4, 20).Value!;
            Assert.Null(Call(prs, "Append", new Dispatch(p)).Failure);
            Release(p);
            Assert.Equal(1, Get(prs, "Count"));
            Assert.Null(Call(prs, "Refresh").Failure);
            Assert.Equal(0, Get(prs, "Count"));

            nint pClose = (nint)Call(cmd, "CreateParameter", "p2", 3, 1, 4, 20).Value!;
            Assert.Null(Call(prs, "Append", new Dispatch(pClose)).Failure);
            Release(pClose);
            Assert.Equal(1, Get(prs, "Count"));

            Assert.Null(Call(cn, "Close").Failure);
            Assert.Equal(0, Get(prs, "Count"));
            Assert.Null(Call(prs, "Refresh").Failure);
            Assert.Equal(unchecked((int)0x800A0E7D), Call(cmd, "Execute").Failure!.Value.Scode);

            Release(prs);
            Release(cmd);
        }
        finally
        {
            if ((int)Get(cn, "State")! == 1)
            {
                Assert.Null(Call(cn, "Close").Failure);
            }
            Release(cn);
        }
    }

    private static void DecimalExpressionMetadata(string decPath)
    {
        nint cn = OpenAceConnection(decPath, mode: 1);
        try
        {
            // Server cursor: -N evaluates end-to-end (adNumeric 131, Size=19, Prec=18, Scale=4, Attr=114, ActualSize=16, Value=-3.1250m)
            // (evidence/r-fix-wire-ado/fields-meta-decimal-expressions #151-168).
            nint srv = New(OwnRecordset);
            Assert.Null(Call(srv, "Open", "SELECT N + 1 AS A, N * 1.5 AS B, N / 2 AS C, -N AS D, N + N AS E FROM K WHERE ID = 1", new Dispatch(cn), 1, 1, 1).Failure);
            nint sf = (nint)Get(srv, "Fields")!;
            AssertFieldMeta(sf, "D", expectedType: 131, expectedSize: 19, expectedPrec: 18, expectedScale: 4, expectedAttr: 114);

            nint fNeg = (nint)Get(sf, "Item", "D")!;
            Assert.Equal(-3.1250m, Convert.ToDecimal(Get(fNeg, "Value")));
            Assert.Equal(16, Get(fNeg, "ActualSize"));
            Release(fNeg);

            // Binary StoredDecimal arithmetic expressions are unimplemented in CalcDb.Expressions and refused by name on metadata & value.
            foreach (string col in new[] { "A", "B", "C", "E" })
            {
                nint fBin = (nint)Get(sf, "Item", col)!;
                Assert.Equal(HResults.E_NOTIMPL, TryGet(fBin, "Type").Failure!.Value.Scode);
                Assert.Equal(HResults.E_NOTIMPL, TryGet(fBin, "Value").Failure!.Value.Scode);
                Release(fBin);
            }

            Release(sf);
            Assert.Null(Call(srv, "Close").Failure);
            Release(srv);

            // Client cursor for -N reports ActualSize=19 while server reports 16 (evidence/r-fix-wire-ado/fields-meta-decimal-expressions).
            nint cliNeg = New(OwnRecordset);
            Assert.Null(Put(cliNeg, "CursorLocation", 3).Failure);
            Assert.Null(Call(cliNeg, "Open", "SELECT N AS BaseN, -N AS NegN FROM K WHERE ID = 1", new Dispatch(cn), 3, 1, 1).Failure);
            nint cf = (nint)Get(cliNeg, "Fields")!;
            nint fBase = (nint)Get(cf, "Item", "BaseN")!;
            Assert.Equal(3.1250m, Convert.ToDecimal(Get(fBase, "Value")));
            Assert.Equal(19, Get(fBase, "ActualSize"));
            Release(fBase);
            nint fCliNeg = (nint)Get(cf, "Item", "NegN")!;
            Assert.Equal(-3.1250m, Convert.ToDecimal(Get(fCliNeg, "Value")));
            Assert.Equal(19, Get(fCliNeg, "ActualSize"));
            Release(fCliNeg);
            Release(cf);
            Assert.Null(Call(cliNeg, "Close").Failure);
            Release(cliNeg);

            // Count(N) succeeds (adInteger, Attr=122); Sum(N)/Avg(N) succeed with recorded ADO Decimal metadata & values
            // (evidence/r-fix-wire-ado/fields-meta-decimal-expressions #424-455, #492-523).
            nint cntRs = New(OwnRecordset);
            Assert.Null(Call(cntRs, "Open", "SELECT Count(N) AS C FROM K", new Dispatch(cn), 1, 1, 1).Failure);
            nint cntF = (nint)Get(cntRs, "Fields")!;
            AssertFieldMeta(cntF, "C", expectedType: 3, expectedSize: 4, expectedPrec: 10, expectedScale: 255, expectedAttr: 122);
            Assert.Equal(3, ReadIntField(cntRs, "C"));
            Release(cntF);
            Assert.Null(Call(cntRs, "Close").Failure);
            Release(cntRs);

            nint aggRs = New(OwnRecordset);
            Assert.Null(Call(aggRs, "Open", "SELECT Sum(N) AS S, Avg(N) AS A FROM K", new Dispatch(cn), 1, 1, 1).Failure);
            nint aggF = (nint)Get(aggRs, "Fields")!;
            AssertFieldMeta(aggF, "S", expectedType: 131, expectedSize: 19, expectedPrec: 28, expectedScale: 4, expectedAttr: 122);
            AssertFieldMeta(aggF, "A", expectedType: 131, expectedSize: 19, expectedPrec: 28, expectedScale: 10, expectedAttr: 114);
            nint fSum = (nint)Get(aggF, "Item", "S")!;
            Assert.Equal(3.1249m, Convert.ToDecimal(Get(fSum, "Value")));
            Assert.Equal(19, Get(fSum, "ActualSize"));
            Release(fSum);
            nint fAvg = (nint)Get(aggF, "Item", "A")!;
            Assert.Equal(1.0416333333333333333333333333m, Convert.ToDecimal(Get(fAvg, "Value")));
            Assert.Equal(16, Get(fAvg, "ActualSize"));
            Release(fAvg);
            Release(aggF);
            Assert.Null(Call(aggRs, "Close").Failure);
            Release(aggRs);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static Outcome CallGetRowsWithFieldArray(nint rs, string col0, string col1)
    {
        SafeArrayBound bound = new() { Count = 2, LowerBound = 0 };
        nint psa = SafeArrayCreate((ushort)VarType.Variant, 1, &bound);
        Assert.NotEqual((nint)0, psa);
        nint b0 = Marshal.StringToBSTR(col0);
        nint b1 = Marshal.StringToBSTR(col1);
        try
        {
            NativeVariant v0 = new() { Vt = (ushort)VarType.Bstr, Pointer = b0 };
            NativeVariant v1 = new() { Vt = (ushort)VarType.Bstr, Pointer = b1 };
            int idx0 = 0;
            int idx1 = 1;
            Assert.Equal(0, SafeArrayPutElement(psa, &idx0, &v0));
            Assert.Equal(0, SafeArrayPutElement(psa, &idx1, &v1));

            int dispId = GetDispId(rs, "GetRows");
            NativeVariant* raw = stackalloc NativeVariant[3];
            // Reverse order: arg0=Fields (array), arg1=Start (missing), arg2=Rows (2)
            raw[0] = new NativeVariant { Vt = (ushort)(VarType.Array | VarType.Variant), Pointer = psa };
            raw[1] = new NativeVariant { Vt = (ushort)VarType.Error, Int64 = unchecked((uint)HResults.DISP_E_PARAMNOTFOUND) };
            raw[2] = new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 2 };
            return InvokeDispId(rs, dispId, 1, raw, 3, 0, null);
        }
        finally
        {
            Marshal.FreeBSTR(b0);
            Marshal.FreeBSTR(b1);
            _ = SafeArrayDestroy(psa);
        }
    }

    private static void AssertFieldMeta(nint fields, string name, int expectedType, int expectedSize, byte expectedPrec, byte expectedScale, int expectedAttr)
    {
        nint f = (nint)Get(fields, "Item", name)!;
        Assert.Equal(expectedType, Get(f, "Type"));
        Assert.Equal(expectedSize, Get(f, "DefinedSize"));
        Assert.Equal(expectedPrec, Convert.ToByte(Get(f, "Precision")));
        Assert.Equal(expectedScale, Convert.ToByte(Get(f, "NumericScale")));
        Assert.Equal(expectedAttr, Get(f, "Attributes"));
        Release(f);
    }

    private static int ReadIntField(nint rs, string name) => Convert.ToInt32(ReadObjField(rs, name));

    private static object? ReadObjField(nint rs, string name)
    {
        nint flds = (nint)Get(rs, "Fields")!;
        nint f = (nint)Get(flds, "Item", name)!;
        object? val = Get(f, "Value");
        Release(f);
        Release(flds);
        return val;
    }

    private sealed class MissingValue;
    private static readonly MissingValue Missing = new();
    private readonly record struct Dispatch(nint Pointer);
    private readonly record struct OleDate(double Value);
    private readonly record struct Error(int Scode, string? Source, string? Description, string? HelpFile, uint HelpContext, Guid ErrorInfoGuid);
    private readonly record struct Outcome(object? Value, Error? Failure);

    private static object? Get(nint target, string member, params object[] args)
    {
        Outcome o = Invoke(target, member, 2, args);
        Assert.True(o.Failure is null, $"{member} failed: {o.Failure}");
        return o.Value;
    }

    private static Outcome TryGet(nint target, string member, params object[] args) => Invoke(target, member, 2, args);

    private static Outcome Put(nint target, string member, object value) => Invoke(target, member, 4, value);

    private static Outcome Call(nint target, string member, params object[] args) => Invoke(target, member, 1, args);

    private static nint New(Guid clsid)
    {
        Assert.Equal(0, Create(clsid, 0, ClsctxInprocServer, IidIDispatch, out nint created));
        return created;
    }

    private static int GetDispId(nint target, string member)
    {
        nint* vt = *(nint**)target;
        Guid iidNull = Guid.Empty;
        int dispId;
        fixed (char* name = member)
        {
            char* pName = name;
            int hr = ((delegate* unmanaged<nint, Guid*, char**, uint, uint, int*, int>)vt[5])(target, &iidNull, &pName, 1, 1033, &dispId);
            Assert.Equal(0, hr);
        }
        return dispId;
    }

    private static Outcome Invoke(nint target, string member, ushort flags, params object[] args)
    {
        int dispId = GetDispId(target, member);
        NativeVariant* raw = stackalloc NativeVariant[Math.Max(args.Length, 1)];
        for (int i = 0; i < args.Length; i++)
        {
            raw[args.Length - 1 - i] = VariantOf(args[i]);
        }
        int namedPut = -3;
        try
        {
            return InvokeDispId(target, dispId, flags, args.Length == 0 ? null : raw, (uint)args.Length, flags == 4 ? 1u : 0u, flags == 4 ? &namedPut : null);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (raw[i].Vt == (ushort)VarType.Bstr && raw[i].Pointer != 0)
                {
                    Marshal.FreeBSTR(raw[i].Pointer);
                }
            }
        }
    }

    private static Outcome InvokeDispId(nint target, int dispId, ushort flags, NativeVariant* raw, uint argCount, uint namedCount, int* namedIds)
    {
        nint* vt = *(nint**)target;
        Guid iidNull = Guid.Empty;
        DispParams dp = new()
        {
            Args = (nint)raw,
            NamedArgs = (nint)namedIds,
            ArgCount = argCount,
            NamedArgCount = namedCount,
        };
        NativeVariant result = default;
        ExcepInfo ex = default;
        uint argErr;
        if (GetErrorInfo(0, out nint staleInfo) == 0 && staleInfo != 0)
        {
            Release(staleInfo);
        }
        int hr = ((delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)vt[6])(
            target, dispId, &iidNull, 1033, flags, &dp, &result, &ex, &argErr);
        if (hr == 0)
        {
            return new(Value(result), null);
        }
        Guid infoGuid = Guid.Empty;
        if (GetErrorInfo(0, out nint info) == 0 && info != 0)
        {
            nint* ivt = *(nint**)info;
            ((delegate* unmanaged<nint, Guid*, int>)ivt[3])(info, &infoGuid);
            Release(info);
        }
        string? src = TakeBstr(ex.Source);
        string? desc = TakeBstr(ex.Description);
        string? help = TakeBstr(ex.HelpFile);
        return new(null, new(hr == HResults.DISP_E_EXCEPTION ? ex.Scode : hr, src, desc, help, ex.HelpContext, infoGuid));
    }

    private static string? TakeBstr(nint bstr)
    {
        if (bstr == 0)
        {
            return null;
        }
        string s = Marshal.PtrToStringBSTR(bstr);
        Marshal.FreeBSTR(bstr);
        return s;
    }

    private static NativeVariant VariantOf(object value)
    {
        NativeVariant v = default;
        switch (value)
        {
            case int i:
                v.Vt = (ushort)VarType.I4;
                v.Int64 = (uint)i;
                break;
            case double d:
                v.Vt = (ushort)VarType.R8;
                v.Int64 = BitConverter.DoubleToInt64Bits(d);
                break;
            case bool b:
                v.Vt = (ushort)VarType.Bool;
                v.Int64 = b ? unchecked((ushort)(-1)) : 0;
                break;
            case string s:
                v.Vt = (ushort)VarType.Bstr;
                v.Pointer = Marshal.StringToBSTR(s);
                break;
            case DBNull:
                v.Vt = (ushort)VarType.Null;
                break;
            case Dispatch d:
                v.Vt = (ushort)VarType.Dispatch;
                v.Pointer = d.Pointer;
                break;
            case nint p:
                v.Vt = (ushort)VarType.Dispatch;
                v.Pointer = p;
                break;
            default:
                Assert.Same(Missing, value);
                v.Vt = (ushort)VarType.Error;
                v.Int64 = unchecked((uint)HResults.DISP_E_PARAMNOTFOUND);
                break;
        }
        return v;
    }

    private static object? Value(NativeVariant v)
    {
        if (((VarType)v.Vt & VarType.Array) != 0)
        {
            VarType elemVt = (VarType)v.Vt & ~VarType.Array;
            nint psa = v.Pointer;
            try
            {
                if (elemVt == VarType.Variant)
                {
                    Assert.Equal(0, SafeArrayGetLBound(psa, 1, out int l0));
                    Assert.Equal(0, SafeArrayGetUBound(psa, 1, out int u0));
                    Assert.Equal(0, SafeArrayGetLBound(psa, 2, out int l1));
                    Assert.Equal(0, SafeArrayGetUBound(psa, 2, out int u1));
                    int d0 = u0 - l0 + 1;
                    int d1 = u1 - l1 + 1;
                    var grid = new object?[d0, d1];
                    int* idx = stackalloc int[2];
                    for (int r = 0; r < d1; r++)
                    {
                        for (int c = 0; c < d0; c++)
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
                if (elemVt == VarType.UI1)
                {
                    Assert.Equal(0, SafeArrayGetLBound(psa, 1, out int lo));
                    Assert.Equal(0, SafeArrayGetUBound(psa, 1, out int hi));
                    int len = hi - lo + 1;
                    byte[] bytes = new byte[len];
                    for (int i = 0; i < len; i++)
                    {
                        int idx = lo + i;
                        byte b = 0;
                        Assert.Equal(0, SafeArrayGetElement(psa, &idx, &b));
                        bytes[i] = b;
                    }
                    return bytes;
                }
            }
            finally
            {
                _ = SafeArrayDestroy(psa);
            }
        }

        switch ((VarType)v.Vt)
        {
            case VarType.Empty:
                return null;
            case VarType.Null:
                return DBNull.Value;
            case VarType.I4:
                return (int)v.Int64;
            case VarType.I2:
                return (short)v.Int64;
            case VarType.I8:
                return v.Int64;
            case VarType.UI1:
                return (byte)v.Int64;
            case VarType.R4:
                return BitConverter.Int32BitsToSingle((int)v.Int64);
            case VarType.R8:
                return BitConverter.Int64BitsToDouble(v.Int64);
            case VarType.Currency:
                return decimal.FromOACurrency(v.Int64);
            case VarType.Date:
                return new OleDate(BitConverter.Int64BitsToDouble(v.Int64));
            case VarType.Bool:
                return (short)v.Int64 != 0;
            case VarType.Decimal:
                {
                    byte* p = (byte*)&v;
                    byte scale = p[2];
                    byte sign = p[3];
                    uint hi = *(uint*)(p + 4);
                    uint lo = *(uint*)(p + 8);
                    uint mid = *(uint*)(p + 12);
                    return new decimal((int)lo, (int)mid, (int)hi, (sign & 0x80) != 0, scale);
                }
            case VarType.Bstr when v.Pointer == 0:
                return null;
            case VarType.Bstr:
                string s = Marshal.PtrToStringBSTR(v.Pointer);
                Marshal.FreeBSTR(v.Pointer);
                return s;
            case VarType.Dispatch:
            case VarType.Unknown:
                return v.Pointer;
            default:
                throw new InvalidOperationException("unexpected result type " + (VarType)v.Vt);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SafeArrayBound
    {
        public uint Count;
        public int LowerBound;
    }

    [DllImport("oleaut32.dll")]
    private static extern nint SafeArrayCreate(ushort vt, uint cDims, SafeArrayBound* rgsabound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayPutElement(nint psa, int* rgIndices, void* pv);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetLBound(nint psa, uint nDim, out int plLbound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetUBound(nint psa, uint nDim, out int plUbound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(nint psa);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetElement(nint psa, int* rgIndices, void* pv);
}
