using System.Runtime.InteropServices;
using CalcDb.Foundation.Windows;

namespace CalcDb.Com.Com;

/// <summary>Which kind of failure an operation produced; the two adapters publish them through their own channels.</summary>
internal enum FailureKind
{
    /// <summary>An ACE-equivalent DAO error: its number, source and text as the oracle gives them.</summary>
    Dao,

    /// <summary>A declared member CalcDb has not implemented yet: a named gap, counted as a failure, never a native match.</summary>
    NotImplemented,

    /// <summary>A CalcDb defect caught at the boundary (an exception that must not cross a native entry, II-13 §3).</summary>
    Defect,
}

/// <summary>An operation's failure, independent of the caller's representation (II-12 owned error).</summary>
internal sealed record Failure(FailureKind Kind, int Scode, string Source, string Description, string? HelpFile = null, int HelpContext = 0)
{
    /// <summary>A DAO error as ACE publishes it: scode 0x800A0000 | number, help file jeterr40.chm, context 5000000 + number.</summary>
    public static Failure Dao(int number, string source, string description) =>
        new(FailureKind.Dao, unchecked((int)0x800A0000) | number, source, description, "jeterr40.chm", 5000000 + number);

    public static Failure NotImplemented(string member) =>
        new(FailureKind.NotImplemented, HResults.E_NOTIMPL, "CalcDb", $"CalcDb has not implemented {member} yet.");

    public static Failure Defect(Exception e) =>
        new(FailureKind.Defect, HResults.E_UNEXPECTED, "CalcDb defect", e.GetType().Name + ": " + e.Message);
}

/// <summary>EXCEPINFO, Windows x64 layout (64 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ExcepInfo
{
    public ushort Code;
    public ushort Reserved;
    public nint Source;
    public nint Description;
    public nint HelpFile;
    public uint HelpContext;
    public nint ReservedPointer;
    public nint DeferredFillIn;
    public int Scode;
}

/// <summary>
/// The channels a failure is published through (II-13 §13). Dispatch fills EXCEPINFO and returns DISP_E_EXCEPTION and,
/// as ACE's DAO does through IDispatch, sets no IErrorInfo (R-ACT-1 ACT-THREAD records: GetErrorInfo S_FALSE after a
/// failed OpenDatabase). The vtable channel returns the scode and sets IErrorInfo; what ACE does there is not yet
/// observed (a native vtable caller is open in ACT-ABI).
/// </summary>
internal static unsafe partial class ErrorPublication
{
    public static int ToExcepInfo(Failure failure, ExcepInfo* info)
    {
        if (info is not null)
        {
            *info = default;
            info->Source = Bstr(failure.Source);
            info->Description = Bstr(failure.Description);
            info->HelpFile = failure.HelpFile is null ? 0 : Bstr(failure.HelpFile);
            info->HelpContext = (uint)failure.HelpContext;
            info->Scode = failure.Scode;
        }
        return HResults.DISP_E_EXCEPTION;
    }

    public static int ToErrorInfo(Failure failure, in Guid iid)
    {
        if (CreateErrorInfo(out nint create) >= 0 && create != 0)
        {
            nint** vt = (nint**)create;
            Guid g = iid;
            ((delegate* unmanaged<nint, Guid*, int>)(*vt)[3])(create, &g);
            fixed (char* s = failure.Source)
            {
                ((delegate* unmanaged<nint, char*, int>)(*vt)[4])(create, s);
            }
            fixed (char* d = failure.Description)
            {
                ((delegate* unmanaged<nint, char*, int>)(*vt)[5])(create, d);
            }
            if (failure.HelpFile is { } file)
            {
                fixed (char* h = file)
                {
                    ((delegate* unmanaged<nint, char*, int>)(*vt)[6])(create, h);
                }
            }
            ((delegate* unmanaged<nint, uint, int>)(*vt)[7])(create, (uint)failure.HelpContext);
            Guid errorInfo = ComIids.IErrorInfo;
            nint info = 0;
            if (((delegate* unmanaged<nint, Guid*, nint*, int>)(*vt)[0])(create, &errorInfo, &info) >= 0 && info != 0)
            {
                // A failed SetErrorInfo leaves no IErrorInfo; the failure's own HRESULT is still returned below.
                _ = SetErrorInfo(0, info);
                OleAutRelease(info);
            }
            OleAutRelease(create);
        }
        return failure.Scode;
    }

    private static nint Bstr(string text) => OwnedVariant.FromText(text).TransferBstr();

    private static void OleAutRelease(nint unknown) => ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[2])(unknown);

    [LibraryImport("oleaut32.dll")]
    private static partial int CreateErrorInfo(out nint createErrorInfo);

    [LibraryImport("oleaut32.dll")]
    private static partial int SetErrorInfo(uint reserved, nint errorInfo);
}
