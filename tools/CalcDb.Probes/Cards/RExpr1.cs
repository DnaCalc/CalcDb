namespace CalcDb.Probes.Cards;

/// <summary>
/// R-EXPR-1: shared values with Access context (A-02), the scalar and context baseline that DAO can answer.
/// The probes evaluate expressions as <c>SELECT expr FROM One</c> on a one-row table, through temporary
/// QueryDefs with declared and undeclared parameters, as predicates and Find criteria, and as field and table
/// property expressions on writable fixture copies. Each expression that might fail gets its own snapshot, so
/// one failure does not hide another. Every value is captured as the returned VARIANT and Field.Type; nothing is
/// compared as formatted text. The LIKE wildcard modes are asked through DAO LIKE and ALIKE and through the ACE
/// OLE DB provider (ADO). Access-hosted, VBA and host-UDF routes are tier 2 and are not here.
/// </summary>
public sealed class RExpr1 : ICard
{
    private const string Card = "r-expr-1";
    // "-main2": the first main fixture lost table Vals to DECIMAL in DAO DDL (3292); this one adds M through DAO.
    private const string Main = Card + "-main2";
    private const string Spanish = Card + "-spanish";
    private const string LangSpanish = ";LANGID=0x040A;CP=1252;COUNTRY=0";
    private const string AceProvider = "Microsoft.ACE.OLEDB.12.0";

    private static readonly ulong Bits25 = (ulong)BitConverter.DoubleToInt64Bits(2.5);
    private static readonly ulong Bits01 = (ulong)BitConverter.DoubleToInt64Bits(0.1);

    private static readonly double[] DtsValues = [-0.25, 0.25, -1.25, -1.5, 0.0, -0.75, -0.5, 45000.5];

    /// <summary>Subject groups of the Pat table (column G), one per PAT case family.</summary>
    private static readonly (string Group, string[] Subjects)[] PatGroups =
    [
        ("p01", ["Q", "Q*", "Q%", "Q7", "Q*7", "Q7%", "q"]),
        ("p02", ["[", "]", "[]", "-", "a", "!", "^", "", "x"]),
        ("p03", ["A", "B", "C", "a", "b", "c", "\u00E0", "\u00E9", "\u00E6", "-"]),
        ("p04", ["x", "qx", "q", "", "yq", "xqx", "qabc"]),
        ("p07", ["\u00E6", "ae", "\u00C6", "AE", "a", "e", "aex", "\u00E6x"]),
        ("p09", ["c", "ch", "cz", "d", "h", "c-h", "cha", "ci", "Ch", "CH"]),
        ("p10", ["\uD83D\uDE00", "\uD800", "\uDC00", "\uDE00\uD83D", "a\uD83D\uDE00b", "ab"]),
        ("p11", ["A", "A\0", "A\0B", "A\0C"]),
        ("p12", ["A", "A ", "A  ", "A\u00A0", "A\t", "a"]),
        ("p13", ["5", "\u0661", "\uFF11", ".", "\u0301", "a", "a\u0301"]),
        ("p14", ["MN", "MNO", "AMN", "AMNO", "mn", "Mn", ""]),
        ("p17", [new string('a', 20), new string('a', 19) + "b"]),
        ("p18", ["e\u0301", "\u00E9", "\u00DF", "ss", "I", "\u0131", "\u0130", "i", "\u30A2", "\uFF71", "\u30AB", "\uFF76"]),
    ];

    /// <summary>Unique binder names within one probe program.</summary>
    private sealed class Names
    {
        private int next;

        public string Next(string prefix) => prefix + next++;
    }

