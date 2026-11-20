using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;

namespace CalcDb.Com.Ado;

/// <summary>
/// ADODB.Stream (_Stream, dual) as constructed and closed (W-09/construction-policy; II-12 §24). A new Stream is State
/// 0, Type 2 (adTypeText), Charset "Unicode", Mode 0 (r-str-ado-act/ado-stream); Type and Charset can be set before
/// Open (r-q-ado-misc/stream-file-ops: Type 1, then Open). Size, ReadText and Close of a closed Stream fail 0x800A0E78.
/// Open and the content operations are ADO-STREAM (W-09/events-persistence-inventory): named gaps here.
/// </summary>
internal sealed class StreamObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Stream;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("State", (self, args) => Int32(0))
        .Get("Type", (self, args) => Int32(((StreamObject)self)._type))
        .Put("Type", (self, args) => ((StreamObject)self).PutType(args[0]))
        .Get("Charset", (self, args) => Text(((StreamObject)self)._charset))
        .Put("Charset", (self, args) => ((StreamObject)self).PutCharset(args[0]))
        .Get("Mode", (self, args) => Int32(0))
        .Get("Size", (self, args) => ((StreamObject)self).Fail(AdoErrors.ObjectClosed))
        .Method("ReadText", (self, args) => ((StreamObject)self).Fail(AdoErrors.ObjectClosed))
        .Method("Close", (self, args) => ((StreamObject)self).Fail(AdoErrors.ObjectClosed))
        .Method("Open", (self, args) => NotImplemented("Stream.Open (ADO-STREAM: W-09/events-persistence-inventory)"));

    private int _type = 2; // adTypeText
    private string _charset = "Unicode";

    private StreamObject() : base(Decl, Vtable, "ADODB.Stream")
    {
    }

    protected override OperationTable Table => Operations;

    public static unsafe int Create(HostBinding binding, nint outer, in Guid iid, nint* result) => Publish(new StreamObject(), outer, iid, result);

    private CallResult PutType(nint arg)
    {
        int type = AdoArgs.Int32(arg) ?? 0;
        if (type is not (1 or 2))
        {
            return NotImplemented("Stream.Type outside adTypeBinary and adTypeText (not asked)");
        }
        _type = type;
        return CallResult.Nothing;
    }

    /// <summary>Charset of a closed Stream: kept as given ("unicode" was assigned on an open one and read back as given).</summary>
    private CallResult PutCharset(nint arg)
    {
        string? charset = AdoArgs.IsObject(arg) ? null : Args.Text(arg);
        if (charset is null)
        {
            return NotImplemented("Stream.Charset = a value that is not text");
        }
        _charset = charset;
        return CallResult.Nothing;
    }
}
