namespace CalcDb.Probes.Cards;

/// <summary>
/// Deferred branches of R-EXPR-1, R-SQL-1, R-SQL-2, R-SQL-3 and R-STRING-1 that a DAO-only, single-host, no-UI
/// program can answer (docs/work/deferred-branches.md, waves 1 to 4). Every probe asks what the installed ACE does and
/// asserts nothing. It adds questions that <see cref="RExpr1"/>, <see cref="RExpr1B"/>, <see cref="RSql1"/>,
/// <see cref="RSql13B"/>, <see cref="RSql23"/>, <see cref="RWire"/> and <see cref="RStrAdoAct"/> do not ask:
/// functions and conversions not yet named (CLngLng, CLngPtr, InStrB, RightB, PPmt, the domain aggregates over a
/// table), literal and operator spellings, non-Jet SQL names and clauses with their error identity, statement
/// framing, join and identifier syntax, INSERT, UPDATE and DELETE forms, QueryDef creation texts and names, a DDL
/// type-name matrix, column and constraint clauses, counter seeds, referential actions, reserved and limit-sized
/// names, ALTER and DROP forms, crosstab syntax errors, undeclared host-style parameter names, and stored,
/// concatenated, parameterised and SQL-text string edge values (embedded U+0000, odd-byte BSTRs, lone surrogates,
/// astral pairs, compatibility characters) through SQL. No call can open a dialog: every database is a named
/// fixture copy, every statement goes through Database.OpenRecordset or Database.Execute, and no argument names a
/// DSN, an ODBC source or an empty path. Nothing here starts a process, reads the registry or writes outside its
/// own copy.
/// </summary>
public sealed class RDefExprSql : ICard
{
    private const string Card = "r-def-expr-sql";
    private const string Main = Card + "-main";
    private const string Mdb = Card + "-mdb";

    /// <summary>Unique binder names within one probe program.</summary>
    private sealed class Names
    {
        private int next;

        public string Next(string prefix) => prefix + next++;
    }

    /// <summary>Rows of Tx: the same value goes into the Text(255) column T and the Memo column M. ID 25 is Null.</summary>
    private static readonly (int Id, Arg Value)[] EdgeRows =
    [
        (1, A.Str("abc")), (2, A.Str("a\0b")), (3, A.Str("\0")), (4, A.BstrBytes(0x41)), (5, A.BstrBytes(0x41, 0x00, 0x42)),
        (6, A.BstrUnits("\uD800")), (7, A.BstrUnits("\uDC00")), (8, A.Str("\U0001D11E")), (9, A.Str("\uFEFFbom")), (10, A.Str("\uFFFE")),
        (11, A.Str("a ")), (12, A.Str("a")), (13, A.Str("A")), (14, A.Str("\u00DF")), (15, A.Str("ss")), (16, A.Str("e\u0301")),
        (17, A.Str("\u00E9")), (18, A.Str("\u00A0")), (19, A.Str(" ")), (20, A.Str("\u200B")), (21, A.Str("\u00AD")), (22, A.Str("\uFF21")),
        (23, A.Str("a\r\nb")), (24, A.Str("")), (25, A.Null), (26, A.Str("\u00C5")), (27, A.Str("\u212B")), (28, A.Str("\u0130")),
        (29, A.Str("i")), (30, A.Str("I")), (31, A.Str("\u0131")), (32, A.Str("\u30A2")), (33, A.Str("\u3042")), (34, A.BstrUnits("\uDC00\uD800")),
        (35, A.Str("a\0")), (36, A.Str("\0a")), (37, A.Str("a\0\0b")), (38, A.Str("A\u00E9z")),
    ];

