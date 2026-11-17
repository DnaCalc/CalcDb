namespace CalcDb.Probes.Cards;

/// <summary>
/// R-STRING-1 and R-ORACLE-1 OR-WIRE: does ACE keep the noncharacters U+FFFE and U+FFFF in Text and Memo values?
/// Seen incidentally when the evidence index was verified: a LONGTEXT value "￾" written through the DAO PIA
/// read back as "". These probes ask it by design through the raw lane: each string is written to a TEXT(255) and
/// a MEMO column by DAO Field.Value put and by a SQL literal, then read back by DAO Value, Len and AscW of each
/// position, in ACCDB and MDB. The values vary one thing at a time: the code unit alone, leading, trailing, inside,
/// doubled, and U+FFFD as a control. Output files carry no card prefix: they are scratch databases.
/// </summary>
public sealed class RTextFffe : ICard
{
    private const string Card = "r-text-fffe";

    private static readonly (string Tag, string Value)[] Values =
    [
        ("fffe", "￾"),
        ("fffe-a", "￾a"),
        ("a-fffe", "a￾"),
        ("a-fffe-b", "a￾b"),
        ("fffe-fffe", "￾￾"),
        ("ffff", "￿"),
        ("a-ffff-b", "a￿b"),
        ("feff", "﻿"),
        ("fffd", "�"),
        ("plain", "ab"),
    ];

    public IEnumerable<Probe> Probes()
    {
        foreach ((string ext, int version) in new[] { ("accdb", Dao.Version120), ("mdb", Dao.Version40) })
        {
            foreach (string route in new[] { "dao-put", "sql-literal" })
            {
                string file = $"scratch-{route}.{ext}";
                yield return new Probe(Card, $"{ext}-{route}", ["R-STRING-1", "R-ORACLE-1", "OR-WIRE"],
                    $"On a new {ext.ToUpperInvariant()} database with W(ID LONG primary key, T TEXT(255), M MEMO), {Values.Length} rows are written by {(route == "dao-put" ? "a DAO dynaset AddNew, T and M Field.Value puts of the BSTR, and Update" : "DAO Database.Execute of INSERT statements with the string as a SQL literal")}: " +
                    string.Join(", ", Values.Select((v, i) => $"ID {i + 1} {v.Tag}")) +
                    ". After closing and reopening read-only, asks what a snapshot of SELECT ID, T, M, Len(T), Len(M), AscW(Mid(T,1,1)), AscW(Mid(T,2,1)), AscW(Mid(M,1,1)), AscW(Mid(M,2,1)) returns for each row.",
                    p =>
                    {
                        p.EngineLite().CallBind("db", "eng", "CreateDatabase", A.Out(file), A.Str(Dao.LangGeneral), A.I4(version))
                            .Call("db", "Execute", A.Str("CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, T TEXT(255), M MEMO)"));
                        if (route == "dao-put")
                        {
                            p.OpenRs("w", "db", "W", Dao.OpenDynaset).GetBind("wf", "w", "Fields");
                            for (int i = 0; i < Values.Length; i++)
                            {
                                p.Call("w", "AddNew")
                                    .Item("fi", "wf", A.Str("ID")).Put("fi", "Value", A.I4(i + 1)).Release("fi")
                                    .Item("ft", "wf", A.Str("T")).Put("ft", "Value", A.Str(Values[i].Value)).Get("ft", "Value").Release("ft")
                                    .Item("fm", "wf", A.Str("M")).Put("fm", "Value", A.Str(Values[i].Value)).Get("fm", "Value").Release("fm")
                                    .Call("w", "Update");
                            }
                            p.Release("wf").Call("w", "Close").Release("w");
                        }
                        else
                        {
                            for (int i = 0; i < Values.Length; i++)
                            {
                                p.Call("db", "Execute", A.Str($"INSERT INTO W (ID, T, M) VALUES ({i + 1}, '{Values[i].Value}', '{Values[i].Value}')"));
                            }
                        }
                        p.Errors().Call("db", "Close").Release("db");
                        p.CallBind("db", "eng", "OpenDatabase", A.Out(file), A.Bool(false), A.Bool(true))
                            .OpenRs("rs", "db", "SELECT ID, T, M, Len(T) AS LT, Len(M) AS LM, AscW(Mid(T,1,1)) AS T1, AscW(Mid(T,2,1)) AS T2, AscW(Mid(M,1,1)) AS M1, AscW(Mid(M,2,1)) AS M2 FROM W ORDER BY ID", Dao.OpenSnapshot)
                            .ReadAll("rs", 16).Call("rs", "Close").Release("rs").Errors();
                        p.FinishLite("db");
                    });
            }
        }
    }
}
