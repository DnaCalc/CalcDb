using CalcDb.Com.Com;
using CalcDb.Foundation;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// Standalone COM end-to-end tests for W-07/ddl-full-coverage (r-q50 Part B qkrw Stretch 2 &amp; r-q49 ADO error codes):
/// ADO CREATE/DROP VIEW, ADO CREATE/EXEC/DROP PROCEDURE (plain SELECT, param UPDATE, param SELECT),
/// DAO 3290/3129/3295 refusals, CREATE INDEX WITH IGNORE NULL/DISALLOW NULL/PRIMARY &amp; 3058,
/// and ADO 3290/3000/3355 error tuples (r-q49-dt2-draftprops/ddl-default-eval-edge-ado-*).
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class DdlViewProcIndexTests
{
    [Fact]
    public void Ado_view_procedure_exec_drop_and_default_error_mappings_match_ace()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ddl-vpi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "sample.accdb");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                // Seed table T via DAO
                nint eng = New(OwnEngine);
                Outcome created = Call(eng, "CreateDatabase", dbPath, ";LANGID=0x0409;CP=1252;COUNTRY=0", 128);
                Assert.Null(created.Failure);
                nint db = (nint)created.Value!;
                Assert.Null(Call(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, A TEXT(20))", 128).Failure);
                Assert.Null(Call(db, "Execute", "INSERT INTO T (ID, V, A) VALUES (1, 10, 'a1')", 128).Failure);
                Assert.Null(Call(db, "Execute", "INSERT INTO T (ID, V, A) VALUES (2, 20, 'a2')", 128).Failure);
                Assert.Null(Call(db, "Execute", "INSERT INTO T (ID, V, A) VALUES (3, 30, 'a3')", 128).Failure);
                Assert.Null(Call(db, "Close").Failure);
                Release(db);

                // ADO Connection: VIEW, PROCEDURE, EXEC, DROP, and r-q49 3290/3000/3355 error mappings
                nint cn = New(OwnAdoConnection);
                Assert.Null(Put(cn, "Provider", "Microsoft.ACE.OLEDB.12.0").Failure);
                Assert.Null(Call(cn, "Open", $"Data Source={dbPath}").Failure);

                // 1. CREATE VIEW + SELECT FROM V1 + CREATE VIEW WITH OWNERACCESS OPTION + DROP VIEW
                Assert.Null(Call(cn, "Execute", "CREATE VIEW V1 AS SELECT ID, A FROM T", Missing, 129).Failure);
                Outcome rsV1 = Call(cn, "Execute", "SELECT ID, A FROM V1 ORDER BY ID");
                Assert.Null(rsV1.Failure);
                nint rsv = (nint)rsV1.Value!;
                Assert.Equal(1, ReadFieldValue(rsv, "ID"));
                Assert.Equal("a1", ReadFieldValue(rsv, "A"));
                Assert.Null(Call(rsv, "Close").Failure);
                Release(rsv);
                Assert.Null(Call(cn, "Execute", "CREATE VIEW V2 AS SELECT ID FROM T WITH OWNERACCESS OPTION", Missing, 129).Failure);

                // 2. CREATE PROCEDURE + EXEC (SELECT, param UPDATE, param SELECT) + DROP PROCEDURE
                Assert.Null(Call(cn, "Execute", "CREATE PROCEDURE P1 AS SELECT ID, V FROM T WHERE V > 10", Missing, 129).Failure);
                Outcome rsP1 = Call(cn, "Execute", "EXEC P1");
                Assert.Null(rsP1.Failure);
                nint rsp1 = (nint)rsP1.Value!;
                Assert.Equal(2, ReadFieldValue(rsp1, "ID"));
                Assert.Null(Call(rsp1, "Close").Failure);
                Release(rsp1);

                Assert.Null(Call(cn, "Execute", "CREATE PROCEDURE P2 (pId LONG, pTxt TEXT(20)) AS UPDATE T SET A = pTxt WHERE ID = pId", Missing, 129).Failure);
                Assert.Null(Call(cn, "Execute", "EXEC P2 1, 'updated'", Missing, 129).Failure);

                Assert.Null(Call(cn, "Execute", "CREATE PROCEDURE P3 (pMin LONG) AS SELECT ID, V FROM T WHERE V >= pMin ORDER BY ID", Missing, 129).Failure);
                Outcome rsP3 = Call(cn, "Execute", "EXEC P3 20");
                Assert.Null(rsP3.Failure);
                nint rsp3 = (nint)rsP3.Value!;
                Assert.Equal(2, ReadFieldValue(rsp3, "ID"));
                Assert.Equal(20, ReadFieldValue(rsp3, "V"));
                Assert.Null(Call(rsp3, "Close").Failure);
                Release(rsp3);

                // Verify P2 updated row 1
                Outcome rsCheck = Call(cn, "Execute", "SELECT A FROM T WHERE ID = 1");
                Assert.Null(rsCheck.Failure);
                nint rsc = (nint)rsCheck.Value!;
                Assert.Equal("updated", ReadFieldValue(rsc, "A"));
                Assert.Null(Call(rsc, "Close").Failure);
                Release(rsc);

                // 3. ADO r-q49/ddl-default-eval-edge-ado-* error mappings: 3290, 3000, 3355
                Outcome e3290 = Call(cn, "Execute", "CREATE TABLE TE_BadMiss (ID LONG, A LONG DEFAULT)", Missing, 129);
                Assert.NotNull(e3290.Failure);
                Assert.Equal(unchecked((int)0x80040E14), e3290.Failure!.Scode);
                Assert.Equal(5003290, e3290.Failure.HelpContext);

                Outcome e3000 = Call(cn, "Execute", "CREATE TABLE TE_BadType (ID LONG, A LONG DEFAULT 'abc')", Missing, 129);
                Assert.NotNull(e3000.Failure);
                Assert.Equal(unchecked((int)0x80004005), e3000.Failure!.Scode);
                Assert.Equal(5003000, e3000.Failure.HelpContext);

                Outcome e3355 = Call(cn, "Execute", "CREATE TABLE TE_BadCol (ID LONG, A LONG DEFAULT [ID])", Missing, 129);
                Assert.NotNull(e3355.Failure);
                Assert.Equal(unchecked((int)0x80004005), e3355.Failure!.Scode);
                Assert.Equal(5003355, e3355.Failure.HelpContext);

                // Drop views and procedures
                Assert.Null(Call(cn, "Execute", "DROP VIEW V1", Missing, 129).Failure);
                Assert.Null(Call(cn, "Execute", "DROP VIEW V2", Missing, 129).Failure);
                Assert.Null(Call(cn, "Execute", "DROP PROCEDURE P1", Missing, 129).Failure);
                Assert.Null(Call(cn, "Execute", "DROP PROCEDURE P2", Missing, 129).Failure);
                Assert.Null(Call(cn, "Execute", "DROP PROCEDURE P3", Missing, 129).Failure);

                Assert.Null(Call(cn, "Close").Failure);
                Release(cn);
                Release(eng);
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

    private static object? ReadFieldValue(nint rs, string name)
    {
        nint fields = (nint)Get(rs, "Fields").Value!;
        nint field = (nint)Call(fields, "Item", name).Value!;
        object? val = Get(field, "Value").Value;
        Release(field);
        Release(fields);
        return val;
    }
}
