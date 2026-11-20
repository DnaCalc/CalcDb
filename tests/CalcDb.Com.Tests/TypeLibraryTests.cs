using System.Runtime.CompilerServices;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;

namespace CalcDb.Com.Tests;

/// <summary>
/// CalcDb's type libraries (II-13 §§11, 16; R-ACT-1 ACT-META, ACT-DECL): built from the generated declarations, saved as
/// SYS_WIN32 images and loaded from them, in-process. The images report ACE's attributes (SYS_WIN32, FHASDISKIMAGE;
/// r-str-ado-act act-dao120, ado-describe) and, as loaded, every captured x64 vtable offset and size.
/// </summary>
public sealed unsafe class TypeLibraryTests
{
    /// <summary>The in-process host's image directory: the images are saved there once, before any test uses them.</summary>
    internal static readonly string Images = Path.Combine(Path.GetTempPath(), "calcdb-tlb-" + Environment.ProcessId);

    /// <summary>What saving the images answered; a failure fails the tests that need them, not the test run.</summary>
    private static string _saved = "not run";

    [ModuleInitializer]
    internal static void SaveImages()
    {
        Directory.CreateDirectory(Images);
        TypeLibrary.ImageDirectory = Images;
        int hr = TypeLibrary.SaveImages(Images);
        _saved = hr < 0 ? $"0x{hr:X8} {TypeLibrary.BuildFailureStep}" : "S_OK";
    }

    private static nint* Vt(nint unknown) => *(nint**)unknown;

    private static void Release(nint unknown) => ((delegate* unmanaged<nint, uint>)Vt(unknown)[2])(unknown);

    [Fact]
    public void Both_images_are_saved() => Assert.Equal("S_OK", _saved);

    [Fact]
    public void Every_dual_interface_of_both_libraries_has_its_dispatch_view()
    {
        foreach (InterfaceDecl i in DeclarationIndex.Libraries.SelectMany(l => l.Interfaces))
        {
            nint ti = 0;
            int hr = TypeLibrary.TypeInfoOf(i, &ti);
            Assert.True(hr == 0, $"{i.Name}: 0x{hr:X8} {TypeLibrary.BuildFailureStep}");
            Release(ti);
        }
    }

    /// <summary>ACE's attributes: DAO SYS_WIN32, ADO SYS_WIN64, both FHASDISKIMAGE; help strings; the captured type order.</summary>
    [Fact]
    public void Both_libraries_report_the_captured_attributes_help_strings_and_type_order()
    {
        Assert.Equal((1, 8), (DaoTypes.Library.Image.SysKind, DaoTypes.Library.Image.Flags));
        Assert.Equal((3, 8), (AdoTypes.Library.Image.SysKind, AdoTypes.Library.Image.Flags));
        foreach (InterfaceDecl first in DeclarationIndex.Libraries.Select(l => l.Interfaces[0]))
        {
            nint lib = Library(first);
            nint attr = 0;
            Assert.Equal(0, ((delegate* unmanaged<nint, nint*, int>)Vt(lib)[7])(lib, &attr)); // GetLibAttr
            LibraryDecl decl = DeclarationIndex.LibraryOf(first);
            Assert.Equal(decl.Guid, *(Guid*)attr);
            Assert.Equal((decl.Image.SysKind, decl.Image.Flags), (*(int*)(attr + 20), (int)*(ushort*)(attr + 28)));
            Assert.Equal((decl.Major, decl.Minor), (*(ushort*)(attr + 24), *(ushort*)(attr + 26)));
            ((delegate* unmanaged<nint, nint, void>)Vt(lib)[12])(lib, attr);

            Assert.Equal((decl.Name, decl.Image.HelpString), Documentation(lib, -1));
            uint count = ((delegate* unmanaged<nint, uint>)Vt(lib)[3])(lib); // GetTypeInfoCount
            var names = new List<string>();
            for (int k = 0; k < count; k++)
            {
                (string? name, string? help) = Documentation(lib, k);
                names.Add(name ?? "(none)");
                Assert.Equal(decl.Image.TypeHelp.Where(h => h.Type == name).Select(h => h.HelpString).SingleOrDefault(), help);
            }
            Assert.Equal(decl.Image.TypeOrder, names);
            Release(lib);
        }
    }

