namespace CalcDb.Probes.Cards;

/// <summary>
/// Second follow-up card of the qualification round (The Plan 8.1) for R-FMT-1, R-FMT-2, R-PRESERVE-1 and R-SORT-3. It
/// asks, one variant at a time, exactly what the final status table still lists as F (or as an F part):
/// FMT1-CALCULATED and R-FMT-2 Calculated wrappers (public side: Text and Date result types with admitted expressions,
/// recomputation, writing and dropping a calculated column, numeric magnitudes), R-FMT-2 Generated values (random and GUID
/// AutoNumber by the routes not yet tried: ADO DDL, ADOX, a DAO property set before append), and R-SORT-3 Shortened-key
/// collisions (SQL equality, joins, Seek and unique indexes on keys that expand to more than the key limit; the length
/// at which the trailing field of a compound key and the last unit of a single key stop deciding order). No row of the
/// final table that is T has a reason these operations address (all need bytes, Access, patched files, settings or a
/// write recorder), so the card has no probe of that kind and uses none of the new worker operations except plain
/// DAO and ADO/ADOX calls. Probes ask, they never assert. Conventions as in r-q-fmt-sort: a statement whose refusal
/// matters runs with dbFailOnError behind a marker (a DROP TABLE of a missing table, which always fails) so the
/// DBEngine.Errors read after it is its own; collections are refreshed after DDL; Decimal columns come from ADO DDL
/// (none are needed here). Every file is named explicitly and carries the prefix <c>scratch-q2-</c>, never the card id;
/// the card needs no fixture. No OpenDatabase, CreateDatabase, CompactDatabase or Connect with an empty name, no ODBC, no
/// Requery with an argument, no SELECT Array().
/// </summary>
public sealed class RQ2FmtSort : ICard
{
    private const string Card = "r-q2-fmt-sort";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const int Fail = Dao.FailOnError;
    private const string MarkerTable = "RqNoSuchMarker";

    private static string[] F1(params string[] b) => ["R-FMT-1", .. b];

    private static string[] F2(params string[] b) => ["R-FMT-2", .. b];

    private static string[] S3(params string[] b) => ["R-SORT-3", "SORT-CHECK", .. b];

    private static string Scratch(string name, string ext) => $"scratch-q2-{name}.{ext}";

    public IEnumerable<Probe> Probes()
    {
        // FMT1-CALCULATED and R-FMT-2 Calculated wrappers.
        yield return CalcTextDate();
        yield return CalcRecompute();
        yield return CalcMagnitudes();

        // R-FMT-2 Generated values: random and GUID AutoNumber.
        yield return AdoDdlGuid();
        yield return AdoxAutoGenerate();
        yield return DaoNewValues();

        // R-SORT-3 Shortened-key collisions.
        foreach ((string tag, string unit, string ext, int version) in new[]
        {
            ("sharp-s", X(0xDF), "accdb", Dao.Version120),
            ("ae-ligature", X(0xE6), "accdb", Dao.Version120),
            ("ffi-ligature", X(0xFB03), "accdb", Dao.Version120),
            ("decomposed-e", "e" + X(0x301), "accdb", Dao.Version120),
            ("sharp-s-mdb", X(0xDF), "mdb", Dao.Version40),
        })
        {
            yield return CollideExpansion(tag, unit, ext, version);
        }
        yield return CollideBracket();

        // R-SORT-3 Length510 boundary: where the trailing field stops deciding order.
        foreach ((string tag, string unit) in new[] { ("e-acute", X(0xE9)), ("sharp-s", X(0xDF)), ("han", X(0x6F22)) })
        {
            yield return NTailBracket(tag, unit);
        }
    }

    // ================================================================================================
    // Calculated columns

    private sealed record CalcSpec(string Name, int Type, int Size, string Expr);

    /// <summary>
    /// A table CT&lt;name&gt;(ID Long, A Long, B Text(20), D Date, C calculated) through CreateTableDef, Expression put before the
    /// field is appended, every step's DBEngine.Errors read after a marker; rows are then written through a recordset.
    /// </summary>
    private static void CalcTable(P p, CalcSpec s, string tag)
    {
        string t = "CT" + s.Name;
        p.CallBind(tag + "t", "db", "CreateTableDef", A.Str(t))
            .CallBind(tag + "i", tag + "t", "CreateField", A.Str("ID"), A.I4(4)).GetBind(tag + "l", tag + "t", "Fields").Call(tag + "l", "Append", A.Obj(tag + "i"))
            .CallBind(tag + "a", tag + "t", "CreateField", A.Str("A"), A.I4(4)).Call(tag + "l", "Append", A.Obj(tag + "a"))
            .CallBind(tag + "b", tag + "t", "CreateField", A.Str("B"), A.I4(10), A.I4(20)).Call(tag + "l", "Append", A.Obj(tag + "b"))
            .CallBind(tag + "d", tag + "t", "CreateField", A.Str("D"), A.I4(8)).Call(tag + "l", "Append", A.Obj(tag + "d"));
        if (s.Size > 0)
        {
            p.CallBind(tag + "c", tag + "t", "CreateField", A.Str("C"), A.I4(s.Type), A.I4(s.Size));
        }
        else
        {
            p.CallBind(tag + "c", tag + "t", "CreateField", A.Str("C"), A.I4(s.Type));
        }
        Mark(p);
        p.Put(tag + "c", "Expression", A.Str(s.Expr)).Errors();
        Mark(p);
        p.Call(tag + "l", "Append", A.Obj(tag + "c")).Errors();
        Mark(p);
        p.GetBind("cts", "db", "TableDefs").Call("cts", "Append", A.Obj(tag + "t")).Errors().Release("cts");
        p.Release(tag + "c").Release(tag + "d").Release(tag + "b").Release(tag + "a").Release(tag + "i").Release(tag + "l").Release(tag + "t");
    }

