namespace CalcDb.Com.Host;

/// <summary>
/// An immutable host binding (II-13 §23, D-HOST-1 provisional): the identity a factory captures when issued and an
/// object retains when created. Standalone is the explicit own-class mode (T-INT-49), never a fallback.
/// </summary>
public sealed record HostBinding(ulong Identity, bool Standalone, ulong HostIncarnation, ushort Major, ushort Minor, uint TargetAbi, ulong Capabilities);
