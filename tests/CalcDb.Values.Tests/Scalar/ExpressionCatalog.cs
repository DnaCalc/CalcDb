using CalcDb.Values.Scalar;

using static CalcDb.Values.Scalar.ScalarOperation;
using static CalcDb.Values.Tests.Scalar.ExpressionHarness;

namespace CalcDb.Values.Tests.Scalar;

/// <summary>
/// The questions of the retained R-EXPR-1 records that the scalar kernel answers (T-EXPR-11, 13, 14, 15, 16, 19, 21, 22),
/// each with its record, its exact SQL text and its tree. Expected answers are read from the record; none is written here.
/// A case that names a <see cref="NotModelledCase"/> is a declared open item (docs/work/qualified.md), counted apart.
/// Left out: questions over stored fields (their operands come from fixture rows, W-06), questions whose literal typing is
/// the parser's open question (<c>-0.0</c> is VarType Long: num-abs-sgn-int-fix), clock-dependent CDate forms
/// ('Mar 5'), and functions outside this kernel (StrComp, ChrW, String, Space, LenB, Val, Str).
/// </summary>
public static class ExpressionCatalog
{
    private const string PlusConcat = "r-expr-1/b-plus-versus-concat/r-expr-1.b-plus-versus-concat.963f485c1c.a1.json";
    private const string Widening = "r-expr-1/b-integer-widening/r-expr-1.b-integer-widening.52ba244d05.a1.json";
    private const string IntDivMod = "r-expr-1/b-intdiv-mod/r-expr-1.b-intdiv-mod.60456b370a.a1.json";
    private const string IntConversion = "r-expr-1/b-integer-conversion/r-expr-1.b-integer-conversion.4a417fa48a.a1.json";
    private const string Logical = "r-expr-1/d-logical-bitwise/r-expr-1.d-logical-bitwise.952f06510f.a1.json";
    private const string NullLiterals = "r-expr-1/a-null-empty-error-literals/r-expr-1.a-null-empty-error-literals.f2882098b8.a1.json";
    private const string Overflow = "r-expr-1b/arith-overflow-promotion/r-expr-1b.arith-overflow-promotion.8e33d497ff.a1.json";
    private const string IntDivTypes = "r-expr-1b/arith-intdiv-mod-types/r-expr-1b.arith-intdiv-mod-types.dee0e58c4e.a1.json";
    private const string PlusAmp = "r-expr-1b/op-plus-amp-precedence-and-types/r-expr-1b.op-plus-amp-precedence-and-types.674d84115d.a1.json";
    private const string ConcatForms = "r-expr-1b/op-concat-number-forms/r-expr-1b.op-concat-number-forms.94303d4918.a1.json";
    private const string AbsSgn = "r-expr-1b/num-abs-sgn-int-fix/r-expr-1b.num-abs-sgn-int-fix.cf5081e6a8.a1.json";
    private const string Power = "r-expr-1b/num-power-operator/r-expr-1b.num-power-operator.0c7a57c1f3.a1.json";
    private const string CrossTypes = "r-expr-1b/cmp-cross-types/r-expr-1b.cmp-cross-types.e898adce72.a1.json";
    private const string TextOperators = "r-expr-1b/str-strcomp-and-text-operators/r-expr-1b.str-strcomp-and-text-operators.e1b7794263.a1.json";
    private const string NullFunctions = "r-expr-1b/null-propagation-functions/r-expr-1b.null-propagation-functions.b2d04aed9f.a1.json";
    private const string CBoolCVar = "r-expr-1b/conv-cbool-cvar/r-expr-1b.conv-cbool-cvar.6ee08b62c8.a1.json";
    private const string CStrForms = "r-expr-1b/conv-cstr-forms/r-expr-1b.conv-cstr-forms.302477daf4.a1.json";
    private const string RangeEdges = "r-expr-1b/conv-range-edges/r-expr-1b.conv-range-edges.01b85c3b66.a1.json";
    private const string TextToInteger = "r-expr-1b/conv-text-to-integer/r-expr-1b.conv-text-to-integer.b0637e2413.a1.json";
    private const string TextToFloating = "r-expr-1b/conv-text-to-floating/r-expr-1b.conv-text-to-floating.3213e9b55c.a1.json";
    private const string RoundDigits = "r-expr-1b/conv-round-function-digits/r-expr-1b.conv-round-function-digits.8f79dca0d3.a1.json";
    private const string HalfRounding = "r-expr-1b/conv-half-rounding-large/r-expr-1b.conv-half-rounding-large.11d003a414.a1.json";
    private const string LenForms = "r-expr-1b/str-len-lenb-forms/r-expr-1b.str-len-lenb-forms.3150f4cb9d.a1.json";
    private const string CDateForms = "r-expr-1b/conv-cdate-forms/r-expr-1b.conv-cdate-forms.d5a3fec393.a1.json";
    private const string VarTypes = "r-expr-1b/info-result-vartype-numeric/r-expr-1b.info-result-vartype-numeric.2ca10df6b5.a1.json";
    private const string FieldTypes = "r-expr-1b/meta-conversion-field-types/r-expr-1b.meta-conversion-field-types.bef199cacc.a1.json";
    private const string AccessHost = "r-access-host/eval-context/access-smoke-eval-context.a1.json";

    private static X Fn(ScalarOperation op, X a, X? b = null) => Op(op, a, b);

    /// <summary>All cases, by record.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ExpressionCase>> ByRecord { get; } = Build();

