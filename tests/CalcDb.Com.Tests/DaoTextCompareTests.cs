using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;
using CalcDb.Values.Scalar;
using CalcDb.Values.Windows;

namespace CalcDb.Com.Tests;

/// <summary>
/// Verifies that <see cref="DatabaseObject.CreateExpressionContext"/> wires <see cref="WindowsTextCollation"/> into
/// every DAO expression context (SQL queries, Find criteria, and mutation rule binder) so <c>InStr</c> and
/// <c>StrComp</c> with omitted or text compare mode (=1) evaluate through DAO
/// (<c>r-str-ado-act/str-projection</c>, <c>r-str-ado-act/str-revisit</c>,
/// <c>r-def-expr-sql/str-stored-instr-strcomp</c>).
/// </summary>
public sealed unsafe class DaoTextCompareTests : IDisposable
{
    private const string StrFixture = "22b81465269ae9b0dd729590d416c3778eb3385aaa2721a160665537a2cbc5a7"; // r-str-ado-act/str-projection

    private readonly List<string> _files = [];
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_7E58, Standalone: true, 0, 0, 0, 0, 0));

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
        foreach (string file in _files)
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Database_expression_context_supplies_windows_text_collation_for_session_lcid()
    {
        DatabaseObject db = Open(StrFixture, readOnly: true);
        ExpressionContext query = db.CreateExpressionContext(ExpressionUse.Query);
        ExpressionContext find = db.CreateExpressionContext(ExpressionUse.Find);

        WindowsTextCollation expected = WindowsTextCollation.For(SessionLocale.Lcid);
        Assert.Equal(SessionLocale.Lcid, query.Lcid);
        Assert.Same(expected, query.TextOrder);
        Assert.Same(expected, find.TextOrder);
        Assert.Same(expected, db.Sql.Context.TextOrder);
    }

    [Fact]
    public void OpenRecordset_evaluates_InStr_and_StrComp_with_omitted_and_text_compare_modes()
    {
        DatabaseObject db = Open(StrFixture, readOnly: true);
        const string sql =
            "SELECT ID, InStr(T, Chr(0)) AS NT, InStr('abcABC', 'BC') AS I1, InStr('é', 'É') AS I2, " +
            "StrComp('a', 'A') AS S1, StrComp('ß', 'ss', 1) AS S2 FROM S ORDER BY ID";

        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", sql, 4)));
        int?[] expectedNt = [0, null, 2, 1, 4, 0, 0, 0, 0, 0, 0];
        var ids = new List<int>();
        var nts = new List<int?>();

        while ((short)Ok(Get(rs, "EOF")).Scalar.Int64 == 0)
        {
            SelectFieldsObject fields = Object<SelectFieldsObject>(rs, "Fields");
            SelectFieldObject fId = Object<SelectFieldObject>(fields, "Item", 0);
            SelectFieldObject fNt = Object<SelectFieldObject>(fields, "Item", 1);
            SelectFieldObject fI1 = Object<SelectFieldObject>(fields, "Item", 2);
            SelectFieldObject fI2 = Object<SelectFieldObject>(fields, "Item", 3);
            SelectFieldObject fS1 = Object<SelectFieldObject>(fields, "Item", 4);
            SelectFieldObject fS2 = Object<SelectFieldObject>(fields, "Item", 5);

            ids.Add((int)Ok(Get(fId, "Value")).Scalar.Int64);
            NativeVariant nt = Ok(Get(fNt, "Value")).Scalar;
            nts.Add((VarType)nt.Vt == VarType.Null ? null : (int)nt.Int64);

            Assert.Equal(2, (int)Ok(Get(fI1, "Value")).Scalar.Int64);
            Assert.Equal(1, (int)Ok(Get(fI2, "Value")).Scalar.Int64);
            Assert.Equal((short)0, (short)Ok(Get(fS1, "Value")).Scalar.Int64);
            Assert.Equal((short)0, (short)Ok(Get(fS2, "Value")).Scalar.Int64);

            Ok(Method(rs, "MoveNext"));
        }

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], ids);
        Assert.Equal(expectedNt, nts);
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Recordset_FindFirst_and_FindNext_evaluate_InStr_with_omitted_compare_mode()
    {
        DatabaseObject db = Open(StrFixture, readOnly: true);
        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "S", 4)));

        Ok(Method(rs, "FindFirst", "InStr(T, Chr(0)) > 0"));
        Assert.Equal((short)0, (short)Ok(Get(rs, "NoMatch")).Scalar.Int64);
        FieldsObject fields = Object<FieldsObject>(rs, "Fields");
        FieldObject fId = Object<FieldObject>(fields, "Item", "ID");
        Assert.Equal(3, (int)Ok(Get(fId, "Value")).Scalar.Int64);

        Ok(Method(rs, "FindNext", "InStr(T, Chr(0)) > 0"));
        Assert.Equal((short)0, (short)Ok(Get(rs, "NoMatch")).Scalar.Int64);
        Assert.Equal(4, (int)Ok(Get(fId, "Value")).Scalar.Int64);

        Ok(Method(rs, "Close"));
    }

    private DatabaseObject Open(string fixture, bool readOnly = false)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dao-text-compare-{Guid.NewGuid():N}.accdb");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(fixture));
        _files.Add(path);
        return Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(readOnly)])));
    }

    private nint Arg(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case int i:
                v->Vt = (ushort)VarType.I4;
                v->Int64 = (uint)i;
                break;
            case bool b:
                v->Vt = (ushort)VarType.Bool;
                v->Int64 = b ? 0xFFFF : 0;
                break;
            case string s:
                v->Vt = (ushort)VarType.Bstr;
                v->Pointer = Marshal.StringToBSTR(s);
                break;
        }
        _cells.Add((nint)v);
        return (nint)v;
    }

    private CallResult Call(DispatchObject target, string member, InvokeKind kind, params object?[] args)
    {
        nint[] cells = [.. args.Select(Arg)];
        return target.Run(target.Declaration.Member(member, kind), cells);
    }

    private CallResult Get(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyGet, args);

    private CallResult Method(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.Method, args);

    private static CallResult Ok(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        return result;
    }

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        _held.Add(result.Scalar.Pointer);
        return ComObject.Recover<T>(result.Scalar.Pointer);
    }

    private T Object<T>(DispatchObject target, string member, params object?[] args) where T : ComObject => Recover<T>(Ok(Get(target, member, args)));
}
