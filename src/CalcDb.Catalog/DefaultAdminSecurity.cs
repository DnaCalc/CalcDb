using CalcDb.Format;

namespace CalcDb.Catalog;

/// <summary>
/// The ownership and access rows of the default Admin session (T-CAT-21; RQ scope: user-level security is excluded,
/// the default Admin with a blank password is supported). A 2-byte built-in SID is stored XOR-masked with the file's
/// RC4 SID keystream (<see cref="DatabaseHeaderEncoder.DeriveSidKeystream"/>). The SIDs are those of the four retained
/// empty ACCDBs ACE created on different dates (NewDatabaseSynthesizerTests); what they grant is stored, never evaluated.
/// </summary>
public static class DefaultAdminSecurity
{
    public static ReadOnlySpan<byte> Engine => [0x02, 0x03];

    public static ReadOnlySpan<byte> Admin => [0x03, 0x01];

    public static ReadOnlySpan<byte> Users => [0x02, 0x01];

    public static ReadOnlySpan<byte> Admins => [0x02, 0x04];

    /// <summary>
    /// The MSysACEs rows of a table the default Admin creates, in slot order: Admin with 0x000F00FE, then Users with
    /// 0x000FFEFF, neither inheritable (evidence/r-q11-ddl-closure/create-one-long, page 41 slots 36 and 37; the same two
    /// rows on the DAO route, append-dao). They equal the Tables container's two inheritable grants with Admins replaced
    /// by the creating user; whether that is ACE's rule (another user, another container) is not recorded.
    /// </summary>
    public static IReadOnlyList<(byte[] Sid, int Acm)> NewTableAces { get; } = [([0x03, 0x01], 0x000F00FE), ([0x02, 0x01], 0x000FFEFF)];

    /// <summary>
    /// The MSysACEs rows of a relationship (MSysObjects Type 8, ParentId 0x0F000003) the default Admin creates, in slot
    /// order: Admin with 0x000F00FE, then Users with 0x000FFFFF, neither inheritable
    /// (evidence/r-q34-ddl-sweep/fk-inline-accdb-dao page 41 slots 40 and 41; fk-inline-mdb-dao page 18 slots 20 and 21).
    /// Matches the Relationships container's two inheritable grants with Admins replaced by Admin.
    /// </summary>
    public static IReadOnlyList<(byte[] Sid, int Acm)> NewRelationshipAces { get; } = [([0x03, 0x01], 0x000F00FE), ([0x02, 0x01], 0x000FFFFF)];

    /// <summary>The stored form of a built-in SID under the file's keystream.</summary>
    public static byte[] Mask(ReadOnlySpan<byte> plaintextSid, ReadOnlySpan<byte> keystream) =>
        [(byte)(plaintextSid[0] ^ keystream[0]), (byte)(plaintextSid[1] ^ keystream[1])];

    /// <summary>The file's SID keystream from its stored page 0.</summary>
    public static byte[] Keystream(ReadOnlySpan<byte> storedPage0, FormatDescriptor d)
    {
        byte[] keystream = new byte[2];
        DatabaseHeaderEncoder.DeriveSidKeystream(HeaderMask.Decode(storedPage0, d), keystream);
        return keystream;
    }
}
