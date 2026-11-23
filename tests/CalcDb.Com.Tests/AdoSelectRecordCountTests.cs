using CalcDb.Com.Ado;
using CalcDb.Com.Com;
using CalcDb.Evidence;
using CalcDb.Foundation;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-09 / 5ktt:
/// exercises ADO SELECT <c>Recordset.RecordCount</c> against the published <c>CalcDb.dll</c> under
/// <c>CalcDb.Own.manifest</c> across format (ACCDB vs MDB), cursor location, cursor type, lock type,
/// source kind, and SQL shape, asserting exact retained ACE values (notably
/// <c>r-q2-expr-sql/demand-ado-events-shapes</c>) and named <c>NotModelled.AdoRecordCount.*</c> refusals
/// for every unrecorded tuple while confirming <c>Open()</c> and row reading stay intact.
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoSelectRecordCountTests
{
    private const string DemandShapesFixture = "d5b9ea177ac4d13c17561932e1c0a73240f06639a0fd1446ea319155bbf7ff3d"; // r-q2-expr-sql-main.accdb
    private const string Ado1bMainFixture = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7";     // r-ado-1b-main.accdb
    private const string OledbV2MdbFixture = "7bd7ceb48b5ea4897446fa758617b0f44b31a4aa4fe744c07dcb05ddbc8a523c";    // r-oledb-odbc-v2-mdb.mdb

    [Fact]
    public void CalcDb_ADO_Select_RecordCount_follows_retained_ACE_records_and_refuses_unrecorded_tuples()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-5ktt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string accdbPath = CopyFixture(store, DemandShapesFixture, tempDir, "r-q2-expr-sql-main.accdb");
        string ado1bPath = CopyFixture(store, Ado1bMainFixture, tempDir, "r-ado-1b-main.accdb");
        string mdbPath = CopyFixture(store, OledbV2MdbFixture, tempDir, "r-oledb-odbc-v2-mdb.mdb");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                AccdbServerStaticRecordedShapesAndRefusals(accdbPath, ado1bPath);
                AccdbNonBookmarkCursors(accdbPath);
                AccdbServerKeysetEligibleAndIneligible(accdbPath);
                AccdbClientStaticRecordedAndRefusals(accdbPath);
                MdbAllCursorsRefuseRecordCount(mdbPath);

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

    [Fact]
    public void ClassifyRecordCountRefusal_covers_unrecorded_cursor_and_lock_fallbacks()
    {
        // Defensive fallback arms for cursor/lock combinations not reachable via current TryNegotiateCursor.
        Failure? fKeysetBadLock = RecordsetObject.ClassifyRecordCountRefusal(
            isMdb: false, 2, 1, 9, hasBookmarks: true, AdoRecordCountSource.Keyset, AdoSelectShape.PlainSingleTable);
        Assert.NotNull(fKeysetBadLock);
        Assert.Equal(FailureKind.NotModelled, fKeysetBadLock!.Kind);
        Assert.StartsWith("NotModelled.AdoRecordCount.CursorLock", fKeysetBadLock.Description, StringComparison.Ordinal);

        Failure? fNonBookmarkBadCursor = RecordsetObject.ClassifyRecordCountRefusal(
            isMdb: false, 2, 3, 1, hasBookmarks: false, AdoRecordCountSource.Select, AdoSelectShape.PlainSingleTable);
        Assert.NotNull(fNonBookmarkBadCursor);
        Assert.Equal(FailureKind.NotModelled, fNonBookmarkBadCursor!.Kind);
        Assert.StartsWith("NotModelled.AdoRecordCount.CursorLock", fNonBookmarkBadCursor.Description, StringComparison.Ordinal);

        Failure? fClientBadLock = RecordsetObject.ClassifyRecordCountRefusal(
            isMdb: false, 3, 3, 2, hasBookmarks: true, AdoRecordCountSource.Select, AdoSelectShape.PlainSingleTable);
        Assert.NotNull(fClientBadLock);
        Assert.Equal(FailureKind.NotModelled, fClientBadLock!.Kind);
        Assert.StartsWith("NotModelled.AdoRecordCount.CursorLock", fClientBadLock.Description, StringComparison.Ordinal);

        // Defensive shape arm: CalcDb.Sql currently refuses SELECT without FROM at parse time (NotModelled.NoFrom),
        // but ADO still classifies NoFrom explicitly if a no-FROM plan ever opens.
        Failure? fNoFrom = RecordsetObject.ClassifyRecordCountRefusal(
            isMdb: false, 2, 3, 1, hasBookmarks: true, AdoRecordCountSource.Select, AdoSelectShape.NoFrom);
        Assert.NotNull(fNoFrom);
        Assert.Equal(FailureKind.NotModelled, fNoFrom!.Kind);
        Assert.StartsWith("NotModelled.AdoRecordCount.NoFrom", fNoFrom.Description, StringComparison.Ordinal);
    }

    private static void AccdbServerStaticRecordedShapesAndRefusals(string accdbPath, string ado1bPath)
    {
        nint cn = OpenAceConnection(accdbPath, mode: 1);
        try
        {
            // Recorded ACCDB Server Static (2, 3, 1) SQL shapes (evidence/r-q2-expr-sql/demand-ado-events-shapes #16-105):
            AssertRecordedCount(cn, loc: 2, cur: 3, lck: 1, opt: 1, "SELECT n FROM Nm", expectedCount: 256);
            AssertRecordedCount(cn, loc: 2, cur: 3, lck: 1, opt: 1, "SELECT n FROM Nm ORDER BY n DESC", expectedCount: 256);
            AssertRecordedCount(cn, loc: 2, cur: 3, lck: 1, opt: 1, "SELECT n, Count(*) AS c FROM Nm GROUP BY n", expectedCount: 256);
            AssertRecordedCount(cn, loc: 2, cur: 3, lck: 1, opt: 1, "SELECT DISTINCT n Mod 7 AS k FROM Nm", expectedCount: 7);
            AssertRecordedCount(cn, loc: 2, cur: 3, lck: 1, opt: 1, "SELECT TOP 10 n FROM Nm ORDER BY n DESC", expectedCount: 10);
            AssertRecordedCount(cn, loc: 2, cur: 3, lck: 1, opt: 1, "SELECT n FROM Nm WHERE n < 0", expectedCount: 0);

            // Unrecorded ACCDB Server Static (2, 3, 1) SQL shapes: Open() and reading field 0 succeed; RecordCount refuses by name.
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT a.n AS an, b.n AS bn FROM Nm AS a INNER JOIN Nm AS b ON a.n = b.n WHERE a.n < 3 ORDER BY a.n", "NotModelled.AdoRecordCount.Join");
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT n FROM Nm WHERE n = 1 UNION SELECT n FROM Nm WHERE n = 2", "NotModelled.AdoRecordCount.Union");
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT d.n FROM (SELECT n FROM Nm WHERE n < 3) AS d ORDER BY d.n", "NotModelled.AdoRecordCount.Subquery");
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT Count(*) AS c FROM Nm", "NotModelled.AdoRecordCount.UngroupedAggregate");
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT Count(*) + 1 AS c FROM Nm", "NotModelled.AdoRecordCount.UngroupedAggregate");
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT n, Count(*) AS c FROM Nm GROUP BY n HAVING Count(*) > 0", "NotModelled.AdoRecordCount.GroupByHaving");
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT n, Count(*) AS c FROM Nm GROUP BY n ORDER BY n", "NotModelled.AdoRecordCount.GroupByOrderBy");
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT TOP 3 n, Count(*) AS c FROM Nm GROUP BY n", "NotModelled.AdoRecordCount.GroupByCombined");
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT DISTINCT n FROM Nm ORDER BY n", "NotModelled.AdoRecordCount.DistinctOrderBy");
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT DISTINCT TOP 3 n FROM Nm", "NotModelled.AdoRecordCount.DistinctCombined");
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT DISTINCTROW n FROM Nm WHERE n < 5 ORDER BY n", "NotModelled.AdoRecordCount.DistinctRow");
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT TOP 5 PERCENT n FROM Nm ORDER BY n DESC", "NotModelled.AdoRecordCount.TopPercent");
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT TOP 5 n FROM Nm", "NotModelled.AdoRecordCount.TopWithoutOrderBy");
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT @@IDENTITY", "NotModelled.AdoRecordCount.SelectIdentity");
            AssertRecordCountRefusal(cn, 2, 3, 1, 2, "Nm", "NotModelled.AdoRecordCount.TableSourceCursor");
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }

        // Also verify collapsing GROUP BY and DISTINCT on a field with repeated values (table T in r-ado-1b-main.accdb, 7 rows -> 2 distinct Y values).
        nint cnT = OpenAceConnection(ado1bPath, mode: 1);
        try
        {
            AssertRecordedCount(cnT, loc: 2, cur: 3, lck: 1, opt: 1, "SELECT Y, Count(*) AS c FROM T GROUP BY Y", expectedCount: 2);
            AssertRecordedCount(cnT, loc: 2, cur: 3, lck: 1, opt: 1, "SELECT DISTINCT Y FROM T", expectedCount: 2);
        }
        finally
        {
            Assert.Null(Call(cnT, "Close").Failure);
            Release(cnT);
        }
    }

    private static void AccdbNonBookmarkCursors(string accdbPath)
    {
        // Writable ACCDB so Dynamic (2, 2) and (2, 3) are both exercised alongside ForwardOnly (0, 1).
        nint cn = OpenAceConnection(accdbPath);
        try
        {
            // Recorded plain single-table SELECT on non-bookmark cursors -> RecordCount = -1:
            // r-str-ado-act/ado-rs-cursors, r-fix-wire-ado/ado-rs-cursors-writable, r-q-ado-misc/cursor-external-visibility #376-378.
            AssertRecordedCount(cn, loc: 2, cur: 0, lck: 1, opt: 1, "SELECT n FROM Nm ORDER BY n", expectedCount: -1);
            AssertRecordedCount(cn, loc: 2, cur: 2, lck: 2, opt: 1, "SELECT n FROM Nm ORDER BY n", expectedCount: -1);
            AssertRecordedCount(cn, loc: 2, cur: 2, lck: 3, opt: 1, "SELECT n FROM Nm ORDER BY n", expectedCount: -1);

            // Connection.Execute on Server cursor -> RecordCount = -1 for plain SELECT (r-fix-wire-ado/ado-execute-results-rows).
            Outcome execPlain = Call(cn, "Execute", "SELECT n FROM Nm ORDER BY n", Missing, 1);
            Assert.Null(execPlain.Failure);
            nint rExec = (nint)execPlain.Value!;
            try
            {
                Assert.Equal(-1, Get(rExec, "RecordCount"));
                Assert.Null(Call(rExec, "Close").Failure);
            }
            finally
            {
                Release(rExec);
            }

            // Unrecorded non-plain shapes on ForwardOnly (2, 0, 1): Open() and reading succeed; RecordCount refuses by name.
            AssertRecordCountRefusal(cn, 2, 0, 1, 1, "SELECT n, Count(*) AS c FROM Nm GROUP BY n", "NotModelled.AdoRecordCount.NonBookmarkGroupBy");
            AssertRecordCountRefusal(cn, 2, 0, 1, 1, "SELECT DISTINCT n Mod 7 AS k FROM Nm", "NotModelled.AdoRecordCount.NonBookmarkDistinct");
            AssertRecordCountRefusal(cn, 2, 0, 1, 1, "SELECT TOP 10 n FROM Nm ORDER BY n DESC", "NotModelled.AdoRecordCount.NonBookmarkTop");
            AssertRecordCountRefusal(cn, 2, 0, 1, 1, "SELECT Count(*) AS c FROM Nm", "NotModelled.AdoRecordCount.UngroupedAggregate");
            AssertRecordCountRefusal(cn, 2, 0, 1, 1, "SELECT @@IDENTITY", "NotModelled.AdoRecordCount.SelectIdentity");
            AssertRecordCountRefusal(cn, 2, 0, 1, 2, "Nm", "NotModelled.AdoRecordCount.TableSourceCursor");
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void AccdbServerKeysetEligibleAndIneligible(string accdbPath)
    {
        nint cn = OpenAceConnection(accdbPath);
        try
        {
            // Recorded eligible single-table base-column SELECT on Server Keyset (2, 1, 1) and (2, 1, 3):
            // r-ado-1b/rs-lock-matrix, r-str-ado-act/ado-rs-cursors, r-fix-wire-ado/ado-rs-cursors-writable.
            AssertRecordedCount(cn, loc: 2, cur: 1, lck: 1, opt: 1, "SELECT n FROM Nm ORDER BY n", expectedCount: 256);
            AssertRecordedCount(cn, loc: 2, cur: 1, lck: 3, opt: 1, "SELECT n FROM Nm ORDER BY n", expectedCount: 256);

            // Unrecorded keyset-ineligible SELECTs on Server Keyset (2, 1, 1): Open() and row reading succeed;
            // RecordCount refuses NotModelled.AdoRecordCount.ServerKeysetIneligibleSelect without eager MoveLast().
            AssertRecordCountRefusal(cn, 2, 1, 1, 1, "SELECT n + 1 AS p FROM Nm ORDER BY n", "NotModelled.AdoRecordCount.ServerKeysetIneligibleSelect");
            AssertRecordCountRefusal(cn, 2, 1, 1, 1, "SELECT n, Count(*) AS c FROM Nm GROUP BY n", "NotModelled.AdoRecordCount.ServerKeysetIneligibleSelect");
            AssertRecordCountRefusal(cn, 2, 1, 1, 1, "SELECT DISTINCT n Mod 7 AS k FROM Nm", "NotModelled.AdoRecordCount.ServerKeysetIneligibleSelect");
            AssertRecordCountRefusal(cn, 2, 1, 1, 1, "SELECT TOP 10 n FROM Nm ORDER BY n DESC", "NotModelled.AdoRecordCount.ServerKeysetIneligibleSelect");
            AssertRecordCountRefusal(cn, 2, 1, 1, 1, "SELECT Count(*) AS c FROM Nm", "NotModelled.AdoRecordCount.ServerKeysetIneligibleSelect");
            AssertRecordCountRefusal(cn, 2, 1, 1, 1, "SELECT @@IDENTITY", "NotModelled.AdoRecordCount.SelectIdentity");
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void AccdbClientStaticRecordedAndRefusals(string accdbPath)
    {
        nint cn = OpenAceConnection(accdbPath);
        try
        {
            // Recorded plain single-table SELECT on Client Static (3, 3, 1/3/4):
            // r-ado-1b/rs-lock-matrix, r-ado-1b/conn-cursorlocation-inherit, r-str-ado-act/ado-rs-cursors.
            AssertRecordedCount(cn, loc: 3, cur: 3, lck: 1, opt: 1, "SELECT n FROM Nm", expectedCount: 256);
            AssertRecordedCount(cn, loc: 3, cur: 3, lck: 3, opt: 1, "SELECT n FROM Nm ORDER BY n DESC", expectedCount: 256);
            AssertRecordedCount(cn, loc: 3, cur: 3, lck: 4, opt: 1, "SELECT n FROM Nm ORDER BY n", expectedCount: 256);

            // Unrecorded non-plain shapes on Client Static (3, 3, 1): Open() and reading field 0 succeed; RecordCount refuses by name.
            AssertRecordCountRefusal(cn, 3, 3, 1, 1, "SELECT n, Count(*) AS c FROM Nm GROUP BY n", "NotModelled.AdoRecordCount.ClientGroupBy");
            AssertRecordCountRefusal(cn, 3, 3, 1, 1, "SELECT DISTINCT n Mod 7 AS k FROM Nm", "NotModelled.AdoRecordCount.ClientDistinct");
            AssertRecordCountRefusal(cn, 3, 3, 1, 1, "SELECT TOP 10 n FROM Nm ORDER BY n DESC", "NotModelled.AdoRecordCount.ClientTop");
            AssertRecordCountRefusal(cn, 3, 3, 1, 1, "SELECT a.n AS an, b.n AS bn FROM Nm AS a INNER JOIN Nm AS b ON a.n = b.n WHERE a.n < 3 ORDER BY a.n", "NotModelled.AdoRecordCount.Join");
            AssertRecordCountRefusal(cn, 3, 3, 1, 1, "SELECT n FROM Nm WHERE n = 1 UNION SELECT n FROM Nm WHERE n = 2", "NotModelled.AdoRecordCount.Union");
            AssertRecordCountRefusal(cn, 3, 3, 1, 1, "SELECT d.n FROM (SELECT n FROM Nm WHERE n < 3) AS d ORDER BY d.n", "NotModelled.AdoRecordCount.Subquery");
            AssertRecordCountRefusal(cn, 3, 3, 1, 1, "SELECT Count(*) AS c FROM Nm", "NotModelled.AdoRecordCount.UngroupedAggregate");
            AssertRecordCountRefusal(cn, 3, 3, 1, 2, "Nm", "NotModelled.AdoRecordCount.TableSourceCursor");
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void MdbAllCursorsRefuseRecordCount(string mdbPath)
    {
        // Zero retained ACE records read ADODB.Recordset.RecordCount on an MDB database across any cursor or SQL shape.
        nint cn = OpenAceConnection(mdbPath, mode: 1);
        try
        {
            AssertRecordCountRefusal(cn, 2, 0, 1, 1, "SELECT ID FROM Items ORDER BY ID", "NotModelled.AdoRecordCount.Mdb");
            AssertRecordCountRefusal(cn, 2, 1, 1, 1, "SELECT ID FROM Items ORDER BY ID", "NotModelled.AdoRecordCount.Mdb");
            AssertRecordCountRefusal(cn, 2, 3, 1, 1, "SELECT ID FROM Items ORDER BY ID", "NotModelled.AdoRecordCount.Mdb");
            AssertRecordCountRefusal(cn, 3, 3, 1, 1, "SELECT ID FROM Items ORDER BY ID", "NotModelled.AdoRecordCount.Mdb");

            Outcome execOut = Call(cn, "Execute", "SELECT ID FROM Items ORDER BY ID", Missing, 1);
            Assert.Null(execOut.Failure);
            nint rExec = (nint)execOut.Value!;
            try
            {
                AssertNotModelled(TryGet(rExec, "RecordCount"), "NotModelled.AdoRecordCount.Mdb");
                Assert.Null(Call(rExec, "Close").Failure);
            }
            finally
            {
                Release(rExec);
            }
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void AssertRecordedCount(nint cn, int loc, int cur, int lck, int opt, string sql, int expectedCount)
    {
        nint rs = New(OwnRecordset);
        try
        {
            Assert.Null(Put(rs, "CursorLocation", loc).Failure);
            Assert.Null(Call(rs, "Open", sql, new Dispatch(cn), cur, lck, opt).Failure);
            Assert.Equal(expectedCount, Get(rs, "RecordCount"));
            Assert.Null(Call(rs, "Close").Failure);
        }
        finally
        {
            Release(rs);
        }
    }

    private static void AssertRecordCountRefusal(nint cn, int loc, int cur, int lck, int opt, string sql, string expectedNotModelled)
    {
        nint rs = New(OwnRecordset);
        try
        {
            Assert.Null(Put(rs, "CursorLocation", loc).Failure);
            Assert.Null(Call(rs, "Open", sql, new Dispatch(cn), cur, lck, opt).Failure);
            AssertNotModelled(TryGet(rs, "RecordCount"), expectedNotModelled);
            // Confirm Open() and row reading remain intact despite RecordCount refusal.
            if (!(bool)Get(rs, "EOF")!)
            {
                nint flds = (nint)Get(rs, "Fields")!;
                nint f0 = (nint)Get(flds, "Item", 0)!;
                try
                {
                    Assert.Null(TryGet(f0, "Value").Failure);
                }
                finally
                {
                    Release(f0);
                    Release(flds);
                }
            }
            Assert.Null(Call(rs, "Close").Failure);
        }
        finally
        {
            Release(rs);
        }
    }

    private static void AssertNotModelled(Outcome outcome, string expectedReason)
    {
        Assert.NotNull(outcome.Failure);
        Error err = outcome.Failure!.Value;
        Assert.Equal(HResults.E_NOTIMPL, err.Scode);
        Assert.NotNull(err.Description);
        Assert.StartsWith(expectedReason, err.Description, StringComparison.Ordinal);
    }
}
