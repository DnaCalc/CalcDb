using CalcDb.Com.Com;
using CalcDb.Com.Declarations;

namespace CalcDb.Com.Tests;

/// <summary>CalcDb's in-memory type library builds from the generated declarations (II-13 §11), in-process.</summary>
public sealed unsafe class TypeLibraryTests
{
    [Fact]
    public void Every_dual_interface_has_its_dispatch_view()
    {
        foreach (InterfaceDecl i in DaoTypes.Library.Interfaces)
        {
            nint ti = 0;
            int hr = TypeLibrary.TypeInfoOf(i, &ti);
            Assert.True(hr == 0, $"{i.Name}: 0x{hr:X8} {TypeLibrary.BuildFailureStep}");
            ((delegate* unmanaged<nint, uint>)(*(nint**)ti)[2])(ti);
        }
    }
}