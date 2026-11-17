namespace CalcDb.Probes.Cards;

/// <summary>
/// R-ORACLE-1 OR-WIRE, R-FMT-1 and R-ADO-1 discriminator for stored Decimal values. Wave 4 captured ADO reads of
/// stored Decimal fields whose 96-bit integer appeared with its 32-bit words moved up one place (7 as Mid32 = 7,
/// Lo32 = 0), while other Decimal captures decoded normally. These probes ask, with the words of each value
/// distinct, what each read route returns for the same stored values: DAO Field.Value, SQL CStr and CDbl, SQL
/// equality filters, ADO Field.Value and ADO GetRows. Each probe uses one write route (SQL literal, DAO Field.Value
/// put, ADO Field.Value put), on a fresh database whose table is made through ADO DDL with explicit precision and
/// scale (DAO DDL refuses DECIMAL, evidence/r-ddl-limits). Fractional values are written as VT_CY, so no BSTR
/// conversion depends on the locale. The "dao-field" variants make N and S with DAO TableDef.CreateField(name, 20)
/// and no precision or scale, the form the wave-4 fixtures used. Output files do not carry the card prefix: they are
/// scratch databases, not fixtures.
/// </summary>
public sealed class RDecRead : ICard
{
    private const string Card = "r-dec-read";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const string Select = "SELECT ID, N, S, CStr(N) AS TN, CStr(S) AS TS, CDbl(N) AS DN FROM D ORDER BY ID";

    /// <summary>ID, N as integer text (Decimal(28,0)), S as a Currency amount (Decimal(18,4)).</summary>
    private static readonly (int Id, string N, decimal S)[] Rows =
    [
        (1, "7", 1.5m),
        (2, "4294967296", 0.0001m),
        (3, "4294967301", -2.5m),
        (4, "18446744073709551621", 12345678901234.5678m),
        (5, "79228162514264337593543950335", 0m),
        (6, "-7", -0.0001m),
        (7, "0", 3.125m),
    ];

    private static readonly string[] Filters =
    [
        "SELECT ID FROM D WHERE N = 7 ORDER BY ID",
        "SELECT ID FROM D WHERE N = 4294967296 ORDER BY ID",
        "SELECT ID FROM D WHERE N = 30064771072 ORDER BY ID",
        "SELECT ID FROM D WHERE N > 4294967295 ORDER BY ID",
        "SELECT ID FROM D WHERE S = 1.5 ORDER BY ID",
        "SELECT SUM(N) AS SN, MAX(N) AS MN, MIN(N) AS LN FROM D WHERE ID <= 3",
    ];

