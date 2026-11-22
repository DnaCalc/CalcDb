using CalcDb.Com.Com;
using CalcDb.Com.Declarations;

namespace CalcDb.Com;

/// <summary>
/// Ensures both the DAO and ADO generated declaration libraries are registered with <see cref="DeclarationIndex"/>
/// in deterministic order (DAO, then ADO) before type-library export or registration iterates them.
/// </summary>
internal static class ComLibraries
{
    internal static void EnsureRegistered()
    {
        DeclarationIndex.Register(DaoTypes.Library);
        DeclarationIndex.Register(AdoTypes.Library);
    }
}