    public IEnumerable<Probe> Probes()
    {
        yield return Creation(Main, Main + ".accdb", Dao.Version120, "ACCDB created by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120)");
        yield return Creation(Mdb, Mdb + ".mdb", Dao.Version40, "MDB (Jet 4 format) created by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion40)");

        // R-EXPR-1: functions and conversions not yet asked --------------------------------------------------------------
        yield return Read("expr-clnglng-clngptr", ["R-EXPR-1", "EXPR-INVENTORY", "Branch B"],
            "Asks what CLngLng and CLngPtr (the 64-bit conversion functions) return or raise, and the inner VarType and TypeName of the result: for integer, fractional (half-way), text, Boolean, Date, Currency, Decimal, empty and Null operands, at and one past the signed 64-bit limits (as an exact text and as a numeric literal), and as an operand of +, *, / , \\ and Mod. Each call is its own snapshot SELECT expr, VarType(expr), TypeName(expr) FROM One.",
            (p, n) => Typed(p, n, ["CLngLng(1)", "CLngLng(2.5)", "CLngLng(3.5)", "CLngLng(-2.5)", "CLngLng('42')", "CLngLng(' 42 ')", "CLngLng('9223372036854775807')",
                "CLngLng('9223372036854775808')", "CLngLng('-9223372036854775808')", "CLngLng('-9223372036854775809')", "CLngLng(9223372036854775807)",
                "CLngLng(9223372036854775808)", "CLngLng(-9223372036854775808)", "CLngLng(1E18)", "CLngLng(1E19)", "CLngLng(9.2233720368547758E18)",
                "CLngLng(True)", "CLngLng(Null)", "CLngLng('')", "CLngLng('x')", "CLngLng(#2024-03-05#)", "CLngLng(CCur(2.5))", "CLngLng(CDec(2.5))",
                "CLngLng(CDec('79228162514264337593543950335'))", "CLngLng('&H7FFFFFFFFFFFFFFF')", "CLngLng('&HFFFFFFFFFFFFFFFF')", "CLngLng(CByte(255))",
                "CLngPtr(1)", "CLngPtr(2.5)", "CLngPtr('5')", "CLngPtr(2147483648)", "CLngPtr(9223372036854775807)", "CLngPtr(Null)",
                "CLngLng(2147483647) + 1", "CLngLng(9223372036854775807) + 1", "CLngLng(9223372036854775807) - 1", "CLngLng(3) * CLngLng(4)",
                "CLngLng(4611686018427387904) * 2", "CLngLng(3) / 2", "CLngLng(3) \\ 2", "CLngLng(7) Mod 4", "CLngLng(1) + 1.5", "CLngLng(1) + CCur(1)",
                "-CLngLng(5)", "Abs(CLngLng(-5))", "Sgn(CLngLng(-5))", "CLngLng(5) = 5.0", "CStr(CLngLng(9223372036854775807))", "Hex(CLngLng(255))",
                "Hex(CLngLng(-1))", "Oct(CLngLng(8))", "Format(CLngLng(9223372036854775807), '0')", "CDbl(CLngLng(9223372036854775807))", "CLng(CLngLng(2147483648))",
                "Str(CLngLng(7))", "Val('9223372036854775807')", "Val('9223372036854775808')"], true));
        yield return Read("expr-clnglng-mdb", ["R-EXPR-1", "EXPR-INVENTORY", "Branch B", "R-FMT-1"],
            "Asks, on a Jet 4 (MDB) file, whether the expression service answers CLngLng and CLngPtr and what the 64-bit arithmetic returns: the same calls as the first rows of expr-clnglng-clngptr, so that ACCDB and MDB can be compared. Each call is its own snapshot SELECT expr, VarType(expr), TypeName(expr) FROM One.",
            (p, n) => Typed(p, n, ["CLngLng(1)", "CLngLng(2.5)", "CLngLng('9223372036854775807')", "CLngLng(9223372036854775807)", "CLngLng(1E19)", "CLngLng(Null)",
                "CLngPtr(1)", "CLngPtr(2147483648)", "CLngLng(2147483647) + 1", "CLngLng(9223372036854775807) + 1", "CLngLng(3) \\ 2", "CStr(CLngLng(9223372036854775807))"], true),
            fixture: Mdb);
        yield return Read("expr-bigint-stored-arith", ["R-EXPR-1", "Branch B", "R-FMT-1"],
            "Asks how a stored BIGINT column (ACCDB only; table Bg created by DDL) takes values and what arithmetic over it returns. Inserted through SQL: the largest and smallest signed 64-bit literals, CLngLng(5), the text '9223372036854775807', 2.5, one past the maximum, 1E19, Null, the text 'x', and 2^62 and 2^62 + 1. Then, for the rows with ID 1, 2, 3 and 10 separately, B + 1, B - 1, B * 2, B / 2, B \\ 2, B Mod 7, -B, Abs(B), B + 1.5 and B * B, each with the inner VarType, as its own snapshot.",
            (p, n) =>
            {
                Exs(p, BigSetup);
                Sel(p, n, "SELECT ID, B, VarType(B) AS vt, TypeName(B) AS tn FROM Bg ORDER BY ID", true);
                foreach (int id in new[] { 1, 2, 3, 10 })
                {
                    foreach (string e in new[] { "B + 1", "B - 1", "B * 2", "B / 2", "B \\ 2", "B Mod 7", "-B", "Abs(B)", "B + 1.5", "B * B" })
                    {
                        Sel(p, n, $"SELECT {e} AS v, VarType({e}) AS vt FROM Bg WHERE ID = {id}");
                    }
                }
            }, writable: true);
        yield return Read("expr-bigint-stored-compare-convert", ["R-EXPR-1", "Branch B", "R-FMT-1"],
            "Asks, over the same Bg rows, how a BIGINT column compares and converts: B = the maximum literal, B > the maximum minus one, B = 2^62 + 1 as a literal, as 2^62 + 1 computed, and as a Double; ORDER BY B and B DESC; Min, Max, Count, Sum and Avg; GROUP BY B; and CStr, Str, Format, Hex, CDbl, CDec, CCur, CLng and CInt of B. Each is its own snapshot; the Field type of each result column is read.",
            (p, n) =>
            {
                Exs(p, BigSetup);
                foreach (string w in new[] { "B = 9223372036854775807", "B > 9223372036854775806", "B = 4611686018427387905", "B = 4611686018427387904 + 1",
                    "B = CDbl(4611686018427387904)", "B = CDbl(4611686018427387905)", "B = '9223372036854775807'", "B = 2.5", "B = 3", "B Is Null", "B In (1, 5, 3)", "B Between 0 And 10" })
                {
                    Sel(p, n, $"SELECT ID FROM Bg WHERE {w} ORDER BY ID");
                }
                foreach (string q in new[] { "SELECT ID, B FROM Bg ORDER BY B, ID", "SELECT ID, B FROM Bg ORDER BY B DESC, ID", "SELECT Min(B) AS a, Max(B) AS b, Count(B) AS c FROM Bg",
                    "SELECT Sum(B) AS s FROM Bg", "SELECT Avg(B) AS s FROM Bg", "SELECT Sum(B) AS s FROM Bg WHERE ID In (3, 5)", "SELECT B, Count(*) AS c FROM Bg GROUP BY B ORDER BY B" })
                {
                    Sel(p, n, q, true);
                }
                foreach (string e in new[] { "CStr(B)", "Str(B)", "Format(B, '0')", "Hex(B)", "CDbl(B)", "CDec(B)", "CCur(B)", "CLng(B)", "CInt(B)", "CByte(B)", "CBool(B)", "CVar(B)", "B & ''" })
                {
                    Sel(p, n, $"SELECT ID, {e} AS v FROM Bg WHERE ID In (1, 3)", true);
                }
            }, writable: true);
        yield return Read("expr-bytes-instrb-rightb", ["R-EXPR-1", "EXPR-INVENTORY", "Branch B"],
            "Asks what the byte-oriented InStrB and RightB return or raise (they are not asked elsewhere): over literal operands (start positions, compare modes, an empty operand, Null, a number, a non-Latin character, an astral pair, an embedded U+0000) and over the stored values of Tx (InStrB(T, 'b'), InStrB(T, Chr(0)), RightB(T, 1), RightB(T, 2), with LenB(T) beside). Each literal call is its own snapshot; the stored-value forms are one snapshot each, ordered by ID.",
            (p, n) =>
            {
                Scalars(p, n, ["InStrB('abc', 'b')", "InStrB(2, 'abc', 'b')", "InStrB(3, 'abc', 'b')", "InStrB(1, 'abc', 'B', 1)", "InStrB(1, 'abc', 'B', 0)", "InStrB('abc', '')",
                    "InStrB('', 'a')", "InStrB('abc', Null)", "InStrB(Null, 'a')", "InStrB('a\u00E4b', 'b')", "InStrB('a\u00E4b', '\u00E4')", "InStrB('\uD83D\uDE00b', 'b')",
                    "InStrB('a' & Chr(0) & 'b', 'b')", "InStrB(12345, 34)", "InStrB(0, 'abc', 'a')", "InStrB(-1, 'abc', 'a')", "InStrB(100, 'abc', 'a')",
                    "RightB('abc', 2)", "RightB('abc', 0)", "RightB('abc', -1)", "RightB('abc', 100)", "RightB('a\u00E4', 2)", "RightB('a\u00E4', 1)", "RightB('\uD83D\uDE00', 2)",
                    "RightB(Null, 1)", "RightB(1234, 2)", "RightB('', 1)", "RightB('abc', 1.5)", "RightB('abc', 'x')", "LeftB('a\u00E4', 3)", "LeftB('abc', 1.5)", "MidB('a\u00E4b', 2, 2)"], true);
                foreach (string q in new[] { "SELECT ID, InStrB(T, 'b') AS v, LenB(T) AS lb FROM Tx WHERE T Is Not Null ORDER BY ID", "SELECT ID, InStrB(T, Chr(0)) AS v FROM Tx WHERE T Is Not Null ORDER BY ID",
                    "SELECT ID, RightB(T, 1) AS v FROM Tx WHERE T Is Not Null ORDER BY ID", "SELECT ID, RightB(T, 2) AS v FROM Tx WHERE T Is Not Null ORDER BY ID",
                    "SELECT ID, LeftB(T, 1) AS v FROM Tx WHERE T Is Not Null ORDER BY ID", "SELECT ID, MidB(T, 2) AS v FROM Tx WHERE T Is Not Null ORDER BY ID" })
                {
                    Sel(p, n, q, true, maxRows: 64);
                }
            });
        yield return Read("expr-financial-ppmt-ipmt-pmt", ["R-EXPR-1", "EXPR-INVENTORY", "Branch B"],
            "Asks what PPmt returns or raises (it is the one financial function not yet asked) and how it relates to IPmt and Pmt: a 12-period loan at 5 % per year, periods 1, 12, 0 and 13, zero periods, a zero rate, a negative rate, a future value, the payment-due flag 0, 1, 2 and True, zero and negative principal, Null, text and fractional operands; then PPmt + IPmt beside Pmt for the same arguments. Each call is its own snapshot SELECT expr, VarType(expr) FROM One.",
            (p, n) =>
            {
                Typed(p, n, ["PPmt(0.05/12, 1, 12, 10000)", "PPmt(0.05/12, 12, 12, 10000)", "PPmt(0.05/12, 0, 12, 10000)", "PPmt(0.05/12, 13, 12, 10000)", "PPmt(0.05/12, 1, 0, 10000)",
                    "PPmt(0, 1, 12, 10000)", "PPmt(-0.01, 1, 12, 10000)", "PPmt(-1, 1, 12, 10000)", "PPmt(0.05/12, 1, 12, 10000, 500)", "PPmt(0.05/12, 1, 12, 10000, 0, 0)",
                    "PPmt(0.05/12, 1, 12, 10000, 0, 1)", "PPmt(0.05/12, 1, 12, 10000, 0, 2)", "PPmt(0.05/12, 1, 12, 10000, 0, True)", "PPmt(0.05/12, 1, 12, 0)", "PPmt(0.05/12, 1, 12, -10000)",
                    "PPmt(Null, 1, 12, 10000)", "PPmt(0.05/12, Null, 12, 10000)", "PPmt(0.05/12, 1, 12, Null)", "PPmt(0.05/12, 1.5, 12, 10000)", "PPmt(0.05/12, 1, 12.5, 10000)",
                    "PPmt('0.05', '1', '12', '10000')", "PPmt('x', 1, 12, 10000)", "PPmt(0.05/12, 1, 12)", "PPmt(0.05/12, 1, 12, 10000, 0, 1, 1)", "PPmt(0.05/12, 1, -12, 10000)",
                    "PPmt(1E-300, 1, 12, 10000)", "PPmt(0.05/12, 1, 1, 10000)", "IPmt(0.05/12, 1, 12, 10000)", "IPmt(0.05/12, 0, 12, 10000)", "Pmt(0.05/12, 12, 10000)",
                    "PPmt(0.05/12, 3, 12, 10000) + IPmt(0.05/12, 3, 12, 10000)", "Pmt(0.05/12, 12, 10000)", "PPmt(0.05/12, 3, 12, 10000, 0, 1) + IPmt(0.05/12, 3, 12, 10000, 0, 1)",
                    "Pmt(0.05/12, 12, 10000, 0, 1)", "PPmt(0.05/12, 3, 12, CCur(10000))", "PPmt(0.05/12, 3, 12, CDec(10000))"], true);
            });
        yield return Read("expr-domain-aggregate-values", ["R-EXPR-1", "EXPR-DOMAIN", "EXPR-INVENTORY"],
            "Asks what the domain aggregates return over table Dm (8 rows; G text with a Null, V Double with a Null, N Long with a Null, S text with a Null, an empty string, a quote and a padded value, DT date) with and without a criteria string: DCount, DSum, DAvg, DMin, DMax, DFirst, DLast, DStDev, DStDevP, DVar and DVarP (the last two and DStDevP are not asked elsewhere), counting a column with Nulls, an empty domain (criteria matching no row), single-row variance, text and date arguments, an expression as the field argument and a criteria containing quotes and a date literal. Each call is its own snapshot SELECT expr, VarType(expr), TypeName(expr) FROM One.",
            (p, n) => Typed(p, n, ["DCount('*', 'Dm')", "DCount('*', '[Dm]')", "DCount('N', 'Dm')", "DCount('S', 'Dm')", "DCount('G', 'Dm')", "DCount('*', 'Dm', 'N > 25')", "DCount('*', 'Dm', 'G = ''a''')",
                "DCount('*', 'Dm', 'G Is Null')", "DCount('*', 'Dm', 'S = ''''')", "DCount('*', 'Dm', 'DT > #2023-03-15 12:00#')", "DCount('*', 'Dm', 'N > 1000')", "DCount('DISTINCT G', 'Dm')",
                "DSum('V', 'Dm')", "DSum('N', 'Dm')", "DSum('N * 2', 'Dm')", "DSum('V', 'Dm', '1 = 0')", "DSum('S', 'Dm')", "DAvg('V', 'Dm')", "DAvg('N', 'Dm')", "DAvg('N', 'Dm', 'N > 1000')",
                "DMin('V', 'Dm')", "DMax('V', 'Dm')", "DMin('S', 'Dm')", "DMax('S', 'Dm')", "DMin('DT', 'Dm')", "DMax('DT', 'Dm')", "DMax('N', 'Dm', 'G = ''zz''')",
                "DFirst('N', 'Dm')", "DLast('N', 'Dm')", "DFirst('N', 'Dm', 'G = ''b''')", "DLast('S', 'Dm', 'G = ''a''')", "DFirst('S', 'Dm', 'ID = 3')",
                "DStDev('V', 'Dm')", "DStDevP('V', 'Dm')", "DVar('V', 'Dm')", "DVarP('V', 'Dm')", "DStDev('V', 'Dm', 'ID = 1')", "DStDevP('V', 'Dm', 'ID = 1')", "DVar('V', 'Dm', 'ID = 1')",
                "DVarP('V', 'Dm', 'ID = 1')", "DVar('V', 'Dm', 'G = ''b''')", "DVarP('V', 'Dm', 'G = ''b''')", "DStDev('N', 'Dm')", "DVar('S', 'Dm')"], true));
        yield return Read("expr-domain-criteria-and-failures", ["R-EXPR-1", "EXPR-DOMAIN", "EXPR-BIND"],
            "Asks how the domain functions read their three arguments and what they raise: a missing row, several matching rows, an embedded quote, an unknown column, table and criteria column, a syntax error in the criteria, Null and empty arguments, a numeric or Boolean criteria, an aggregate or a list as the field argument, a SELECT text, a bracketed subquery and a join as the domain, ORDER BY and GROUP BY inside the arguments, an arity mismatch, and a system table. Each call is its own snapshot SELECT expr FROM One, followed by DBEngine.Errors.",
            (p, n) => Scalars(p, n, ["DLookup('S', 'Dm', 'ID = 6')", "DLookup('S', 'Dm', 'ID = 99')", "DLookup('S', 'Dm', 'ID = 6 OR ID = 8')", "DLookup('ID', 'Dm', 'S = ''q''''r''')",
                "DLookup('Nope', 'Dm')", "DLookup('ID', 'NoTable')", "DLookup('ID', 'Dm', 'Nope = 1')", "DLookup('ID', 'Dm', 'ID =')", "DLookup(Null, 'Dm')", "DLookup('ID', Null)",
                "DLookup('ID', 'Dm', Null)", "DLookup('ID', 'Dm', '')", "DLookup('ID', 'Dm', ' ')", "DLookup('Count(*)', 'Dm')", "DLookup('ID', 'Dm', 'ID IN (SELECT ID FROM Dm WHERE N > 40)')",
                "DLookup('ID', 'SELECT ID FROM Dm')", "DLookup('ID', '(SELECT ID FROM Dm)')", "DLookup('ID', '[SELECT ID FROM Dm]. AS x')", "DCount('*', 'Dm', 'G = (SELECT Max(G) FROM Dm)')",
                "DLookup('ID', 'Dm', 'ID = ' & 4)", "DLookup('ID', 'Dm', 4)", "DLookup('ID', 'Dm', True)", "DLookup('ID', 'Dm ORDER BY ID DESC')", "DLookup('ID', 'Dm', 'ID > 0 ORDER BY ID DESC')",
                "DLookup('ID', 'Dm', 'ID > 0 GROUP BY ID')", "DLookup('ID', 'One, Dm')", "DLookup('ID', 'Dm INNER JOIN One ON Dm.ID = One.ID')", "DLookup('1/0', 'Dm')", "DLookup('ID', 'Dm', '1/0 = 1')",
                "DLookup(1, 'Dm')", "DLookup('ID', 1)", "DLookup('*', 'Dm')", "DLookup('ID, N', 'Dm')", "DLookup('[ID]', '[Dm]', '[ID] = 3')", "DLookup('Dm.ID', 'Dm')", "DLookup('ID', 'Dm AS a')",
                "DLookup('ID', 'Dm', 'ID = 3', 1)", "DLookup('ID')", "DCount()", "DCount('*')", "DLookup('Name', 'MSysObjects')", "DLookup('ID', 'Dm', 'ID = [ID]')", "DLookup('ID', 'Dm', 'N = Null')",
                "DLookup('ID', 'Dm', 'N Is Null')", "DCount('*', 'Dm', 'DT = #2023-03-15#')", "DCount('*', 'Dm', 'DT = #2023-03-15 00:00:00#')"], true));
        yield return Read("expr-domain-per-row", ["R-EXPR-1", "EXPR-DOMAIN", "EXPR-STAGE"],
            "Asks how domain functions evaluate inside a query over the same table (per row, in WHERE, in GROUP BY and DISTINCT, with a criteria built from the current row's values, with a Null in the criteria text, as a running total and as a correlated count): each form is one snapshot over Dm, ordered where the form has an ID, with DBEngine.Errors read after it.",
            (p, n) => Sels(p, n, ["SELECT ID, DLookup('S', 'Dm', 'ID = ' & ID) AS v FROM Dm ORDER BY ID", "SELECT ID, DCount('*', 'Dm', 'G = ''' & G & '''') AS c FROM Dm ORDER BY ID",
                "SELECT ID, DCount('*', 'Dm', 'ID < ' & ID) AS c FROM Dm ORDER BY ID", "SELECT ID, DMax('N', 'Dm', 'G = ''' & Nz(G, '') & '''') AS c FROM Dm ORDER BY ID",
                "SELECT ID FROM Dm WHERE N = DMax('N', 'Dm')", "SELECT ID FROM Dm WHERE ID = DLookup('ID', 'Dm', 'N = 20')", "SELECT ID, DSum('N', 'Dm', 'ID <= ' & ID) AS running FROM Dm ORDER BY ID",
                "SELECT G, DCount('*', 'Dm', 'G = ''' & G & '''') AS c FROM Dm GROUP BY G", "SELECT DISTINCT DLookup('S', 'Dm', 'ID = 1') AS v FROM Dm",
                "SELECT ID, DLookup('S', 'Dm', 'ID = ' & ID) AS v FROM Dm WHERE DLookup('N', 'Dm', 'ID = ' & ID) > 25 ORDER BY ID",
                "SELECT ID, DLookup('ID', 'Dm', 'ID = ' & ID & ' OR 1 = 1') AS first FROM Dm ORDER BY ID", "SELECT ID FROM Dm ORDER BY DLookup('N', 'Dm', 'ID = ' & ID), ID",
                "SELECT ID, DLookup('S', 'Dm', 'ID = ' & (ID + 1)) AS nxt FROM Dm ORDER BY ID", "SELECT ID, DCount('*', 'Dm', 'V > ' & V) AS c FROM Dm ORDER BY ID",
                "SELECT ID, DCount('*', 'Dm', 'DT > #' & Format(DT, 'yyyy-mm-dd hh:nn:ss') & '#') AS c FROM Dm ORDER BY ID"]));
        yield return Read("expr-literals-numeric-suffix-radix", ["R-EXPR-1", "Branch B", "EXPR-BIND"],
            "Asks how the SQL expression parser reads numeric literal spellings: VBA type-suffix characters (# ! @ & %), D and E exponents with and without sign, a bare or dangling exponent, a digit group separator, hexadecimal and octal forms with and without suffix and past the 16-, 32- and 64-bit widths, a 0x prefix, leading zeros, signs and repeated signs, values at the Double, Decimal, Currency and 64-bit edges, and 30-digit and 40-digit literals. Each literal is its own snapshot SELECT expr, VarType(expr), TypeName(expr) FROM One.",
            (p, n) => Typed(p, n, ["1#", "1!", "1@", "1&", "1%", "1.5@", "1.5!", "1.5#", "1.5&", "1.5%", "1D3", "1d3", "1D-3", "1E3", "1e+3", ".5E1", "5.E1", "1E", "1E+", "1.2.3", "1_000", "1,000",
                "&HFF", "&hff", "&HFFFF", "&H7FFF", "&H8000", "&H7FFFFFFF", "&H80000000", "&HFFFFFFFF", "&H100000000", "&HFF&", "&HFFFF%", "&HFF#", "&H", "&HG", "&O17", "&O777777",
                "&O1777777777777", "&B101", "0xFF", "0x10", "00012", "-0", "-0.0", "+5", "--5", "+-5", "- -5", "-(-5)", "5 5", "1e308", "1e309", "1e-324", "1e-325", "2.2250738585072014E-308",
                "4.9E-324", "1.7976931348623157E308", "79228162514264337593543950335", "79228162514264337593543950336", "9223372036854775807", "9223372036854775808", "-9223372036854775808",
                "2147483647", "2147483648", "-2147483648", "-2147483649", "922337203685477.5807", "922337203685477.5808", "0.30000000000000004", "123456789012345678901234567890",
                "1234567890123456789012345678901234567890", "0.000000000000000000000000000001", "1.0", "1.00", "100", "1.5E0", "1.5E+0", "1.5e-0", "1E0", "0E0", "0.0E5"], true));
        yield return Read("expr-literals-date-forms", ["R-EXPR-1", "Branch C", "EXPR-BIND"],
            "Asks how the #...# date-literal parser reads spellings that c-date-text-and-literals does not use: an out-of-range hour, impossible days and a leap day, time-only forms with AM/PM and 24:00, month-name and day-name forms, two-digit and four-digit years around the century window and below year 100, the first and last representable moments and one past each, spaces inside the delimiters, a T separator, fractional seconds, an empty, one-part and non-date body, and arithmetic on a literal. Each literal is its own snapshot SELECT expr, VarType(expr), CDbl(expr) FROM One; results depend on the locale, which the run records.",
            (p, n) =>
            {
                foreach (string e in new[] { "#2024-03-05 25:00#", "#2024-02-30#", "#2023-02-29#", "#2024-02-29#", "#12:30#", "#12:30:45 PM#", "#12:30 AM#", "#0:00#", "#24:00#", "#3/5/2024#", "#13/5/2024#",
                    "#2024-3-5#", "#2024/03/05#", "#5 Mar 2024#", "#March 5, 2024#", "#Mar 5#", "#Tuesday#", "#12/30/1899#", "#12/29/1899#", "#1/1/0100#", "#1/1/0099#", "#100-01-01#", "#99-01-01#",
                    "#9999-12-31 23:59:59#", "#9999-12-31 23:59:59.999#", "#10000-01-01#", "# 2024-03-05 #", "#2024-03-05T10:00#", "#2024-03-05 10:00:00.5#", "#1/1/30#", "#1/1/29#", "##", "#abc#", "#1#",
                    "#2024#", "#3/5#", "#2024-03-05#+1", "#2024-03-05# + 0.5", "#2024-03-05# - #2024-03-04#", "#2024-03-05# & ''", "#2024-03-05# = #2024-03-05 00:00:00#", "#2024-03-05# < 45000" })
                {
                    Sel(p, n, $"SELECT {e} AS v, VarType({e}) AS vt, CDbl({e}) AS d FROM One", true);
                }
            });
        yield return Read("expr-operator-spellings", ["R-EXPR-1", "Branch B", "R-SQL-1", "SQL-B"],
            "Asks which operator spellings the expression parser accepts and what they return: <> and the other not-equal and less-or-equal spellings (!=, ^=, ><, =<, =>, ==, <=>), chained comparisons, the logical words in mixed case, && and ||, Is Null forms and their negations, Not with Between, In and Like, Mod in three cases and %, //, \\ and ** , ^ with negative and fractional operands, unary signs on Boolean, and & and + with Null. Each expression is its own snapshot SELECT expr, VarType(expr) FROM One, followed by DBEngine.Errors.",
            (p, n) => Typed(p, n, ["1 <> 2", "1 != 2", "1 ^= 2", "1 >< 2", "1 =< 2", "1 => 2", "1 <= 2", "1 == 1", "1 <=> 1", "1 = 1 = 1", "1 < 2 < 3", "3 > 2 > 1", "Not 1 = 2", "Not Not 1",
                "1 && 1", "1 || 0", "1 AND 1", "1 and 1", "1 aNd 1", "True And 2", "True And Null", "1 Is Null", "Null Is Null", "Null Is Not Null", "1 IS NOT NULL", "Not Null Is Null",
                "1 Not Between 0 And 2", "Not 1 Between 0 And 2", "1 Not In (2, 3)", "'a' Not Like 'b'", "Not 'a' Like 'b'", "'a' Like 'a' Like 'a'", "5 Mod 3", "5 mod 3", "5 MOD 3", "5 % 3",
                "5 // 3", "5 \\ 3", "5 / 3", "5 ** 2", "5 ^ 2", "2 ^ -1", "2 ^ 0.5", "-2 ^ 2", "(-2) ^ 2", "-2 ^ 0.5", "(-2) ^ 0.5", "5 & 3", "5 + '3'", "'5' + '3'", "5 - '3'", "5 * '3'",
                "'a' & Null", "'a' + Null", "5 + Null", "5 & Null", "Null & Null", "5 Xor 3", "5 Eqv 3", "5 Imp 3", "5 And 3", "5 Or 3", "Not 5", "-True", "+True", "True + True", "True * True",
                "True = 1", "True = -1", "1 = '1'", "'1' = 1.0", "'a' = 'A'", "'a' < 'B'", "5 \\ 0", "5 Mod 0", "5 / 0", "0 / 0", "5.5 Mod 2", "5.5 \\ 2", "-5 \\ 2", "-5 Mod 3",
                "2147483647 + 1", "2147483647 * 2", "1 Between 2 And 0", "1 Between Null And 2", "1 In ()", "1 In (1,)"], true));
        yield return Read("expr-nonjet-function-names", ["R-EXPR-1", "EXPR-INVENTORY", "EXPR-SANDBOX", "R-SQL-1", "SQL-B"],
            "Asks which names and clauses from other SQL dialects the expression service binds and how it refuses the rest: CASE WHEN, COALESCE, NULLIF, IFNULL, ISNULL with one and two arguments, CAST and CONVERT, UPPER, LOWER, LENGTH, SUBSTRING, SUBSTR, TRIM, POSITION, CONCAT, the CURRENT_ and GETDATE family, CEILING, FLOOR, POWER, SQRT, MOD, TRUNC, RAND, NEWID, and the @@IDENTITY family. Each expression is its own snapshot SELECT expr FROM One, followed by DBEngine.Errors.",
            (p, n) => Scalars(p, n, ["CASE WHEN 1 = 1 THEN 'a' ELSE 'b' END", "CASE 1 WHEN 1 THEN 'a' END", "COALESCE(Null, 1)", "COALESCE(Null, Null)", "NULLIF(1, 1)", "IFNULL(Null, 1)", "ISNULL(Null)",
                "ISNULL(Null, 1)", "ISNULL(1, 2)", "IsNull(Null)", "IIF(1 = 1, 'a', 'b')", "CAST(1 AS LONG)", "CAST('1' AS INTEGER)", "CAST(1 AS TEXT)", "CAST(1 AS VARCHAR(5))", "CONVERT(1, TEXT)",
                "CONVERT(INTEGER, '1')", "UPPER('a')", "LOWER('A')", "LENGTH('abc')", "CHAR_LENGTH('abc')", "CHARACTER_LENGTH('abc')", "SUBSTRING('abc', 2, 1)", "SUBSTRING('abc' FROM 2 FOR 1)",
                "SUBSTR('abc', 2, 1)", "TRIM(' a ')", "TRIM(BOTH ' ' FROM ' a ')", "POSITION('b' IN 'abc')", "CONCAT('a', 'b')", "'a' || 'b'", "CURRENT_TIMESTAMP", "CURRENT_DATE", "CURRENT_TIME",
                "GETDATE()", "SYSDATE", "SYSDATE()", "NOW()", "TODAY()", "CEILING(1.2)", "CEIL(1.2)", "FLOOR(1.8)", "POWER(2, 3)", "SQRT(4)", "MOD(7, 2)", "TRUNC(1.8)", "TRUNCATE(1.8, 0)",
                "RAND()", "RANDOM()", "NEWID()", "UUID()", "STR_TO_DATE('2024-03-05', '%Y-%m-%d')", "DATE_ADD(#2024-03-05#, 1)", "DATEADD(d, 1, #2024-03-05#)", "DATEADD('d', 1, #2024-03-05#)",
                "ISNUMERIC('1')", "ISDATE('2024-03-05')", "LEN('abc')", "LTRIM(' a')", "SPACE(2)", "REPLICATE('a', 2)", "REVERSE('abc')", "STUFF('abc', 2, 1, 'x')", "CHARINDEX('b', 'abc')",
                "LEFT('abc', 1)", "RIGHT('abc', 1)", "ASCII('a')", "CHAR(65)", "NCHAR(65)", "UNICODE('a')", "ABS(-1)", "SIGN(-1)", "ROUND(1.5, 0)", "LOG10(100)", "LN(1)", "EXP(0)", "PI()", "SIN(0)",
                "@@IDENTITY", "@@ROWCOUNT", "@@VERSION", "@@ERROR", "@@SPID", "USER", "CURRENT_USER", "SESSION_USER", "SYSTEM_USER", "USER()", "DATABASE()", "VERSION()", "LAST_INSERT_ID()",
                "DB_NAME()", "OBJECT_ID('One')", "COUNT(*)", "1 FROM One", "*"], true));
        yield return Read("expr-filehandle-functions-unopened", ["R-EXPR-1", "EXPR-SANDBOX", "EXPR-INVENTORY"],
            "Asks what the VBA file-handle functions answer or raise when no file was opened by the expression service (no path is named and nothing is read or written): FreeFile with and without a range argument, EOF, LOF, Loc, Seek and FileAttr on handles 0, 1, 255 and 256, negative handles, and Input on an unopened handle. Each call is its own snapshot SELECT expr FROM One, followed by DBEngine.Errors.",
            (p, n) => Scalars(p, n, ["FreeFile()", "FreeFile(0)", "FreeFile(1)", "FreeFile(2)", "FreeFile(-1)", "EOF(1)", "EOF(0)", "EOF(-1)", "EOF(255)", "EOF(256)", "LOF(1)", "LOF(0)", "LOF(255)", "Loc(1)", "Loc(0)",
                "Seek(1)", "Seek(0)", "Seek(255)", "FileAttr(1, 1)", "FileAttr(1, 2)", "FileAttr(1, 3)", "FileAttr(0, 1)", "FileAttr(255, 1)", "Input(1, 1)", "Input(0, 1)", "Input(1, 0)", "Input(-1, 1)",
                "Tab(5)", "Spc(5)"], true));

        // R-SQL-1 / R-SQL-2: SQL text, clauses and statements ---------------------------------------------------------------
        yield return Read("sql-text-framing-forms", ["R-SQL-1", "SQL-B"],
            "Asks which exact texts Database.OpenRecordset accepts as a SELECT over One, and with what error where it refuses: no FROM, one or several trailing semicolons, text after a semicolon, a leading semicolon, leading and internal tabs, CR and LF, non-ASCII spaces (U+00A0, U+3000, U+2003) as separators, lower case, a trailing keyword, # and -- and /* */ comment forms (including an unterminated one), empty and blank and semicolon-only text, a bare table name, parenthesised statements and sources, dangling commas, unbalanced parentheses and brackets, and an unterminated string. Each text is its own snapshot, followed by DBEngine.Errors.",
            (p, n) => Sels(p, n, ["SELECT 1", "SELECT 1 AS v", "SELECT 1 AS v;", "SELECT 1 AS v FROM One", "SELECT 1 AS v FROM One;", "SELECT 1 AS v FROM One;;", "SELECT 1 AS v FROM One ; ", "SELECT 1 AS v FROM One;\r\n",
                "SELECT 1 AS v FROM One; SELECT 2 AS v FROM One", "SELECT 1 AS v FROM One; garbage", ";SELECT 1 AS v FROM One", " SELECT 1 AS v FROM One", "\tSELECT 1 AS v FROM One", "\r\nSELECT 1 AS v FROM One",
                "SELECT\t1 AS v\rFROM\nOne", "SELECT\u00A01 AS v FROM One", "SELECT 1 AS v FROM\u00A0One", "SELECT\u30001 AS v FROM One", "SELECT\u2003 1 AS v FROM One", "select 1 as v from one",
                "SELECT 1 AS v FROM ONE WHERE", "SELECT 1 AS v FROM One -- c", "SELECT 1 AS v FROM One # c", "SELECT 1 AS v /* c */ FROM One", "SELECT/*c*/1 AS v FROM One", "/* c */ SELECT 1 AS v FROM One",
                "SELECT 1 AS v FROM One /* c", "", " ", ";", "SELECT", "SELECT *", "SELECT * FROM", "SELECT * FROM One WHERE ID = 1;", "One", "[One]", "SELECT * FROM [One]", "SELECT * FROM One.", "SELECT One.* FROM One",
                "(SELECT 1 AS v FROM One)", "((SELECT 1 AS v FROM One))", "SELECT 1 AS v FROM (One)", "SELECT 1 AS v FROM ((One))", "SELECT (1) AS v FROM One", "SELECT 1 AS v,  FROM One", "SELECT , 1 AS v FROM One",
                "SELECT 1 AS v FROM One,", "SELECT 1 AS v FROM One WHERE ID = 1 AND", "SELECT 1 AS v FROM One WHERE (ID = 1", "SELECT 1 AS v FROM One WHERE ID = 1)", "SELECT 1 AS v FROM One ORDER BY",
                "SELECT 1 AS v FROM One GROUP BY", "SELECT '1 AS v FROM One", "SELECT 1 AS v FROM [One", "SELECT 1 AS v FROM One]", "SELECT 1 AS v FROM One WHERE ID = 1 ORDER BY ID DESC;"]));
        yield return Read("sql-select-modifier-order", ["R-SQL-1", "R-SQL-3", "SQL-B", "SQL-TOP-02"],
            "Asks which orders and repetitions of the SELECT modifiers ALL, DISTINCT, DISTINCTROW, TOP n and TOP n PERCENT are accepted over Dm, which rows they return, and what ORDER BY a column that is not selected does under DISTINCT: DISTINCT with TOP in both orders, DISTINCTROW with TOP, ALL with TOP and DISTINCT, doubled modifiers, TOP with PERCENT and DISTINCT, TOP over ties, TOP over an aggregate ordered by Count and by position, and WITH TIES. Each form is its own snapshot, followed by DBEngine.Errors.",
            (p, n) => Sels(p, n, ["SELECT DISTINCT TOP 2 G FROM Dm ORDER BY G", "SELECT TOP 2 DISTINCT G FROM Dm", "SELECT DISTINCTROW TOP 2 G FROM Dm ORDER BY G", "SELECT TOP 2 DISTINCTROW G FROM Dm",
                "SELECT ALL G FROM Dm ORDER BY G, ID", "SELECT ALL TOP 2 G FROM Dm ORDER BY G, ID", "SELECT TOP 2 ALL G FROM Dm ORDER BY G, ID", "SELECT DISTINCT ALL G FROM Dm", "SELECT DISTINCT DISTINCTROW G FROM Dm",
                "SELECT TOP 2 TOP 3 G FROM Dm", "SELECT DISTINCT DISTINCT G FROM Dm", "SELECT TOP 2 PERCENT DISTINCT G FROM Dm ORDER BY G", "SELECT DISTINCT TOP 50 PERCENT G FROM Dm ORDER BY G",
                "SELECT DISTINCT G, N FROM Dm ORDER BY N DESC", "SELECT DISTINCT G FROM Dm ORDER BY N", "SELECT DISTINCT G FROM Dm ORDER BY G, ID", "SELECT TOP 3 G FROM Dm ORDER BY G",
                "SELECT TOP 1 G FROM Dm ORDER BY G", "SELECT TOP 1 G FROM Dm ORDER BY G, ID", "SELECT TOP 1 WITH TIES G FROM Dm ORDER BY G", "SELECT TOP 2 * FROM Dm ORDER BY V DESC, ID",
                "SELECT DISTINCT TOP 2 * FROM Dm ORDER BY ID", "SELECT TOP 2 G, Count(*) AS c FROM Dm GROUP BY G ORDER BY Count(*) DESC, G", "SELECT TOP 2 G, Count(*) AS c FROM Dm GROUP BY G ORDER BY 2 DESC, 1",
                "SELECT DISTINCT TOP 2 G, Count(*) AS c FROM Dm GROUP BY G ORDER BY G", "SELECT TOP 2 ID FROM Dm WHERE ID IN (SELECT TOP 3 ID FROM Dm ORDER BY ID DESC) ORDER BY ID",
                "SELECT ID FROM Dm WHERE ID IN (SELECT TOP 3 ID FROM Dm) ORDER BY ID", "SELECT TOP 2 ID FROM Dm UNION SELECT TOP 2 ID FROM Dm ORDER BY ID DESC",
                "SELECT TOP 100 PERCENT ID FROM Dm ORDER BY ID DESC", "SELECT TOP 0 PERCENT ID FROM Dm", "SELECT TOP 2 ID FROM Dm ORDER BY Rnd(ID) * 0, ID"]));
        yield return Read("sql-nonstandard-clauses-select", ["R-SQL-1", "SQL-B", "SQL-EQ", "SQL-AGG-COUNT"],
            "Asks how ACE answers SELECT clauses from other SQL dialects and some Jet-only source forms over Dm and One: LIMIT, OFFSET, FETCH FIRST, FIRST and SKIP as modifiers, ROWNUM and ROW_NUMBER, FOR UPDATE and table hints, WITH (a common table expression), VALUES and TABLE statements, GROUP BY ALL, DISTINCT in Sum, Avg and a two-column Count, a derived table with and without an alias and in the bracketed [SELECT ...]. form, HAVING without GROUP BY, an aggregate in WHERE, non-grouped selected columns, star and qualified-star combinations, ORDER BY a position out of range, a string, a collation or NULLS FIRST, and ORDER BY after UNION by a qualified name. Each text is its own snapshot, followed by DBEngine.Errors.",
            (p, n) => Sels(p, n, ["SELECT ID FROM Dm ORDER BY ID LIMIT 2", "SELECT ID FROM Dm ORDER BY ID LIMIT 2 OFFSET 1", "SELECT ID FROM Dm ORDER BY ID OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY", "SELECT ID FROM Dm ORDER BY ID FETCH FIRST 2 ROWS ONLY",
                "SELECT FIRST 2 ID FROM Dm", "SELECT SKIP 1 ID FROM Dm", "SELECT ROWNUM, ID FROM Dm", "SELECT ROW_NUMBER() OVER (ORDER BY ID) AS r FROM Dm", "SELECT ID FROM Dm FOR UPDATE", "SELECT ID FROM Dm WITH (NOLOCK)",
                "SELECT ID FROM Dm WHERE ID = 1 FOR READ ONLY", "WITH x AS (SELECT ID FROM Dm) SELECT ID FROM x", "VALUES (1)", "TABLE Dm", "SELECT ID FROM Dm GROUP BY ALL ID", "SELECT Sum(DISTINCT N) AS c FROM Dm",
                "SELECT Avg(DISTINCT V) AS c FROM Dm", "SELECT Count(DISTINCT G, N) AS c FROM Dm", "SELECT Count(*) AS c FROM (SELECT DISTINCT G FROM Dm)", "SELECT Count(*) AS c FROM (SELECT DISTINCT G FROM Dm) AS d",
                "SELECT Count(*) AS c FROM [SELECT DISTINCT G FROM Dm]. AS d", "SELECT d.ID FROM [SELECT ID FROM Dm;]. AS d", "SELECT ID FROM [SELECT ID FROM Dm;].", "SELECT x.ID FROM (SELECT ID FROM Dm) x",
                "SELECT ID FROM Dm HAVING ID > 1", "SELECT Count(*) FROM Dm HAVING Count(*) > 1", "SELECT Count(*) FROM Dm WHERE Count(*) > 1", "SELECT G AS k, Count(*) AS c FROM Dm GROUP BY k",
                "SELECT G AS k, Count(*) AS c FROM Dm GROUP BY G ORDER BY k", "SELECT G, N FROM Dm GROUP BY G", "SELECT G, First(N) AS f FROM Dm GROUP BY G", "SELECT * FROM Dm GROUP BY ID, G, V, N, S, DT",
                "SELECT Dm.*, One.* FROM Dm, One WHERE Dm.ID = One.ID", "SELECT *, ID FROM Dm", "SELECT ID, * FROM Dm WHERE ID = 1", "SELECT Dm.* FROM Dm WHERE ID In (1, 2) ORDER BY 1", "SELECT ID FROM Dm ORDER BY 0",
                "SELECT ID FROM Dm ORDER BY 9", "SELECT ID FROM Dm ORDER BY -1", "SELECT ID FROM Dm ORDER BY 1.5", "SELECT ID FROM Dm ORDER BY 'ID'", "SELECT ID FROM Dm ORDER BY ID COLLATE NOCASE",
                "SELECT ID FROM Dm ORDER BY ID NULLS FIRST", "SELECT ID FROM Dm ORDER BY ID ASC, ID DESC", "SELECT ID FROM Dm ORDER BY (ID)", "SELECT ID FROM Dm ORDER BY ID + 0",
                "SELECT ID FROM Dm WHERE ID = 1 UNION SELECT ID FROM Dm WHERE ID = 2 ORDER BY ID DESC", "SELECT ID FROM Dm UNION SELECT ID FROM One ORDER BY Dm.ID", "SELECT ID FROM Dm WHERE ID = 1 INTERSECT SELECT ID FROM Dm",
                "SELECT ID FROM Dm EXCEPT SELECT ID FROM One", "SELECT ID FROM Dm MINUS SELECT ID FROM One", "SELECT ID FROM Dm UNION ALL SELECT ID FROM One ORDER BY 1 DESC"]));
        yield return Read("sql-join-syntax-forms", ["R-SQL-1", "SQL-J", "SQL-B"],
            "Asks which join spellings and shapes parse, with what rows or error: comma joins, bare JOIN, LEFT and RIGHT with and without OUTER, a join without ON or with USING, an unparenthesised chain of two and three joins and the parenthesised forms, mixing LEFT and INNER in both orders, ON with a one-sided, constant, non-equi, BETWEEN, IN, OR and subquery condition, an ON referring to a later alias, a duplicate alias, an ambiguous unqualified name, the Oracle (+) and T-SQL *= outer-join spellings, and a WHERE on the outer side. Each text is its own snapshot over Dm (8 rows) and One (1 row), followed by DBEngine.Errors.",
            (p, n) => Sels(p, n, ["SELECT Dm.ID FROM Dm, One", "SELECT Dm.ID FROM Dm JOIN One ON Dm.ID = One.ID", "SELECT Dm.ID FROM Dm INNER JOIN One ON Dm.ID = One.ID", "SELECT Dm.ID FROM Dm LEFT JOIN One ON Dm.ID = One.ID ORDER BY Dm.ID",
                "SELECT Dm.ID FROM Dm LEFT OUTER JOIN One ON Dm.ID = One.ID ORDER BY Dm.ID", "SELECT Dm.ID FROM Dm RIGHT JOIN One ON Dm.ID = One.ID", "SELECT Dm.ID FROM Dm RIGHT OUTER JOIN One ON Dm.ID = One.ID",
                "SELECT Dm.ID FROM Dm INNER JOIN One", "SELECT Dm.ID FROM Dm INNER JOIN One USING (ID)", "SELECT Dm.ID FROM Dm INNER JOIN One ON Dm.ID = One.ID INNER JOIN Dm AS d2 ON d2.ID = Dm.ID",
                "SELECT Dm.ID FROM (Dm INNER JOIN One ON Dm.ID = One.ID) INNER JOIN Dm AS d2 ON d2.ID = Dm.ID", "SELECT Dm.ID FROM Dm INNER JOIN (One INNER JOIN Dm AS d2 ON d2.ID = One.ID) ON Dm.ID = d2.ID",
                "SELECT Dm.ID FROM Dm LEFT JOIN One ON Dm.ID = One.ID LEFT JOIN Dm AS d2 ON d2.ID = Dm.ID", "SELECT Dm.ID FROM (Dm LEFT JOIN One ON Dm.ID = One.ID) LEFT JOIN Dm AS d2 ON d2.ID = Dm.ID ORDER BY Dm.ID",
                "SELECT Dm.ID FROM (Dm LEFT JOIN One ON Dm.ID = One.ID) INNER JOIN Dm AS d2 ON d2.ID = Dm.ID", "SELECT Dm.ID FROM (Dm INNER JOIN One ON Dm.ID = One.ID) LEFT JOIN Dm AS d2 ON d2.ID = Dm.ID",
                "SELECT Dm.ID FROM Dm INNER JOIN One ON Dm.ID = One.ID AND Dm.N > 5", "SELECT Dm.ID FROM Dm LEFT JOIN One ON Dm.ID = One.ID AND Dm.N > 15 ORDER BY Dm.ID", "SELECT Dm.ID FROM Dm INNER JOIN One ON Dm.ID > One.ID",
                "SELECT Dm.ID FROM Dm INNER JOIN One ON Dm.ID <> One.ID", "SELECT Dm.ID FROM Dm INNER JOIN One ON Dm.ID BETWEEN One.ID AND 3", "SELECT Dm.ID FROM Dm INNER JOIN One ON Dm.ID In (1, 2)",
                "SELECT Dm.ID FROM Dm INNER JOIN One ON Dm.ID = One.ID OR Dm.ID = 2", "SELECT Dm.ID FROM Dm INNER JOIN One ON 1 = 1", "SELECT Dm.ID FROM Dm INNER JOIN One ON Dm.ID = 1", "SELECT Dm.ID FROM Dm INNER JOIN One ON One.ID = 1",
                "SELECT Dm.ID FROM Dm INNER JOIN One ON Dm.ID = (SELECT Max(ID) FROM One)", "SELECT Dm.ID FROM Dm INNER JOIN One ON Dm.ID = One.ID AND (SELECT 1 FROM One) = 1",
                "SELECT Dm.ID FROM Dm INNER JOIN One ON Dm.ID = d2.ID INNER JOIN Dm AS d2 ON d2.ID = One.ID", "SELECT d.ID FROM Dm AS d INNER JOIN Dm AS d ON d.ID = d.ID", "SELECT ID FROM Dm, One",
                "SELECT Dm.ID FROM Dm LEFT JOIN One ON Dm.ID = One.ID WHERE One.ID IS NULL ORDER BY Dm.ID", "SELECT Dm.ID FROM Dm, One WHERE Dm.ID = One.ID(+)", "SELECT Dm.ID FROM Dm, One WHERE Dm.ID *= One.ID",
                "SELECT Dm.ID FROM Dm, One WHERE Dm.ID = One.ID", "SELECT Dm.ID FROM Dm FULL OUTER JOIN One ON Dm.ID = One.ID", "SELECT Dm.ID FROM Dm CROSS JOIN One", "SELECT Dm.ID FROM Dm NATURAL JOIN One"]));
        yield return Read("sql-foreign-identifier-quoting", ["R-SQL-1", "SQL-B", "R-SQL-3"],
            "Asks how identifier and parameter-marker spellings from other systems are read over Dm: a double-quoted or single-quoted name, a #-delimited name, @, : and $ prefixed names, a bare ? marker, a Forms!F!C, TempVars!x and Screen.ActiveForm.Name reference in WHERE, a bang qualifier, a dotted bracket, spaces around the dot, a name starting with a digit, a keyword and a quoted name as alias with and without AS, and a duplicated alias. DAO opens each as a snapshot directly; an unresolved name is read as the call at which it fails and its DBEngine.Errors, never as a prompt.",
            (p, n) => Sels(p, n, ["SELECT \"ID\" FROM Dm", "SELECT 'ID' FROM Dm", "SELECT ID FROM \"Dm\"", "SELECT ID FROM 'Dm'", "SELECT ID FROM #Dm#", "SELECT @ID FROM Dm", "SELECT :ID FROM Dm", "SELECT $ID FROM Dm",
                "SELECT ? FROM One", "SELECT ID FROM Dm WHERE ID = ?", "SELECT ID FROM Dm WHERE ID = @p", "SELECT ID FROM Dm WHERE ID = :p", "SELECT ID FROM Dm WHERE ID = [p]", "SELECT ID FROM Dm WHERE ID = [Forms]![F]![C]",
                "SELECT ID FROM Dm WHERE ID = Forms!F!C", "SELECT ID FROM Dm WHERE ID = TempVars!x", "SELECT ID FROM Dm WHERE ID = Screen.ActiveForm.Name", "SELECT [Dm]![ID] FROM Dm", "SELECT Dm!ID FROM Dm",
                "SELECT [Dm.ID] FROM Dm", "SELECT Dm . ID FROM Dm", "SELECT Dm. ID FROM Dm", "SELECT [Dm] . [ID] FROM Dm", "SELECT ID FROM Dm AS 1x", "SELECT ID AS 1x FROM Dm", "SELECT ID AS [1x] FROM Dm",
                "SELECT ID AS From FROM Dm", "SELECT ID AS Select FROM Dm", "SELECT ID AS [Select] FROM Dm", "SELECT ID x FROM Dm", "SELECT ID 'x' FROM Dm", "SELECT ID AS 'x' FROM Dm", "SELECT ID AS \"x\" FROM Dm",
                "SELECT ID AS x, ID AS x FROM Dm", "SELECT ID AS X, ID AS x FROM Dm", "SELECT ID AS ID, ID FROM Dm", "SELECT ID AS [a b], ID AS [a  b] FROM Dm", "SELECT ID AS [] FROM Dm", "SELECT ID AS [ ] FROM Dm"]));
        yield return Read("sql-insert-values-forms", ["R-SQL-2", "R-SQL-1", "SQL-ACT-DDL", "ACT-INS-03"],
            "Asks how Database.Execute (dbFailOnError) answers INSERT spellings into U (ID Long primary key, K Text(50) with a unique index), in order, each followed by RecordsAffected and DBEngine.Errors: VALUES with one row, several parenthesised rows and a trailing comma, no column list, a missing or extra value, a repeated or unknown column, an unknown table, no INTO, brackets, an expression, a scalar subquery and a domain function as a value, Null and empty text twice, a number and text for the wrong column type, a fractional and an out-of-range key, a 60-character value in a Text(50) column, INSERT ... SELECT with a TOP, UNION and GROUP BY source, a SELECT without FROM, a duplicate key from a second SELECT, and two statements in one text. Finally every row of U is read.",
            (p, n) =>
            {
                Exs(p, ["INSERT INTO U (ID, K) VALUES (1, 'a')", "INSERT INTO U (ID, K) VALUES (2, 'b'), (3, 'c')", "INSERT INTO U (ID, K) VALUES (4, 'd'),", "INSERT INTO U VALUES (5, 'e')",
                    "INSERT INTO U (K) VALUES ('f')", "INSERT INTO U (ID) VALUES (6)", "INSERT INTO U (ID, K) VALUES (7)", "INSERT INTO U (ID, K) VALUES (8, 'h', 'x')", "INSERT INTO U (ID, ID) VALUES (9, 9)",
                    "INSERT INTO U (ID, Zz) VALUES (10, 'j')", "INSERT INTO Nope (ID) VALUES (1)", "INSERT U (ID, K) VALUES (11, 'k')", "INSERT INTO [U] ([ID], [K]) VALUES (12, 'l')",
                    "INSERT INTO U (ID, K) VALUES (13, 'm' & 'n')", "INSERT INTO U (ID, K) VALUES (14, (SELECT Max(S) FROM Dm))", "INSERT INTO U (ID, K) VALUES (15, DLookup('S', 'Dm', 'ID = 2'))",
                    "INSERT INTO U (ID, K) VALUES (16, Null)", "INSERT INTO U (ID, K) VALUES (17, Null)", "INSERT INTO U (ID, K) VALUES (18, '')", "INSERT INTO U (ID, K) VALUES (19, '')",
                    "INSERT INTO U (ID, K) VALUES (20, 123)", "INSERT INTO U (ID, K) VALUES ('21', 'u')", "INSERT INTO U (ID, K) VALUES ('x', 'u')", "INSERT INTO U (ID, K) VALUES (22.5, 'v')",
                    "INSERT INTO U (ID, K) VALUES (2147483648, 'w')", "INSERT INTO U (ID, K) VALUES (24, 'x' & Space(60))", "INSERT INTO U (ID, K) SELECT ID + 100, S FROM Dm WHERE ID < 4",
                    "INSERT INTO U (ID, K) SELECT TOP 1 ID + 200, S FROM Dm ORDER BY ID DESC", "INSERT INTO U (ID, K) SELECT ID + 300, S FROM Dm UNION SELECT ID + 400, G FROM Dm",
                    "INSERT INTO U (ID, K) SELECT ID + 500, G FROM Dm GROUP BY ID, G", "INSERT INTO U SELECT * FROM Dm", "INSERT INTO U (ID, K) SELECT 600, 'z' FROM One", "INSERT INTO U (ID, K) SELECT 601, 'z' FROM One",
                    "INSERT INTO U (ID, K) SELECT 602, 'y'", "INSERT INTO U (ID, K) VALUES (700, 'p'); INSERT INTO U (ID, K) VALUES (701, 'q')"]);
                Sel(p, n, "SELECT ID, K FROM U ORDER BY ID", true, maxRows: 64);
            }, writable: true);
        yield return Read("sql-update-assignment-order", ["R-SQL-2", "ACT-UPD-02", "SQL-ACT-DDL"],
            "Asks, on a writable copy, how Database.Execute (dbFailOnError) evaluates UPDATE assignments over Dm, in sequence, each followed by RecordsAffected, DBEngine.Errors and the rows (ID, V, N, S) of Dm: a later assignment reading a column assigned earlier in the same SET, the reverse order, a swap of two columns, a qualified and an aliased target, a never-true WHERE, a concatenation over a Null, a row-value SET, a FROM clause, a scalar subquery, a domain function and an aggregate over the table being updated, a conversion of text into a Long column in both directions, a Long into a text column, and a primary-key shift (ID = ID + 1, then ID = ID - 1 for ID > 1).",
            (p, n) =>
            {
                foreach (string sql in new[] { "UPDATE Dm SET N = 1, V = N WHERE ID = 1", "UPDATE Dm SET V = N, N = 2 WHERE ID = 2", "UPDATE Dm SET N = V, V = N WHERE ID = 5", "UPDATE Dm SET N = N + 1, V = N + 1 WHERE ID = 7",
                    "UPDATE Dm SET Dm.N = 5 WHERE ID = 6", "UPDATE Dm AS d SET d.N = 6 WHERE d.ID = 6", "UPDATE Dm SET N = N WHERE 1 = 0", "UPDATE Dm SET S = S & 'x' WHERE ID IN (3, 4)", "UPDATE Dm SET (N, V) = (1, 2)",
                    "UPDATE Dm SET N = 1 FROM One", "UPDATE Dm SET N = (SELECT Max(N) FROM Dm) WHERE ID = 7", "UPDATE Dm SET N = DMax('N', 'Dm') WHERE ID = 8", "UPDATE Dm SET N = DCount('*', 'Dm')",
                    "UPDATE Dm SET N = ID * 2, V = N WHERE ID < 3", "UPDATE Dm SET N = '5' WHERE ID = 1", "UPDATE Dm SET N = 'x' WHERE ID = 1", "UPDATE Dm SET G = 123 WHERE ID = 1", "UPDATE Dm SET ID = ID + 1",
                    "UPDATE Dm SET ID = ID - 1 WHERE ID > 1", "UPDATE Dm SET N = Null" })
                {
                    Ex(p, sql);
                    Sel(p, n, "SELECT ID, V, N, S FROM Dm ORDER BY ID");
                }
            }, writable: true);
        yield return Read("sql-delete-forms", ["R-SQL-2", "ACT-DEL", "SQL-ACT-DDL"],
            "Asks, on a writable copy, which DELETE spellings Database.Execute (dbFailOnError) accepts over Dm and One, in sequence, each followed by RecordsAffected, DBEngine.Errors and the count of Dm rows: with and without *, a qualified star, an alias, a missing column list, a join with either table as target and with DISTINCTROW, TOP, LIMIT and ORDER BY, a subquery returning one and several rows, an EXISTS condition, a never-true WHERE, an unknown table and column, and no WHERE.",
            (p, n) =>
            {
                foreach (string sql in new[] { "DELETE FROM Dm WHERE ID = 1", "DELETE * FROM Dm WHERE ID = 2", "DELETE Dm.* FROM Dm WHERE ID = 3", "DELETE ID FROM Dm WHERE ID = 4",
                    "DELETE FROM Dm WHERE ID IN (SELECT TOP 1 ID FROM Dm ORDER BY ID DESC)", "DELETE FROM Dm WHERE N = (SELECT Max(N) FROM Dm)", "DELETE FROM Dm AS d WHERE d.ID = 5", "DELETE d.* FROM Dm AS d WHERE d.ID = 5",
                    "DELETE FROM Dm, One", "DELETE Dm.* FROM Dm INNER JOIN One ON Dm.ID = One.ID", "DELETE One.* FROM Dm INNER JOIN One ON Dm.ID = One.ID", "DELETE DISTINCTROW Dm.* FROM Dm INNER JOIN One ON Dm.ID = One.ID",
                    "DELETE TOP 1 FROM Dm", "DELETE FROM Dm LIMIT 1", "DELETE FROM Dm ORDER BY ID", "DELETE FROM Dm WHERE 1 = 0", "DELETE FROM Nope", "DELETE FROM Dm WHERE Nope = 1",
                    "DELETE FROM Dm WHERE ID = (SELECT ID FROM Dm)", "DELETE FROM Dm WHERE ID IN (SELECT ID FROM Dm WHERE N Is Null)", "DELETE FROM Dm WHERE EXISTS (SELECT 1 FROM One WHERE One.ID = Dm.ID)",
                    "DELETE FROM Dm WHERE EXISTS (SELECT 1 FROM One)", "DELETE FROM Dm" })
                {
                    Ex(p, sql);
                    Sel(p, n, "SELECT Count(*) AS c FROM Dm");
                }
            }, writable: true);
        yield return Read("sql-unsupported-statements", ["R-SQL-2", "R-SQL-1", "SQL-ACT-DDL", "SQL-B"],
            "Asks how Database.Execute (dbFailOnError) answers statements from other dialects and engines, on a writable copy, each followed by DBEngine.Errors: TRUNCATE, MERGE, REPLACE INTO, ON DUPLICATE KEY and INSERT OR REPLACE, RETURNING, RENAME in both forms, IF [NOT] EXISTS, CREATE TABLE ... AS SELECT and ... LIKE and ... ENGINE, CREATE DATABASE, USE, SET, transaction keywords, EXPLAIN, SHOW, DESCRIBE, PRAGMA, EXEC and CALL, SELECT INTO a # name and an existing table, ANALYZE, VACUUM, LOCK TABLE, DECLARE, PRINT, CREATE SEQUENCE, TRIGGER, FUNCTION and SCHEMA, and COMMENT ON. The TableDefs names are listed at the end.",
            (p, n) =>
            {
                Exs(p, ["TRUNCATE TABLE U", "MERGE INTO U USING Dm ON U.ID = Dm.ID WHEN MATCHED THEN DELETE", "REPLACE INTO U (ID, K) VALUES (1, 'a')", "INSERT INTO U (ID, K) VALUES (1, 'a') ON DUPLICATE KEY UPDATE K = 'b'",
                    "INSERT OR REPLACE INTO U (ID, K) VALUES (1, 'a')", "INSERT INTO U (ID, K) VALUES (1, 'a') RETURNING ID", "UPDATE U SET K = 'x' RETURNING K", "ALTER TABLE U RENAME TO U9", "ALTER TABLE U RENAME COLUMN K TO K9",
                    "RENAME TABLE U TO U9", "DROP TABLE IF EXISTS Nope", "CREATE TABLE IF NOT EXISTS Q1 (A LONG)", "CREATE TABLE Q2 AS SELECT * FROM One", "CREATE TABLE Q3 LIKE One", "CREATE TABLE Q4 (A LONG) ENGINE = X",
                    "CREATE DATABASE X", "USE X", "SET NOCOUNT ON", "BEGIN TRANSACTION", "COMMIT", "ROLLBACK", "SAVEPOINT s", "EXPLAIN SELECT 1", "SHOW TABLES", "DESCRIBE U", "PRAGMA table_info(U)", "EXEC sp_help",
                    "EXECUTE sp_help", "CALL x()", "SELECT ID INTO #t FROM U", "SELECT ID INTO Q5 FROM U", "SELECT ID INTO Q5 FROM U", "SELECT ID INTO Q7 FROM U WHERE 1 = 0", "ANALYZE", "VACUUM", "LOCK TABLE U IN EXCLUSIVE MODE",
                    "DECLARE @x INT", "PRINT 1", "CREATE SEQUENCE s", "CREATE TRIGGER t ON U", "CREATE FUNCTION f()", "CREATE SCHEMA s", "COMMENT ON TABLE U IS 'x'", "SELECT 1 AS v FROM One", "ALTER TABLE U ADD COLUMN C2 LONG; "]);
                TableNames(p, n);
            }, writable: true);
        yield return Read("sql-querydef-create-texts", ["R-SQL-1", "SQL-B", "CAT-Q"],
            "Asks, on a writable copy, what Database.CreateQueryDef(name, sql) keeps and refuses for SQL texts of many shapes (spacing and case variants, brackets and qualifiers, one and two trailing semicolons, CR/LF, comment forms, an unknown table or column or function, a syntax error, empty, blank and semicolon-only text, every statement family without running it, a declaration, an append, make-table, DDL, crosstab and union text, a reference to another QueryDef, and an embedded accent): after each create, the SQL property exactly, Type and ReturnsRecords; then the Database is closed and reopened and each QueryDef's SQL is read again by name.",
            (p, n) =>
            {
                string[] texts =
                [
                    "select  id   from   one", "SELECT [ID] FROM [One]", "SELECT One.ID FROM One", "SELECT ID FROM One;", "SELECT ID FROM One ; ", "SELECT ID FROM One\r\n", "SELECT ID\r\nFROM One\r\n", "SELECT * FROM One",
                    "SELECT ID AS ID FROM One", "SELECT ID FROM One WHERE ID=1 AND ID<>2", "SELECT ID FROM One WHERE (ID=1)", "SELECT ID FROM One WHERE ((ID=1))", "SELECT \"a\" AS v FROM One", "SELECT 'a' AS v FROM One",
                    "SELECT ID FROM One /* c */", "SELECT ID FROM One -- c", "SELECT ID FROM Nope", "SELEC ID", "", " ", ";", "SELECT 1", "SELECT Nope FROM One", "SELECT NoFunc(1) FROM One", "UPDATE One SET ID = 1",
                    "DELETE * FROM One", "INSERT INTO One (ID) VALUES (9)", "SELECT ID INTO Mk FROM One", "CREATE TABLE QT (A LONG)", "DROP TABLE Nope", "ALTER TABLE One ADD COLUMN X LONG",
                    "TRANSFORM Count(ID) SELECT ID FROM One GROUP BY ID PIVOT ID", "SELECT ID FROM One UNION SELECT ID FROM One", "PARAMETERS p Long; SELECT ID FROM One WHERE ID = p;",
                    "PARAMETERS p Long; UPDATE One SET ID = p", "SELECT ID FROM One WITH OWNERACCESS OPTION", "SELECT ID FROM One;SELECT ID FROM One", "SELECT ID FROM One WHERE ID = [Q0]", "SELECT 'caf\u00E9' AS v FROM One",
                    "SELECT ID FROM Q1", "SELECT ID FROM [Q2]",
                ];
                p.GetBind("qdfs", "db", "QueryDefs").Get("qdfs", "Count");
                for (int i = 0; i < texts.Length; i++)
                {
                    string q = n.Next("q");
                    p.CallBind(q, "db", "CreateQueryDef", A.Str($"Q{i}"), A.Str(texts[i])).Get(q, "SQL").Get(q, "Type").Get(q, "ReturnsRecords").Release(q);
                }
                p.Get("qdfs", "Count").Release("qdfs").Call("db", "Close").Release("db").OpenDb("db2", readOnly: true);
                string qs = n.Next("qs");
                p.GetBind(qs, "db2", "QueryDefs").Get(qs, "Count");
                for (int i = 0; i < texts.Length; i++)
                {
                    string q = n.Next("q");
                    p.Item(q, qs, A.Str($"Q{i}")).Get(q, "SQL").Release(q);
                }
                p.Release(qs).Call("db2", "Close");
            }, writable: true);
        yield return Read("sql-querydef-names", ["R-SQL-1", "SQL-B", "CAT-Q"],
            "Asks, on a writable copy, which names Database.CreateQueryDef(name, 'SELECT ID FROM One;') accepts, what Name then reads, and whether the query opens by that name: blank and padded names, names with a dot, bang, brackets, quotes, a semicolon, comma, wildcard, slash and colon, a leading tilde, hyphen, digit and #, the MSys and USys prefixes, 64 and 65 characters, non-Latin and astral characters, an embedded U+0000, tab and CR/LF, and the name of a table (One) and of a QueryDef differing only in case. At the end the QueryDefs Count and every name are read.",
            (p, n) =>
            {
                string[] names = [" ", "q ", " q", "q.q", "q!q", "q[1]", "q]q", "q\"q", "q'q", "~q", "~sq_x", "MSysQ", "USysQ", new string('Q', 64), new string('Q', 65), "Q\u00E4\u20AC", "Q\U0001D11E", "q\0q", "q\tq",
                    "q\r\nq", "One", "ONE", "q", "Q", "[q]", "q;q", "q,q", "q*q", "q?q", "q%q", "-q", "1q", "#q", "q\\q", "q/q", "q:q", "q|q"];
                foreach (string name in names)
                {
                    string q = n.Next("q"), rs = n.Next("rs");
                    p.CallBind(q, "db", "CreateQueryDef", A.Str(name), A.Str("SELECT ID FROM One;")).Get(q, "Name").Release(q);
                    p.OpenRs(rs, "db", name, Dao.OpenSnapshot);
                    Rows(p, rs, 4);
                    p.Call(rs, "Close").Release(rs);
                }
                p.GetBind("qdfs", "db", "QueryDefs").Call("qdfs", "Refresh").Get("qdfs", "Count")
                    .Each("qdfs", "qx", 200, x => x.Get("qx", "Name")).Release("qdfs");
            }, writable: true);

        // R-SQL-2: DDL ---------------------------------------------------------------------------------------------------
        yield return Read("ddl-type-names-accdb", ["R-SQL-2", "SQL-ACT-DDL", "R-CAT-1", "CAT-D", "R-FMT-1"],
            "Asks, on a writable ACCDB copy, which column type spellings CREATE TABLE accepts through Database.Execute (dbFailOnError) and which DAO Field Type, Size and Attributes each gives: the Jet names (BYTE, SHORT, LONG, COUNTER, SINGLE, DOUBLE, CURRENCY, DATETIME, TEXT, MEMO, YESNO, GUID, BINARY, LONGBINARY) and the synonyms of other systems (TINYINT, SMALLINT, INT, INTEGER, REAL, FLOAT, MONEY, DATE, TIME, TIMESTAMP, CHAR, VARCHAR, NCHAR, NVARCHAR, NTEXT, LONGTEXT, LONGCHAR, BIT, BOOLEAN, LOGICAL, UNIQUEIDENTIFIER, IMAGE, OLEOBJECT, VARBINARY, BIGINT, DATETIME2 and the long spellings), with and without a length and at 0, 255 and 256. Each statement is CREATE TABLE Dt<i> (K LONG, C <type>); DBEngine.Errors follows, then each table's fields are read.",
            (p, n) => TypeNameMatrix(p, n), writable: true);
        yield return Read("ddl-type-names-mdb", ["R-SQL-2", "SQL-ACT-DDL", "R-CAT-1", "CAT-D", "R-FMT-1"],
            "The same type-name matrix as ddl-type-names-accdb on a writable Jet 4 (MDB) copy, so that ACCDB and MDB acceptance and the DAO Field Type, Size and Attributes can be compared.",
            (p, n) => TypeNameMatrix(p, n), writable: true, fixture: Mdb);
        yield return Read("ddl-column-clauses", ["R-SQL-2", "SQL-ACT-DDL", "R-CAT-1", "CAT-D"],
            "Asks, on a writable copy, which column and table clauses CREATE TABLE accepts through Database.Execute (dbFailOnError), each statement its own table Cc<i> followed by DBEngine.Errors: NOT NULL and NULL, DEFAULT, column-level and table-level PRIMARY KEY (single, composite, with direction, repeated and mixed), UNIQUE in column and table form, an INDEX clause, CHECK in column and table form, a trailing comma, no columns, no column list, a repeated column name in two cases, a missing type, a missing comma, COLLATE, COMMENT, AUTO_INCREMENT, IDENTITY, UNSIGNED, REFERENCES in column and table form with and without a column list, and a computed-column spelling. Then for each created table the fields (Name, Type, Attributes, Required, AllowZeroLength) and indexes (Name, Primary, Unique, IgnoreNulls, Required) are read.",
            (p, n) =>
            {
                string[] sql =
                [
                    "CREATE TABLE Cc0 (A LONG NOT NULL)", "CREATE TABLE Cc1 (A LONG NULL)", "CREATE TABLE Cc2 (A LONG DEFAULT 5)", "CREATE TABLE Cc3 (A TEXT(10) DEFAULT 'x')", "CREATE TABLE Cc4 (A LONG NOT NULL DEFAULT 5)",
                    "CREATE TABLE Cc5 (A LONG CONSTRAINT PK_Cc5 PRIMARY KEY)", "CREATE TABLE Cc6 (A LONG PRIMARY KEY)", "CREATE TABLE Cc7 (A LONG, B LONG, CONSTRAINT PK_Cc7 PRIMARY KEY (A, B))",
                    "CREATE TABLE Cc8 (A LONG, B LONG, CONSTRAINT PK_Cc8 PRIMARY KEY (A DESC, B ASC))", "CREATE TABLE Cc9 (A LONG, B LONG, UNIQUE (A, B))", "CREATE TABLE Cc10 (A LONG UNIQUE, B LONG)",
                    "CREATE TABLE Cc11 (A LONG NOT NULL UNIQUE)", "CREATE TABLE Cc12 (A LONG PRIMARY KEY, B LONG PRIMARY KEY)", "CREATE TABLE Cc13 (A LONG PRIMARY KEY, CONSTRAINT PK2 PRIMARY KEY (A))",
                    "CREATE TABLE Cc14 (A LONG, INDEX IX_A (A))", "CREATE TABLE Cc15 (A LONG, B LONG, CONSTRAINT CK_15 CHECK (A > B))", "CREATE TABLE Cc16 (A LONG CHECK (A > 0))", "CREATE TABLE Cc17 (A LONG,)",
                    "CREATE TABLE Cc18 ()", "CREATE TABLE Cc19", "CREATE TABLE Cc20 (A LONG, A LONG)", "CREATE TABLE Cc21 (A LONG, a LONG)", "CREATE TABLE Cc22 (A)", "CREATE TABLE Cc23 (A LONG B LONG)",
                    "CREATE TABLE Cc24 (A TEXT(10) COLLATE X)", "CREATE TABLE Cc25 (A LONG COMMENT 'x')", "CREATE TABLE Cc26 (A LONG AUTO_INCREMENT)", "CREATE TABLE Cc27 (A LONG IDENTITY)",
                    "CREATE TABLE Cc28 (A LONG UNSIGNED)", "CREATE TABLE Cc29 (A LONG REFERENCES One (ID))", "CREATE TABLE Cc30 (A LONG REFERENCES One)", "CREATE TABLE Cc31 (A LONG, FOREIGN KEY (A) REFERENCES One)",
                    "CREATE TABLE Cc32 (A LONG GENERATED ALWAYS AS (1))", "CREATE TABLE Cc33 (A LONG, B AS (A + 1))", "CREATE TABLE Cc34 (A LONG NOT NULL NULL)", "CREATE TABLE Cc35 (A LONG, B LONG, PRIMARY KEY (A) PRIMARY KEY (B))",
                ];
                Exs(p, sql);
                p.GetBind("cctdfs", "db", "TableDefs").Call("cctdfs", "Refresh");
                for (int i = 0; i < sql.Length; i++)
                {
                    string t = n.Next("cct"), fs = t + "f", f = t + "x", ixs = t + "i", ix = t + "y";
                    p.Item(t, "cctdfs", A.Str($"Cc{i}")).GetBind(fs, t, "Fields")
                        .Each(fs, f, 8, x => x.Get(f, "Name").Get(f, "Type").Get(f, "Attributes").Get(f, "Required").Get(f, "AllowZeroLength")).Release(fs)
                        .GetBind(ixs, t, "Indexes")
                        .Each(ixs, ix, 8, x => x.Get(ix, "Name").Get(ix, "Primary").Get(ix, "Unique").Get(ix, "IgnoreNulls").Get(ix, "Required")).Release(ixs).Release(t);
                }
                p.Release("cctdfs");
            }, writable: true);
        yield return Read("ddl-counter-seed-increment", ["R-SQL-2", "SQL-ACT-DDL", "R-FMT-2", "R-CAT-1"],
            "Asks, on a writable copy, how COUNTER and AUTOINCREMENT and IDENTITY accept a seed and increment through Database.Execute (dbFailOnError) and what they generate: seeds 0, 1, 10, 100, negative, 2147483647 and fractional, increments 1, 5, 0 and -1, a single-argument form, two counters in one table. Each table Cs<i> is created, two rows are inserted without the counter, a row with the counter set to 50, then one more without it, and the rows are read ordered by the counter; DBEngine.Errors follows each statement that may fail.",
            (p, n) =>
            {
                string[] defs = ["A COUNTER, B LONG", "A COUNTER(100, 5), B LONG", "A AUTOINCREMENT(10, 1), B LONG", "A COUNTER(0, 1), B LONG", "A COUNTER(1, 0), B LONG", "A COUNTER(1, -1), B LONG",
                    "A COUNTER(-5, 2), B LONG", "A COUNTER(2147483647, 1), B LONG", "A COUNTER(1.5, 1), B LONG", "A COUNTER(1), B LONG", "A IDENTITY(7, 3), B LONG", "A COUNTER(1, 1), C COUNTER(1, 1), B LONG"];
                for (int i = 0; i < defs.Length; i++)
                {
                    Ex(p, $"CREATE TABLE Cs{i} ({defs[i]})");
                    Ex(p, $"INSERT INTO Cs{i} (B) VALUES (1)", false);
                    Ex(p, $"INSERT INTO Cs{i} (B) VALUES (2)", false);
                    Ex(p, $"INSERT INTO Cs{i} (A, B) VALUES (50, 3)");
                    Ex(p, $"INSERT INTO Cs{i} (B) VALUES (4)", false);
                    Sel(p, n, $"SELECT * FROM Cs{i} ORDER BY B");
                }
            }, writable: true);
        yield return Read("ddl-fk-actions", ["R-SQL-2", "SQL-ACT-DDL", "R-CAT-1", "CAT-D"],
            "Asks, on a writable copy, which referential-action spellings ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY accepts through Database.Execute (dbFailOnError) and what they do: none, ON DELETE and ON UPDATE with CASCADE, SET NULL, NO ACTION, SET DEFAULT and RESTRICT, both clauses together and a repeated clause. For each variant i the parent Fp<i> (rows 1, 2) and child Fc<i> (rows 10 -> 1, 11 -> 2) are created, then the parent row 1 is deleted and the parent row 2 renumbered to 9, and both tables are read. Further forms: NO INDEX, a missing referenced column list, a repeated referenced column, an unknown parent and column, a non-unique parent column, a self reference and a repeated constraint name. Finally the Relations (Name, Table, ForeignTable, Attributes) are read.",
            (p, n) =>
            {
                string[] actions = ["", "ON DELETE CASCADE", "ON UPDATE CASCADE", "ON DELETE SET NULL", "ON UPDATE SET NULL", "ON DELETE NO ACTION", "ON UPDATE NO ACTION", "ON DELETE SET DEFAULT",
                    "ON DELETE CASCADE ON UPDATE CASCADE", "ON UPDATE CASCADE ON DELETE SET NULL", "ON DELETE RESTRICT", "ON DELETE CASCADE ON DELETE CASCADE"];
                for (int i = 0; i < actions.Length; i++)
                {
                    Exs(p, [$"CREATE TABLE Fp{i} (ID LONG CONSTRAINT PK_Fp{i} PRIMARY KEY)", $"CREATE TABLE Fc{i} (CID LONG CONSTRAINT PK_Fc{i} PRIMARY KEY, PID LONG)",
                        $"ALTER TABLE Fc{i} ADD CONSTRAINT FK_{i} FOREIGN KEY (PID) REFERENCES Fp{i} (ID) {actions[i]}"]);
                    Exs(p, [$"INSERT INTO Fp{i} (ID) VALUES (1)", $"INSERT INTO Fp{i} (ID) VALUES (2)", $"INSERT INTO Fc{i} (CID, PID) VALUES (10, 1)", $"INSERT INTO Fc{i} (CID, PID) VALUES (11, 2)"], false);
                    Exs(p, [$"DELETE FROM Fp{i} WHERE ID = 1", $"UPDATE Fp{i} SET ID = 9 WHERE ID = 2"]);
                    Sel(p, n, $"SELECT CID, PID FROM Fc{i} ORDER BY CID");
                    Sel(p, n, $"SELECT ID FROM Fp{i} ORDER BY ID");
                }
                Exs(p, ["CREATE TABLE Fx0 (CID LONG, PID LONG, CONSTRAINT FK_x0 FOREIGN KEY NO INDEX (PID) REFERENCES Fp1 (ID))", "CREATE TABLE Fx1 (CID LONG, PID LONG, CONSTRAINT FK_x1 FOREIGN KEY (PID) REFERENCES Fp1)",
                    "CREATE TABLE Fx2 (CID LONG, PID LONG, CONSTRAINT FK_x2 FOREIGN KEY (PID) REFERENCES Fp1 (ID, ID))", "CREATE TABLE Fx3 (CID LONG, PID LONG, CONSTRAINT FK_x3 FOREIGN KEY (PID) REFERENCES Nope (ID))",
                    "CREATE TABLE Fx4 (CID LONG, PID LONG, CONSTRAINT FK_x4 FOREIGN KEY (PID) REFERENCES Fp1 (Nope))", "CREATE TABLE Fx5 (CID LONG, PID LONG, CONSTRAINT FK_x5 FOREIGN KEY (PID) REFERENCES Fc1 (PID))",
                    "CREATE TABLE Fx6 (CID LONG CONSTRAINT PK_Fx6 PRIMARY KEY, PID LONG, CONSTRAINT FK_x6 FOREIGN KEY (PID) REFERENCES Fx6 (CID))",
                    "CREATE TABLE Fx7 (CID LONG, PID LONG, CONSTRAINT FK_x0 FOREIGN KEY (PID) REFERENCES Fp1 (ID))", "CREATE TABLE Fx8 (CID LONG, PID LONG, CONSTRAINT FK_x8 FOREIGN KEY (PID, CID) REFERENCES Fp1 (ID))"]);
                p.GetBind("rels", "db", "Relations").Call("rels", "Refresh").Get("rels", "Count")
                    .Each("rels", "rel", 64, x => x.Get("rel", "Name").Get("rel", "Table").Get("rel", "ForeignTable").Get("rel", "Attributes")).Release("rels");
            }, writable: true);
        yield return Read("ddl-reserved-column-names", ["R-SQL-2", "SQL-ACT-DDL", "R-CAT-1", "CAT-D", "SQL-B"],
            "Asks, on a writable copy, which unbracketed words CREATE TABLE refuses as a column name through Database.Execute (dbFailOnError): each statement is CREATE TABLE Rc<i> (<word> LONG) for the words listed in the probe (SQL keywords, type names, function names and constants such as Name, Date, Text, Value, Order, Group, Table, Count, Year, Type, Level, Key, User, Time, Note, Desc, Min, Null, True, Password, Owner, Comment, Description, Field, Column, Row, Data, Primary, Check, Constraint, Create, Drop, Alter, Add, Update, Delete, Insert, Values, Set, Join, On, As, By, Is, In, Like, Not, And, Or, All, Any, Union, Distinct, Top), with no DBEngine.Errors read between them. At the end the TableDefs names are listed, so that refused words are the missing tables.",
            (p, n) =>
            {
                for (int i = 0; i < ReservedWords.Length; i++)
                {
                    Ex(p, $"CREATE TABLE Rc{i} ({ReservedWords[i]} LONG)", false);
                }
                TableNames(p, n);
            }, writable: true);
        yield return Read("ddl-reserved-table-names", ["R-SQL-2", "SQL-ACT-DDL", "R-CAT-1", "CAT-D", "SQL-B"],
            "Asks, on a writable copy, which unbracketed words CREATE TABLE refuses as a table name through Database.Execute (dbFailOnError): each statement is CREATE TABLE <word> (A LONG) for the same words as ddl-reserved-column-names, with no DBEngine.Errors read between them. At the end the TableDefs names are listed, so that refused words are the missing tables.",
            (p, n) =>
            {
                foreach (string w in ReservedWords)
                {
                    Ex(p, $"CREATE TABLE {w} (A LONG)", false);
                }
                TableNames(p, n);
            }, writable: true);
        yield return Read("ddl-name-and-size-limits", ["R-SQL-2", "SQL-ACT-DDL", "SQL-B", "R-CAT-1", "CAT-D"],
            "Asks, on a writable copy, what limits and spellings CREATE and ALTER accept through Database.Execute (dbFailOnError), each followed by DBEngine.Errors where it may fail: table, column, index and constraint names of 64 and 65 characters; tables of 255, 256 and 300 Long columns; tables of 8, 16 and 17 Text(255) columns with a row inserted whose every column holds 255 characters (and 255 euro-sign characters for 8); an index over 10 and over 11 columns; 32 and then 33 single-column indexes on one table; bracketed names with a space, dot, bang, escaped bracket, edge spaces, empty and blank text, U+0000, tab, CR/LF, accents, an astral pair, a lone surrogate, a tilde, an MSys and a USys prefix and punctuation; and a table created twice, in another case and over an existing table. The TableDefs names are listed at the end.",
            (p, n) =>
            {
                string t64 = new('T', 64), t65 = new('T', 65);
                Exs(p, [$"CREATE TABLE {t64} (A LONG)", $"CREATE TABLE {t65} (A LONG)", $"CREATE TABLE Nl1 ({new string('C', 64)} LONG)", $"CREATE TABLE Nl2 ({new string('C', 65)} LONG)",
                    $"CREATE INDEX {new string('I', 64)} ON Dm (N)", $"CREATE INDEX {new string('I', 65)} ON Dm (N)", $"CREATE TABLE Nl3 (A LONG, CONSTRAINT {new string('K', 64)} UNIQUE (A))",
                    $"CREATE TABLE Nl4 (A LONG, CONSTRAINT {new string('K', 65)} UNIQUE (A))"]);
                foreach (int cols in new[] { 255, 256, 300 })
                {
                    Ex(p, $"CREATE TABLE Wc{cols} ({string.Join(", ", Enumerable.Range(1, cols).Select(i => $"c{i} LONG"))})");
                }
                foreach (int cols in new[] { 8, 16, 17 })
                {
                    Ex(p, $"CREATE TABLE Wt{cols} ({string.Join(", ", Enumerable.Range(1, cols).Select(i => $"c{i} TEXT(255)"))})");
                    Ex(p, $"INSERT INTO Wt{cols} ({string.Join(", ", Enumerable.Range(1, cols).Select(i => $"c{i}"))}) VALUES ({string.Join(", ", Enumerable.Repeat("String(255, 'x')", cols))})");
                }
                Ex(p, "INSERT INTO Wt8 (c1, c2, c3, c4, c5, c6, c7, c8) VALUES (String(255, ChrW(8364)), String(255, ChrW(8364)), String(255, ChrW(8364)), String(255, ChrW(8364)), String(255, ChrW(8364)), String(255, ChrW(8364)), String(255, ChrW(8364)), String(255, ChrW(8364)))");
                Ex(p, $"CREATE TABLE Wi ({string.Join(", ", Enumerable.Range(1, 12).Select(i => $"c{i} LONG"))})");
                Ex(p, $"CREATE INDEX IX_W10 ON Wi ({string.Join(", ", Enumerable.Range(1, 10).Select(i => $"c{i}"))})");
                Ex(p, $"CREATE INDEX IX_W11 ON Wi ({string.Join(", ", Enumerable.Range(1, 11).Select(i => $"c{i}"))})");
                Ex(p, $"CREATE TABLE Wx ({string.Join(", ", Enumerable.Range(1, 40).Select(i => $"c{i} LONG"))})");
                for (int i = 1; i <= 33; i++)
                {
                    Ex(p, $"CREATE INDEX IX_X{i} ON Wx (c{i})");
                }
                foreach (string name in new[] { "[a b]", "[a.b]", "[a!b]", "[a]]b]", "[ a]", "[a ]", "[]", "[ ]", "[a\0b]", "[a\tb]", "[a\r\nb]", "[T\u00E4\u20AC]", "[T\U0001D11E]", "[T\uD800]", "[~T]", "[MSysT]", "[USysT]",
                    "[T;]", "[T,]", "[T*]", "[T?]", "[T#]", "[T%]", "[T'T]", "[T\"T]", "[T`T]", "[T\\T]", "[T/T]" })
                {
                    Ex(p, $"CREATE TABLE {name} (A LONG)");
                }
                Exs(p, ["CREATE TABLE Dup1 (A LONG)", "CREATE TABLE Dup1 (A LONG)", "CREATE TABLE DUP1 (A LONG)", "CREATE TABLE One (A LONG)"]);
                TableNames(p, n);
            }, writable: true);
        yield return Read("ddl-alter-column-forms", ["R-SQL-2", "SQL-ACT-DDL", "R-CAT-1", "CAT-D"],
            "Asks, on a writable copy, which ALTER TABLE spellings Database.Execute (dbFailOnError) accepts over At (ID Long primary key, A Text(10) holding 'abc', 'def', Null; B Long holding 1, 2, 3), each followed by DBEngine.Errors and the rows of At: ADD with and without COLUMN, NOT NULL, DEFAULT, several columns in one statement, a second COLUMN keyword, COUNTER on a table with rows (twice), UNIQUE, a duplicate name; ALTER COLUMN to COUNTER, NOT NULL, NULL, DEFAULT, SET DEFAULT, DROP DEFAULT, and to Long over text, Text, Memo, Double, Yes/No and Date/Time over Long, on the primary key and on an unknown column; DROP with and without COLUMN, twice, on the primary key and with two names; ADD PRIMARY KEY, ADD and DROP PRIMARY KEY and CONSTRAINT, ADD UNIQUE and INDEX, a REFERENCES column, an unknown table, and truncated statements.",
            (p, n) =>
            {
                Exs(p, ["CREATE TABLE At (ID LONG CONSTRAINT PK_At PRIMARY KEY, A TEXT(10), B LONG)", "INSERT INTO At (ID, A, B) VALUES (1, 'abc', 1)", "INSERT INTO At (ID, A, B) VALUES (2, 'def', 2)", "INSERT INTO At (ID, B) VALUES (3, 3)"], false);
                foreach (string sql in new[] { "ALTER TABLE At ADD X1 LONG", "ALTER TABLE At ADD COLUMN X2 LONG NOT NULL", "ALTER TABLE At ADD COLUMN X3 LONG DEFAULT 7", "ALTER TABLE At ADD COLUMN X4 LONG, X5 LONG",
                    "ALTER TABLE At ADD COLUMN X6 LONG, COLUMN X7 LONG", "ALTER TABLE At ADD COLUMN X8 COUNTER", "ALTER TABLE At ADD COLUMN X9 COUNTER", "ALTER TABLE At ADD COLUMN XA TEXT(5) UNIQUE",
                    "ALTER TABLE At ADD COLUMN ID LONG", "ALTER TABLE At ALTER COLUMN B COUNTER", "ALTER TABLE At ALTER COLUMN B LONG NOT NULL", "ALTER TABLE At ALTER COLUMN B LONG NULL",
                    "ALTER TABLE At ALTER COLUMN B DEFAULT 5", "ALTER TABLE At ALTER COLUMN B SET DEFAULT 5", "ALTER TABLE At ALTER COLUMN B DROP DEFAULT", "ALTER TABLE At ALTER COLUMN A LONG",
                    "ALTER TABLE At ALTER COLUMN B TEXT(10)", "ALTER TABLE At ALTER COLUMN B MEMO", "ALTER TABLE At ALTER COLUMN B DOUBLE", "ALTER TABLE At ALTER COLUMN B YESNO", "ALTER TABLE At ALTER COLUMN B DATETIME",
                    "ALTER TABLE At ALTER COLUMN ID COUNTER", "ALTER TABLE At ALTER COLUMN Nope LONG", "ALTER TABLE At DROP X1", "ALTER TABLE At DROP COLUMN X2", "ALTER TABLE At DROP COLUMN X2",
                    "ALTER TABLE At DROP COLUMN ID", "ALTER TABLE At DROP COLUMN X3, X4", "ALTER TABLE At ADD PRIMARY KEY (A)", "ALTER TABLE At ADD CONSTRAINT PK_At2 PRIMARY KEY (A)", "ALTER TABLE At DROP PRIMARY KEY",
                    "ALTER TABLE At DROP CONSTRAINT PK_At", "ALTER TABLE At ADD CONSTRAINT UQ_A UNIQUE (A)", "ALTER TABLE At ADD UNIQUE (B)", "ALTER TABLE At ADD INDEX IXB (B)", "ALTER TABLE At ADD COLUMN XB LONG REFERENCES One (ID)",
                    "ALTER TABLE Nope ADD COLUMN Z LONG", "ALTER TABLE At", "ALTER TABLE At ADD", "ALTER TABLE At ADD COLUMN Q LONG," })
                {
                    Ex(p, sql);
                    Sel(p, n, "SELECT * FROM At ORDER BY ID");
                }
            }, writable: true);
        yield return Read("ddl-index-forms", ["R-SQL-2", "SQL-ACT-DDL", "R-CAT-1", "CAT-D"],
            "Asks, on a writable copy, which CREATE INDEX and DROP INDEX spellings Database.Execute (dbFailOnError) accepts over Dm, each followed by DBEngine.Errors: single and compound keys with ASC and DESC, UNIQUE, WITH PRIMARY twice, DISALLOW NULL and IGNORE NULL together, no index name, an unknown column and table, a Memo, Yes/No and Double key, the same index name on two tables and the name of a table, CLUSTERED, HASH, FULLTEXT and USING forms, a repeated column, DROP INDEX with and without ON and on an unknown table. At the end the indexes of Dm (Name, Primary, Unique, IgnoreNulls, Required, Clustered) are read.",
            (p, n) =>
            {
                Exs(p, ["CREATE INDEX ix1 ON Dm (N)", "CREATE INDEX ix2 ON Dm (G ASC, N DESC)", "CREATE UNIQUE INDEX ix3 ON Dm (ID, S)", "CREATE INDEX ix4 ON Dm (V) WITH PRIMARY", "CREATE INDEX ix5 ON Dm (N) WITH PRIMARY",
                    "CREATE INDEX ix6 ON Dm (S) WITH IGNORE NULL DISALLOW NULL", "CREATE INDEX ix7 ON Dm (S) WITH DISALLOW NULL IGNORE NULL", "CREATE INDEX ON Dm (DT)", "CREATE INDEX ix8 ON Dm (Nope)", "CREATE INDEX ix9 ON Nope (N)",
                    "CREATE INDEX ixa ON Tx (M)", "CREATE INDEX ixb ON Tx (T)", "CREATE INDEX ix1 ON Tx (ID)", "CREATE INDEX Tx ON Dm (G)", "CREATE CLUSTERED INDEX ixc ON Dm (G)", "CREATE HASH INDEX ixd ON Dm (G)",
                    "CREATE FULLTEXT INDEX ixe ON Dm (S)", "CREATE INDEX ixf ON Dm (S) USING BTREE", "CREATE INDEX ixg ON Dm (N, N)", "CREATE INDEX ixh ON Dm (N DESC DESC)", "CREATE INDEX ixi ON Dm ()", "CREATE INDEX ixj ON Dm",
                    "DROP INDEX ix1 ON Dm", "DROP INDEX ix1 ON Dm", "DROP INDEX ix2", "DROP INDEX ix2 ON Nope", "DROP INDEX ix1 ON Tx", "DROP INDEX PK_Dm ON Dm", "DROP INDEX PrimaryKey ON Dm"]);
                p.GetBind("ixtdfs", "db", "TableDefs").Call("ixtdfs", "Refresh").Item("ixt", "ixtdfs", A.Str("Dm")).GetBind("ixs", "ixt", "Indexes")
                    .Each("ixs", "ix", 40, x => x.Get("ix", "Name").Get("ix", "Primary").Get("ix", "Unique").Get("ix", "IgnoreNulls").Get("ix", "Required").Get("ix", "Clustered"))
                    .Release("ixs").Release("ixt").Release("ixtdfs");
            }, writable: true);
        yield return Read("ddl-drop-and-missing-objects", ["R-SQL-2", "SQL-ACT-DDL", "R-CAT-1", "CAT-D"],
            "Asks, on a writable copy, how DROP and ALTER ... DROP answer for missing and dependent objects through Database.Execute (dbFailOnError), each followed by DBEngine.Errors: DROP TABLE of an unknown table, with IF EXISTS, with two names, with a bracketed name, with CASCADE and RESTRICT, of a table that is the parent of a relationship (Dp1 over Dp2) and then of the child first; DROP INDEX of the primary key behind a relationship; DROP CONSTRAINT, DROP COLUMN, DROP VIEW and DROP PROCEDURE of an unknown name. The TableDefs names and the Relations count are read at the end.",
            (p, n) =>
            {
                Exs(p, ["CREATE TABLE Dp1 (A LONG CONSTRAINT PK_Dp1 PRIMARY KEY)", "CREATE TABLE Dp2 (B LONG, CONSTRAINT FK_Dp2 FOREIGN KEY (B) REFERENCES Dp1 (A))", "DROP TABLE Nope", "DROP TABLE IF EXISTS Nope",
                    "DROP TABLE Dp1", "DROP INDEX PK_Dp1 ON Dp1", "ALTER TABLE Dp1 DROP CONSTRAINT PK_Dp1", "ALTER TABLE Dp2 DROP CONSTRAINT Nope", "ALTER TABLE Dp2 DROP COLUMN Nope", "DROP VIEW Nope", "DROP PROCEDURE Nope",
                    "DROP TABLE Dp1 RESTRICT", "DROP TABLE Dp1 CASCADE", "DROP TABLE Dp2", "DROP TABLE Dp1", "DROP TABLE [Tx]", "DROP TABLE U, One", "DROP TABLE One"]);
                TableNames(p, n);
                p.GetBind("dprels", "db", "Relations").Call("dprels", "Refresh").Get("dprels", "Count").Release("dprels");
            }, writable: true);

        // R-SQL-3: crosstab syntax and parameter names ---------------------------------------------------------------------
        yield return Read("crosstab-syntax-errors", ["R-SQL-3", "SQL-PIVOT-01", "SQL-PIVOT-02", "SQL-B"],
            "Asks which TRANSFORM ... PIVOT spellings parse over Dm and with what Fields and rows or error: a missing PIVOT, TRANSFORM or aggregate, a missing GROUP BY, two PIVOT columns or clauses, IN () and IN with Null and mixed types, the pivot column also a row heading, an arithmetic expression over aggregates and an alias in TRANSFORM, TOP, DISTINCT, HAVING, WHERE and ORDER BY (before and after PIVOT) around it, parentheses and a subquery around the crosstab, UNION, WITH OWNERACCESS OPTION, a constant PIVOT, and two IN lists. Each text is its own snapshot, followed by DBEngine.Errors.",
            (p, n) => Sels(p, n, ["TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT S", "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G", "SELECT G FROM Dm GROUP BY G PIVOT S", "TRANSFORM N SELECT G FROM Dm GROUP BY G PIVOT S",
                "TRANSFORM Count(*) SELECT G FROM Dm PIVOT S", "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT S, N", "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT S PIVOT N",
                "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT S IN ()", "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT S IN (Null)", "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT S IN ('x', Null)",
                "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT S IN ('x', 1)", "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT S IN ('x') IN ('y')", "TRANSFORM Count(*) SELECT G, S FROM Dm GROUP BY G, S PIVOT S",
                "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT G", "TRANSFORM Sum(N) * 2 SELECT G FROM Dm GROUP BY G PIVOT S", "TRANSFORM Sum(N) / Count(*) SELECT G FROM Dm GROUP BY G PIVOT S",
                "TRANSFORM Count(*) AS c SELECT G FROM Dm GROUP BY G PIVOT S", "TRANSFORM Count(*) SELECT TOP 2 G FROM Dm GROUP BY G PIVOT S", "TRANSFORM Count(*) SELECT DISTINCT G FROM Dm GROUP BY G PIVOT S",
                "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G HAVING Count(*) > 1 PIVOT S", "TRANSFORM Count(*) SELECT G FROM Dm WHERE N > 10 GROUP BY G ORDER BY G DESC PIVOT S",
                "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT S ORDER BY G", "(TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT S)", "SELECT * FROM (TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT S)",
                "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT S UNION SELECT G, 0 FROM Dm", "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT S WITH OWNERACCESS OPTION",
                "TRANSFORM Count(*) SELECT G AS [S] FROM Dm GROUP BY G PIVOT S", "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT ''", "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT Null",
                "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT 1", "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT 'a'", "TRANSFORM Count(*) SELECT G FROM Dm GROUP BY G PIVOT S & ''",
                "TRANSFORM Count(*) SELECT G, Count(*) AS n FROM Dm GROUP BY G PIVOT S"]));
        yield return Read("params-undeclared-host-names", ["R-SQL-3", "SQL-B", "EXPR-BIND"],
            "Asks how an undeclared name in a WHERE clause is classified when it is spelt like a host-application reference or a built-in: [x y], x.y, [x.y], x!y, Forms!F!C in three bracket spellings, TempVars!x, Screen.ActiveForm.Name, Reports![R]![C], CurrentUser with and without parentheses and brackets, Me.x, Date, Now and Rnd bare and bracketed, Null and True bracketed, a column name bracketed, a function call that does not exist and the same in brackets, and a string literal. For each a temporary QueryDef over Dm is created, its Parameters (Name, Type, Direction) are read and a snapshot is opened without assigning anything, with DBEngine.Errors read; nothing can prompt, because DAO reports an unresolved name as an error.",
            (p, n) =>
            {
                foreach (string name in new[] { "[x y]", "x.y", "[x.y]", "x!y", "[Forms]![F]![C]", "Forms!F!C", "[Forms]![F]!C", "TempVars!x", "[TempVars]![x]", "Screen.ActiveForm.Name", "Reports![R]![C]", "CurrentUser", "CurrentUser()",
                    "[CurrentUser]", "Me.x", "[Me].x", "Date", "[Date]", "Now", "[Now]", "Rnd", "[Rnd]", "[Null]", "[True]", "[ID]", "[Dm.ID]", "[Dm].[ID]", "NoSuch(1)", "[NoSuch(1)]", "'NoSuch'", "[NoSuch].[ID]", "[Dm]![NoSuch]" })
                {
                    string qd = n.Next("qd"), qp = qd + "p", qx = qd + "x", rs = n.Next("rs");
                    p.CallBind(qd, "db", "CreateQueryDef", A.Str(""), A.Str($"SELECT ID FROM Dm WHERE ID = {name};")).GetBind(qp, qd, "Parameters")
                        .Each(qp, qx, 8, x => x.Get(qx, "Name").Get(qx, "Type").Get(qx, "Direction")).Release(qp)
                        .CallBind(rs, qd, "OpenRecordset", A.I4(Dao.OpenSnapshot));
                    Rows(p, rs, 8);
                    p.Call(rs, "Close").Release(rs).Release(qd).Errors();
                }
            });

        // R-STRING-1: string and BSTR edge values through SQL ------------------------------------------------------------------
        yield return Read("str-stored-length-and-codes", ["R-STRING-1", "II-02", "R-EXPR-1", "EXPR-INVENTORY"],
            "Asks what SQL reports for the stored edge strings of Tx (38 rows in the Text(255) column T and the Memo column M: embedded and leading and trailing U+0000, odd-byte BSTRs of 1 and 3 bytes, lone high and low surrogates in both orders, an astral pair, a leading U+FEFF, U+FFFE, trailing space, no-break and zero-width spaces, a soft hyphen, compatibility forms, an empty string and Null): Len, LenB, Asc, AscW and AscB (rows with Len(T) > 0 only), VarType, TypeName and IsNull, Left, Right and Mid and their Len, and the plain T and M values (exact BSTR bytes). Each form is one snapshot ordered by ID, followed by DBEngine.Errors.",
            (p, n) =>
            {
                foreach (string q in new[] { "SELECT ID, Len(T) AS l, LenB(T) AS lb, Len(M) AS lm, LenB(M) AS lbm FROM Tx ORDER BY ID", "SELECT ID, Asc(T) AS v FROM Tx WHERE Len(T) > 0 ORDER BY ID",
                    "SELECT ID, AscW(T) AS v FROM Tx WHERE Len(T) > 0 ORDER BY ID", "SELECT ID, AscB(T) AS v FROM Tx WHERE Len(T) > 0 ORDER BY ID", "SELECT ID, VarType(T) AS vt, TypeName(T) AS tn, IsNull(T) AS n, T = '' AS e FROM Tx ORDER BY ID",
                    "SELECT ID, Left(T, 1) AS l1, Right(T, 1) AS r1, Mid(T, 2, 1) AS m2, Mid(T, 1, 100) AS mall FROM Tx ORDER BY ID", "SELECT ID, Len(Left(T, 1)) AS ll, LenB(Left(T, 1)) AS lbl, Len(Right(T, 1)) AS lr, LenB(Right(T, 1)) AS lbr FROM Tx ORDER BY ID",
                    "SELECT ID, ChrW(AscW(T)) AS c FROM Tx WHERE Len(T) > 0 ORDER BY ID", "SELECT ID, T FROM Tx ORDER BY ID", "SELECT ID, M FROM Tx ORDER BY ID", "SELECT ID, T & '' AS tc, M & '' AS mc, T & M AS tm FROM Tx ORDER BY ID" })
                {
                    Sel(p, n, q, true, maxRows: 64);
                }
                Meta(p, n, "SELECT T, M, T & '' AS tc, M & '' AS mc, Left(T, 1) AS lt, Left(M, 1) AS lm FROM Tx WHERE ID = 2");
            });
        yield return Read("str-stored-case-trim-reverse", ["R-STRING-1", "II-02", "R-EXPR-1", "EXPR-INVENTORY"],
            "Asks what the text functions return over the stored edge strings of Tx (rows with a non-Null T): UCase, LCase, StrReverse, Trim, LTrim, RTrim, Replace of 'a', of Chr(0), of a space and of U+0301, Space(Len(T)), and String(2, T). Each form is one snapshot ordered by ID, followed by DBEngine.Errors.",
            (p, n) =>
            {
                foreach (string e in new[] { "UCase(T)", "LCase(T)", "StrReverse(T)", "Trim(T)", "LTrim(T)", "RTrim(T)", "Replace(T, 'a', 'X')", "Replace(T, Chr(0), '_')", "Replace(T, ' ', '_')", "Replace(T, ChrW(769), '')",
                    "Space(Len(T))", "String(2, T)", "UCase(T) = LCase(T)", "UCase(LCase(T)) = UCase(T)", "Len(UCase(T)) = Len(T)", "StrReverse(StrReverse(T)) = T" })
                {
                    Sel(p, n, $"SELECT ID, {e} AS v FROM Tx WHERE T Is Not Null AND Len(T) > 0 ORDER BY ID", true, maxRows: 64);
                }
            });
        yield return Read("str-stored-instr-strcomp", ["R-STRING-1", "II-02", "R-EXPR-1", "EXPR-INVENTORY"],
            "Asks what InStr and StrComp return over the stored edge strings of Tx: InStr for 'a', 'b', Chr(0) and a space, in text and binary compare, from a start of 2, and InStr of the whole stored value inside itself; StrComp against 'a' and against 'A' in binary, text and database compare, and of each value against itself in the three modes. Each form is one snapshot ordered by ID over the rows with a non-Null T, followed by DBEngine.Errors.",
            (p, n) =>
            {
                foreach (string e in new[] { "InStr(T, 'a')", "InStr(T, 'b')", "InStr(T, Chr(0))", "InStr(T, ' ')", "InStr(1, T, 'A', 1)", "InStr(1, T, 'A', 0)", "InStr(2, T, 'a')", "InStr(T, T)", "InStr(1, T, T, 1)",
                    "StrComp(T, 'a', 0)", "StrComp(T, 'a', 1)", "StrComp(T, 'a', 2)", "StrComp(T, 'A', 0)", "StrComp(T, 'A', 1)", "StrComp(T, T, 0)", "StrComp(T, T, 1)", "StrComp(T, T, 2)", "StrComp(T, M, 0)" })
                {
                    Sel(p, n, $"SELECT ID, {e} AS v FROM Tx WHERE T Is Not Null ORDER BY ID", true, maxRows: 64);
                }
            });
        yield return Read("str-stored-where-equality", ["R-STRING-1", "II-02", "R-SQL-1", "SQL-EQ"],
            "Asks which Tx rows a WHERE equality (and LIKE) on T selects for each constructed comparand: 'a', 'A', 'a ' (trailing space), 'a' & Chr(0), Chr(0), the empty string, a space, 'ss', ChrW(223), ChrW(233), 'e' & ChrW(769), ChrW(65313), ChrW(55296), ChrW(56320), ChrW(55296) & ChrW(56320), ChrW(56320) & ChrW(55296), ChrW(8203), ChrW(173), ChrW(160), ChrW(65279) & 'bom', ChrW(197), ChrW(8491), ChrW(304), 'i', ChrW(305) and ChrW(12450) and ChrW(12354); and T Like 'a*', 'a?', 'a?b', '?', '[a-b]' and '*' & Chr(0) & '*'. Each form is one snapshot of the IDs, ordered, followed by DBEngine.Errors.",
            (p, n) =>
            {
                foreach (string c in new[] { "'a'", "'A'", "'a '", "'a' & Chr(0)", "Chr(0)", "''", "' '", "'ss'", "ChrW(223)", "ChrW(233)", "'e' & ChrW(769)", "ChrW(65313)", "ChrW(55296)", "ChrW(56320)", "ChrW(55296) & ChrW(56320)",
                    "ChrW(56320) & ChrW(55296)", "ChrW(8203)", "ChrW(173)", "ChrW(160)", "ChrW(65279) & 'bom'", "ChrW(197)", "ChrW(8491)", "ChrW(304)", "'i'", "ChrW(305)", "ChrW(12450)", "ChrW(12354)" })
                {
                    Sel(p, n, $"SELECT ID FROM Tx WHERE T = {c} ORDER BY ID", true, maxRows: 64);
                }
                foreach (string l in new[] { "'a*'", "'a?'", "'a?b'", "'?'", "'[a-b]'", "'*' & Chr(0) & '*'", "'a' & Chr(0) & '*'", "'*'", "'[!a]'" })
                {
                    Sel(p, n, $"SELECT ID FROM Tx WHERE T Like {l} ORDER BY ID", true, maxRows: 64);
                }
            });
        yield return Read("str-self-join-equality-classes", ["R-STRING-1", "II-02", "R-SQL-1", "SQL-EQ", "SQL-J"],
            "Asks which pairs of stored Tx values (a.ID < b.ID) compare equal under four predicates over the 38 x 38 self join: a.T = b.T, a.M = b.M, StrComp(a.T, b.T, 1) = 0 and StrComp(a.T, b.T, 0) = 0, and which pairs differ in Len or LenB while comparing equal. Each is one snapshot of the ID pairs ordered by both IDs, followed by DBEngine.Errors.",
            (p, n) =>
            {
                foreach (string w in new[] { "a.T = b.T", "a.M = b.M", "StrComp(a.T, b.T, 1) = 0", "StrComp(a.T, b.T, 0) = 0", "a.T = b.T AND (Len(a.T) <> Len(b.T) OR LenB(a.T) <> LenB(b.T))", "a.T = b.M", "UCase(a.T) = UCase(b.T)" })
                {
                    Sel(p, n, $"SELECT a.ID AS ia, b.ID AS ib FROM Tx AS a INNER JOIN Tx AS b ON a.ID < b.ID WHERE {w} ORDER BY a.ID, b.ID", true, maxRows: 64);
                }
            });
        yield return Read("str-group-distinct-order-union", ["R-STRING-1", "II-02", "R-SQL-1", "SQL-EQ", "SQL-UNION-01"],
            "Asks how grouping, de-duplication, ordering and extremes treat the stored edge strings of Tx: GROUP BY T with Count, DISTINCT T, DISTINCT M and GROUP BY M (Memo), ORDER BY T and T DESC with ID as tie-break, First(ID) by group, Min and Max of T and of M, UNION and UNION ALL of T with itself, a derived DISTINCT count, HAVING Count(*) > 1, and DISTINCT over UCase(T). Each form is one snapshot, followed by DBEngine.Errors.",
            (p, n) => Sels(p, n, ["SELECT T, Count(*) AS c FROM Tx GROUP BY T ORDER BY T", "SELECT DISTINCT T FROM Tx ORDER BY T", "SELECT DISTINCT M FROM Tx", "SELECT M, Count(*) AS c FROM Tx GROUP BY M",
                "SELECT ID, T FROM Tx ORDER BY T, ID", "SELECT ID FROM Tx ORDER BY T DESC, ID", "SELECT First(ID) AS f, T FROM Tx GROUP BY T ORDER BY T", "SELECT Min(T) AS a, Max(T) AS b FROM Tx", "SELECT Min(M) AS a, Max(M) AS b FROM Tx",
                "SELECT T FROM Tx UNION SELECT T FROM Tx ORDER BY 1", "SELECT Count(*) AS c FROM (SELECT T FROM Tx UNION ALL SELECT T FROM Tx)", "SELECT Count(*) AS c FROM (SELECT DISTINCT T FROM Tx)",
                "SELECT T, Count(*) AS c FROM Tx GROUP BY T HAVING Count(*) > 1", "SELECT DISTINCT UCase(T) AS u FROM Tx ORDER BY 1", "SELECT T, Count(*) AS c FROM Tx GROUP BY T ORDER BY Count(*) DESC, T"], maxRows: 64));
        yield return Read("str-concat-update-roundtrip", ["R-STRING-1", "II-02", "R-FMT-2", "R-SQL-2"],
            "Asks, on a writable copy of Tx, what SQL does when it builds edge strings itself and what is then stored, in sequence, each followed by RecordsAffected and DBEngine.Errors: appending Chr(0), putting a lone high surrogate in front of a value, cutting an astral pair with Left, copying the Memo column to the Text column and back, a 300-character value into Text(255) and into Memo, a 256-character value from Space, StrConv to and from the Unicode form, UCase of U+00DF, Replace to a lone surrogate, and INSERT ... SELECT that doubles values and swaps T and M. After each group the rows (ID, T, LenB(T), M) are read ordered by ID.",
            (p, n) =>
            {
                string[][] groups =
                [
                    ["UPDATE Tx SET T = T & Chr(0) WHERE ID IN (1, 2, 12)", "UPDATE Tx SET T = ChrW(55296) & T WHERE ID = 13", "UPDATE Tx SET T = Left(T, 1) WHERE ID IN (6, 8)", "UPDATE Tx SET M = T WHERE T Is Not Null"],
                    ["UPDATE Tx SET T = M WHERE ID = 5", "UPDATE Tx SET T = String(300, 'x') WHERE ID = 1", "UPDATE Tx SET M = String(300, 'x') WHERE ID = 1", "UPDATE Tx SET T = Space(255) & 'a' WHERE ID = 2", "UPDATE Tx SET T = Chr(0) & Chr(0) WHERE ID = 3"],
                    ["UPDATE Tx SET T = StrConv(T, 64) WHERE ID = 12", "UPDATE Tx SET T = StrConv(StrConv(T, 128), 64) WHERE ID = 4", "UPDATE Tx SET T = UCase(T) WHERE ID IN (14, 16)", "UPDATE Tx SET T = Replace(T, 'a', ChrW(55357)) WHERE ID = 11",
                        "INSERT INTO Tx (ID, T) SELECT ID + 100, T & T FROM Tx WHERE ID < 6", "INSERT INTO Tx (ID, T, M) SELECT ID + 200, M, T FROM Tx WHERE ID IN (4, 5, 6, 7)"],
                ];
                foreach (string[] group in groups)
                {
                    Exs(p, group);
                    Sel(p, n, "SELECT ID, T, LenB(T) AS lb, M FROM Tx ORDER BY ID", true, maxRows: 64);
                }
            }, writable: true);
        yield return Read("str-memo-versus-text-expressions", ["R-STRING-1", "II-02", "R-EXPR-1", "EXPR-INVENTORY"],
            "Asks what type, size and value an expression gets when its operand is the Memo column M instead of the Text column T, for the rows with ID 2 (embedded U+0000) and 5 (odd-byte BSTR): M, M & '', M & M, Left, Mid, Trim, UCase, Len, LenB, M = T, M Like, InStr, Replace, StrReverse, CStr, Format, IIf, Nz and Asc of M, and Space(Len(M)). Each form is one snapshot per row with the Field Name, Type, Size, Attributes and Value.",
            (p, n) => MetaEach(p, n, ["M", "M & ''", "M & M", "Left(M, 2)", "Mid(M, 1)", "Trim(M)", "UCase(M)", "Len(M)", "LenB(M)", "M = T", "M Like 'a*'", "InStr(M, 'b')", "Replace(M, 'a', 'x')", "StrReverse(M)",
                "CStr(M)", "Format(M, '>')", "IIf(True, M, T)", "Nz(M, 'x')", "Asc(M)", "Space(Len(M))", "T", "T & M"], "Tx", [2, 5]));
        yield return Read("str-unique-index-acceptance", ["R-STRING-1", "II-02", "R-SORT-3", "R-SQL-2"],
            "Asks, on a writable copy, which constructed values a unique text index (U.K, Text(50), unique index UX_U_K) takes as distinct keys: each INSERT INTO U (ID, K) VALUES (n, <expr>) in order, followed by DBEngine.Errors, for 'a', 'A', 'a ', 'a' & Chr(0), Chr(0), Chr(0) & Chr(0), '', ' ', ChrW(160), 'ss', ChrW(223), ChrW(233), 'e' & ChrW(769), ChrW(65313), ChrW(8203), ChrW(173), ChrW(65279), ChrW(55296), ChrW(56320), a high then a low surrogate, a low then a high surrogate, an astral pair, 'a' & ChrW(65534), 'b', 'b' & Chr(0), 'a' & ChrW(8203), and 50 and 51 characters. Then U is read (ID, K, Len(K), LenB(K)).",
            (p, n) =>
            {
                string[] values = ["'a'", "'A'", "'a '", "'a' & Chr(0)", "Chr(0)", "Chr(0) & Chr(0)", "''", "' '", "ChrW(160)", "'ss'", "ChrW(223)", "ChrW(233)", "'e' & ChrW(769)", "ChrW(65313)", "ChrW(8203)", "ChrW(173)",
                    "ChrW(65279)", "ChrW(55296)", "ChrW(56320)", "ChrW(55296) & ChrW(56320)", "ChrW(56320) & ChrW(55296)", "ChrW(55357) & ChrW(56832)", "'a' & ChrW(65534)", "'b'", "'b' & Chr(0)", "'a' & ChrW(8203)",
                    "String(50, 'z')", "String(51, 'z')"];
                for (int i = 0; i < values.Length; i++)
                {
                    Ex(p, $"INSERT INTO U (ID, K) VALUES ({i + 1}, {values[i]})");
                }
                Sel(p, n, "SELECT ID, K, Len(K) AS l, LenB(K) AS lb FROM U ORDER BY ID", true, maxRows: 64);
            }, writable: true);
        yield return Read("str-sql-text-edge-bytes", ["R-STRING-1", "II-02", "R-ORACLE-1", "OR-WIRE", "R-SQL-1", "SQL-B"],
            "Asks what ACE does with SQL text that is not ordinary text, passed as the BSTR argument of Database.OpenRecordset (snapshot) and then of Database.Execute (dbFailOnError, on a writable copy): an odd-byte BSTR (the text of SELECT 1 AS v FROM One plus one extra byte 0x00, 0x20 and 0x41), a lone high or low surrogate inside a string literal and inside a bracketed alias, an astral pair in a literal and in an alias, a leading U+FEFF, U+0000 at the end, before FROM and between two statements, U+FFFE and U+0001 inside a literal, a 255, 256, 257 and 4,000-character literal, and a 64- and 65-character alias; and INSERT INTO U (ID, K) VALUES with a lone surrogate and with an odd-byte BSTR statement, followed by the rows of U with Len(K) and LenB(K). Each call is followed by DBEngine.Errors.",
            (p, n) =>
            {
                const string basic = "SELECT 1 AS v FROM One";
                byte[] basicBytes = System.Text.Encoding.Unicode.GetBytes(basic);
                SelArg(p, n, A.BstrBytes([.. basicBytes, 0x00]));
                SelArg(p, n, A.BstrBytes([.. basicBytes, 0x20]));
                SelArg(p, n, A.BstrBytes([.. basicBytes, 0x41]));
                SelArg(p, n, A.BstrBytes(0x53));
                foreach (string sql in new[] { "SELECT '\uD800' AS v, Len('\uD800') AS l FROM One", "SELECT '\uDC00' AS v, Len('\uDC00') AS l FROM One", "SELECT 1 AS [\uD800] FROM One", "SELECT 'a\uD800b' AS v FROM One",
                    "SELECT '\U0001D11E' AS v, Len('\U0001D11E') AS l, LenB('\U0001D11E') AS lb FROM One", "SELECT 1 AS [\U0001D11E] FROM One", "\uFEFFSELECT 1 AS v FROM One", "SELECT 1 AS v FROM One\0", "SELECT 1 AS v\0 FROM One",
                    "SELECT 1 AS v FROM One;\0SELECT 2 AS v FROM One", "\0", "SELECT '\uFFFE' AS v FROM One", "SELECT '\u0001' AS v FROM One", "SELECT 'a\0b' AS v, Len('a\0b') AS l FROM One",
                    $"SELECT '{new string('x', 255)}' AS v FROM One", $"SELECT '{new string('x', 256)}' AS v FROM One", $"SELECT '{new string('x', 257)}' AS v FROM One", $"SELECT Len('{new string('x', 4000)}') AS l FROM One",
                    $"SELECT 1 AS [{new string('a', 64)}] FROM One", $"SELECT 1 AS [{new string('a', 65)}] FROM One" })
                {
                    SelArg(p, n, A.Str(sql));
                }
                p.Call("db", "Execute", A.Str("INSERT INTO U (ID, K) VALUES (1, 'a\uD800')"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                p.Call("db", "Execute", A.BstrBytes([.. System.Text.Encoding.Unicode.GetBytes("INSERT INTO U (ID, K) VALUES (2, 'b')"), 0x00]), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                p.Call("db", "Execute", A.Str("INSERT INTO U (ID, K) VALUES (3, 'c\0d')"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                Sel(p, n, "SELECT ID, K, Len(K) AS l, LenB(K) AS lb FROM U ORDER BY ID", true);
            }, writable: true);
        yield return Read("str-parameter-compare-and-length", ["R-STRING-1", "II-02", "R-SQL-3", "R-SQL-1"],
            "Asks what a Text(255) and a LongText QueryDef parameter does with edge values inside expressions: the values 'a', 'a ', 'a' & U+0000, an odd-byte BSTR of 1 byte, one of 3 bytes, a lone high surrogate, an astral pair, 'ss', the empty string, Null and a fullwidth A are assigned in turn (Value read back) and the QueryDef is opened as a snapshot for SELECT ID FROM Tx WHERE T = p ORDER BY ID, for SELECT ID FROM Tx WHERE M = p ORDER BY ID (LongText) and for SELECT Len(p) AS l, LenB(p) AS lb, p & '' AS c, UCase(p) AS u, Asc(p) AS a FROM One.",
            (p, n) =>
            {
                Arg[] values = [A.Str("a"), A.Str("a "), A.Str("a\0"), A.BstrBytes(0x41), A.BstrBytes(0x41, 0x00, 0x42), A.BstrUnits("\uD800"), A.Str("\U0001D11E"), A.Str("ss"), A.Str(""), A.Null, A.Str("\uFF21")];
                QdRun(p, n, "PARAMETERS pp Text(255); SELECT ID FROM Tx WHERE T = pp ORDER BY ID;", "pp", values);
                QdRun(p, n, "PARAMETERS pp LongText; SELECT ID FROM Tx WHERE M = pp ORDER BY ID;", "pp", values);
                QdRun(p, n, "PARAMETERS pp Text(255); SELECT Len(pp) AS l, LenB(pp) AS lb, pp & '' AS c, UCase(pp) AS u, Asc(pp) AS a FROM One;", "pp", values);
            });
        yield return Read("str-conversion-and-format-of-stored", ["R-STRING-1", "II-02", "R-EXPR-1", "EXPR-INVENTORY"],
            "Asks what StrConv with each mode (1 upper, 2 lower, 3 proper, 4 wide, 8 narrow, 16 katakana, 32 hiragana, 64 Unicode, 128 from Unicode), Format with the text pictures >, <, @@@@@@, !@@@@@@, &&& and a quoted literal, Format$, CStr, CVar, Trim$, UCase$ and Chr(Asc()) return for the stored edge strings of Tx (rows with a non-Null, non-empty T). Each form is one snapshot ordered by ID, followed by DBEngine.Errors.",
            (p, n) =>
            {
                foreach (string e in new[] { "StrConv(T, 1)", "StrConv(T, 2)", "StrConv(T, 3)", "StrConv(T, 4)", "StrConv(T, 8)", "StrConv(T, 16)", "StrConv(T, 32)", "StrConv(T, 64)", "StrConv(T, 128)", "Format(T, '>')",
                    "Format(T, '<')", "Format(T, '@@@@@@')", "Format(T, '!@@@@@@')", "Format(T, '&&&')", "Format(T, '\"x\"@')", "Format(T)", "Format$(T, '>')", "CStr(T)", "CVar(T)", "Trim$(T)", "UCase$(T)", "Chr(Asc(T))" })
                {
                    Sel(p, n, $"SELECT ID, {e} AS v FROM Tx WHERE T Is Not Null AND Len(T) > 0 ORDER BY ID", true, maxRows: 64);
                }
            });
    }

    // Statement and name lists ---------------------------------------------------------------------------------------------

    private static readonly string[] BigSetup =
    [
        "CREATE TABLE Bg (ID LONG CONSTRAINT PK_Bg PRIMARY KEY, B BIGINT)",
        "INSERT INTO Bg (ID, B) VALUES (1, 9223372036854775807)", "INSERT INTO Bg (ID, B) VALUES (2, -9223372036854775808)", "INSERT INTO Bg (ID, B) VALUES (3, CLngLng(5))",
        "INSERT INTO Bg (ID, B) VALUES (4, '9223372036854775807')", "INSERT INTO Bg (ID, B) VALUES (5, 2.5)", "INSERT INTO Bg (ID, B) VALUES (6, 9223372036854775808)", "INSERT INTO Bg (ID, B) VALUES (7, 1E19)",
        "INSERT INTO Bg (ID, B) VALUES (8, Null)", "INSERT INTO Bg (ID, B) VALUES (9, 'x')", "INSERT INTO Bg (ID, B) VALUES (10, 4611686018427387904)", "INSERT INTO Bg (ID, B) VALUES (11, 4611686018427387905)",
    ];

    private static readonly string[] ReservedWords =
    [
        "Name", "Date", "Text", "Value", "Order", "Group", "Table", "Count", "Year", "Type", "Level", "Key", "User", "Time", "Memo", "Currency", "Number", "Percent", "Index", "Select", "From", "Where", "Note", "Desc", "Asc",
        "Min", "Max", "Sum", "Avg", "First", "Last", "Day", "Month", "Left", "Right", "Mid", "Null", "True", "False", "Yes", "No", "Size", "Password", "Owner", "Option", "Section", "Comment", "Description", "Field", "Fields",
        "Column", "Row", "Rows", "Data", "Binary", "Short", "Long", "Single", "Double", "Boolean", "Integer", "Byte", "Timestamp", "Primary", "Unique", "Check", "Constraint", "Create", "Drop", "Alter", "Add", "Update",
        "Delete", "Insert", "Into", "Values", "Set", "Join", "On", "As", "By", "Is", "In", "Like", "Not", "And", "Or", "Between", "All", "Any", "Some", "Exists", "Union", "Distinct", "Top", "Id", "Counter", "Text1", "Memo1",
    ];

    private static readonly string[] TypeNames =
    [
        "BYTE", "TINYINT", "INTEGER1", "SHORT", "SMALLINT", "INTEGER2", "LONG", "INT", "INTEGER", "INTEGER4", "COUNTER", "AUTOINCREMENT", "IDENTITY", "SERIAL", "SINGLE", "FLOAT4", "REAL", "IEEESINGLE", "DOUBLE", "FLOAT",
        "FLOAT8", "IEEEDOUBLE", "NUMBER", "CURRENCY", "MONEY", "DATE", "DATETIME", "TIME", "TIMESTAMP", "TEXT", "TEXT(10)", "TEXT(0)", "TEXT(255)", "TEXT(256)", "CHAR", "CHAR(10)", "CHARACTER", "CHARACTER(10)", "STRING",
        "STRING(10)", "VARCHAR", "VARCHAR(10)", "VARCHAR(256)", "NCHAR(10)", "NCHAR VARYING(10)", "NATIONAL CHAR(10)", "NATIONAL CHARACTER VARYING(10)", "ALPHANUMERIC(10)", "MEMO", "NOTE", "LONGTEXT", "LONGCHAR",
        "NTEXT", "YESNO", "BIT", "BOOLEAN", "LOGICAL", "LOGICAL1", "GUID", "UNIQUEIDENTIFIER", "BINARY", "BINARY(10)", "BINARY(256)", "VARBINARY(10)", "BINARY VARYING(10)", "LONGBINARY", "GENERAL", "IMAGE", "OLEOBJECT",
        "BIGINT", "DATETIME2", "DATETIME2(3)", "NUMERIC", "NUMERIC(10, 2)", "DECIMAL(10, 2)", "VARIANT", "OBJECT", "LONG(10)", "TEXT(10) NOT NULL", "TEXT(10) UNICODE", "CHAR(10) COLLATE X",
    ];

    // Probe builders -------------------------------------------------------------------------------------------------------

    private static Probe Read(string name, string[] refs, string what, Action<P, Names> body, bool writable = false, string fixture = Main) =>
        new(Card, name, refs,
            what + $" Fixture {fixture} is opened {(writable ? "on a writable fresh copy" : "read-only")} through DAO.DBEngine.120; engine version and the loaded modules (once) are recorded; each recordset reads Name, Type and Value of every field.",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version").OpenDb(readOnly: !writable).Get("db", "Version").Get("db", "CollatingOrder");
                body(p, n);
                p.FinishLite("db");
            })
        { Fixtures = [("main", fixture)] };

    private static Probe Creation(string fixture, string fileName, int version, string what) =>
        new(Card, "create-" + fixture[(Card.Length + 1)..], ["R-EXPR-1", "R-SQL-1", "R-SQL-2", "R-SQL-3", "R-STRING-1"],
            $"R-DEF-EXPR-SQL fixture {fixture}: tables One (one row), Dm (8 rows of G Text with a Null, V Double, N Long, S Text, DT Date/Time, with Nulls, duplicates, a quote and a padded value), Tx (38 rows of ID, a Text(255) column T and a Memo column M holding the same edge value: embedded and leading and trailing U+0000, odd-byte BSTRs, lone surrogates in both orders, an astral pair, U+FEFF, U+FFFE, trailing, no-break and zero-width spaces, a soft hyphen, compatibility and case forms, empty and Null) and U (ID Long primary key, K Text(50) with a unique index UX_U_K, empty). {what}; values are stored through table-type AddNew/Value/Update; the closed file is kept.",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out(fileName), A.Str(Dao.LangGeneral), A.I4(version))
                    .Get("db", "Version").Get("db", "CollatingOrder");
                Fixture(p, n);
                p.FinishLite("db");
            })
        { CreatesFixture = true };

    private static void Fixture(P p, Names n)
    {
        Exs(p, ["CREATE TABLE One (ID LONG CONSTRAINT PK_One PRIMARY KEY)", "INSERT INTO One (ID) VALUES (1)",
            "CREATE TABLE Dm (ID LONG CONSTRAINT PK_Dm PRIMARY KEY, G TEXT(10), V DOUBLE, N LONG, S TEXT(20), DT DATETIME)",
            "CREATE TABLE Tx (ID LONG CONSTRAINT PK_Tx PRIMARY KEY, T TEXT(255), M MEMO)",
            "CREATE TABLE U (ID LONG CONSTRAINT PK_U PRIMARY KEY, K TEXT(50))", "CREATE UNIQUE INDEX UX_U_K ON U (K)"], false);
        AddRows(p, n, "Dm", ["ID", "G", "V", "N", "S", "DT"],
        [
            [A.I4(1), A.Str("a"), A.R8(1.5), A.I4(10), A.Str("x"), A.Date(45000.0)],
            [A.I4(2), A.Str("a"), A.R8(2.5), A.I4(20), A.Str("y"), A.Date(45001.5)],
            [A.I4(3), A.Str("a"), A.Null, A.I4(30), A.Null, A.Null],
            [A.I4(4), A.Str("b"), A.R8(4.0), A.Null, A.Str("x"), A.Date(45002.0)],
            [A.I4(5), A.Str("b"), A.R8(4.0), A.I4(50), A.Str("z"), A.Date(45002.0)],
            [A.I4(6), A.Null, A.R8(6.5), A.I4(60), A.Str("q'r"), A.Date(45003.0)],
            [A.I4(7), A.Str("c"), A.R8(-1.0), A.I4(0), A.Str(""), A.Date(45004.0)],
            [A.I4(8), A.Str("c"), A.R8(1e10), A.I4(70), A.Str(" pad "), A.Date(45005.25)],
        ]);
        AddRows(p, n, "Tx", ["ID", "T", "M"], EdgeRows.Select(r => new[] { A.I4(r.Id), r.Value, r.Value }));
    }

    // Program helpers ------------------------------------------------------------------------------------------------------

    /// <summary>Database.Execute with dbFailOnError, then RecordsAffected, then DBEngine.Errors unless <paramref name="errors"/> is false.</summary>
    private static void Ex(P p, string sql, bool errors = true)
    {
        p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected");
        if (errors)
        {
            p.Errors();
        }
    }

    private static void Exs(P p, IEnumerable<string> statements, bool errors = true)
    {
        foreach (string sql in statements)
        {
            Ex(p, sql, errors);
        }
    }

    /// <summary>Table-type AddNew/Value/Update per row.</summary>
    private static void AddRows(P p, Names n, string table, string[] cols, IEnumerable<Arg[]> rows)
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
        }
        for (int i = 0; i < cols.Length; i++)
        {
            p.Release($"{rs}c{i}");
        }
        p.Release(rs + "f").Call(rs, "Close").Release(rs);
    }

