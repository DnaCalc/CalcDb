using System.Text.Json;

using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Byte-for-byte replay of every stretch-1 <c>evidence/r-q39-querydef-bytes</c> question through
/// <see cref="QueryDefinitionCompiler.Compile"/>, <see cref="SchemaWriter.CreateQueryDef"/>, and
/// <see cref="TransactionCoordinator.PublishCloseCommit"/>, asserting every byte of every page both while the
/// database is still open (<c>pg1</c>/<c>pg2</c>) and after <c>Close</c> (<c>pg2</c>/<c>pg3</c>).
/// </summary>
public sealed class QueryDefBytesReplayTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    [Theory]
    [InlineData("select-star-accdb", "Q1", "SELECT * FROM T", null)]
    [InlineData("select-star-mdb", "Q1", "SELECT * FROM T", null)]
    [InlineData("select-cols-accdb", "Q1", "SELECT ID, V AS Val FROM T", null)]
    [InlineData("select-where-order-accdb", "Q1", "SELECT ID, V FROM T WHERE V > 10 ORDER BY ID DESC, V", null)]
    [InlineData("select-where-order-mdb", "Q1", "SELECT ID, V FROM T WHERE V > 10 ORDER BY ID DESC, V", null)]
    [InlineData("select-params-accdb", "Q1", "PARAMETERS pId Long, pW Text(20); SELECT ID, V FROM T WHERE ID = pId AND W = pW", null)]
    [InlineData("select-params-mdb", "Q1", "PARAMETERS pId Long, pW Text(20); SELECT ID, V FROM T WHERE ID = pId AND W = pW", null)]
    [InlineData("expr-len-32-accdb", "Q1", "SELECT ID FROM T WHERE W = 'aaaaaaaaaaaaaaaaaaaaaaaaaa'", null)]
    [InlineData("expr-len-33-accdb", "Q1", "SELECT ID FROM T WHERE W = 'aaaaaaaaaaaaaaaaaaaaaaaaaaa'", null)]
    [InlineData("long-expr-accdb", "Q1", "SELECT ID FROM T WHERE W = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'", null)]
    [InlineData("long-expr-mdb", "Q1", "SELECT ID FROM T WHERE W = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'", null)]
    [InlineData("action-delete-accdb", "QDel", "DELETE FROM T WHERE ID = 1", null)]
    [InlineData("action-update-accdb", "QUpd", "UPDATE T SET V = 1, W = 'x' WHERE ID = 1", null)]
    [InlineData("action-update-mdb", "QUpd", "UPDATE T SET V = 1, W = 'x' WHERE ID = 1", null)]
    [InlineData("action-insert-values-accdb", "QInsVal", "INSERT INTO T (ID, V, W) VALUES (1, 10, 'a')", null)]
    [InlineData("action-insert-select-accdb", "QInsSel", "INSERT INTO T (ID, V) SELECT ID, V FROM T WHERE V > 0", null)]
    [InlineData("action-select-into-accdb", "QMake", "SELECT ID, V INTO T2 FROM T WHERE V > 0", null)]
    [InlineData("crosstab-accdb", "QXtab", "TRANSFORM Count(ID) AS Cnt SELECT V FROM T GROUP BY V PIVOT W", null)]
    [InlineData("ddl-accdb", "QDdl", "CREATE TABLE T2 (A LONG)", null)]
    [InlineData("passthrough-accdb", "QPass", "SELECT 1", "ODBC;DSN=CalcDbProbeNoSuchDsn;")]
    [InlineData("union-accdb", "QUnion", "SELECT ID FROM T UNION SELECT V FROM T", null)]
    [InlineData("union-all-accdb", "QUnionAll", "SELECT ID FROM T UNION ALL SELECT V FROM T", null)]
    public void Single_query_matches_ace_byte_exact_open_and_close(string question, string name, string sql, string? connect)
    {
        Dictionary<string, byte[]> snaps = ReadSnapshots(question);
        byte[] before = snaps["pg0"];
        byte[] aceOpen = snaps["pg1"];
        byte[] aceClose = snaps["pg2"];

        (ulong created, ulong vacUpdated, ulong liveUpdated) = ExtractQueryDates(aceOpen, name);

        QueryDefinitionCompilation compiled = QueryDefinitionCompiler.Compile(sql, connect);
        Assert.True(compiled.Succeeded, $"{question}: compile failed: {compiled.Failure}");

        using var db = new WritableSqlFixture(before);
        SchemaWriter? writer = db.OpenSchemaWriter(out string? writerFailure);
        Assert.NotNull(writer);
        Assert.Null(writerFailure);

        var request = new CreateQueryDefRequest(name, compiled.Family, compiled.Rows!);
        SchemaOutcome outcome = writer!.CreateQueryDef(db.Workspace, request, created, liveUpdated, out int objectId, initialDateUpdateBits: vacUpdated);
        Assert.True(outcome.Succeeded, $"{question}: CreateQueryDef failed: {outcome.Refused?.NotWritten ?? outcome.Accepted.Detail}");
        Assert.Equal(int.MinValue, objectId);
        db.OnSchemaPublished(outcome.ExtentPages);

        AssertImageEqual($"{question} (after-create-open)", aceOpen, db.Image.Snapshot());

        (MutationOpen closeOpen, string? closeDetail) = db.Workspace.PublishCloseCommit(db.Cache);
        Assert.Equal(MutationOpen.None, closeOpen);
        Assert.Null(closeDetail);

        AssertImageEqual($"{question} (after-close)", aceClose, db.Image.Snapshot());

        // Round-trip readback through QueryCatalog and QuerySql.Render.
        using var verifyFile = new MemoryDatabaseFile(db.Image.Snapshot(), D.PageSize);
        var verifyCache = new PageCache(verifyFile, verifyFile.Incarnation, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(verifyCache, D, verifyFile.PageCount, out CatalogFailure? catFail)!;
        Assert.Null(catFail);
        QueryCatalog qcat = QueryCatalog.Read(verifyCache, D, catalog, verifyFile.PageCount, out string? qFail)!;
        Assert.Null(qFail);
        SavedQuery saved = Assert.Single(qcat.Queries);
        Assert.Equal(name, saved.Name);
        Assert.Equal(compiled.Family, saved.Family);
        RenderedSql rendered = QuerySql.Render(saved);
        Assert.Null(rendered.NotModelled);
        if (ReadRecordedSql(question) is { } recordedSql)
        {
            Assert.Equal(recordedSql, rendered.Text);
        }
        else
        {
            // expr-len-32-accdb, expr-len-33-accdb, long-expr-mdb do not reopen to read qdr.SQL; check the WHERE literal round-trip.
            Assert.Contains("WHERE W = 'aaa", rendered.Text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("two-queries-accdb")]
    [InlineData("two-queries-mdb")]
    public void Two_queries_match_ace_byte_exact_at_every_step(string question)
    {
        Dictionary<string, byte[]> snaps = ReadSnapshots(question);
        byte[] before = snaps["pg0"];
        byte[] aceAfterQ1 = snaps["pg1"];
        byte[] aceAfterQ2 = snaps["pg2"];
        byte[] aceClose = snaps["pg3"];

        (ulong q1Created, ulong q1VacUpd, ulong q1LiveUpd) = ExtractQueryDates(aceAfterQ1, "Q1");
        (ulong q2Created, ulong q2VacUpd, ulong q2LiveUpd) = ExtractQueryDates(aceAfterQ2, "Q2");

        QueryDefinitionCompilation c1 = QueryDefinitionCompiler.Compile("SELECT * FROM T");
        QueryDefinitionCompilation c2 = QueryDefinitionCompiler.Compile("SELECT ID FROM T WHERE V > 0");
        Assert.True(c1.Succeeded);
        Assert.True(c2.Succeeded);

        using var db = new WritableSqlFixture(before);

        SchemaWriter w1 = db.OpenSchemaWriter(out _)!;
        SchemaOutcome o1 = w1.CreateQueryDef(db.Workspace, new CreateQueryDefRequest("Q1", c1.Family, c1.Rows!), q1Created, q1LiveUpd, out int id1, initialDateUpdateBits: q1VacUpd);
        Assert.True(o1.Succeeded, $"{question} Q1 failed: {o1.Refused?.NotWritten ?? o1.Accepted.Detail}");
        Assert.Equal(int.MinValue, id1);
        db.OnSchemaPublished(o1.ExtentPages);
        AssertImageEqual($"{question} (after Q1 open)", aceAfterQ1, db.Image.Snapshot());

        SchemaWriter w2 = db.OpenSchemaWriter(out _)!;
        SchemaOutcome o2 = w2.CreateQueryDef(db.Workspace, new CreateQueryDefRequest("Q2", c2.Family, c2.Rows!), q2Created, q2LiveUpd, out int id2, initialDateUpdateBits: q2VacUpd);
        Assert.True(o2.Succeeded, $"{question} Q2 failed: {o2.Refused?.NotWritten ?? o2.Accepted.Detail}");
        Assert.Equal(int.MinValue + 1, id2);
        db.OnSchemaPublished(o2.ExtentPages);
        AssertImageEqual($"{question} (after Q2 open)", aceAfterQ2, db.Image.Snapshot());

        (MutationOpen closeOpen, string? closeDetail) = db.Workspace.PublishCloseCommit(db.Cache);
        Assert.Equal(MutationOpen.None, closeOpen);
        Assert.Null(closeDetail);
        AssertImageEqual($"{question} (after close)", aceClose, db.Image.Snapshot());
    }

    [Theory]
    [InlineData("draft-append-accdb")]
    [InlineData("temp-then-named-accdb")]
    [InlineData("temp-then-named-mdb")]
    public void Draft_and_temp_then_named_write_no_bytes_until_named_create_then_match_ace_byte_exact(string question)
    {
        Dictionary<string, byte[]> snaps = ReadSnapshots(question);
        byte[] before = snaps["pg0"];
        byte[] afterNoWrite = snaps["pg1"];
        byte[] aceOpen = snaps["pg2"];
        byte[] aceClose = snaps["pg3"];

        // Draft creation / temporary QueryDef changes zero bytes in ACE (r-q39).
        AssertImageEqual($"{question} (before-write)", before, afterNoWrite);

        (ulong created, ulong vacUpdated, ulong liveUpdated) = ExtractQueryDates(aceOpen, "Q1");

        QueryDefinitionCompilation compiled = QueryDefinitionCompiler.Compile("SELECT * FROM T");
        Assert.True(compiled.Succeeded);

        using var db = new WritableSqlFixture(before);
        SchemaWriter writer = db.OpenSchemaWriter(out _)!;
        SchemaOutcome outcome = writer.CreateQueryDef(db.Workspace, new CreateQueryDefRequest("Q1", compiled.Family, compiled.Rows!), created, liveUpdated, out int objectId, initialDateUpdateBits: vacUpdated);
        Assert.True(outcome.Succeeded, $"{question}: CreateQueryDef failed: {outcome.Refused?.NotWritten ?? outcome.Accepted.Detail}");
        Assert.Equal(int.MinValue, objectId);
        db.OnSchemaPublished(outcome.ExtentPages);

        AssertImageEqual($"{question} (after-create-open)", aceOpen, db.Image.Snapshot());

        (MutationOpen closeOpen, string? closeDetail) = db.Workspace.PublishCloseCommit(db.Cache);
        Assert.Equal(MutationOpen.None, closeOpen);
        Assert.Null(closeDetail);

        AssertImageEqual($"{question} (after-close)", aceClose, db.Image.Snapshot());
    }

    /// <summary>
    /// Oracle-fixture invariant check for <c>r-q39-querydef-bytes/temp-querydef-accdb</c>: confirms ACE itself wrote
    /// zero bytes open or closed (DAO-side zero-byte verification lives in <c>DaoCreateQueryDefTests</c>).
    /// </summary>
    [Fact]
    public void Temp_querydef_oracle_record_changes_no_bytes()
    {
        Dictionary<string, byte[]> snaps = ReadSnapshots("temp-querydef-accdb");
        AssertImageEqual("temp-querydef-accdb (open)", snaps["pg0"], snaps["pg1"]);
        AssertImageEqual("temp-querydef-accdb (close)", snaps["pg0"], snaps["pg2"]);
    }

    [Theory]
    [InlineData("sql-put-accdb", "SELECT ID, V FROM T WHERE V > 5 ORDER BY ID", QueryFamily.Select)]
    [InlineData("sql-put-mdb", "SELECT ID, V FROM T WHERE V > 5 ORDER BY ID", QueryFamily.Select)]
    [InlineData("sql-put-type-change-accdb", "UPDATE T SET V = 1 WHERE ID = 1", QueryFamily.Update)]
    public void Sql_put_matches_ace_byte_exact_open_and_close(string question, string newSql, QueryFamily expectedFamily)
    {
        Dictionary<string, byte[]> snaps = ReadSnapshots(question);
        byte[] before = snaps["pg0"];
        byte[] aceAfterS1 = snaps["pg1"];
        byte[] acePutOpen = snaps["pg2"];
        byte[] aceClose = snaps["pg3"];

        // Session 1: CreateQueryDef("Q1", "SELECT * FROM T") + Close.
        (ulong s1Created, ulong s1VacUpd, ulong s1LiveUpd) = ExtractQueryDates(aceAfterS1, "Q1");
        QueryDefinitionCompilation c1 = QueryDefinitionCompiler.Compile("SELECT * FROM T");
        Assert.True(c1.Succeeded);

        byte[] afterS1;
        using (var db1 = new WritableSqlFixture(before))
        {
            SchemaWriter w1 = db1.OpenSchemaWriter(out _)!;
            SchemaOutcome o1 = w1.CreateQueryDef(db1.Workspace, new CreateQueryDefRequest("Q1", c1.Family, c1.Rows!), s1Created, s1LiveUpd, out int id1, initialDateUpdateBits: s1VacUpd);
            Assert.True(o1.Succeeded, $"{question} s1 failed: {o1.Refused?.NotWritten ?? o1.Accepted.Detail}");
            Assert.Equal(int.MinValue, id1);
            db1.OnSchemaPublished(o1.ExtentPages);
            (MutationOpen close1, string? close1Detail) = db1.Workspace.PublishCloseCommit(db1.Cache);
            Assert.Equal(MutationOpen.None, close1);
            Assert.Null(close1Detail);
            afterS1 = db1.Image.Snapshot();
            AssertImageEqual($"{question} (after-s1)", aceAfterS1, afterS1);
        }

        // Session 2: QueryDef.SQL put + Close.
        (_, _, ulong s2Updated) = ExtractQueryDates(acePutOpen, "Q1");
        QueryDefinitionCompilation c2 = QueryDefinitionCompiler.Compile(newSql);
        Assert.True(c2.Succeeded, $"{question}: compile new SQL failed: {c2.Failure}");
        Assert.Equal(expectedFamily, c2.Family);

        using var db2 = new WritableSqlFixture(afterS1);
        SchemaWriter w2 = db2.OpenSchemaWriter(out _)!;
        SchemaOutcome o2 = w2.UpdateQueryDefSql(db2.Workspace, new UpdateQueryDefSqlRequest("Q1", c2.Family, c2.Rows!), s2Updated);
        Assert.True(o2.Succeeded, $"{question} sql-put failed: {o2.Refused?.NotWritten ?? o2.Accepted.Detail}");
        db2.OnSchemaPublished(o2.ExtentPages);
        AssertImageEqual($"{question} (after-put-open)", acePutOpen, db2.Image.Snapshot());

        (MutationOpen close2, string? close2Detail) = db2.Workspace.PublishCloseCommit(db2.Cache);
        Assert.Equal(MutationOpen.None, close2);
        Assert.Null(close2Detail);
        AssertImageEqual($"{question} (after-s2-close)", aceClose, db2.Image.Snapshot());

        // Round-trip readback through QueryCatalog and QuerySql.Render.
        using var verifyFile = new MemoryDatabaseFile(db2.Image.Snapshot(), D.PageSize);
        var verifyCache = new PageCache(verifyFile, verifyFile.Incarnation, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(verifyCache, D, verifyFile.PageCount, out CatalogFailure? catFail)!;
        Assert.Null(catFail);
        QueryCatalog qcat = QueryCatalog.Read(verifyCache, D, catalog, verifyFile.PageCount, out string? qFail)!;
        Assert.Null(qFail);
        SavedQuery saved = Assert.Single(qcat.Queries);
        Assert.Equal("Q1", saved.Name);
        Assert.Equal(expectedFamily, saved.Family);
        RenderedSql rendered = QuerySql.Render(saved);
        Assert.Null(rendered.NotModelled);
        Assert.Equal(ReadRecordedSql(question), rendered.Text);
    }

    [Theory]
    [InlineData("delete-querydef-accdb")]
    [InlineData("delete-querydef-mdb")]
    public void Delete_querydef_matches_ace_byte_exact_open_and_close(string question)
    {
        Dictionary<string, byte[]> snaps = ReadSnapshots(question);
        byte[] before = snaps["pg0"];
        byte[] aceAfterS1 = snaps["pg1"];
        byte[] aceDeleteOpen = snaps["pg2"];
        byte[] aceClose = snaps["pg3"];

        // Session 1: CreateQueryDef("Q1", "SELECT * FROM T") + Close.
        (ulong s1Created, ulong s1VacUpd, ulong s1LiveUpd) = ExtractQueryDates(aceAfterS1, "Q1");
        QueryDefinitionCompilation c1 = QueryDefinitionCompiler.Compile("SELECT * FROM T");
        Assert.True(c1.Succeeded);

        byte[] afterS1;
        using (var db1 = new WritableSqlFixture(before))
        {
            SchemaWriter w1 = db1.OpenSchemaWriter(out _)!;
            SchemaOutcome o1 = w1.CreateQueryDef(db1.Workspace, new CreateQueryDefRequest("Q1", c1.Family, c1.Rows!), s1Created, s1LiveUpd, out int id1, initialDateUpdateBits: s1VacUpd);
            Assert.True(o1.Succeeded, $"{question} s1 failed: {o1.Refused?.NotWritten ?? o1.Accepted.Detail}");
            Assert.Equal(int.MinValue, id1);
            db1.OnSchemaPublished(o1.ExtentPages);
            (MutationOpen close1, string? close1Detail) = db1.Workspace.PublishCloseCommit(db1.Cache);
            Assert.Equal(MutationOpen.None, close1);
            Assert.Null(close1Detail);
            afterS1 = db1.Image.Snapshot();
            AssertImageEqual($"{question} (after-s1)", aceAfterS1, afterS1);
        }

        // Session 2: QueryDefs.Delete("Q1") + Close.
        using var db2 = new WritableSqlFixture(afterS1);
        SchemaWriter w2 = db2.OpenSchemaWriter(out _)!;
        SchemaOutcome o2 = w2.DeleteQueryDef(db2.Workspace, "Q1", out int deletedId);
        Assert.True(o2.Succeeded, $"{question} delete failed: {o2.Refused?.NotWritten ?? o2.Accepted.Detail}");
        Assert.Equal(int.MinValue, deletedId);
        db2.OnSchemaPublished(o2.ExtentPages);
        AssertImageEqual($"{question} (after-delete-open)", aceDeleteOpen, db2.Image.Snapshot());

        (MutationOpen close2, string? close2Detail) = db2.Workspace.PublishCloseCommit(db2.Cache);
        Assert.Equal(MutationOpen.None, close2);
        Assert.Null(close2Detail);
        AssertImageEqual($"{question} (after-s2-close)", aceClose, db2.Image.Snapshot());

        // Verify zero queries remain on reopen.
        using var verifyFile = new MemoryDatabaseFile(db2.Image.Snapshot(), D.PageSize);
        var verifyCache = new PageCache(verifyFile, verifyFile.Incarnation, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(verifyCache, D, verifyFile.PageCount, out CatalogFailure? catFail)!;
        Assert.Null(catFail);
        QueryCatalog qcat = QueryCatalog.Read(verifyCache, D, catalog, verifyFile.PageCount, out string? qFail)!;
        Assert.Null(qFail);
        Assert.Empty(qcat.Queries);
    }

    [Fact]
    public void Delete_then_create_matches_ace_byte_exact_at_every_step()
    {
        const string question = "delete-then-create-accdb";
        Dictionary<string, byte[]> snaps = ReadSnapshots(question);
        byte[] before = snaps["pg0"];
        byte[] aceAfterS1 = snaps["pg1"];
        byte[] aceAfterDelete = snaps["pg2"];
        byte[] aceAfterQ2 = snaps["pg3"];
        byte[] aceClose = snaps["pg4"];

        // Session 1: CreateQueryDef("Q1", "SELECT * FROM T") + Close.
        (ulong s1Created, ulong s1VacUpd, ulong s1LiveUpd) = ExtractQueryDates(aceAfterS1, "Q1");
        QueryDefinitionCompilation c1 = QueryDefinitionCompiler.Compile("SELECT * FROM T");
        Assert.True(c1.Succeeded);

        byte[] afterS1;
        using (var db1 = new WritableSqlFixture(before))
        {
            SchemaWriter w1 = db1.OpenSchemaWriter(out _)!;
            SchemaOutcome o1 = w1.CreateQueryDef(db1.Workspace, new CreateQueryDefRequest("Q1", c1.Family, c1.Rows!), s1Created, s1LiveUpd, out int id1, initialDateUpdateBits: s1VacUpd);
            Assert.True(o1.Succeeded);
            Assert.Equal(int.MinValue, id1);
            db1.OnSchemaPublished(o1.ExtentPages);
            (MutationOpen close1, _) = db1.Workspace.PublishCloseCommit(db1.Cache);
            Assert.Equal(MutationOpen.None, close1);
            afterS1 = db1.Image.Snapshot();
            AssertImageEqual($"{question} (after-s1)", aceAfterS1, afterS1);
        }

        // Session 2: Delete("Q1"), CreateQueryDef("Q2", "SELECT ID FROM T"), Close.
        (ulong q2Created, ulong q2VacUpd, ulong q2LiveUpd) = ExtractQueryDates(aceAfterQ2, "Q2");
        QueryDefinitionCompilation c2 = QueryDefinitionCompiler.Compile("SELECT ID FROM T");
        Assert.True(c2.Succeeded);

        using var db2 = new WritableSqlFixture(afterS1);
        SchemaWriter wDel = db2.OpenSchemaWriter(out _)!;
        SchemaOutcome oDel = wDel.DeleteQueryDef(db2.Workspace, "Q1", out int deletedId);
        Assert.True(oDel.Succeeded, $"{question} delete failed: {oDel.Refused?.NotWritten ?? oDel.Accepted.Detail}");
        Assert.Equal(int.MinValue, deletedId);
        db2.OnSchemaPublished(oDel.ExtentPages);
        AssertImageEqual($"{question} (after-delete-open)", aceAfterDelete, db2.Image.Snapshot());

        SchemaWriter wQ2 = db2.OpenSchemaWriter(out _)!;
        SchemaOutcome oQ2 = wQ2.CreateQueryDef(db2.Workspace, new CreateQueryDefRequest("Q2", c2.Family, c2.Rows!), q2Created, q2LiveUpd, out int id2, initialDateUpdateBits: q2VacUpd);
        Assert.True(oQ2.Succeeded, $"{question} create Q2 failed: {oQ2.Refused?.NotWritten ?? oQ2.Accepted.Detail}");
        Assert.Equal(int.MinValue, id2);
        db2.OnSchemaPublished(oQ2.ExtentPages);
        AssertImageEqual($"{question} (after-q2-open)", aceAfterQ2, db2.Image.Snapshot());

        (MutationOpen close2, string? close2Detail) = db2.Workspace.PublishCloseCommit(db2.Cache);
        Assert.Equal(MutationOpen.None, close2);
        Assert.Null(close2Detail);
        AssertImageEqual($"{question} (after-s2-close)", aceClose, db2.Image.Snapshot());
    }

    [Fact]
    public void Stretch2_refusals_fail_by_name_without_writing()
    {
        // 1. SQL put introducing an external LVAL Expression (> 32 UTF-16 units) refuses without writing (r-q39 Open).
        byte[] afterQ1 = ReadSnapshots("sql-put-accdb")["pg1"];
        using (var db = new WritableSqlFixture(afterQ1))
        {
            QueryDefinitionCompilation longExpr = QueryDefinitionCompiler.Compile("SELECT ID FROM T WHERE W = 'aaaaaaaaaaaaaaaaaaaaaaaaaaa'");
            Assert.True(longExpr.Succeeded);
            SchemaOutcome res = db.OpenSchemaWriter(out _)!.UpdateQueryDefSql(db.Workspace, new UpdateQueryDefSqlRequest("Q1", longExpr.Family, longExpr.Rows!), 0);
            Assert.NotNull(res.Refused);
            Assert.Contains("LVAL Expression", res.Refused.Value.NotWritten, StringComparison.Ordinal);
            AssertImageEqual("sql-put-long-expr-refused", afterQ1, db.Image.Snapshot());

            // SQL put to UNION refuses without writing.
            QueryDefinitionCompilation unionSql = QueryDefinitionCompiler.Compile("SELECT ID FROM T UNION SELECT V FROM T");
            Assert.True(unionSql.Succeeded);
            SchemaOutcome unionRes = db.OpenSchemaWriter(out _)!.UpdateQueryDefSql(db.Workspace, new UpdateQueryDefSqlRequest("Q1", unionSql.Family, unionSql.Rows!), 0);
            Assert.NotNull(unionRes.Refused);
            Assert.Contains("UNION", unionRes.Refused.Value.NotWritten, StringComparison.Ordinal);
            AssertImageEqual("sql-put-union-refused", afterQ1, db.Image.Snapshot());

            // SQL put inside BeginTrans refuses without writing.
            Assert.Null(db.Workspace.BeginTrans());
            QueryDefinitionCompilation plain = QueryDefinitionCompiler.Compile("SELECT ID FROM T");
            SchemaOutcome txnPut = db.OpenSchemaWriter(out _)!.UpdateQueryDefSql(db.Workspace, new UpdateQueryDefSqlRequest("Q1", plain.Family, plain.Rows!), 0);
            Assert.NotNull(txnPut.Refused);
            Assert.Contains("BeginTrans", txnPut.Refused.Value.NotWritten, StringComparison.Ordinal);

            // Delete inside BeginTrans refuses without writing.
            SchemaOutcome txnDel = db.OpenSchemaWriter(out _)!.DeleteQueryDef(db.Workspace, "Q1", out _);
            Assert.NotNull(txnDel.Refused);
            Assert.Contains("BeginTrans", txnDel.Refused.Value.NotWritten, StringComparison.Ordinal);
            db.Workspace.Rollback();
            AssertImageEqual("txn-refusals-no-bytes", afterQ1, db.Image.Snapshot());
        }

        // 2. Delete of a query sharing its LvProp LVAL page refuses without writing (r-q39 Open).
        byte[] twoQueriesClosed = ReadSnapshots("two-queries-accdb")["pg3"];
        using (var db = new WritableSqlFixture(twoQueriesClosed))
        {
            SchemaOutcome sharedDel = db.OpenSchemaWriter(out _)!.DeleteQueryDef(db.Workspace, "Q1", out _);
            Assert.False(sharedDel.Succeeded);
            Assert.Contains("shares MSysObjects.LvProp", sharedDel.Accepted.Detail ?? sharedDel.Refused?.NotWritten, StringComparison.Ordinal);
            AssertImageEqual("delete-shared-lvprop-refused", twoQueriesClosed, db.Image.Snapshot());
        }

        // 3. Delete and SQL put on a query owning an external Expression LVAL page refuse without writing (r-q39 Open).
        byte[] longExprClosed = ReadSnapshots("expr-len-33-accdb")["pg2"];
        using (var db = new WritableSqlFixture(longExprClosed))
        {
            SchemaOutcome exprDel = db.OpenSchemaWriter(out _)!.DeleteQueryDef(db.Workspace, "Q1", out _);
            Assert.False(exprDel.Succeeded);
            Assert.Contains("external LVAL Expression", exprDel.Accepted.Detail ?? exprDel.Refused?.NotWritten, StringComparison.Ordinal);
            AssertImageEqual("delete-expr-lval-refused", longExprClosed, db.Image.Snapshot());

            QueryDefinitionCompilation shortSql = QueryDefinitionCompiler.Compile("SELECT ID FROM T");
            SchemaOutcome exprPut = db.OpenSchemaWriter(out _)!.UpdateQueryDefSql(db.Workspace, new UpdateQueryDefSqlRequest("Q1", shortSql.Family, shortSql.Rows!), 0);
            Assert.False(exprPut.Succeeded);
            Assert.Contains("external LVAL Expression", exprPut.Accepted.Detail ?? exprPut.Refused?.NotWritten, StringComparison.Ordinal);
            AssertImageEqual("sql-put-replace-expr-lval-refused", longExprClosed, db.Image.Snapshot());
        }

        // 4. SQL put from a UNION query refuses without writing.
        byte[] unionClosed = ReadSnapshots("union-accdb")["pg2"];
        using (var db = new WritableSqlFixture(unionClosed))
        {
            QueryDefinitionCompilation shortSql = QueryDefinitionCompiler.Compile("SELECT ID FROM T");
            SchemaOutcome fromUnion = db.OpenSchemaWriter(out _)!.UpdateQueryDefSql(db.Workspace, new UpdateQueryDefSqlRequest("QUnion", shortSql.Family, shortSql.Rows!), 0);
            Assert.NotNull(fromUnion.Refused);
            Assert.Contains("UNION", fromUnion.Refused.Value.NotWritten, StringComparison.Ordinal);
            AssertImageEqual("sql-put-from-union-refused", unionClosed, db.Image.Snapshot());
        }
    }

    [Theory]
    [InlineData("SELECT DISTINCT ID FROM T", "NotModelled.QueryDefSelectPredicate")]
    [InlineData("SELECT DISTINCTROW ID FROM T", "NotModelled.QueryDefSelectPredicate")]
    [InlineData("SELECT TOP 5 ID FROM T", "NotModelled.QueryDefTop")]
    [InlineData("SELECT ID FROM T GROUP BY ID", "NotModelled.QueryDefGroupBy")]
    [InlineData("SELECT ID FROM T GROUP BY ID HAVING Count(ID) > 1", "NotModelled.QueryDefGroupBy")]
    [InlineData("SELECT T.ID FROM T INNER JOIN U ON T.ID = U.ID", "NotModelled.QueryDefJoin")]
    [InlineData("SELECT ID FROM T, U", "NotModelled.QueryDefMultipleSources")]
    [InlineData("SELECT ID FROM (SELECT ID FROM T)", "NotModelled.QueryDefSubquery")]
    [InlineData("SELECT ID FROM T WITH OWNERACCESS OPTION", "NotModelled.QueryDefOwnerAccess")]
    [InlineData("PARAMETERS pD DateTime; SELECT ID FROM T WHERE D = pD", "NotModelled.QueryDefParameterType")]
    [InlineData("PARAMETERS pV Long; UPDATE T SET V = pV WHERE ID = 1", "NotModelled.QueryDefActionParameters")]
    [InlineData("TRANSFORM Count(ID) SELECT V FROM T GROUP BY V PIVOT W IN ('a','b')", "NotModelled.QueryDefPivotIn")]
    [InlineData("DROP TABLE T", "NotModelled.QueryDefDdlStatement")]
    [InlineData("SELECT ID FROM T ORDER BY ID DESC", "NotModelled.QueryDefOrderByShape")]
    [InlineData("SELECT ID, V FROM T ORDER BY ID, V", "NotModelled.QueryDefOrderByShape")]
    [InlineData("SELECT [A] AS [My Col] FROM T", "NotModelled.QueryDefDelimitedIdentifier")]
    [InlineData("SELECT ID FROM [My Table]", "NotModelled.QueryDefDelimitedIdentifier")]
    [InlineData("SELECT * FROM T WHERE ID > 1", "NotModelled.QueryDefStarWithClauses")]
    [InlineData("PARAMETERS pName Text(10), pId Long; SELECT ID FROM T WHERE ID = pId", "NotModelled.QueryDefParameterOrder")]
    [InlineData("TRANSFORM Count(ID) SELECT V FROM T GROUP BY V PIVOT W", "NotModelled.QueryDefCrosstabUnaliasedTransform")]
    [InlineData("TRANSFORM Count(ID) AS C SELECT V FROM T GROUP BY V, W PIVOT W", "NotModelled.QueryDefCrosstabClause")]
    public void Undecided_shapes_refuse_by_name(string sql, string expectedNotModelled)
    {
        QueryDefinitionCompilation compiled = QueryDefinitionCompiler.Compile(sql);
        Assert.False(compiled.Succeeded);
        Assert.NotNull(compiled.Failure);
        Assert.True(compiled.Failure!.IsNotModelled);
        Assert.Equal(expectedNotModelled, compiled.Failure.NotModelled);
    }

    private static string? ReadRecordedSql(string question)
    {
        string folder = Path.Combine(SqlFixture.Root(), "evidence", "r-q39-querydef-bytes", question);
        string record = Directory.GetFiles(folder, "*.a1.json").Single();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(record));
        foreach (JsonElement ev in doc.RootElement.GetProperty("capture").GetProperty("events").EnumerateArray())
        {
            if (ev.TryGetProperty("event", out JsonElement kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "invoke"
                && ev.TryGetProperty("target", out JsonElement target) && target.ValueKind == JsonValueKind.String && target.GetString() == "qdr"
                && ev.TryGetProperty("member", out JsonElement member) && member.ValueKind == JsonValueKind.String && member.GetString() == "SQL"
                && ev.TryGetProperty("result", out JsonElement result)
                && result.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }
        return null;
    }

    private static Dictionary<string, byte[]> ReadSnapshots(string question)
    {
        string folder = Path.Combine(SqlFixture.Root(), "evidence", "r-q39-querydef-bytes", question);
        string record = Directory.GetFiles(folder, "*.a1.json").Single();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(record));
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (JsonElement ev in doc.RootElement.GetProperty("capture").GetProperty("events").EnumerateArray())
        {
            if (ev.TryGetProperty("op", out JsonElement op) && op.ValueKind == JsonValueKind.String && op.GetString() == "read"
                && ev.TryGetProperty("handle", out JsonElement handle) && handle.ValueKind == JsonValueKind.String
                && ev.TryGetProperty("bytes", out JsonElement bytes) && bytes.ValueKind == JsonValueKind.String)
            {
                result[handle.GetString()!] = Convert.FromHexString(bytes.GetString()!);
            }
        }
        return result;
    }

    private static (ulong Created, ulong VacatedUpdated, ulong LiveUpdated) ExtractQueryDates(byte[] aceImage, string name)
    {
        using var file = new MemoryDatabaseFile(aceImage, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, file.PageCount, out _)!;
        CatalogEntry entry = catalog.Entries.Single(e => e.Name == name && e.ParentId == catalog.TablesContainer);
        ulong created = entry.DateCreateBits!.Value;
        ulong liveUpdated = entry.DateUpdateBits!.Value;

        // Read the vacated copy's DateUpdate 56 bytes before the shrunk live row on the same page.
        using PagePin pin = cache.Pin(entry.Root.Page).Pin!;
        SlotView slot = DataPages.CheckedSlot(pin.Bytes, entry.Root.Row, D, out _)!.Value;
        int dateUpdateOffset = D.Row.FixedBase + catalog.ObjectTable.Columns.Single(c => c.Name == "DateUpdate").FixedOffset;
        int vacStart = slot.Start - 56;
        ulong vacUpdated = vacStart >= 0 && vacStart + dateUpdateOffset + 8 <= pin.Bytes.Length
            ? BitConverter.ToUInt64(pin.Bytes.Slice(vacStart + dateUpdateOffset, 8))
            : liveUpdated;
        return (created, vacUpdated, liveUpdated);
    }

    private static void AssertImageEqual(string label, byte[] ace, byte[] ours)
    {
        Assert.True(ace.Length == ours.Length, $"{label}: file length ours {ours.Length} ({ours.Length / D.PageSize} pages), ACE {ace.Length} ({ace.Length / D.PageSize} pages)");
        List<string> diffs = [];
        int pages = ace.Length / D.PageSize;
        for (int p = 0; p < pages; p++)
        {
            ReadOnlySpan<byte> a = ace.AsSpan(p * D.PageSize, D.PageSize);
            ReadOnlySpan<byte> o = ours.AsSpan(p * D.PageSize, D.PageSize);
            if (a.SequenceEqual(o))
            {
                continue;
            }
            int pageDiffs = 0;
            for (int i = 0; i < D.PageSize; i++)
            {
                if (o[i] != a[i])
                {
                    if (pageDiffs < 12)
                    {
                        diffs.Add($"page {p} +0x{i:X3} ({i}): ours {o[i]:X2}, ACE {a[i]:X2}");
                    }
                    pageDiffs++;
                }
            }
            if (pageDiffs > 12)
            {
                diffs.Add($"page {p}: ... ({pageDiffs} total byte diffs on page)");
            }
        }
        Assert.True(diffs.Count == 0, $"{label}: {diffs.Count} diff line(s):\n" + string.Join("\n", diffs.Take(60)));
    }
}