    public IEnumerable<Probe> Probes() =>
    [
        Creation(Main, Dao.LangGeneral,
            "tables One (one row), Vals (one row of typed scalars: Byte 255, Integer 32767, Long 2147483647, Single 1.5, Double 2.5, Currency 12.3456, Decimal(28,4) from the BSTR \"-1844674407370955.1621\", Date 45000.5, Yes/No True, Text \"7\" and \"q\", Long Null), Big (BIGINT from the BSTR \"9223372036854775807\", and -1), Den (Id, Denominator, TextValue: (1,2,'12'), (2,0,'abc'), (3,Null,Null)), Dts (DATETIME values -0.25, 0.25, -1.25, -1.5, 0, -0.75, -0.5, 45000.5 with index IX_D), Pat (grouped pattern subjects stored as typed BSTRs, including empty, NUL, surrogate and combining subjects; index IX_S; S AllowZeroLength set True) and Nm (columns named Date, Len and p)",
            MainFixture),
        Creation(Spanish, LangSpanish,
            "table Sp(Id, S TEXT(10)) with index IX_SpS holding the PAT-09 subjects (c, ch, cz, d, h, c-h, cha, ci, Ch, CH), in a database created with the Spanish (LANGID 0x040A) sort locale",
            (p, n) =>
            {
                Exec(p, "CREATE TABLE Sp (Id LONG CONSTRAINT PK_Sp PRIMARY KEY, S TEXT(10))", "CREATE INDEX IX_SpS ON Sp (S)");
                AddRows(p, n, "Sp", ["Id", "S"], PatGroups.Single(g => g.Group == "p09").Subjects.Select((s, i) => new[] { A.I4(i + 1), A.Str(s) }));
            }),

        // Branch A: value versus failure and argument presence.
        Read("a-null-empty-error-literals", ["Branch A"],
            "Asks what IsNull, IsEmpty, IsError, CVErr and Null propagation through +, &, =, unary minus, Not and Len return or raise in a projection, each expression in its own snapshot SELECT expr FROM One, plus the stored Null Long field N of Vals through the same functions.",
            (p, n) =>
            {
                Scalars(p, n, "IsNull(Null)", "IsNull('')", "IsNull(0)", "IsEmpty(Null)", "IsEmpty('')", "IsError(1)", "IsError(Null)",
                    "Null + 1", "Null & 'x'", "'x' & Null", "Null & Null", "Null = Null", "Null <> 1", "-Null", "Not Null", "Len(Null)", "Len('')",
                    "CVErr(2007)", "IsError(CVErr(2007))", "CVErr(0)", "CVErr(65536)", "CVErr(Null)", "CVErr(5) + 1", "IsNull(CVErr(5))");
                Sel(p, n, "SELECT N, IsNull(N) AS a, N + 1 AS b, N & 'x' AS c FROM Vals");
                Sel(p, n, "SELECT IsEmpty(N) FROM Vals");
            }),
        Read("a-parameter-inputs", ["Branch A", "EXPR-BIND"],
            "Asks which Automation inputs a QueryDef parameter admits and what the query returns for each: a declared Long and a declared Text parameter, and an undeclared parameter [pv], each set to Null, Empty, the optional-argument marker (VT_ERROR DISP_E_PARAMNOTFOUND), an empty BSTR, a BSTR with an embedded U+0000, a lone-surrogate BSTR and ordinary typed values. Also asks the failure of a fresh declared-parameter QueryDef opened without a value. Parameter Name and Type are read from the Parameters collection.",
            (p, n) =>
            {
                string ql = QDef(p, n, "PARAMETERS pl Long; SELECT pl AS v, IsNull(pl) AS n, pl + 1 AS s FROM One");
                QRunEach(p, n, ql, "pl", A.Null, A.Empty, A.Missing, A.I4(7), A.Str("7"), A.Str("q"), A.Str(""), A.R8(2.5), A.Bool(true));
                QDone(p, ql);
                string qt = QDef(p, n, "PARAMETERS pt Text; SELECT pt AS v, IsNull(pt) AS n, Len(pt) AS l, pt & '|' AS c FROM One");
                QRunEach(p, n, qt, "pt", A.Null, A.Empty, A.Missing, A.Str(""), A.Str("A\0B"), A.Str("\uD800"), A.I4(7));
                QDone(p, qt);
                string qv = QDef(p, n, "SELECT [pv] AS v, IsNull([pv]) AS n FROM One");
                QRunEach(p, n, qv, "pv", A.Null, A.Empty, A.Missing, A.Str(""), A.Str("A\0B"), A.Str("\uD800"), A.I4(7), A.R8(2.5),
                    A.Bool(true), A.Date(0.25), A.Cy(1.5m), A.UI1(255), A.I2(-1));
                QDone(p, qv);
                string qu = QDef(p, n, "PARAMETERS pl Long; SELECT pl AS v FROM One");
                QRun(p, n, qu);
                QDone(p, qu);
            }),
        Read("a-optional-arguments", ["Branch A", "Branch E"],
            "Asks how omission of an optional argument compares with explicit Null, Empty and typed replacements for Nz, Mid, InStr and Round, each expression in its own snapshot SELECT expr FROM One, and with Empty and the optional-argument marker through an undeclared parameter in Nz([pv]) and Nz([pv], 0).",
            (p, n) =>
            {
                Scalars(p, n, "Nz(Null)", "Nz(Null, Null)", "Nz(Null, '')", "Nz(Null, 0)", "Nz(Null, 'x')", "Nz(5, 'x')", "Nz('', 1)",
                    "Mid('abcd', 2)", "Mid('abcd', 2, Null)", "Mid('abcd', Null)", "InStr('abcd', 'c')", "InStr(1, 'abcd', 'c')",
                    "InStr(Null, 'abcd', 'c')", "InStr(1, 'abcd', 'C', 0)", "InStr(1, 'abcd', 'C', 1)", "Round(2.5)", "Round(2.5, 0)", "Round(2.5, Null)");
                string q1 = QDef(p, n, "SELECT Nz([pv]) AS v FROM One");
                QRunEach(p, n, q1, "pv", A.Empty, A.Null, A.Missing, A.I4(3));
                QDone(p, q1);
                string q2 = QDef(p, n, "SELECT Nz([pv], 0) AS v FROM One");
                QRunEach(p, n, q2, "pv", A.Empty, A.Null, A.Missing);
                QDone(p, q2);
            }),

        // Branch B: operator dispatch and exact result types.
        Read("b-numeric-text-compare", ["Branch B"],
            "Asks how a numeric operand compares with numeric text \"7\" and nonnumeric text \"q\" in the literal lane (each comparison its own SELECT), the stored-field lane (Vals.T = '7', Vals.Q = 'q', Vals.L, Vals.D), the declared-parameter lane (PARAMETERS pn Long, pt Text) and the undeclared-parameter lane: result tag and value, or the failure.",
            (p, n) =>
            {
                Scalars(p, n, "7 = '7'", "'7' = 7", "7 < 'q'", "'q' > 7", "7 = 'q'", "'7' < '10'", "7 < 10", "'10' < 7", "7 = '07'");
                foreach (string e in new[] { "T = 7", "T < 10", "Q > 7", "T = Q", "D = '2.5'", "L > T" })
                {
                    Sel(p, n, $"SELECT {e} FROM Vals");
                }
                string qd = QDef(p, n, "PARAMETERS pn Long, pt Text; SELECT pn = pt AS eq, pn < pt AS lt FROM One");
                QRun(p, n, qd, ("pn", A.I4(7)), ("pt", A.Str("7")));
                QRun(p, n, qd, ("pn", A.I4(7)), ("pt", A.Str("q")));
                QRun(p, n, qd, ("pn", A.I4(7)), ("pt", A.Str("07")));
                QDone(p, qd);
                string qu = QDef(p, n, "SELECT [ua] = [ub] AS eq, [ua] < [ub] AS lt FROM One");
                QRun(p, n, qu, ("ua", A.I4(7)), ("ub", A.Str("7")));
                QRun(p, n, qu, ("ua", A.I4(7)), ("ub", A.Str("q")));
                QRun(p, n, qu, ("ua", A.Str("7")), ("ub", A.Str("07")));
                QRun(p, n, qu, ("ua", A.I4(10)), ("ub", A.Str("9")));
                QDone(p, qu);
            }),
        Read("b-plus-versus-concat", ["Branch B"],
            "Asks how + and & dispatch over numeric 7, text \"7\", nonnumeric text, Null and (through an undeclared parameter) Empty, and the result tag of each. Each literal expression and each stored-field expression on Vals is its own SELECT. [pe] + 7, [pe] & 'x' and [pe] + [pe] are each run with Empty and with Null.",
            (p, n) =>
            {
                Scalars(p, n, "7 + '7'", "'7' + '7'", "'7' + 7", "7 & '7'", "7 & 7", "7 + Null", "Null + '7'", "Null & Null", "7 & Null",
                    "'q' + 7", "'q' + 'q'", "'7' - '2'", "'7' * '2'", "True & ''", "#2024-01-01# & ''");
                foreach (string e in new[] { "T + 7", "T + T", "T & L", "Q + 7", "N + T", "N & T" })
                {
                    Sel(p, n, $"SELECT {e} FROM Vals");
                }
                foreach (string e in new[] { "[pe] + 7", "[pe] & 'x'", "[pe] + [pe]" })
                {
                    string qd = QDef(p, n, $"SELECT {e} AS v FROM One");
                    QRunEach(p, n, qd, "pe", A.Empty, A.Null);
                    QDone(p, qd);
                }
            }),
        Read("b-integer-widening", ["Branch B"],
            "Asks whether integer arithmetic on stored Byte 255, Integer 32767 and Long 2147483647 widens or overflows when the other operand is a separately typed 1 or 2 (CByte, CInt, CLng) or an untyped literal, the same for typed constants, and for the BIGINT (Large Number) column of Big. Each expression is its own SELECT. The result tag and Field.Type, or the failure.",
            (p, n) =>
            {
                foreach (string e in new[] { "B + CByte(1)", "I + CInt(1)", "L + CLng(1)", "B * CByte(2)", "I * CInt(2)", "L * CLng(2)",
                    "B + 1", "I + 1", "L + 1", "B - CByte(255) - CByte(1)", "-I - CInt(2)", "B + I", "I + L" })
                {
                    Sel(p, n, $"SELECT {e} FROM Vals");
                }
                Scalars(p, n, "CByte(255) + CByte(1)", "CInt(32767) + CInt(1)", "CLng(2147483647) + CLng(1)", "2147483647 + 1", "32767 + 1");
                foreach (string e in new[] { "LL", "LL + 1", "LL + CLng(1)", "LL * 2", "LL + LL", "LL - 1", "LL + 0.5" })
                {
                    Sel(p, n, $"SELECT ID, {e} AS v FROM Big ORDER BY ID");
                }
            }),
        Read("b-mixed-precedence", ["Branch B"],
            "Asks the published result type of mixed-type addition and multiplication in both operand orders (Currency/Double, Single/Long, Single/Double, Currency/Long, Decimal with Double/Currency/Long, Boolean and Date operands) over the stored Vals row, as Field.Type and returned VARIANT tag in one projection. A second projection asks the inner VarType of the same expressions where VarType is admitted.",
            (p, n) =>
            {
                string[] exprs = ["C + D", "D + C", "C * D", "D * C", "S + L", "L + S", "S * L", "L * S", "S + D", "D + S", "C + L", "L + C",
                    "M + D", "D + M", "M + C", "C + M", "M + L", "M * L", "Y + 1", "Y * Y", "DT + 1", "1 + DT", "DT - DT", "DT + C", "B + B", "I + B"];
                Sel(p, n, "SELECT " + string.Join(", ", exprs.Select((e, i) => $"{e} AS e{i}")) + " FROM Vals");
                Sel(p, n, "SELECT " + string.Join(", ", exprs.Select((e, i) => $"VarType({e}) AS t{i}")) + " FROM Vals");
            }),
        Read("b-integer-conversion", ["Branch B"],
            "Asks how CInt, CLng, CByte, Int, Fix and Round treat halves, neighbours of halves and range-boundary halves (round-half-even, truncation, overflow and its phase). Each literal is its own SELECT, then CInt/CLng of the stored Double 2.5, then CInt([px]) through an undeclared parameter with R8 2.5, its two adjacent Double payloads, -2.5, 3.5, 32767.5, Currency 2.5 and the BSTR \"2.5\".",
            (p, n) =>
            {
                Scalars(p, n, "CInt(2.5)", "CInt(-2.5)", "CInt(3.5)", "CInt(-3.5)", "CInt(2.4999999999999996)", "CInt(2.5000000000000004)",
                    "CLng(2.5)", "CLng(-0.5)", "CByte(0.5)", "CByte(1.5)", "Int(-2.5)", "Fix(-2.5)", "Round(-2.5)", "CInt(32767.4)", "CInt(32767.5)",
                    "CInt(-32768.5)", "CLng(2147483647.5)", "CLng(-2147483648.5)", "CByte(255.5)", "CByte(-0.5)", "CByte(-0.6)", "CInt('2.5')");
                Sel(p, n, "SELECT CInt(D) AS a, CLng(D) AS b, CInt(-D) AS c, CByte(D) AS d FROM Vals");
                string qd = QDef(p, n, "SELECT CInt([px]) AS v FROM One");
                QRunEach(p, n, qd, "px", A.R8(2.5), A.R8Bits(Bits25 - 1), A.R8Bits(Bits25 + 1), A.R8(-2.5), A.R8(3.5), A.R8(32767.5), A.Cy(2.5m), A.Str("2.5"));
                QDone(p, qd);
            }),
        Read("b-intdiv-mod", ["Branch B"],
            "Asks whether \\ and Mod round or truncate nonintegral operands before the operation, the sign of the result with negative operands, and when division by zero, including a divisor that rounds to zero, fails. Each literal expression is its own SELECT; then 7 \\ Denominator and 7 Mod Denominator over the rows of Den (2, 0, Null), each as its own SELECT.",
            (p, n) =>
            {
                Scalars(p, n, "7.5 \\ 2", "7 \\ 2.5", "7 \\ 2.4", "-7 \\ 2", "-7.5 \\ 2", "7 Mod 2.5", "7 Mod 2.4", "-7 Mod 2", "7.5 Mod -2",
                    "5 \\ 0.4", "5 Mod 0.4", "5 \\ 0.5", "1 \\ 0", "1 Mod 0", "1 / 0", "0 / 0", "0 \\ 0", "2147483648 \\ 1", "7 \\ '2'",
                    "CByte(7) \\ CByte(2)", "Null \\ 0", "0 / Null");
                Sel(p, n, "SELECT Id, 7 \\ [Denominator] AS q FROM Den ORDER BY Id");
                Sel(p, n, "SELECT Id, 7 Mod [Denominator] AS r FROM Den ORDER BY Id");
            }),
        Read("b-fixed-point-fidelity", ["Branch B"],
            "Asks whether Decimal and Currency payloads survive arithmetic, conversion and comparison. Decimal: the stored M of Vals (the -(2^64+5)/10^4 magnitude) through identity, unary minus, +0, *1, CDec, CDbl, CCur and division forms, and CDec of BSTR forms (locale-sensitive text, recorded as such) for scale-28 values and equal values with different scales. Currency: a declared Currency parameter with 12.3456, the maximum and the minimum, each through identity, plus or minus one unit, and doubling, plus CCur rounding at the fifth decimal and the stored C. Each expression is its own SELECT.",
            (p, n) =>
            {
                foreach (string e in new[] { "M", "-M", "M + 0", "M * 1", "M + CDec(0)", "CDbl(M)", "CCur(M)", "M / 1", "M \\ 1", "M = M", "VarType(M)",
                    "C", "C * C", "C / 3", "C + 0.00005", "VarType(C * C)" })
                {
                    Sel(p, n, $"SELECT {e} FROM Vals");
                }
                Scalars(p, n, "CDec(-1844674407370955) - CDec(1621) / CDec(10000)", "CDec('0.0000000000000000000000000001')",
                    "CDec('0.0000000000000000000000000001') / 2", "CDec('1.0') + CDec('1.00')", "CDec('1.0') = CDec('1.00')", "CDec(1) / CDec(3)",
                    "CCur(0.00005)", "CCur(0.00015)", "CCur(0.00025)", "CCur(-0.00005)");
                foreach (string e in new[] { "pc", "pc + CCur(0.0001)", "pc - CCur(0.0001)", "pc * 2" })
                {
                    string qd = QDef(p, n, $"PARAMETERS pc Currency; SELECT {e} AS v FROM One");
                    QRunEach(p, n, qd, "pc", A.Cy(12.3456m), A.Cy(922337203685477.5807m), A.Cy(-922337203685477.5808m));
                    QDone(p, qd);
                }
            }),
        Read("b-floating-fidelity", ["Branch B"],
            "Asks whether comparison and subtraction distinguish adjacent Double payloads and signed zeros (declared IEEEDouble pair), how a declared IEEESingle compares with a declared IEEEDouble of the same decimal text, which special payloads (negative zero, NaN, infinities, denormals) an undeclared parameter admits and returns unchanged, and how the stored Single/Double fields compare with literals and CSng/CDbl.",
            (p, n) =>
            {
                string qd = QDef(p, n, "PARAMETERS pa IEEEDouble, pb IEEEDouble; SELECT pa, pb, pa = pb AS eq, pa < pb AS lt, pa - pb AS diff FROM One");
                QRun(p, n, qd, ("pa", A.R8(0.1)), ("pb", A.R8Bits(Bits01 + 1)));
                QRun(p, n, qd, ("pa", A.R8(0.0)), ("pb", A.R8(-0.0)));
                QRun(p, n, qd, ("pa", A.R8(-0.0)), ("pb", A.R8(-0.0)));
                QDone(p, qd);
                string qs = QDef(p, n, "PARAMETERS ps IEEESingle, pd IEEEDouble; SELECT ps, pd, ps = pd AS eq, ps - pd AS diff FROM One");
                QRun(p, n, qs, ("ps", A.R4(1.1f)), ("pd", A.R8(1.1)));
                QRun(p, n, qs, ("ps", A.R4(1.5f)), ("pd", A.R8(1.5)));
                QRun(p, n, qs, ("ps", A.R4(-0.0f)), ("pd", A.R8(0.0)));
                QDone(p, qs);
                string qu = QDef(p, n, "SELECT [u] AS v, [u] = 0 AS z FROM One");
                QRunEach(p, n, qu, "u", A.R8(-0.0), A.R4(-0.0f), A.R8Bits(0x7FF8000000000000UL), A.R8(double.PositiveInfinity),
                    A.R8(double.NegativeInfinity), A.R8(double.Epsilon), A.R4(float.Epsilon));
                QDone(p, qu);
                Sel(p, n, "SELECT S, D, S = 1.5 AS a, D = 2.5 AS b, CSng(D) = S AS c, CDbl(S) AS d FROM Vals");
                Scalars(p, n, "0.1 + 0.2 = 0.3", "CSng(0.1) = 0.1", "1E308 * 10", "-0.0");
            }),

        // Branch C: DATE encoding versus comparison and arithmetic.
        Read("c-date-payloads", ["Branch C"],
            "Asks whether DATE payloads -0.25/0.25, -1.25/-1.5, 0/-0.25, -1.5/-0.5 and two modern times compare equal or ordered, and what subtraction, +1, -1 and CDbl return. Sent as declared DateTime parameters with VT_DATE values, as declared DateTime with VT_R8 values, and as undeclared parameters with VT_DATE and VT_R8 values. A second declared QueryDef asks DateAdd, DateDiff, Hour, Day, DateValue and TimeValue over the same pairs. Payloads are captured as returned.",
            (p, n) =>
            {
                (double A, double B)[] pairs = [(-0.25, 0.25), (-1.25, -1.5), (0.0, -0.25), (-1.5, -0.5), (45000.25, 45000.5)];
                string qc = QDef(p, n, "PARAMETERS pa DateTime, pb DateTime; SELECT pa, pb, pa = pb AS eq, pa < pb AS lt, pa - pb AS diff, CDbl(pa) AS da, CDbl(pb) AS db, pa + 1 AS inc, pa - 1 AS dec FROM One");
                foreach ((double a, double b) in pairs)
                {
                    QRun(p, n, qc, ("pa", A.Date(a)), ("pb", A.Date(b)));
                }
                QRun(p, n, qc, ("pa", A.R8(-0.25)), ("pb", A.R8(0.25)));
                QDone(p, qc);
                string qf = QDef(p, n, "PARAMETERS pa DateTime, pb DateTime; SELECT DateAdd('h', 1, pa) AS a, DateDiff('h', pa, pb) AS b, Hour(pa) AS c, Day(pa) AS d, DateValue(pa) AS e, TimeValue(pa) AS f FROM One");
                foreach ((double a, double b) in pairs)
                {
                    QRun(p, n, qf, ("pa", A.Date(a)), ("pb", A.Date(b)));
                }
                QDone(p, qf);
                string qu = QDef(p, n, "SELECT [ua] = [ub] AS eq, [ua] < [ub] AS lt, [ua] - [ub] AS diff FROM One");
                QRun(p, n, qu, ("ua", A.Date(-0.25)), ("ub", A.Date(0.25)));
                QRun(p, n, qu, ("ua", A.R8(-0.25)), ("ub", A.R8(0.25)));
                QRun(p, n, qu, ("ua", A.Date(-0.25)), ("ub", A.R8(-0.25)));
                QDone(p, qu);
            }),
        Read("c-date-text-and-literals", ["Branch C"],
            "Asks what date literals (#...# in US and ISO forms), CDate and DateValue of text (including the ambiguous '1/2/2003' and the invalid '2/30/2003'), and CDate/CDbl of negative and positive fractional numbers return, each expression in its own SELECT. The results depend on the oracle's locale, which is recorded with the run.",
            (p, n) => Scalars(p, n, "CDate('1899-12-30 06:00')", "#12/30/1899 06:00#", "#1899-12-30 06:00#", "CDate('1/2/2003')", "DateValue('1/2/2003')",
                "#1/2/2003#", "CDate('2003-02-01')", "CDate(-0.25)", "CDate(0.25)", "CDbl(#12/30/1899 06:00#)", "CDbl(CDate(-0.25))",
                "#12/29/1899 18:00#", "CDbl(#12/29/1899 18:00#)", "IsDate('2/30/2003')", "CDate('2/30/2003')", "CDate(-657435)", "CDate(2958466)")),
        Read("c-date-stored-predicates", ["Branch C"],
            "Asks which Dts rows (DATE payloads -0.25, 0.25, -1.25, -1.5, 0, -0.75, -0.5, 45000.5) are selected by equality and range predicates on D written as date literals, CDate(-0.25) and CDbl(D), the ORDER BY D sequence, and what a table-type Seek on index IX_D finds for VT_DATE -0.25, 0.25, -1.25 and VT_R8 -0.25 (NoMatch and current row).",
            (p, n) =>
            {
                Sel(p, n, "SELECT ID, D, CDbl(D) AS x FROM Dts ORDER BY D, ID");
                foreach (string w in new[] { "D = #12/30/1899 06:00#", "D = CDate(-0.25)", "D = CDate(0.25)", "D < #12/30/1899#", "CDbl(D) < 0",
                    "D BETWEEN #12/29/1899# AND #12/31/1899#", "D > CDate(-0.75)" })
                {
                    Sel(p, n, $"SELECT ID FROM Dts WHERE {w} ORDER BY ID");
                }
                p.OpenRs("ts", "db", "Dts", Dao.OpenTable).Put("ts", "Index", A.Str("IX_D"));
                foreach (Arg key in new[] { A.Date(-0.25), A.Date(0.25), A.Date(-1.25), A.R8(-0.25) })
                {
                    p.Call("ts", "Seek", A.Str("="), key).Get("ts", "NoMatch");
                    CurrentRow(p, n, "ts");
                }
                p.Call("ts", "Close").Release("ts");
            }),

        // Branch D: logical/bitwise operation and consumer acceptance.
        Read("d-logical-bitwise", ["Branch D"],
            "Asks the result of And, Or, Xor, Eqv, Imp and Not over the full True/False/Null grid in one projection, then bitwise results on integer pairs (6, 3), mixed Boolean/integer, mixed integer widths and nonintegral operands, each of the latter its own SELECT.",
            (p, n) =>
            {
                Sel(p, n, $"SELECT {LogicGrid()} FROM One");
                Scalars(p, n, "6 And 3", "6 Or 3", "6 Xor 3", "Not 6", "6 Eqv 3", "6 Imp 3", "True And 3", "True Or 6", "-1 And 3", "2.5 And 3",
                    "3.5 And 7", "CByte(6) And CInt(3)", "CByte(6) And CLng(3)", "Not CByte(6)", "'6' And 3", "'x' And True", "Null And 0", "Null Or -1");
            }),
        Read("d-predicate-consumers", ["Branch D"],
            "Asks how three consumers treat the same scalar: the row selection of SELECT ID FROM One WHERE v, IIf(v, 'T', 'F'), and Recordset.FindFirst v on a dynaset (NoMatch), for v in Null, 0, 1, -1, 2, 2.5, '', '1', '0', 'q', 'True' and CVErr(5).",
            (p, n) =>
            {
                string[] values = ["Null", "0", "1", "-1", "2", "2.5", "''", "'1'", "'0'", "'q'", "'True'", "CVErr(5)"];
                foreach (string v in values)
                {
                    Sel(p, n, $"SELECT ID FROM One WHERE {v}");
                    Sel(p, n, $"SELECT IIf({v}, 'T', 'F') FROM One");
                }
                p.OpenRs("fr", "db", "SELECT ID FROM One", Dao.OpenDynaset);
                foreach (string v in values)
                {
                    p.Call("fr", "FindFirst", A.Str(v)).Get("fr", "NoMatch");
                }
                p.Call("fr", "Close").Release("fr");
            }),

        // Branch E: conditional functions, eager arguments and phase order.
        Read("e-eager-failures", ["Branch E"],
            "Asks whether an unselected IIf/Switch/Choose arm that would fail (constant 1/0, CInt('q'), row-dependent 1/[Denominator] and CInt([TextValue]) on Den row 2, parameter-dependent 1/pz with pz = 0) fails the query, and when two failures are possible which one is reported, with arms and operands in both orders. Each expression is its own SELECT.",
            (p, n) =>
            {
                Scalars(p, n, "IIf(True, 1, 1/0)", "IIf(False, 1/0, 2)", "IIf(True, 1, CInt('q'))", "Switch(True, 1, True, 1/0)", "Choose(1, 'a', 1/0)",
                    "IIf(False, 1/0, CInt('q'))", "IIf(True, CInt('q'), 1/0)", "1/0 + CInt('q')", "CInt('q') + 1/0");
                foreach (string e in new[] { "IIf(True, 1, 1/[Denominator])", "IIf(True, 1, CInt([TextValue]))", "1/[Denominator] + CInt([TextValue])",
                    "CInt([TextValue]) + 1/[Denominator]", "IIf([Denominator] = 0, CInt([TextValue]), 1/[Denominator])" })
                {
                    Sel(p, n, $"SELECT Id, {e} AS v FROM Den WHERE Id = 2");
                }
                Sel(p, n, "SELECT Id, IIf([Denominator] = 0, 0, 1/[Denominator]) AS v FROM Den ORDER BY Id");
                string qd = QDef(p, n, "PARAMETERS pz Long; SELECT IIf(pz = 0, 0, 1 / pz) AS v FROM One");
                QRunEach(p, n, qd, "pz", A.I4(0), A.I4(4));
                QDone(p, qd);
            }),
        Read("e-conditional-selection", ["Branch E"],
            "Asks the result tag, value or failure of IIf with Null, 0 and mixed-type arms, of Switch with first-true, no-true, Null condition and malformed pairing, and of Choose with out-of-range, Null, text and half-index selectors, each its own SELECT. Then IIf(Id = 1, 1, 'a') over the rows of Den, with Field.Type, for mixed arm types across rows.",
            (p, n) =>
            {
                Scalars(p, n, "IIf(Null, 1, 'a')", "IIf(True, 1, 'a')", "IIf(False, 1, 'a')", "IIf(0, 1, 2)", "IIf('', 1, 2)",
                    "Switch(False, 1, True, 'b')", "Switch(False, 1)", "Switch(Null, 1, True, 2)", "Switch(True)", "Switch(True, 1, False)", "Switch()",
                    "Choose(0, 'a', 'b')", "Choose(3, 'a', 'b')", "Choose(Null, 'a', 'b')", "Choose(1.4, 'a', 'b')", "Choose(1.5, 'a', 'b')",
                    "Choose(2.5, 'a', 'b')", "Choose(0.5, 'a', 'b')", "Choose(-0.5, 'a', 'b')", "Choose('2', 'a', 'b')");
                Sel(p, n, "SELECT Id, IIf(Id = 1, 1, 'a') AS v FROM Den ORDER BY Id");
            }),
        Read("e-failure-phase", ["Branch E", "EXPR-STAGE"],
            "Asks at which public call a row-dependent failure appears: OpenRecordset, the first field read, MoveNext onto the failing row, or the Value read. SELECT Id, 1/[Denominator] over Den (2, 0, Null) as snapshot, dynaset and forward-only; zero-row queries whose projection would fail; and a snapshot whose failing CInt([TextValue]) column is never read while Id is traversed, then MoveLast and RecordCount.",
            (p, n) =>
            {
                foreach (int type in new[] { Dao.OpenSnapshot, Dao.OpenDynaset, Dao.OpenForwardOnly })
                {
                    Sel(p, n, "SELECT Id, 1 / [Denominator] AS q FROM Den ORDER BY Id", type);
                }
                Sel(p, n, "SELECT 1 / 0 AS q FROM Den WHERE Id = 999");
                Sel(p, n, "SELECT 1 / [Denominator] AS q FROM Den WHERE Id = 999");
                Sel(p, n, "SELECT Id, CInt([TextValue]) AS c FROM Den ORDER BY Id");
                foreach (int type in new[] { Dao.OpenSnapshot, Dao.OpenDynaset })
                {
                    string rs = n.Next("ur");
                    p.OpenRs(rs, "db", "SELECT Id, CInt([TextValue]) AS c FROM Den ORDER BY Id", type)
                        .GetBind(rs + "f", rs, "Fields").Item(rs + "id", rs + "f", A.Str("Id"))
                        .Until(rs, "EOF", 8, b => b.Get(rs + "id", "Value").Call(rs, "MoveNext"))
                        .Call(rs, "MoveLast").Get(rs, "RecordCount").Get(rs + "id", "Value")
                        .Release(rs + "id").Release(rs + "f").Call(rs, "Close").Release(rs);
                }
            }),

        // EXPR-BIND: source grammar, operand origin and stable execution inputs.
        Read("bind-names-and-literals", ["EXPR-BIND"],
            "Asks how names resolve when a column, a function and a parameter share a name (columns Date, Len and p of Nm; a declared parameter p over Nm), how bracketed and punctuated aliases, quoted delimiters in both quote styles and a SQL text with an embedded U+0000 are read, what an unknown name and an unknown function return, and how 32-deep and 128-deep nesting of parentheses and of Abs calls evaluate. Each statement is its own snapshot.",
            (p, n) =>
            {
                foreach (string sql in new[]
                {
                    "SELECT Date FROM Nm", "SELECT [Date] FROM Nm", "SELECT Nm.[Date] FROM Nm", "SELECT Len FROM Nm", "SELECT [Len] FROM Nm",
                    "SELECT Len('abc') FROM Nm", "SELECT [Len] + 1 FROM Nm", "SELECT p FROM Nm", "SELECT 1 AS [a b], 2 AS [a!b], 3 AS [a.b] FROM One",
                    "SELECT 'it''s' AS a, \"dq\"\"x\" AS b, 'a\"b' AS c, \"a'b\" AS d FROM One", "SELECT 'A\0B' AS v FROM One",
                    "SELECT 'A' & Chr(0) & 'B' AS v FROM One", "SELECT [NoSuchName] FROM One", "SELECT NoSuchFunc(1) FROM One",
                    $"SELECT {Nest("(", ")", 32)} FROM One", $"SELECT {Nest("(", ")", 128)} FROM One",
                    $"SELECT {Nest("Abs(", ")", 32)} FROM One", $"SELECT {Nest("Abs(", ")", 128)} FROM One",
                })
                {
                    Sel(p, n, sql);
                }
                string qd = QDef(p, n, "PARAMETERS p Long; SELECT p, Nm.p AS f FROM Nm");
                QRun(p, n, qd, ("p", A.I4(99)));
                QDone(p, qd);
            }),
        Read("bind-parameter-lifecycle", ["EXPR-BIND"],
            "Asks what an open recordset returns after its QueryDef parameter changes: read the row, change pl, read again, Requery(), read, Requery(qd), read, then open a second recordset. Then the QueryDef SQL is replaced with a Text-parameter statement and its Parameters are read again. A saved QueryDef is read before and after ALTER TABLE adds a column to its source. The fixture is a writable copy.",
            (p, n) =>
            {
                string qd = QDef(p, n, "PARAMETERS pl Long; SELECT pl * 2 AS v, ID FROM One");
                p.Item("pl1", qd + "p", A.Str("pl")).Put("pl1", "Value", A.I4(1)).CallBind("lrs", qd, "OpenRecordset", A.I4(Dao.OpenDynaset));
                CurrentRow(p, n, "lrs");
                p.Put("pl1", "Value", A.I4(2));
                CurrentRow(p, n, "lrs");
                p.Call("lrs", "Requery");
                CurrentRow(p, n, "lrs");
                p.Call("lrs", "Requery", A.Obj(qd));
                CurrentRow(p, n, "lrs");
                p.CallBind("lrs2", qd, "OpenRecordset", A.I4(Dao.OpenSnapshot));
                CurrentRow(p, n, "lrs2");
                p.Call("lrs2", "Close").Release("lrs2").Call("lrs", "Close").Release("lrs").Release("pl1").Release(qd + "p")
                    .Put(qd, "SQL", A.Str("PARAMETERS pl Text; SELECT pl & '!' AS v FROM One")).Get(qd, "SQL")
                    .GetBind("qp2", qd, "Parameters").Each("qp2", "qp2x", 8, b => b.Get("qp2x", "Name").Get("qp2x", "Type"))
                    .Item("pl2", "qp2", A.Str("pl")).Put("pl2", "Value", A.Str("x")).CallBind("lrs3", qd, "OpenRecordset", A.I4(Dao.OpenSnapshot));
                Rows(p, "lrs3", 4);
                p.Call("lrs3", "Close").Release("lrs3").Release("pl2").Release("qp2").Call(qd, "Close").Release(qd)
                    .CallBind("qs", "db", "CreateQueryDef", A.Str("QSaved"), A.Str("SELECT * FROM One"));
                Sel(p, n, "QSaved");
                p.Call("db", "Execute", A.Str("ALTER TABLE One ADD COLUMN X LONG"));
                Sel(p, n, "QSaved");
                p.Get("qs", "SQL").Release("qs");
            }, mutates: true),

        // EXPR-STAGE and EXPR-MUTATION: edit-time validation and defaults.
        Read("stage-validate-on-set", ["EXPR-STAGE", "EXPR-MUTATION"],
            "Asks when a field validation rule ([V] > 0) rejects a proposed value on a dynaset Edit. With ValidateOnSet True: Value put of -1, then Value and EditMode read, then a valid put and Update. With ValidateOnSet False: an invalid put, Value and EditMode, Update, Value and EditMode again, then CancelUpdate. Then which of two invalid fields (V and W, both ruled > 0) is reported, with the assignment order swapped. DBEngine.Errors is read after each step. The fixture is a writable copy.",
            (p, n) =>
            {
                Exec(p, "CREATE TABLE Vs (ID LONG CONSTRAINT PK_Vs PRIMARY KEY, V LONG, W LONG)", "INSERT INTO Vs (ID, V, W) VALUES (1, 5, 5)");
                p.GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh").Item("tdf", "tdfs", A.Str("Vs")).GetBind("tfl", "tdf", "Fields")
                    .Item("tv", "tfl", A.Str("V")).Put("tv", "ValidationRule", A.Str("[V] > 0")).Put("tv", "ValidationText", A.Str("V must be positive"))
                    .Item("tw", "tfl", A.Str("W")).Put("tw", "ValidationRule", A.Str("[W] > 0")).Put("tw", "ValidationText", A.Str("W must be positive"))
                    .Release("tv").Release("tw").Release("tfl").Release("tdf").Release("tdfs")
                    .OpenRs("vr", "db", "SELECT ID, V, W FROM Vs", Dao.OpenDynaset)
                    .GetBind("vrf", "vr", "Fields").Item("vv", "vrf", A.Str("V")).Item("vw", "vrf", A.Str("W"))
                    .Call("vr", "Edit").Put("vv", "ValidateOnSet", A.Bool(true)).Get("vv", "ValidateOnSet")
                    .Put("vv", "Value", A.I4(-1)).Get("vv", "Value").Get("vr", "EditMode").Errors()
                    .Put("vv", "Value", A.I4(7)).Call("vr", "Update").Errors()
                    .Call("vr", "Edit").Put("vv", "ValidateOnSet", A.Bool(false))
                    .Put("vv", "Value", A.I4(-1)).Get("vv", "Value").Get("vr", "EditMode")
                    .Call("vr", "Update").Errors().Get("vv", "Value").Get("vr", "EditMode")
                    .Call("vr", "CancelUpdate").Get("vv", "Value")
                    .Call("vr", "Edit").Put("vw", "Value", A.I4(-1)).Put("vv", "Value", A.I4(-1)).Call("vr", "Update").Errors().Call("vr", "CancelUpdate")
                    .Call("vr", "Edit").Put("vv", "Value", A.I4(-2)).Put("vw", "Value", A.I4(-2)).Call("vr", "Update").Errors().Call("vr", "CancelUpdate")
                    .Release("vv").Release("vw").Release("vrf").Call("vr", "Close").Release("vr");
                Sel(p, n, "SELECT ID, V, W FROM Vs");
            }, mutates: true),
        Read("mut-validation-rules", ["EXPR-MUTATION", "Branch D"],
            "Asks which field validation rules (Null, 0, 2, '', '1', 'q', True, False, 1/0, CVErr(5)) are accepted when set, and whether an AddNew/Update with V = 5 is then accepted. Then rule [V] > 0 with V = 5, -1 and unassigned, with Required False and True. Then table rule [V] < [R] with (1,2), (2,1) and (1,Null), and table rule Null. EditMode and DBEngine.Errors are read after each Update, and the stored rows at the end. The fixture is a writable copy.",
            (p, n) =>
            {
                p.Call("db", "Execute", A.Str("CREATE TABLE Vr (ID LONG CONSTRAINT PK_Vr PRIMARY KEY, V LONG, R LONG)"))
                    .GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh").Item("tvr", "tdfs", A.Str("Vr"))
                    .GetBind("tvf", "tvr", "Fields").Item("tvv", "tvf", A.Str("V"));
                int id = 0;
                foreach (string rule in new[] { "Null", "0", "2", "''", "'1'", "'q'", "True", "False", "1/0", "CVErr(5)" })
                {
                    FieldRule(p, rule);
                    AddRows(p, n, "Vr", ["ID", "V"], [[A.I4(++id), A.I4(5)]], observe: true);
                }
                FieldRule(p, "[V] > 0");
                AddRows(p, n, "Vr", ["ID", "V"], [[A.I4(++id), A.I4(5)], [A.I4(++id), A.I4(-1)]], observe: true);
                AddRows(p, n, "Vr", ["ID"], [[A.I4(++id)]], observe: true);
                p.Put("tvv", "Required", A.Bool(true)).Get("tvv", "Required");
                AddRows(p, n, "Vr", ["ID"], [[A.I4(++id)]], observe: true);
                AddRows(p, n, "Vr", ["ID", "V"], [[A.I4(++id), A.Null]], observe: true);
                p.Put("tvv", "Required", A.Bool(false));
                FieldRule(p, "");
                p.Put("tvr", "ValidationRule", A.Str("[V] < [R]")).Put("tvr", "ValidationText", A.Str("table rule")).Get("tvr", "ValidationRule");
                AddRows(p, n, "Vr", ["ID", "V", "R"], [[A.I4(++id), A.I4(1), A.I4(2)], [A.I4(++id), A.I4(2), A.I4(1)], [A.I4(++id), A.I4(1), A.Null]], observe: true);
                p.Put("tvr", "ValidationRule", A.Str("Null")).Get("tvr", "ValidationRule");
                AddRows(p, n, "Vr", ["ID", "V", "R"], [[A.I4(++id), A.I4(1), A.I4(2)]], observe: true);
                p.Release("tvv").Release("tvf").Release("tvr").Release("tdfs");
                Sel(p, n, "SELECT ID, V, R FROM Vr ORDER BY ID", maxRows: 32);
            }, mutates: true),
        Read("mut-default-values", ["EXPR-MUTATION", "EXPR-STAGE"],
            "Asks which DefaultValue expressions are accepted when set ('2+3' on Long, \"'x' & 'y'\" on Text, '1/0' on Double, '#2024-01-31# + 1' on Date, \"'q'\" on Long, 'Null' on Long). Then what an AddNew exposes in each field before any assignment, and what Update stores, both with only ID assigned and with A set to Null. Then SQL INSERT with the columns omitted versus A given as Null. The fixture is a writable copy.",
            (p, n) =>
            {
                p.Call("db", "Execute", A.Str("CREATE TABLE Df (ID LONG CONSTRAINT PK_Df PRIMARY KEY, A LONG, B TEXT(20), C DOUBLE, D DATETIME, E LONG, F LONG)"))
                    .GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh").Item("tdf", "tdfs", A.Str("Df")).GetBind("tfl", "tdf", "Fields");
                foreach ((string f, string e) in new[] { ("A", "2+3"), ("B", "'x' & 'y'"), ("C", "1/0"), ("D", "#2024-01-31# + 1"), ("E", "'q'"), ("F", "Null") })
                {
                    p.Item("dv" + f, "tfl", A.Str(f)).Put("dv" + f, "DefaultValue", A.Str(e)).Get("dv" + f, "DefaultValue").Release("dv" + f);
                }
                p.Release("tfl").Release("tdf").Release("tdfs")
                    .OpenRs("dr", "db", "Df", Dao.OpenTable).GetBind("drf", "dr", "Fields").Item("drid", "drf", A.Str("ID")).Item("dra", "drf", A.Str("A"))
                    .Call("dr", "AddNew");
                CurrentRow(p, n, "dr");
                p.Put("drid", "Value", A.I4(1)).Call("dr", "Update").Get("dr", "EditMode").Errors()
                    .Call("dr", "AddNew").Put("drid", "Value", A.I4(2)).Put("dra", "Value", A.Null).Get("dra", "Value")
                    .Call("dr", "Update").Get("dr", "EditMode").Errors()
                    .Release("drid").Release("dra").Release("drf").Call("dr", "Close").Release("dr");
                Exec(p, "INSERT INTO Df (ID) VALUES (3)", "INSERT INTO Df (ID, A) VALUES (4, Null)");
                Sel(p, n, "SELECT * FROM Df ORDER BY ID");
            }, mutates: true),

        // EXPR-INVENTORY: per-function availability and contracts.
        Read("inv-string-functions", ["EXPR-INVENTORY"],
            "Asks whether each string builtin is available in the DAO query context and what it returns at ordinary and boundary inputs ($ forms, byte forms, zero and out-of-range positions, empty search, Null, non-Latin-1 characters, a lone surrogate code), each call its own SELECT.",
            (p, n) => Scalars(p, n, "Left('abcd', 2)", "Right('abcd', 2)", "Mid('abcd', 2, 2)", "Mid('abcd', 0)", "Mid('abcd', 5)", "Left('abcd', -1)",
                "Left$('abcd', 2)", "Left$(Null, 2)", "Left(Null, 2)", "LeftB('abcd', 2)", "MidB('abcd', 2)", "LenB('abcd')", "Len('a' & Chr(0) & 'b')",
                "InStr('abcabc', 'c')", "InStrRev('abcabc', 'c')", "InStr(0, 'abc', 'a')", "InStr('abc', '')", "Replace('abcabc', 'b', 'xy')",
                "Replace('abc', '', 'x')", "StrReverse('abc')", "UCase('a\u00DF\u00E9')", "LCase('\u0130')", "Trim('  a  ')", "LTrim(Chr(9) & 'a')",
                "Space(3)", "String(3, 'xy')", "String(3, 65)", "Asc('')", "Asc('\u20AC')", "AscW('\u20AC')", "Chr(128)", "ChrW(8364)", "ChrW(55296)",
                "Chr(256)", "StrConv('abc def', 3)", "Format(1234.5, '0.00')", "Format(#2003-02-01#, 'yyyy-mm-dd')")),
        Read("inv-number-conversion-financial", ["EXPR-INVENTORY", "Branch A"],
            "Asks whether each numeric, conversion and financial builtin is available in the DAO query context and what it returns or raises at ordinary, half, range, zero-rate and non-converging inputs, each call its own SELECT.",
            (p, n) => Scalars(p, n, "Abs(-2147483648)", "Abs(CInt(-32768))", "Sgn(-0.5)", "Sgn(Null)", "Int(-0.5)", "Fix(-0.5)", "Round(0.5)",
                "Round(1.5)", "Round(2.675, 2)", "Round(-0.5)", "Round(1.25, 1)", "Round(2.5, -1)", "Sqr(-1)", "Sqr(2)", "Exp(710)", "Log(0)", "Log(1)",
                "Atn(1) * 4", "2 ^ 0.5", "(-8) ^ (1/3)", "0 ^ 0", "Val('1.5e2x')", "Val('&H10')", "Hex(255)", "Hex(-1)", "Oct(8)", "Str(1.5)",
                "CStr(1.5)", "CStr(True)", "CStr(Null)", "CBool('True')", "CBool('yes')", "CBool(2)", "CDbl('1e400')", "CSng(1e39)", "CVar(1)", "CDec(1)",
                "Pmt(0, 12, 1000)", "Pmt(0.01, 12, 1000)", "FV(0.01, 12, -100)", "PV(0.01, 12, -100)", "NPer(0.01, -100, 1000)", "NPer(0, 0, 1000)",
                "Rate(12, -100, 1000)", "Rate(1, 0, 0)", "IPmt(0.01, 1, 12, 1000)", "DDB(1000, 100, 5, 1)", "SLN(1000, 100, 5)", "SYD(1000, 100, 5, 1)")),
        Read("inv-date-and-info", ["EXPR-INVENTORY", "Branch C"],
            "Asks whether each date and information builtin is available in the DAO query context and what it returns at month-end, leap-day, week-setting, out-of-range, negative-fraction and Null inputs, plus the VarType/TypeName reported for literal forms, each call its own SELECT.",
            (p, n) => Scalars(p, n, "DateSerial(2024, 2, 30)", "DateSerial(2024, 13, 1)", "DateSerial(99, 1, 1)", "DateSerial(10000, 1, 1)",
                "TimeSerial(25, 0, 0)", "DateAdd('m', 1, #2024-01-31#)", "DateAdd('yyyy', 1, #2024-02-29#)", "DateAdd('q', -1, #2024-05-31#)",
                "DateAdd('d', 1, Null)", "DateAdd('x', 1, #2024-01-01#)", "DateDiff('d', #2024-01-01#, #2024-12-31#)",
                "DateDiff('ww', #2024-01-01#, #2024-12-31#)", "DateDiff('ww', #2024-01-01#, #2024-12-31#, 2)", "DateDiff('yyyy', #2024-12-31#, #2025-01-01#)",
                "DatePart('ww', #2021-01-01#)", "DatePart('ww', #2021-01-01#, 2, 2)", "DatePart('ww', #2021-01-01#, 2, 3)", "Weekday(#2024-01-01#)",
                "Weekday(#2024-01-01#, 2)", "Year(#2024-02-29#)", "Month(Null)", "Day(-0.25)", "Hour(-0.25)", "MonthName(2)", "WeekdayName(1)",
                "IsDate('2024-02-30')", "IsDate('2024-02-29')", "IsNumeric('1e3')", "IsNumeric('&H10')", "IsNumeric('')", "IsNumeric(Null)",
                "VarType(Null)", "VarType(1)", "VarType(32768)", "VarType(2147483648)", "VarType(1.5)", "VarType('a')", "VarType(#2024-01-01#)",
                "VarType(True)", "TypeName(1)", "TypeName(CDec(1))", "IsArray(1)", "IsObject(1)")),
        Read("host-routes-domain-eval", ["EXPR-DOMAIN", "EXPR-EVAL", "EXPR-INVENTORY"],
            "Asks whether the DAO query context binds the domain functions DLookup, DSum and DCount and the dynamic Eval (constant, nested, malformed, Null text and a row-dependent predicate), and what each returns or raises, each its own SELECT. This is availability only; transaction visibility and Access-hosted context are not asked here.",
            (p, n) =>
            {
                Scalars(p, n, "DLookup('ID', 'One')", "DLookup('ID', 'One', 'ID = 1')", "DSum('ID', 'One')", "DCount('*', 'One')",
                    "Eval('1+1')", "Eval('Eval(\"2*3\")')", "Eval('1+')", "Eval('Len(Null)')", "Eval(Null)");
                Sel(p, n, "SELECT ID FROM One WHERE Eval('ID = 1')");
            }),
        Read("runtime-rnd-now", ["EXPR-RUNTIME"],
            "Asks how often Rnd and Now are sampled: two Rnd() occurrences per row over Den (run twice), Rnd(Id), Rnd(-1)/Rnd()/Rnd(0) in one projection (run twice), a lone Rnd(-1) (run twice), and two Now() occurrences per row. The returned values and their tags are captured; repeats show the shape.",
            (p, n) =>
            {
                Sel(p, n, "SELECT Id, Rnd() AS r1, Rnd() AS r2 FROM Den ORDER BY Id");
                Sel(p, n, "SELECT Id, Rnd() AS r1, Rnd() AS r2 FROM Den ORDER BY Id");
                Sel(p, n, "SELECT Id, Rnd(Id) AS r FROM Den ORDER BY Id");
                Sel(p, n, "SELECT Rnd(-1) AS a, Rnd() AS b, Rnd(0) AS c FROM One");
                Sel(p, n, "SELECT Rnd(-1) AS a, Rnd() AS b, Rnd(0) AS c FROM One");
                Sel(p, n, "SELECT Rnd(-1) AS a FROM One");
                Sel(p, n, "SELECT Rnd(-1) AS a FROM One");
                Sel(p, n, "SELECT Id, Now() AS a, Now() AS b FROM Den ORDER BY Id");
            }),

        // Pattern and comparison profiles.
        new Probe(Card, "pat-01-wildcard-modes", ["R-EXPR-1", "PAT-01"],
            "Asks which wildcard grammar each route applies. Pat group p01 (Q, Q*, Q%, Q7, Q*7, Q7%, q) is matched against Q*, Q%, Q*%, Q? and Q_ with LIKE and with ALIKE, as projections and as a WHERE selection, through DAO on the fixture opened read-only and through ADODB.Connection with Provider Microsoft.ACE.OLEDB.12.0 (Mode adModeRead). The database ANSI-query option is not changed.",
            p =>
            {
                var n = new Names();
                p.Engine().Get("eng", "Version").OpenDb().Get("db", "Version");
                string[] pats = ["Q*", "Q%", "Q*%", "Q?", "Q_"];
                string[] sqls =
                [
                    $"SELECT Id, S, {Cols("LIKE", pats)} FROM Pat WHERE G = 'p01' ORDER BY Id",
                    $"SELECT Id, S, {Cols("ALIKE", pats)} FROM Pat WHERE G = 'p01' ORDER BY Id",
                    "SELECT Id FROM Pat WHERE G = 'p01' AND S LIKE 'Q%' ORDER BY Id",
                    "SELECT Id FROM Pat WHERE G = 'p01' AND S LIKE 'Q*' ORDER BY Id",
                ];
                foreach (string sql in sqls)
                {
                    Sel(p, n, sql);
                }
                AdoOpen(p);
                foreach (string sql in sqls)
                {
                    AdoSel(p, n, sql);
                }
                p.Call("cn", "Close").Release("cn").Finish("db");
            })
        { Fixtures = [("main", Main)] },
        Read("pat-02-bracket-classes", ["PAT-02"],
            "Asks how bracket, hyphen, closing-bracket, empty-class and negation-marker patterns ([[], ], [[]], [-a], [a-], [], [!], [^], [!a], []]) match the subjects [, ], [], -, a, !, ^, empty and x (Pat group p02), or whether they fail, with LIKE and with ALIKE. Each pattern is its own projection.",
            (p, n) =>
            {
                string[] pats = ["[[]", "]", "[[]]", "[-a]", "[a-]", "[]", "[!]", "[^]", "[!a]", "[]]"];
                Match(p, n, "p02", "LIKE", pats);
                Match(p, n, "p02", "ALIKE", pats);
            }),
        Read("pat-03-ranges", ["PAT-03"],
            "Asks which ranges are valid and what they match under the database (General) sort order: [C-A], [A-C], [a-C], [\u00E0-\u00E9], [a-\u00E6] and [!A-C] against A, B, C, a, b, c, \u00E0, \u00E9, \u00E6 and - (Pat group p03), each pattern its own LIKE projection.",
            (p, n) => Match(p, n, "p03", "LIKE", "[C-A]", "[A-C]", "[a-C]", "[\u00E0-\u00E9]", "[a-\u00E6]", "[!A-C]")),
        Read("pat-04-05-structural-diagnostics", ["PAT-04", "PAT-05"],
            "Asks when a malformed pattern is diagnosed relative to matching: q[, xq[, q*[ and q*[C-A] with LIKE, and q%[ and q%[C-A] with ALIKE, against x, qx, q, empty, yq, xqx and qabc (Pat group p04), each pattern its own projection. Then constant-folded forms 'x' LIKE 'q[', 'q' LIKE 'q[' and '' LIKE 'q[' as their own SELECTs.",
            (p, n) =>
            {
                Match(p, n, "p04", "LIKE", "q[", "xq[", "q*[", "q*[C-A]");
                Match(p, n, "p04", "ALIKE", "q%[", "q%[C-A]");
                Scalars(p, n, "'x' LIKE 'q['", "'q' LIKE 'q['", "'' LIKE 'q['");
            }),
        Read("pat-06-null-and-operand-order", ["PAT-06"],
            "Asks how LIKE treats Null subject or pattern combined with valid, malformed and nontext operands, numeric operands, and zero-length strings, each as its own SELECT; the stored Null N of Vals against a malformed pattern; and undeclared parameters [ps] LIKE [pp] bound to Empty, Null, zero-length and numeric values.",
            (p, n) =>
            {
                Scalars(p, n, "Null LIKE 'a'", "Null LIKE 'q['", "Null LIKE 5", "5 LIKE Null", "'a' LIKE Null", "123 LIKE '1*'", "123 LIKE 1",
                    "'' LIKE ''", "'' LIKE '*'", "'' LIKE '?'", "Null LIKE Null");
                Sel(p, n, "SELECT N LIKE 'q[' AS m FROM Vals");
                string qd = QDef(p, n, "SELECT [ps] LIKE [pp] AS m FROM One");
                QRun(p, n, qd, ("ps", A.Empty), ("pp", A.Str("*")));
                QRun(p, n, qd, ("ps", A.Null), ("pp", A.Str("q[")));
                QRun(p, n, qd, ("ps", A.Str("a")), ("pp", A.Empty));
                QRun(p, n, qd, ("ps", A.Str("")), ("pp", A.Str("")));
                QRun(p, n, qd, ("ps", A.I4(5)), ("pp", A.Str("5")));
                QDone(p, qd);
            }),
        Read("pat-07-08-expansion-and-classes", ["PAT-07", "PAT-08"],
            "Asks how \u00E6/ae/\u00C6/AE subjects (Pat group p07) match literal and wildcard patterns (ae, \u00E6, AE, a?, ?e, a*, *e, \u00E6*, ?, ??) and '=' against 'ae', in one LIKE projection. A second projection asks the class patterns [\u00E6a], [a\u00E6], a[ef], [!\u00E6a], [!a\u00E6], [\u00E6a]e, [\u00E6a]x and [\u00E6a]?, under the General sort order.",
            (p, n) =>
            {
                MatchAll(p, n, "p07", Cols("LIKE", "ae", "\u00E6", "AE", "a?", "?e", "a*", "*e", "\u00E6*", "?", "??") + ", S = 'ae' AS eq");
                MatchAll(p, n, "p07", Cols("LIKE", "[\u00E6a]", "[a\u00E6]", "a[ef]", "[!\u00E6a]", "[!a\u00E6]", "[\u00E6a]e", "[\u00E6a]x", "[\u00E6a]?"));
            }),
        new Probe(Card, "pat-09-contraction", ["R-EXPR-1", "PAT-09", "PAT-03", "PAT-15"],
            "Asks whether a sort locale with contractions changes pattern cardinality, ranges and ordering: the same subjects (c, ch, cz, d, h, c-h, cha, ci, Ch, CH) in the General-sort main fixture (Pat group p09) and in the Spanish-sort fixture (Sp). Patterns c?, ?, ??, c*, [c-d], [c-d]*, ch, c[h]; comparisons S = 'ch', S < 'd', S > 'cz' and StrComp(S, 'd', 2); ORDER BY S. Both databases are open read-only on one engine and CollatingOrder is read for each.",
            p =>
            {
                var n = new Names();
                p.Engine().Get("eng", "Version").OpenDb().Get("db", "CollatingOrder").OpenDb(bind: "dbes", role: "es").Get("dbes", "CollatingOrder");
                string cols = Cols("LIKE", "c?", "?", "??", "c*", "[c-d]", "[c-d]*", "ch", "c[h]") + ", S = 'ch' AS eq, S < 'd' AS ltd, S > 'cz' AS gtcz, StrComp(S, 'd', 2) AS sc";
                foreach ((string db, string src) in new[] { ("db", "Pat WHERE G = 'p09'"), ("dbes", "Sp") })
                {
                    Sel(p, n, $"SELECT Id, S, {cols} FROM {src} ORDER BY Id", db: db);
                    Sel(p, n, $"SELECT Id, S FROM {src} ORDER BY S, Id", db: db);
                }
                p.Finish("dbes", "db");
            })
        { Fixtures = [("main", Main), ("es", Spanish)] },
        Read("pat-10-13-character-boundaries", ["PAT-10", "PAT-13"],
            "Asks what one wildcard position consumes. Surrogate pairs, lone and reversed surrogates (Pat group p10) against ?, ??, ???, a?b, a??b and *, ALIKE _ and __, with Len and AscW. Digits and marks (ASCII 5, U+0661, U+FF11, '.', U+0301, a, a+U+0301; group p13) against #, ##, [0-9], [!0-9] and ?, ALIKE _, with Len. One projection per group and mode.",
            (p, n) =>
            {
                MatchAll(p, n, "p10", Cols("LIKE", "?", "??", "???", "a?b", "a??b", "*") + ", Len(S) AS len, AscW(S) AS w");
                MatchAll(p, n, "p10", Cols("ALIKE", "_", "__"));
                MatchAll(p, n, "p13", Cols("LIKE", "#", "##", "[0-9]", "[!0-9]", "?") + ", Len(S) AS len");
                MatchAll(p, n, "p13", Cols("ALIKE", "_"));
            }),
        Read("pat-11-16-counted-nul-and-reuse", ["PAT-11", "PAT-16", "EXPR-BIND"],
            "Asks whether embedded U+0000 is carried and compared as counted text. Subjects A, A+NUL, A+NUL+B, A+NUL+C (Pat group p11) through Len, '=', LIKE A*, A? and A??; a SQL literal containing NUL in a WHERE; and an undeclared pattern parameter [pp] alternated between A\\0B, A\\0C, A\\0B, A\\0* and A on one QueryDef. Then a dynaset is opened with pp = A\\0B, pp is changed to A\\0C, and the dynaset is requeried, to ask whether a stale pattern is reused.",
            (p, n) =>
            {
                MatchAll(p, n, "p11", "Len(S) AS len, S = 'A' AS eq, " + Cols("LIKE", "A*", "A?", "A??"));
                Sel(p, n, "SELECT Id FROM Pat WHERE G = 'p11' AND S = 'A\0B' ORDER BY Id");
                string qd = QDef(p, n, "SELECT Id, S LIKE [pp] AS m, S = [pp] AS e FROM Pat WHERE G = 'p11' ORDER BY Id");
                QRunEach(p, n, qd, "pp", A.Str("A\0B"), A.Str("A\0C"), A.Str("A\0B"), A.Str("A\0*"), A.Str("A"));
                p.Item("ppx", qd + "p", A.Str("pp")).Put("ppx", "Value", A.Str("A\0B")).CallBind("prs", qd, "OpenRecordset", A.I4(Dao.OpenDynaset));
                Rows(p, "prs", 8);
                p.Put("ppx", "Value", A.Str("A\0C")).Call("prs", "Requery").Call("prs", "Requery", A.Obj(qd));
                Rows(p, "prs", 8);
                p.Call("prs", "Close").Release("prs").Release("ppx");
                QDone(p, qd);
            }),
        Read("pat-12-14-17-literal-shape", ["PAT-12", "PAT-14", "PAT-17"],
            "Asks whether trailing spaces, NBSP and tab are significant to LIKE, '=', StrComp and Len (Pat group p12, plus a WHERE S = 'A' selection that may use index IX_S). Asks whether a literal pattern matches the whole subject or a substring (MN against group p14). Asks the outcomes, not the timing, of long wildcard patterns ending in a mismatch (group p17). One projection per group.",
            (p, n) =>
            {
                MatchAll(p, n, "p12", Cols("LIKE", "A", "A*", "A?") + ", S = 'A' AS eq, StrComp(S, 'A') AS sc, StrComp(S, 'A', 0) AS sb, Len(S) AS len");
                Sel(p, n, "SELECT Id FROM Pat WHERE S = 'A' ORDER BY Id");
                MatchAll(p, n, "p14", Cols("LIKE", "MN", "*MN*", "MN*") + ", S = 'MN' AS eq");
                MatchAll(p, n, "p17", Cols("LIKE", "*a*a*a*a*a*a*b", "*a*a*a*a*a*a*a", new string('?', 20), string.Concat(Enumerable.Repeat("[a]", 20))));
            }),
        Read("pat-15-strcomp-selector", ["PAT-15"],
            "Asks how StrComp binds its comparison selector: omitted, 0, 1, 2, -1, 3, Null, 'x', 0.5, 1.5 and an erroring 1/0, plus Null operands, a numeric operand and the B/a order under selectors 0, 1 and 2. Each call is its own SELECT.",
            (p, n) => Scalars(p, n, "StrComp('a', 'A')", "StrComp('a', 'A', 0)", "StrComp('a', 'A', 1)", "StrComp('a', 'A', 2)", "StrComp('a', 'A', -1)",
                "StrComp('a', 'A', 3)", "StrComp('a', 'A', Null)", "StrComp('a', 'A', 'x')", "StrComp('a', 'A', 0.5)", "StrComp('a', 'A', 1.5)",
                "StrComp('a', 'A', 1/0)", "StrComp(Null, 'a')", "StrComp('a', Null)", "StrComp(1, '1')", "StrComp('B', 'a', 0)", "StrComp('B', 'a', 1)",
                "StrComp('B', 'a', 2)")),
        Read("pat-18-composition-and-folding", ["PAT-18"],
            "Asks how decomposed and precomposed e-acute, sharp s and ss, the dotted and dotless I forms, and full- and half-width kana (Pat group p18) relate under '=', LIKE, StrComp text mode, UCase, LCase and Len, in one projection. A second query asks their ORDER BY S sequence under the General sort order.",
            (p, n) =>
            {
                MatchAll(p, n, "p18", "S = '\u00E9' AS e1, S = 'ss' AS e2, S = 'i' AS e3, S = 'I' AS e4, S = '\uFF71' AS e5, S = '\u30A2' AS e6, "
                    + Cols("LIKE", "\u00E9", "e?", "?") + ", StrComp(S, '\u00E9', 1) AS c1, StrComp(S, 'ss', 1) AS c2, UCase(S) AS u, LCase(S) AS l, Len(S) AS len");
                Sel(p, n, "SELECT Id, S FROM Pat WHERE G = 'p18' ORDER BY S, Id");
            }),
    ];