    public IEnumerable<Probe> Probes()
    {
        foreach ((string ext, int version) in new[] { ("accdb", Dao.Version120), ("mdb", Dao.Version40) })
        {
            foreach ((string route, bool daoField) in new[] { ("sql-literal", false), ("dao-put", false), ("ado-put", false), ("sql-literal", true), ("dao-put", true), ("ado-put", true) })
            {
                string name = daoField ? "daofield-" + route : route;
                string file = $"scratch-{name}.{ext}";
                string ddl = daoField
                    ? "DAO creates D(ID LONG primary key) by DDL and appends N and S with TableDef.CreateField(name, 20) (Decimal, no precision or scale given)"
                    : $"ADO (Provider {Ace}) creates D(ID INTEGER primary key, N DECIMAL(28,0), S DECIMAL(18,4))";
                yield return new Probe(Card, $"{ext}-{name}", ["R-ORACLE-1", "OR-WIRE", "R-FMT-1", "R-ADO-1"],
                    $"On a new {ext.ToUpperInvariant()} database, {ddl}. Then N and S are read from TableDefs (Type, Size, Attributes, and Properties Precision and Scale where present). Seven rows are written by {Describe(route)}: N = {string.Join(", ", Rows.Select(r => r.N))}; S = {string.Join(", ", Rows.Select(r => r.S.ToString(System.Globalization.CultureInfo.InvariantCulture)))}. Then, after closing and reopening, asks what DAO Field.Value, SQL CStr(N), CStr(S) and CDbl(N), DAO equality and range filters on N and S, SUM/MAX/MIN of N, ADO Field.Value and ADO GetRows return for those rows.",
                    p =>
                    {
                        p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(version));
                        if (daoField)
                        {
                            p.Call("db", "Execute", A.Str("CREATE TABLE D (ID LONG CONSTRAINT PK_D PRIMARY KEY)")).AddDecimalField("D", "N").AddDecimalField("D", "S");
                        }
                        p.Call("db", "Close").Release("db");
                        OpenAdo(p, file);
                        if (!daoField)
                        {
                            p.Call("cn", "Execute", A.Str("CREATE TABLE D (ID INTEGER CONSTRAINT PK_D PRIMARY KEY, N DECIMAL(28,0), S DECIMAL(18,4))"));
                        }
                        Write(p, route, file);
                        p.Call("cn", "Close").Release("cn");

                        p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true));
                        p.GetBind("tds", "db", "TableDefs").Item("td", "tds", A.Str("D")).GetBind("tdf", "td", "Fields");
                        foreach (string f in new[] { "N", "S" })
                        {
                            p.Item("df", "tdf", A.Str(f)).Get("df", "Type").Get("df", "Size").Get("df", "Attributes")
                                .GetBind("dfp", "df", "Properties").Each("dfp", "pp", 40, b => b.Get("pp", "Name").Get("pp", "Value")).Release("dfp").Release("df");
                        }
                        p.Release("tdf").Release("td").Release("tds");
                        p.OpenRs("rs", "db", Select, Dao.OpenSnapshot).ReadAll("rs", 16).Call("rs", "Close").Release("rs");
                        foreach (string sql in Filters)
                        {
                            p.OpenRs("rf", "db", sql, Dao.OpenSnapshot).ReadAll("rf", 16).Call("rf", "Close").Release("rf");
                        }
                        p.Errors().Call("db", "Close").Release("db");

                        OpenAdo(p, file);
                        p.Create("ADODB.Recordset", "ar").Call("ar", "Open", A.Str(Select), A.Obj("cn"), A.I4(3), A.I4(1), A.I4(1))
                            .ReadAll("ar", 16).Call("ar", "MoveFirst").Call("ar", "GetRows").Call("ar", "Close").Release("ar");
                        p.FinishLite("cn");
                    });
            }
        }
    }

    private static string Describe(string route) => route switch
    {
        "sql-literal" => "DAO Database.Execute of INSERT statements with the values as SQL numeric literals",
        "dao-put" => "a DAO dynaset AddNew, Field.Value puts (N as BSTR integer text, S as VT_CY) and Update",
        _ => "an ADO keyset recordset (adLockOptimistic) AddNew, Field.Value puts (N as BSTR integer text, S as VT_CY) and Update",
    };

    private static void OpenAdo(P p, string file) =>
        p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace))
            .GetBind("cnp", "cn", "Properties").Item("cds", "cnp", A.Str("Data Source")).Put("cds", "Value", A.Out(file))
            .Release("cds").Release("cnp").Call("cn", "Open");

    private static void Write(P p, string route, string file)
    {
        switch (route)
        {
            case "sql-literal":
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false));
                foreach ((int id, string n, decimal s) in Rows)
                {
                    p.Call("db", "Execute", A.Str($"INSERT INTO D (ID, N, S) VALUES ({id}, {n}, {s.ToString(System.Globalization.CultureInfo.InvariantCulture)})"));
                }
                p.Errors().Call("db", "Close").Release("db");
                break;
            case "dao-put":
                p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(false));
                p.OpenRs("w", "db", "D", Dao.OpenDynaset).GetBind("wf", "w", "Fields");
                PutRows(p, "w", "wf");
                p.Release("wf").Call("w", "Close").Release("w").Errors().Call("db", "Close").Release("db");
                break;
            default:
                p.Create("ADODB.Recordset", "aw").Call("aw", "Open", A.Str("D"), A.Obj("cn"), A.I4(1), A.I4(3), A.I4(2)).GetBind("wf", "aw", "Fields");
                PutRows(p, "aw", "wf");
                p.Release("wf").Call("aw", "Close").Release("aw");
                break;
        }
    }

    private static void PutRows(P p, string rs, string fields)
    {
        foreach ((int id, string n, decimal s) in Rows)
        {
            p.Call(rs, "AddNew")
                .Item("fi", fields, A.Str("ID")).Put("fi", "Value", A.I4(id)).Release("fi")
                .Item("fn", fields, A.Str("N")).Put("fn", "Value", A.Str(n)).Get("fn", "Value").Release("fn")
                .Item("fs", fields, A.Str("S")).Put("fs", "Value", A.Cy(s)).Get("fs", "Value").Release("fs")
                .Call(rs, "Update");
        }
    }
}