    /// <summary>Every row to EOF: Name, Type and Value of each field.</summary>
    private static void Rows(P p, string rs, int maxRows)
    {
        string fl = rs + "f", f = rs + "x";
        p.Until(rs, "EOF", maxRows, b => b.GetBind(fl, rs, "Fields")
            .Each(fl, f, 64, x => x.Get(f, "Name").Get(f, "Type").Get(f, "Value"))
            .Release(fl).Call(rs, "MoveNext"));
    }

    private static void Sel(P p, Names n, string sql, bool errors = false, int type = Dao.OpenSnapshot, int maxRows = 16)
    {
        string rs = n.Next("rs");
        p.OpenRs(rs, "db", sql, type);
        Rows(p, rs, maxRows);
        p.Call(rs, "Close").Release(rs);
        if (errors)
        {
            p.Errors();
        }
    }

    private static void Sels(P p, Names n, IEnumerable<string> statements, bool errors = true, int maxRows = 16)
    {
        foreach (string sql in statements)
        {
            Sel(p, n, sql, errors, maxRows: maxRows);
        }
    }

    /// <summary>OpenRecordset with the exact BSTR argument <paramref name="sql"/>, rows read, then DBEngine.Errors.</summary>
    private static void SelArg(P p, Names n, Arg sql)
    {
        string rs = n.Next("rs");
        p.CallBind(rs, "db", "OpenRecordset", sql, A.I4(Dao.OpenSnapshot));
        Rows(p, rs, 8);
        p.Call(rs, "Close").Release(rs).Errors();
    }