    // Probe factories --------------------------------------------------------------------------------

    private static Probe Read(string name, string[] refs, string what, Action<P, Names> body, bool mutates = false) =>
        new(Card, name, ["R-EXPR-1", .. refs],
            what + $" Fixture {Main} is opened {(mutates ? "on a writable fresh copy" : "read-only")} through DAO.DBEngine.120; engine version and loaded modules are recorded; each recordset reads Name, Type and Value of every field.",
            p =>
            {
                var n = new Names();
                p.Engine().Get("eng", "Version").OpenDb(readOnly: !mutates).Get("db", "Version").Get("db", "CollatingOrder").Files("after-open");
                body(p, n);
                p.Finish("db");
            })
        { Fixtures = [("main", Main)] };

    private static Probe Creation(string fixture, string locale, string what, Action<P, Names> body) =>
        new(Card, "create-" + fixture[(Card.Length + 1)..], ["R-EXPR-1", "RQ-SEM-01", "RQ-VAL-02", "RQ-SQL-07", "RQ-EXPR-01"],
            $"R-EXPR-1 fixture {fixture}: {what}. Created in ACCDB by DBEngine.CreateDatabase(path, {locale}, dbVersion120); typed values are stored through table-type AddNew/Value/Update; the closed file is kept.",
            p =>
            {
                var n = new Names();
                p.Modules("before-create").Create().Modules("after-create").Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out(fixture + ".accdb"), A.Str(locale), A.I4(Dao.Version120))
                    .Get("db", "Version").Get("db", "CollatingOrder");
                body(p, n);
                p.Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close");
            })
        { CreatesFixture = true };

    private static void MainFixture(P p, Names n)
    {
        Exec(p,
            "CREATE TABLE One (ID LONG CONSTRAINT PK_One PRIMARY KEY)",
            "INSERT INTO One (ID) VALUES (1)",
            "CREATE TABLE Vals (ID LONG CONSTRAINT PK_Vals PRIMARY KEY, B BYTE, I SHORT, L LONG, S SINGLE, D DOUBLE, C CURRENCY, DT DATETIME, Y YESNO, T TEXT(50), Q TEXT(50), N LONG)",
            "CREATE TABLE Big (ID LONG CONSTRAINT PK_Big PRIMARY KEY, LL BIGINT)",
            "CREATE TABLE Den (Id LONG CONSTRAINT PK_Den PRIMARY KEY, Denominator LONG, TextValue TEXT(20))",
            "INSERT INTO Den (Id, Denominator, TextValue) VALUES (1, 2, '12')",
            "INSERT INTO Den (Id, Denominator, TextValue) VALUES (2, 0, 'abc')",
            "INSERT INTO Den (Id, Denominator, TextValue) VALUES (3, Null, Null)",
            "CREATE TABLE Dts (ID LONG CONSTRAINT PK_Dts PRIMARY KEY, D DATETIME)",
            "CREATE INDEX IX_D ON Dts (D)",
            "CREATE TABLE Pat (Id LONG CONSTRAINT PK_Pat PRIMARY KEY, G TEXT(10), S TEXT(50))",
            "CREATE INDEX IX_S ON Pat (S)",
            "CREATE TABLE Nm (ID LONG CONSTRAINT PK_Nm PRIMARY KEY, [Date] TEXT(20), [Len] LONG, p LONG)",
            "INSERT INTO Nm (ID, [Date], [Len], p) VALUES (1, 'field-date', 42, 7)");
        p.AddDecimalField("Vals", "M");
        AddRows(p, n, "Vals", ["ID", "B", "I", "L", "S", "D", "C", "M", "DT", "Y", "T", "Q", "N"],
            [[A.I4(1), A.UI1(255), A.I2(short.MaxValue), A.I4(int.MaxValue), A.R4(1.5f), A.R8(2.5), A.Cy(12.3456m), A.Str("-1844674407370955.1621"),
                A.Date(45000.5), A.Bool(true), A.Str("7"), A.Str("q"), A.Null]]);
        AddRows(p, n, "Big", ["ID", "LL"], [[A.I4(1), A.Str("9223372036854775807")], [A.I4(2), A.I4(-1)]]);
        AddRows(p, n, "Dts", ["ID", "D"], DtsValues.Select((d, i) => new[] { A.I4(i + 1), A.Date(d) }));
        p.GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh").Item("tpat", "tdfs", A.Str("Pat")).GetBind("tpf", "tpat", "Fields")
            .Item("tps", "tpf", A.Str("S")).Put("tps", "AllowZeroLength", A.Bool(true)).Get("tps", "AllowZeroLength")
            .Release("tps").Release("tpf").Release("tpat").Release("tdfs");
        AddRows(p, n, "Pat", ["Id", "G", "S"], PatRows());
    }

    private static IEnumerable<Arg[]> PatRows()
    {
        int id = 1;
        foreach ((string g, string[] subjects) in PatGroups)
        {
            foreach (string s in subjects)
            {
                yield return [A.I4(id++), A.Str(g), A.Str(s)];
            }
        }
    }

    // Program helpers ---------------------------------------------------------------------------------

    private static void Exec(P p, params string[] statements)
    {
        foreach (string sql in statements)
        {
            p.Call("db", "Execute", A.Str(sql));
        }
    }

    /// <summary>Table-type AddNew/Value/Update per row; with <paramref name="observe"/>, EditMode and Errors after each Update.</summary>
    private static void AddRows(P p, Names n, string table, string[] cols, IEnumerable<Arg[]> rows, bool observe = false)
    {
        string rs = n.Next("add");
        p.OpenRs(rs, "db", table, Dao.OpenTable).GetBind(rs + "f", rs, "Fields");
        for (int i = 0; i < cols.Length; i++)
        {
            p.Item($"{rs}c{i}", rs + "f", A.Str(cols[i]));
        }
        foreach (Arg[] row in rows)
        {
            p.Call(rs, "AddNew");
            for (int i = 0; i < row.Length; i++)
            {
                p.Put($"{rs}c{i}", "Value", row[i]);
            }
            p.Call(rs, "Update");
            if (observe)
            {
                p.Get(rs, "EditMode").Errors();
            }
        }
        for (int i = 0; i < cols.Length; i++)
        {
            p.Release($"{rs}c{i}");
        }
        p.Release(rs + "f").Call(rs, "Close").Release(rs);
    }

    private static void FieldRule(P p, string rule) =>
        p.Put("tvv", "ValidationRule", A.Str(rule)).Put("tvv", "ValidationText", A.Str("field rule " + rule)).Get("tvv", "ValidationRule");

    /// <summary>Every row to EOF: Name, Type and Value of each field.</summary>
    private static void Rows(P p, string rs, int maxRows)
    {
        string fl = rs + "f", f = rs + "x";
        p.Until(rs, "EOF", maxRows, b => b.GetBind(fl, rs, "Fields")
            .Each(fl, f, 64, x => x.Get(f, "Name").Get(f, "Type").Get(f, "Value"))
            .Release(fl).Call(rs, "MoveNext"));
    }

    /// <summary>Name, Type and Value of each field of the current record (or edit buffer).</summary>
    private static void CurrentRow(P p, Names n, string rs)
    {
        string fl = n.Next("cur"), f = fl + "x";
        p.GetBind(fl, rs, "Fields").Each(fl, f, 64, x => x.Get(f, "Name").Get(f, "Type").Get(f, "Value")).Release(fl);
    }

    private static void Sel(P p, Names n, string sql, int type = Dao.OpenSnapshot, string db = "db", int maxRows = 16)
    {
        string rs = n.Next("rs");
        p.OpenRs(rs, db, sql, type);
        Rows(p, rs, maxRows);
        p.Call(rs, "Close").Release(rs);
    }

    /// <summary>Each expression in its own snapshot <c>SELECT expr FROM One</c>.</summary>
    private static void Scalars(P p, Names n, params string[] exprs)
    {
        foreach (string e in exprs)
        {
            Sel(p, n, $"SELECT {e} FROM One");
        }
    }

    /// <summary>A temporary QueryDef; its Parameters (Name, Type) are read and kept bound as <c>qd + "p"</c>.</summary>
    private static string QDef(P p, Names n, string sql)
    {
        string qd = n.Next("qd");
        p.CallBind(qd, "db", "CreateQueryDef", A.Str(""), A.Str(sql)).Get(qd, "SQL")
            .GetBind(qd + "p", qd, "Parameters")
            .Each(qd + "p", qd + "x", 16, b => b.Get(qd + "x", "Name").Get(qd + "x", "Type"));
        return qd;
    }

    /// <summary>Set the given parameters (reading each Value back), then open a snapshot and read it.</summary>
    private static void QRun(P p, Names n, string qd, params (string Name, Arg Value)[] values)
    {
        foreach ((string name, Arg value) in values)
        {
            string prm = n.Next("prm");
            p.Item(prm, qd + "p", A.Str(name)).Put(prm, "Value", value).Get(prm, "Value").Release(prm);
        }
        string rs = n.Next("qrs");
        p.CallBind(rs, qd, "OpenRecordset", A.I4(Dao.OpenSnapshot));
        Rows(p, rs, 16);
        p.Call(rs, "Close").Release(rs);
    }

    private static void QRunEach(P p, Names n, string qd, string name, params Arg[] values)
    {
        foreach (Arg v in values)
        {
            QRun(p, n, qd, (name, v));
        }
    }

    private static void QDone(P p, string qd) => p.Release(qd + "p").Call(qd, "Close").Release(qd);

    /// <summary>ADO connection to the main input through the ACE OLE DB provider, read-only (adModeRead).</summary>
    private static void AdoOpen(P p) =>
        p.Create("ADODB.Connection", "cn").Get("cn", "Version")
            .Put("cn", "Provider", A.Str(AceProvider)).Put("cn", "Mode", A.I4(1))
            .GetBind("cnp", "cn", "Properties").Item("cnds", "cnp", A.Str("Data Source")).Put("cnds", "Value", A.In())
            .Release("cnds").Release("cnp")
            .Call("cn", "Open").Get("cn", "ConnectionString").Modules("after-ado-open");

    private static void AdoSel(P p, Names n, string sql)
    {
        string rs = n.Next("ars");
        p.CallBind(rs, "cn", "Execute", A.Str(sql));
        Rows(p, rs, 32);
        p.Call(rs, "Close").Release(rs);
    }

    private static string Cols(string op, params string[] patterns) =>
        string.Join(", ", patterns.Select((pat, i) => $"S {op} '{pat}' AS m{i}"));

    private static void MatchAll(P p, Names n, string group, string cols) =>
        Sel(p, n, $"SELECT Id, S, {cols} FROM Pat WHERE G = '{group}' ORDER BY Id");

    /// <summary>Each pattern in its own projection, so a malformed pattern does not hide the others.</summary>
    private static void Match(P p, Names n, string group, string op, params string[] patterns)
    {
        foreach (string pat in patterns)
        {
            Sel(p, n, $"SELECT Id, S, S {op} '{pat}' AS m FROM Pat WHERE G = '{group}' ORDER BY Id");
        }
    }

    private static string LogicGrid()
    {
        string[] v = ["True", "False", "Null"];
        var cols = new List<string>();
        foreach (string op in new[] { "And", "Or", "Xor", "Eqv", "Imp" })
        {
            foreach (string a in v)
            {
                foreach (string b in v)
                {
                    cols.Add($"({a} {op} {b}) AS [{a}_{op}_{b}]");
                }
            }
        }
        cols.AddRange(v.Select(a => $"(Not {a}) AS [Not_{a}]"));
        return string.Join(", ", cols);
    }

    private static string Nest(string open, string close, int depth) =>
        string.Concat(Enumerable.Repeat(open, depth)) + "1" + string.Concat(Enumerable.Repeat(close, depth));
}