    private static Probe CalcTextDate()
    {
        CalcSpec[] specs =
        [
            new("T1", 10, 255, "[B] & \"-calc\""),
            new("T2", 10, 255, "[B] & \"-\" & [A]"),
            new("T3", 10, 255, "UCase([B])"),
            new("T4", 10, 255, "Left([B], 1)"),
            new("T5", 10, 255, "Format([A], \"000\")"),
            new("T6", 10, 255, "CStr([A])"),
            new("M1", 12, 0, "[B] & \"x\""),
            new("D1", 8, 0, "[D] + 1"),
            new("D2", 8, 0, "#1/1/2000# + [A]"),
            new("D3", 8, 0, "DateAdd(\"d\", [A], [D])"),
            new("D4", 8, 0, "DateSerial(2000, 1, [A])"),
            new("D5", 8, 0, "Date()"),
        ];
        return new Probe(Card, "calc-text-date-accdb", F1("II-04 structural discrimination", "calculated Boolean/Numeric/Text/Memo"),
            "Branch R-FMT-1 FMT1-CALCULATED and R-FMT-2 Calculated wrappers (public side), the part the first card could not ask because its Text and Date expressions (CStr, DateAdd with a # literal) were refused with 3859. On an ACCDB, one table CT<name>(ID Long, A Long, B Text(20), D Date, C calculated) per expression, made through CreateTableDef with Expression put before the field is appended, each step's Errors read after a marker. Text results: [B] & \"-calc\" (the form the earlier fixture admitted), [B] & \"-\" & [A], UCase([B]), Left([B], 1), Format([A], \"000\"), and CStr([A]) as the control that was refused; a Memo result [B] & \"x\"; Date results: [D] + 1, #1/1/2000# + [A], DateAdd(\"d\", [A], [D]) with double-quoted interval, DateSerial(2000, 1, [A]), Date(). Rows (5, 'xy', 2000-01-01) and a row of Nulls are written through a recordset and read back, and C's Type, Size, Attributes and properties (Expression, ResultType) are read. Asks which Text, Memo and Date results ACE admits as calculated columns, which expressions it refuses, and what each reads back.",
            p =>
            {
                Begin(p, Scratch("calc-text-date", "accdb"), Dao.Version120);
                foreach (CalcSpec s in specs)
                {
                    CalcTable(p, s, "c" + s.Name);
                    Insert(p, "db", "CT" + s.Name, ["ID", "A", "B", "D"],
                    [
                        [A.I4(1), A.I4(5), A.Str("xy"), A.Date(36526.0)],
                        [A.I4(2), A.Null, A.Null, A.Null],
                    ]);
                    ReadSql(p, $"SELECT * FROM CT{s.Name} ORDER BY ID", 4, 6);
                    FieldProps(p, "db", "CT" + s.Name, "C", "cp" + s.Name);
                }
                p.FinishLite("db");
            });
    }