    /// <summary>Each expression in its own snapshot <c>SELECT expr FROM One</c>.</summary>
    private static void Scalars(P p, Names n, IEnumerable<string> exprs, bool errors = false)
    {
        foreach (string e in exprs)
        {
            Sel(p, n, $"SELECT {e} AS v FROM One", errors);
        }
    }

    /// <summary>The value, the inner VarType and the TypeName of each expression, each in its own snapshot.</summary>
    private static void Typed(P p, Names n, IEnumerable<string> exprs, bool errors = false)
    {
        foreach (string e in exprs)
        {
            Sel(p, n, $"SELECT {e} AS v, VarType({e}) AS vt, TypeName({e}) AS tn FROM One", errors);
        }
    }

    /// <summary>Field Name, Type, Size, Attributes and Value of the first row of the snapshot.</summary>
    private static void Meta(P p, Names n, string sql)
    {
        string rs = n.Next("mrs"), fl = rs + "f", f = rs + "x";
        p.OpenRs(rs, "db", sql, Dao.OpenSnapshot)
            .GetBind(fl, rs, "Fields")
            .Each(fl, f, 64, x => x.Get(f, "Name").Get(f, "Type").Get(f, "Size").Get(f, "Attributes").Get(f, "Value"))
            .Release(fl).Call(rs, "Close").Release(rs);
    }

