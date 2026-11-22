using System.Runtime.InteropServices;
using CalcDb.Com.Com;

namespace CalcDb.Com.Host;

/// <summary>Where the process host anchor is (II-13 §23).</summary>
internal enum HostLifecycle
{
    /// <summary>No host has bootstrapped: compatibility creation is refused.</summary>
    Absent,

    Active,

    /// <summary>The host began shutdown: no new compatibility creation; issued objects keep their binding.</summary>
    Stopping,
}

/// <summary>
/// The bootstrap offer, contract 1.0 (I-HOST-1 HOST-ABI): fixed-width fields only. The 16-byte header (size, version,
/// target ABI) is validated before anything after it is read; a later minor version may be longer, and only this
/// prefix is read.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BootstrapOffer
{
    public uint Size;
    public ushort Major;
    public ushort Minor;
    public uint TargetAbi;
    public uint Reserved;
    public ulong HostIncarnation;
    public ulong OfferedCapabilities;
    public ulong RequiredCapabilities;
}

/// <summary>The bootstrap result; CalcDb writes only the fields that fit the caller's declared size.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BootstrapResult
{
    public uint Size;
    public ushort Major;
    public ushort Minor;
    public ulong BindingIdentity;
    public ulong GrantedCapabilities;
}

/// <summary>Named bootstrap outcomes (FACILITY_ITF, CalcDb's own range).</summary>
internal static class HostResults
{
    public const int HeaderTooSmall = unchecked((int)0x80040A01);
    public const int MajorMismatch = unchecked((int)0x80040A02);
    public const int TargetAbiMismatch = unchecked((int)0x80040A03);
    public const int TailMissing = unchecked((int)0x80040A04);
    public const int MissingCapability = unchecked((int)0x80040A05);
    public const int AlreadyBound = unchecked((int)0x80040A06);
    public const int HostStopping = unchecked((int)0x80040A07);
    public const int NotTheActiveBinding = unchecked((int)0x80040A08);
}

/// <summary>
/// The process host anchor (I-HOST-1 HOST-BOOT): the host loads and pins the module, bootstraps outside DllMain, and
/// only then are compatibility identities served. The binding is built completely before it is published; nothing
/// foreign runs under the gate.
/// </summary>
internal static unsafe class HostAnchor
{
    public const ushort ContractMajor = 1;
    public const ushort ContractMinor = 0;

    /// <summary>The Windows x64 target (W-02/target-declarations, TargetDeclarations.WindowsX64).</summary>
    public const uint WindowsX64 = 1;

    /// <summary>Capabilities this build can honour when a host requires them: none yet.</summary>
    public const ulong KnownCapabilities = 0;

    private const int HeaderBytes = 16;
    private static readonly Lock Gate = new();
    private static ulong _nextIdentity = 1;
    private static HostBinding? _active;
    private static HostBinding? _standalone;

    public static HostLifecycle State { get; private set; } = HostLifecycle.Absent;

    /// <summary>The Active binding, or null when the anchor is Absent or Stopping.</summary>
    public static HostBinding? Active
    {
        get
        {
            lock (Gate)
            {
                return State == HostLifecycle.Active ? _active : null;
            }
        }
    }

    /// <summary>The explicit standalone binding of CalcDb's own class identities, with default configuration.</summary>
    public static HostBinding Standalone
    {
        get
        {
            lock (Gate)
            {
                return _standalone ??= new HostBinding(_nextIdentity++, true, 0, ContractMajor, ContractMinor, WindowsX64, 0);
            }
        }
    }

    public static int Bootstrap(BootstrapOffer* offer, BootstrapResult* result)
    {
        if (offer is null || result is null)
        {
            return HResults.E_POINTER;
        }
        uint size = offer->Size;
        if (size < HeaderBytes || result->Size < 8)
        {
            return HostResults.HeaderTooSmall;
        }
        if (offer->Major != ContractMajor)
        {
            return HostResults.MajorMismatch;
        }
        if (offer->TargetAbi != WindowsX64)
        {
            return HostResults.TargetAbiMismatch;
        }
        if (size < (uint)sizeof(BootstrapOffer))
        {
            return HostResults.TailMissing;
        }
        BootstrapOffer prefix = *offer; // exactly the 1.0 prefix, whatever a newer minor appends
        if ((prefix.RequiredCapabilities & ~KnownCapabilities) != 0)
        {
            return HostResults.MissingCapability;
        }
        HostBinding binding;
        lock (Gate)
        {
            if (State == HostLifecycle.Active)
            {
                return HostResults.AlreadyBound;
            }
            if (State == HostLifecycle.Stopping)
            {
                return HostResults.HostStopping;
            }
            binding = new HostBinding(_nextIdentity++, false, prefix.HostIncarnation, prefix.Major, Math.Min(prefix.Minor, ContractMinor), WindowsX64, prefix.OfferedCapabilities & KnownCapabilities);
            _active = binding;
            State = HostLifecycle.Active;
        }
        BootstrapResult answer = new() { Size = result->Size, Major = ContractMajor, Minor = ContractMinor, BindingIdentity = binding.Identity, GrantedCapabilities = binding.Capabilities };
        uint writable = Math.Min(result->Size, (uint)sizeof(BootstrapResult));
        Buffer.MemoryCopy(&answer, result, writable, writable);
        return HResults.S_OK;
    }

    /// <summary>Begin shutdown: new compatibility creation stops; issued factories and objects keep their captured binding.</summary>
    public static int Shutdown(ulong bindingIdentity)
    {
        lock (Gate)
        {
            if (State != HostLifecycle.Active || _active?.Identity != bindingIdentity)
            {
                return HostResults.NotTheActiveBinding;
            }
            State = HostLifecycle.Stopping;
            return HResults.S_OK;
        }
    }
}