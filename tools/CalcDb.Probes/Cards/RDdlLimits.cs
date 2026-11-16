namespace CalcDb.Probes.Cards;

/// <summary>
/// R-CAT-1 (CAT-D) and R-SQL-2 (SQL-ACT-DDL) discriminators for DAO DDL refusals seen while building
/// fixtures: each statement differs from its neighbour in one element only (a column named Note, the same
/// name bracketed, a column-level REFERENCES clause, a table-level FOREIGN KEY, DECIMAL, WITH COMPRESSION,
/// a column-level UNIQUE). Every statement runs on its own fresh database, so one refusal cannot hide another.
/// </summary>
public sealed class RDdlLimits : ICard
{
    private const string Card = "r-ddl-limits";

    private static readonly (string Name, string Sql)[] Statements =
    [
        ("plain", "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG, Txt TEXT(20))"),
        ("note-column", "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG, Note TEXT(20))"),
        ("note-bracketed", "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG, [Note] TEXT(20))"),
        ("fk-column-level", "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG CONSTRAINT FK_C REFERENCES P (ID), Txt TEXT(20))"),
        ("fk-table-level", "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG, Txt TEXT(20), CONSTRAINT FK_C FOREIGN KEY (PID) REFERENCES P (ID))"),
        ("fk-alter", "ALTER TABLE P2 ADD CONSTRAINT FK_P2 FOREIGN KEY (PID) REFERENCES P (ID)"),
        ("decimal", "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, V DECIMAL(18,4))"),
        ("decimal-plain", "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, V DECIMAL)"),
        ("compression", "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, T TEXT(255) WITH COMPRESSION)"),
        ("unique-column-level", "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, Code TEXT(10) CONSTRAINT UX_C UNIQUE)"),
    ];

    public IEnumerable<Probe> Probes()
    {
        foreach ((string ext, int version) in new[] { ("accdb", Dao.Version120), ("mdb", Dao.Version40) })
        {
            foreach ((string name, string sql) in Statements)
            {
                yield return new Probe(Card, $"{ext}-{name}", ["R-CAT-1", "CAT-D", "R-SQL-2", "SQL-ACT-DDL"],
                    $"On a new {ext.ToUpperInvariant()} database holding P(ID LONG primary key) and P2(ID LONG primary key, PID LONG), Database.Execute of: {sql} — then the TableDefs and their fields are listed and DBEngine.Errors read.",
                    p => p.Modules("before-create").Create().Modules("after-create")
                        .CallBind("db", "eng", "CreateDatabase", A.Out($"scratch.{ext}"), A.Str(Dao.LangGeneral), A.I4(version))
                        .Call("db", "Execute", A.Str("CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY)"))
                        .Call("db", "Execute", A.Str("CREATE TABLE P2 (ID LONG CONSTRAINT PK_P2 PRIMARY KEY, PID LONG)"))
                        .Call("db", "Execute", A.Str(sql)).Errors()
                        .GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh")
                        .Each("tdfs", "t", 16, t => t.Get("t", "Name").GetBind("tf", "t", "Fields").Each("tf", "f", 8, f => f.Get("f", "Name").Get("f", "Type")).Release("tf"))
                        .Release("tdfs").GetBind("rels", "db", "Relations").Get("rels", "Count").Release("rels")
                        .Call("db", "Close").Release("db").Modules("after-close"));
            }
        }
    }
}
