namespace CalcDb.Probes.Cards;

/// <summary>
/// R-EXPR-1, second set (A-02 Branches A-E, EXPR-INVENTORY, EXPR-RUNTIME): breadth of the VBA function library and
/// the conversions, operators and comparisons that Access SQL reaches through <c>SELECT expr FROM One</c> on a
/// one-row table. It adds questions that <see cref="RExpr1"/> does not ask: conversion of text and half-way values
/// across the C* functions, Val, IsNumeric and IsDate forms, the result type of function calls, Null in every
/// function family, text, date, format and math function arguments at their boundaries, operator precedence and
/// mixed-type comparison, field metadata of function results, memo operands, which names the expression sandbox
/// refuses, and the same expressions through other routes (Recordset.Filter, action queries, ADO, an MDB file).
/// Each expression that might fail has its own snapshot, so one failure does not hide another; every value is
/// captured as the returned VARIANT and Field.Type, never compared as formatted text. Probes read their fixture
/// read-only, except those marked as writable copies. Nothing here starts a process, opens a dialog, writes outside
/// its own copy or reads the registry: file-system functions are asked only with a path that does not exist.
/// </summary>
public sealed class RExpr1B : ICard
{
    private const string Card = "r-expr-1b";
    private const string Main = Card + "-main";
    private const string Mdb = Card + "-mdb";
    private const string AceProvider = "Microsoft.ACE.OLEDB.12.0";

    /// <summary>A date-time literal used by many date probes (Tuesday 2024-03-05, 14:07:09).</summary>
    private const string Dt = "#2024-03-05 14:07:09#";

    private const string NoFile = "C:\\r-expr-1b-nofile.txt";

    /// <summary>One representative expression per function family, run through every route by the route probes.</summary>
    private static readonly string[] Battery =
    [
        "Format(1234.5, '#,##0.00')", "CInt(2.5)", "CDate('2024-03-05')", "DateAdd('m', 1, #2024-01-31#)", "IIf(True, 1, 'a')",
        "Switch(False, 1, True, 2)", "Choose(2, 'a', 'b')", "Nz(Null, 'x')", "Left('abc', 2)", "StrReverse('abc')",
        "Environ('R_EXPR_1B_NOVAR')", "CurDir()", "FileLen('" + NoFile + "')", "DLookup('ID', 'One')", "Eval('1+1')", "Round(2.5)",
        "Val('12abc')", "Hex(255)", "StrConv('abc def', 3)", "MonthName(1)", "WeekdayName(1)", "Partition(5, 0, 10, 2)",
        "IsNumeric('1e3')", "7 \\ 2", "7 Mod 2", "2 ^ 0.5", "1 / 0", "'a' & 1", "VarType(CCur(1))", "TypeName(1.5)",
        "FormatNumber(1234.5, 2)", "Split('a b', ' ')(1)", "Now() > #2000-01-01#",
    ];

    /// <summary>Unique binder names within one probe program.</summary>
    private sealed class Names
    {
        private int next;

        public string Next(string prefix) => prefix + next++;
    }