    private static Probe CalcRecompute()
    {
        return new Probe(Card, "calc-recompute-accdb", F1("II-04 structural discrimination", "calculated dependency"),
            "Branch R-FMT-1 FMT1-CALCULATED and R-FMT-2 Calculated wrappers: recomputation and dependency (public side), with expressions the first card showed ACE admits. On an ACCDB, CD(ID Long, A Long, B Text(20), C Text(255) = [B] & \"-calc\", N Long = [A] * 2) made through CreateTableDef; row (1, 5, 'x') inserted and read. One change at a time, each followed by a read of every column: A = 6; B = 'y'; B = Null; A = Null; A = 7 again; an UPDATE that sets C directly; a recordset Edit that sets N then Update (refused?) with CancelUpdate; ALTER TABLE DROP COLUMN A (N depends on it). A second table CD2 of the same shape: the input A renamed through Field.Name; N's Expression put to [A2] * 3 on the existing field; ALTER TABLE DROP COLUMN C (a calculated column itself). Asks when a stored result is recomputed, whether a calculated column can be written, and what dropping or renaming an input does.",
            p =>
            {
                Begin(p, Scratch("calc-recompute", "accdb"), Dao.Version120);
                foreach (string t in new[] { "CD", "CD2" })
                {
                    string g = t.ToLowerInvariant();
                    p.CallBind(g + "t", "db", "CreateTableDef", A.Str(t))
                        .CallBind(g + "i", g + "t", "CreateField", A.Str("ID"), A.I4(4)).GetBind(g + "l", g + "t", "Fields").Call(g + "l", "Append", A.Obj(g + "i"))
                        .CallBind(g + "a", g + "t", "CreateField", A.Str("A"), A.I4(4)).Call(g + "l", "Append", A.Obj(g + "a"))
                        .CallBind(g + "b", g + "t", "CreateField", A.Str("B"), A.I4(10), A.I4(20)).Call(g + "l", "Append", A.Obj(g + "b"))
                        .CallBind(g + "c", g + "t", "CreateField", A.Str("C"), A.I4(10), A.I4(255)).Put(g + "c", "Expression", A.Str("[B] & \"-calc\""))
                        .Call(g + "l", "Append", A.Obj(g + "c"))
                        .CallBind(g + "n", g + "t", "CreateField", A.Str("N"), A.I4(4)).Put(g + "n", "Expression", A.Str("[A] * 2"));
                    Mark(p);
                    p.Call(g + "l", "Append", A.Obj(g + "n")).Errors();
                    Mark(p);
                    p.GetBind("cts", "db", "TableDefs").Call("cts", "Append", A.Obj(g + "t")).Errors().Release("cts");
                    p.Release(g + "n").Release(g + "c").Release(g + "b").Release(g + "a").Release(g + "i").Release(g + "l").Release(g + "t");
                    Act(p, $"INSERT INTO {t} (ID, A, B) VALUES (1, 5, 'x')");
                    ReadSql(p, $"SELECT * FROM {t} ORDER BY ID", 4, 6);
                }
                foreach (string sql in new[]
                {
                    "UPDATE CD SET A = 6 WHERE ID = 1",
                    "UPDATE CD SET B = 'y' WHERE ID = 1",
                    "UPDATE CD SET B = Null WHERE ID = 1",
                    "UPDATE CD SET A = Null WHERE ID = 1",
                    "UPDATE CD SET A = 7 WHERE ID = 1",
                    "UPDATE CD SET C = 'z' WHERE ID = 1",
                })
                {
                    Act(p, sql);
                    ReadSql(p, "SELECT * FROM CD ORDER BY ID", 4, 6);
                }
                p.OpenRs("er", "db", "CD", Dao.OpenDynaset).Call("er", "MoveFirst").Call("er", "Edit").GetBind("efl", "er", "Fields").Item("en", "efl", A.Str("N"));
                Mark(p);
                p.Put("en", "Value", A.I4(99)).Errors();
                Mark(p);
                p.Call("er", "Update").Errors().Call("er", "CancelUpdate").Release("en").Release("efl").Call("er", "Close").Release("er");
                ReadSql(p, "SELECT * FROM CD ORDER BY ID", 4, 6);
                Act(p, "ALTER TABLE CD DROP COLUMN A");
                ReadSql(p, "SELECT * FROM CD ORDER BY ID", 4, 6);
                FieldProps(p, "db", "CD", "N", "cdn");

                TdOpen(p, "db", "CD2", "r2t");
                p.GetBind("r2f", "r2t", "Fields").Call("r2f", "Refresh").Item("r2a", "r2f", A.Str("A"));
                Mark(p);
                p.Put("r2a", "Name", A.Str("A2")).Errors().Release("r2a");
                p.Call("r2f", "Refresh").Item("r2n", "r2f", A.Str("N"));
                Mark(p);
                p.Put("r2n", "Expression", A.Str("[A2] * 3")).Errors().Release("r2n").Release("r2f").Release("r2t");
                ReadSql(p, "SELECT * FROM CD2 ORDER BY ID", 4, 6);
                FieldProps(p, "db", "CD2", "N", "cd2n");
                Act(p, "ALTER TABLE CD2 DROP COLUMN C");
                ReadSql(p, "SELECT * FROM CD2 ORDER BY ID", 4, 6);
                p.FinishLite("db");
            });
    }

    private static Probe CalcMagnitudes()
    {
        CalcSpec[] specs =
        [
            new("Long", 4, 0, "[A] * 1000"),
            new("LongSq", 4, 0, "[A] * [A]"),
            new("Dbl", 7, 0, "[A] ^ 5"),
            new("Cur", 5, 0, "[A] * 100000"),
            new("Dec", 20, 0, "[A] * 100000000000000"),
            new("DecNeg", 20, 0, "[A] * -100000000000000"),
        ];
        return new Probe(Card, "calc-magnitudes-accdb", F2("Calculated wrappers", "Numeric magnitude"),
            "Branch R-FMT-2 Calculated wrappers, numeric magnitude (public side), on an ACCDB. One table per result type, made through CreateTableDef with Expression before append: Long [A] * 1000 and [A] * [A] (overflow of the declared type), Double [A] ^ 5, Currency [A] * 100000, Decimal [A] * 100000000000000 and [A] * -100000000000000 (up to 24 digits). Rows A = 1, 2147483647, -2147483647, 0 and Null are written through a recordset and read back (each insert with dbFailOnError and a marked Errors read). C's Type, Size and ResultType are read. Asks what the engine stores or refuses when a calculated result exceeds its declared type and how the 12- and 16-byte magnitudes read back.",
            p =>
            {
                Begin(p, Scratch("calc-magnitudes", "accdb"), Dao.Version120);
                foreach (CalcSpec s in specs)
                {
                    CalcTable(p, s, "m" + s.Name);
                    foreach (int a in new[] { 1, 2147483647, -2147483647, 0 })
                    {
                        Act(p, $"INSERT INTO CT{s.Name} (ID, A) VALUES ({Math.Abs(a) % 1000 + 1}, {a})");
                    }
                    Act(p, $"INSERT INTO CT{s.Name} (ID, A) VALUES (2000, Null)");
                    ReadSql(p, $"SELECT * FROM CT{s.Name} ORDER BY ID", 8, 6);
                    FieldProps(p, "db", "CT" + s.Name, "C", "mp" + s.Name);
                }
                p.FinishLite("db");
            });
    }

