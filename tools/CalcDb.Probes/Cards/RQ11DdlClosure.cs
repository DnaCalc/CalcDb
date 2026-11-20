namespace CalcDb.Probes.Cards;

/// <summary>
/// Designed probes for the native closure of a catalog write, before W-07/native-metadata-write (42cd) writes one
/// (R-CAT-1 CAT-X "start with page2/MSysObjects projection"; R-PRESERVE-1 same-length/reconstruction outcomes; R-TXN-1
/// TXN-DDL). Each question copies an empty ACE-created database (r-cat-1-accdb-empty, or the MDB one), makes one catalog
/// change, varying one element at a time against create-one-long (the route, a second column, a key, a later index, a
/// second table, the name's sort position, a drop, the format), closes and keeps the pages ACE wrote (page 0, the
/// global usage map, the MSysObjects and MSysACEs TDEF, index and data pages, and the appended TDEF and map pages)
/// inline in the answer, for a byte-for-byte comparison. no-ddl is the control for opener bookkeeping. Probes ask;
/// they hold no expected answers.
/// </summary>
public sealed class RQ11DdlClosure : ICard
{
    private const string Card = "r-q11-ddl-closure";

    private const string Accdb = "r-cat-1-accdb-empty";

    private const string Mdb = "r-cat-1-mdb-empty";

    private const int DbLong = 4;

    public IEnumerable<Probe> Probes()
    {
        yield return Make("no-ddl", "No catalog change (the control).", p => { });
        yield return Make("create-one-long", "db.Execute CREATE TABLE T (n LONG).", p => Exec(p, "CREATE TABLE T (n LONG)"));
        yield return Make("create-two-columns", "db.Execute CREATE TABLE T (n LONG, t TEXT(50)).", p => Exec(p, "CREATE TABLE T (n LONG, t TEXT(50))"));
        yield return Make("create-primary-key", "db.Execute CREATE TABLE T (n LONG CONSTRAINT PK_T PRIMARY KEY).",
            p => Exec(p, "CREATE TABLE T (n LONG CONSTRAINT PK_T PRIMARY KEY)"));
        yield return Make("create-then-index", "db.Execute CREATE TABLE T (n LONG), then db.Execute CREATE INDEX IX_T ON T (n).",
            p =>
            {
                Exec(p, "CREATE TABLE T (n LONG)");
                Exec(p, "CREATE INDEX IX_T ON T (n)");
            });
        yield return Make("append-dao", "CreateTableDef(\"T\"), CreateField(\"n\", dbLong), Fields.Append, TableDefs.Append.",
            p =>
            {
                p.CallBind("td", "db", "CreateTableDef", A.Str("T")).CallBind("tf", "td", "CreateField", A.Str("n"), A.I4(DbLong));
                p.GetBind("tfs", "td", "Fields").Call("tfs", "Append", A.Obj("tf")).Release("tfs").Release("tf");
                p.GetBind("tds", "db", "TableDefs").Call("tds", "Append", A.Obj("td")).Release("tds").Release("td");
            });
        yield return Make("create-two-tables", "db.Execute CREATE TABLE T (n LONG), then db.Execute CREATE TABLE U (n LONG).",
            p =>
            {
                Exec(p, "CREATE TABLE T (n LONG)");
                Exec(p, "CREATE TABLE U (n LONG)");
            });
        yield return Make("create-name-first", "db.Execute CREATE TABLE A (n LONG): a name that sorts before every system table's.",
            p => Exec(p, "CREATE TABLE A (n LONG)"));
        yield return Make("create-then-drop", "db.Execute CREATE TABLE T (n LONG), then db.Execute DROP TABLE T.",
            p =>
            {
                Exec(p, "CREATE TABLE T (n LONG)");
                Exec(p, "DROP TABLE T");
            });
        yield return Make("mdb-create-one-long", "db.Execute CREATE TABLE T (n LONG), on the Jet 4 MDB.",
            p => Exec(p, "CREATE TABLE T (n LONG)"), Mdb);
    }

    private static Probe Make(string name, string what, Action<P> body, string fixture = Accdb) =>
        new(Card, name, ["R-CAT-1", "CAT-X", "R-PRESERVE-1", "W-07/native-metadata-write"],
            $"A copy of the {fixture} fixture (an empty database ACE created) opened read/write through DAO. " + what +
            " Then Close; the closed file is kept. Asks which pages and bytes ACE writes for the catalog change.",
            p =>
            {
                p.Engine().OpenDb(readOnly: false);
                body(p);
                p.Call("db", "Close").Release("db").Release("eng").Files("after-close");
                Pages(p, fixture == Mdb ? 19 : 42);
            })
        { Fixtures = [("main", fixture)] };

    /// <summary>
    /// Keeps the closed file's pages a first run saw change (0-3, 7-8, 10, 17-18, 41) and the pages appended after the
    /// fixture's last page (up to six), inline in the answer. A read past the end returns the bytes there are.
    /// </summary>
    private static void Pages(P p, int firstNew)
    {
        const int page = 4096;
        p.NativeOpen("pg", "input:main", Win.GenericRead, Win.ShareRead);
        foreach ((int first, int count) in new[] { (0, 4), (7, 2), (10, 1), (17, 2), (41, 1), (firstNew, 6) })
        {
            p.NativeRead("pg", first * (long)page, count * page, keep: true);
        }
        p.NativeClose("pg");
    }

    private static P Exec(P p, string sql) => p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
}
