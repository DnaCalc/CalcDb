using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// Standalone COM tests for <c>w14-ado-params-errinfo</c>:
/// <list type="bullet">
/// <item><c>prfx-f3</c>: custom COM <c>IErrorInfo</c> for <c>Failure.NullExcepStrings</c> (<c>GetGUID</c> = <c>S_OK</c> <c>Guid.Empty</c>,
/// <c>GetSource</c>/<c>GetDescription</c>/<c>GetHelpFile</c>/<c>GetHelpContext</c> = <c>0x80004005</c> <c>E_FAIL</c>;
/// <c>r-ado-1b/persist-overwrite-flags</c> #31, <c>r-fix-wire-ado/persist-save-two-parameter</c> #39/#47/#55).</item>
/// <item>Core ADO <c>Command</c> and <c>Parameters</c> stops across <c>r-ado-1b</c> and <c>r-fix-wire-ado</c>:
/// <c>cmd-before-connection</c>, <c>cmd-execute-args</c>, <c>param-create-forms</c>, <c>param-mutate-after-append</c>,
/// <c>param-refresh-after-text-change</c>, <c>param-value-tag-matrix</c>, <c>ado-params-manual-append</c>,
/// <c>ado-params-refresh-only</c>, <c>ado-params-saved-query</c>, and <c>ado-command-parameters-fresh</c>.</item>
/// </list>
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoParamsAndErrorInfoTests
{
    private static readonly Guid IidCommand = new("986761E8-7269-4890-AA65-AD7C03697A6D");
    private static readonly Guid IidParameter = new("0000150C-0000-0010-8000-00AA006D2EA4");
    private static readonly Guid IidOleDbCommand = new("0C733A63-2A1C-11CE-ADE5-00AA0044773D");
    private static readonly Guid IidOleDbAccessor = new("0C733A8C-2A1C-11CE-ADE5-00AA0044773D");

    // Exact retained ArtifactStore fixtures from the cited ACE records.
    private const string Ado1bMainFixture = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7"; // inputs/r-ado-1b-main.accdb
    private const string Ado5Fixture = "f0e5a1384126593d7cd78696de7102db28c8d805a628e7789fe3b9ec4fa81cb3";      // inputs/r-fix-wire-ado-ado5.accdb

    private const int HResultEFail = unchecked((int)0x80004005);
    private const int HResultFileAlreadyExists = unchecked((int)0x80030050);
    private const int HResultParamNotOptional = unchecked((int)0x80040E10);
    private const int HResultErrorsOccurred = unchecked((int)0x80040E21);
    private const int HResultOutOfRange = unchecked((int)0x8002000A);
    private const int HResultArgumentsWrongType = unchecked((int)0x800A0BB9);
    private const int HResultItemNotFound = unchecked((int)0x800A0CC1);
    private const int HResultWrongValueType = unchecked((int)0x800A0D5D);
    private const int HResultParamImproperlyDefined = unchecked((int)0x800A0E7C);

    [Fact]
    public void CalcDb_ADO_prfx_f3_IErrorInfo_and_Command_Parameters_match_retained_ACE_records()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-params-errinfo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string dbMain = CopyFixture(store, Ado1bMainFixture, tempDir, "main.accdb");
        string dbAdo5 = CopyFixture(store, Ado5Fixture, tempDir, "ado5.accdb");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                VerifyPersistOverwriteCustomErrorInfo(dbMain, tempDir);
                VerifyCmdBeforeConnection(dbMain);
                VerifyCmdExecuteArgs(dbMain);
                VerifyParamCreateForms(dbMain);
                VerifyParamMutateAfterAppend(dbMain);
                VerifyParamRefreshAfterTextChange(dbMain);
                VerifyParamValueEmptyAndOverflow(dbMain);
                VerifyAdo5ManualAppendRefreshOnlyAndSavedQuery(dbAdo5);

                Assert.False(IsLoaded("msado15.dll"));
                Assert.False(IsLoaded("ACEOLEDB.DLL"));
                Assert.False(IsLoaded("ACECORE.DLL"));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        try { Directory.Delete(tempDir, recursive: true); } catch { }
        if (failure is not null)
        {
            throw new AggregateException(failure);
        }
    }

    /// <summary>
    /// r-ado-1b/persist-overwrite-flags #31, r-fix-wire-ado/persist-save-two-parameter #39/#47/#55:
    /// STG_E_FILEALREADYEXISTS publishes an IErrorInfo whose GetGUID returns S_OK (Guid.Empty) and whose
    /// GetSource, GetDescription, GetHelpFile, and GetHelpContext return E_FAIL (0x80004005).
    /// </summary>
    private static void VerifyPersistOverwriteCustomErrorInfo(string dbPath, string tempDir)
    {
        nint cn = OpenAceConnection(dbPath, mode: 1);
        try
        {
            nint rs = New(OwnRecordset);
            try
            {
                Assert.Null(Put(rs, "CursorLocation", 3).Failure);
                Assert.Null(Call(rs, "Open", "SELECT ID, Code, L, S FROM T ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                string outFile = Path.Combine(tempDir, "overwrite.dat");
                Assert.Null(Call(rs, "Save", outFile).Failure);

                // Invoke Save directly via IDispatch so we can inspect the raw thread-local IErrorInfo getters.
                _ = SetErrorInfo(0, 0);
                int dispId = GetDispId(rs, "Save");
                NativeVariant arg = default;
                arg.Vt = (ushort)VarType.Bstr;
                arg.Pointer = Marshal.StringToBSTR(outFile);
                DispParams dp = new() { Args = (nint)(&arg), ArgCount = 1 };
                ExcepInfo excep = default;
                uint argErr = 0;
                Guid empty = Guid.Empty;
                int hr = ((delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)(*(nint**)rs)[6])(
                    rs, dispId, &empty, 1033, 1, &dp, null, &excep, &argErr);
                Marshal.FreeBSTR(arg.Pointer);

                Assert.Equal(HResults.DISP_E_EXCEPTION, hr);
                Assert.Equal(HResultFileAlreadyExists, excep.Scode);
                Assert.Equal(0, excep.Source);
                Assert.Equal(0, excep.Description);
                Assert.Equal(0, excep.HelpFile);
                Assert.Equal(0u, excep.HelpContext);

                Assert.Equal(HResults.S_OK, GetErrorInfo(0, out nint errInfo));
                Assert.NotEqual(0, errInfo);
                try
                {
                    nint** vt = (nint**)errInfo;
                    Guid g = Guid.NewGuid();
                    Assert.Equal(HResults.S_OK, ((delegate* unmanaged<nint, Guid*, int>)(*vt)[3])(errInfo, &g));
                    Assert.Equal(Guid.Empty, g);

                    nint bstr = 1;
                    Assert.Equal(HResultEFail, ((delegate* unmanaged<nint, nint*, int>)(*vt)[4])(errInfo, &bstr));
                    Assert.Equal(0, bstr);

                    bstr = 1;
                    Assert.Equal(HResultEFail, ((delegate* unmanaged<nint, nint*, int>)(*vt)[5])(errInfo, &bstr));
                    Assert.Equal(0, bstr);

                    bstr = 1;
                    Assert.Equal(HResultEFail, ((delegate* unmanaged<nint, nint*, int>)(*vt)[6])(errInfo, &bstr));
                    Assert.Equal(0, bstr);

                    uint hc = 1;
                    Assert.Equal(HResultEFail, ((delegate* unmanaged<nint, uint*, int>)(*vt)[7])(errInfo, &hc));
                    Assert.Equal(0u, hc);
                }
                finally
                {
                    Release(errInfo);
                }
            }
            finally
            {
                _ = Call(rs, "Close");
                Release(rs);
            }
        }
        finally
        {
            _ = Call(cn, "Close");
            Release(cn);
        }
    }

    /// <summary>r-ado-1b/cmd-before-connection #14-#39: Refresh after attaching an open connection replaces manual params with derived ? (VT_EMPTY).</summary>
    private static void VerifyCmdBeforeConnection(string dbPath)
    {
        nint cmd = New(OwnCommand);
        nint prs = (nint)Get(cmd, "Parameters")!;
        nint px = (nint)Call(cmd, "CreateParameter", "x", 3, 1, Missing, 1).Value!;
        Assert.Null(Call(prs, "Append", new Dispatch(px)).Failure);
        Assert.Null(Put(cmd, "CommandText", "SELECT ID FROM T WHERE L = ?").Failure);
        Assert.Null(Call(prs, "Refresh").Failure);
        Assert.Equal(1, Get(prs, "Count"));

        nint cn = OpenAceConnection(dbPath, mode: 1);
        try
        {
            Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
            Assert.Equal(1, Get(prs, "Count"));
            Assert.Null(Call(prs, "Refresh").Failure);
            Assert.Equal(1, Get(prs, "Count"));

            Outcome exec = Call(cmd, "Execute");
            AssertError(exec, HResultParamNotOptional, "Microsoft Access Database Engine", "Parameter ?_1 has no default value.", 5003747u, IidOleDbCommand);
            AssertConnectionError0(cn, HResultParamNotOptional, "3748", -542379020);
        }
        finally
        {
            Release(px);
            Release(prs);
            Release(cmd);
            _ = Call(cn, "Close");
            Release(cn);
        }
    }

    /// <summary>r-ado-1b/cmd-execute-args #26, #96: string scalar parameter "20" on L = ? returns rows 2 and 4; Options=12345 fails 0x800A0BB9.</summary>
    private static void VerifyCmdExecuteArgs(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath, mode: 1);
        nint cmd = New(OwnCommand);
        try
        {
            Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
            Assert.Null(Put(cmd, "CommandText", "SELECT ID, Code FROM T WHERE L = ?").Failure);
            Assert.Null(Put(cmd, "CommandType", 1).Failure);

            Outcome execStr = Call(cmd, "Execute", Missing, "20", 1);
            Assert.Null(execStr.Failure);
            nint rs = (nint)execStr.Value!;
            try
            {
                Assert.Equal(false, Get(rs, "EOF"));
                nint flds = (nint)Get(rs, "Fields")!;
                nint f0 = (nint)Get(flds, "Item", 0)!;
                Assert.Equal(2, Get(f0, "Value"));
                Release(f0);
                Release(flds);
            }
            finally
            {
                _ = Call(rs, "Close");
                Release(rs);
            }

            Outcome badOpt = Call(cmd, "Execute", Missing, 20, 12345);
            AssertError(badOpt, HResultArgumentsWrongType, "ADODB.Command", expectedHelpContext: 1240641u, expectedInfoGuid: IidCommand);
        }
        finally
        {
            Release(cmd);
            _ = Call(cn, "Close");
            Release(cn);
        }
    }

    /// <summary>r-ado-1b/param-create-forms #18, #27-#28, #153: null BSTR pointer on omitted Name; failed Append freezes derivation; unattempted Append derives 1.</summary>
    private static void VerifyParamCreateForms(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath, mode: 1);
        try
        {
            nint cmd1 = New(OwnCommand);
            try
            {
                Assert.Null(Put(cmd1, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmd1, "CommandText", "SELECT ID FROM T WHERE L = ?").Failure);
                Assert.Null(Put(cmd1, "CommandType", 1).Failure);
                nint pc = (nint)Call(cmd1, "CreateParameter").Value!;
                try
                {
                    // Verify raw BSTR pointer is null (0), matching r-ado-1b/param-create-forms #18.
                    int nameDispId = GetDispId(pc, "Name");
                    DispParams emptyDp = default;
                    NativeVariant nameRes = default;
                    Guid empty = Guid.Empty;
                    int hr = ((delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)(*(nint**)pc)[6])(
                        pc, nameDispId, &empty, 1033, 2, &emptyDp, &nameRes, null, null);
                    Assert.Equal(HResults.S_OK, hr);
                    Assert.Equal((ushort)VarType.Bstr, nameRes.Vt);
                    Assert.Equal(0, nameRes.Pointer);

                    nint cps = (nint)Get(cmd1, "Parameters")!;
                    try
                    {
                        Outcome badAppend = Call(cps, "Append", new Dispatch(pc));
                        AssertError(badAppend, HResultParamImproperlyDefined, "ADODB.Parameters");
                        Assert.Equal(0, Get(cps, "Count"));
                    }
                    finally
                    {
                        Release(cps);
                    }
                }
                finally
                {
                    Release(pc);
                }
            }
            finally
            {
                Release(cmd1);
            }

            nint cmd2 = New(OwnCommand);
            try
            {
                Assert.Null(Put(cmd2, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmd2, "CommandText", "SELECT ID FROM T WHERE L = ?").Failure);
                Assert.Null(Put(cmd2, "CommandType", 1).Failure);
                nint cps = (nint)Get(cmd2, "Parameters")!;
                Assert.Equal(1, Get(cps, "Count"));
                Release(cps);
            }
            finally
            {
                Release(cmd2);
            }
        }
        finally
        {
            _ = Call(cn, "Close");
            Release(cn);
        }
    }

    /// <summary>r-ado-1b/param-mutate-after-append #40-#114: Type, Size, Direction, Attributes, and Value=VT_EMPTY are mutable after Append.</summary>
    private static void VerifyParamMutateAfterAppend(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath, mode: 1);
        nint cmd = New(OwnCommand);
        try
        {
            Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
            Assert.Null(Put(cmd, "CommandText", "SELECT ID, Code FROM T WHERE L = ?").Failure);
            Assert.Null(Put(cmd, "CommandType", 1).Failure);
            nint px = (nint)Call(cmd, "CreateParameter", "x", 3, 1, Missing, 20).Value!;
            nint prs = (nint)Get(cmd, "Parameters")!;
            try
            {
                Assert.Null(Call(prs, "Append", new Dispatch(px)).Failure);
                Assert.Null(Put(px, "Type", 202).Failure);
                Assert.Null(Put(px, "Size", 5).Failure);
                Assert.Null(Put(px, "Value", "20").Failure);
                Assert.Equal(202, Get(px, "Type"));
                Assert.Equal(5, Get(px, "Size"));

                Outcome exec = Call(cmd, "Execute");
                Assert.Null(exec.Failure);
                nint rs = (nint)exec.Value!;
                Assert.Equal(false, Get(rs, "EOF"));
                _ = Call(rs, "Close");
                Release(rs);

                Assert.Null(Put(px, "Direction", 2).Failure);
                Assert.Equal(2, Get(px, "Direction"));
                AssertError(Call(cmd, "Execute"), HResultErrorsOccurred, "Microsoft Access Database Engine", expectedHelpContext: 5000000u, expectedInfoGuid: IidOleDbAccessor);

                Assert.Null(Put(px, "Direction", 1).Failure);
                Assert.Null(Put(px, "Value", null).Failure); // VT_EMPTY
                Assert.Null(Get(px, "Value"));
                AssertError(Call(cmd, "Execute"), HResultParamNotOptional, "Microsoft Access Database Engine", "Parameter ?_1 has no default value.", 5003747u, IidOleDbCommand);

                Assert.Null(Put(px, "Attributes", 64).Failure);
                Assert.Equal(64, Get(px, "Attributes"));
                Assert.Null(Put(px, "Attributes", 16).Failure);
                Assert.Equal(16, Get(px, "Attributes"));
            }
            finally
            {
                Release(prs);
                Release(px);
            }
        }
        finally
        {
            Release(cmd);
            _ = Call(cn, "Close");
            Release(cn);
        }
    }

    /// <summary>r-ado-1b/param-refresh-after-text-change #18-#52: Refresh derivation, re-derivation on text change and after Execute, and Prepared=True QByL error.</summary>
    private static void VerifyParamRefreshAfterTextChange(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath, mode: 1);
        nint cmd = New(OwnCommand);
        try
        {
            Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
            Assert.Null(Put(cmd, "CommandText", "SELECT ID FROM T WHERE L = ?").Failure);
            Assert.Null(Put(cmd, "CommandType", 1).Failure);
            nint prs = (nint)Get(cmd, "Parameters")!;
            try
            {
                Assert.Null(Call(prs, "Refresh").Failure);
                Assert.Equal(1, Get(prs, "Count"));
                nint p0 = (nint)Get(prs, "Item", 0)!;
                Assert.Equal("?", Get(p0, "Name"));
                Assert.Equal(202, Get(p0, "Type"));
                Assert.Equal(1, Get(p0, "Direction"));
                Assert.Equal(510, Get(p0, "Size"));
                Assert.Equal((byte)255, Get(p0, "Precision"));
                Assert.Equal((byte)255, Get(p0, "NumericScale"));
                Assert.Equal(64, Get(p0, "Attributes"));
                Release(p0);

                Assert.Null(Put(cmd, "CommandText", "SELECT ID FROM T WHERE L = ? AND S = ?").Failure);
                Assert.Equal(2, Get(prs, "Count"));
                AssertError(Call(cmd, "Execute"), HResultParamNotOptional, "Microsoft Access Database Engine", "Parameter ?_1 has no default value.", 5003747u, IidOleDbCommand);
                Assert.Null(Call(prs, "Refresh").Failure);
                Assert.Equal(2, Get(prs, "Count"));

                Assert.Null(Put(cmd, "CommandType", 4).Failure);
                Assert.Null(Put(cmd, "CommandText", "QByL").Failure);
                Assert.Equal(0, Get(prs, "Count"));
                Assert.Null(Call(prs, "Refresh").Failure);
                Assert.Equal(0, Get(prs, "Count"));
                Assert.Null(Put(cmd, "Prepared", true).Failure);
                AssertError(Call(cmd, "Execute"), HResultParamNotOptional, "Microsoft Access Database Engine", "Parameter pL has no default value.", 5003747u, IidOleDbCommand);
            }
            finally
            {
                Release(prs);
            }
        }
        finally
        {
            Release(cmd);
            _ = Call(cn, "Close");
            Release(cn);
        }
    }

    /// <summary>r-ado-1b/param-value-tag-matrix #196-#208 &amp; r-fix-wire-ado/ado-command-parameters-fresh #23/#47/#71/#95.</summary>
    private static void VerifyParamValueEmptyAndOverflow(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        nint cmd = New(OwnCommand);
        try
        {
            Assert.Null(Put(cmd, "ActiveConnection", new Dispatch(cn)).Failure);
            nint pSmall = (nint)Call(cmd, "CreateParameter", "pV", 2, 1).Value!;
            nint pLong = (nint)Call(cmd, "CreateParameter", "pL", 3, 1).Value!;
            try
            {
                AssertError(Put(pSmall, "Value", 32768), HResultOutOfRange, "Provider", "Out of present range.", 1240640u, IidParameter);
                Assert.Null(Get(pSmall, "Value"));
                AssertError(Put(pSmall, "Value", -32769), HResultOutOfRange, "Provider", "Out of present range.", 1240640u, IidParameter);
                Assert.Null(Get(pSmall, "Value"));

                AssertError(Put(pLong, "Value", 2147483648.0), HResultOutOfRange, "Provider", "Out of present range.", 1240640u, IidParameter);
                Assert.Null(Get(pLong, "Value"));
                AssertError(Put(pLong, "Value", -2147483649.0), HResultOutOfRange, "Provider", "Out of present range.", 1240640u, IidParameter);
                Assert.Null(Get(pLong, "Value"));

                Assert.Null(Put(pLong, "Value", 20).Failure);
                Assert.Equal(20, Get(pLong, "Value"));
                Assert.Null(Put(pLong, "Value", null).Failure); // VT_EMPTY
                Assert.Null(Get(pLong, "Value"));
                AssertError(Put(pLong, "Value", new byte[] { 0x14 }), HResultWrongValueType, "ADODB.Parameter");
                Assert.Null(Get(pLong, "Value"));
            }
            finally
            {
                Release(pLong);
                Release(pSmall);
            }
        }
        finally
        {
            Release(cmd);
            _ = Call(cn, "Close");
            Release(cn);
        }
    }

    /// <summary>r-fix-wire-ado/ado-params-manual-append, ado-params-refresh-only, ado-params-saved-query.</summary>
    private static void VerifyAdo5ManualAppendRefreshOnlyAndSavedQuery(string dbPath)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            // ado-params-manual-append: Count=2 -> Append x -> Count=3 -> Append y -> Count=4 -> Execute 0x80040E10 ?_1.
            nint cmd1 = New(OwnCommand);
            try
            {
                Assert.Null(Put(cmd1, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmd1, "CommandText", "SELECT ID, Code FROM K WHERE Code = ? OR Code = ?").Failure);
                Assert.Null(Put(cmd1, "CommandType", 1).Failure);
                nint pp = (nint)Get(cmd1, "Parameters")!;
                nint px = (nint)Call(cmd1, "CreateParameter", "x", 202, 1, 10, "a2").Value!;
                nint py = (nint)Call(cmd1, "CreateParameter", "y", 202, 1, 10, "b3").Value!;
                try
                {
                    Assert.Equal(2, Get(pp, "Count"));
                    Assert.Null(Call(pp, "Append", new Dispatch(px)).Failure);
                    Assert.Equal(3, Get(pp, "Count"));
                    Assert.Null(Call(pp, "Append", new Dispatch(py)).Failure);
                    Assert.Equal(4, Get(pp, "Count"));
                    AssertError(Call(cmd1, "Execute"), HResultParamNotOptional, "Microsoft Access Database Engine", "Parameter ?_1 has no default value.", 5003747u, IidOleDbCommand);
                }
                finally
                {
                    Release(py);
                    Release(px);
                    Release(pp);
                }
            }
            finally
            {
                Release(cmd1);
            }

            // ado-params-refresh-only: Count=2 -> Refresh -> Count=0 -> Execute "No value given for one or more required parameters."
            nint cmd2 = New(OwnCommand);
            try
            {
                Assert.Null(Put(cmd2, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmd2, "CommandText", "SELECT ID, Code FROM K WHERE Code = ? OR Code = ?").Failure);
                Assert.Null(Put(cmd2, "CommandType", 1).Failure);
                nint pp = (nint)Get(cmd2, "Parameters")!;
                try
                {
                    Assert.Equal(2, Get(pp, "Count"));
                    Assert.Null(Call(pp, "Refresh").Failure);
                    Assert.Equal(0, Get(pp, "Count"));
                    AssertError(Invoke(pp, "Item", 2, 0), HResultItemNotFound, "ADODB.Parameters");
                    AssertError(Call(cmd2, "Execute"), HResultParamNotOptional, "Microsoft Access Database Engine", "No value given for one or more required parameters.", 5000000u, IidOleDbCommand);
                }
                finally
                {
                    Release(pp);
                }
            }
            finally
            {
                Release(cmd2);
            }

            // ado-params-saved-query: QParam adCmdStoredProc -> Count=0, Refresh -> Count=0, Execute 3061 "Too few parameters. Expected 1."
            nint cmd3 = New(OwnCommand);
            try
            {
                Assert.Null(Put(cmd3, "ActiveConnection", new Dispatch(cn)).Failure);
                Assert.Null(Put(cmd3, "CommandText", "QParam").Failure);
                Assert.Null(Put(cmd3, "CommandType", 4).Failure);
                nint pq = (nint)Get(cmd3, "Parameters")!;
                try
                {
                    Assert.Equal(0, Get(pq, "Count"));
                    Assert.Null(Call(pq, "Refresh").Failure);
                    Assert.Equal(0, Get(pq, "Count"));
                    AssertError(Call(cmd3, "Execute"), HResultParamNotOptional, "Microsoft Access Database Engine", "Too few parameters. Expected 1.", 5003061u, IidOleDbCommand);
                }
                finally
                {
                    Release(pq);
                }
            }
            finally
            {
                Release(cmd3);
            }
        }
        finally
        {
            _ = Call(cn, "Close");
            Release(cn);
        }
    }

    [DllImport("oleaut32.dll")]
    private static extern int SetErrorInfo(uint reserved, nint errorInfo);

    [DllImport("oleaut32.dll")]
    private static extern int GetErrorInfo(uint reserved, out nint errorInfo);
}