    // ================================================================================================
    // Generated values: random and GUID AutoNumber

    private static Probe AdoDdlGuid()
    {
        string file = Scratch("ado-ddl-guid", "accdb");
        string[] statements =
        [
            "CREATE TABLE G1 (ID COUNTER(1,1) CONSTRAINT PK_G1 PRIMARY KEY, S TEXT(10))",
            "CREATE TABLE G2 (ID LONG CONSTRAINT PK_G2 PRIMARY KEY, G UNIQUEIDENTIFIER DEFAULT GenGUID())",
            "CREATE TABLE G3 (ID LONG CONSTRAINT PK_G3 PRIMARY KEY, G GUID DEFAULT GenGUID())",
            "CREATE TABLE G4 (ID LONG CONSTRAINT PK_G4 PRIMARY KEY, G UNIQUEIDENTIFIER)",
            "ALTER TABLE G4 ADD COLUMN H UNIQUEIDENTIFIER DEFAULT GenGUID()",
            "ALTER TABLE G4 ALTER COLUMN G SET DEFAULT GenGUID()",
        ];
        return new Probe(Card, "ado-ddl-guid-accdb", F2("Generated values", "GUID/AutoNumber behavior"),
            "Branch R-FMT-2 Generated values, GUID AutoNumber by ADO DDL (the DAO DDL form was refused with 3292 and 3290). After CreateDatabase and close, ADO (Provider " + Ace + ") runs, each statement on its own so a refusal of one form is an observation: CREATE TABLE G1 with COUNTER(1,1) as the control; G2 with G UNIQUEIDENTIFIER DEFAULT GenGUID(); G3 with G GUID DEFAULT GenGUID(); G4 with a plain UNIQUEIDENTIFIER, then ALTER ADD COLUMN H UNIQUEIDENTIFIER DEFAULT GenGUID() and ALTER COLUMN G SET DEFAULT GenGUID(). DAO then reopens the file, inserts three rows into each table that exists without naming the GUID columns, and reads them and the fields' Type and Attributes. Asks whether any ADO DDL form makes a generated GUID and what it stores.",
            p =>
            {
                Begin(p, file, Dao.Version120);
                p.Call("db", "Close").Release("db");
                AdoDdl(p, A.Out(file), statements);
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false));
                foreach (string t in new[] { "G1", "G2", "G3", "G4" })
                {
                    for (int i = 1; i <= 3; i++)
                    {
                        Act(p, t == "G1" ? $"INSERT INTO G1 (S) VALUES ('s{i}')" : $"INSERT INTO {t} (ID) VALUES ({i})");
                    }
                    ReadSql(p, $"SELECT * FROM {t} ORDER BY ID", 8, 4);
                    FieldsOf(p, "db", t, "gf" + t);
                }
                p.FinishLite("db");
            });
    }

    private static Probe AdoxAutoGenerate()
    {
        string file = Scratch("adox-auto", "accdb");
        return new Probe(Card, "adox-autogenerate-accdb", F2("Generated values", "GUID/AutoNumber behavior"),
            "Branch R-FMT-2 Generated values, GUID and counter generation through ADOX (ADOX.Catalog and ADOX.Table over an open ADO connection, ActiveConnection and ParentCatalog set by reference). After CreateDatabase and close, ADO opens the file; ADOX table AX1 gets an Integer column ID whose provider property Autoincrement is set True (with its Properties listed), and AX2 gets an Integer ID and a GUID column G (adGUID, 72) whose provider properties are listed and whose Jet OLEDB:AutoGenerate is set True before Tables.Append; each call's HRESULT is the observation. DAO then reopens the file, inserts three rows into each table that exists (without G) and reads them with the fields' Type and Attributes. Asks which provider properties an ADOX GUID column offers, whether the append is admitted, and what the engine stores for G.",
            p =>
            {
                Begin(p, file, Dao.Version120);
                p.Call("db", "Close").Release("db");
                p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace))
                    .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out(file))
                    .Release("cds").Release("cnp").Call("cn", "Open");
                p.Create("ADOX.Catalog", "cat").PutRef("cat", "ActiveConnection", "cn").GetBind("tbs", "cat", "Tables");

                p.Create("ADOX.Table", "t1").Put("t1", "Name", A.Str("AX1")).PutRef("t1", "ParentCatalog", "cat").GetBind("c1s", "t1", "Columns")
                    .Call("c1s", "Append", A.Str("ID"), A.I4(3)).Item("c1", "c1s", A.Str("ID")).GetBind("c1p", "c1", "Properties")
                    .Each("c1p", "c1q", 48, b => b.Get("c1q", "Name").Get("c1q", "Value"))
                    .Item("c1a", "c1p", A.Str("Autoincrement")).Put("c1a", "Value", A.Bool(true)).Get("c1a", "Value")
                    .Release("c1a").Release("c1p").Release("c1").Release("c1s");
                p.Call("tbs", "Append", A.Obj("t1")).Release("t1");

                p.Create("ADOX.Table", "t2").Put("t2", "Name", A.Str("AX2")).PutRef("t2", "ParentCatalog", "cat").GetBind("c2s", "t2", "Columns")
                    .Call("c2s", "Append", A.Str("ID"), A.I4(3)).Call("c2s", "Append", A.Str("G"), A.I4(72))
                    .Item("c2g", "c2s", A.Str("G")).GetBind("c2p", "c2g", "Properties")
                    .Each("c2p", "c2q", 48, b => b.Get("c2q", "Name").Get("c2q", "Value"))
                    .Item("c2a", "c2p", A.Str("Jet OLEDB:AutoGenerate")).Put("c2a", "Value", A.Bool(true)).Get("c2a", "Value")
                    .Release("c2a").Release("c2p").Release("c2g").Release("c2s");
                p.Call("tbs", "Append", A.Obj("t2")).Release("t2");
                p.Release("tbs").Release("cat").Call("cn", "Close").Release("cn");

                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false));
                foreach (string t in new[] { "AX1", "AX2" })
                {
                    for (int i = 1; i <= 3; i++)
                    {
                        Act(p, $"INSERT INTO {t} (ID) VALUES ({i})");
                    }
                    ReadSql(p, $"SELECT * FROM {t} ORDER BY ID", 8, 4);
                    FieldsOf(p, "db", t, "xf" + t);
                }
                p.FinishLite("db");
            });
    }

    private static Probe DaoNewValues()
    {
        return new Probe(Card, "dao-newvalues-accdb", F2("Generated values", "GUID/AutoNumber behavior"),
            "Branch R-FMT-2 Generated values, random AutoNumber by DAO, repairing the first attempt (the NewValues property was appended to a field of an existing table, accepted, and the IDs stayed sequential). On an ACCDB: (1) the Properties of an ordinary AutoNumber field are listed (is NewValues a built-in property, and its value?); its NewValues value is put to \"Random\" and to 1 with a marked Errors read, then three rows inserted and the IDs read; (2) a table made through CreateTableDef whose ID field has Attributes 16 (dbAutoIncrField) and a NewValues property created as Text \"Random\" and appended to the new field's Properties before the field is appended; (3) the same with the property created as Long 1; (4) the same without any NewValues property (the control). Each table gets six inserts without an ID; the IDs are read. Asks whether DAO can produce a random AutoNumber at all and by which route.",
            p =>
            {
                Begin(p, Scratch("newvalues", "accdb"), Dao.Version120);
                Run(p, "CREATE TABLE R0 (ID COUNTER CONSTRAINT PK_R0 PRIMARY KEY, S TEXT(10))");
                TdOpen(p, "db", "R0", "r0t");
                p.GetBind("r0f", "r0t", "Fields").Item("r0i", "r0f", A.Str("ID")).GetBind("r0p", "r0i", "Properties").Call("r0p", "Refresh")
                    .Each("r0p", "r0q", 48, b => b.Get("r0q", "Name").Get("r0q", "Value"));
                Mark(p);
                p.Item("r0n", "r0p", A.Str("NewValues")).Errors();
                Mark(p);
                p.Put("r0n", "Value", A.Str("Random")).Errors();
                Mark(p);
                p.Put("r0n", "Value", A.I4(1)).Errors().Get("r0n", "Value").Release("r0n").Release("r0p").Release("r0i").Release("r0f").Release("r0t");
                for (int i = 1; i <= 3; i++)
                {
                    Act(p, $"INSERT INTO R0 (S) VALUES ('r{i}')");
                }
                ReadSql(p, "SELECT * FROM R0 ORDER BY S", 8, 3);

                foreach ((string name, int propType, string value, bool withProperty) in new[]
                {
                    ("R1", 10, "Random", true),
                    ("R2", 4, "1", true),
                    ("R3", 0, string.Empty, false),
                })
                {
                    string g = name.ToLowerInvariant();
                    p.CallBind(g + "t", "db", "CreateTableDef", A.Str(name))
                        .CallBind(g + "i", g + "t", "CreateField", A.Str("ID"), A.I4(4));
                    Mark(p);
                    p.Put(g + "i", "Attributes", A.I4(16)).Errors();
                    if (withProperty)
                    {
                        Mark(p);
                        p.GetBind(g + "pp", g + "i", "Properties");
                        p.CallBind(g + "np", g + "i", "CreateProperty", A.Str("NewValues"), A.I4(propType), propType == 4 ? A.I4(int.Parse(value)) : A.Str(value)).Errors();
                        Mark(p);
                        p.Call(g + "pp", "Append", A.Obj(g + "np")).Errors().Release(g + "np").Release(g + "pp");
                    }
                    p.GetBind(g + "l", g + "t", "Fields");
                    Mark(p);
                    p.Call(g + "l", "Append", A.Obj(g + "i")).Errors();
                    p.CallBind(g + "s", g + "t", "CreateField", A.Str("S"), A.I4(10), A.I4(10)).Call(g + "l", "Append", A.Obj(g + "s"));
                    Mark(p);
                    p.GetBind("cts", "db", "TableDefs").Call("cts", "Append", A.Obj(g + "t")).Errors().Release("cts");
                    p.Release(g + "s").Release(g + "l").Release(g + "i").Release(g + "t");
                    for (int i = 1; i <= 6; i++)
                    {
                        Act(p, $"INSERT INTO {name} (S) VALUES ('r{i}')");
                    }
                    ReadSql(p, $"SELECT * FROM {name} ORDER BY S", 8, 3);
                    FieldProps(p, "db", name, "ID", "nv" + g);
                }
                p.FinishLite("db");
            });
    }

    // ================================================================================================
    // R-SORT-3 Shortened-key collisions

    private static Probe CollideExpansion(string tag, string unit, string ext, int version)
    {
        int heads = 254 / unit.Length;
        return new Probe(Card, $"collide-expansion-{tag}", S3("Shortened-key collisions", "Length510 boundary"),
            $"Branch R-SORT-3 Shortened-key collisions (public side), one expanding character kind per probe (this one: {Describe(unit)}, {unit.Length} unit(s) per repetition, on a {ext.ToUpperInvariant()}), repairing the single earlier case where SQL equality called two different 255-unit keys equal. L(ID, K Text(255)) has non-unique IX_K built before the rows and L0 is an unindexed copy. Rows: IDs 1 and 2 = the character repeated to 254 units + 'x' and + 'y'; IDs 3 and 4 the same with 200 units (a control well inside the limit); ID 5 = 'y' + the 254-unit head (a change at the first unit). Reads on L and on L0: GROUP BY K (first ID, count) in key order; ORDER BY K, ID; the equality join A.K = B.K where A.ID < B.ID; the join A.K < B.K where A.ID + 1 = B.ID; WHERE K = (the key of ID 1). On L the table-type Seek on IX_K for the keys of IDs 1 and 2 with = (NoMatch and the current ID) and > (the current ID), and the IX_K walk. A unique index UX_K on a second table UL with the same rows is loaded with a marked Errors read per row. Asks whether the engine ever calls the two keys equal (by index, join, subquery or Seek), orders them inconsistently, or refuses the second as a duplicate, with and without an index.",
            p =>
            {
                string head254 = Rep(unit, heads), head200 = Rep(unit, 200 / unit.Length);
                Begin(p, Scratch("collide-" + tag, ext), version);
                Run(p, "CREATE TABLE L (ID LONG CONSTRAINT PK_L PRIMARY KEY, K TEXT(255))");
                Run(p, "CREATE INDEX IX_K ON L (K)");
                Run(p, "CREATE TABLE UL (ID LONG CONSTRAINT PK_UL PRIMARY KEY, K TEXT(255))");
                Run(p, "CREATE UNIQUE INDEX UX_K ON UL (K)");
                Arg[][] rows =
                [
                    [A.I4(1), Units(head254 + "x")],
                    [A.I4(2), Units(head254 + "y")],
                    [A.I4(3), Units(head200 + "x")],
                    [A.I4(4), Units(head200 + "y")],
                    [A.I4(5), Units("y" + head254)],
                ];
                Insert(p, "db", "L", ["ID", "K"], rows);
                RowsE(p, "UL", ["ID", "K"], rows);
                Run(p, "CREATE TABLE L0 (ID LONG CONSTRAINT PK_L0 PRIMARY KEY, K TEXT(255))");
                Run(p, "INSERT INTO L0 (ID, K) SELECT ID, K FROM L");
                foreach (string t in new[] { "L", "L0" })
                {
                    ReadSql(p, $"SELECT MIN(ID), COUNT(*) FROM {t} GROUP BY K ORDER BY K", 8, 2);
                    ReadSql(p, $"SELECT ID FROM {t} ORDER BY K, ID", 8, 1);
                    ReadSql(p, $"SELECT A.ID, B.ID FROM {t} AS A INNER JOIN {t} AS B ON A.K = B.K WHERE A.ID < B.ID", 8, 2);
                    ReadSql(p, $"SELECT A.ID, B.ID FROM {t} AS A INNER JOIN {t} AS B ON A.K < B.K WHERE A.ID + 1 = B.ID", 8, 2);
                    ReadSql(p, $"SELECT ID FROM {t} WHERE K = (SELECT K FROM {t} WHERE ID = 1) ORDER BY ID", 8, 1);
                }
                Walk(p, "db", "L", "IX_K", false, 8);
                foreach (int id in new[] { 1, 2 })
                {
                    string k = id == 1 ? head254 + "x" : head254 + "y";
                    foreach (string op in new[] { "=", ">" })
                    {
                        p.OpenRs("sk", "db", "L", Dao.OpenTable).Put("sk", "Index", A.Str("IX_K")).Call("sk", "Seek", A.Str(op), Units(k)).Get("sk", "NoMatch")
                            .GetBind("skf", "sk", "Fields").Item("skid", "skf", A.Str("ID"));
                        Mark(p);
                        p.Get("skid", "Value").Errors().Release("skid").Release("skf").Call("sk", "Close").Release("sk");
                    }
                }
                ReadSql(p, "SELECT ID FROM UL ORDER BY ID", 8, 1);
                p.FinishLite("db");
            });
    }

    private static Probe CollideBracket()
    {
        int[] heads = [200, 240, 245, 248, 250, 251, 252, 253, 254];
        return new Probe(Card, "collide-bracket-sharp-s-accdb", S3("Shortened-key collisions", "Length510 boundary"),
            "Branch R-SORT-3 Shortened-key collisions, locating the length (public side). On an ACCDB, the sharp s repeated h times + 'x' and + 'y' for h = 200, 240, 245, 248, 250, 251, 252, 253 and 254 (so the key is h + 1 units; the pair k = 0 to 8 has IDs 2k+1 and 2k+2), in L (non-unique IX_K built first) and in an unindexed copy L0. Reads the equality join A.K = B.K where A.ID < B.ID, the join A.K < B.K where A.ID + 1 = B.ID and GROUP BY K (first ID, count) on both, plus the IX_K walk. Asks at which h the first colliding pair appears and whether the answer is the same with and without the index.",
            p =>
            {
                string ss = X(0xDF);
                Begin(p, Scratch("collide-bracket", "accdb"), Dao.Version120);
                Run(p, "CREATE TABLE L (ID LONG CONSTRAINT PK_L PRIMARY KEY, K TEXT(255))");
                Run(p, "CREATE INDEX IX_K ON L (K)");
                var rows = new List<Arg[]>();
                for (int k = 0; k < heads.Length; k++)
                {
                    string head = Rep(ss, heads[k]);
                    rows.Add([A.I4(2 * k + 1), Units(head + "x")]);
                    rows.Add([A.I4(2 * k + 2), Units(head + "y")]);
                }
                Insert(p, "db", "L", ["ID", "K"], [.. rows]);
                Run(p, "CREATE TABLE L0 (ID LONG CONSTRAINT PK_L0 PRIMARY KEY, K TEXT(255))");
                Run(p, "INSERT INTO L0 (ID, K) SELECT ID, K FROM L");
                foreach (string t in new[] { "L", "L0" })
                {
                    ReadSql(p, $"SELECT A.ID, B.ID FROM {t} AS A INNER JOIN {t} AS B ON A.K = B.K WHERE A.ID < B.ID", 24, 2);
                    ReadSql(p, $"SELECT A.ID, B.ID FROM {t} AS A INNER JOIN {t} AS B ON A.K < B.K WHERE A.ID + 1 = B.ID", 24, 2);
                    ReadSql(p, $"SELECT MIN(ID), COUNT(*) FROM {t} GROUP BY K ORDER BY K", 24, 2);
                }
                Walk(p, "db", "L", "IX_K", false, 24);
                p.FinishLite("db");
            });
    }

    private static Probe NTailBracket(string tag, string unit)
    {
        int[] lengths = [248, 249, 250, 251, 252, 253, 254, 255];
        return new Probe(Card, $"ntail-bracket-{tag}-accdb", S3("Length510 boundary", "Compound framing"),
            $"Branch R-SORT-3 Length510 boundary, locating the length where a trailing key field stops deciding order (public side), one character kind per probe (this one: {Describe(unit)}), on an ACCDB. LA(ID, K Text(255), N Long) with non-unique IX_KN on (K, N) built before the rows; for each length 248 to 255 two rows with K = the character repeated to that length, N = 2 inserted first (ID 10k+1) and N = 1 second (ID 10k+2), k = 1 to 8. Reads the IX_KN walk (a pair listed with the N = 1 row first keeps N in the key; the N = 2 row first means the tie fell to insertion order), the backward walk, and SQL ORDER BY K, N. The first card found the loss at 253 and 255 but not 254 for the acute e, so every length from 248 to 255 is asked separately. Asks the first length at which N stops deciding order, whether it is monotonic in length, and whether the answer depends on the index (SQL order is read on the same table).",
            p =>
            {
                Begin(p, Scratch("ntail-" + tag, "accdb"), Dao.Version120);
                Run(p, "CREATE TABLE LA (ID LONG CONSTRAINT PK_LA PRIMARY KEY, K TEXT(255), N LONG)");
                Run(p, "CREATE INDEX IX_KN ON LA (K, N)");
                var rows = new List<Arg[]>();
                for (int k = 0; k < lengths.Length; k++)
                {
                    string key = Rep(unit, lengths[k]);
                    rows.Add([A.I4(10 * (k + 1) + 1), Units(key), A.I4(2)]);
                    rows.Add([A.I4(10 * (k + 1) + 2), Units(key), A.I4(1)]);
                }
                Insert(p, "db", "LA", ["ID", "K", "N"], [.. rows]);
                Walk(p, "db", "LA", "IX_KN", false, 24);
                Walk(p, "db", "LA", "IX_KN", true, 24);
                ReadSql(p, "SELECT ID FROM LA ORDER BY K, N", 24, 1);
                p.FinishLite("db");
            });
    }

    // ================================================================================================
    // Helpers

    private static string Describe(string unit) => unit.Length == 1 && unit[0] < 0x80 ? "ASCII letter a" : $"U+{(int)unit[0]:X4}" + (unit.Length > 1 ? $" + U+{(int)unit[1]:X4}" : string.Empty);

    private static string X(params int[] codePoints)
    {
        var sb = new System.Text.StringBuilder();
        foreach (int cp in codePoints)
        {
            sb.Append(cp < 0x10000 ? ((char)cp).ToString() : char.ConvertFromUtf32(cp));
        }
        return sb.ToString();
    }

    private static string Rep(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    private static Arg Units(string? units) => units is null ? A.Null : A.BstrUnits(units);

    private static void Begin(P p, string file, int version) =>
        p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(version)).Get("db", "Version").Get("db", "CollatingOrder");

    /// <summary>A statement that always fails, so the DBEngine.Errors read after the next step belongs to that step.</summary>
    private static void Mark(P p, string db = "db") => p.Call(db, "Execute", A.Str("DROP TABLE " + MarkerTable));

    /// <summary>The statement under test: marker, Execute with dbFailOnError, RecordsAffected, then DBEngine.Errors.</summary>
    private static void Act(P p, string sql, string db = "db")
    {
        Mark(p, db);
        p.Call(db, "Execute", A.Str(sql), A.I4(Fail)).Get(db, "RecordsAffected").Errors();
    }

    private static void Run(P p, string sql, string db = "db") => p.Call(db, "Execute", A.Str(sql), A.I4(Fail));

    private static void ReadSql(P p, string sql, int maxRows, int maxFields, string db = "db")
    {
        string tag = "q" + p.Ops.Count;
        p.OpenRs(tag, db, sql, Dao.OpenSnapshot).ReadAll(tag, maxRows, maxFields).Call(tag, "Close").Release(tag);
    }

    private static void TdOpen(P p, string db, string table, string bind)
    {
        string tds = bind + "_tds";
        p.GetBind(tds, db, "TableDefs").Call(tds, "Refresh").Item(bind, tds, A.Str(table)).Release(tds);
    }

    private static void FieldProps(P p, string db, string table, string field, string tag)
    {
        TdOpen(p, db, table, tag + "t");
        p.GetBind(tag + "f", tag + "t", "Fields").Item(tag + "d", tag + "f", A.Str(field)).Get(tag + "d", "Type").Get(tag + "d", "Size").Get(tag + "d", "Attributes")
            .GetBind(tag + "p", tag + "d", "Properties").Each(tag + "p", tag + "q", 48, b => b.Get(tag + "q", "Name").Get(tag + "q", "Value"))
            .Release(tag + "p").Release(tag + "d").Release(tag + "f").Release(tag + "t");
    }

    private static void FieldsOf(P p, string db, string table, string bind)
    {
        string fl = bind + "_fl", fd = bind + "_fd";
        TdOpen(p, db, table, bind);
        p.GetBind(fl, bind, "Fields").Call(fl, "Refresh").Get(fl, "Count")
            .Each(fl, fd, 64, f => f.Get(fd, "Name").Get(fd, "Type").Get(fd, "Size").Get(fd, "Attributes"))
            .Release(fl).Release(bind);
    }

    private static void Walk(P p, string db, string table, string? index, bool backward, int max)
    {
        p.OpenRs("w", db, table, Dao.OpenTable);
        if (index is not null)
        {
            p.Put("w", "Index", A.Str(index));
        }
        p.GetBind("wfl", "w", "Fields").Item("wid", "wfl", A.Str("ID"));
        if (backward)
        {
            p.Call("w", "MoveLast").Until("w", "BOF", max, b => b.Get("wid", "Value").Call("w", "MovePrevious"));
        }
        else
        {
            p.Call("w", "MoveFirst").Until("w", "EOF", max, b => b.Get("wid", "Value").Call("w", "MoveNext"));
        }
        p.Release("wid").Release("wfl").Call("w", "Close").Release("w");
    }

    private static void Insert(P p, string db, string table, string[] cols, Arg[][] rows)
    {
        p.OpenRs("rs", db, table, Dao.OpenTable).GetBind("fl", "rs", "Fields");
        for (int c = 0; c < cols.Length; c++)
        {
            p.Item("f" + c, "fl", A.Str(cols[c]));
        }
        foreach (Arg[] row in rows)
        {
            p.Call("rs", "AddNew");
            for (int c = 0; c < cols.Length; c++)
            {
                p.Put("f" + c, "Value", row[c]);
            }
            p.Call("rs", "Update");
        }
        for (int c = 0; c < cols.Length; c++)
        {
            p.Release("f" + c);
        }
        p.Release("fl").Call("rs", "Close").Release("rs");
    }

    /// <summary>Like <see cref="Insert"/>, but each row's Update has a marked DBEngine.Errors read.</summary>
    private static void RowsE(P p, string table, string[] cols, IEnumerable<Arg[]> rows)
    {
        p.OpenRs("re", "db", table, Dao.OpenTable).GetBind("refl", "re", "Fields");
        for (int c = 0; c < cols.Length; c++)
        {
            p.Item("rf" + c, "refl", A.Str(cols[c]));
        }
        foreach (Arg[] row in rows)
        {
            Mark(p);
            p.Call("re", "AddNew");
            for (int c = 0; c < cols.Length; c++)
            {
                p.Put("rf" + c, "Value", row[c]);
            }
            p.Call("re", "Update").Errors();
        }
        for (int c = 0; c < cols.Length; c++)
        {
            p.Release("rf" + c);
        }
        p.Release("refl").Call("re", "Close").Release("re");
    }

    /// <summary>ADO DDL on a closed file (Provider ACE.OLEDB.12.0, Data Source set through Connection.Properties), each statement on its own.</summary>
    private static void AdoDdl(P p, Arg path, params string[] sqls)
    {
        p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace))
            .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", path)
            .Release("cds").Release("cnp").Call("cn", "Open");
        foreach (string sql in sqls)
        {
            p.Call("cn", "Execute", A.Str(sql));
        }
        p.Call("cn", "Close").Release("cn");
    }
}
