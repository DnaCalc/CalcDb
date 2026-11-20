using CalcDb.Values;

namespace CalcDb.Com.Com;

/// <summary>
/// The one source of CalcDb's session LCID on Windows (qualified.md R-EXPR-1 EXPR-LOCALE, task 5zh9; II-10 §14):
/// the user default locale read once per process through <see cref="OleAut.GetUserDefaultLCID"/>. ACE converts text
/// under the user default locale with the user's own settings; the calling thread's culture/locale and
/// <c>IDispatch::Invoke</c>'s LCID are never consulted.
/// </summary>
internal static class SessionLocale
{
    /// <summary>The process session LCID (non-zero on Windows, e.g. 7177 for en-ZA).</summary>
    public static uint Lcid { get; } = Read();

    private static uint Read()
    {
        uint lcid = OleAut.GetUserDefaultLCID();
        return lcid != 0 ? lcid : throw new PlatformNotSupportedException("GetUserDefaultLCID is not available on this platform.");
    }
}
