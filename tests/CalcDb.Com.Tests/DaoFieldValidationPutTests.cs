using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// DAO-level tests for <c>Field.ValidationRule</c> and <c>Field.ValidationText</c> puts on a stored table's field
/// (W-08 task 9dvt; qualified.md <c>r-q37</c>): live getters with trailing NUL on <c>ValidationRule</c>, removal,
/// validation errors (3783, 3320, 3421 with/without <c>IErrorInfo</c>), unchanged file bytes on error, preserved
/// <c>DBEngine.Errors</c> on success, immediate enforcement by subsequent <c>INSERT</c>, and named refusals.
/// </summary>
public sealed unsafe class DaoFieldValidationPutTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int DbVersion120 = 128;
    private const int DbFailOnError = 128;

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-dao-valrule-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_C7E7, Standalone: true, 0, 0, 0, 0, 0));

    public void Dispose()
    {
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            Marshal.Release(_held[i]);
        }
        foreach (nint cell in _cells)
        {
            var v = (NativeVariant*)cell;
            if ((VarType)v->Vt == VarType.Bstr)
            {
                Marshal.FreeBSTR(v->Pointer);
            }
            NativeMemory.Free((void*)cell);
        }
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private DBEngineObject CreateEngine()
    {
        nint engine;
        Assert.Equal(0, DBEngineObject.Create(_engine.Binding, 0, NativeRoute.IidIDispatch, &engine));
        _held.Add(engine);
        return ComObject.Recover<DBEngineObject>(engine);
    }

    private nint Arg(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case int i: v->Vt = (ushort)VarType.I4; v->Int64 = (uint)i; break;
            case bool b: v->Vt = (ushort)VarType.Bool; v->Int64 = b ? 0xFFFF : 0; break;
            case string text: v->Vt = (ushort)VarType.Bstr; v->Pointer = Marshal.StringToBSTR(text); break;
            case DBNull: v->Vt = (ushort)VarType.Null; break;
        }
        _cells.Add((nint)v);
        return (nint)v;
    }

    private CallResult Get(DispatchObject target, string member, params object?[] args) =>
        target.Run(target.Declaration.Member(member, InvokeKind.PropertyGet), [.. args.Select(Arg)]);

    private CallResult Put(DispatchObject target, string member, params object?[] args) =>
        target.Run(target.Declaration.Member(member, InvokeKind.PropertyPut), [.. args.Select(Arg)]);

    private CallResult Method(DispatchObject target, string member, params object?[] args) =>
        target.Run(target.Declaration.Member(member, InvokeKind.Method), [.. args.Select(Arg)]);

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        nint p = result.OwnsScalar ? result.Scalar.Pointer : result.Value!.Pointer->Pointer;
        _held.Add(p);
        return ComObject.Recover<T>(p);
    }

    private static string TextOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        nint ptr = result.HasScalar ? result.Scalar.Pointer : result.Value!.Pointer->Pointer;
        string s = Marshal.PtrToStringBSTR(ptr);
        result.ReleaseUntaken();
        return s;
    }

    private (DBEngineObject Engine, DatabaseObject Db, DefinitionFieldObject FieldV, string Path) CreateTableDb()
    {
        string path = Path.Combine(_dir, "val.accdb");
        DBEngineObject eng = CreateEngine();
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120));
        Assert.Null(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, W LONG)", DbFailOnError).Failure);
        DefinitionCollection tds = Recover<DefinitionCollection>(Get(db, "TableDefs"));
        TableDefObject td = Recover<TableDefObject>(Get(tds, "Item", "T"));
        DefinitionCollection fs = Recover<DefinitionCollection>(Get(td, "Fields"));
        DefinitionFieldObject fv = Recover<DefinitionFieldObject>(Get(fs, "Item", "V"));
        return (eng, db, fv, path);
    }

    [Fact]
    public void Put_rule_and_text_roundtrip_enforces_on_insert_and_removes_cleanly()
    {
        (_, DatabaseObject db, DefinitionFieldObject fv, _) = CreateTableDb();

        Assert.Null(Put(fv, "ValidationRule", "> 10").Failure);
        Assert.Null(Put(fv, "ValidationText", "must exceed 10").Failure);
        Assert.Equal("> 10\0", TextOf(Get(fv, "ValidationRule")));
        Assert.Equal("must exceed 10", TextOf(Get(fv, "ValidationText")));

        CallResult rejected = Method(db, "Execute", "INSERT INTO T (ID, V, W) VALUES (1, 5, 0)", DbFailOnError);
        Assert.NotNull(rejected.Failure);
        Assert.Equal(3316, rejected.Failure!.Scode & 0xFFFF);
        Assert.Equal("must exceed 10", rejected.Failure.Description);

        Assert.Null(Put(fv, "ValidationRule", "").Failure);
        Assert.Null(Put(fv, "ValidationText", "").Failure);
        Assert.Equal("", TextOf(Get(fv, "ValidationRule")));
        Assert.Equal("", TextOf(Get(fv, "ValidationText")));
        Assert.Null(Method(db, "Execute", "INSERT INTO T (ID, V, W) VALUES (1, 5, 0)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Close").Failure);
    }

    [Fact]
    public void Validation_errors_match_ace_codes_keep_file_unchanged_and_preserve_errors_on_success()
    {
        (DBEngineObject eng, DatabaseObject db, DefinitionFieldObject fv, string path) = CreateTableDb();
        Assert.Null(Method(db, "Close").Failure);
        byte[] before = File.ReadAllBytes(path);

        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        TableDefObject td = Recover<TableDefObject>(Get(Recover<DefinitionCollection>(Get(db2, "TableDefs")), "Item", "T"));
        DefinitionFieldObject fv2 = Recover<DefinitionFieldObject>(Get(Recover<DefinitionCollection>(Get(td, "Fields")), "Item", "V"));

        CallResult multi = Put(fv2, "ValidationRule", "[V] < [W]");
        Assert.Equal(unchecked((int)0x800A0EC7), multi.Failure!.Scode);
        Assert.Equal("DAO.Field", multi.Failure.Source);
        Assert.Null(multi.Failure.HelpFile);
        Assert.Equal(-1, multi.Failure.HelpContext);
        Assert.False(multi.Failure.PublishesErrorInfo);

        CallResult syn = Put(fv2, "ValidationRule", ">");
        Assert.Equal(unchecked((int)0x800A0CF8), syn.Failure!.Scode);
        Assert.Equal("DAO.Field", syn.Failure.Source);
        Assert.Equal("jeterr40.chm", syn.Failure.HelpFile);
        Assert.Equal(5003320, syn.Failure.HelpContext);
        Assert.False(syn.Failure.PublishesErrorInfo);

        CallResult nullRule = Put(fv2, "ValidationRule", DBNull.Value);
        Assert.Equal(unchecked((int)0x800A0D5D), nullRule.Failure!.Scode);
        Assert.True(nullRule.Failure.PublishesErrorInfo);

        CallResult nullText = Put(fv2, "ValidationText", DBNull.Value);
        Assert.Equal(unchecked((int)0x800A0D5D), nullText.Failure!.Scode);
        Assert.True(nullText.Failure.PublishesErrorInfo);

        Assert.Null(Method(db2, "Close").Failure);
        Assert.Equal(before, File.ReadAllBytes(path));

        // Success after an error preserves DBEngine.Errors; replacing a 2-page chained blob refuses by name.
        DatabaseObject db3 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        TableDefObject td3 = Recover<TableDefObject>(Get(Recover<DefinitionCollection>(Get(db3, "TableDefs")), "Item", "T"));
        DefinitionFieldObject fv3 = Recover<DefinitionFieldObject>(Get(Recover<DefinitionCollection>(Get(td3, "Fields")), "Item", "V"));
        Assert.Equal(unchecked((int)0x800A0CF8), Put(fv3, "ValidationRule", ">").Failure!.Scode);
        string twoPage = "<> '" + new string('a', 1040) + "' And <> '" + new string('b', 1040) + "'";
        Assert.Null(Put(fv3, "ValidationRule", twoPage).Failure);
        Assert.Single(eng.Engine.Errors);
        Assert.Equal(3320, eng.Engine.Errors[0].Number);

        CallResult chainedReplace = Put(fv3, "ValidationText", "x");
        Assert.Equal(FailureKind.NotImplemented, chainedReplace.Failure!.Kind);
        Assert.Null(Method(db3, "Close").Failure);
    }

    [Fact]
    public void Dispatch_invoke_hooks_and_validation_rule_edge_cases_match_records()
    {
        string path = Path.Combine(_dir, "val-edges.accdb");
        DBEngineObject eng = CreateEngine();
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120));
        Assert.Null(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, W LONG, S TEXT(40))", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "CREATE INDEX IX_T_S ON T (S)", DbFailOnError).Failure);
        DefinitionCollection tds = Recover<DefinitionCollection>(Get(db, "TableDefs"));
        TableDefObject td = Recover<TableDefObject>(Get(tds, "Item", "T"));
        DefinitionCollection fields = Recover<DefinitionCollection>(Get(td, "Fields"));
        DefinitionFieldObject fv = Recover<DefinitionFieldObject>(Get(fields, "Item", "V"));
        DefinitionFieldObject fs = Recover<DefinitionFieldObject>(Get(fields, "Item", "S"));

        // r-expr-1/mut-validation-rules call 211: constant expression "1/0" is accepted.
        Assert.Null(Put(fv, "ValidationRule", "1/0").Failure);
        Assert.Equal("1/0\0", TextOf(Get(fv, "ValidationRule")));

        // r-expr-1/mut-validation-rules call 236: standalone-unknown function fails DAO 3388 (no IErrorInfo).
        CallResult badFn = Put(fv, "ValidationRule", "CVErr(5)");
        Assert.Equal(unchecked((int)0x800A0D3C), badFn.Failure!.Scode);
        Assert.Equal("DAO.Field", badFn.Failure.Source);
        Assert.Equal("Unknown function 'CVErr' in validation expression or default value on 'T.V'.", badFn.Failure.Description);
        Assert.Equal("jeterr40.chm", badFn.Failure.HelpFile);
        Assert.Equal(5003388, badFn.Failure.HelpContext);
        Assert.False(badFn.Failure.PublishesErrorInfo);

        // ExpressionUse.ValidationRule allows Like on an indexed Text field (unlike Query's NotModelled.IndexedLike).
        Assert.Null(Put(fs, "ValidationRule", "Like 'A*'").Failure);

        // Unrecorded syntax error (explicit-column unclosed paren) and single-other-column reference refuse by name.
        Assert.Equal(FailureKind.NotImplemented, Put(fv, "ValidationRule", "[V] > (10").Failure!.Kind);
        Assert.Equal(FailureKind.NotImplemented, Put(fv, "ValidationRule", "[W] > 10").Failure!.Kind);

        // IDispatch::Invoke path: VT_NULL -> 3421 with IErrorInfo; VT_I4 -> E_NOTIMPL refusal.
        (int hrNull, ExcepInfo exNull, bool hasEiNull) = InvokePut(fv, "ValidationRule", new NativeVariant { Vt = (ushort)VarType.Null });
        Assert.Equal(HResults.DISP_E_EXCEPTION, hrNull);
        Assert.Equal(unchecked((int)0x800A0D5D), exNull.Scode);
        Assert.True(hasEiNull);
        FreeExcep(exNull);

        (int hrInt, ExcepInfo exInt, _) = InvokePut(fv, "ValidationText", new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 7 });
        Assert.Equal(HResults.DISP_E_EXCEPTION, hrInt);
        Assert.Equal(HResults.E_NOTIMPL, exInt.Scode);
        FreeExcep(exInt);

        // Closed Database field: VT_BSTR reports 3420 via Live; VT_NULL refuses by name instead of raising 3421 before Live.
        Assert.Null(Method(db, "Close").Failure);
        nint bstr = Marshal.StringToBSTR("> 1");
        try
        {
            (int hrClosedBstr, ExcepInfo exClosedBstr, _) = InvokePut(fv, "ValidationRule", new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr });
            Assert.Equal(HResults.DISP_E_EXCEPTION, hrClosedBstr);
            Assert.Equal(unchecked((int)0x800A0D5C), exClosedBstr.Scode);
            FreeExcep(exClosedBstr);
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }

        (int hrClosedNull, ExcepInfo exClosedNull, _) = InvokePut(fv, "ValidationRule", new NativeVariant { Vt = (ushort)VarType.Null });
        Assert.Equal(HResults.DISP_E_EXCEPTION, hrClosedNull);
        Assert.Equal(HResults.E_NOTIMPL, exClosedNull.Scode);
        FreeExcep(exClosedNull);
    }

    [Fact]
    public void Multi_clause_implied_rules_match_r_q37_put_and_refuse_unrecorded_forms_and_enforcement()
    {
        string path = Path.Combine(_dir, "val-multiclause.accdb");
        DBEngineObject eng = CreateEngine();
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120));
        Assert.Null(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, S TEXT(40))", DbFailOnError).Failure);
        TableDefObject td = Recover<TableDefObject>(Get(Recover<DefinitionCollection>(Get(db, "TableDefs")), "Item", "T"));
        DefinitionCollection fields = Recover<DefinitionCollection>(Get(td, "Fields"));
        DefinitionFieldObject fv = Recover<DefinitionFieldObject>(Get(fields, "Item", "V"));
        DefinitionFieldObject fs = Recover<DefinitionFieldObject>(Get(fields, "Item", "S"));

        // Multi-clause and parenthesized implied-operand forms put cleanly on empty T:
        // r-q42-field-rule-and/put-forms-accdb rows 22, 48, 50, 54, 56, 68; r-q42-field-rule-and/put-refusals-accdb row 74.
        foreach (string recorded in new[]
        {
            "<> 'a' Or <> 'b'",
            "> 10 And < 100",
            "(<> 'a') And <> 'b'",
            "(> 10)",
            "[S] <> 'a' And <> 'b'",
            "<> 'a' And [S] <> 'b'",
        })
        {
            Assert.Null(Put(fs, "ValidationRule", recorded).Failure);
        }

        // Mixed clause referencing another column fails DAO 3783
        // (r-q42-field-rule-and/put-refusals-accdb rows 59, 67, 84).
        CallResult otherCol = Put(fs, "ValidationRule", "> 10 Or [V] < 0");
        Assert.NotNull(otherCol.Failure);
        Assert.Equal(3783, otherCol.Failure!.Scode & 0xFFFF);

        // Unrecorded bare negative numeric literal still refuses by name at put (never guessing 3320 or truthiness).
        CallResult unrecordedNeg = Put(fv, "ValidationRule", "1 Or -5");
        Assert.NotNull(unrecordedNeg.Failure);
        Assert.Equal(FailureKind.NotImplemented, unrecordedNeg.Failure!.Kind);

        // First ValidationRule read on fs (after puts with no intervening Get) reads the stored NUL-terminated Memo bytes
        // (r-q42-field-rule-and/put-forms-accdb rows 16-17, 68-69; put-forms-mdb rows 16-17, 68-69).
        Assert.Null(Put(fs, "ValidationRule", "<> 'a' And <> 'b'").Failure);
        Assert.Equal("<> 'a' And <> 'b'\0", TextOf(Get(fs, "ValidationRule")));
        Assert.Null(Put(fv, "ValidationRule", "Between 1 And 10").Failure);
        Assert.Equal("Between 1 And 10\0", TextOf(Get(fv, "ValidationRule")));

        // Enforcement of <> 'a' And <> 'b' on DAO Execute (128)
        // (r-q42-field-rule-and/rule-and2-ne-accdb rows 23, 34, 52, 70):
        // 'c' and Null admit; 'a' and 'b' fail DAO 3317.
        Assert.Null(Method(db, "Execute", "INSERT INTO T (ID, V, S) VALUES (1, 5, 'c')", DbFailOnError).Failure);

        CallResult failClause1 = Method(db, "Execute", "INSERT INTO T (ID, V, S) VALUES (2, 5, 'a')", DbFailOnError);
        Assert.NotNull(failClause1.Failure);
        Assert.Equal(3317, failClause1.Failure!.Scode & 0xFFFF);
        Assert.Contains("<> 'a' And <> 'b'", failClause1.Failure.Description);

        CallResult failClause2 = Method(db, "Execute", "INSERT INTO T (ID, V, S) VALUES (3, 5, 'b')", DbFailOnError);
        Assert.NotNull(failClause2.Failure);
        Assert.Equal(3317, failClause2.Failure!.Scode & 0xFFFF);

        Assert.Null(Method(db, "Execute", "INSERT INTO T (ID, V, S) VALUES (4, 5, Null)", DbFailOnError).Failure);

        // Single implied Between 1 And 10 on V rejects 20 with DAO 3317.
        CallResult outOfRange = Method(db, "Execute", "INSERT INTO T (ID, V, S) VALUES (5, 20, 'ok')", DbFailOnError);
        Assert.NotNull(outOfRange.Failure);
        Assert.Equal(3317, outOfRange.Failure!.Scode & 0xFFFF);

        Assert.Null(Method(db, "Close").Failure);
    }

    [Fact]
    public void Put_refusals_tabledef_rule_3320_subquery_3420_and_aggregate_multi_col_3783_match_r_q42()
    {
        string path = Path.Combine(_dir, "val-r-q42-refusals.accdb");
        DBEngineObject eng = CreateEngine();
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120));
        Assert.Null(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, W TEXT(50))", DbFailOnError).Failure);
        TableDefObject td = Recover<TableDefObject>(Get(Recover<DefinitionCollection>(Get(db, "TableDefs")), "Item", "T"));
        DefinitionCollection fields = Recover<DefinitionCollection>(Get(td, "Fields"));
        DefinitionFieldObject fv = Recover<DefinitionFieldObject>(Get(fields, "Item", "V"));
        DefinitionFieldObject fw = Recover<DefinitionFieldObject>(Get(fields, "Item", "W"));

        // TableDef.ValidationRule implied-subject forms fail DAO 3320 from DAO.TableDef without IErrorInfo
        // (evidence/r-q42-field-rule-and/edge-forms-accdb rows 7-8, edge-forms-mdb rows 7-8); valid table rules and
        // non-implied syntax errors stay refused by name.
        foreach (string implied in new[] { "<> 'a' And <> 'b'", "<> 'a'" })
        {
            CallResult tdFail = Put(td, "ValidationRule", implied);
            Assert.Equal(unchecked((int)0x800A0CF8), tdFail.Failure!.Scode);
            Assert.Equal("DAO.TableDef", tdFail.Failure.Source);
            Assert.Equal(FieldValidationRuleValidator.SyntaxErrorDescription, tdFail.Failure.Description);
            Assert.Equal("jeterr40.chm", tdFail.Failure.HelpFile);
            Assert.Equal(5003320, tdFail.Failure.HelpContext);
            Assert.False(tdFail.Failure.PublishesErrorInfo);
        }
        Assert.Equal(FailureKind.NotImplemented, Put(td, "ValidationRule", "[V] > 0").Failure!.Kind);
        Assert.Equal(FailureKind.NotImplemented, Put(td, "ValidationRule", "[V] >").Failure!.Kind);

        // Unclosed leading paren on implied-operand `(<> ...` fails DAO 3320
        // (evidence/r-q42-field-rule-and/put-refusals-accdb row 58, put-refusals-mdb row 58);
        // other unrecorded leading-paren shapes stay refused by name.
        CallResult paren = Put(fw, "ValidationRule", "(<> 'a'");
        Assert.Equal(unchecked((int)0x800A0CF8), paren.Failure!.Scode);
        Assert.Equal("DAO.Field", paren.Failure.Source);
        Assert.Equal(FieldValidationRuleValidator.MissingParenthesisDescription, paren.Failure.Description);
        Assert.Equal("jeterr40.chm", paren.Failure.HelpFile);
        Assert.Equal(5003320, paren.Failure.HelpContext);
        Assert.False(paren.Failure.PublishesErrorInfo);
        Assert.Equal(FailureKind.NotImplemented, Put(fv, "ValidationRule", "(> 5").Failure!.Kind);

        // `In (SELECT ...)` subquery fails DAO 3420 without invalidating the Field object
        // (evidence/r-q42-field-rule-and/put-refusals-accdb row 72, put-refusals-mdb row 72);
        // scalar subqueries stay NotModelled.Subquery.
        CallResult subq = Put(fw, "ValidationRule", "In (SELECT W FROM T)");
        Assert.Equal(unchecked((int)0x800A0D5C), subq.Failure!.Scode);
        Assert.Equal("DAO.Field", subq.Failure.Source);
        Assert.Equal("Object invalid or no longer set.", subq.Failure.Description);
        Assert.Equal("jeterr40.chm", subq.Failure.HelpFile);
        Assert.Equal(5003420, subq.Failure.HelpContext);
        Assert.False(subq.Failure.PublishesErrorInfo);
        Assert.Equal("W", TextOf(Get(fw, "Name")));
        Assert.Equal(FailureKind.NotModelled, Put(fw, "ValidationRule", "= (SELECT 1)").Failure!.Kind);

        // Multi-column Sum aggregate fails DAO 3783 (no helpFile, helpContext -1, no IErrorInfo:
        // evidence/r-q42-field-rule-and/put-refusals-accdb row 73, put-refusals-mdb row 73),
        // while single-column Sum and other unmodelled functions stay NotModelled.Function.
        CallResult multiAgg = Put(fw, "ValidationRule", "> Sum([V])");
        Assert.Equal(unchecked((int)0x800A0EC7), multiAgg.Failure!.Scode);
        Assert.Equal("DAO.Field", multiAgg.Failure.Source);
        Assert.Equal(FieldValidationRuleValidator.MultipleColumnsDescription, multiAgg.Failure.Description);
        Assert.Null(multiAgg.Failure.HelpFile);
        Assert.Equal(-1, multiAgg.Failure.HelpContext);
        Assert.False(multiAgg.Failure.PublishesErrorInfo);

        Assert.Equal(FailureKind.NotModelled, Put(fw, "ValidationRule", "> Sum([W])").Failure!.Kind);
        Assert.Equal(FailureKind.NotModelled, Put(fw, "ValidationRule", "> Avg([V])").Failure!.Kind);

        // ValidationRule getter lifecycle:
        // 1. First read after put -> trailing NUL; subsequent re-put -> no trailing NUL; clear -> ""
        //    (evidence/r-q42-field-rule-and/put-forms-accdb rows 16-19, put-forms-mdb rows 16-19;
        //    evidence/r-expr-1/mut-validation-rules rows 16, 18, 36, 38; evidence/r-q37-valrule-put/remove-rule-only-accdb).
        Assert.Null(Put(fw, "ValidationRule", "<> 'a'").Failure);
        Assert.Equal("<> 'a'\0", TextOf(Get(fw, "ValidationRule")));
        Assert.Null(Put(fw, "ValidationRule", "<> 'b'").Failure);
        Assert.Equal("<> 'b'", TextOf(Get(fw, "ValidationRule")));
        Assert.Null(Put(fw, "ValidationRule", "").Failure);
        Assert.Equal("", TextOf(Get(fw, "ValidationRule")));

        // 2. Read-empty first, then put -> no trailing NUL on same COM object; reopen -> trailing NUL from disk
        //    (evidence/r-cat-1/field-rules rows 16, 25-26, 106).
        Assert.Equal("", TextOf(Get(fv, "ValidationRule")));
        Assert.Null(Put(fv, "ValidationRule", "> 100").Failure);
        Assert.Equal("> 100", TextOf(Get(fv, "ValidationRule")));
        Assert.Null(Method(db, "Close").Failure);

        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DefinitionFieldObject fv2 = Recover<DefinitionFieldObject>(Get(Recover<DefinitionCollection>(Get(Recover<TableDefObject>(Get(Recover<DefinitionCollection>(Get(db2, "TableDefs")), "Item", "T")), "Fields")), "Item", "V"));
        Assert.Equal("> 100\0", TextOf(Get(fv2, "ValidationRule")));
        Assert.Null(Method(db2, "Close").Failure);
    }

    [Fact]
    public void AllowZeroLength_populated_ValidationText_and_select_dynaset_edit_match_r_q42()
    {
        string path = Path.Combine(_dir, "val-r-q42-edit.accdb");
        DBEngineObject eng = CreateEngine();
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120));
        Assert.Null(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, W TEXT(50))", DbFailOnError).Failure);
        TableDefObject td = Recover<TableDefObject>(Get(Recover<DefinitionCollection>(Get(db, "TableDefs")), "Item", "T"));
        DefinitionFieldObject fw = Recover<DefinitionFieldObject>(Get(Recover<DefinitionCollection>(Get(td, "Fields")), "Item", "W"));

        // AllowZeroLength = True then ValidationRule on empty T (evidence/r-q42-field-rule-and/rule-and2-ne-accdb).
        Assert.Null(Put(fw, "AllowZeroLength", true).Failure);
        Assert.Equal(0xFFFF, Get(fw, "AllowZeroLength").Scalar.Int64);
        Assert.Null(Put(fw, "ValidationRule", "<> 'a' And <> 'b'").Failure);
        Assert.Null(Method(db, "Close").Failure);

        // Reopen, insert a row, then edit via SELECT dynaset (Edit / Field.Value put / Update / CancelUpdate / EditMode).
        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        Assert.Null(Method(db2, "Execute", "INSERT INTO T (ID, W) VALUES (1, 'ok')", DbFailOnError).Failure);

        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Method(db2, "OpenRecordset", "SELECT ID, W FROM T ORDER BY ID", 2));
        SelectFieldObject rsw = Recover<SelectFieldObject>(Get(Recover<SelectFieldsObject>(Get(rs, "Fields")), "Item", "W"));
        Assert.Equal(0, Get(rs, "EditMode").Scalar.Int64);

        // Unrecorded depth-0 SELECT dynaset Field.Value put without Edit refuses by name; CancelUpdate without Edit
        // fails DAO 3020 from DAO.Recordset (evidence/r-str-ado-act/str-edit-requery call 32).
        Assert.Equal(FailureKind.NotModelled, Put(rsw, "Value", "x").Failure!.Kind);
        CallResult cancelNoEdit = Method(rs, "CancelUpdate");
        Assert.Equal(FailureKind.Dao, cancelNoEdit.Failure!.Kind);
        Assert.Equal(3020, cancelNoEdit.Failure.Number);
        Assert.Equal("DAO.Recordset", cancelNoEdit.Failure.Source);
        Assert.False(cancelNoEdit.Failure.PublishesErrorInfo);

        // Violating value fails Update with DAO 3317, keeps EditMode == 1, CancelUpdate restores EditMode == 0;
        // nested Edit while EditMode == 1 refuses by name.
        Assert.Null(Method(rs, "Edit").Failure);
        Assert.Equal(1, Get(rs, "EditMode").Scalar.Int64);
        Assert.Equal(FailureKind.NotModelled, Method(rs, "Edit").Failure!.Kind);
        Assert.Null(Put(rsw, "Value", "a").Failure);
        Assert.Equal("a", TextOf(Get(rsw, "Value")));
        CallResult rej3317 = Method(rs, "Update");
        Assert.Equal(3317, rej3317.Failure!.Scode & 0xFFFF);
        Assert.Equal(1, Get(rs, "EditMode").Scalar.Int64);
        Assert.Null(Method(rs, "CancelUpdate").Failure);
        Assert.Equal(0, Get(rs, "EditMode").Scalar.Int64);
        Assert.Equal("ok", TextOf(Get(rsw, "Value")));

        // Admitted value (including "" because AllowZeroLength = True) updates row and reads back immediately.
        Assert.Null(Method(rs, "Edit").Failure);
        Assert.Null(Put(rsw, "Value", "").Failure);
        Assert.Null(Method(rs, "Update").Failure);
        Assert.Equal(0, Get(rs, "EditMode").Scalar.Int64);
        Assert.Equal("", TextOf(Get(rsw, "Value")));
        Assert.Null(Method(rs, "Close").Failure);

        // Non-empty ValidationText on populated T is admitted while clearing ("") stays refused by name.
        DefinitionFieldObject fw2 = Recover<DefinitionFieldObject>(Get(Recover<DefinitionCollection>(Get(Recover<TableDefObject>(Get(Recover<DefinitionCollection>(Get(db2, "TableDefs")), "Item", "T")), "Fields")), "Item", "W"));
        Assert.Null(Put(fw2, "ValidationText", "W must not be a or b").Failure);
        Assert.Equal(FailureKind.NotImplemented, Put(fw2, "ValidationText", "").Failure!.Kind);

        SelectRecordsetObject rs2 = Recover<SelectRecordsetObject>(Method(db2, "OpenRecordset", "SELECT ID, W FROM T ORDER BY ID", 2));
        SelectFieldObject rsw2 = Recover<SelectFieldObject>(Get(Recover<SelectFieldsObject>(Get(rs2, "Fields")), "Item", "W"));
        Assert.Null(Method(rs2, "Edit").Failure);
        Assert.Null(Put(rsw2, "Value", "b").Failure);
        CallResult rej3316 = Method(rs2, "Update");
        Assert.Equal(3316, rej3316.Failure!.Scode & 0xFFFF);
        Assert.Equal("W must not be a or b", rej3316.Failure.Description);
        Assert.Null(Method(rs2, "CancelUpdate").Failure);
        Assert.Null(Method(rs2, "Close").Failure);

        // Snapshot SELECT refuses Edit by name.
        SelectRecordsetObject snap = Recover<SelectRecordsetObject>(Method(db2, "OpenRecordset", "SELECT ID, W FROM T ORDER BY ID", 4));
        Assert.Equal(FailureKind.NotModelled, Method(snap, "Edit").Failure!.Kind);
        Assert.Null(Method(snap, "Close").Failure);
        Assert.Null(Method(db2, "Close").Failure);
    }

    private static (int Hr, ExcepInfo Excep, bool HasErrorInfo) InvokePut(DispatchObject target, string member, NativeVariant value)
    {
        if (NativeRoute.GetErrorInfo(0, out nint stale) == 0 && stale != 0)
        {
            Marshal.Release(stale);
        }
        int propPutId = -3;
        DispParams dp = new()
        {
            Args = (nint)(&value),
            ArgCount = 1,
            NamedArgs = (nint)(&propPutId),
            NamedArgCount = 1,
        };
        NativeVariant result = default;
        ExcepInfo excep = default;
        uint argError = 0;
        int hr = target.Invoke(target.Declaration.Member(member, InvokeKind.PropertyPut).MemberId, (ushort)InvokeKind.PropertyPut, &dp, &result, &excep, &argError);
        bool hasErrorInfo = NativeRoute.GetErrorInfo(0, out nint errorInfo) == 0 && errorInfo != 0;
        if (hasErrorInfo)
        {
            Marshal.Release(errorInfo);
        }
        return (hr, excep, hasErrorInfo);
    }

    private static void FreeExcep(ExcepInfo excep)
    {
        if (excep.Source != 0)
        {
            Marshal.FreeBSTR(excep.Source);
        }
        if (excep.Description != 0)
        {
            Marshal.FreeBSTR(excep.Description);
        }
        if (excep.HelpFile != 0)
        {
            Marshal.FreeBSTR(excep.HelpFile);
        }
    }
}
