using System.Text;

namespace CalcDb.Format.Tests;

/// <summary>
/// The description against files ACE itself created (R-FMT-1 native fixtures). The R-ORACLE-1 fixture was made by
/// <c>CREATE TABLE T1/T0 (ID LONG PRIMARY KEY, N LONG, S TEXT(50), B YESNO)</c> and two inserts into T1:
/// (1, 10, 'alpha', True) and (2, -20, 'Bä€', False) (evidence/r-oracle-1/create-fixture-t1); DAO read those values
/// back (table-read-t1). Decoding the raw bytes must agree.
/// </summary>
public sealed class NativeFixtureTests
{
    private const string T1Fixture = "486e67a3af61f4fedf267d3345b50d0b3dc6e10db7dea76bc93a2aad8fc57b32";

    internal static string Evidence()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "CalcDb.slnx")))
            {
                return Path.Combine(dir, "evidence");
            }
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private static byte[] Fixture(string sha) => File.ReadAllBytes(Path.Combine(Evidence(), "artifacts", sha));

    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    [Fact]
    public void The_ACE_fixture_selects_an_ACE_profile_and_whole_pages()
    {
        InspectionReport r = Inspector.Inspect(Fixture(T1Fixture));
        Assert.Equal(ProfileRefusal.None, r.Refusal);
        Assert.True(r.Profile!.IsAce);
        Assert.Equal(0, r.TrailingBytes);
        Assert.Equal(200704 / 4096, r.PageCount);
        Assert.Empty(r.Problems);
    }

    [Fact]
    public void The_two_user_TableDefs_have_the_created_columns_and_types()
    {
        InspectionReport r = Inspector.Inspect(Fixture(T1Fixture));
        List<TableDefinition> user = [.. r.TableDefs.Where(t => t.Columns.Select(c => c.Name).SequenceEqual(["ID", "N", "S", "B"]))];
        Assert.Equal(2, user.Count);
        foreach (TableDefinition t in user)
        {
            Assert.Equal([NativeTypes.Long, NativeTypes.Long, NativeTypes.Text, NativeTypes.Boolean], t.Columns.Select(c => c.TypeCode));
            Assert.True(t.Columns[0].IsFixed);
            Assert.True(t.Columns[1].IsFixed);
            Assert.False(t.Columns[2].IsFixed);
            Assert.Equal(100, t.Columns[2].Length); // TEXT(50): 50 characters, 100 bytes
            Assert.Equal([0, 1, 2, 3], t.Columns.Select(c => c.PhysicalNumber));
        }
        Assert.Equal([0u, 2u], user.Select(t => t.RowCount).Order());
    }

    [Fact]
    public void The_rows_of_T1_decode_to_the_inserted_values()
    {
        byte[] file = Fixture(T1Fixture);
        InspectionReport r = Inspector.Inspect(file);
        TableDefinition t1 = r.TableDefs.Single(t => t.Columns.Select(c => c.Name).SequenceEqual(["ID", "N", "S", "B"]) && t.RowCount == 2);
        var rows = new List<(int Id, int N, bool B, byte[] S)>();
        foreach ((uint page, SlotView slot) in Inspector.RowsOf(r, t1.FirstPage).Where(x => !x.Slot.Deleted))
        {
            ReadOnlySpan<byte> body = file.AsSpan((int)(page * 4096) + slot.Start, slot.Length);
            Assert.True(RowEnvelope.TryOpen(body, D, out RowEnvelope row, out string? failure), failure);
            int fixedEnd = RowEnvelope.KnownFixedEnd(t1.Columns, row.ColumnCount, D);
            FieldRead id = row.ReadField(t1.Columns[0], fixedEnd);
            FieldRead n = row.ReadField(t1.Columns[1], fixedEnd);
            FieldRead s = row.ReadField(t1.Columns[2], fixedEnd);
            FieldRead b = row.ReadField(t1.Columns[3], fixedEnd);
            Assert.Equal(FieldOutcome.Bytes, id.Outcome);
            Assert.Equal(FieldOutcome.Bytes, s.Outcome);
            Assert.Equal(FieldOutcome.Boolean, b.Outcome);
            rows.Add((BitConverter.ToInt32(body[id.Start..id.End]), BitConverter.ToInt32(body[n.Start..n.End]), b.Value, body[s.Start..s.End].ToArray()));
        }
        rows.Sort((x, y) => x.Id.CompareTo(y.Id));
        Assert.Equal([(1, 10, true), (2, -20, false)], rows.Select(x => (x.Id, x.N, x.B)));
        // Text is a byte grammar (II-04 §6): 'alpha' may be stored compressed (FF FE then one byte per unit) or as UTF-16.
        string first = Encoding.Latin1.GetString(rows[0].S);
        Assert.True(first.EndsWith("alpha", StringComparison.Ordinal) || Encoding.Unicode.GetString(rows[0].S) == "alpha", Convert.ToHexString(rows[0].S));
    }

    [Fact]
    public void The_catalog_root_TableDef_is_page_2_with_the_system_object_columns()
    {
        InspectionReport r = Inspector.Inspect(Fixture(T1Fixture));
        TableDefinition root = r.TableDefs.Single(t => t.FirstPage == 2);
        string[] names = [.. root.Columns.Select(c => c.Name)];
        Assert.Contains("Name", names);
        Assert.Contains("ParentId", names);
        Assert.Contains("Type", names);
    }

    [Fact]
    public void A_native_Jet4_MDB_selects_the_Jet4_profile()
    {
        // Any MDB the oracle created (R-FMT-1 fixtures): the first artifact whose header carries the Jet signature.
        string? mdb = Directory.EnumerateFiles(Path.Combine(Evidence(), "artifacts")).Order(StringComparer.Ordinal)
            .FirstOrDefault(p => new FileInfo(p).Length >= 4096 && HasSignature(p, "Standard Jet DB"));
        Assert.NotNull(mdb);
        InspectionReport r = Inspector.Inspect(File.ReadAllBytes(mdb));
        Assert.Equal(ProfileRefusal.None, r.Refusal);
        Assert.Equal(FileFormat.Jet4, r.Profile!.Format);
        Assert.Contains(r.TableDefs, t => t.FirstPage == 2);
    }

    private static bool HasSignature(string path, string signature)
    {
        byte[] head = new byte[24];
        using FileStream f = File.OpenRead(path);
        return f.Read(head) == 24 && Encoding.ASCII.GetString(head, 4, signature.Length) == signature;
    }
}
