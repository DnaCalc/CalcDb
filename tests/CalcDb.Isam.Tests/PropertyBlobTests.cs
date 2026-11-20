using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Storage;

namespace CalcDb.Isam.Tests;

/// <summary>
/// MR2 property blobs (II-06 §§5–6, R-CAT-1 CAT-P) and MSysRelationships rows (II-06 §3, CAT-D) of the r-dao-1b rich
/// fixture. Expected values are ACE's answers in evidence/r-dao-1b: db-properties-enumerated-accdb (UPText … UPMemo),
/// tabledef-properties-enumerated (Description "parent table"), field-properties-enumerated (TText DefaultValue
/// "dflt" quoted, AllowZeroLength True; Parent.Name 25 properties), relation-members-each and relationfield-members-each.
/// </summary>
public sealed class PropertyBlobTests
{
    private const string Rich = "3b512c8f10e3ab22e434d9d6e69435bf231a310044b20cdb1c03069c1be34e95";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private static (PageCache Cache, ulong Pages, CatalogSnapshot Catalog) Open()
    {
        byte[] file = NativeFixtures.Read(Rich);
        var source = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(source, 1, D.PageSize, 64);
        return (cache, source.PageCount, CatalogBootstrap.Open(cache, D, source.PageCount, out _, readProperties: true)!);
    }

    private static PropertyBlob Blob(CatalogSnapshot catalog, string name)
    {
        CatalogEntry entry = catalog.Entries.Single(e => e.Name == name);
        PropertyBlob? blob = PropertyBlob.Read(entry.Properties!, out string? failure);
        Assert.True(blob is not null, failure);
        return blob;
    }

    private static PropertyValue Value(PropertyMap map, string name) => PropertyBlob.Decode(map.Properties.Single(p => p.Name == name));

    [Fact]
    public void The_database_properties_are_the_MSysDb_object_map_in_stored_order()
    {
        PropertyMap map = Blob(Open().Catalog, "MSysDb").ObjectMap!;
        Assert.Equal(["UPText", "UPLong", "UPBool", "UPDate", "UPDouble", "UPMemo"], map.Properties.Select(p => p.Name));
        // dp.Type and dp.Value: Text 10 "alpha", Long 4 42, Boolean 1 True, Date 8 46024, Double 7 2.5, Memo 12.
        Assert.Equal([10, 4, 1, 8, 7, 12], map.Properties.Select(p => (int)p.NativeType));
        Assert.Equal("alpha", Value(map, "UPText").Text);
        Assert.Equal(42, Value(map, "UPLong").Integer);
        Assert.True(Value(map, "UPBool").Boolean);
        Assert.Equal(46024d, BitConverter.UInt64BitsToDouble(Value(map, "UPDate").Bits));
        Assert.Equal(2.5d, BitConverter.UInt64BitsToDouble(Value(map, "UPDouble").Bits));
        Assert.All(map.Properties, p => Assert.Equal(0, p.Flags)); // user properties
    }

    [Fact]
    public void A_table_blob_has_its_own_map_and_one_map_per_column_with_stored_properties()
    {
        CatalogSnapshot catalog = Open().Catalog;
        PropertyBlob parent = Blob(catalog, "Parent");
        Assert.Equal("parent table", Value(parent.ObjectMap!, "Description").Text);
        PropertyMap name = parent.Map(PropertySectionKind.ColumnValues, "Name")!;
        Assert.Equal(["Required", "Description"], name.Properties.Select(p => p.Name)); // Parent.Name: 24 built-ins + Description
        PropertyMap text = Blob(catalog, "Typed").Map(PropertySectionKind.ColumnValues, "TText")!;
        Assert.True(Value(text, "AllowZeroLength").Boolean);
        Assert.Equal("\"dflt\"", Value(text, "DefaultValue").Text);
    }

    [Fact]
    public void A_saved_query_stores_ODBCTimeout_and_MaxRecords_as_built_ins()
    {
        PropertyMap map = Blob(Open().Catalog, "QSel").ObjectMap!;
        Assert.Equal(["ODBCTimeout", "MaxRecords"], map.Properties.Select(p => p.Name));
        Assert.All(map.Properties, p => Assert.Equal(1, p.Flags));
        Assert.Equal(60, Value(map, "ODBCTimeout").Integer);
        Assert.Equal(0, Value(map, "MaxRecords").Integer);
    }

    [Fact]
    public void Relationships_read_as_DAO_reports_them()
    {
        (PageCache cache, ulong pages, CatalogSnapshot catalog) = Open();
        IReadOnlyList<Relationship>? relationships = RelationshipCatalog.Read(cache, D, catalog, pages, out string? failure);
        Assert.True(relationships is not null, failure);
        // r.Name, r.Table, r.ForeignTable, r.Attributes; the field's Name and ForeignName.
        Assert.Equal(
            [new("FK_Grand", "Child", "Grand", 0, [new("CID", "CID", 0)]), new("RelMain", "Parent", "Child", 256, [new RelationshipColumn("ID", "PID", 0)])],
            relationships.OrderBy(r => r.Name).Select(r => r with { Columns = [.. r.Columns] }),
            new RelationshipComparer());
    }

    private sealed class RelationshipComparer : IEqualityComparer<Relationship>
    {
        public bool Equals(Relationship? x, Relationship? y) =>
            x is not null && y is not null && (x.Name, x.Table, x.ForeignTable, x.Attributes) == (y.Name, y.Table, y.ForeignTable, y.Attributes) && x.Columns.SequenceEqual(y.Columns);

        public int GetHashCode(Relationship r) => r.Name.GetHashCode(StringComparison.Ordinal);
    }

    [Fact]
    public void A_blob_without_the_signature_is_refused_by_name()
    {
        Assert.Null(PropertyBlob.Read("KKD\0"u8, out string? failure));
        Assert.Equal("no MR2 signature", failure);
    }
}
