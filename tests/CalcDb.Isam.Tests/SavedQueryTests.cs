using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests;

/// <summary>
/// Saved queries read from MSysQueries (II-06 §8; R-CAT-1 CAT-Q) on the r-cat-1 query fixture. Every expected value is
/// ACE's answer in evidence/r-cat-1/querydefs (qd.Type and qd.SQL of the reopened file, exact BSTR).
/// </summary>
public sealed class SavedQueryTests
{
    private const string QueriesFixture = "d53c7ee6e0f16c69107e0be625e15f985312a22f5c03c74b05cb7f6744550b01";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private static QueryCatalog Read(string sha)
    {
        byte[] file = NativeFixtures.Read(sha);
        var source = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(source, 1, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, source.PageCount, out CatalogFailure? catalogFailure)!;
        Assert.True(catalog is not null, catalogFailure?.ToString());
        QueryCatalog? queries = QueryCatalog.Read(cache, D, catalog, source.PageCount, out string? failure);
        Assert.True(queries is not null, failure);
        return queries;
    }

    public static TheoryData<string, int, string> Querydefs => new()
    {
        { "QAlias", 0, "SELECT T.ID AS Key1, T.A AS Label, B * 2 AS Twice\r\nFROM T;\r\n" },
        { "QAppend", 64, "INSERT INTO W ( ID, A )\r\nSELECT ID + 100, A\r\nFROM T;\r\n" },
        { "QBase", 0, "SELECT ID, B\r\nFROM T;\r\n" },
        { "QCrosstab", 16, "TRANSFORM Sum(B) AS SB\r\nSELECT A\r\nFROM T\r\nGROUP BY A\r\nPIVOT ID;\r\n" },
        { "QDdl", 96, "CREATE TABLE Z (ID LONG);" },
        { "QDelete", 32, "DELETE *\r\nFROM W\r\nWHERE ID > 100;\r\n" },
        { "QDistinct", 0, "SELECT DISTINCT A\r\nFROM T;\r\n" },
        { "QDistinctRow", 0, "SELECT DISTINCTROW T.A\r\nFROM T INNER JOIN U ON T.ID = U.TID;\r\n" },
        { "QGroup", 0, "SELECT A, Count(*) AS N, Sum(B) AS SB\r\nFROM T\r\nGROUP BY A\r\nHAVING Count(*) > 0;\r\n" },
        { "QImplicit", 0, "SELECT ID\r\nFROM T\r\nWHERE B > pMin;\r\n" },
        { "QLike89", 0, "SELECT ID\r\nFROM T\r\nWHERE A LIKE 'a*';\r\n" },
        { "QLike92", 0, "SELECT ID\r\nFROM T\r\nWHERE A LIKE 'a%';\r\n" },
        { "QMakeTable", 80, "SELECT ID, A INTO TNew\r\nFROM T;\r\n" },
        { "QNested", 0, "SELECT B\r\nFROM QBase;\r\n" },
        { "QOne", 0, "SELECT A\r\nFROM T;\r\n" },
        { "QOneOrdered", 0, "SELECT A\r\nFROM T\r\nORDER BY A;\r\n" },
        { "QOrder", 0, "SELECT ID, A\r\nFROM T\r\nORDER BY A DESC , ID;\r\n" },
        { "QOuter", 0, "SELECT T.ID, U.V\r\nFROM T LEFT JOIN U ON T.ID = U.TID;\r\n" },
        { "QParam", 0, "PARAMETERS pId Long, pName Text ( 20 );\r\nSELECT ID, A\r\nFROM T\r\nWHERE ID = pId OR A = pName;\r\n" },
        { "QParamTypes", 0, "PARAMETERS pId Short, pName Text ( 10 );\r\nSELECT ID, A\r\nFROM T\r\nWHERE ID = pId OR A = pName;\r\n" },
        { "QPass", 112, "SELECT 1 AS One" },
        { "QProp", 0, "SELECT A\r\nFROM T;\r\n" },
        { "QText", 0, "SELECT ID, A\r\nFROM T\r\nWHERE A = 'it''s' OR A = \"q\";\r\n" },
        { "QTop", 0, "SELECT TOP 2 ID\r\nFROM T\r\nORDER BY ID;\r\n" },
        { "QTopPercent", 0, "SELECT TOP 50 PERCENT ID\r\nFROM T\r\nORDER BY ID;\r\n" },
        { "QUnicode", 0, "SELECT ID AS Bä€\r\nFROM T\r\nWHERE A <> 'Bä€ \U0001D11E';\r\n" },
        { "QUnion3", 128, "SELECT ID, A FROM T UNION SELECT ID, A FROM W UNION ALL SELECT ID, V FROM U;\r\n" },
        { "QUpdate", 48, "UPDATE T SET B = B + 1\r\nWHERE ID = 1;\r\n" },
    };

    [Theory]
    [MemberData(nameof(Querydefs))]
    public void A_saved_query_renders_the_SQL_and_type_ACE_returns(string name, int type, string sql)
    {
        SavedQuery q = Assert.Single(Read(QueriesFixture).Queries, q => q.Name == name);
        Assert.Equal(type, (int)q.Family!);
        RenderedSql rendered = QuerySql.Render(q);
        Assert.Null(rendered.NotModelled);
        Assert.Equal(sql, rendered.Text);
    }

    [Fact]
    public void The_fixture_has_the_28_saved_queries_and_no_orphan_rows()
    {
        QueryCatalog queries = Read(QueriesFixture);
        Assert.Equal(28, queries.Queries.Count); // qdfs.Count() = 28
        Assert.Empty(queries.Orphans);
        // Declared parameters in declaration order with their types (qp.Name, qp.Type: pId 4, pName 10; pId 3, pName 10).
        Assert.Equal([new QueryParameter("pId", 4, null), new QueryParameter("pName", 10, 20)], queries.Queries.Single(q => q.Name == "QParam").DeclaredParameters);
        Assert.Equal([new QueryParameter("pId", 3, null), new QueryParameter("pName", 10, 10)], queries.Queries.Single(q => q.Name == "QParamTypes").DeclaredParameters);
    }
}