    public IEnumerable<Probe> Probes() =>
    [
        Creation(Main, Main + ".accdb", Dao.Version120, "ACCDB created by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion120)", Fixture),
        Creation(Mdb, Mdb + ".mdb", Dao.Version40, "MDB (Jet 4 format) created by DBEngine.CreateDatabase(path, dbLangGeneral, dbVersion40)", Fixture),

        // Conversions (Branch A, Branch B).
        Read("conv-text-to-integer", ["Branch A", "Branch B"],
            "Asks what CInt, CLng and CByte return or raise for text operands: plain, padded, thousands-separated, exponent, hex (&H) and octal (&O) prefixed, trailing-garbage, empty, currency-symbol, parenthesised, internally spaced, signed, and a fullwidth-digit text. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, Cross(["CInt", "CLng", "CByte"], "'12'", "' 12 '", "'1,234'", "'1e2'", "'&H10'", "'&O17'", "'&HFFFF'",
                "'12abc'", "''", "'$12'", "'(12)'", "'1 2'", "'+5'", "'1.5E+2'", "'\uFF11\uFF12'"))),
        Read("conv-text-to-floating", ["Branch A", "Branch B"],
            "Asks what CDbl, CCur and CDec return or raise for text operands: decimal forms with a comma, leading or trailing point, exponent forms including overflow and underflow, the largest Double text and one digit above it, a 16-digit integer, a Currency text with a fifth decimal, trailing blank or garbage, NaN and Infinity words, and a hex prefix. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, Cross(["CDbl", "CCur", "CDec"], "'1.5'", "'1,5'", "'.5'", "'5.'", "'1e-3'", "'1E+400'", "'-1.5E-400'",
                "'1.7976931348623157E308'", "'1.7976931348623159E308'", "'9007199254740993'", "'922337203685477.58075'", "'1.5 '", "'1.5x'", "'NaN'", "'Infinity'", "'&H10'"))),
        Read("conv-range-edges", ["Branch A", "Branch B"],
            "Asks the result and inner VarType/TypeName of the C* conversions at their range edges and from other types: CLng, CInt and CByte at and one past their limits, CInt and CLng of True, Null and a Date, CCur at and past its limits, CSng at the largest and smallest magnitudes and a denormal, CDbl(CSng(0.1)), CDec at its largest literal and past it, CDec of Boolean and Currency. Each call is its own snapshot SELECT expr, VarType(expr), TypeName(expr) FROM One.",
            (p, n) => Typed(p, n, ["CLng(2147483647)", "CLng(2147483648)", "CLng(-2147483648)", "CLng(-2147483649)", "CByte(255)", "CByte(256)", "CByte(-1)",
                "CInt(32767)", "CInt(32768)", "CInt(-32768)", "CInt(-32769)", "CInt(1E10)", "CInt(True)", "CInt(Null)", "CLng(#2024-03-05#)", "CInt(#2024-03-05#)",
                "CCur(922337203685477.5807)", "CCur(922337203685477.5808)", "CCur(-922337203685477.5808)", "CCur(1E15)", "CSng(3.4028235E38)", "CSng(3.5E38)",
                "CSng(1E-45)", "CSng(1E-46)", "CSng(0.1)", "CSng(16777217)", "CDbl(CSng(0.1))", "CDbl(1.8E308)", "CDec(7.9228162514264337593543950335E28)",
                "CDec(1E29)", "CDec(-0)", "CDec(True)", "CDec(CCur(1.5))"])),
        Read("conv-half-rounding-large", ["Branch B"],
            "Asks where half-way values round when the operand is a larger magnitude or another numeric type than RExpr1 uses: CLng and CInt at .5 neighbours of large and near-limit values, CInt and CLng and CByte of Single, Currency and Decimal operands of 2.5, 3.5 and 0.5/1.5, CCur at the fifth decimal for values near 1, and CByte at 254.5 and 255.4. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["CLng(0.5)", "CLng(1.5)", "CLng(2.5)", "CLng(1000000.5)", "CLng(1000001.5)", "CLng(2147483646.5)", "CLng(2147483645.5)",
                "CInt(100.5)", "CInt(101.5)", "CInt(-100.5)", "CInt(-101.5)", "CInt(32766.5)", "CInt(32765.5)", "CInt(CSng(2.5))", "CInt(CSng(3.5))",
                "CInt(CCur(2.5))", "CInt(CCur(3.5))", "CInt(CDec(2.5))", "CInt(CDec(3.5))", "CLng(CCur(0.5))", "CLng(CCur(1.5))", "CLng(CDec(0.5))", "CLng(CDec(1.5))",
                "CInt(0.5000000000000001)", "CInt(1.4999999999999998)", "CCur(1.00005)", "CCur(1.00015)", "CCur(1.00025)", "CCur(-1.00015)", "CCur(CDec(1.00015))",
                "CByte(CCur(2.5))", "CByte(CSng(2.5))", "CByte(CDec(2.5))", "CByte(0.4)", "CByte(2.5)", "CByte(3.5)", "CByte(254.5)", "CByte(255.4)"])),
        Read("conv-round-function-digits", ["Branch B", "EXPR-INVENTORY"],
            "Asks what Round returns for the number of digits and the operand type: half-way values at several digit counts (including negative and out-of-range counts), values whose decimal form is a half but whose binary form is not, Currency, Decimal and Single operands, text and Boolean operands, and non-integer digit counts. Each call is its own snapshot SELECT expr, VarType(expr), TypeName(expr) FROM One.",
            (p, n) => Typed(p, n, ["Round(0.125, 2)", "Round(0.135, 2)", "Round(0.145, 2)", "Round(1.005, 2)", "Round(-1.005, 2)", "Round(1234.5678, -2)", "Round(1234.5678, 2)",
                "Round(1234.5678, 0)", "Round(5.5)", "Round(6.5)", "Round(-5.5)", "Round(2.5, 1)", "Round(1.45, 1)", "Round(1.55, 1)", "Round(1.5, 15)", "Round(1.5, 16)",
                "Round(1.5, -16)", "Round('2.5')", "Round('x')", "Round(True)", "Round(CCur(2.5))", "Round(CCur(1.005), 2)", "Round(CDec(1.005), 2)", "Round(CSng(1.005), 2)",
                "Round(2.5, 0.5)", "Round(2.5, 1.5)", "Round(1234, -2)", "Round(1250, -2)", "Round(1350, -2)", "Round(1.5, 2147483648)"])),
        Read("conv-cstr-forms", ["Branch B", "EXPR-INVENTORY"],
            "Asks the text CStr returns for numbers of many magnitudes (switch to exponent form, tiny values, repeating fractions, a Single, Currency and Decimal operand), Boolean, and Date operands with and without a time part; and what Str returns for sign and zero. Dates are rendered in the oracle's locale, which is recorded with the run. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["CStr(1E10)", "CStr(1E14)", "CStr(1E15)", "CStr(1E16)", "CStr(0.0001)", "CStr(0.00001)", "CStr(1/3)", "CStr(100000000000000000000)", "CStr(-0.0)",
                "CStr(CSng(1/3))", "CStr(CSng(1E10))", "CStr(CCur(1.5))", "CStr(CCur(1))", "CStr(CDec(1.10))", "CStr(" + Dt + ")", "CStr(#2024-03-05#)", "CStr(#14:30#)",
                "CStr(CDate(0.5))", "CStr(CDate(-0.25))", "CStr(True)", "CStr(False)", "CStr(2147483648)", "CStr(CByte(5))", "CStr(1.10)", "CStr(5E-324)",
                "Str(1)", "Str(-1)", "Str(0)", "Str(1E10)", "Str(1.5)", "Str(True)", "Str('5')", "Str('x')", "Str(Null)", "Str(CCur(2.5))"])),
        Read("conv-cbool-cvar", ["Branch A", "Branch D"],
            "Asks which operands CBool accepts and what it returns (numbers including a denormal and negative zero, Currency and Decimal zero, a Date with and without a day part, text 0/-1/1/false/FALSE/TRUE/padded True/non-numeric, empty text and Null), and the result tag and VarType of CVar of each value type. Each call is its own snapshot SELECT expr FROM One, the CVar calls with VarType and TypeName.",
            (p, n) =>
            {
                Scalars(p, n, ["CBool(0)", "CBool(0.1)", "CBool(-1)", "CBool(CByte(0))", "CBool('0')", "CBool('-1')", "CBool('1')", "CBool('false')", "CBool('FALSE')",
                    "CBool('TRUE')", "CBool('x')", "CBool('')", "CBool(' True ')", "CBool(Null)", "CBool(#12/30/1899#)", "CBool(#12/30/1899 06:00#)", "CBool(CCur(0))",
                    "CBool(CDec(0))", "CBool(1E-320)", "CBool(-0.0)"]);
                Typed(p, n, ["CVar(1)", "CVar(1.5)", "CVar('a')", "CVar(Null)", "CVar(#2024-03-05#)", "CVar(CCur(1))", "CVar(True)", "CVar(CByte(1))", "CVar(CSng(1))", "CVar(CDec(1))"]);
            }),
        Read("conv-cdate-forms", ["Branch C", "Branch A"],
            "Asks what CDate returns for text in many shapes (time only, ISO with and without time, month-name forms, a compact eight-digit form, two-digit years at 29/30 and 49/50, year 100, AM/PM, an out-of-range hour) and for numbers, numeric text, empty text, Null and True. Results depend on the oracle's locale and calendar, which are recorded. Each call is its own snapshot SELECT expr, VarType(expr) FROM One.",
            (p, n) => Typed(p, n, ["CDate('12:30')", "CDate('2024-03-05 14:30')", "CDate('March 5, 2024')", "CDate('5 Mar 2024')", "CDate('20240305')", "CDate('3/5/24')",
                "CDate('3/5/49')", "CDate('3/5/50')", "CDate('1/1/29')", "CDate('1/1/30')", "CDate('1/1/100')", "CDate('1/1/0100')", "CDate('12:30 PM')", "CDate('12:30 AM')",
                "CDate('25:00')", "CDate('24:00')", "CDate(45000.75)", "CDate('45000')", "CDate('45000.75')", "CDate('0')", "CDate('')", "CDate(Null)", "CDate(True)",
                "CDate('2024-03-05T14:30:00')", "CDate('Mar 5')", "CDate('5 March')", "CDate(' 2024-03-05 ')", "CDate('2024/03/05')", "CDate('05.03.2024')"])),
        Read("conv-val-forms", ["Branch A", "EXPR-INVENTORY"],
            "Asks what Val returns for text with leading blanks, internal blanks, a comma, leading or trailing point, signs, a bare or dangling exponent, a d exponent, hex and octal prefixes (including the 16-bit boundary), a doubled point, a fullwidth digit, a tab inside, a currency symbol and an overflowing exponent, and for numeric, Boolean, empty and Null operands. Each call is its own snapshot SELECT expr, VarType(expr), TypeName(expr) FROM One.",
            (p, n) => Typed(p, n, ["Val('12abc')", "Val(' 12')", "Val('1 2')", "Val('1,5')", "Val('.5')", "Val('-.5')", "Val('+5')", "Val('1e')", "Val('1e+')", "Val('1d3')",
                "Val('&HFF')", "Val('&HFFFF')", "Val('&H8000')", "Val('&H7FFF')", "Val('&HFFFFFFFF')", "Val('&O17')", "Val('&O177777')", "Val('&H')", "Val('')", "Val(Null)",
                "Val(12)", "Val(1.5)", "Val('1.2.3')", "Val(' - 5')", "Val('0x10')", "Val('\uFF11\uFF12')", "Val('1' & Chr(9) & '2')", "Val('$5')", "Val('1E400')",
                "Val(True)", "Val('True')", "Val(#2024-03-05#)", "Val('&H1G')", "Val('1e5x')"])),
        Read("info-isnumeric-isdate-forms", ["Branch A", "Branch C", "EXPR-INVENTORY"],
            "Asks what the predicates IsNumeric and IsDate return. IsNumeric: thousands separators, currency symbol, parentheses, exponent and d-exponent forms, doubled and trailing signs, lone and trailing points, blanks, internal blanks, a bad hex digit and an octal digit 8, Boolean, Date, Currency, Decimal and numeric-looking operands. IsDate: text in date and time shapes (two-part dates, month-name forms, a bare year, a compact digit form, impossible months and days, time only, an out-of-range hour, ISO with T), numbers, numeric text, Boolean, Null and a Date value. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, [.. Over("IsNumeric({0})", "'1,000'", "'$1'", "'(1)'", "'1e5'", "'1d5'", "'--1'", "'+1'", "'1-'", "'1.'", "'.'", "' 1 '", "'1 1'", "'&H1G'", "'&O8'",
                "'&H'", "'1e'", "'e5'", "'0x1'", "'1.2.3'", "'\uFF11'", "True", "#2024-03-05#", "CCur(1)", "CDec(1)", "CByte(1)", "1E308", "'1E400'", "'-'", "'+'"),
                .. Over("IsDate({0})", "'1/1/2000'", "'13/13/2000'", "'12:30'", "'2000'", "'1/1'", "'Jan 1'", "'1 Jan 2000'", "#2024-03-05#", "45000", "'45000'", "'20240305'",
                "'2024-03-05T10:00'", "''", "Null", "True", "'25:00'", "'12:60'", "'2/29/2023'", "'2/29/2024'", "'0/1/2000'", "'1/32/2000'", "'1/1/10000'", "'Monday'", "'noon'", "'1/1/100'")])),

        // Result types of function calls (Branch B, EXPR-INVENTORY).
        Read("info-result-vartype-numeric", ["Branch B", "EXPR-INVENTORY"],
            "Asks the inner VarType and TypeName of the result of arithmetic operators and numeric, string-length and date-part functions over typed operands (integer widths, Single, Currency, Decimal, numeric text, Boolean). Each expression is its own snapshot SELECT VarType(expr), TypeName(expr) FROM One.",
            (p, n) => TypeOf(p, n, ["1 + 1", "1.5 + 1", "4 / 2", "5 / 2", "2 ^ 2", "5 \\ 2", "5 Mod 2", "-CInt(1)", "Abs(CInt(-1))", "Abs(CByte(1))", "Abs(CCur(-1))", "Abs(CSng(-1))",
                "Abs(CDec(-1))", "Abs(-1)", "Abs(-1.5)", "Abs('-1')", "Abs(True)", "Int(CInt(1))", "Int(1.5)", "Int(CCur(1.5))", "Int(CSng(1.5))", "Int(CDec(1.5))", "Int('1.5')",
                "Fix(CInt(1))", "Fix(1.5)", "Fix(CCur(1.5))", "Fix(CSng(1.5))", "Fix(CDec(1.5))", "Sgn(1.5)", "Sgn(CCur(1))", "Sgn(CDec(1))", "Sgn(CInt(1))", "Sqr(4)", "Exp(0)", "Log(1)",
                "Round(1.5)", "Round(CCur(1.5))", "Round(CDec(1.5))", "Round(CInt(2))", "Round(CSng(1.5), 1)", "Len('a')", "LenB('a')", "InStr('a', 'a')", "InStrRev('a', 'a')", "Asc('a')",
                "AscW('a')", "StrComp('a', 'a')", "Weekday(" + Dt + ")", "Year(" + Dt + ")", "Month(" + Dt + ")", "Day(" + Dt + ")", "Hour(" + Dt + ")", "Minute(" + Dt + ")", "Second(" + Dt + ")",
                "DateDiff('d', #2024-01-01#, " + Dt + ")", "DatePart('d', " + Dt + ")", "Val('1')", "Rnd(-1)", "CSng(1) * CSng(2)", "CSng(1) + 1", "CCur(1) + 1", "CCur(1) + 1.5",
                "CByte(1) + 1", "CInt(1) + 1", "CInt(1) + 40000", "True + 1"])),
        Read("info-result-vartype-text-date", ["Branch B", "EXPR-INVENTORY"],
            "Asks the inner VarType and TypeName of the result of text, conversion, date and conditional functions, including the $ forms and Null-argument forms. Each expression is its own snapshot SELECT VarType(expr), TypeName(expr) FROM One.",
            (p, n) => TypeOf(p, n, ["Left('a', 1)", "Left$('a', 1)", "Left(Null, 1)", "Left$(Null, 1)", "Chr(65)", "Chr$(65)", "ChrW(65)", "ChrW$(65)", "Mid('a', 1)", "Mid$('a', 1)",
                "Right('a', 1)", "Trim('a')", "LTrim('a')", "RTrim('a')", "UCase('a')", "LCase('a')", "UCase$('a')", "Space(1)", "Space$(1)", "String(1, 'a')", "String$(1, 'a')",
                "Replace('a', 'a', 'b')", "StrReverse('a')", "StrConv('a', 1)", "Format(1, '0')", "Format$(1, '0')", "Format(Null, '0')", "Hex(1)", "Hex$(1)", "Oct(1)", "Str(1)", "Str$(1)", "CStr(1)",
                "MonthName(1)", "WeekdayName(1)", "CDate('2024-03-05')", "DateSerial(2024, 1, 1)", "TimeSerial(1, 1, 1)", "DateAdd('d', 1, " + Dt + ")", "DateValue(" + Dt + ")", "TimeValue(" + Dt + ")",
                "Now()", "Date()", "Time()", "Timer()", "IIf(True, 'a', 'b')", "IIf(True, 1, 'a')", "Switch(True, 1)", "Choose(1, 1, 2)", "Nz(Null)", "Nz(Null, 0)", "Nz(Null, '')", "Nz(1)",
                "Null & 'a'", "'a' & 'b'", "1 & 2", "CVDate(1)", "FormatNumber(1.5)", "FormatDateTime(" + Dt + ")"])),

        // Null and Empty across function families (Branch A).
        Read("info-stored-null-handling", ["Branch A", "Branch E"],
            "Asks how IsNull, Nz, Len, &, +, IIf and comparison treat stored Null, empty-text and padded-text values: over the five rows of Mx (N Long, T Text with a Null, an empty and a padded value, D Double, DT Date, Y Yes/No), the projections IsNull/Nz/Nz-with-replacement/Len/VarType of N and T, the & and + forms with a stored Null, an IIf classification, and Nz of a Double replaced by a Long. Then Nz called with no arguments and with three.",
            (p, n) =>
            {
                Sel(p, n, "SELECT ID, N, T, IsNull(N) AS inN, IsNull(T) AS inT, Nz(N) AS nzN, Nz(T) AS nzT, Nz(N, -1) AS nzN1, Nz(T, 'none') AS nzT1, Len(T) AS lT, VarType(Nz(N)) AS vN, VarType(Nz(T)) AS vT FROM Mx ORDER BY ID");
                Sel(p, n, "SELECT ID, N + 1 AS a, N & 'x' AS b, T & 'x' AS c, T + 'x' AS d, N & T AS e, IIf(IsNull(T), 'null', IIf(T = '', 'empty', T)) AS cls, Nz(D, N) AS f, Nz(DT) AS g, LenB(T) AS h FROM Mx ORDER BY ID");
                Sel(p, n, "SELECT ID, Nz(Y) AS a, Nz(DT, 0) AS b, Nz(D, 'x') AS c, Trim(T) & '|' AS d, Left(T, 1) AS e, UCase(T) AS f, Val(T) AS g FROM Mx ORDER BY ID");
                Scalars(p, n, ["Nz()", "Nz(1, 2, 3)", "Nz(1, 2)"]);
            }),
        Read("null-propagation-functions", ["Branch A", "EXPR-INVENTORY"],
            "Asks what each numeric, text, conversion and date function returns or raises when an argument is Null (the Null placed in each argument position in turn for the multi-argument functions): Null result, error, or a non-Null value. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["Abs(Null)", "Int(Null)", "Fix(Null)", "Sqr(Null)", "Exp(Null)", "Log(Null)", "Round(Null)", "Round(1.5, Null)", "Right(Null, 1)", "Right('a', Null)",
                "Mid(Null, 1, 1)", "Mid('a', 1, Null)", "Trim(Null)", "LTrim(Null)", "RTrim(Null)", "UCase(Null)", "LCase(Null)", "Replace(Null, 'a', 'b')", "Replace('a', Null, 'b')", "Replace('a', 'a', Null)",
                "InStr('a', Null)", "InStr(Null, 'a')", "InStrRev(Null, 'a')", "InStrRev('a', Null)", "StrComp(Null, 'a')", "StrComp('a', Null)", "Asc(Null)", "AscW(Null)", "Chr(Null)", "ChrW(Null)",
                "Hex(Null)", "Oct(Null)", "Space(Null)", "String(Null, 'a')", "String(3, Null)", "Format(Null, '0')", "Format(Null)", "Format(1, Null)", "CInt(Null)", "CLng(Null)", "CDbl(Null)",
                "CSng(Null)", "CCur(Null)", "CDec(Null)", "CDate(Null)", "CBool(Null)", "CByte(Null)", "Val(Null)", "LenB(Null)", "Weekday(Null)", "DateAdd('d', Null, " + Dt + ")",
                "DateAdd(Null, 1, " + Dt + ")", "DateDiff('d', Null, " + Dt + ")", "DateDiff(Null, " + Dt + ", " + Dt + ")", "DatePart('d', Null)", "DatePart(Null, " + Dt + ")", "DateSerial(Null, 1, 1)",
                "DateSerial(2024, Null, 1)", "TimeSerial(1, Null, 1)", "MonthName(Null)", "WeekdayName(Null)", "StrReverse(Null)", "StrConv(Null, 1)", "TypeName(Null)", "IsDate(Null)", "Nz(Null, Null)",
                "IIf(True, Null, 1)", "Choose(1, Null)", "Switch(True, Null)", "Null ^ 2", "2 ^ Null", "Null Mod 2", "2 Mod Null", "Null \\ 2", "-Null", "Null * 0"])),

        // Text functions (EXPR-INVENTORY).
        Read("str-instr-compare-modes", ["EXPR-INVENTORY", "Branch A"],
            "Asks what InStr and InStrRev return for each compare mode (0, 1, 2, and invalid 3 and -1), start positions at, past and before the string, a start of 1.5 and 2.5, an empty search at and past the end, empty subject, number operands, Latin-1 and ligature letters under a text compare, the NUL and a surrogate pair inside the subject, and a lone surrogate search. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["InStr(1, 'ABC', 'b', 0)", "InStr(1, 'ABC', 'b', 1)", "InStr(1, 'ABC', 'b', 2)", "InStr(1, 'ABC', 'b', 3)", "InStr(1, 'ABC', 'b', -1)",
                "InStr(2, 'abcabc', 'b')", "InStr(3, 'abcabc', 'b')", "InStr(7, 'abcabc', 'b')", "InStr(6, 'abcabc', '')", "InStr(7, 'abcabc', '')", "InStr(8, 'abcabc', '')", "InStr(1, '', 'a')", "InStr(1, '', '')",
                "InStr(-1, 'abc', 'a')", "InStr(1.5, 'abc', 'b')", "InStr(2.5, 'abc', 'b')", "InStr('abc', 'b', 1)", "InStr(1, 'abc', 'bc', 1)", "InStr('\u00E4', 'a', 1)", "InStr('\u00E4', 'a', 0)",
                "InStr(1, 'a\u00E6b', '\u00E6', 1)", "InStr(1, 'a\u00E6b', 'ae', 1)", "InStr(1, 'abc', Null)", "InStr(1, 12345, 34)", "InStr(1, 'abc', 98)", "InStr(2147483647, 'abc', 'a')",
                "InStr(1, 'a' & Chr(0) & 'b', 'b')", "InStr(1, 'a\uD83D\uDE00b', 'b')", "InStr(1, 'a\uD83D\uDE00b', '\uDE00')",
                "InStrRev('abcabc', 'b', -1)", "InStrRev('abcabc', 'b', 4)", "InStrRev('abcabc', 'b', 2)", "InStrRev('abcabc', 'b', 3)", "InStrRev('abcabc', 'b', 0)", "InStrRev('abcabc', 'b', 7)",
                "InStrRev('abcabc', '', 3)", "InStrRev('', 'a')", "InStrRev('ABC', 'b', -1, 0)", "InStrRev('ABC', 'b', -1, 1)", "InStrRev('abc', 'b', -2)", "InStrRev('abc', 'b', 1.5)", "InStrRev('abc', 'b', Null)"])),
        Read("str-mid-left-right-edges", ["EXPR-INVENTORY", "Branch B"],
            "Asks what Mid, Left and Right return or raise for lengths and starts of zero, negative, fractional, past-the-end and 2^31 magnitude, for non-text operands (number, Boolean, Date), for text and Byte/Decimal length operands, and for the empty subject. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["Mid('abcd', 2, 100)", "Mid('abcd', -1)", "Mid('abcd', 1, 0)", "Mid('abcd', 2, -1)", "Mid('abcd', 1.5, 1.5)", "Mid('abcd', 2.5, 1)", "Mid('abcd', 3, 1.5)", "Mid('abcd', 2147483647)",
                "Mid('abcd', 1, 2147483647)", "Mid('abcd', 1, 2147483648)", "Mid('', 1)", "Mid(1234, 2, 2)", "Mid(True, 2)", "Mid(" + Dt + ", 1, 4)", "Left('abcd', 2.5)", "Left('abcd', 1.5)", "Left('abcd', 0)",
                "Left('abcd', 100)", "Left(1234, 2)", "Left(#2024-03-05#, 4)", "Left(True, 2)", "Left('abcd', '2')", "Left('abcd', 'x')", "Left('', 1)", "Left('abcd', 2147483647)", "Left('abcd', 2147483648)",
                "Left('abcd', CByte(2))", "Left('abcd', CDec(2))", "Left('abcd', True)", "Right('abcd', 0)", "Right('abcd', 100)", "Right('abcd', -1)", "Right(1234, 2)", "Right('abcd', 2.5)", "Right('abcd', 1.5)",
                "Right('abcd', 2147483647)", "Right('', 1)", "Right(#2024-03-05#, 4)", "Right('a\uD83D\uDE00', 1)", "Left('a\uD83D\uDE00', 2)"])),
        Read("str-len-lenb-forms", ["EXPR-INVENTORY", "Branch B"],
            "Asks what Len and LenB return for empty and ordinary text, accented, euro, surrogate-pair, lone-surrogate and NUL characters, a long computed text, and for number, Boolean, Date, Currency, Byte, Null, a 12-digit integer and floating operands (the length of the converted text and the stored byte width). Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["Len('')", "Len('abc')", "Len('\u00E9')", "Len('\u20AC')", "Len(ChrW(8364))", "Len('\uD83D\uDE00')", "Len(ChrW(55357))", "Len(Chr(0))", "Len(Space(3))", "Len(3)", "Len(1.5)", "Len(True)",
                "Len(#2024-03-05#)", "Len(" + Dt + ")", "Len(CCur(1.5))", "Len(CByte(1))", "Len(12345678901)", "Len(1.0)", "Len(0.1 + 0.2)", "Len(1E10)", "Len(CSng(1/3))", "Len(String(300, 'a'))", "Len(String(5000, 'a'))",
                "LenB('')", "LenB('abc')", "LenB('\u00E9')", "LenB('\u20AC')", "LenB('\uD83D\uDE00')", "LenB(1)", "LenB(1.5)", "LenB(True)", "LenB(" + Dt + ")", "LenB(CCur(1))", "LenB(CByte(1))", "LenB(CInt(1))",
                "LenB(CSng(1))", "LenB(CDec(1))", "LenB(Chr(255))", "LenB(ChrW(256))", "LenB(Chr(0))"])),
        Read("str-case-folding", ["EXPR-INVENTORY"],
            "Asks what UCase and LCase return for accented, ligature, Greek (including final sigma), Cyrillic, fullwidth, dotless/dotted i, German sharp s' capital relatives, micro sign, long s, Kelvin sign, ligature fi, an astral letter (surrogate pair), a lone surrogate, text with an embedded NUL, and for number, Boolean and Date operands. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["UCase('\u00E9')", "UCase('\u00FF')", "LCase('\u00C9')", "UCase('\u01C6')", "UCase('\u0131')", "LCase('\u03A3')", "UCase('\u03C2')", "UCase('\u00E6')", "LCase('\u00C6')", "UCase('\u00B5')",
                "UCase('\u0153')", "UCase('\u017F')", "UCase('\uFB01')", "LCase('\u212A')", "UCase('\uFF41')", "UCase('\u0430')", "UCase('\u03B1')", "UCase('\u1E9E')", "LCase('\u1E9E')", "UCase('\uD801\uDC28')",
                "UCase('\uD83D')", "LCase('ABC' & Chr(0) & 'DEF')", "UCase('a' & Chr(0) & 'b')", "UCase(1)", "UCase(True)", "UCase(#2024-03-05#)", "UCase(1.5)", "UCase$('a')", "LCase$('A')"])),
        Read("str-trim-space-string", ["EXPR-INVENTORY"],
            "Asks which characters Trim, LTrim and RTrim remove (space, tab, line feed, no-break space, ideographic space, NUL), what they return for Null, number and padded-numeric operands; and what Space and String return for zero, negative, fractional, text-numeric, past-255, past-4000 and 32768-wide counts, for empty, numeric-code and multi-character fill operands, and for a fill code above 255. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["Trim(Chr(160) & 'a' & Chr(160))", "Trim(ChrW(12288) & 'a')", "Trim(Chr(9) & 'a')", "Trim(Chr(13) & Chr(10) & 'a')", "Trim(Chr(0) & 'a')", "RTrim('a   ' & Chr(0))", "LTrim(' ' & ' a')",
                "Trim(5)", "Trim(' 5 ') + 1", "Trim(True)", "Len(Trim(Space(10)))", "Len(RTrim(Space(1000)))", "Space(0)", "Space(-1)", "Space(2.5)", "Space(1.5)", "Len(Space(300))", "Len(Space(4000))", "Len(Space(32768))",
                "Space('3')", "Space('x')", "Space(2147483648)", "String(0, 'a')", "String(-1, 'a')", "String(3, '')", "String(3, 256)", "String(3, 65.5)", "Len(String(300, 'a'))", "String(3, ChrW(8364))", "String(3, '\u00E9')",
                "String(2.5, 'a')", "String(3, 0)", "String(3, -1)", "String(3, 65536)", "String(3, 'ab')", "String(3, True)", "String('3', 'a')"])),
        Read("str-strcomp-and-text-operators", ["EXPR-INVENTORY", "Branch B"],
            "Asks what StrComp returns for each compare mode (0, 1, 2 and invalid values), for case, accent, ligature and trailing-blank differences, and number/text operands, and how the comparison operators order text under the database collation: case, accent, trailing blank, digits-as-text, empty text, NUL and sign characters. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["StrComp('a', 'A')", "StrComp('a', 'A', 0)", "StrComp('a', 'A', 1)", "StrComp('a', 'A', 2)", "StrComp('a', 'b')", "StrComp('b', 'a')", "StrComp('', '')", "StrComp('', 'a')",
                "StrComp('\u00E9', 'e', 1)", "StrComp('\u00E9', 'e', 0)", "StrComp('\u00DF', 'ss', 1)", "StrComp('a', 'a ', 1)", "StrComp('abc', 'ABD', 0)", "StrComp(1, '1')", "StrComp(10, 9)", "StrComp('10', '9')",
                "StrComp('a', 'b', 3)", "StrComp('a', 'b', -1)", "StrComp('a', 'b', 1.5)", "StrComp('a', 'b', Null)", "StrComp(Chr(0), '')", "StrComp(CDate(1), 1)",
                "'a' < 'B'", "'a' = 'A'", "'\u00E4' = 'a'", "'a' = 'a '", "'Z' < 'a'", "'10' < '9'", "'abc' < 'abd'", "'' < 'a'", "Chr(0) < ''", "Chr(0) = ''", "'-' < '+'", "'_' < 'a'", "'a' < '_'", "'1' < 'a'", "' ' < ''",
                "'\u00E6' = 'ae'", "'\u00DF' = 'ss'", "'a' < 'A'", "'ab' < 'a-b'", "'a-b' < 'ab'"])),
        Read("str-replace-args", ["EXPR-INVENTORY", "Branch A"],
            "Asks what Replace returns for the start, count and compare arguments (start past the match, past the end, zero and negative; count 0, 1 and -1; compare 0 and 1 and invalid), an empty or Null replacement, an omitted replacement, overlapping and growing replacements, number operands, and a NUL replacement. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["Replace('aaa', 'a', 'b', 2)", "Replace('aaa', 'a', 'b', 2, 1)", "Replace('aaa', 'a', 'b', 1, 0)", "Replace('aaa', 'a', 'b', 1, -1)", "Replace('aaa', 'a', 'b', -1, -1)", "Replace('aaa', 'a', 'b', 0)",
                "Replace('aaa', 'A', 'b', 1, -1, 1)", "Replace('aaa', 'A', 'b', 1, -1, 0)", "Replace('aaa', 'A', 'b', 1, -1, 2)", "Replace('aaa', 'a', 'b', 1, -1, 3)", "Replace('abc', 'b', '')", "Replace('abc', 'b', Null)", "Replace('abc', 'b')",
                "Replace('aaa', 'aa', 'b')", "Replace('aaa', 'a', 'aa')", "Replace('abc', 'c', 'x', 4)", "Replace('abc', 'c', 'x', 3)", "Replace('abc', 'c', 'x', 0)", "Replace(123, 2, 9)", "Replace('a-b', '-', Chr(0))",
                "Replace('a-b', '-', '')", "Replace('', 'a', 'b')", "Replace('abc', 'abcd', 'x')", "Replace('\u00E9', 'e', 'x', 1, -1, 1)", "Replace('ABC', 'b', 'x', 1, -1, 1)", "Replace('abc', 'b', 'x', 1.5)", "Replace('abc', 'b', 'x', 2, 1.5)"])),
        Read("str-chr-asc-range", ["EXPR-INVENTORY", "Branch A"],
            "Asks what Chr, ChrW and the byte forms ChrB and AscB return for arguments across and beyond their ranges (negative, 127-129, 255, 256, fractional, text-numeric, Boolean, 65535, 65536, -32768), and what Asc, AscW return for multi-character, space, high-Latin-1, euro, lone-surrogate, empty and number operands. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["Chr(0)", "Chr(65)", "Chr(127)", "Chr(128)", "Chr(129)", "Chr(255)", "Chr(-1)", "Chr(65.5)", "Chr(66.5)", "Chr('65')", "Chr(Null)", "Chr(True)", "ChrW(0)", "ChrW(65535)", "ChrW(65536)",
                "ChrW(-1)", "ChrW(-32768)", "ChrW(32768)", "ChrW(65.5)", "ChrW('65')", "ChrB(65)", "ChrB(256)", "Chr$(65)", "ChrW$(65)",
                "Asc('A')", "Asc('ABC')", "Asc(' ')", "Asc(Chr(255))", "Asc(Chr(128))", "Asc('\u00E9')", "Asc(65)", "Asc(Chr(0))", "Asc(True)", "AscW('A')", "AscW(ChrW(65535))", "AscW(ChrW(32768))", "AscW(ChrW(55357))",
                "AscW('\uD83D\uDE00')", "AscW('')", "AscW(65)", "AscB('A')", "AscB('\u00E9')", "AscB('')", "LenB(Chr(200))", "LenB(ChrW(200))", "LenB(ChrB(65))"])),
        Read("str-hex-oct-forms", ["EXPR-INVENTORY", "Branch B"],
            "Asks what Hex and Oct return across the integer widths (Byte, Integer, Long) and sign boundaries, for fractional, Currency, Boolean, Date, text, empty, Null, above-Long and floating operands, and the $ forms. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["Hex(0)", "Hex(15)", "Hex(16)", "Hex(-255)", "Hex(32767)", "Hex(32768)", "Hex(-32768)", "Hex(65535)", "Hex(65536)", "Hex(2147483647)", "Hex(2147483648)", "Hex(-2147483648)", "Hex(1.5)", "Hex(2.5)",
                "Hex(3.5)", "Hex(CByte(255))", "Hex(CInt(-1))", "Hex(CLng(-1))", "Hex('FF')", "Hex('15')", "Hex(True)", "Hex(#2024-03-05#)", "Hex(CCur(15.5))", "Hex('')", "Hex(4294967295)", "Hex(1E10)", "Hex$(255)",
                "Oct(0)", "Oct(7)", "Oct(-1)", "Oct(CInt(-1))", "Oct(CLng(-1))", "Oct(1.5)", "Oct(2.5)", "Oct(32768)", "Oct(2147483648)", "Oct(4294967296)", "Oct(CByte(255))", "Oct('8')", "Oct(True)", "Oct$(8)"])),
        Read("str-strconv-modes", ["EXPR-INVENTORY", "Branch A"],
            "Asks what StrConv returns for each conversion value (0, 1 upper, 2 lower, 3 proper, 4 wide, 8 narrow, 16 katakana, 32 hiragana, 64 to Unicode, 128 from Unicode, combinations, negative and unknown values) on mixed-case text with punctuation, apostrophe, digits and tab/line separators, on wide and narrow and kana text, on empty, Null and number operands, with and without the LCID argument (US, Turkish, unknown). Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, [.. Over("StrConv('hELLO wORLD-x o''neil 3rd', {0})", "0", "1", "2", "3", "4", "8", "16", "32", "64", "128", "5", "-1", "1025", "256"),
                "StrConv('abc', 4)", "StrConv('\uFF21\uFF22\uFF23', 8)", "StrConv('\u30A2\u30A4', 32)", "StrConv('\u3042\u3044', 16)", "StrConv('\uFF71\uFF72', 16)", "StrConv('abc', 64)", "StrConv('abc', 128)",
                "StrConv(Null, 3)", "StrConv('', 3)", "StrConv(123, 1)", "StrConv('\u00E9cole', 3)", "StrConv('\u00C9COLE', 3)", "StrConv('ab cd', 3, 1033)", "StrConv('ab cd', 3, 1055)", "StrConv('abc', 3, 99999)",
                "StrConv('i', 1, 1055)", "StrConv('i', 1, 1033)", "StrConv('abc')", "StrConv('a b', 3.5)", "StrConv('a b', '3')", "StrConv('ab' & Chr(9) & 'cd' & Chr(13) & 'ef', 3)", "StrConv('ab_cd.ef,gh', 3)",
                "StrConv('1st 2nd', 3)", "StrConv('McDonald', 3)"])),

        // Format (EXPR-INVENTORY).
        Read("fmt-numeric-patterns", ["EXPR-INVENTORY", "Branch B"],
            "Asks what Format returns for numeric pattern characters (zero and hash placeholders, thousands separators and scaling commas, percent, exponent forms, literal text, escapes, sections), half-way values at the displayed precision, Null in a section, text and Boolean operands, and large and tiny magnitudes. Results are locale-dependent and the locale is recorded. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["Format(1234.5, '#,##0.00')", "Format(1234.5, '0.0')", "Format(1234.5, '#.##')", "Format(12, '000000')", "Format(0.5, '0.00%')", "Format(12345.678, '0.00E+00')", "Format(1234.5, '#,##0')",
                "Format(1234.5, '$#,##0.00')", "Format(-5, '0;(0)')", "Format(0, '0;(0);zero')", "Format(Null, '0;(0);zero;nil')", "Format(5, '\\x0')", "Format(1234567.891, '#,##0.0')", "Format(0.5, '#.##')", "Format(0.5, '0.##')",
                "Format(1.5, '0')", "Format(2.5, '0')", "Format(0.125, '0.00')", "Format(0.135, '0.00')", "Format(-0.5, '0')", "Format(1E10, '0')", "Format(123456789012345678, '0')", "Format(0.000123, '0.00E-00')", "Format(5, '#')",
                "Format(0, '#')", "Format(12, '00000')", "Format(1234, '0,0')", "Format(1234, '#,')", "Format(1234567, '#,##0,')", "Format(0.5, '0%')", "Format(1, '0.0#')", "Format(-1234.5, '#,##0.00;(#,##0.00)')",
                "Format(1234.5, '\"EUR \"0.00')", "Format(1234.5, '0.00 \"kr\"')", "Format(1.5, '0.')", "Format(1, '@')", "Format(1.5, '.00')", "Format(1.5, '0;')", "Format(1, 'x')", "Format(1, '')", "Format(1.5)", "Format(1E20)",
                "Format(0.00001)", "Format(1/3)", "Format(CCur(1234.5), '0.00')", "Format(CDec(1234.5), '0.00')", "Format(CSng(0.1), '0.000000000')", "Format(0.1, '0.00000000000000000')", "Format(True, '0')", "Format(True)",
                "Format(True, 'Yes/No')", "Format(1234.5, '0.00;;;')", "Format(0, '0.00;;\"nil\"')"])),
        Read("fmt-named-and-text-formats", ["EXPR-INVENTORY", "Branch B"],
            "Asks what Format returns for each named numeric format (General Number, Currency, Fixed, Standard, Percent, Scientific, Yes/No, True/False, On/Off) on positive, negative, zero and Null operands and in other letter case, and for the text-section characters (@, &, <, >, !) on text, empty, Null, number and a non-text pattern use. Locale-dependent and recorded. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, [.. Over("Format(1234.567, '{0}')", "General Number", "Currency", "Fixed", "Standard", "Percent", "Scientific", "Yes/No", "True/False", "On/Off", "general number", "CURRENCY"),
                "Format(-1234.567, 'Currency')", "Format(0, 'Currency')", "Format(Null, 'Currency')", "Format(0, 'Yes/No')", "Format(2, 'Yes/No')", "Format(0, 'On/Off')", "Format(0.5, 'Percent')", "Format(0.001234, 'Scientific')",
                "Format(0, 'True/False')", "Format('abc', '@@@@@')", "Format('abc', '!@@@@@')", "Format('abc', '>')", "Format('ABC', '<')", "Format('abc', '@;empty')", "Format('', '@;empty')", "Format(Null, '@;empty;nil')",
                "Format('abc', '&&')", "Format('abc', '&&&&')", "Format('abc', '>&&')", "Format('12345', '@@-@@@')", "Format(5, '@@@')", "Format('abc', '0')", "Format('abc', '#')", "Format('abc', 'General Date')", "Format('abc', '\"x\"@')"])),
        Read("fmt-date-patterns", ["EXPR-INVENTORY", "Branch C"],
            "Asks what Format returns for a Date (2024-03-05 14:07:09) under each date-pattern character and named date format (year, quarter, month names, weekday names, week of year, day of year, 12- and 24-hour clocks, minutes versus months, AM/PM and a/p markers, escaped slash, literal text) and for a bare date or time operand. Locale-dependent and recorded. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, Over("Format(" + Dt + ", '{0}')", "yyyy-mm-dd hh:nn:ss", "d/m/yy", "dddd, mmmm d, yyyy", "ddd mmm", "h:nn AM/PM", "hh:nn:ss ampm", "q", "ww", "y", "w", "ttttt", "yyyy-\"W\"ww",
                "dd-mmm-yyyy", "m/d/yyyy", "mmmmm", "dddddd", "ddddd", "c", "mm:ss", "yy", "yyy", "D", "H:N:S", "hh:mm", "nn", "AM/PM", "a/p", "A/P", "m", "n", "dd\\/mm\\/yyyy",
                "General Date", "Long Date", "Medium Date", "Short Date", "Long Time", "Medium Time", "Short Time"))),
        Read("fmt-date-arguments", ["EXPR-INVENTORY", "Branch C"],
            "Asks what Format returns for the first-day-of-week and first-week-of-year arguments on week patterns (values 0 to 4 and invalid ones), and for non-Date operands in date patterns: fractions, zero, negative, the range limits, numeric text, non-date text, Null and a bare time. Locale-dependent and recorded. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["Format(0.5, 'hh:nn')", "Format(-0.25, 'hh:nn')", "Format(1.5, 'yyyy-mm-dd')", "Format(0, 'yyyy-mm-dd')", "Format(-0.25, 'yyyy-mm-dd hh:nn')", "Format(#2024-03-05#, 'yyyy-mm-dd hh:nn:ss')",
                "Format(#14:07:09#, 'yyyy-mm-dd')", "Format(#2024-12-30#, 'ww', 2, 2)", "Format(#2024-12-30#, 'ww', 1, 1)", "Format(#2024-12-30#, 'ww', 2, 3)", "Format(#2024-12-30#, 'ww', 2, 0)", "Format(#2024-12-30#, 'ww', 8)",
                "Format(#2024-12-30#, 'ww', 1, 5)", "Format(#2024-03-03#, 'w', 2)", "Format(#2024-03-03#, 'w', 0)", "Format(#2024-03-03#, 'w', 7)", "Format('2024-03-05', 'yyyy')", "Format('x', 'yyyy')", "Format(Null, 'yyyy')",
                "Format(45356, 'yyyy-mm-dd')", "Format(2958466, 'yyyy-mm-dd')", "Format(-657435, 'yyyy')", "Format(" + Dt + ")", "Format(#2024-03-05#)", "Format(#14:07:09#)", "Format('45356', 'yyyy-mm-dd')", "Format(True, 'yyyy-mm-dd')"])),

        // Date and time functions (Branch C).
        Read("date-dateadd-intervals", ["Branch C", "EXPR-INVENTORY"],
            "Asks what DateAdd returns for each interval code (yyyy, q, m, y, d, w, ww, h, n, s) and invalid or differently written codes (upper case, padded, long, numeric, empty), month-end and leap-day clamping, fractional and huge amounts, text and numeric dates, dates near the range limits and an amount past Long. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, [.. Over("DateAdd('{0}', 1, #2024-02-29 12:00:00#)", "yyyy", "q", "m", "y", "d", "w", "ww", "h", "n", "s"),
                "DateAdd('m', 1, #2024-12-31#)", "DateAdd('m', -1, #2024-03-31#)", "DateAdd('m', 12, #2024-02-29#)", "DateAdd('yyyy', -1, #2024-02-29#)", "DateAdd('d', 1.5, " + Dt + ")", "DateAdd('d', 0.5, " + Dt + ")",
                "DateAdd('d', -0.5, " + Dt + ")", "DateAdd('h', 24, " + Dt + ")", "DateAdd('h', 0.5, " + Dt + ")", "DateAdd('s', 0.5, " + Dt + ")", "DateAdd('n', 1.5, " + Dt + ")", "DateAdd('n', 2.5, " + Dt + ")",
                "DateAdd('s', 1E10, " + Dt + ")", "DateAdd('yyyy', 8000, " + Dt + ")", "DateAdd('yyyy', -2000, " + Dt + ")", "DateAdd('D', 1, " + Dt + ")", "DateAdd(' d', 1, " + Dt + ")", "DateAdd('dd', 1, " + Dt + ")",
                "DateAdd('days', 1, " + Dt + ")", "DateAdd(1, 1, " + Dt + ")", "DateAdd('', 1, " + Dt + ")", "DateAdd('d', '1', " + Dt + ")", "DateAdd('d', 'x', " + Dt + ")", "DateAdd('d', 1, '2024-02-28')", "DateAdd('d', 1, 45000)",
                "DateAdd('d', 1, -0.25)", "DateAdd('d', 1, 0)", "DateAdd('d', -1, 0)", "DateAdd('d', 1, 'x')", "DateAdd('d', 2147483647, " + Dt + ")", "DateAdd('d', 2147483648, " + Dt + ")", "DateAdd('d', 1, #1/1/100#)",
                "DateAdd('d', -1, #1/1/100#)", "DateAdd('d', 1, #12/31/9999#)", "DateAdd('d', 0.99999999, #1/1/2024#)"])),
        Read("date-datediff-boundaries", ["Branch C", "EXPR-INVENTORY"],
            "Asks what DateDiff returns at calendar boundaries for each interval (quarter and month across year end, leap day, weekday-based w, week-based ww under every first-day-of-week value 0 to 8, day/hour/minute/second across midnight by one second), for fractional day operands including pre-1899 negatives, for the widest ranges in seconds and days, text operands, and the first-week-of-year argument at its valid and invalid values. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["DateDiff('q', #2024-03-31#, #2024-04-01#)", "DateDiff('m', #2024-12-31#, #2025-01-01#)", "DateDiff('m', #2024-01-31#, #2024-02-29#)", "DateDiff('yyyy', #2024-12-31#, #2024-01-01#)",
                "DateDiff('w', #2024-03-01#, #2024-03-08#)", "DateDiff('w', #2024-03-03#, #2024-03-09#)", "DateDiff('w', #2024-03-09#, #2024-03-10#)",
                .. Over("DateDiff('ww', #2024-03-09#, #2024-03-10#, {0})", "0", "1", "2", "3", "4", "5", "6", "7", "8"),
                "DateDiff('d', #2024-03-10 23:59:59#, #2024-03-11 00:00:01#)", "DateDiff('h', #2024-03-10 23:59:59#, #2024-03-11 00:00:01#)", "DateDiff('n', #2024-03-10 23:59:59#, #2024-03-11 00:00:01#)",
                "DateDiff('s', #2024-03-10 23:59:59#, #2024-03-11 00:00:01#)", "DateDiff('d', 45000.9, 45001.1)", "DateDiff('d', -0.25, 0.25)", "DateDiff('d', -0.75, -0.25)", "DateDiff('d', 0, 1)",
                "DateDiff('s', #1900-01-01#, #2024-03-05#)", "DateDiff('s', #1/1/100#, #12/31/9999#)", "DateDiff('d', #1/1/100#, #12/31/9999#)", "DateDiff('d', '2024-03-01', '2024-03-05')", "DateDiff('d', 'x', #2024-03-05#)",
                "DateDiff('y', #2024-01-01#, #2024-12-31#)", "DateDiff('yyyy', #2024-01-01#, #2024-01-02#, 1, 0)", "DateDiff('yyyy', #2024-01-01#, #2024-01-02#, 1, 3)", "DateDiff('yyyy', #2024-01-01#, #2024-01-02#, 1, 4)",
                "DateDiff('d', #2024-03-05#, #2024-03-05#, 1, 9)", "DateDiff('d', 1, 2.5)", "DateDiff('d', True, False)"])),
        Read("date-datepart-parts", ["Branch C", "EXPR-INVENTORY"],
            "Asks what DatePart returns for each interval code on one date-time, for weekday with every first-day-of-week value, for week of year across year boundaries (2018-12-31, 2020-12-31, 2021-01-03, 2024-12-31) under each first-week-of-year value 0 to 3 and invalid values, for day of year at year ends, and for fractional, zero, negative and range-limit operands, text and an unknown interval. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, [.. Over("DatePart('{0}', " + Dt + ")", "yyyy", "q", "m", "y", "d", "w", "ww", "h", "n", "s"),
                .. Over("DatePart('w', #2024-03-03#, {0})", "0", "1", "2", "3", "4", "5", "6", "7", "8"),
                .. Over("DatePart('ww', #2024-12-31#, 1, {0})", "0", "1", "2", "3", "4"),
                .. Over("DatePart('ww', #2020-12-31#, 1, {0})", "1", "2", "3"),
                .. Over("DatePart('ww', #2018-12-31#, 2, {0})", "1", "2", "3"),
                .. Over("DatePart('ww', #2021-01-03#, 1, {0})", "1", "2", "3"),
                "DatePart('y', #2024-12-31#)", "DatePart('y', #2023-12-31#)", "DatePart('d', '2024-03-05')", "DatePart('h', 0.75)", "DatePart('h', -0.25)", "DatePart('s', 0.999999)", "DatePart('n', 0.5)",
                "DatePart('yyyy', 0)", "DatePart('yyyy', -0.25)", "DatePart('yyyy', -1)", "DatePart('m', 0)", "DatePart('d', -1)", "DatePart('q', #2024-12-31#)", "DatePart('ww', #2024-03-05#, 8)", "DatePart('x', " + Dt + ")",
                "DatePart('yyyy', #12/31/9999#)", "DatePart('yyyy', #1/1/100#)"])),
        Read("date-serial-edges", ["Branch C", "EXPR-INVENTORY"],
            "Asks what DateSerial and TimeSerial return for out-of-range and rolling arguments (day and month zero and negative, month 14, day 32), two-digit and three-digit years around the century pivot, year 0 and past 9999, fractional and text arguments, hour and minute and second carries and negatives, and values past Integer width; and the Double payload (CDbl) of the day before and after 1899-12-30. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["DateSerial(2024, 0, 0)", "DateSerial(2024, 1, 0)", "DateSerial(2024, 3, 0)", "DateSerial(2024, -1, 1)", "DateSerial(2024, 1, -30)", "DateSerial(2024, 14, 32)", "DateSerial(0, 1, 1)",
                "DateSerial(29, 1, 1)", "DateSerial(30, 1, 1)", "DateSerial(99, 12, 31)", "DateSerial(100, 1, 1)", "DateSerial(1899, 12, 30)", "DateSerial(1899, 12, 29)", "DateSerial(1900, 2, 29)", "DateSerial(1900, 1, 0)",
                "DateSerial(9999, 12, 31)", "DateSerial(9999, 12, 32)", "DateSerial(1, 1, 1)", "DateSerial(2024.5, 1.5, 1.5)", "DateSerial(2024, 2, 29.5)", "DateSerial(-1, 1, 1)", "DateSerial(32768, 1, 1)",
                "DateSerial('2024', '2', '3')", "DateSerial(2024, 'x', 1)", "DateSerial(CByte(24), 1, 1)", "DateSerial(2024, 1, 2147483648)", "DateSerial(100, 1, -1)",
                "TimeSerial(0, 0, 0)", "TimeSerial(12, 30, 45)", "TimeSerial(-1, 0, 0)", "TimeSerial(24, 0, 0)", "TimeSerial(0, 90, 0)", "TimeSerial(0, 0, 3600)", "TimeSerial(0, -1, 0)", "TimeSerial(12, 0, -1)",
                "TimeSerial(23, 59, 60)", "TimeSerial(12.5, 0, 0)", "TimeSerial(0, 0, 0.5)", "TimeSerial(32768, 0, 0)", "TimeSerial(100000, 0, 0)", "TimeSerial(0, 0, 86400)", "TimeSerial('12', '30', '0')",
                "CDbl(TimeSerial(18, 0, 0))", "CDbl(DateSerial(1899, 12, 30))", "CDbl(DateSerial(1899, 12, 29))", "CDbl(DateSerial(1, 1, 1))", "CDbl(DateSerial(100, 1, 1))", "CDbl(DateSerial(9999, 12, 31))",
                "CDbl(TimeSerial(-6, 0, 0))"])),
        Read("date-weekday-forms", ["Branch C", "EXPR-INVENTORY"],
            "Asks what Weekday returns for 2024-03-03 under each first-day-of-week value from -1 to 8, for the Automation date zero and its neighbours, a fractional negative, Null, text, a time-carrying date, the range limits, a fractional or text first-day argument, and invalid argument counts. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, [.. Over("Weekday(#2024-03-03#, {0})", "-1", "0", "1", "2", "3", "4", "5", "6", "7", "8"),
                "Weekday(0)", "Weekday(1)", "Weekday(-1)", "Weekday(-0.25)", "Weekday(Null)", "Weekday('2024-03-03')", "Weekday(#2024-03-03 23:59:59#)", "Weekday(2958465)", "Weekday(-657434)", "Weekday('x')",
                "Weekday(1.9)", "Weekday(#2024-03-03#, '2')", "Weekday(#2024-03-03#, 2.5)", "Weekday(#2024-03-03#, Null)", "Weekday()", "Weekday(#2024-03-03#, 2, 3)", "Weekday(True)"])),
        Read("date-names", ["Branch C", "EXPR-INVENTORY"],
            "Asks what WeekdayName and MonthName return for each index (including 0, past the end, fractional and text), the abbreviate flag at True/False/Null/number, and WeekdayName's first-day argument across -1 to 8; and the VarType of each. Names are locale-dependent and the locale is recorded. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, [.. Over("WeekdayName({0})", "0", "1", "7", "8", "1.5", "2.5", "'3'", "True", "-1"),
                .. Over("MonthName({0})", "0", "1", "12", "13", "1.5", "2.5", "'3'", "True", "-1"),
                "WeekdayName(1, True)", "WeekdayName(1, False)", "WeekdayName(1, Null)", "WeekdayName(1, 2)", "WeekdayName(1, 'x')", "MonthName(1, True)", "MonthName(1, False)", "MonthName(1, Null)", "MonthName(1, 2)",
                .. Over("WeekdayName(1, False, {0})", "-1", "0", "1", "2", "7", "8"), "WeekdayName(7, False, 7)", "WeekdayName(1, True, 2)", "VarType(WeekdayName(1))", "VarType(MonthName(1))"])),
        Read("date-component-functions", ["Branch C", "Branch A", "EXPR-INVENTORY"],
            "Asks what Year, Month, Day, Hour, Minute and Second return at the Automation range limits (below and above them), for zero and negative days, fractional days (including negative), numeric and non-date text, Boolean, and a numeric-looking year; and what DateValue and TimeValue return for date-time, month-name, 12- and 24-hour and out-of-range text, a number, Null, and the Double payload of each. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["Year(0)", "Year(-1)", "Year(-657434)", "Year(-657435)", "Year(2958465)", "Year(2958466)", "Month(0)", "Day(0)", "Day(-1)", "Hour(0.5)", "Hour(0.999999)", "Hour(-0.5)", "Hour(-0.75)",
                "Minute(0.50069444)", "Minute(-0.25)", "Second(0.00001157407407)", "Second(0.999988426)", "Year('2024-03-05')", "Year(2024)", "Year('x')", "Year(True)", "Month(1.5)", "Month('3')",
                "DateValue(" + Dt + ")", "TimeValue(" + Dt + ")", "TimeValue('14:07:09')", "TimeValue('2:07 PM')", "TimeValue('25:00')", "TimeValue(Null)", "DateValue(0.5)", "DateValue(-0.25)", "DateValue('March 5, 2024')",
                "DateValue('2024-03-05 14:00')", "DateValue('x')", "TimeValue('2024-03-05')", "TimeValue(45000.5)", "CDbl(TimeValue('12:00'))", "CDbl(DateValue(#2024-03-05 12:00#))", "CDbl(TimeValue('12:00 AM'))"])),

        // Math (Branch B, EXPR-INVENTORY).
        Read("num-abs-sgn-int-fix", ["Branch B", "EXPR-INVENTORY"],
            "Asks the value and inner type of Abs, Sgn, Int and Fix for fractions, negative zero, very large magnitudes, text, Boolean, Date, Byte, Single, Currency and Decimal operands, and at integer type limits. Each call is its own snapshot SELECT expr, VarType(expr), TypeName(expr) FROM One.",
            (p, n) => Typed(p, n, ["Abs(-2.5)", "Abs(-0.0)", "Abs(CByte(1))", "Abs(-1E308)", "Abs('-3')", "Abs('x')", "Abs(True)", "Abs(#2024-03-05#)", "Abs(CCur(-1.5))", "Abs(CDec(-1.5))", "Abs(CSng(-1.5))", "Abs(CInt(-32768))",
                "Abs(CLng(-2147483648))", "Abs(-9223372036854775808)", "Sgn(0)", "Sgn(-3)", "Sgn(CCur(-1))", "Sgn('3')", "Sgn(True)", "Sgn(1E-300)", "Sgn(-0.0)", "Sgn(CDec(-1))", "Sgn(#2024-03-05#)",
                "Int(2.7)", "Int(-2.7)", "Int(-0.0)", "Int(CCur(-2.7))", "Int(CSng(-2.7))", "Int('2.7')", "Int(True)", "Int(" + Dt + ")", "Int(1E20)", "Int(-1E20)", "Int(2147483647.9)", "Int(CDec(-2.5))", "Int(CByte(5))",
                "Fix(2.7)", "Fix(-2.7)", "Fix(CCur(-2.7))", "Fix(CSng(-2.7))", "Fix('-2.7')", "Fix(True)", "Fix(" + Dt + ")", "Fix(-1E20)", "Fix(CDec(-2.5))", "Fix(CByte(5))", "Fix(-0.9999999999999999)"])),
        Read("num-sqr-exp-log-trig", ["Branch B", "EXPR-INVENTORY"],
            "Asks what Sqr, Exp, Log, Sin, Cos, Tan and Atn return or raise at zero, exact squares, large and tiny arguments, the underflow and overflow thresholds of Exp, negative and zero arguments of Log, huge angles, and numeric text; and whether Log10, Atan2, Pi, Sinh and Sec are admitted names. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["Sqr(0)", "Sqr(16)", "Sqr(-0.0)", "Sqr('16')", "Sqr(1E308)", "Sqr(1E-320)", "Exp(0)", "Exp(1)", "Exp(-745)", "Exp(-746)", "Exp(709)", "Exp(709.78)", "Exp(709.79)", "Log(-1)", "Log(2.718281828459045)",
                "Log(1E-308)", "Log(CDec(10))", "Log(10) / Log(10)", "Log('10')", "Log(True)", "Sin(0)", "Sin(1)", "Cos(0)", "Tan(1)", "Atn(0)", "Atn(1E308)", "Sin(1E22)", "Cos(3.141592653589793)", "Sin(3.141592653589793)",
                "Exp(Log(10))", "Sqr(2) * Sqr(2)", "Sqr(2) * Sqr(2) = 2", "Sin(1E300)", "Tan(1.5707963267948966)", "Sin('1')", "Cos(True)", "Sin(CCur(1))", "Sin(CDec(1))", "Sin(CSng(1))",
                "Log10(100)", "Atan2(1, 1)", "Pi()", "Pi", "Sinh(1)", "Sec(1)", "Tanh(1)", "Cot(1)", "Atn(1) * 4 = 3.141592653589793"])),
        Read("num-power-operator", ["Branch B"],
            "Asks the value and inner type of the ^ operator for integer, negative, fractional, zero and large exponents and bases, for mixed Byte, Integer, Currency, Decimal and text operands, Boolean and Null operands, unary-minus precedence and right-to-left chaining, and its overflow and underflow limits. Each call is its own snapshot SELECT expr, VarType(expr), TypeName(expr) FROM One.",
            (p, n) => Typed(p, n, ["2 ^ 3", "2 ^ -1", "2 ^ 10", "2 ^ 31", "2 ^ 63", "2 ^ 64", "(-2) ^ 3", "(-2) ^ 2", "-2 ^ 2", "2 ^ 3 ^ 2", "(2 ^ 3) ^ 2", "0 ^ -1", "0 ^ 1", "10 ^ 308", "10 ^ 309", "10 ^ -324",
                "CInt(2) ^ CInt(15)", "CByte(2) ^ CByte(7)", "2 ^ '3'", "'2' ^ 3", "True ^ 2", "Null ^ 1", "1 ^ Null", "2 ^ 0.0", "(-2) ^ 0.5", "4 ^ 0.5", "1.1 ^ 2", "CCur(2) ^ 2", "CDec(2) ^ 2", "2 ^ CCur(0.5)",
                "(-1) ^ 1.5", "0 ^ 0.5", "-0.0 ^ 2", "1E200 ^ 2", "2 ^ 1023", "2 ^ 1024", "2 ^ -1074", "2 ^ -1075", "(-8) ^ 3", "'x' ^ 2", "2 ^ 'x'"])),
        Read("arith-overflow-promotion", ["Branch B"],
            "Asks where +, -, *, / and unary minus widen or overflow and what each result's inner type is, with literal and explicitly typed Byte/Integer/Long/Single/Double/Currency/Decimal/Boolean/Date operands near their limits, division results by operand type, Currency at its edges, and Double at overflow and denormal. Each expression is its own snapshot SELECT expr, VarType(expr), TypeName(expr) FROM One.",
            (p, n) => Typed(p, n, ["32767 * 2", "CInt(32767) * CInt(2)", "65536 * 65536", "2147483647 * 2", "CLng(2147483647) * CLng(2)", "-2147483648 - 1", "CLng(-2147483648) - CLng(1)", "-CInt(-32768)", "-CLng(-2147483648)",
                "-CByte(1)", "-True", "-'3'", "-'x'", "+'3'", "+Null", "+True", "CByte(200) + CByte(100)", "CByte(200) * 2", "CByte(5) - CByte(10)", "CInt(100) * CInt(400)", "CInt(100) * 400", "CInt(-32768) - CInt(1)",
                "CInt(1) / CInt(2)", "CInt(4) / CInt(2)", "CByte(4) / CByte(2)", "CLng(4) / CLng(2)", "CCur(1) / CCur(3)", "CCur(1) * CCur(1)", "CCur(922337203685477.5807) + CCur(0.0001)", "CCur(922337203685477) * 2",
                "CCur(1) * 1.5", "CCur(0.0001) * CCur(0.0001)", "CSng(16777216) + CSng(1)", "CSng(1) / CSng(3)", "CSng(1) + CDbl(0.1)", "1 / 3 * 3", "0.1 * 3", "0.1 + 0.2", "7 / 2 * 2", "1E308 + 1E308", "-1E308 - 1E308",
                "5E-324 / 2", "2147483647 + 0.5", "CDec(1) / CDec(3) * 3", "CDec(0.1) + CDec(0.2)", "CDec(1E28) * 10", "True + True", "True * True", "True - True", "True / True", "#1/1/2000# - #1/1/1999#", "#1/1/2000# * 2", "#1/1/2000# / 2"])),
        Read("arith-intdiv-mod-types", ["Branch B"],
            "Asks the value, inner type and failure of \\ and Mod at integer-width limits and sign extremes (-32768 and -2147483648 divided by -1, equal operands), operands above Long, Currency, Decimal, Date, Boolean and numeric-text operands, and Byte divisors. Each expression is its own snapshot SELECT expr, VarType(expr), TypeName(expr) FROM One.",
            (p, n) => Typed(p, n, ["CInt(32767) \\ CInt(-1)", "CInt(-32768) \\ CInt(-1)", "-2147483648 \\ -1", "CLng(-2147483648) \\ CLng(-1)", "CInt(-32768) Mod CInt(-1)", "CLng(-2147483648) Mod CLng(-1)", "2147483647 Mod 2147483647",
                "5 \\ CByte(2)", "5 Mod CByte(2)", "CByte(250) \\ CByte(2)", "CByte(250) Mod CByte(7)", "2 ^ 31 \\ 2", "2147483648 Mod 3", "1E10 Mod 3", "1E10 \\ 3", "CCur(7.5) \\ 2", "CCur(7.5) Mod 2", "CDec(7) Mod 2", "CDec(7) \\ 2",
                "#2024-03-05# Mod 7", "True \\ 1", "True Mod 2", "'7' Mod '4'", "'7' \\ '2'", "7 Mod Null", "Null \\ 3", "7 \\ -2", "-7 Mod -2", "7 Mod -2", "0 Mod 5", "0 \\ 5", "CInt(7) \\ 2", "CInt(7) Mod 2", "CSng(7.5) \\ 2",
                "CSng(7.5) Mod 2", "1E15 Mod 7", "1E19 Mod 7", "-0.4 \\ 1", "7 \\ 0.4", "'x' \\ 2", "2 \\ 'x'", "-2147483648 Mod -1"])),
        Read("rnd-seeded-sequences", ["EXPR-RUNTIME", "Branch B"],
            "Asks what the seeded forms of Rnd return and whether they repeat: Rnd with a negative seed followed by unseeded and positive and zero calls in one projection (run twice), the same negative seed twice and the same positive seed twice in one row, fractional, huge, Single/Double/Currency/Decimal and text-numeric seeds, Null, Boolean and Date seeds, a per-row seed over the rows of Mx and a per-row Rnd(-1), and the inner type of the result; and whether Randomize is admitted with and without a seed. Only seeded sequences are asked; the unseeded first value is not.",
            (p, n) =>
            {
                string seq = "SELECT Rnd(-1) AS a, Rnd() AS b, Rnd() AS c, Rnd(-1) AS d, Rnd() AS e, Rnd(1) AS f, Rnd(0) AS g, Rnd(-2) AS h, Rnd() AS i FROM One";
                Sel(p, n, seq);
                Sel(p, n, seq);
                Sel(p, n, "SELECT Rnd(-7) AS a, Rnd(-7) AS b, Rnd(7) AS c, Rnd(7) AS d FROM One");
                Sel(p, n, "SELECT Rnd(-0.5) AS a, Rnd(-1.5) AS b, Rnd(-1E10) AS c, Rnd(-2147483648) AS d, Rnd(-CSng(1)) AS e, Rnd(-CDbl(1)) AS f FROM One");
                Scalars(p, n, ["Rnd(-1)", "Rnd(-1) = Rnd(-1)", "Rnd('-1')", "Rnd(Null)", "Rnd(True)", "Rnd(-True)", "Rnd(#2024-03-05#)", "Rnd(CCur(-1))", "Rnd(CDec(-1))", "Rnd(-1, 2)", "Randomize()", "Randomize(1)",
                    "Randomize(1) + Rnd(-1)", "Randomize"]);
                TypeOf(p, n, ["Rnd(-1)", "Rnd(-1) + 0", "Int(Rnd(-1) * 1000000)", "CSng(Rnd(-1))"]);
                Sel(p, n, "SELECT ID, Rnd(-ID) AS r, Rnd() AS s FROM Mx ORDER BY ID");
                Sel(p, n, "SELECT ID, Rnd(-1) AS r FROM Mx ORDER BY ID");
                Sel(p, n, "SELECT ID, Int(Rnd(-1) * 1000000) AS r FROM Mx ORDER BY ID");
            }),

        // Conditional functions (Branch E).
        Read("cond-iif-forms", ["Branch E", "Branch B"],
            "Asks the result and inner type of IIf for arms of different numeric widths, Null, text, date and mixed types; with a wrong number of arguments, text and numeric conditions, Null conditions, a result used in + and &, and nested to 10, 20, 64 and 128 levels. Each call is its own snapshot SELECT expr, VarType(expr), TypeName(expr) FROM One.",
            (p, n) => Typed(p, n, ["IIf(True, 1, 1.5)", "IIf(False, 1, 1.5)", "IIf(True, CByte(1), CInt(1))", "IIf(True, CInt(1), CLng(1))", "IIf(True, 1, CCur(1))", "IIf(True, Null, 1)", "IIf(False, 1, Null)",
                "IIf(True, 'a')", "IIf(True)", "IIf()", "IIf(True, 1, 2, 3)", "IIf(True, #2024-03-05#, 'x')", "IIf(False, #2024-03-05#, 'x')", "IIf(Null = Null, 1, 2)", "IIf(Null Is Null, 1, 2)", "IIf(True, 'a', Null) & 'b'",
                "IIf(1, 2, 3)", "IIf(2, 'T', 'F')", "IIf(True, 1, 2) + 1", "IIf(True, '1', 2) + 1", "IIf(True, '1', 2) & 1", "IIf(True, 1, 'a') + 1", "IIf(False, 1, 'a') + 1", "IIf(True, 2147483647, 1)",
                "IIf(True, 1, 2.5E-300)", "IIf(Null Is Not Null, 1, 2)", "IIf(IsNull(Null), 'n', 'v')", "IIf(1 < 2, 'lt', 'ge')", "IIf('a' < 'b', 1, 2)",
                IifNest(10), IifNest(20), IifNest(64), IifNest(128)])),
        Read("cond-switch-choose-forms", ["Branch E", "Branch B"],
            "Asks the result and inner type of Switch and Choose for arms of different widths and types, Null results, an odd number of Switch arguments, no arguments, a Switch condition that is text, numeric or Null, Choose selectors that are Byte, Boolean, negative, past Long or text, a Choose with a Null or text arm, and Switch and Choose with 20 and 100 arms. Each call is its own snapshot SELECT expr, VarType(expr), TypeName(expr) FROM One.",
            (p, n) => Typed(p, n, ["Switch(True, 1, True, 2)", "Switch(1, 'a')", "Switch('x', 1)", "Switch(0, 1, -1, 2)", "Switch(False, 1, False, 2)", "Switch(True, Null)", "Switch(True, 1, True, 'a')", "Switch(False, 1, True, 'a')",
                "Switch(1 = 1, 1.5)", "Switch(True, CByte(1), False, 2)", "Switch(True, 1, 2)", "Switch(True, 1, True)", "Switch(Null, 1)", "Switch(False, 1, Null, 2, True, 3)",
                "Choose(1)", "Choose(1, Null)", "Choose(2, 'a', Null, 'c')", "Choose(CByte(2), 'a', 'b')", "Choose(True, 'a', 'b')", "Choose(-1, 'a', 'b')", "Choose(2147483648, 'a')", "Choose('x', 'a')", "Choose(1, 1, 'a')",
                "Choose(2, 1, 'a')", "Choose(2, 1, 2.5)", "Choose(1, 2.5, 'a')", "Choose(2, 'a', 'b', 'c') & Choose(3, 'x', 'y', 'z')", "Choose(1, 'a', 'b') + 1", "Choose(CSng(2), 'a', 'b')", "Choose(CCur(2), 'a', 'b')",
                SwitchPairs(20), SwitchPairs(100), ChooseMany(20, 20), ChooseMany(100, 100), ChooseMany(100, 101)])),

        // Operators (Branch B, Branch D).
        Read("op-concat-number-forms", ["Branch B", "EXPR-INVENTORY"],
            "Asks the text that & produces from numeric, Boolean, Date, Currency, Decimal, Single and Byte operands of many magnitudes (the switch to exponent form, trailing zeros, repeating fractions, integers above Long, denormals) and from chains of operands. Dates render in the oracle's locale, which is recorded. Each expression is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, Over("{0} & ''", "1.5", "1E10", "1E15", "1E16", "0.1 + 0.2", "(0.1 + 0.2)", "CSng(1/3)", "1/3", "True", Dt, "#2024-03-05#", "#14:30#", "CCur(1.5)", "CCur(1)", "CDec(1.50)", "-0.0", "1E-5",
                "0.0001", "100000000000000000000", "CByte(5)", "1.0", "1.10", "CDbl(1)", "-1.5", "-1", "2147483648", "123456789.123456789", "CSng(123456789)", "CSng(0.1)", "CDbl(CSng(0.1))", "9007199254740993", "1.5E300", "5E-324")
                .Concat(["5 & 5", "1 & 2 & 3", "'a' & 1 & 'b'", "1 & -1", "1 & +1", "-1 & -1", "1 & .5"]))),
        Read("op-plus-amp-precedence-and-types", ["Branch B", "Branch A"],
            "Asks how + and & and the other arithmetic operators treat Date, Boolean, numeric text, non-numeric text and empty text: the precedence of & against + and *, Date plus number or text, Boolean plus Boolean or text, numeric text in -, *, /, ^, \\ and Mod, empty text with + and &, and the Null tests of '' and Null combinations. Each expression is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["1 + 2 & 3 + 4", "'1' & 2 + 3", "1 & 2 * 3", "'a' & 'b' = 'ab'", "'a' & 'b' & Null", "1 + 2 & 3", "1 & 2 + 3", "#2024-03-05# + 1", "#2024-03-05# + '1'", "#2024-03-05# + #2024-03-05#",
                "#2024-03-05# + 0.5", "'2024-03-05' + 1", "'12:30' + 1", "#2024-03-05# & 1", "True + True", "True & True", "True + '1'", "True & '1'", "'a' + Null", "Null + 'a'", "'1' + '2' + 3", "'1' & '2' + 3",
                "1 - '1'", "'3' - '1'", "'3' * '4'", "'3' / '4'", "'3' ^ '2'", "'3' \\ '2'", "'7' Mod '4'", "-'3'", "'5' - Null", "Null - Null", "#2024-03-05# - '1'", "#2024-03-05# - 1.5", "'a' - 1", "'' + 1", "'' & 1",
                "'' + ''", "Null & ''", "Len(Null & '')", "IsNull(Null & '')", "IsNull(Null + '')", "IsNull('' + Null)", "IsNull(Null & Null)", "Len(Null + 'a')"])),
        Read("op-precedence", ["Branch B", "Branch D"],
            "Asks how Access SQL groups mixed operators without parentheses: unary minus against ^, ^ chaining, * and / against \\ and Mod, + and - against &, comparison against And/Or/Not, Not against comparison and And, Eqv/Imp/Xor against And/Or, and comparison chains. Each expression is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["2 ^ -1", "-2 ^ 2", "2 * 3 ^ 2", "1 + 2 * 3", "7 \\ 2 * 2", "7 Mod 3 * 2", "2 ^ 2 ^ 3", "-3 Mod 2", "1 - -1", "10 \\ 3 ^ 2", "2 * -3 \\ 2", "6 \\ 4 Mod 2", "7 \\ 2 \\ 2", "7 Mod 4 Mod 2", "8 / 4 / 2",
                "2 - 1 - 1", "1 + 2 & 3", "1 & 2 + 3", "1 < 2 = True", "Not 1 = 2", "Not True And False", "Not (True And False)", "True And 1 = 1", "1 = 1 And 2 = 2", "True Or False And False", "(True Or False) And False",
                "True Xor True Or True", "True Or True Xor True", "False Eqv False And False", "True Imp False Or True", "1 + 1 = 2 And 2 > 1", "1 = 1 = 1", "1 = 2 = 2", "3 > 2 > 1", "1 < 2 < 3", "2 > 1 = True", "-2 ^ 2 = 4",
                "- 7 \\ 2", "-(7 \\ 2)", "- 7 Mod 2", "2 ^ 3 * 2", "2 * 3 ^ 2 ^ 2", "'a' & 'b' < 'ac'", "1 + 2 < 4 And 3 + 4 > 6", "Not 1 + 1 = 2", "Not -1", "Not 0", "True + 1 = 0", "4 \\ 2 * 3", "4 Mod 3 ^ 2"])),
        Read("cmp-cross-types", ["Branch B", "Branch C", "Branch D"],
            "Asks how = and < order and equate operands of different types: Boolean against numbers and text, integer against Currency, Decimal, Single and Double of the same decimal text, Date against its serial number, date text and non-date text, time-of-day text against a fraction, empty text and Null, numeric text against numbers with trailing garbage, and equality and ordering of the extreme Double values. Each expression is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["True = 1", "True = -1", "True = '1'", "True = 'True'", "True = 'x'", "False = 0", "False = ''", "True < 1", "True < 'a'", "1 = 1.0", "1 = CCur(1)", "1 = CDec(1)", "0.1 = CSng(0.1)", "CCur(0.1) = 0.1", "CDec(0.1) = 0.1",
                "CDec(0.1) = CDbl(0.1)", "1 = True", "#2024-03-05# = 45356", "#2024-03-05# = '2024-03-05'", "#2024-03-05# > '2024-03-04'", "#2024-03-05# < 'abc'", "'12:00' = 0.5", "CDate('12:00') = 0.5", "Null = ''", "'' = ''", "'' < 'a'",
                "'a' < Null", "2 < '10'", "2 < '10a'", "'abc' = 'ABC'", "'abc' <> 'abd'", "1 <> Null", "Null <> Null", "1E308 = 1E308", "1E308 < 1E309", "-1E308 > -1E309", "0 = -0.0", "1 = '1.0'", "1 = '1 '", "' 1' = 1", "1 = '+1'",
                "CByte(1) = CInt(1)", "CInt(1) = CLng(1)", "CSng(1.5) = CDbl(1.5)", "CCur(1.5) = CSng(1.5)", "CSng(0.1) < 0.1", "CSng(0.1) > 0.1", "#12/30/1899# = 0", "#12/30/1899# = ''", "Empty = 0"])),
        Read("cmp-between-in", ["Branch B", "Branch D", "Branch A"],
            "Asks how Between and In (outside LIKE) treat mixed-type, text-numeric, Null, case and boundary operands: Between with text and numeric bounds, reversed bounds, Null bounds and a Null subject; In with mixed-type lists, a Null in the list, a Null subject, case differences, Currency and Double lists, a one-element list and a long list. Each expression is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["5 Between 1 And 9", "5 Between 9 And 1", "5 Between '1' And '9'", "5 Between 1 And Null", "Null Between 1 And 9", "'a' Between 'A' And 'b'", "'b' Between 'a' And 'B'", "'B' Between 'a' And 'c'",
                "5 Between 5 And 5", "5.5 Between 5 And 6", "#2024-03-05# Between #2024-03-01# And #2024-03-31#", "#2024-03-05# Between '2024-03-01' And '2024-03-31'", "'10' Between 2 And 9", "10 Between '2' And '9'",
                "5 Not Between 1 And 9", "Null Not Between 1 And 9", "5 In (1, '5')", "5 In (1, Null)", "Null In (Null)", "Null In (1, 2)", "'a' In ('A')", "'a' In ('b', 'A')", "1.5 In (1, 2)", "1.5 In (1, 1.5)", "CCur(1.5) In (1.5)",
                "1 In (1)", "5 In ('5')", "'5' In (5)", "5 Not In (1, 2)", "5 Not In (1, Null)", "Null Not In (1, 2)", "True In (1, -1)", "-1 In (True)", "7 In (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20)",
                "#2024-03-05# In (45356)", "'abc' In ('ABC')", "IIf(5 In (5), 'y', 'n')"])),
        Read("cmp-stored-lanes", ["Branch B", "Branch A", "Branch C"],
            "Asks how comparison and arithmetic between stored columns of different types evaluate over the five rows of Mx (N Long, T Text, D Double, DT Date, Y Yes/No with Null and empty values): each column pair or operation in its own snapshot SELECT ID, expr FROM Mx ORDER BY ID, plus the selection WHERE T = N, WHERE N = T, WHERE T < 10 and WHERE T = D.",
            (p, n) =>
            {
                foreach (string e in new[] { "N = T", "N < T", "T < N", "N = Y", "N = D", "DT = N", "DT < N", "DT = T", "T = D", "T > D", "Y = T", "Y = D", "N + T", "N & T", "T + D", "D = DT", "D < T", "Nz(N) = Nz(T)",
                    "Nz(T) = ''", "T = ''", "Len(T) = 0", "T Is Null", "N Is Null", "N In (7, 0)", "T In ('7', '')", "T Between '1' And '9'", "N Between -5 And 5", "DT Between 0 And 45000", "Y = True", "Y = 0", "N = Y + 8" })
                {
                    Sel(p, n, $"SELECT ID, {e} AS v FROM Mx ORDER BY ID");
                }
                foreach (string w in new[] { "T = N", "N = T", "T < 10", "T = D", "N = Y", "DT = N", "T Is Null OR T = ''", "NOT (N > 0)", "N > 0 OR T = 'abc'" })
                {
                    Sel(p, n, $"SELECT ID FROM Mx WHERE {w} ORDER BY ID");
                }
            }),

        // Field metadata of function results (Branch B, EXPR-INVENTORY).
        Read("meta-text-function-field-types", ["Branch B", "EXPR-INVENTORY"],
            "Asks the DAO Field Type, Size and Attributes (and first-row Value) of the column that a text-producing function or concatenation yields, including results of 255, 256, 300 and 5000 characters, Replace that grows past 255, $ forms, Null-returning forms, and text from Nz and IIf. Each expression is its own snapshot SELECT expr AS v FROM One.",
            (p, n) => MetaEach(p, n, ["Left('abc', 2)", "Left$('abc', 2)", "Space(3)", "Space(300)", "String(255, 'a')", "String(256, 'a')", "String(5000, 'a')", "Format(1, '0')", "Chr(65)", "CStr(1)", "UCase('a')", "Hex(1)",
                "Str(1)", "Trim('a')", "Replace('abc', 'a', 'b')", "Replace(String(200, 'a'), 'a', 'bb')", "'a' & 'b'", "String(200, 'a') & String(100, 'b')", "Mid('abc', 1)", "IIf(True, 'a', 'b')", "Nz(Null, '')", "Switch(True, 'a')",
                "Choose(1, 'a')", "MonthName(1)", "WeekdayName(1)", "StrConv('a', 1)", "StrReverse('abc')", "Left(Null, 1)", "'a' & Null", "'abc'", "String(255, 'a') & 'b'", "String(250, 'a') & String(5, 'b')", "Format(1.5)", "FormatNumber(1.5)", "Join(Split('a b', ' '), '-')"])),
        Read("meta-conversion-field-types", ["Branch B", "EXPR-INVENTORY"],
            "Asks the DAO Field Type, Size and Attributes (and first-row Value) of the column that each conversion, numeric, date and literal expression yields (C* functions, Int/Fix/Abs/Sgn/Sqr/Round, Len/InStr/Asc/Val, date functions, Rnd, IIf and Nz forms, Null, literals of each magnitude, and mixed division). Each expression is its own snapshot SELECT expr AS v FROM One.",
            (p, n) => MetaEach(p, n, ["CInt(1)", "CLng(1)", "CByte(1)", "CCur(1)", "CSng(1)", "CDbl(1)", "CDec(1)", "CBool(1)", "CDate(1)", "CStr(1)", "CVar(1)", "Int(1.5)", "Fix(1.5)", "Abs(-1)", "Sgn(1)", "Sqr(4)", "Len('a')",
                "InStr('a', 'a')", "Asc('a')", "Round(1.5)", "Val('1')", "Weekday(Date())", "Year(Date())", "DateAdd('d', 1, Date())", "DateDiff('d', Date(), Date())", "Now()", "Rnd(-1)", "VarType(1)", "IsNull(1)", "IIf(True, 1, 2)",
                "IIf(True, 1, 2.5)", "Nz(Null, 0)", "Null", "1", "1.5", "'a'", "True", "#2024-03-05#", "-1", "2147483648", "1E10", "1/2", "1 + 1", "CInt(1) + CInt(1)", "CByte(1) + CByte(1)", "CCur(1) + 1", "1 \\ 1", "1 Mod 1", "2 ^ 2",
                "True + 1", "TypeName(1)", "TimeSerial(1, 1, 1)", "DateSerial(2024, 1, 1)", "Timer()", "Hour(Now())", "FormatNumber(1.5)", "CVDate(1)", "Partition(5, 0, 10, 2)"])),
        Read("meta-memo-functions", ["Branch B", "Branch A", "EXPR-INVENTORY"],
            "Asks the DAO Field Type, Size, Attributes and Value of functions and operators applied to a stored 300-character Memo (Lg.Mm; row 2 holds Null): Len, Left (short and longer than 255), Mid, UCase, Trim, Replace, & and + with text on either side, Format, StrConv, IIf and Nz with the Memo, CStr, InStr, comparison with text, and whether LenB and Format with a pattern accept it. Each expression is its own snapshot SELECT expr AS v FROM Lg, rows 1 and 2.",
            (p, n) =>
            {
                foreach (string e in new[] { "Len(Mm)", "LenB(Mm)", "Left(Mm, 10)", "Left(Mm, 300)", "Mid(Mm, 299, 5)", "Right(Mm, 5)", "UCase(Mm)", "Trim(Mm)", "Replace(Mm, 'a', 'b')", "Replace(Mm, 'a', 'bb')", "Mm & 'x'", "'x' & Mm", "Mm + 'x'",
                    "Mm & Mm", "Format(Mm, '>')", "Format(Mm, '@@')", "StrConv(Mm, 1)", "IIf(True, Mm, 'x')", "IIf(False, Mm, 'x')", "Nz(Mm)", "Nz(Mm, 'x')", "CStr(Mm)", "InStr(Mm, 'j')", "InStr(Mm, 'z')", "Mm = T", "Mm < 'b'", "Mm Is Null",
                    "Len(Mm & Mm)", "VarType(Mm)", "TypeName(Mm)", "Space(Len(Mm))", "StrReverse(Mm)", "Val(Mm)", "Left(Mm, 255)", "Left(Mm, 256)", "Switch(True, Mm)", "Choose(1, Mm)" })
                {
                    MetaEach(p, n, [e], "Lg", 1);
                }
                MetaEach(p, n, ["Len(Mm)", "Left(Mm, 10)", "Mm & 'x'", "Nz(Mm)", "Nz(Mm, 'x')", "IIf(IsNull(Mm), 'n', 'v')", "Mm Is Null", "VarType(Mm)"], "Lg", 2);
            }),

        // Sandbox and availability (EXPR-INVENTORY).
        Read("sandbox-vba-library", ["EXPR-INVENTORY"],
            "Asks which less common VBA library functions the expression service admits through DAO and what they return: FormatNumber, FormatCurrency, FormatPercent and FormatDateTime with their digit/parenthesis/grouping/named-format arguments, Format$, CVDate, Partition, RGB, QBColor, Spc, Tab, IMEStatus, and the Is* family (IsMissing, IsObject, IsError, IsEmpty, IsArray). Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["FormatNumber(1234.5, 2)", "FormatNumber(1234.5)", "FormatNumber(-1234.5, 2, 0, -1)", "FormatNumber(0.5, 2, -1)", "FormatNumber(1234.5, 2, -1, 0, 0)", "FormatNumber(Null)", "FormatNumber('x')",
                "FormatCurrency(1234.5)", "FormatCurrency(1234.5, 2)", "FormatCurrency(-1234.5, 2, 0, -1)", "FormatPercent(0.5)", "FormatPercent(0.5, 1)", "FormatPercent(0.5, 1, 0, 0, 0)",
                "FormatDateTime(" + Dt + ")", .. Over("FormatDateTime(" + Dt + ", {0})", "0", "1", "2", "3", "4", "5", "-1"), "FormatDateTime(Null)", "FormatDateTime('x')", "Format$(1, '0')", "CVDate(1)", "CVDate('2024-03-05')",
                "CVDate(Null)", "Partition(5, 0, 10, 2)", "Partition(-1, 0, 10, 2)", "Partition(11, 0, 10, 2)", "Partition(5, 0, 100, 10)", "Partition(Null, 0, 10, 2)", "Partition(5, 0, 10, 0)", "Partition(5, 10, 0, 2)",
                "RGB(1, 2, 3)", "RGB(255, 255, 255)", "RGB(256, 0, 0)", "RGB(-1, 0, 0)", "QBColor(1)", "QBColor(15)", "QBColor(16)", "Spc(3)", "Tab(3)", "IMEStatus()", "IsMissing(1)", "IsObject(Null)", "IsError(CVErr(5))", "IsEmpty(1)",
                "IsArray(1)", "Len(Spc(3))", "Space(3) & 'x'", "CVErr(5)", "Error(5)", "Error$(5)", "Err()", "Erl()"])),
        Read("sandbox-system-and-file-functions", ["EXPR-INVENTORY"],
            "Asks which environment, path and file-information functions the expression service admits through DAO, called only with names that do not exist (an environment variable R_EXPR_1B_NOVAR and the path C:\\r-expr-1b-nofile.txt): CurDir and Dir (also with an attribute argument, and the no-argument continuation), FileLen, FileDateTime and GetAttr on a missing file and on the root directory, Environ and Environ$, Command, and the file-number functions EOF, LOF, Loc, Seek and FreeFile. No process, dialog or write is started, and no registry function is called. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["CurDir()", "CurDir$()", "CurDir('C')", "Dir('C:\\r-expr-1b-nofile*')", "Dir('C:\\r-expr-1b-nofile*', 16)", "Dir$('C:\\r-expr-1b-nofile*')", "Dir()", "FileLen('" + NoFile + "')", "FileDateTime('" + NoFile + "')",
                "GetAttr('" + NoFile + "')", "GetAttr('C:\\')", "FileLen('C:\\')", "Environ('R_EXPR_1B_NOVAR')", "Environ$('R_EXPR_1B_NOVAR')", "Environ('R_EXPR_1B_NOVAR') & 'x'", "Len(Environ('R_EXPR_1B_NOVAR'))", "Command()", "Command$()", "EOF(1)", "LOF(1)", "Loc(1)", "Seek(1)", "FreeFile()",
                "Timer()"])),
        Read("sandbox-array-functions", ["EXPR-INVENTORY", "Branch A"],
            "Asks which array-valued functions the expression service admits and what an array-typed result does at the DAO boundary: Array, Split, Join, Filter, UBound and LBound over them, indexing the result in place, IsArray/VarType/TypeName of an array, the financial functions that take an array (IRR, NPV, MIRR), and CVar of an array. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["Array(1, 2, 3)", "UBound(Array(1, 2, 3))", "LBound(Array(1, 2))", "Join(Array('a', 'b'), ',')", "Join(Array('a', 'b'))", "Split('a,b,c', ',')", "Split('a b')", "UBound(Split('a,b,c', ','))",
                "Filter(Array('ab', 'b'), 'a')", "IsArray(Array(1))", "VarType(Array(1))", "TypeName(Array(1))", "Array(1, 2)(0)", "Split('a,b', ',')(1)", "Split('a,b', ',')(2)", "IRR(Array(-100, 50, 60))", "NPV(0.1, Array(10, 20))",
                "MIRR(Array(-100, 50, 60), 0.1, 0.1)", "CVar(Array(1))", "UBound(1)", "LBound('a')", "UBound(Null)", "Join(Split('a b', ' '), '-')", "Join(Array(1, 2), 0)", "Split('')", "UBound(Split(''))", "Split(Null)", "Array()", "UBound(Array())"])),
        Read("sandbox-access-hosted-names", ["EXPR-INVENTORY", "EXPR-DOMAIN"],
            "Asks which Access-application names the expression service admits when no Access application hosts it (DAO query context): CurrentUser, CurrentDb, Application and CurrentProject members, Forms, Reports and TempVars references, Screen, SysCmd, IsLoaded, HyperlinkPart, the domain aggregates DMax/DMin/DAvg/DFirst/DLast/DStDev/DVar/DSum/DCount/DLookup with and without criteria, and Eval of the function names that were refused directly. Each call is its own snapshot SELECT expr FROM One.",
            (p, n) => Scalars(p, n, ["CurrentUser()", "CurrentDb()", "Application.Version", "Application.Name", "Application.CurrentProject.Name", "CurrentProject.Name", "Forms!NoForm!x", "Reports!NoReport!x", "TempVars!x", "TempVars('x')",
                "Screen.ActiveForm", "SysCmd(7)", "IsLoaded('x')", "HyperlinkPart('http://a.b/c#d', 0)", "DMax('ID', 'One')", "DMin('ID', 'One')", "DAvg('ID', 'One')", "DFirst('ID', 'One')", "DLast('ID', 'One')", "DStDev('ID', 'One')",
                "DVar('ID', 'One')", "DSum('ID', 'One')", "DCount('*', 'One')", "DLookup('ID', 'One', 'ID = 1')", "DLookup('ID', 'Mx', 'ID = 2')", "DLookup('N', 'Mx', 'ID = 2')", "DLookup('ID', 'NoSuchTable')", "DLookup('NoField', 'One')",
                "DCount('*', 'Mx', 'N > 0')", "Eval('Environ(\"R_EXPR_1B_NOVAR\")')", "Eval('CurDir()')", "Eval('Format(1, \"0\")')", "Eval('DLookup(\"ID\", \"One\")')", "Eval('NoSuchFunc(1)')", "Eval('CurrentUser()')",
                "Eval('2 ^ 0.5')", "Eval('Array(1)(0)')", "Eval('IIf(True, 1, 1/0)')", "Eval('1' & Chr(0) & '2')"])),

        // Other routes (Branch A, EXPR-STAGE, EXPR-MUTATION).
        Read("route-recordset-filter-sort", ["Branch E", "EXPR-STAGE", "EXPR-INVENTORY"],
            "Asks which functions the expression service evaluates in a Recordset.Filter and Recordset.Sort on a dynaset over Mx, and the rows a recordset opened from each Filter returns: Null and Nz tests, Len, InStr, Mid, Format, CInt on a text column that does not convert, an unknown function, CurDir and Environ, IIf, DateDiff, and a Sort with a plain field, a descending field, a field list and a function. A rejected Filter or Sort is an observation; the Filter is cleared before each Sort.",
            (p, n) =>
            {
                p.OpenRs("fr", "db", "SELECT ID, N, T FROM Mx ORDER BY ID", Dao.OpenDynaset);
                foreach (string f in new[] { "IsNull(N)", "Nz(N, 0) > 0", "Len(T) > 1", "InStr(T, 'b') > 0", "Mid(T, 1, 1) = 'a'", "Format(D, '0') = '3'", "CInt(T) = 7", "NoSuchFunc(N) = 1", "CurDir() <> ''",
                    "Environ('R_EXPR_1B_NOVAR') = ''", "IIf(IsNull(N), 0, N) < 1", "DateDiff('d', DT, #2024-03-05#) > 0", "Val(T) = 12", "T = ''", "T Is Null", "N Between -5 And 5", "N In (7, 0)", "Not (N > 0)", "1/0 = 1" })
                {
                    string r2 = n.Next("flt");
                    p.Put("fr", "Filter", A.Str(f)).Get("fr", "Filter").CallBind(r2, "fr", "OpenRecordset");
                    Rows(p, r2, 8);
                    p.Call(r2, "Close").Release(r2);
                }
                p.Put("fr", "Filter", A.Str(""));
                foreach (string s in new[] { "N", "N DESC", "T, N", "T DESC, ID", "Len(T)", "Nz(N, 0)", "NoSuchField" })
                {
                    string r2 = n.Next("srt");
                    p.Put("fr", "Sort", A.Str(s)).Get("fr", "Sort").CallBind(r2, "fr", "OpenRecordset");
                    Rows(p, r2, 8);
                    p.Call(r2, "Close").Release(r2);
                }
                p.Call("fr", "Close").Release("fr");
            }),
        Read("route-action-query-expressions", ["EXPR-MUTATION", "EXPR-STAGE", "Branch E"],
            "Asks how expressions behave inside action queries on a writable copy: INSERT ... SELECT with Format over Mx, UPDATE SET with Replace and with Left/Len under a WHERE function, INSERT ... SELECT with CInt over text that does not convert (and the same over convertible rows), UPDATE with a division by zero on every row and on one row, UPDATE and DELETE with an unknown function, INSERT with a refused-name candidate (Environ), INSERT VALUES with function calls, DELETE under a function predicate, and UPDATE setting Null. After each statement the RecordsAffected and every row of the scratch table W are read, so partial effects and the statement's atomicity are visible.",
            (p, n) =>
            {
                Exec(p, "CREATE TABLE W (ID LONG CONSTRAINT PK_W PRIMARY KEY, V TEXT(100))");
                foreach (string sql in new[]
                {
                    "INSERT INTO W (ID, V) SELECT ID, Format(D, '0.00') FROM Mx",
                    "UPDATE W SET V = Replace(V, '.', ',')",
                    "UPDATE W SET V = Left(V, 2) & '|' & Len(V) WHERE Len(V) > 3",
                    "INSERT INTO W (ID, V) SELECT ID + 100, CStr(CInt(T)) FROM Mx",
                    "INSERT INTO W (ID, V) SELECT ID + 200, CStr(CInt(T)) FROM Mx WHERE ID IN (1, 5)",
                    "UPDATE W SET V = CStr(1 / (ID - ID))",
                    "UPDATE W SET V = CStr(1 / (ID - 1))",
                    "UPDATE W SET V = NoSuchFunc(V)",
                    "INSERT INTO W (ID, V) SELECT ID + 300, Environ('R_EXPR_1B_NOVAR') FROM Mx WHERE ID = 1",
                    "INSERT INTO W (ID, V) VALUES (500, UCase('x') & Format(5, '000'))",
                    "DELETE FROM W WHERE Len(V) > 3",
                    "DELETE FROM W WHERE NoSuchFunc(ID) = 1",
                    "UPDATE W SET V = Null WHERE IsNull(V) = False AND ID < 10",
                })
                {
                    p.Call("db", "Execute", A.Str(sql)).Get("db", "RecordsAffected");
                    Sel(p, n, "SELECT ID, V FROM W ORDER BY ID");
                }
            }, mutates: true),
        new Probe(Card, "route-ado-and-dao-battery", ["R-EXPR-1", "EXPR-INVENTORY", "Branch B"],
            "Asks whether the same one-expression battery (33 expressions across the function families, including names the sandbox may refuse) gives the same value, tag and failure through DAO and through ADODB.Connection with Provider Microsoft.ACE.OLEDB.12.0 (Mode adModeRead) on the same file: each expression is its own SELECT expr FROM One, first through DAO then through ADO, each recordset reading Name, Type and Value of every field. Both engines' modules are recorded once after both are loaded.",
            p =>
            {
                var n = new Names();
                p.Files("before-open").Create().Get("eng", "Version").OpenDb().Get("db", "Version");
                Scalars(p, n, Battery);
                AdoOpen(p);
                foreach (string e in Battery)
                {
                    AdoSel(p, n, $"SELECT {e} FROM One");
                }
                p.Call("cn", "Close").Release("cn").FinishLite("db");
            })
        { Fixtures = [("main", Main)] },
        Read("route-mdb-battery", ["EXPR-INVENTORY", "Branch B", "Branch C"],
            "Asks whether the same one-expression battery as route-ado-and-dao-battery gives the same value, tag and failure through DAO when the database is an MDB (Jet 4 format, same collation) rather than an ACCDB, plus date, rounding and text-compare expressions whose behaviour may depend on the file format, and the stored-column projections over Mx. Each expression is its own snapshot SELECT expr FROM One.",
            (p, n) =>
            {
                Scalars(p, n, Battery);
                Scalars(p, n, ["CInt(2.5)", "CInt(3.5)", "Round(2.5)", "Round(0.125, 2)", "CDate('1/1/30')", "DateAdd('m', 1, #2024-01-31#)", "DatePart('ww', #2024-12-31#, 1, 3)", "StrComp('a', 'A', 2)", "InStr(1, 'ABC', 'b', 2)",
                    "'a' = 'A'", "'\u00E4' = 'a'", "Len('\uD83D\uDE00')", "UCase('\u00DF')", "Chr(128)", "ChrW(8364)", "Hex(-1)", "CCur(1.00005)", "7 \\ 2.5", "7 Mod 2.5", "5 + Null", "IIf(Null, 1, 2)", "CDec(1)", "CLng(2147483648)"]);
                Sel(p, n, "SELECT ID, N, T, IsNull(N) AS inN, Nz(T, 'none') AS nzT, Len(T) AS lT, N & T AS c, IIf(IsNull(T), 'null', IIf(T = '', 'empty', T)) AS cls FROM Mx ORDER BY ID");
                MetaEach(p, n, ["Left('abc', 2)", "String(300, 'a')", "CInt(1)", "CDec(1)", "Now()", "1 / 2", "Left(Mm, 10)", "Mm & 'x'", "Nz(Mm)", "Len(Mm)", "Format(1, '0')"], "Lg", 1);
            }, fixture: Mdb),
    ];

    // Probe factories --------------------------------------------------------------------------------

    private static Probe Read(string name, string[] refs, string what, Action<P, Names> body, bool mutates = false, string fixture = Main) =>
        new(Card, name, ["R-EXPR-1", .. refs],
            what + $" Fixture {fixture} is opened {(mutates ? "on a writable fresh copy" : "read-only")} through DAO.DBEngine.120; engine version and the loaded modules (once) are recorded; each recordset reads Name, Type and Value of every field.",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version").OpenDb(readOnly: !mutates).Get("db", "Version").Get("db", "CollatingOrder");
                body(p, n);
                p.FinishLite("db");
            })
        { Fixtures = [("main", fixture)] };

    private static Probe Creation(string fixture, string fileName, int version, string what, Action<P, Names> body) =>
        new(Card, "create-" + fixture[(Card.Length + 1)..], ["R-EXPR-1", "RQ-SEM-01", "RQ-VAL-02", "RQ-EXPR-01"],
            $"R-EXPR-1 fixture {fixture}: tables One (one row), Mx (five rows of N Long, T Text with Null, empty and padded values, D Double, DT Date and Y Yes/No, with T set to allow a zero-length string) and Lg (a 300-character Memo, its Null row, and a Text(255) column). {what}; values are stored through table-type AddNew/Value/Update; the closed file is kept.",
            p =>
            {
                var n = new Names();
                p.EngineLite().Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out(fileName), A.Str(Dao.LangGeneral), A.I4(version))
                    .Get("db", "Version").Get("db", "CollatingOrder");
                body(p, n);
                p.FinishLite("db");
            })
        { CreatesFixture = true };

    /// <summary>Tables One, Mx and Lg, created and filled through DAO DDL and table-type AddNew (no Decimal, no reserved column names).</summary>
    private static void Fixture(P p, Names n)
    {
        Exec(p,
            "CREATE TABLE One (ID LONG CONSTRAINT PK_One PRIMARY KEY)",
            "INSERT INTO One (ID) VALUES (1)",
            "CREATE TABLE Mx (ID LONG CONSTRAINT PK_Mx PRIMARY KEY, N LONG, T TEXT(30), D DOUBLE, DT DATETIME, Y YESNO)",
            "CREATE TABLE Lg (ID LONG CONSTRAINT PK_Lg PRIMARY KEY, Mm MEMO, T TEXT(255))");
        p.GetBind("tdfs", "db", "TableDefs").Call("tdfs", "Refresh").Item("tmx", "tdfs", A.Str("Mx")).GetBind("tmf", "tmx", "Fields")
            .Item("tmt", "tmf", A.Str("T")).Put("tmt", "AllowZeroLength", A.Bool(true)).Get("tmt", "AllowZeroLength")
            .Release("tmt").Release("tmf").Release("tmx").Release("tdfs");
        AddRows(p, n, "Mx", ["ID", "N", "T", "D", "DT", "Y"],
        [
            [A.I4(1), A.I4(7), A.Str("7"), A.R8(2.5), A.Date(45000.75), A.Bool(true)],
            [A.I4(2), A.Null, A.Str("abc"), A.R8(-2.5), A.Null, A.Bool(false)],
            [A.I4(3), A.I4(0), A.Str(""), A.R8(0.5), A.Date(45000.0), A.Bool(false)],
            [A.I4(4), A.I4(-3), A.Null, A.Null, A.Date(-0.25), A.Bool(true)],
            [A.I4(5), A.I4(int.MaxValue), A.Str(" 12 "), A.R8(1e10), A.Date(0.0), A.Bool(false)],
        ]);
        AddRows(p, n, "Lg", ["ID", "Mm", "T"],
        [
            [A.I4(1), A.Str(string.Concat(Enumerable.Repeat("abcdefghij", 30))), A.Str("abcdefghij")],
            [A.I4(2), A.Null, A.Null],
        ]);
    }

    // Program helpers ---------------------------------------------------------------------------------

    private static void Exec(P p, params string[] statements)
    {
        foreach (string sql in statements)
        {
            p.Call("db", "Execute", A.Str(sql));
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

    private static void Sel(P p, Names n, string sql, int type = Dao.OpenSnapshot, int maxRows = 16)
    {
        string rs = n.Next("rs");
        p.OpenRs(rs, "db", sql, type);
        Rows(p, rs, maxRows);
        p.Call(rs, "Close").Release(rs);
    }

    /// <summary>Each expression in its own snapshot <c>SELECT expr FROM One</c>.</summary>
    private static void Scalars(P p, Names n, IEnumerable<string> exprs)
    {
        foreach (string e in exprs)
        {
            Sel(p, n, $"SELECT {e} FROM One");
        }
    }

    /// <summary>The value, the inner VarType and the TypeName of each expression, each in its own snapshot.</summary>
    private static void Typed(P p, Names n, IEnumerable<string> exprs)
    {
        foreach (string e in exprs)
        {
            Sel(p, n, $"SELECT {e} AS v, VarType({e}) AS vt, TypeName({e}) AS tn FROM One");
        }
    }

    /// <summary>The inner VarType and TypeName of each expression, each in its own snapshot.</summary>
    private static void TypeOf(P p, Names n, IEnumerable<string> exprs)
    {
        foreach (string e in exprs)
        {
            Sel(p, n, $"SELECT VarType({e}) AS vt, TypeName({e}) AS tn FROM One");
        }
    }

    /// <summary>Name, Type, Size, Attributes and Value of every field of the first row of the snapshot.</summary>
    private static void Meta(P p, Names n, string sql)
    {
        string rs = n.Next("mrs"), fl = rs + "f", f = rs + "x";
        p.OpenRs(rs, "db", sql, Dao.OpenSnapshot)
            .GetBind(fl, rs, "Fields")
            .Each(fl, f, 64, x => x.Get(f, "Name").Get(f, "Type").Get(f, "Size").Get(f, "Attributes").Get(f, "Value"))
            .Release(fl).Call(rs, "Close").Release(rs);
    }

    /// <summary>Metadata of each expression as column v of <paramref name="table"/> (row <paramref name="id"/>, ID = 1 for One).</summary>
    private static void MetaEach(P p, Names n, IEnumerable<string> exprs, string table = "One", int id = 1)
    {
        foreach (string e in exprs)
        {
            Meta(p, n, $"SELECT {e} AS v FROM {table} WHERE ID = {id}");
        }
    }

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

    /// <summary>Each template with <c>{0}</c> replaced by each item.</summary>
    private static IEnumerable<string> Over(string template, params string[] items) =>
        items.Select(i => template.Replace("{0}", i, StringComparison.Ordinal));

    /// <summary>Every function name applied to every argument text.</summary>
    private static IEnumerable<string> Cross(string[] functions, params string[] args) =>
        functions.SelectMany(f => args.Select(a => $"{f}({a})"));

    private static string IifNest(int depth) =>
        string.Concat(Enumerable.Repeat("IIf(True, ", depth)) + "1" + string.Concat(Enumerable.Repeat(", 0)", depth));

    /// <summary>A Switch of <paramref name="pairs"/> pairs in which only the last condition is True.</summary>
    private static string SwitchPairs(int pairs) =>
        "Switch(" + string.Concat(Enumerable.Range(1, pairs - 1).Select(i => $"False, {i}, ")) + $"True, {pairs})";

    /// <summary>A Choose of <paramref name="count"/> text arms, selecting arm <paramref name="select"/> (past the end when larger).</summary>
    private static string ChooseMany(int count, int select) =>
        $"Choose({select}, " + string.Join(", ", Enumerable.Range(1, count).Select(i => $"'c{i}'")) + ")";
}
