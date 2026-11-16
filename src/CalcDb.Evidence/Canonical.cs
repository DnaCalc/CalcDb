using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CalcDb.Evidence;

/// <summary>
/// Versioned, length-delimited binary encoding used to compute exact keys (II-15 §2, T-EVID-01).
/// Strings are written as their exact UTF-16 code units, numbers in their declared width, and every
/// value carries a type tag, so <c>I4 5</c>, <c>I2 5</c> and the text <c>"5"</c> never collide. Nothing is
/// normalized: no case folding, trimming, Unicode normalization or Double round trip.
/// </summary>
public sealed class CanonicalWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public CanonicalWriter(string encodingName)
    {
        Text("encoding", encodingName);
    }

    public CanonicalWriter Text(string field, string? value)
    {
        Tag('S', field);
        if (value is null)
        {
            WriteInt32(-1);
            return this;
        }
        WriteInt32(value.Length);
        Span<byte> unit = stackalloc byte[2];
        foreach (char c in value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(unit, c);
            _buffer.Write(unit);
        }
        return this;
    }

    public CanonicalWriter Int16(string field, short value)
    {
        Tag('H', field);
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(b, value);
        _buffer.Write(b);
        return this;
    }

    public CanonicalWriter Int32(string field, int value)
    {
        Tag('I', field);
        WriteInt32(value);
        return this;
    }

    public CanonicalWriter Int64(string field, long value)
    {
        Tag('Q', field);
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, value);
        _buffer.Write(b);
        return this;
    }

    public CanonicalWriter Bool(string field, bool value)
    {
        Tag('B', field);
        _buffer.Write([value ? (byte)1 : (byte)0]);
        return this;
    }

    public CanonicalWriter Begin(string recordName)
    {
        Tag('R', recordName);
        return this;
    }

    public CanonicalWriter List<T>(string field, IReadOnlyList<T> items, Action<CanonicalWriter, T> write)
    {
        Tag('L', field);
        WriteInt32(items.Count);
        foreach (T item in items)
        {
            write(this, item);
        }
        return this;
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    public string Sha256Hex() => Hashing.Sha256Hex(_buffer.WrittenSpan);

    private void Tag(char tag, string field)
    {
        _buffer.Write([(byte)tag]);
        byte[] name = Encoding.UTF8.GetBytes(field);
        WriteInt32(name.Length);
        _buffer.Write(name);
    }

    private void WriteInt32(int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        _buffer.Write(b);
    }
}

public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256File(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>True when the string is well-formed UTF-16 (no lone surrogates).</summary>
    public static bool IsWellFormedUtf16(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return false;
                }
                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }
        return true;
    }
}
