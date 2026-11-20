namespace CalcDb.Values.Scalar;

/// <summary>
/// A compiled Like pattern of the DAO (ANSI-89) grammar (II-10 §§16–18; qualified.md R-EXPR-1 PAT-01, PAT-02, PAT-06,
/// PAT-07, PAT-10/13, PAT-12/14): <c>*</c> any run, <c>?</c> one UTF-16 unit, <c>#</c> one digit, <c>[…]</c> a class with
/// <c>!</c> negation and ranges, every other unit itself (<c>%</c> and <c>_</c> too). Compilation never fails: a structural
/// fault (an unclosed <c>[</c>) is kept as an instruction, so the error 93 is raised only when matching reaches it
/// (PAT-02: <c>q[</c> fails for "qx", not for "x" or "q"), and a reversed range is checked when its class is tried.
/// Immutable: a constant pattern is compiled once per bound expression and matched per row without allocation.
/// </summary>
public sealed class LikePattern
{
    private readonly Instruction[] _program;
    private readonly ClassMember[] _members;

    private LikePattern(Instruction[] program, ClassMember[] members, bool isStarOnly)
    {
        _program = program;
        _members = members;
        IsStarOnly = isStarOnly;
    }

    /// <summary>
    /// The pattern is exactly <c>*</c>: a Null subject is False against it, not Null (PAT-06; <c>**</c> and <c>?*</c> over Null are Null).
    /// </summary>
    internal bool IsStarOnly { get; }

    private enum Kind : byte
    {
        Literal,
        AnyOne,
        Digit,
        Star,
        Class,

        /// <summary><c>[]</c>: matches no unit, only the empty string at its place (PAT-02).</summary>
        Empty,

        /// <summary>An unclosed class: error 93 when matching reaches it with subject left.</summary>
        Fault,
    }

    private readonly record struct Instruction(Kind Kind, char Unit = '\0', int First = 0, int Count = 0, bool Negated = false, bool Caret = false, bool FaultFollows = false);

    private readonly record struct ClassMember(char Low, char High);

    /// <summary>Compile <paramref name="pattern"/>; never fails.</summary>
    public static LikePattern Compile(ReadOnlySpan<char> pattern)
    {
        var program = new List<Instruction>(pattern.Length);
        var members = new List<ClassMember>();
        int i = 0;
        while (i < pattern.Length)
        {
            char ch = pattern[i];
            switch (ch)
            {
                case '*':
                    if (program.Count == 0 || program[^1].Kind != Kind.Star)
                    {
                        program.Add(new Instruction(Kind.Star));
                    }
                    i++;
                    break;
                case '?':
                    program.Add(new Instruction(Kind.AnyOne));
                    i++;
                    break;
                case '#':
                    program.Add(new Instruction(Kind.Digit));
                    i++;
                    break;
                case '[':
                    i = CompileClass(pattern, i, program, members);
                    break;
                default:
                    program.Add(new Instruction(Kind.Literal, ch));
                    i++;
                    break;
            }
        }
        // A star whose remainder holds a fault is marked: reaching it is the not-recorded case.
        bool fault = false;
        for (int k = program.Count - 1; k >= 0; k--)
        {
            if (program[k].Kind == Kind.Star && fault)
            {
                program[k] = program[k] with { FaultFollows = true };
            }
            fault |= program[k].Kind == Kind.Fault;
        }
        return new LikePattern([.. program], [.. members], pattern is "*");
    }

    /// <summary>
    /// A class from the <c>[</c> at <paramref name="start"/>: <c>[]</c> is Empty; <c>!</c> first negates unless the class
    /// is just <c>[!]</c> (a literal "!", PAT-02); <c>[</c> inside is a member (<c>[[]</c> matches "["); <c>-</c> between two
    /// units is a range and at either end a member. Without a closing <c>]</c> the class is a fault and compilation ends.
    /// </summary>
    private static int CompileClass(ReadOnlySpan<char> pattern, int start, List<Instruction> program, List<ClassMember> members)
    {
        int close = pattern[(start + 1)..].IndexOf(']');
        if (close < 0)
        {
            program.Add(new Instruction(Kind.Fault));
            return pattern.Length;
        }
        ReadOnlySpan<char> body = pattern.Slice(start + 1, close);
        int end = start + 1 + close + 1;
        if (body.IsEmpty)
        {
            program.Add(new Instruction(Kind.Empty));
            return end;
        }
        bool negated = body[0] == '!' && body.Length > 1;
        bool caret = body[0] == '^';
        if (negated)
        {
            body = body[1..];
        }
        int first = members.Count;
        for (int j = 0; j < body.Length; j++)
        {
            if (j + 2 < body.Length && body[j + 1] == '-')
            {
                members.Add(new ClassMember(body[j], body[j + 2]));
                j += 2;
            }
            else
            {
                members.Add(new ClassMember(body[j], body[j]));
            }
        }
        program.Add(new Instruction(Kind.Class, First: first, Count: members.Count - first, Negated: negated, Caret: caret));
        return end;
    }