    [Fact]
    public void DAOs_language_constants_are_a_module_of_string_constants()
    {
        nint lib = Library(DaoTypes.DBEngine);
        int index = DaoTypes.Library.Image.TypeOrder.ToList().IndexOf("LanguageConstants");
        nint ti = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, uint, nint*, int>)Vt(lib)[4])(lib, (uint)index, &ti)); // GetTypeInfo
        nint ta = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, nint*, int>)Vt(ti)[3])(ti, &ta));
        Assert.Equal((2, DaoTypes.LanguageConstantsModule.Constants.Count), (*(int*)(ta + 44), (int)*(ushort*)(ta + 50))); // TKIND_MODULE, cVars
        ((delegate* unmanaged<nint, nint, void>)Vt(ti)[19])(ti, ta);
        nint vd = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, uint, nint*, int>)Vt(ti)[6])(ti, 3, &vd)); // GetVarDesc: dbLangGeneral
        nint value = *(nint*)(vd + 16); // lpvarValue
        Assert.Equal((1073741827, 2, (ushort)30, (ushort)8), (*(int*)vd, *(int*)(vd + 60),*(ushort*)(vd + 32), *(ushort*)value));
        Assert.Equal(";LANGID=0x0409;CP=1252;COUNTRY=0", System.Runtime.InteropServices.Marshal.PtrToStringBSTR(*(nint*)(value + 8)));
        ((delegate* unmanaged<nint, nint, void>)Vt(ti)[21])(ti, vd);
        Release(ti);
        Release(lib);
    }

    private static (string? Name, string? Help) Documentation(nint lib, int index)
    {
        nint name = 0, help = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, int, nint*, nint*, uint*, nint*, int>)Vt(lib)[9])(lib, index, &name, &help, null, null));
        string? n = name == 0 ? null : System.Runtime.InteropServices.Marshal.PtrToStringBSTR(name);
        string? h = help == 0 ? null : System.Runtime.InteropServices.Marshal.PtrToStringBSTR(help);
        System.Runtime.InteropServices.Marshal.FreeBSTR(name);
        System.Runtime.InteropServices.Marshal.FreeBSTR(help);
        return (n, h);
    }

    /// <summary>
    /// sysKind does not move CalcDb's declared vtables: as loaded, every dispatch member and every vtable entry has its
    /// captured x64 byte offset and every vtable view its captured size (the image stores them in SYS_WIN32 units).
    /// </summary>
    [Fact]
    public void The_loaded_image_has_every_captured_x64_offset_and_vtable_size()
    {
        foreach (InterfaceDecl i in DeclarationIndex.Libraries.SelectMany(l => l.Interfaces))
        {
            nint ti = 0;
            Assert.Equal(0, TypeLibrary.TypeInfoOf(i, &ti));
            (int kind, int functions, int vtableBytes, int flags) = Attributes(ti);
            Assert.Equal((4, 56, i.DispatchFlags), (kind, vtableBytes, flags));
            var dispatch = new List<(int, int, int)>();
            for (int k = 0; k < functions; k++)
            {
                (int memberId, int invokeKind, int offset, _) = Function(ti, k);
                dispatch.Add((memberId, invokeKind, offset));
            }
            foreach (MemberDecl m in i.Members)
            {
                Assert.Contains((m.MemberId, (int)m.Kind, m.VtableOffset), dispatch);
            }

            uint href = 0;
            nint vtable = 0;
            Assert.Equal(0, ((delegate* unmanaged<nint, int, uint*, int>)Vt(ti)[8])(ti, -1, &href)); // GetRefTypeOfImplType(-1)
            Assert.Equal(0, ((delegate* unmanaged<nint, uint, nint*, int>)Vt(ti)[14])(ti, href, &vtable));
            (kind, functions, vtableBytes, flags) = Attributes(vtable);
            Assert.Equal((3, i.VtableBytes, i.Entries.Count, i.VtableFlags), (kind, vtableBytes, functions, flags));
            for (int k = 0; k < functions; k++)
            {
                VtableEntryDecl e = i.Entries[k];
                Assert.Equal((e.MemberId, (int)e.Kind, e.VtableOffset, e.OptionalCount), Function(vtable, k));
            }
            Release(vtable);
            Release(ti);
        }
    }

    private static nint Library(InterfaceDecl i)
    {
        nint ti = 0;
        Assert.Equal(0, TypeLibrary.TypeInfoOf(i, &ti));
        nint lib = 0;
        uint index = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, nint*, uint*, int>)Vt(ti)[18])(ti, &lib, &index)); // GetContainingTypeLib
        Release(ti);
        return lib;
    }

    private static (int Kind, int Functions, int VtableBytes, int Flags) Attributes(nint ti)
    {
        nint ta = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, nint*, int>)Vt(ti)[3])(ti, &ta)); // GetTypeAttr
        (int, int, int, int) result = (*(int*)(ta + 44), *(ushort*)(ta + 48), *(ushort*)(ta + 54), *(ushort*)(ta + 58)); // typekind, cFuncs, cbSizeVft, wTypeFlags
        ((delegate* unmanaged<nint, nint, void>)Vt(ti)[19])(ti, ta);
        return result;
    }

    private static (int MemberId, int InvokeKind, int VtableOffset, int OptionalCount) Function(nint ti, int index)
    {
        nint fd = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, uint, nint*, int>)Vt(ti)[5])(ti, (uint)index, &fd)); // GetFuncDesc
        (int, int, int, int) result = (*(int*)fd, *(int*)(fd + 28), *(short*)(fd + 40), *(short*)(fd + 38)); // memid, invkind, oVft, cParamsOpt
        ((delegate* unmanaged<nint, nint, void>)Vt(ti)[20])(ti, fd);
        return result;
    }
}