    /// <summary>Metadata of each expression as column v of <paramref name="table"/>, for each ID in <paramref name="ids"/>.</summary>
    private static void MetaEach(P p, Names n, IEnumerable<string> exprs, string table, int[] ids)
    {
        foreach (string e in exprs)
        {
            foreach (int id in ids)
            {
                Meta(p, n, $"SELECT {e} AS v FROM {table} WHERE ID = {id}");
            }
        }
    }

    /// <summary>The names of every TableDef, after a Refresh.</summary>
    private static void TableNames(P p, Names n)
    {
        string c = n.Next("tn"), i = c + "x";
        p.GetBind(c, "db", "TableDefs").Call(c, "Refresh").Get(c, "Count").Each(c, i, 400, x => x.Get(i, "Name")).Release(c);
    }

    /// <summary>
    /// A temporary QueryDef of <paramref name="sql"/>: each value is assigned to the parameter <paramref name="parameter"/>
    /// (read back), then the QueryDef is opened as a snapshot and its rows are read.
    /// </summary>
    private static void QdRun(P p, Names n, string sql, string parameter, Arg[] values)
    {
        string qd = n.Next("qd"), ps = qd + "p", pm = qd + "m";
        p.CallBind(qd, "db", "CreateQueryDef", A.Str(""), A.Str(sql)).GetBind(ps, qd, "Parameters").Item(pm, ps, A.Str(parameter));
        foreach (Arg v in values)
        {
            string rs = n.Next("rs");
            p.Put(pm, "Value", v).Get(pm, "Value").CallBind(rs, qd, "OpenRecordset", A.I4(Dao.OpenSnapshot));
            Rows(p, rs, 64);
            p.Call(rs, "Close").Release(rs);
        }
        p.Release(pm).Release(ps).Release(qd);
    }

    /// <summary>CREATE TABLE Dt&lt;i&gt; (K LONG, C &lt;type&gt;) for every spelling, then each table's fields.</summary>
    private static void TypeNameMatrix(P p, Names n)
    {
        for (int i = 0; i < TypeNames.Length; i++)
        {
            Ex(p, $"CREATE TABLE Dt{i} (K LONG, C {TypeNames[i]})");
        }
        p.GetBind("dttdfs", "db", "TableDefs").Call("dttdfs", "Refresh");
        for (int i = 0; i < TypeNames.Length; i++)
        {
            string t = n.Next("dtt"), fs = t + "f", f = t + "x";
            p.Item(t, "dttdfs", A.Str($"Dt{i}")).GetBind(fs, t, "Fields")
                .Each(fs, f, 8, x => x.Get(f, "Name").Get(f, "Type").Get(f, "Size").Get(f, "Attributes")).Release(fs).Release(t);
        }
        p.Release("dttdfs");
    }
}
