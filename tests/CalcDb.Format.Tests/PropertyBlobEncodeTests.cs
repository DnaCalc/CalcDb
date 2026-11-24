using CalcDb.Format;

namespace CalcDb.Format.Tests;

/// <summary>
/// MR2 property-blob encoding and name-dictionary preservation (<c>PropertyBlob.Encode</c>, W-08 task 9dvt;
/// qualified.md <c>r-q37</c>): removing every property keeps the existing name dictionary and produces the
/// 40-byte Names-only blob (<c>r-q37-valrule-put/remove-rule-only-accdb</c>, <c>remove-inline-text-only-accdb</c>);
/// the single-argument <c>Encode(maps)</c> overload remains unchanged for existing DDL callers.
/// </summary>
public sealed class PropertyBlobEncodeTests
{
    [Fact]
    public void Read_exposes_name_dictionary_and_section_count()
    {
        byte[] encoded = PropertyBlob.Encode(
            ["ValidationRule", "ValidationText"],
            [new PropertyMap(PropertySectionKind.ColumnValues, "V", [new StoredProperty("ValidationText", 0, NativeTypes.Text, [0x78, 0x00])])]);

        PropertyBlob? blob = PropertyBlob.Read(encoded, out string? failure);
        Assert.True(blob is not null, failure);
        Assert.Equal(1, blob.NamesSections);
        Assert.Equal(["ValidationRule", "ValidationText"], blob.Names);
        Assert.Single(blob.Maps);
        Assert.Equal("x", PropertyBlob.Decode(blob.Maps[0].Properties[0]).Text);
    }

    [Fact]
    public void Encode_with_empty_maps_writes_the_40_byte_names_only_blob()
    {
        byte[] encoded = PropertyBlob.Encode(["ValidationRule"], []);

        Assert.Equal(40, encoded.Length);
        PropertyBlob? blob = PropertyBlob.Read(encoded, out string? failure);
        Assert.True(blob is not null, failure);
        Assert.Equal(1, blob.NamesSections);
        Assert.Equal(["ValidationRule"], blob.Names);
        Assert.Empty(blob.Maps);
    }

    [Fact]
    public void Encode_maps_only_matches_empty_explicit_names_byte_for_byte()
    {
        PropertyMap[] maps =
        [
            new PropertyMap(PropertySectionKind.ColumnValues, "V", [new StoredProperty("Required", 0, NativeTypes.Boolean, [1])]),
            new PropertyMap(PropertySectionKind.ColumnValues, "W", [new StoredProperty("AllowZeroLength", 0, NativeTypes.Boolean, [0])]),
        ];

        byte[] legacy = PropertyBlob.Encode(maps);
        byte[] explicitEmpty = PropertyBlob.Encode([], maps);

        Assert.Equal(legacy, explicitEmpty);
        PropertyBlob? blob = PropertyBlob.Read(legacy, out string? failure);
        Assert.True(blob is not null, failure);
        Assert.Equal(["Required", "AllowZeroLength"], blob.Names);
    }
}