    private static Dictionary<string, IReadOnlyList<ExpressionCase>> Build()
    {
        var all = new List<ExpressionCase>();

        // "SELECT e FROM One": the first field.
        void Plain(string record, string e, X tree, NotModelledCase open = NotModelledCase.None) =>
            all.Add(new ExpressionCase(record, $"SELECT {e} FROM One", tree, null, null, open));
        // "SELECT e AS v, VarType(e) AS vt, TypeName(e) AS tn FROM One": the value and the inner VarType.
        void Typed(string record, string e, X tree, NotModelledCase open = NotModelledCase.None) =>
            all.Add(new ExpressionCase(record, $"SELECT {e} AS v, VarType({e}) AS vt, TypeName({e}) AS tn FROM One", tree, "v", "vt", open));
        // "SELECT VarType(e) AS vt, TypeName(e) AS tn FROM One": the inner VarType only.
        void InnerType(string record, string e, X tree) =>
            all.Add(new ExpressionCase(record, $"SELECT VarType({e}) AS vt, TypeName({e}) AS tn FROM One", tree, "vt", "vt", NotModelledCase.None));
        // "SELECT e AS v FROM One WHERE ID = 1".
        void Meta(string e, X tree) =>
            all.Add(new ExpressionCase(FieldTypes, $"SELECT {e} AS v FROM One WHERE ID = 1", tree, "v", null, NotModelledCase.None));

        // ---- + versus & (T-EXPR-11), r-expr-1 b-plus-versus-concat ----
        Plain(PlusConcat, "7 + '7'", Add(I(7), S("7")));
        Plain(PlusConcat, "'7' + '7'", Add(S("7"), S("7")));
        Plain(PlusConcat, "'7' + 7", Add(S("7"), I(7)));
        Plain(PlusConcat, "7 & '7'", Cat(I(7), S("7")));
        Plain(PlusConcat, "7 & 7", Cat(I(7), I(7)));
        Plain(PlusConcat, "7 + Null", Add(I(7), N));
        Plain(PlusConcat, "Null + '7'", Add(N, S("7")));
        Plain(PlusConcat, "Null & Null", Cat(N, N));
        Plain(PlusConcat, "7 & Null", Cat(I(7), N));
        Plain(PlusConcat, "'q' + 7", Add(S("q"), I(7)));
        Plain(PlusConcat, "'q' + 'q'", Add(S("q"), S("q")));
        Plain(PlusConcat, "'7' - '2'", Sub(S("7"), S("2")));
        Plain(PlusConcat, "'7' * '2'", Mul(S("7"), S("2")));
        Plain(PlusConcat, "True & ''", Cat(T, S("")));
        Plain(PlusConcat, "#2024-01-01# & ''", Cat(Dt(2024, 1, 1), S("")));

        // ---- Integer widening (T-EXPR-14), r-expr-1 b-integer-widening ----
        Plain(Widening, "CByte(255) + CByte(1)", Add(Fn(CByte, I(255)), Fn(CByte, I(1))));
        Plain(Widening, "CInt(32767) + CInt(1)", Add(Fn(CInt, I(32767)), Fn(CInt, I(1))));
        Plain(Widening, "CLng(2147483647) + CLng(1)", Add(Fn(CLng, I(2147483647)), Fn(CLng, I(1))));
        Plain(Widening, "2147483647 + 1", Add(I(2147483647), I(1)));
        Plain(Widening, "32767 + 1", Add(I(32767), I(1)));

        // ---- \ and Mod (T-EXPR-15), r-expr-1 b-intdiv-mod ----
        Plain(IntDivMod, "7.5 \\ 2", IDiv(Dc("7.5"), I(2)));
        Plain(IntDivMod, "7 \\ 2.5", IDiv(I(7), Dc("2.5")));
        Plain(IntDivMod, "7 \\ 2.4", IDiv(I(7), Dc("2.4")));
        Plain(IntDivMod, "-7 \\ 2", IDiv(I(-7), I(2)));
        Plain(IntDivMod, "-7.5 \\ 2", IDiv(Dc("-7.5"), I(2)));
        Plain(IntDivMod, "7 Mod 2.5", Mod(I(7), Dc("2.5")));
        Plain(IntDivMod, "7 Mod 2.4", Mod(I(7), Dc("2.4")));
        Plain(IntDivMod, "-7 Mod 2", Mod(I(-7), I(2)));
        Plain(IntDivMod, "7.5 Mod -2", Mod(Dc("7.5"), I(-2)));
        Plain(IntDivMod, "5 \\ 0.4", IDiv(I(5), Dc("0.4")));
        Plain(IntDivMod, "5 Mod 0.4", Mod(I(5), Dc("0.4")));
        Plain(IntDivMod, "5 \\ 0.5", IDiv(I(5), Dc("0.5")));
        Plain(IntDivMod, "1 \\ 0", IDiv(I(1), I(0)));
        Plain(IntDivMod, "1 Mod 0", Mod(I(1), I(0)));
        Plain(IntDivMod, "1 / 0", Div(I(1), I(0)));
        Plain(IntDivMod, "0 / 0", Div(I(0), I(0)));
        Plain(IntDivMod, "0 \\ 0", IDiv(I(0), I(0)));
        Plain(IntDivMod, "2147483648 \\ 1", IDiv(Dc("2147483648"), I(1)));
        Plain(IntDivMod, "7 \\ '2'", IDiv(I(7), S("2")));
        Plain(IntDivMod, "CByte(7) \\ CByte(2)", IDiv(Fn(CByte, I(7)), Fn(CByte, I(2))));
        Plain(IntDivMod, "Null \\ 0", IDiv(N, I(0)));
        Plain(IntDivMod, "0 / Null", Div(I(0), N));

        // ---- Integer conversion and halves (T-EXPR-16), r-expr-1 b-integer-conversion ----
        Plain(IntConversion, "CInt(2.5)", Fn(CInt, Dc("2.5")));
        Plain(IntConversion, "CInt(-2.5)", Fn(CInt, Dc("-2.5")));
        Plain(IntConversion, "CInt(3.5)", Fn(CInt, Dc("3.5")));
        Plain(IntConversion, "CInt(-3.5)", Fn(CInt, Dc("-3.5")));
        Plain(IntConversion, "CInt(2.4999999999999996)", Fn(CInt, Dc("2.4999999999999996")));
        Plain(IntConversion, "CInt(2.5000000000000004)", Fn(CInt, Dc("2.5000000000000004")));
        Plain(IntConversion, "CLng(2.5)", Fn(CLng, Dc("2.5")));
        Plain(IntConversion, "CLng(-0.5)", Fn(CLng, Dc("-0.5")));
        Plain(IntConversion, "CByte(0.5)", Fn(CByte, Dc("0.5")));
        Plain(IntConversion, "CByte(1.5)", Fn(CByte, Dc("1.5")));
        Plain(IntConversion, "Int(-2.5)", Fn(Int, Dc("-2.5")));
        Plain(IntConversion, "Fix(-2.5)", Fn(Fix, Dc("-2.5")));
        Plain(IntConversion, "Round(-2.5)", Fn(Round, Dc("-2.5")));
        Plain(IntConversion, "CInt(32767.4)", Fn(CInt, Dc("32767.4")));
        Plain(IntConversion, "CInt(32767.5)", Fn(CInt, Dc("32767.5")));
        Plain(IntConversion, "CInt(-32768.5)", Fn(CInt, Dc("-32768.5")));
        Plain(IntConversion, "CLng(2147483647.5)", Fn(CLng, Dc("2147483647.5")));
        Plain(IntConversion, "CLng(-2147483648.5)", Fn(CLng, Dc("-2147483648.5")));
        Plain(IntConversion, "CByte(255.5)", Fn(CByte, Dc("255.5")));
        Plain(IntConversion, "CByte(-0.5)", Fn(CByte, Dc("-0.5")));
        Plain(IntConversion, "CByte(-0.6)", Fn(CByte, Dc("-0.6")));
        Plain(IntConversion, "CInt('2.5')", Fn(CInt, S("2.5")));

        // ---- Logical operators (T-EXPR-21), r-expr-1 d-logical-bitwise ----
        (string Name, X Value)[] tfn = [("True", T), ("False", F), ("Null", N)];
        foreach ((string opName, ScalarOperation op) in new[] { ("And", And), ("Or", Or), ("Xor", Xor), ("Eqv", Eqv), ("Imp", Imp) })
        {
            foreach ((string ln, X lv) in tfn)
            {
                foreach ((string rn, X rv) in tfn)
                {
                    all.Add(new ExpressionCase(Logical, "SELECT (True And True) AS [True_And_True]", Op(op, lv, rv), $"{ln}_{opName}_{rn}", null, NotModelledCase.None, Prefix: true));
                }
            }
        }
        Plain(Logical, "6 And 3", Op(And, I(6), I(3)));
        Plain(Logical, "6 Or 3", Op(Or, I(6), I(3)));
        Plain(Logical, "6 Xor 3", Op(Xor, I(6), I(3)));
        Plain(Logical, "Not 6", Op(Not, I(6)));
        Plain(Logical, "6 Eqv 3", Op(Eqv, I(6), I(3)));
        Plain(Logical, "6 Imp 3", Op(Imp, I(6), I(3)));
        Plain(Logical, "True And 3", Op(And, T, I(3)));
        Plain(Logical, "True Or 6", Op(Or, T, I(6)));
        Plain(Logical, "-1 And 3", Op(And, I(-1), I(3)));
        Plain(Logical, "2.5 And 3", Op(And, Dc("2.5"), I(3)));
        Plain(Logical, "3.5 And 7", Op(And, Dc("3.5"), I(7)));
        Plain(Logical, "CByte(6) And CInt(3)", Op(And, Fn(CByte, I(6)), Fn(CInt, I(3))));
        Plain(Logical, "CByte(6) And CLng(3)", Op(And, Fn(CByte, I(6)), Fn(CLng, I(3))));
        Plain(Logical, "Not CByte(6)", Op(Not, Fn(CByte, I(6))));
        Plain(Logical, "'6' And 3", Op(And, S("6"), I(3)));
        Plain(Logical, "'x' And True", Op(And, S("x"), T));
        Plain(Logical, "Null And 0", Op(And, N, I(0)));
        Plain(Logical, "Null Or -1", Op(Or, N, I(-1)));

        // ---- Null, Empty and inspection (T-EXPR-09, T-EXPR-11), r-expr-1 a-null-empty-error-literals ----
        Plain(NullLiterals, "IsNull(Null)", Fn(IsNull, N));
        Plain(NullLiterals, "IsNull('')", Fn(IsNull, S("")));
        Plain(NullLiterals, "IsNull(0)", Fn(IsNull, I(0)));
        Plain(NullLiterals, "IsEmpty(Null)", Fn(IsEmpty, N));
        Plain(NullLiterals, "IsEmpty('')", Fn(IsEmpty, S("")));
        Plain(NullLiterals, "Null + 1", Add(N, I(1)));
        Plain(NullLiterals, "Null & 'x'", Cat(N, S("x")));
        Plain(NullLiterals, "'x' & Null", Cat(S("x"), N));
        Plain(NullLiterals, "Null & Null", Cat(N, N));
        Plain(NullLiterals, "Null = Null", Eq(N, N));
        Plain(NullLiterals, "Null <> 1", Ne(N, I(1)));
        Plain(NullLiterals, "-Null", Neg(N));
        Plain(NullLiterals, "Not Null", Op(Not, N));
        Plain(NullLiterals, "Len(Null)", Fn(Len, N));
        Plain(NullLiterals, "Len('')", Fn(Len, S("")));

        // ---- Overflow and promotion (T-EXPR-13, T-EXPR-14, T-EXPR-17), r-expr-1b arith-overflow-promotion ----
        Typed(Overflow, "32767 * 2", Mul(I(32767), I(2)));
        Typed(Overflow, "CInt(32767) * CInt(2)", Mul(Fn(CInt, I(32767)), Fn(CInt, I(2))));
        Typed(Overflow, "65536 * 65536", Mul(I(65536), I(65536)));
        Typed(Overflow, "2147483647 * 2", Mul(I(2147483647), I(2)));
        Typed(Overflow, "CLng(2147483647) * CLng(2)", Mul(Fn(CLng, I(2147483647)), Fn(CLng, I(2))));
        Typed(Overflow, "-2147483648 - 1", Sub(I(-2147483648), I(1)));
        Typed(Overflow, "CLng(-2147483648) - CLng(1)", Sub(Fn(CLng, I(-2147483648)), Fn(CLng, I(1))));
        Typed(Overflow, "-CInt(-32768)", Neg(Fn(CInt, I(-32768))));
        Typed(Overflow, "-CLng(-2147483648)", Neg(Fn(CLng, I(-2147483648))));
        Typed(Overflow, "-CByte(1)", Neg(Fn(CByte, I(1))));
        Typed(Overflow, "-True", Neg(T));
        Typed(Overflow, "-'3'", Neg(S("3")));
        Typed(Overflow, "-'x'", Neg(S("x")));
        Typed(Overflow, "CByte(200) + CByte(100)", Add(Fn(CByte, I(200)), Fn(CByte, I(100))));
        Typed(Overflow, "CByte(200) * 2", Mul(Fn(CByte, I(200)), I(2)));
        Typed(Overflow, "CByte(5) - CByte(10)", Sub(Fn(CByte, I(5)), Fn(CByte, I(10))));
        Typed(Overflow, "CInt(100) * CInt(400)", Mul(Fn(CInt, I(100)), Fn(CInt, I(400))));
        Typed(Overflow, "CInt(100) * 400", Mul(Fn(CInt, I(100)), I(400)));
        Typed(Overflow, "CInt(-32768) - CInt(1)", Sub(Fn(CInt, I(-32768)), Fn(CInt, I(1))));
        Typed(Overflow, "CInt(1) / CInt(2)", Div(Fn(CInt, I(1)), Fn(CInt, I(2))));
        Typed(Overflow, "CInt(4) / CInt(2)", Div(Fn(CInt, I(4)), Fn(CInt, I(2))));
        Typed(Overflow, "CByte(4) / CByte(2)", Div(Fn(CByte, I(4)), Fn(CByte, I(2))));
        Typed(Overflow, "CLng(4) / CLng(2)", Div(Fn(CLng, I(4)), Fn(CLng, I(2))));
        Typed(Overflow, "CCur(1) / CCur(3)", Div(Fn(CCur, I(1)), Fn(CCur, I(3))));
        Typed(Overflow, "CCur(1) * CCur(1)", Mul(Fn(CCur, I(1)), Fn(CCur, I(1))));
        Typed(Overflow, "CCur(922337203685477.5807) + CCur(0.0001)", Add(Fn(CCur, Dc("922337203685477.5807")), Fn(CCur, Dc("0.0001"))));
        Typed(Overflow, "CCur(922337203685477) * 2", Mul(Fn(CCur, Dc("922337203685477")), I(2)));
        Typed(Overflow, "CCur(1) * 1.5", Mul(Fn(CCur, I(1)), Dc("1.5")));
        Typed(Overflow, "CCur(0.0001) * CCur(0.0001)", Mul(Fn(CCur, Dc("0.0001")), Fn(CCur, Dc("0.0001"))));
        Typed(Overflow, "CSng(16777216) + CSng(1)", Add(Fn(CSng, I(16777216)), Fn(CSng, I(1))));
        Typed(Overflow, "CSng(1) / CSng(3)", Div(Fn(CSng, I(1)), Fn(CSng, I(3))));
        Typed(Overflow, "CSng(1) + CDbl(0.1)", Add(Fn(CSng, I(1)), Fn(CDbl, Dc("0.1"))));
        Typed(Overflow, "1 / 3 * 3", Mul(Div(I(1), I(3)), I(3)));
        Typed(Overflow, "0.1 * 3", Mul(Dc("0.1"), I(3)));
        Typed(Overflow, "0.1 + 0.2", Add(Dc("0.1"), Dc("0.2")));
        Typed(Overflow, "7 / 2 * 2", Mul(Div(I(7), I(2)), I(2)));
        Typed(Overflow, "1E308 + 1E308", Add(R(1E308), R(1E308)));
        Typed(Overflow, "-1E308 - 1E308", Sub(R(-1E308), R(1E308)));
        Typed(Overflow, "5E-324 / 2", Div(R(5E-324), I(2)));
        Typed(Overflow, "2147483647 + 0.5", Add(I(2147483647), Dc("0.5")));
        Typed(Overflow, "True + True", Add(T, T));
        Typed(Overflow, "True * True", Mul(T, T));
        Typed(Overflow, "True - True", Sub(T, T));
        Typed(Overflow, "True / True", Div(T, T));
        Typed(Overflow, "#1/1/2000# - #1/1/1999#", Sub(Dt(2000, 1, 1), Dt(1999, 1, 1)));
        Typed(Overflow, "#1/1/2000# * 2", Mul(Dt(2000, 1, 1), I(2)));
        Typed(Overflow, "#1/1/2000# / 2", Div(Dt(2000, 1, 1), I(2)));

        // ---- \ and Mod result types (T-EXPR-15), r-expr-1b arith-intdiv-mod-types ----
        Typed(IntDivTypes, "CInt(32767) \\ CInt(-1)", IDiv(Fn(CInt, I(32767)), Fn(CInt, I(-1))));
        Typed(IntDivTypes, "CInt(-32768) \\ CInt(-1)", IDiv(Fn(CInt, I(-32768)), Fn(CInt, I(-1))));
        Typed(IntDivTypes, "-2147483648 \\ -1", IDiv(I(-2147483648), I(-1)));
        Typed(IntDivTypes, "CLng(-2147483648) \\ CLng(-1)", IDiv(Fn(CLng, I(-2147483648)), Fn(CLng, I(-1))));
        Typed(IntDivTypes, "CInt(-32768) Mod CInt(-1)", Mod(Fn(CInt, I(-32768)), Fn(CInt, I(-1))));
        Typed(IntDivTypes, "CLng(-2147483648) Mod CLng(-1)", Mod(Fn(CLng, I(-2147483648)), Fn(CLng, I(-1))));
        Typed(IntDivTypes, "2147483647 Mod 2147483647", Mod(I(2147483647), I(2147483647)));
        Typed(IntDivTypes, "5 \\ CByte(2)", IDiv(I(5), Fn(CByte, I(2))));
        Typed(IntDivTypes, "5 Mod CByte(2)", Mod(I(5), Fn(CByte, I(2))));
        Typed(IntDivTypes, "CByte(250) \\ CByte(2)", IDiv(Fn(CByte, I(250)), Fn(CByte, I(2))));
        Typed(IntDivTypes, "CByte(250) Mod CByte(7)", Mod(Fn(CByte, I(250)), Fn(CByte, I(7))));
        Typed(IntDivTypes, "2 ^ 31 \\ 2", IDiv(Pow(I(2), I(31)), I(2)));
        Typed(IntDivTypes, "2147483648 Mod 3", Mod(Dc("2147483648"), I(3)));
        Typed(IntDivTypes, "1E10 Mod 3", Mod(Dc("1E10"), I(3)));
        Typed(IntDivTypes, "1E10 \\ 3", IDiv(Dc("1E10"), I(3)));
        Typed(IntDivTypes, "CCur(7.5) \\ 2", IDiv(Fn(CCur, Dc("7.5")), I(2)));
        Typed(IntDivTypes, "CCur(7.5) Mod 2", Mod(Fn(CCur, Dc("7.5")), I(2)));
        Typed(IntDivTypes, "#2024-03-05# Mod 7", Mod(Dt(2024, 3, 5), I(7)));
        Typed(IntDivTypes, "True \\ 1", IDiv(T, I(1)));
        Typed(IntDivTypes, "True Mod 2", Mod(T, I(2)));
        Typed(IntDivTypes, "'7' Mod '4'", Mod(S("7"), S("4")));
        Typed(IntDivTypes, "'7' \\ '2'", IDiv(S("7"), S("2")));
        Typed(IntDivTypes, "7 Mod Null", Mod(I(7), N));
        Typed(IntDivTypes, "Null \\ 3", IDiv(N, I(3)));
        Typed(IntDivTypes, "7 \\ -2", IDiv(I(7), I(-2)));
        Typed(IntDivTypes, "-7 Mod -2", Mod(I(-7), I(-2)));
        Typed(IntDivTypes, "7 Mod -2", Mod(I(7), I(-2)));
        Typed(IntDivTypes, "0 Mod 5", Mod(I(0), I(5)));
        Typed(IntDivTypes, "0 \\ 5", IDiv(I(0), I(5)));
        Typed(IntDivTypes, "CInt(7) \\ 2", IDiv(Fn(CInt, I(7)), I(2)));
        Typed(IntDivTypes, "CInt(7) Mod 2", Mod(Fn(CInt, I(7)), I(2)));
        Typed(IntDivTypes, "CSng(7.5) \\ 2", IDiv(Fn(CSng, Dc("7.5")), I(2)));
        Typed(IntDivTypes, "CSng(7.5) Mod 2", Mod(Fn(CSng, Dc("7.5")), I(2)));
        Typed(IntDivTypes, "1E15 Mod 7", Mod(Dc("1E15"), I(7)));
        Typed(IntDivTypes, "1E19 Mod 7", Mod(Dc("1E19"), I(7)));
        Typed(IntDivTypes, "-0.4 \\ 1", IDiv(Dc("-0.4"), I(1)));
        Typed(IntDivTypes, "7 \\ 0.4", IDiv(I(7), Dc("0.4")));
        Typed(IntDivTypes, "'x' \\ 2", IDiv(S("x"), I(2)));
        Typed(IntDivTypes, "2 \\ 'x'", IDiv(I(2), S("x")));
        Typed(IntDivTypes, "-2147483648 Mod -1", Mod(I(-2147483648), I(-1)));

        // ---- + and & with every type (T-EXPR-11, T-EXPR-20), r-expr-1b op-plus-amp-precedence-and-types ----
        Plain(PlusAmp, "1 + 2 & 3 + 4", Cat(Add(I(1), I(2)), Add(I(3), I(4))));
        Plain(PlusAmp, "'1' & 2 + 3", Cat(S("1"), Add(I(2), I(3))));
        Plain(PlusAmp, "1 & 2 * 3", Cat(I(1), Mul(I(2), I(3))));
        Plain(PlusAmp, "'a' & 'b' & Null", Cat(Cat(S("a"), S("b")), N));
        Plain(PlusAmp, "#2024-03-05# + 1", Add(Dt(2024, 3, 5), I(1)));
        Plain(PlusAmp, "#2024-03-05# + '1'", Add(Dt(2024, 3, 5), S("1")));
        Plain(PlusAmp, "#2024-03-05# + #2024-03-05#", Add(Dt(2024, 3, 5), Dt(2024, 3, 5)));
        Plain(PlusAmp, "#2024-03-05# + 0.5", Add(Dt(2024, 3, 5), Dc("0.5")));
        Plain(PlusAmp, "'2024-03-05' + 1", Add(S("2024-03-05"), I(1)));
        Plain(PlusAmp, "'12:30' + 1", Add(S("12:30"), I(1)));
        Plain(PlusAmp, "#2024-03-05# & 1", Cat(Dt(2024, 3, 5), I(1)));
        Plain(PlusAmp, "True + True", Add(T, T));
        Plain(PlusAmp, "True & True", Cat(T, T));
        Plain(PlusAmp, "True + '1'", Add(T, S("1")));
        Plain(PlusAmp, "True & '1'", Cat(T, S("1")));
        Plain(PlusAmp, "'a' + Null", Add(S("a"), N));
        Plain(PlusAmp, "Null + 'a'", Add(N, S("a")));
        Plain(PlusAmp, "'1' + '2' + 3", Add(Add(S("1"), S("2")), I(3)));
        Plain(PlusAmp, "'1' & '2' + 3", Cat(S("1"), Add(S("2"), I(3))));
        Plain(PlusAmp, "1 - '1'", Sub(I(1), S("1")));
        Plain(PlusAmp, "'3' - '1'", Sub(S("3"), S("1")));
        Plain(PlusAmp, "'3' * '4'", Mul(S("3"), S("4")));
        Plain(PlusAmp, "'3' / '4'", Div(S("3"), S("4")));
        Plain(PlusAmp, "'3' ^ '2'", Pow(S("3"), S("2")));
        Plain(PlusAmp, "'3' \\ '2'", IDiv(S("3"), S("2")));
        Plain(PlusAmp, "'7' Mod '4'", Mod(S("7"), S("4")));
        Plain(PlusAmp, "-'3'", Neg(S("3")));
        Plain(PlusAmp, "'5' - Null", Sub(S("5"), N));
        Plain(PlusAmp, "Null - Null", Sub(N, N));
        Plain(PlusAmp, "#2024-03-05# - '1'", Sub(Dt(2024, 3, 5), S("1")));
        Plain(PlusAmp, "#2024-03-05# - 1.5", Sub(Dt(2024, 3, 5), Dc("1.5")));
        Plain(PlusAmp, "'a' - 1", Sub(S("a"), I(1)));
        Plain(PlusAmp, "'' + 1", Add(S(""), I(1)));
        Plain(PlusAmp, "'' & 1", Cat(S(""), I(1)));
        Plain(PlusAmp, "'' + ''", Add(S(""), S("")));
        Plain(PlusAmp, "Null & ''", Cat(N, S("")));
        Plain(PlusAmp, "Len(Null & '')", Fn(Len, Cat(N, S(""))));
        Plain(PlusAmp, "IsNull(Null & '')", Fn(IsNull, Cat(N, S(""))));
        Plain(PlusAmp, "IsNull(Null + '')", Fn(IsNull, Add(N, S(""))));
        Plain(PlusAmp, "IsNull('' + Null)", Fn(IsNull, Add(S(""), N)));
        Plain(PlusAmp, "IsNull(Null & Null)", Fn(IsNull, Cat(N, N)));
        Plain(PlusAmp, "Len(Null + 'a')", Fn(Len, Add(N, S("a"))));

        // ---- Text forms of numbers in & (T-EXPR-19), r-expr-1b op-concat-number-forms ----
        X empty = S("");
        Plain(ConcatForms, "1.5 & ''", Cat(Dc("1.5"), empty));
        Plain(ConcatForms, "1E10 & ''", Cat(Dc("1E10"), empty));
        Plain(ConcatForms, "1E15 & ''", Cat(Dc("1E15"), empty));
        Plain(ConcatForms, "1E16 & ''", Cat(Dc("1E16"), empty));
        Plain(ConcatForms, "(0.1 + 0.2) & ''", Cat(Add(Dc("0.1"), Dc("0.2")), empty));
        Plain(ConcatForms, "CSng(1/3) & ''", Cat(Fn(CSng, Div(I(1), I(3))), empty));
        Plain(ConcatForms, "1/3 & ''", Cat(Div(I(1), I(3)), empty));
        Plain(ConcatForms, "True & ''", Cat(T, empty));
        Plain(ConcatForms, "#2024-03-05 14:07:09# & ''", Cat(Dt(2024, 3, 5, 14, 7, 9), empty));
        Plain(ConcatForms, "#2024-03-05# & ''", Cat(Dt(2024, 3, 5), empty));
        Plain(ConcatForms, "#14:30# & ''", Cat(Dt(1899, 12, 30, 14, 30), empty));
        Plain(ConcatForms, "CCur(1.5) & ''", Cat(Fn(CCur, Dc("1.5")), empty));
        Plain(ConcatForms, "CCur(1) & ''", Cat(Fn(CCur, I(1)), empty));
        Plain(ConcatForms, "1E-5 & ''", Cat(Dc("1E-5"), empty));
        Plain(ConcatForms, "0.0001 & ''", Cat(Dc("0.0001"), empty));
        Plain(ConcatForms, "100000000000000000000 & ''", Cat(Dc("100000000000000000000"), empty));
        Plain(ConcatForms, "CByte(5) & ''", Cat(Fn(CByte, I(5)), empty));
        Plain(ConcatForms, "1.0 & ''", Cat(Dc("1.0"), empty));
        Plain(ConcatForms, "1.10 & ''", Cat(Dc("1.10"), empty));
        Plain(ConcatForms, "CDbl(1) & ''", Cat(Fn(CDbl, I(1)), empty));
        Plain(ConcatForms, "-1.5 & ''", Cat(Dc("-1.5"), empty));
        Plain(ConcatForms, "-1 & ''", Cat(I(-1), empty));
        Plain(ConcatForms, "2147483648 & ''", Cat(Dc("2147483648"), empty));
        Plain(ConcatForms, "123456789.123456789 & ''", Cat(Dc("123456789.123456789"), empty));
        Plain(ConcatForms, "CSng(123456789) & ''", Cat(Fn(CSng, I(123456789)), empty));
        Plain(ConcatForms, "CSng(0.1) & ''", Cat(Fn(CSng, Dc("0.1")), empty));
        Plain(ConcatForms, "CDbl(CSng(0.1)) & ''", Cat(Fn(CDbl, Fn(CSng, Dc("0.1"))), empty));
        Plain(ConcatForms, "9007199254740993 & ''", Cat(Dc("9007199254740993"), empty));
        Plain(ConcatForms, "1.5E300 & ''", Cat(R(1.5E300), empty));
        Plain(ConcatForms, "5E-324 & ''", Cat(R(5E-324), empty));
        Plain(ConcatForms, "5 & 5", Cat(I(5), I(5)));
        Plain(ConcatForms, "1 & 2 & 3", Cat(Cat(I(1), I(2)), I(3)));
        Plain(ConcatForms, "'a' & 1 & 'b'", Cat(Cat(S("a"), I(1)), S("b")));
        Plain(ConcatForms, "1 & -1", Cat(I(1), I(-1)));
        Plain(ConcatForms, "-1 & -1", Cat(I(-1), I(-1)));
        Plain(ConcatForms, "1 & .5", Cat(I(1), Dc("0.5")));

        // ---- Abs, Sgn, Int, Fix (T-EXPR-22), r-expr-1b num-abs-sgn-int-fix ----
        Typed(AbsSgn, "Abs(-2.5)", Fn(Abs, Dc("-2.5")));
        Typed(AbsSgn, "Abs(CByte(1))", Fn(Abs, Fn(CByte, I(1))));
        Typed(AbsSgn, "Abs(-1E308)", Fn(Abs, R(-1E308)));
        Typed(AbsSgn, "Abs('-3')", Fn(Abs, S("-3")));
        Typed(AbsSgn, "Abs('x')", Fn(Abs, S("x")));
        Typed(AbsSgn, "Abs(True)", Fn(Abs, T));
        Typed(AbsSgn, "Abs(#2024-03-05#)", Fn(Abs, Dt(2024, 3, 5)));
        Typed(AbsSgn, "Abs(CCur(-1.5))", Fn(Abs, Fn(CCur, Dc("-1.5"))));
        Typed(AbsSgn, "Abs(CSng(-1.5))", Fn(Abs, Fn(CSng, Dc("-1.5"))));
        Typed(AbsSgn, "Abs(CInt(-32768))", Fn(Abs, Fn(CInt, I(-32768))));
        Typed(AbsSgn, "Abs(CLng(-2147483648))", Fn(Abs, Fn(CLng, I(-2147483648))));
        Typed(AbsSgn, "Sgn(0)", Fn(Sgn, I(0)));
        Typed(AbsSgn, "Sgn(-3)", Fn(Sgn, I(-3)));
        Typed(AbsSgn, "Sgn(CCur(-1))", Fn(Sgn, Fn(CCur, I(-1))));
        Typed(AbsSgn, "Sgn('3')", Fn(Sgn, S("3")));
        Typed(AbsSgn, "Sgn(True)", Fn(Sgn, T));
        Typed(AbsSgn, "Sgn(1E-300)", Fn(Sgn, R(1E-300)));
        Typed(AbsSgn, "Sgn(#2024-03-05#)", Fn(Sgn, Dt(2024, 3, 5)));
        Typed(AbsSgn, "Int(2.7)", Fn(Int, Dc("2.7")));
        Typed(AbsSgn, "Int(-2.7)", Fn(Int, Dc("-2.7")));
        Typed(AbsSgn, "Int(CCur(-2.7))", Fn(Int, Fn(CCur, Dc("-2.7"))));
        Typed(AbsSgn, "Int(CSng(-2.7))", Fn(Int, Fn(CSng, Dc("-2.7"))));
        Typed(AbsSgn, "Int('2.7')", Fn(Int, S("2.7")));
        Typed(AbsSgn, "Int(True)", Fn(Int, T));
        Typed(AbsSgn, "Int(#2024-03-05 14:07:09#)", Fn(Int, Dt(2024, 3, 5, 14, 7, 9)));
        Typed(AbsSgn, "Int(1E20)", Fn(Int, Dc("1E20")));
        Typed(AbsSgn, "Int(-1E20)", Fn(Int, Dc("-1E20")));
        Typed(AbsSgn, "Int(2147483647.9)", Fn(Int, Dc("2147483647.9")));
        Typed(AbsSgn, "Int(CByte(5))", Fn(Int, Fn(CByte, I(5))));
        Typed(AbsSgn, "Fix(2.7)", Fn(Fix, Dc("2.7")));
        Typed(AbsSgn, "Fix(-2.7)", Fn(Fix, Dc("-2.7")));
        Typed(AbsSgn, "Fix(CCur(-2.7))", Fn(Fix, Fn(CCur, Dc("-2.7"))));
        Typed(AbsSgn, "Fix(CSng(-2.7))", Fn(Fix, Fn(CSng, Dc("-2.7"))));
        Typed(AbsSgn, "Fix('-2.7')", Fn(Fix, S("-2.7")));
        Typed(AbsSgn, "Fix(True)", Fn(Fix, T));
        Typed(AbsSgn, "Fix(#2024-03-05 14:07:09#)", Fn(Fix, Dt(2024, 3, 5, 14, 7, 9)));
        Typed(AbsSgn, "Fix(-1E20)", Fn(Fix, Dc("-1E20")));
        Typed(AbsSgn, "Fix(CByte(5))", Fn(Fix, Fn(CByte, I(5))));
        Typed(AbsSgn, "Fix(-0.9999999999999999)", Fn(Fix, Dc("-0.9999999999999999")));

        // ---- ^ (T-EXPR-19, T-EXPR-22), r-expr-1b num-power-operator ----
        Typed(Power, "2 ^ 3", Pow(I(2), I(3)));
        Typed(Power, "2 ^ -1", Pow(I(2), I(-1)));
        Typed(Power, "2 ^ 10", Pow(I(2), I(10)));
        Typed(Power, "2 ^ 31", Pow(I(2), I(31)));
        Typed(Power, "2 ^ 63", Pow(I(2), I(63)));
        Typed(Power, "2 ^ 64", Pow(I(2), I(64)));
        Typed(Power, "(-2) ^ 3", Pow(I(-2), I(3)));
        Typed(Power, "(-2) ^ 2", Pow(I(-2), I(2)));
        Typed(Power, "-2 ^ 2", Pow(I(-2), I(2)));
        Typed(Power, "2 ^ 3 ^ 2", Pow(Pow(I(2), I(3)), I(2)));
        Typed(Power, "(2 ^ 3) ^ 2", Pow(Pow(I(2), I(3)), I(2)));
        Typed(Power, "0 ^ -1", Pow(I(0), I(-1)));
        Typed(Power, "0 ^ 1", Pow(I(0), I(1)));
        Typed(Power, "10 ^ 308", Pow(I(10), I(308)));
        Typed(Power, "10 ^ 309", Pow(I(10), I(309)));
        Typed(Power, "10 ^ -324", Pow(I(10), I(-324)));
        Typed(Power, "CInt(2) ^ CInt(15)", Pow(Fn(CInt, I(2)), Fn(CInt, I(15))));
        Typed(Power, "CByte(2) ^ CByte(7)", Pow(Fn(CByte, I(2)), Fn(CByte, I(7))));
        Typed(Power, "2 ^ '3'", Pow(I(2), S("3")));
        Typed(Power, "'2' ^ 3", Pow(S("2"), I(3)));
        Typed(Power, "True ^ 2", Pow(T, I(2)));
        Typed(Power, "Null ^ 1", Pow(N, I(1)));
        Typed(Power, "1 ^ Null", Pow(I(1), N));
        Typed(Power, "2 ^ 0.0", Pow(I(2), Dc("0.0")));
        Typed(Power, "(-2) ^ 0.5", Pow(I(-2), Dc("0.5")));
        Typed(Power, "4 ^ 0.5", Pow(I(4), Dc("0.5")));
        Typed(Power, "1.1 ^ 2", Pow(Dc("1.1"), I(2)));
        Typed(Power, "CCur(2) ^ 2", Pow(Fn(CCur, I(2)), I(2)));
        Typed(Power, "2 ^ CCur(0.5)", Pow(I(2), Fn(CCur, Dc("0.5"))));
        Typed(Power, "(-1) ^ 1.5", Pow(I(-1), Dc("1.5")));
        Typed(Power, "0 ^ 0.5", Pow(I(0), Dc("0.5")));
        Typed(Power, "1E200 ^ 2", Pow(R(1E200), I(2)));
        Typed(Power, "2 ^ 1023", Pow(I(2), I(1023)));
        Typed(Power, "2 ^ 1024", Pow(I(2), I(1024)));
        Typed(Power, "2 ^ -1074", Pow(I(2), I(-1074)));
        Typed(Power, "2 ^ -1075", Pow(I(2), I(-1075)));
        Typed(Power, "(-8) ^ 3", Pow(I(-8), I(3)));
        Typed(Power, "'x' ^ 2", Pow(S("x"), I(2)));
        Typed(Power, "2 ^ 'x'", Pow(I(2), S("x")));

        // ---- Comparison across types (T-EXPR-12, T-EXPR-19, T-EXPR-20), r-expr-1b cmp-cross-types ----
        Plain(CrossTypes, "True = 1", Eq(T, I(1)));
        Plain(CrossTypes, "True = -1", Eq(T, I(-1)));
        Plain(CrossTypes, "True = '1'", Eq(T, S("1")));
        Plain(CrossTypes, "True = 'x'", Eq(T, S("x")));
        Plain(CrossTypes, "False = 0", Eq(F, I(0)));
        Plain(CrossTypes, "True < 1", Lt(T, I(1)));
        Plain(CrossTypes, "True < 'a'", Lt(T, S("a")));
        Plain(CrossTypes, "1 = True", Eq(I(1), T));
        Plain(CrossTypes, "1 = 1.0", Eq(I(1), Dc("1.0")));
        Plain(CrossTypes, "1 = CCur(1)", Eq(I(1), Fn(CCur, I(1))));
        Plain(CrossTypes, "0.1 = CSng(0.1)", Eq(Dc("0.1"), Fn(CSng, Dc("0.1"))));
        Plain(CrossTypes, "CCur(0.1) = 0.1", Eq(Fn(CCur, Dc("0.1")), Dc("0.1")));
        Plain(CrossTypes, "#2024-03-05# = 45356", Eq(Dt(2024, 3, 5), I(45356)));
        Plain(CrossTypes, "#2024-03-05# = '2024-03-05'", Eq(Dt(2024, 3, 5), S("2024-03-05")));
        Plain(CrossTypes, "#2024-03-05# > '2024-03-04'", Gt(Dt(2024, 3, 5), S("2024-03-04")));
        Plain(CrossTypes, "#2024-03-05# < 'abc'", Lt(Dt(2024, 3, 5), S("abc")));
        Plain(CrossTypes, "'12:00' = 0.5", Eq(S("12:00"), Dc("0.5")));
        Plain(CrossTypes, "CDate('12:00') = 0.5", Eq(Fn(CDate, S("12:00")), Dc("0.5")));
        Plain(CrossTypes, "Null = ''", Eq(N, S("")));
        Plain(CrossTypes, "'' = ''", Eq(S(""), S("")));
        Plain(CrossTypes, "'' < 'a'", Lt(S(""), S("a")));
        Plain(CrossTypes, "'a' < Null", Lt(S("a"), N));
        Plain(CrossTypes, "2 < '10'", Lt(I(2), S("10")));
        Plain(CrossTypes, "2 < '10a'", Lt(I(2), S("10a")));
        Plain(CrossTypes, "'abc' = 'ABC'", Eq(S("abc"), S("ABC")));
        Plain(CrossTypes, "'abc' <> 'abd'", Ne(S("abc"), S("abd")));
        Plain(CrossTypes, "1E308 = 1E308", Eq(R(1E308), R(1E308)));
        Plain(CrossTypes, "1 = '1.0'", Eq(I(1), S("1.0")));
        Plain(CrossTypes, "1 = '1 '", Eq(I(1), S("1 ")));
        Plain(CrossTypes, "' 1' = 1", Eq(S(" 1"), I(1)));
        Plain(CrossTypes, "1 = '+1'", Eq(I(1), S("+1")));
        Plain(CrossTypes, "CByte(1) = CInt(1)", Eq(Fn(CByte, I(1)), Fn(CInt, I(1))));
        Plain(CrossTypes, "CInt(1) = CLng(1)", Eq(Fn(CInt, I(1)), Fn(CLng, I(1))));
        Plain(CrossTypes, "CSng(1.5) = CDbl(1.5)", Eq(Fn(CSng, Dc("1.5")), Fn(CDbl, Dc("1.5"))));
        Plain(CrossTypes, "CCur(1.5) = CSng(1.5)", Eq(Fn(CCur, Dc("1.5")), Fn(CSng, Dc("1.5"))));
        Plain(CrossTypes, "CSng(0.1) < 0.1", Lt(Fn(CSng, Dc("0.1")), Dc("0.1")));
        Plain(CrossTypes, "CSng(0.1) > 0.1", Gt(Fn(CSng, Dc("0.1")), Dc("0.1")));
        Plain(CrossTypes, "#12/30/1899# = 0", Eq(Dt(1899, 12, 30), I(0)));
        Plain(CrossTypes, "#12/30/1899# = ''", Eq(Dt(1899, 12, 30), S("")));

        // ---- SQL text comparison is the database sort order's (T-EXPR-12), r-expr-1b str-strcomp-and-text-operators ----
        foreach ((string e, X tree) in new[]
        {
            ("'a' < 'B'", Lt(S("a"), S("B"))), ("'a' = 'A'", Eq(S("a"), S("A"))), ("'ä' = 'a'", Eq(S("ä"), S("a"))),
            ("'a' = 'a '", Eq(S("a"), S("a "))), ("'Z' < 'a'", Lt(S("Z"), S("a"))), ("'10' < '9'", Lt(S("10"), S("9"))),
            ("'æ' = 'ae'", Eq(S("æ"), S("ae"))), ("'ß' = 'ss'", Eq(S("ß"), S("ss"))), ("'ab' < 'a-b'", Lt(S("ab"), S("a-b"))),
        })
        {
            Plain(TextOperators, e, tree);
        }

        // ---- Null into functions (T-EXPR-09, T-EXPR-11), r-expr-1b null-propagation-functions ----
        Plain(NullFunctions, "Abs(Null)", Fn(Abs, N));
        Plain(NullFunctions, "Int(Null)", Fn(Int, N));
        Plain(NullFunctions, "Fix(Null)", Fn(Fix, N));
        Plain(NullFunctions, "Round(Null)", Fn(Round, N));
        Plain(NullFunctions, "Round(1.5, Null)", Fn(Round, Dc("1.5"), N));
        foreach ((string name, ScalarOperation op) in new[] { ("CInt", CInt), ("CLng", CLng), ("CDbl", CDbl), ("CSng", CSng), ("CCur", CCur), ("CDate", CDate), ("CBool", CBool), ("CByte", CByte) })
        {
            Plain(NullFunctions, $"{name}(Null)", Fn(op, N));
        }

        // ---- CBool and CVar (T-EXPR-16, T-EXPR-21), r-expr-1b conv-cbool-cvar ----
        Plain(CBoolCVar, "CBool(0)", Fn(CBool, I(0)));
        Plain(CBoolCVar, "CBool(0.1)", Fn(CBool, Dc("0.1")));
        Plain(CBoolCVar, "CBool(-1)", Fn(CBool, I(-1)));
        Plain(CBoolCVar, "CBool(CByte(0))", Fn(CBool, Fn(CByte, I(0))));
        foreach (string text in new[] { "0", "-1", "1", "false", "FALSE", "TRUE", "x", "", " True " })
        {
            Plain(CBoolCVar, $"CBool('{text}')", Fn(CBool, S(text)));
        }
        Plain(CBoolCVar, "CBool(#12/30/1899#)", Fn(CBool, Dt(1899, 12, 30)));
        Plain(CBoolCVar, "CBool(#12/30/1899 06:00#)", Fn(CBool, Dt(1899, 12, 30, 6)));
        Plain(CBoolCVar, "CBool(CCur(0))", Fn(CBool, Fn(CCur, I(0))));
        Plain(CBoolCVar, "CBool(1E-320)", Fn(CBool, R(1E-320)));
        Typed(CBoolCVar, "CVar(1)", Fn(CVar, I(1)));
        Typed(CBoolCVar, "CVar(1.5)", Fn(CVar, Dc("1.5")));
        Typed(CBoolCVar, "CVar('a')", Fn(CVar, S("a")));
        Typed(CBoolCVar, "CVar(Null)", Fn(CVar, N));
        Typed(CBoolCVar, "CVar(#2024-03-05#)", Fn(CVar, Dt(2024, 3, 5)));
        Typed(CBoolCVar, "CVar(CCur(1))", Fn(CVar, Fn(CCur, I(1))));
        Typed(CBoolCVar, "CVar(True)", Fn(CVar, T));
        Typed(CBoolCVar, "CVar(CByte(1))", Fn(CVar, Fn(CByte, I(1))));
        Typed(CBoolCVar, "CVar(CSng(1))", Fn(CVar, Fn(CSng, I(1))));

        // ---- CStr (T-EXPR-19), r-expr-1b conv-cstr-forms ----
        Plain(CStrForms, "CStr(1E10)", Fn(CStr, Dc("1E10")));
        Plain(CStrForms, "CStr(1E14)", Fn(CStr, Dc("1E14")));
        Plain(CStrForms, "CStr(1E15)", Fn(CStr, Dc("1E15")));
        Plain(CStrForms, "CStr(1E16)", Fn(CStr, Dc("1E16")));
        Plain(CStrForms, "CStr(0.0001)", Fn(CStr, Dc("0.0001")));
        Plain(CStrForms, "CStr(0.00001)", Fn(CStr, Dc("0.00001")));
        Plain(CStrForms, "CStr(1/3)", Fn(CStr, Div(I(1), I(3))));
        Plain(CStrForms, "CStr(100000000000000000000)", Fn(CStr, Dc("100000000000000000000")));
        Plain(CStrForms, "CStr(CSng(1/3))", Fn(CStr, Fn(CSng, Div(I(1), I(3)))));
        Plain(CStrForms, "CStr(CSng(1E10))", Fn(CStr, Fn(CSng, Dc("1E10"))));
        Plain(CStrForms, "CStr(CCur(1.5))", Fn(CStr, Fn(CCur, Dc("1.5"))));
        Plain(CStrForms, "CStr(CCur(1))", Fn(CStr, Fn(CCur, I(1))));
        Plain(CStrForms, "CStr(#2024-03-05 14:07:09#)", Fn(CStr, Dt(2024, 3, 5, 14, 7, 9)));
        Plain(CStrForms, "CStr(#2024-03-05#)", Fn(CStr, Dt(2024, 3, 5)));
        Plain(CStrForms, "CStr(#14:30#)", Fn(CStr, Dt(1899, 12, 30, 14, 30)));
        Plain(CStrForms, "CStr(CDate(0.5))", Fn(CStr, Fn(CDate, Dc("0.5"))));
        Plain(CStrForms, "CStr(CDate(-0.25))", Fn(CStr, Fn(CDate, Dc("-0.25"))));
        Plain(CStrForms, "CStr(True)", Fn(CStr, T));
        Plain(CStrForms, "CStr(False)", Fn(CStr, F));
        Plain(CStrForms, "CStr(2147483648)", Fn(CStr, Dc("2147483648")));
        Plain(CStrForms, "CStr(CByte(5))", Fn(CStr, Fn(CByte, I(5))));
        Plain(CStrForms, "CStr(1.10)", Fn(CStr, Dc("1.10")));
        Plain(CStrForms, "CStr(5E-324)", Fn(CStr, R(5E-324)));

        // ---- Conversion range edges (T-EXPR-16, T-EXPR-17, T-EXPR-19), r-expr-1b conv-range-edges ----
        Typed(RangeEdges, "CLng(2147483647)", Fn(CLng, I(2147483647)));
        Typed(RangeEdges, "CLng(2147483648)", Fn(CLng, Dc("2147483648")));
        Typed(RangeEdges, "CLng(-2147483648)", Fn(CLng, I(-2147483648)));
        Typed(RangeEdges, "CLng(-2147483649)", Fn(CLng, Dc("-2147483649")));
        Typed(RangeEdges, "CByte(255)", Fn(CByte, I(255)));
        Typed(RangeEdges, "CByte(256)", Fn(CByte, I(256)));
        Typed(RangeEdges, "CByte(-1)", Fn(CByte, I(-1)));
        Typed(RangeEdges, "CInt(32767)", Fn(CInt, I(32767)));
        Typed(RangeEdges, "CInt(32768)", Fn(CInt, I(32768)));
        Typed(RangeEdges, "CInt(-32768)", Fn(CInt, I(-32768)));
        Typed(RangeEdges, "CInt(-32769)", Fn(CInt, I(-32769)));
        Typed(RangeEdges, "CInt(1E10)", Fn(CInt, Dc("1E10")));
        Typed(RangeEdges, "CInt(True)", Fn(CInt, T));
        Typed(RangeEdges, "CLng(#2024-03-05#)", Fn(CLng, Dt(2024, 3, 5)));
        Typed(RangeEdges, "CInt(#2024-03-05#)", Fn(CInt, Dt(2024, 3, 5)));
        Typed(RangeEdges, "CCur(922337203685477.5807)", Fn(CCur, Dc("922337203685477.5807")));
        Typed(RangeEdges, "CCur(922337203685477.5808)", Fn(CCur, Dc("922337203685477.5808")));
        Typed(RangeEdges, "CCur(-922337203685477.5808)", Fn(CCur, Dc("-922337203685477.5808")));
        Typed(RangeEdges, "CCur(1E15)", Fn(CCur, Dc("1E15")));
        Typed(RangeEdges, "CSng(3.4028235E38)", Fn(CSng, R(3.4028235E38)));
        Typed(RangeEdges, "CSng(3.5E38)", Fn(CSng, R(3.5E38)));
        Typed(RangeEdges, "CSng(1E-45)", Fn(CSng, R(1E-45)));
        Typed(RangeEdges, "CSng(1E-46)", Fn(CSng, R(1E-46)));
        Typed(RangeEdges, "CSng(0.1)", Fn(CSng, Dc("0.1")));
        Typed(RangeEdges, "CSng(16777217)", Fn(CSng, I(16777217)));
        Typed(RangeEdges, "CDbl(CSng(0.1))", Fn(CDbl, Fn(CSng, Dc("0.1"))));

        // ---- Text to integer and floating (T-EXPR-16, EXPR-INVENTORY), r-expr-1b conv-text-to-integer, -floating ----
        foreach ((string name, ScalarOperation op) in new[] { ("CInt", CInt), ("CLng", CLng), ("CByte", CByte) })
        {
            foreach (string text in new[] { "12", " 12 ", "1,234", "1e2", "&H10", "&O17", "&HFFFF", "12abc", "", "$12", "(12)", "1 2", "+5", "1.5E+2", "１２" })
            {
                Plain(TextToInteger, $"{name}('{text}')", Fn(op, S(text)));
            }
        }
        foreach ((string name, ScalarOperation op) in new[] { ("CDbl", CDbl), ("CCur", CCur) })
        {
            foreach (string text in new[] { "1.5", "1,5", ".5", "5.", "1e-3", "1E+400", "-1.5E-400", "1.7976931348623157E308", "1.7976931348623159E308", "9007199254740993", "922337203685477.58075", "1.5 ", "1.5x", "NaN", "Infinity", "&H10" })
            {
                Plain(TextToFloating, $"{name}('{text}')", Fn(op, S(text)));
            }
        }

        // ---- Round (T-EXPR-22), r-expr-1b conv-round-function-digits ----
        Typed(RoundDigits, "Round(0.125, 2)", Fn(Round, Dc("0.125"), I(2)));
        Typed(RoundDigits, "Round(0.135, 2)", Fn(Round, Dc("0.135"), I(2)));
        Typed(RoundDigits, "Round(0.145, 2)", Fn(Round, Dc("0.145"), I(2)));
        Typed(RoundDigits, "Round(1.005, 2)", Fn(Round, Dc("1.005"), I(2)));
        Typed(RoundDigits, "Round(-1.005, 2)", Fn(Round, Dc("-1.005"), I(2)));
        Typed(RoundDigits, "Round(1234.5678, -2)", Fn(Round, Dc("1234.5678"), I(-2)));
        Typed(RoundDigits, "Round(1234.5678, 2)", Fn(Round, Dc("1234.5678"), I(2)));
        Typed(RoundDigits, "Round(1234.5678, 0)", Fn(Round, Dc("1234.5678"), I(0)));
        Typed(RoundDigits, "Round(5.5)", Fn(Round, Dc("5.5")));
        Typed(RoundDigits, "Round(6.5)", Fn(Round, Dc("6.5")));
        Typed(RoundDigits, "Round(-5.5)", Fn(Round, Dc("-5.5")));
        Typed(RoundDigits, "Round(2.5, 1)", Fn(Round, Dc("2.5"), I(1)));
        Typed(RoundDigits, "Round(1.45, 1)", Fn(Round, Dc("1.45"), I(1)));
        Typed(RoundDigits, "Round(1.55, 1)", Fn(Round, Dc("1.55"), I(1)));
        Typed(RoundDigits, "Round(1.5, 15)", Fn(Round, Dc("1.5"), I(15)));
        Typed(RoundDigits, "Round(1.5, 16)", Fn(Round, Dc("1.5"), I(16)));
        Typed(RoundDigits, "Round(1.5, -16)", Fn(Round, Dc("1.5"), I(-16)));
        Typed(RoundDigits, "Round('2.5')", Fn(Round, S("2.5")));
        Typed(RoundDigits, "Round('x')", Fn(Round, S("x")));
        Typed(RoundDigits, "Round(True)", Fn(Round, T));
        Typed(RoundDigits, "Round(CCur(2.5))", Fn(Round, Fn(CCur, Dc("2.5"))));
        Typed(RoundDigits, "Round(CCur(1.005), 2)", Fn(Round, Fn(CCur, Dc("1.005")), I(2)));
        Typed(RoundDigits, "Round(CSng(1.005), 2)", Fn(Round, Fn(CSng, Dc("1.005")), I(2)));
        Typed(RoundDigits, "Round(2.5, 0.5)", Fn(Round, Dc("2.5"), Dc("0.5")));
        Typed(RoundDigits, "Round(2.5, 1.5)", Fn(Round, Dc("2.5"), Dc("1.5")));
        Typed(RoundDigits, "Round(1234, -2)", Fn(Round, I(1234), I(-2)));
        Typed(RoundDigits, "Round(1.5, 2147483648)", Fn(Round, Dc("1.5"), Dc("2147483648")));

        // ---- Halves at large values (T-EXPR-16), r-expr-1b conv-half-rounding-large ----
        foreach ((string e, X tree) in new[]
        {
            ("CLng(0.5)", Fn(CLng, Dc("0.5"))), ("CLng(1.5)", Fn(CLng, Dc("1.5"))), ("CLng(2.5)", Fn(CLng, Dc("2.5"))),
            ("CLng(1000000.5)", Fn(CLng, Dc("1000000.5"))), ("CLng(1000001.5)", Fn(CLng, Dc("1000001.5"))),
            ("CLng(2147483646.5)", Fn(CLng, Dc("2147483646.5"))), ("CLng(2147483645.5)", Fn(CLng, Dc("2147483645.5"))),
            ("CInt(100.5)", Fn(CInt, Dc("100.5"))), ("CInt(101.5)", Fn(CInt, Dc("101.5"))), ("CInt(-100.5)", Fn(CInt, Dc("-100.5"))),
            ("CInt(-101.5)", Fn(CInt, Dc("-101.5"))), ("CInt(32766.5)", Fn(CInt, Dc("32766.5"))), ("CInt(32765.5)", Fn(CInt, Dc("32765.5"))),
            ("CInt(CSng(2.5))", Fn(CInt, Fn(CSng, Dc("2.5")))), ("CInt(CSng(3.5))", Fn(CInt, Fn(CSng, Dc("3.5")))),
            ("CInt(CCur(2.5))", Fn(CInt, Fn(CCur, Dc("2.5")))), ("CInt(CCur(3.5))", Fn(CInt, Fn(CCur, Dc("3.5")))),
            ("CLng(CCur(0.5))", Fn(CLng, Fn(CCur, Dc("0.5")))), ("CLng(CCur(1.5))", Fn(CLng, Fn(CCur, Dc("1.5")))),
            ("CInt(0.5000000000000001)", Fn(CInt, Dc("0.5000000000000001"))), ("CInt(1.4999999999999998)", Fn(CInt, Dc("1.4999999999999998"))),
            ("CCur(1.00005)", Fn(CCur, Dc("1.00005"))), ("CCur(1.00015)", Fn(CCur, Dc("1.00015"))), ("CCur(1.00025)", Fn(CCur, Dc("1.00025"))),
            ("CCur(-1.00015)", Fn(CCur, Dc("-1.00015"))), ("CByte(CCur(2.5))", Fn(CByte, Fn(CCur, Dc("2.5")))),
            ("CByte(CSng(2.5))", Fn(CByte, Fn(CSng, Dc("2.5")))), ("CByte(0.4)", Fn(CByte, Dc("0.4"))), ("CByte(2.5)", Fn(CByte, Dc("2.5"))),
            ("CByte(3.5)", Fn(CByte, Dc("3.5"))), ("CByte(254.5)", Fn(CByte, Dc("254.5"))), ("CByte(255.4)", Fn(CByte, Dc("255.4"))),
        })
        {
            Plain(HalfRounding, e, tree);
        }

        // ---- Len (T-EXPR-22), r-expr-1b str-len-lenb-forms ----
        foreach ((string e, X tree) in new[]
        {
            ("Len('')", Fn(Len, S(""))), ("Len('abc')", Fn(Len, S("abc"))), ("Len('é')", Fn(Len, S("é"))), ("Len('€')", Fn(Len, S("€"))),
            ("Len(3)", Fn(Len, I(3))), ("Len(1.5)", Fn(Len, Dc("1.5"))), ("Len(True)", Fn(Len, T)),
            ("Len(#2024-03-05#)", Fn(Len, Dt(2024, 3, 5))), ("Len(#2024-03-05 14:07:09#)", Fn(Len, Dt(2024, 3, 5, 14, 7, 9))),
            ("Len(CCur(1.5))", Fn(Len, Fn(CCur, Dc("1.5")))), ("Len(CByte(1))", Fn(Len, Fn(CByte, I(1)))),
            ("Len(12345678901)", Fn(Len, Dc("12345678901"))), ("Len(1.0)", Fn(Len, Dc("1.0"))), ("Len(0.1 + 0.2)", Fn(Len, Add(Dc("0.1"), Dc("0.2")))),
            ("Len(1E10)", Fn(Len, Dc("1E10"))), ("Len(CSng(1/3))", Fn(Len, Fn(CSng, Div(I(1), I(3))))),
        })
        {
            Plain(LenForms, e, tree);
        }

        // ---- CDate (T-EXPR-20), r-expr-1b conv-cdate-forms ----
        foreach (string text in new[] { "12:30", "2024-03-05 14:30", "March 5, 2024", "5 Mar 2024", "20240305", "3/5/24", "3/5/49", "3/5/50", "1/1/29", "1/1/30", "1/1/100", "1/1/0100", "12:30 PM", "12:30 AM", "25:00", "24:00", "45000", "45000.75", "0", "", "2024-03-05T14:30:00", " 2024-03-05 ", "2024/03/05", "05.03.2024" })
        {
            Typed(CDateForms, $"CDate('{text}')", Fn(CDate, S(text)));
        }
        Typed(CDateForms, "CDate(45000.75)", Fn(CDate, Dc("45000.75")));
        Typed(CDateForms, "CDate(Null)", Fn(CDate, N));
        Typed(CDateForms, "CDate(True)", Fn(CDate, T));

        // ---- Inner result types (T-EXPR-13, T-EXPR-22), r-expr-1b info-result-vartype-numeric ----
        foreach ((string e, X tree) in new[]
        {
            ("1 + 1", Add(I(1), I(1))), ("1.5 + 1", Add(Dc("1.5"), I(1))), ("4 / 2", Div(I(4), I(2))), ("5 / 2", Div(I(5), I(2))),
            ("2 ^ 2", Pow(I(2), I(2))), ("5 \\ 2", IDiv(I(5), I(2))), ("5 Mod 2", Mod(I(5), I(2))), ("-CInt(1)", Neg(Fn(CInt, I(1)))),
            ("Abs(CInt(-1))", Fn(Abs, Fn(CInt, I(-1)))), ("Abs(CByte(1))", Fn(Abs, Fn(CByte, I(1)))), ("Abs(CCur(-1))", Fn(Abs, Fn(CCur, I(-1)))),
            ("Abs(CSng(-1))", Fn(Abs, Fn(CSng, I(-1)))), ("Abs(-1)", Fn(Abs, I(-1))), ("Abs(-1.5)", Fn(Abs, Dc("-1.5"))), ("Abs('-1')", Fn(Abs, S("-1"))),
            ("Abs(True)", Fn(Abs, T)), ("Int(CInt(1))", Fn(Int, Fn(CInt, I(1)))), ("Int(1.5)", Fn(Int, Dc("1.5"))), ("Int(CCur(1.5))", Fn(Int, Fn(CCur, Dc("1.5")))),
            ("Int(CSng(1.5))", Fn(Int, Fn(CSng, Dc("1.5")))), ("Int('1.5')", Fn(Int, S("1.5"))), ("Fix(CInt(1))", Fn(Fix, Fn(CInt, I(1)))),
            ("Fix(1.5)", Fn(Fix, Dc("1.5"))), ("Fix(CCur(1.5))", Fn(Fix, Fn(CCur, Dc("1.5")))), ("Fix(CSng(1.5))", Fn(Fix, Fn(CSng, Dc("1.5")))),
            ("Sgn(1.5)", Fn(Sgn, Dc("1.5"))), ("Sgn(CCur(1))", Fn(Sgn, Fn(CCur, I(1)))), ("Sgn(CInt(1))", Fn(Sgn, Fn(CInt, I(1)))),
            ("Round(1.5)", Fn(Round, Dc("1.5"))), ("Round(CCur(1.5))", Fn(Round, Fn(CCur, Dc("1.5")))), ("Round(CInt(2))", Fn(Round, Fn(CInt, I(2)))),
            ("Round(CSng(1.5), 1)", Fn(Round, Fn(CSng, Dc("1.5")), I(1))), ("Len('a')", Fn(Len, S("a"))),
            ("CSng(1) * CSng(2)", Mul(Fn(CSng, I(1)), Fn(CSng, I(2)))), ("CSng(1) + 1", Add(Fn(CSng, I(1)), I(1))),
            ("CCur(1) + 1", Add(Fn(CCur, I(1)), I(1))), ("CCur(1) + 1.5", Add(Fn(CCur, I(1)), Dc("1.5"))), ("CByte(1) + 1", Add(Fn(CByte, I(1)), I(1))),
            ("CInt(1) + 1", Add(Fn(CInt, I(1)), I(1))), ("CInt(1) + 40000", Add(Fn(CInt, I(1)), I(40000))), ("True + 1", Add(T, I(1))),
        })
        {
            InnerType(VarTypes, e, tree);
        }

        // ---- Field types of conversions and literals (T-EXPR-13, T-EXPR-16), r-expr-1b meta-conversion-field-types ----
        foreach ((string e, X tree) in new[]
        {
            ("CInt(1)", Fn(CInt, I(1))), ("CLng(1)", Fn(CLng, I(1))), ("CByte(1)", Fn(CByte, I(1))), ("CCur(1)", Fn(CCur, I(1))),
            ("CSng(1)", Fn(CSng, I(1))), ("CDbl(1)", Fn(CDbl, I(1))), ("CBool(1)", Fn(CBool, I(1))), ("CDate(1)", Fn(CDate, I(1))),
            ("CStr(1)", Fn(CStr, I(1))), ("CVar(1)", Fn(CVar, I(1))), ("Int(1.5)", Fn(Int, Dc("1.5"))), ("Fix(1.5)", Fn(Fix, Dc("1.5"))),
            ("Abs(-1)", Fn(Abs, I(-1))), ("Sgn(1)", Fn(Sgn, I(1))), ("Len('a')", Fn(Len, S("a"))), ("Round(1.5)", Fn(Round, Dc("1.5"))),
            ("IsNull(1)", Fn(IsNull, I(1))), ("1/2", Div(I(1), I(2))), ("1 + 1", Add(I(1), I(1))), ("CInt(1) + CInt(1)", Add(Fn(CInt, I(1)), Fn(CInt, I(1)))),
            ("CByte(1) + CByte(1)", Add(Fn(CByte, I(1)), Fn(CByte, I(1)))), ("CCur(1) + 1", Add(Fn(CCur, I(1)), I(1))), ("1 \\ 1", IDiv(I(1), I(1))),
            ("1 Mod 1", Mod(I(1), I(1))), ("2 ^ 2", Pow(I(2), I(2))), ("True + 1", Add(T, I(1))),
        })
        {
            Meta(e, tree);
        }

        // ---- Nz in an Access host (T-EXPR-11), r-access-host eval-context ----
        all.Add(new ExpressionCase(AccessHost, "Nz(Null, 5)", Fn(Nz, N, I(5)), null, null, NotModelledCase.None, Eval: true));

        return all.GroupBy(c => c.Record).ToDictionary(g => g.Key, g => (IReadOnlyList<ExpressionCase>)g.ToList(), StringComparer.Ordinal);
    }
}