    /// <summary>
    /// Match the whole <paramref name="subject"/> (PAT-12/14: <c>LIKE 'MN'</c> matches only the whole subject): True or
    /// False, or a failure (93 when a fault is reached, or a named not-modelled case). Greedy with a return to the last
    /// star: every instruction but <c>*</c> and <c>[]</c> takes exactly one unit, so this is complete for this grammar.
    /// </summary>
    internal bool? Match(ReadOnlySpan<char> subject, ExpressionContext context, out NotModelledCase open, out bool fault)
    {
        open = NotModelledCase.None;
        fault = false;
        int p = 0, s = 0, starP = -1, starS = 0;
        while (s < subject.Length)
        {
            if (p < _program.Length)
            {
                Instruction instruction = _program[p];
                switch (instruction.Kind)
                {
                    case Kind.Star:
                        if (instruction.FaultFollows)
                        {
                            open = NotModelledCase.PatternFaultAfterStar;
                            return null;
                        }
                        starP = p++;
                        starS = s;
                        continue;
                    case Kind.Empty:
                        p++;
                        continue;
                    case Kind.Fault:
                        fault = true;
                        return null;
                }
                bool? one = Step(instruction, subject[s], context, out open, out fault);
                if (one is null)
                {
                    return null;
                }
                if (one.Value)
                {
                    p++;
                    s++;
                    continue;
                }
            }
            if (starP < 0)
            {
                return false;
            }
            p = starP + 1;
            s = ++starS;
        }
        while (p < _program.Length && _program[p].Kind is Kind.Star or Kind.Empty)
        {
            if (_program[p].FaultFollows)
            {
                open = NotModelledCase.PatternFaultAfterStar;
                return null;
            }
            p++;
        }
        return p == _program.Length;
    }

    /// <summary>Whether one instruction takes the unit <paramref name="u"/>; null with the failure.</summary>
    private bool? Step(in Instruction instruction, char u, ExpressionContext context, out NotModelledCase open, out bool fault)
    {
        open = NotModelledCase.None;
        fault = false;
        switch (instruction.Kind)
        {
            case Kind.AnyOne:
                return true;
            case Kind.Digit:
                // PAT-13: ASCII digits and the full-width digits, not other scripts' digits.
                return u is (>= '0' and <= '9') or (>= '０' and <= '９');
            case Kind.Literal:
                return UnitEquals(instruction.Unit, u, context, out open);
            default:
                {
                    if (instruction.Caret)
                    {
                        open = NotModelledCase.PatternCaretClass;
                        return null;
                    }
                    bool member = false;
                    for (int m = instruction.First; m < instruction.First + instruction.Count && !member; m++)
                    {
                        ClassMember range = _members[m];
                        bool? hit;
                        if (range.Low == range.High)
                        {
                            hit = UnitEquals(range.Low, u, context, out open);
                        }
                        else
                        {
                            if (context.Compare is not (TextCompareMode.Binary or TextCompareMode.Database))
                            {
                                open = NotModelledCase.PatternUnitCompare;
                                return null;
                            }
                            // PAT-03: under the database order both bounds and the unit are lowercased and compared as code
                            // units (collation plays no part); under Binary the units themselves.
                            bool fold = context.Compare == TextCompareMode.Database;
                            char low = fold ? Lower(range.Low) : range.Low, high = fold ? Lower(range.High) : range.High, unit = fold ? Lower(u) : u;
                            if (low > high)
                            {
                                // PAT-02: a reversed range ([C-A], [z-a], and [Z-a] once lowercased) is an invalid pattern where it is tried.
                                fault = true;
                                return null;
                            }
                            hit = low <= unit && unit <= high;
                        }
                        if (hit is null)
                        {
                            return null;
                        }
                        member = hit.Value;
                    }
                    return member != instruction.Negated;
                }
        }
    }

    /// <summary>Unit equality under the context: identity, else the same lowercase mapping under the database order (Like is case-insensitive there).</summary>
    private static bool? UnitEquals(char pattern, char subject, ExpressionContext context, out NotModelledCase open)
    {
        open = NotModelledCase.None;
        if (pattern == subject)
        {
            return true;
        }
        switch (context.Compare)
        {
            case TextCompareMode.Binary:
                return false;
            case TextCompareMode.Database:
                // PAT-03: a literal unit is mapped like a range's, so ä and Ä match each other and ǅ does not match ǆ.
                return Lower(pattern) == Lower(subject);
            default:
                open = NotModelledCase.PatternUnitCompare;
                return null;
        }
    }

    /// <summary>
    /// The one-to-one lowercase mapping of a range bound and unit (PAT-03): the simple Unicode lower case of the unit, which
    /// leaves İ, the Kelvin and Ohm signs, µ and the titlecase digraphs as they are.
    /// </summary>
    private static char Lower(char u) => u switch
    {
        'İ' or 'K' or 'Ω' or 'ǅ' or 'ǈ' or 'ǋ' or 'ǲ' => u,
        _ => char.ToLowerInvariant(u),
    };
}
