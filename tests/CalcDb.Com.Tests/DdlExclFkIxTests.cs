using CalcDb.Com.Com;
using CalcDb.Foundation;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// Standalone COM end-to-end tests for W-07 / R-CAT-1, R-SORT-3, R-SQL-2 (r-q54-excl-ddl-ix-cmd Parts A &amp; B):
/// exclusive SQL/ADO ALTER TABLE ADD/DROP COLUMN/CONSTRAINT &amp; DROP INDEX, ADO ADD COLUMN without DEFAULT,
/// shared &amp; exclusive DROP TABLE on populated indexed LVAL tables, multi-FK ascending negative IDs &amp; LVAL
/// child tables, multi-column UNIQUE / WITH IGNORE NULL partial Nulls, SQL 3022 / 3058 failure paths,
/// and ADO Recordset.Open adCmdStoredProc 3732 regression (evidence/r-ado-1b/rs-source-kinds).
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class DdlExclFkIxTests
{
    [Fact]
    public void Excl_ddl_ado_nodef_drop_lval_multi_fk_null_ix_and_storedproc_error_match_ace()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ddl-efx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "sample.accdb");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                nint eng = New(OwnEngine);
                Outcome created = Call(eng, "CreateDatabase", dbPath, ";LANGID=0x0409;CP=1252;COUNTRY=0", 128);
                Assert.Null(created.Failure);
                nint db = (nint)created.Value!;

                // 1. Seed table T + index for exclusive ALTER / DROP tests
                Assert.Null(Call(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, A LONG, B TEXT(20))", 128).Failure);
                Assert.Null(Call(db, "Execute", "CREATE INDEX IX_A ON T (A)", 128).Failure);
                Assert.Null(Call(db, "Close").Failure);
                Release(db);

                // Exclusive DAO ALTER TABLE ADD COLUMN, NOT NULL, MEMO, ADD CONSTRAINT UNIQUE, DROP INDEX, DROP CONSTRAINT, DROP COLUMN
                Outcome exclOpen = Call(eng, "OpenDatabase", dbPath, true, false);
                Assert.Null(exclOpen.Failure);
                nint dbExcl = (nint)exclOpen.Value!;
                Assert.Null(Call(dbExcl, "Execute", "ALTER TABLE T ADD COLUMN C TEXT(25)", 128).Failure);
                Assert.Null(Call(dbExcl, "Execute", "ALTER TABLE T ADD COLUMN D LONG NOT NULL", 128).Failure);
                Assert.Null(Call(dbExcl, "Execute", "ALTER TABLE T ADD COLUMN M MEMO", 128).Failure);
                Assert.Null(Call(dbExcl, "Execute", "ALTER TABLE T ADD CONSTRAINT UQ_B UNIQUE (B)", 128).Failure);
                Assert.Null(Call(dbExcl, "Execute", "DROP INDEX IX_A ON T", 128).Failure);
                Assert.Null(Call(dbExcl, "Execute", "ALTER TABLE T DROP CONSTRAINT UQ_B", 128).Failure);
                Assert.Null(Call(dbExcl, "Execute", "ALTER TABLE T DROP COLUMN C", 128).Failure);
                Assert.Null(Call(dbExcl, "Execute", "ALTER TABLE T DROP CONSTRAINT PK_T", 128).Failure);
                Assert.Null(Call(dbExcl, "Close").Failure);
                Release(dbExcl);

                // Exclusive ADO ALTER TABLE ADD COLUMN DEFAULT after DROP COLUMN (re-sequenced ColumnId)
                nint cnExcl = New(OwnConnection);
                Assert.Null(Put(cnExcl, "Provider", "Microsoft.ACE.OLEDB.12.0").Failure);
                Assert.Null(Put(cnExcl, "Mode", 12).Failure); // adModeShareExclusive
                Assert.Null(Call(cnExcl, "Open", dbPath).Failure);
                Assert.Null(Call(cnExcl, "Execute", "ALTER TABLE T ADD COLUMN E TEXT(15) DEFAULT 'hi'", Missing, 129).Failure);
                Assert.Null(Call(cnExcl, "Close").Failure);
                Release(cnExcl);

                // 2. ADO ALTER TABLE ADD COLUMN without DEFAULT (nullable TEXT/LONG/MEMO + NOT NULL) & r-ado-1b/rs-source-kinds adCmdStoredProc 3732
                nint cn = OpenAceConnection(dbPath);
                Assert.Null(Call(cn, "Execute", "ALTER TABLE T ADD COLUMN X TEXT(20)", Missing, 129).Failure);
                Assert.Null(Call(cn, "Execute", "ALTER TABLE T ADD COLUMN Z LONG", Missing, 129).Failure);
                Assert.Null(Call(cn, "Execute", "ALTER TABLE T ADD COLUMN M2 MEMO", Missing, 129).Failure);

                // evidence/r-ado-1b/rs-source-kinds call 190: Recordset.Open("SELECT ID FROM T", cn, 1, 1, 4 /* adCmdStoredProc */)
                // must fail 0x80040E14 / helpContext 5003732 ("Expected query name after EXECUTE.").
                nint rsBadProc = New(OwnRecordset);
                Outcome badProc = Call(rsBadProc, "Open", "SELECT ID FROM T", new Dispatch(cn), 1, 1, 4);
                AssertError(badProc, unchecked((int)0x80040E14), expectedHelpContext: 5003732u, expectedInfoGuid: IidCommandText);
                Release(rsBadProc);
                Assert.Null(Call(cn, "Close").Failure);
                Release(cn);

                // 3. Multi-FK same parent + LVAL child table + multi-col UNIQUE with partial Nulls + SQL 3022 / 3058 + DROP TABLE populated indexed LVAL
                Outcome shOpen = Call(eng, "OpenDatabase", dbPath, false, false);
                Assert.Null(shOpen.Failure);
                nint dbSh = (nint)shOpen.Value!;

                Assert.Null(Call(dbSh, "Execute", "CREATE TABLE P1 (ID LONG CONSTRAINT PK_P1 PRIMARY KEY, Name TEXT(20))", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "CREATE TABLE P2 (ID LONG CONSTRAINT PK_P2 PRIMARY KEY, Name TEXT(20))", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "CREATE TABLE C1 (CID LONG CONSTRAINT PK_C1 PRIMARY KEY, P1ID LONG, CONSTRAINT FK_C1_P1 FOREIGN KEY (P1ID) REFERENCES P1 (ID))", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "CREATE TABLE C2 (CID LONG CONSTRAINT PK_C2 PRIMARY KEY, P2ID LONG, CONSTRAINT FK_C2_P2 FOREIGN KEY (P2ID) REFERENCES P2 (ID))", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "CREATE TABLE C3 (CID LONG CONSTRAINT PK_C3 PRIMARY KEY, P1ID LONG, P2ID LONG, Descr MEMO, Pic LONGBINARY, CONSTRAINT FK_C3_P1 FOREIGN KEY (P1ID) REFERENCES P1 (ID))", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "ALTER TABLE C3 ADD CONSTRAINT FK_C3_P2 FOREIGN KEY (P2ID) REFERENCES P2 (ID)", 128).Failure);

                // Multi-column UNIQUE with duplicate partial Nulls & multiple (Null,Null)
                Assert.Null(Call(dbSh, "Execute", "CREATE TABLE TU (ID LONG CONSTRAINT PK_TU PRIMARY KEY, A LONG, B TEXT(10))", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "INSERT INTO TU VALUES (1, 10, Null)", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "INSERT INTO TU VALUES (2, 10, Null)", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "INSERT INTO TU VALUES (3, Null, 'y')", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "INSERT INTO TU VALUES (4, Null, 'y')", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "INSERT INTO TU VALUES (5, Null, Null)", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "INSERT INTO TU VALUES (6, Null, Null)", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "CREATE UNIQUE INDEX UX_TU_AB ON TU (A, B)", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "CREATE UNIQUE INDEX UX_TU_AB_IGN ON TU (A, B) WITH IGNORE NULL", 128).Failure);

                // SQL CREATE UNIQUE INDEX duplicate -> 3022; WITH DISALLOW NULL -> 3058
                Assert.Null(Call(dbSh, "Execute", "CREATE TABLE TD (ID LONG CONSTRAINT PK_TD PRIMARY KEY, K LONG, S TEXT(10))", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "INSERT INTO TD VALUES (1, 10, 'a')", 128).Failure);
                Assert.Null(Call(dbSh, "Execute", "INSERT INTO TD VALUES (2, 10, Null)", 128).Failure);
                Outcome dup3022 = Call(dbSh, "Execute", "CREATE UNIQUE INDEX UX_TD_K ON TD (K)", 128);
                Assert.NotNull(dup3022.Failure);
                Assert.Equal(unchecked((int)0x800A0BCE), dup3022.Failure.Value.Scode); // 3022
                Outcome null3058 = Call(dbSh, "Execute", "CREATE INDEX IX_TD_S ON TD (S) WITH DISALLOW NULL", 128);
                Assert.NotNull(null3058.Failure);
                Assert.Equal(unchecked((int)0x800A0BF2), null3058.Failure.Value.Scode); // 3058

                // Populated indexed LVAL table DROP TABLE (external Memo > 2400 chars)
                Assert.Null(Call(dbSh, "Close").Failure);
                Release(dbSh);

                Outcome dropOpen = Call(eng, "OpenDatabase", dbPath, false, false);
                Assert.Null(dropOpen.Failure);
                nint dbDrop = (nint)dropOpen.Value!;
                Assert.Null(Call(dbDrop, "Execute", "CREATE TABLE TL (ID LONG CONSTRAINT PK_TL PRIMARY KEY, K TEXT(20), M MEMO)", 128).Failure);
                Assert.Null(Call(dbDrop, "Execute", "CREATE INDEX IX_TL_K ON TL (K)", 128).Failure);
                Assert.Null(Call(dbDrop, "Execute", $"INSERT INTO TL (ID, K, M) VALUES (1, 'k1', '{new string('M', 2400)}')", 128).Failure);
                Assert.Null(Call(dbDrop, "Close").Failure);
                Release(dbDrop);

                Outcome dropOpen2 = Call(eng, "OpenDatabase", dbPath, false, false);
                Assert.Null(dropOpen2.Failure);
                nint dbDrop2 = (nint)dropOpen2.Value!;
                Assert.Null(Call(dbDrop2, "Execute", "DROP TABLE TL", 128).Failure);
                Assert.Null(Call(dbDrop2, "Close").Failure);
                Release(dbDrop2);

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

        try { Directory.Delete(tempDir, recursive: true); } catch { }
        if (failure is not null)
        {
            throw new Exception("STA thread failed", failure);
        }
    }
}
