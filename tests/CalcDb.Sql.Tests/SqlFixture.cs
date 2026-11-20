using System.Buffers.Binary;
using System.Runtime.InteropServices;

using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql.Tests;

/// <summary>
/// A database image from the artifact store as the SQL layer sees it (the test's <see cref="ISqlDatabase"/> adapter over
/// the page cache and the catalog bootstrap; the DAO wiring will supply its own over its open database).
/// </summary>
internal sealed class SqlFixture : ISqlDatabase
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private readonly PageCache _cache;
    private readonly ImagePageSource _image;
    private readonly CatalogSnapshot _catalog;
    private readonly Dictionary<string, TableDefinition?> _tables = new(StringComparer.OrdinalIgnoreCase);

    public SqlFixture(string sha256)
    {
        byte[] file = ArtifactStore.ForRepository(Root()).ReadAll(sha256);
        _image = new ImagePageSource(file, D.PageSize);
        _cache = new PageCache(_image, 1, D.PageSize, 256);
        _catalog = CatalogBootstrap.Open(_cache, D, _image.PageCount, out _)!;
        DatabaseHeader header = DatabaseHeader.Decode(file.AsSpan(0, D.PageSize), D);
        Context = new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query);
    }

    public ExpressionContext Context { get; }

    public static string Root()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        return root;
    }

    public static bool Retained(string sha256) => ArtifactStore.ForRepository(Root()).Contains(sha256);

    public TableDefinition? Table(string name)
    {
        if (_tables.TryGetValue(name, out TableDefinition? known))
        {
            return known;
        }
        CatalogEntry? entry = _catalog.LocalTables(D).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        TableDefinition? table = entry is null ? null
            : TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => _cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)entry.Id, D, out _);
        _tables[name] = table;
        return table;
    }

    /// <summary>A saved query: an MSysObjects entry of type 5 in the Tables container.</summary>
    public bool IsQuery(string name) =>
        _catalog.TablesContainer is int container && _catalog.Entries.Any(e => e.ParentId == container && e.Type == 5 && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public RowReader? OpenRows(TableDefinition table, out string? failure)
    {
        RowReader? reader = RowReader.Open(_cache, D, table, _image.PageCount, out TraversalFailure? f);
        failure = f?.Predicate;
        return reader;
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }

    /// <summary>A value in the evidence form, for the one verdict comparer (Corpus.ValueComparer).</summary>
    public static unsafe CapturedValue Capture(NativeVariant v) => (VarType)v.Vt switch
    {
        VarType.Empty or VarType.Null => CapturedValue.Simple(v.Vt),
        VarType.I2 or VarType.Bool => CapturedValue.FromInteger(v.Vt, (short)v.Int64),
        VarType.I4 => CapturedValue.FromInteger(v.Vt, (int)v.Int64),
        VarType.UI1 => CapturedValue.FromInteger(v.Vt, (byte)v.Int64),
        VarType.I8 => CapturedValue.FromInteger(v.Vt, v.Int64),
        VarType.R4 => CapturedValue.FromSingle(BitConverter.Int32BitsToSingle((int)v.Int64)),
        VarType.R8 or VarType.Date => CapturedValue.FromDouble(v.Vt, v.Double),
        VarType.Currency => CapturedValue.FromCurrency(v.Int64),
        VarType.Bstr => CapturedValue.FromBstrBytes(MemoryMarshal.AsBytes(Bstrs.Chars(v.Pointer))),
        VarType.Decimal => Decimal(v),
        _ => CapturedValue.Refused(v.Vt, "not a SQL result type"),
    };

    /// <summary>
    /// A value converted to another VARIANT type by the kernel's conversion functions (CDbl, CLng, CInt, CSng, CCur,
    /// CDate, CStr, CByte), as the test's stand-in for DAO's conversion of a computed column; null when it fails.
    /// </summary>
    public static CapturedValue? Convert(NativeVariant v, VarType target, ExpressionContext context)
    {
        ScalarOperation? op = target switch
        {
            VarType.R8 => ScalarOperation.CDbl,
            VarType.I4 => ScalarOperation.CLng,
            VarType.I2 => ScalarOperation.CInt,
            VarType.R4 => ScalarOperation.CSng,
            VarType.Currency => ScalarOperation.CCur,
            VarType.Date => ScalarOperation.CDate,
            VarType.Bstr => ScalarOperation.CStr,
            VarType.UI1 => ScalarOperation.CByte,
            _ => null,
        };
        if (op is not { } operation)
        {
            return null;
        }
        ScalarOutcome r = ScalarKernel.Apply(ScalarDescriptor.Unary(operation, 0, OperandContract.Expression), v, context);
        if (!r.Succeeded || r.Type != target)
        {
            if (r.Succeeded)
            {
                r.Clear();
            }
            return null;
        }
        CapturedValue captured = Capture(r.Value);
        r.Clear();
        return captured;
    }

    private static unsafe CapturedValue Decimal(NativeVariant v)
    {
        byte* b = (byte*)&v;
        DecimalLayout d = TargetDeclarations.WindowsX64.Decimal;
        return new CapturedValue
        {
            Vt = v.Vt,
            Decimal = new DecimalParts(b[d.ScaleOffset], b[d.SignOffset], BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(b + d.High32Offset, 4)), BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(b + d.Low64Offset, 8))),
        };
    }
}