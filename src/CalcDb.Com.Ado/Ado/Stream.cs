using System.Text;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Ado;

/// <summary>
/// ADODB.Stream (<c>_Stream</c>, dual; W-09/events-persistence-inventory, R-ADO-1 ADO-STREAM, II-12 §24).
/// Implements the recorded standalone text and binary stream semantics from
/// <c>evidence/r-q45-querydef-stream-rollback/stream-*</c>, <c>evidence/r-str-ado-act/ado-stream</c>,
/// <c>evidence/r-q-ado-misc/stream-text-ops</c>, <c>evidence/r-q-ado-misc/stream-file-ops</c>,
/// <c>evidence/r-ado-1b/persist-stream-roundtrip</c>, <c>evidence/r-cplx-rebuild/att-*</c>,
/// and <c>evidence/r-oledb-odbc/stream-record-from-row</c>; every unrecorded option, charset,
/// source form, or edge is refused explicitly by name.
/// </summary>
internal sealed unsafe class StreamObject : AdoObject
{
    private const int AdTypeBinary = 1;
    private const int AdTypeText = 2;

    private const int AdModeUnknown = 0;
    private const int AdModeRead = 1;
    private const int AdModeWrite = 2;
    private const int AdModeReadWrite = 3;

    private const int AdOpenStreamUnspecified = -1;

    private const int AdReadAll = -1;
    private const int AdReadLine = -2;

    private const int AdWriteChar = 0;
    private const int AdWriteLine = 1;

    private const int AdSaveCreateNotExist = 1;
    private const int AdSaveCreateOverWrite = 2;

    private const int AdCrLf = -1;
    private const int AdLf = 10;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly InterfaceDecl Decl = AdoTypes.Stream;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Size", (self, _) => ((StreamObject)self).GetSize())
        .Get("EOS", (self, _) => ((StreamObject)self).GetEos())
        .Get("Position", (self, _) => ((StreamObject)self).GetPosition())
        .Put("Position", (self, args) => ((StreamObject)self).PutPosition(args[0]))
        .Get("Type", (self, _) => Int32(((StreamObject)self)._type))
        .Put("Type", (self, args) => ((StreamObject)self).PutType(args[0]))
        .Get("LineSeparator", (self, _) => Int32(((StreamObject)self)._lineSeparator))
        .Put("LineSeparator", (self, args) => ((StreamObject)self).PutLineSeparator(args[0]))
        .Get("State", (self, _) => Int32(((StreamObject)self)._isOpen ? 1 : 0))
        .Get("Mode", (self, _) => Int32(((StreamObject)self)._mode))
        .Put("Mode", (self, args) => ((StreamObject)self).PutMode(args[0]))
        .Get("Charset", (self, _) => Text(((StreamObject)self)._charset))
        .Put("Charset", (self, args) => ((StreamObject)self).PutCharset(args[0]))
        .Method("Read", (self, args) => ((StreamObject)self).Read(args))
        .Method("Open", (self, args) => ((StreamObject)self).Open(args))
        .Method("Close", (self, _) => ((StreamObject)self).Close())
        .Method("SkipLine", (self, _) => ((StreamObject)self).SkipLine())
        .Method("Write", (self, args) => ((StreamObject)self).Write(args[0]))
        .Method("SetEOS", (self, _) => ((StreamObject)self).SetEos())
        .Method("CopyTo", (self, args) => ((StreamObject)self).CopyTo(args))
        .Method("Flush", (self, _) => ((StreamObject)self).Flush())
        .Method("SaveToFile", (self, args) => ((StreamObject)self).SaveToFile(args))
        .Method("LoadFromFile", (self, args) => ((StreamObject)self).LoadFromFile(args[0]))
        .Method("ReadText", (self, args) => ((StreamObject)self).ReadText(args))
        .Method("WriteText", (self, args) => ((StreamObject)self).WriteText(args))
        .Method("Cancel", (self, _) => ((StreamObject)self).Cancel());

    private readonly List<byte> _bytes = [];
    private bool _isOpen;
    private int _position;
    private int _type = AdTypeText;
    private int _mode = AdModeUnknown;
    private int _lineSeparator = AdCrLf;
    private string _charset = "Unicode";

    private StreamObject() : base(Decl, Vtable, "ADODB.Stream")
    {
    }

    protected override OperationTable Table => Operations;

    internal bool IsOpen => _isOpen;

    internal int StreamType => _type;

    internal int CurrentPosition => _position;

    public static int Create(HostBinding binding, nint outer, in Guid iid, nint* result) =>
        Publish(new StreamObject(), outer, iid, result);

    /// <summary>
    /// Internal destination hook for <c>Recordset.Save(stream, adPersistADTG)</c>: opens a closed Stream or resets
    /// an already-open Stream (empty, prefilled, text/binary, or <c>adModeRead</c>) from byte 0, sets <c>Type = 1</c>,
    /// and leaves <c>Position = 0</c> (<c>r-ado-1b/persist-stream-roundtrip</c>,
    /// <c>r-q45-querydef-stream-rollback/stream-save-into-open</c> s1, s2, s5, s6, s7).
    /// </summary>
    internal void WritePersistedBinary(ReadOnlySpan<byte> payload)
    {
        _isOpen = true;
        _type = AdTypeBinary;
        _bytes.Clear();
        _bytes.AddRange(payload);
        _position = 0;
    }

    /// <summary>
    /// Internal destination hook for <c>Recordset.Save(stream, adPersistXML)</c>: opens a closed Stream or resets
    /// an already-open Stream from byte 0, sets <c>Type = 2</c> and <c>Charset = "Unicode"</c>, writes the UTF-16LE
    /// BOM plus <paramref name="xmlText"/>, and leaves <c>Position = 0</c> (<c>r-ado-1b/persist-stream-roundtrip</c> sx,
    /// <c>r-q45-querydef-stream-rollback/stream-save-into-open</c> s3, s4).
    /// </summary>
    internal void WritePersistedUnicodeXml(string xmlText)
    {
        _isOpen = true;
        _type = AdTypeText;
        _charset = "Unicode";
        _bytes.Clear();
        _bytes.Add(0xFF);
        _bytes.Add(0xFE);
        _bytes.AddRange(Encoding.Unicode.GetBytes(xmlText));
        _position = 0;
    }

    /// <summary>Return the remaining bytes starting at <see cref="CurrentPosition"/> for <c>Recordset.Open(Stream)</c>.</summary>
    internal byte[] ReadRemainingRawBytes()
    {
        if (!_isOpen || _position >= _bytes.Count)
        {
            return [];
        }
        int remaining = _bytes.Count - _position;
        byte[] slice = new byte[remaining];
        _bytes.CopyTo(_position, slice, 0, remaining);
        return slice;
    }

    private CallResult GetSize() =>
        _isOpen ? Int32(_bytes.Count) : Fail(AdoErrors.ObjectClosed);

    private CallResult GetEos() =>
        _isOpen ? Bool(_position >= _bytes.Count) : Fail(AdoErrors.ObjectClosed);

    private CallResult GetPosition() =>
        _isOpen ? Int32(_position) : Fail(AdoErrors.ObjectClosed);

    private CallResult PutPosition(nint arg)
    {
        if (!_isOpen)
        {
            return Fail(AdoErrors.ObjectClosed);
        }
        if (AdoArgs.Int32(arg) is not int pos)
        {
            return NotModelled("NotModelled.AdoStreamPositionType", "Stream.Position put with a non-integer argument is not recorded");
        }
        if (pos < 0)
        {
            return NotModelled("NotModelled.AdoStreamPositionNegative", $"Stream.Position = {pos} (negative) is not recorded");
        }
        if (pos > _bytes.Count)
        {
            return NotModelled("NotModelled.AdoStreamPositionPastEnd", $"Stream.Position = {pos} past Size {_bytes.Count} is not recorded");
        }
        _position = pos;
        return CallResult.Nothing;
    }

    private CallResult PutType(nint arg)
    {
        if (AdoArgs.Int32(arg) is not int type || type is not (AdTypeBinary or AdTypeText))
        {
            return NotModelled("NotModelled.AdoStreamType", "Stream.Type outside adTypeBinary (1) and adTypeText (2) is not recorded");
        }
        if (_isOpen && _position != 0)
        {
            // r-str-ado-act/ado-stream #43, r-q-ado-misc/stream-file-ops #20: switching Type at Position != 0 fails 0x800A0C93.
            if (type != _type)
            {
                return Fail(AdoErrors.NotAllowedInContext);
            }
            return NotModelled("NotModelled.AdoStreamTypeSameAtNonZeroPos", "Stream.Type put with unchanged type at Position != 0 is not recorded");
        }
        _type = type;
        return CallResult.Nothing;
    }

    private CallResult PutLineSeparator(nint arg)
    {
        if (AdoArgs.Int32(arg) is not int sep || sep is not (AdCrLf or AdLf))
        {
            return NotModelled("NotModelled.AdoStreamLineSeparator", "Stream.LineSeparator outside adCRLF (-1) and adLF (10) is not recorded");
        }
        _lineSeparator = sep;
        return CallResult.Nothing;
    }

    private CallResult PutMode(nint arg)
    {
        if (_isOpen)
        {
            // r-q45-querydef-stream-rollback/stream-open-modes-reopen #20: Mode put while open fails 0x800A0E79.
            return Fail(AdoErrors.ObjectOpen);
        }
        if (AdoArgs.Int32(arg) is not int mode || mode is not (AdModeUnknown or AdModeRead or AdModeWrite or AdModeReadWrite))
        {
            return NotModelled("NotModelled.AdoStreamMode", "Stream.Mode outside adModeUnknown (0), adModeRead (1), adModeWrite (2), adModeReadWrite (3) is not recorded");
        }
        _mode = mode;
        return CallResult.Nothing;
    }

    private CallResult PutCharset(nint arg)
    {
        string? charset = AdoArgs.IsObject(arg) ? null : Args.Text(arg);
        if (charset is null)
        {
            return NotModelled("NotModelled.AdoStreamCharset", "Stream.Charset = a value that is not text is not recorded");
        }
        if (!IsRecordedCharset(charset))
        {
            return NotModelled("NotModelled.AdoStreamCharset", $"Stream.Charset = '{charset}' is not recorded (recorded: Unicode, utf-8, ascii)");
        }
        if (_isOpen && (_type != AdTypeText || _position != 0))
        {
            return NotModelled("NotModelled.AdoStreamCharsetContext", "Stream.Charset put on an open binary stream or at Position != 0 is not recorded");
        }
        // r-str-ado-act/ado-stream #11, #22-23; r-q-ado-misc/stream-text-ops #29-31, #35-37: preserves exact casing and keeps Size.
        _charset = charset;
        return CallResult.Nothing;
    }

    private CallResult Open(ReadOnlySpan<nint> args)
    {
        if (_isOpen)
        {
            // r-q45-querydef-stream-rollback/stream-open-modes-reopen #16: reopen fails 0x800A0E79 leaving state/size/position intact.
            return Fail(AdoErrors.ObjectOpen);
        }

        nint sourceArg = args.Length > 0 ? args[0] : 0;
        nint modeArg = args.Length > 1 ? args[1] : 0;
        nint optionsArg = args.Length > 2 ? args[2] : 0;
        nint userArg = args.Length > 3 ? args[3] : 0;
        nint passArg = args.Length > 4 ? args[4] : 0;

        bool sourcePresent = sourceArg != 0 && !TypedCall.IsMissing(Args.Cell(sourceArg)) && Args.TypeOf(sourceArg) != VarType.Empty;
        if (sourcePresent)
        {
            if (AdoArgs.ObjectOf<RecordObject>(sourceArg) is not null)
            {
                // r-oledb-odbc/stream-record-from-row #26: Stream.Open(closedRecord, 1, 4) fails 0x800A0E78.
                return Fail(AdoErrors.ObjectClosed);
            }
            return NotModelled("NotModelled.AdoStreamOpenSource", "Stream.Open with a URL or object Source (other than a closed Record) is not recorded");
        }

        if (userArg != 0 && !TypedCall.IsMissing(Args.Cell(userArg)) && !string.IsNullOrEmpty(Args.Text(userArg)))
        {
            return NotModelled("NotModelled.AdoStreamOpenCredentials", "Stream.Open with UserName is not recorded");
        }
        if (passArg != 0 && !TypedCall.IsMissing(Args.Cell(passArg)) && !string.IsNullOrEmpty(Args.Text(passArg)))
        {
            return NotModelled("NotModelled.AdoStreamOpenCredentials", "Stream.Open with Password is not recorded");
        }

        int openMode = AdModeUnknown;
        if (modeArg != 0 && !TypedCall.IsMissing(Args.Cell(modeArg)))
        {
            if (AdoArgs.Int32(modeArg) is not int parsedMode)
            {
                return Fail(AdoErrors.ArgumentsWrongType);
            }
            openMode = parsedMode;
        }

        int openOptions = AdOpenStreamUnspecified;
        if (optionsArg != 0 && !TypedCall.IsMissing(Args.Cell(optionsArg)))
        {
            if (AdoArgs.Int32(optionsArg) is not int parsedOptions)
            {
                return Fail(AdoErrors.ArgumentsWrongType);
            }
            openOptions = parsedOptions;
        }

        // r-q45-querydef-stream-rollback/stream-open-modes-reopen #25, #33, #44, #85, #91, #97, #103:
        // with Missing Source, Mode != 0 or Options != -1 fails 0x800A0BB9 while (Missing, 0, -1) succeeds.
        if (openMode != AdModeUnknown || openOptions != AdOpenStreamUnspecified)
        {
            return Fail(AdoErrors.ArgumentsWrongType);
        }

        _bytes.Clear();
        _position = 0;
        _isOpen = true;
        return CallResult.Nothing;
    }

    private CallResult Close()
    {
        if (!_isOpen)
        {
            return Fail(AdoErrors.ObjectClosed);
        }
        _isOpen = false;
        _bytes.Clear();
        _position = 0;
        return CallResult.Nothing;
    }

    private CallResult Flush()
    {
        // r-q45-querydef-stream-rollback/stream-flush-cancel #4, #9, #22, #30, #47.
        return _isOpen ? CallResult.Nothing : Fail(AdoErrors.ObjectClosed);
    }

    private CallResult Cancel()
    {
        // r-q45-querydef-stream-rollback/stream-flush-cancel #5, #14, #23, #34, #48.
        return _isOpen ? CallResult.Nothing : Fail(AdoErrors.ObjectClosed);
    }

    private CallResult SetEos()
    {
        if (!_isOpen)
        {
            return Fail(AdoErrors.ObjectClosed);
        }
        if (_mode == AdModeRead)
        {
            return NotModelled("NotModelled.AdoStreamSetEosReadOnly", "Stream.SetEOS on an adModeRead stream is not recorded");
        }
        if (_position < _bytes.Count)
        {
            _bytes.RemoveRange(_position, _bytes.Count - _position);
        }
        return CallResult.Nothing;
    }

    private CallResult Write(nint arg)
    {
        if (!_isOpen)
        {
            // r-q45-querydef-stream-rollback/stream-write-on-text-and-types #4.
            return Fail(AdoErrors.ObjectClosed);
        }
        if (_type != AdTypeBinary)
        {
            // r-q45-querydef-stream-rollback/stream-write-on-text-and-types #8.
            return Fail(AdoErrors.NotAllowedInContext);
        }
        if (_mode == AdModeRead)
        {
            return NotModelled("NotModelled.AdoStreamWriteReadOnly", "Stream.Write on an adModeRead binary stream is not recorded");
        }

        NativeVariant v = ByRefValues.Dereference(Args.Cell(arg));
        if (v.Type != (VarType.Array | VarType.UI1) || v.Pointer == 0)
        {
            // r-q45-querydef-stream-rollback/stream-write-on-text-and-types #28, #29, #30: BSTR, VT_EMPTY, VT_NULL fail 0x800A0BB9.
            return Fail(AdoErrors.ArgumentsWrongType);
        }

        (int Lower, int Count)[] shape = SafeArrays.Shape(v.Pointer);
        if (shape.Length != 1 || shape[0].Lower != 0)
        {
            return NotModelled("NotModelled.AdoStreamWriteArrayShape", "Stream.Write with multi-dimensional or non-zero-bound byte array is not recorded");
        }

        int count = shape[0].Count;
        if (count == 0)
        {
            // r-q45-querydef-stream-rollback/stream-write-on-text-and-types #22: 0-byte Write succeeds as no-op.
            return CallResult.Nothing;
        }

        ReadOnlySpan<byte> data = new(SafeArrays.ElementPointer(v.Pointer, 0), count);
        WriteRawBytes(data);
        return CallResult.Nothing;
    }

    private CallResult Read(ReadOnlySpan<nint> args)
    {
        if (!_isOpen)
        {
            // r-q45-querydef-stream-rollback/stream-read-bounds #4.
            return Fail(AdoErrors.ObjectClosed);
        }
        if (_type != AdTypeBinary)
        {
            // r-q45-querydef-stream-rollback/stream-write-on-text-and-types #15, #16; stream-save-into-open #56.
            return Fail(AdoErrors.NotAllowedInContext);
        }
        if (_mode == AdModeWrite)
        {
            return NotModelled("NotModelled.AdoStreamReadWriteOnly", "Stream.Read on an adModeWrite binary stream is not recorded");
        }

        int numBytes = AdReadAll;
        if (args.Length > 0 && args[0] != 0 && !TypedCall.IsMissing(Args.Cell(args[0])))
        {
            if (AdoArgs.Int32(args[0]) is not int parsed)
            {
                return Fail(AdoErrors.ArgumentsWrongType);
            }
            numBytes = parsed;
        }

        if (numBytes < AdReadAll)
        {
            // r-q45-querydef-stream-rollback/stream-read-bounds #31 (-2), #35 (-5): fail 0x800A0BB9 leaving Position intact.
            return Fail(AdoErrors.ArgumentsWrongType);
        }
        if (numBytes == 0 || _position >= _bytes.Count)
        {
            // r-q45-querydef-stream-rollback/stream-read-bounds #16, #19, #23: Read(0) and Read at EOS return VT_NULL.
            return CallResult.Of(OwnedVariant.Null());
        }

        int avail = _bytes.Count - _position;
        int take = numBytes == AdReadAll ? avail : Math.Min(numBytes, avail);
        nint psa = SafeArrays.Create(VarType.UI1, (0, take));
        byte[] temp = new byte[take];
        _bytes.CopyTo(_position, temp, 0, take);
        temp.AsSpan().CopyTo(new Span<byte>(SafeArrays.ElementPointer(psa, 0), take));
        _position += take;
        return CallResult.Of(OwnedVariant.TakeArray(psa, VarType.UI1));
    }

    private CallResult WriteText(ReadOnlySpan<nint> args)
    {
        if (!_isOpen)
        {
            // r-q45-querydef-stream-rollback/stream-write-on-text-and-types #5, stream-open-modes-reopen #28/#36/#47.
            return Fail(AdoErrors.ObjectClosed);
        }
        if (_type != AdTypeText)
        {
            // r-q45-querydef-stream-rollback/stream-write-on-text-and-types #31.
            return Fail(AdoErrors.NotAllowedInContext);
        }
        if (_mode == AdModeRead)
        {
            // r-q45-querydef-stream-rollback/stream-open-modes-reopen #58: Provider 0x80030005 "Access Denied." on _Stream.
            return CallResult.Fail(ProviderFailure(HResultAccessDenied, "Access Denied.", Decl.Iid));
        }

        string? text = args.Length > 0 && args[0] != 0 ? Args.Text(args[0]) : null;
        if (text is null)
        {
            return NotModelled("NotModelled.AdoStreamWriteTextArg", "Stream.WriteText with a non-BSTR Data argument is not recorded");
        }

        int options = AdWriteChar;
        if (args.Length > 1 && args[1] != 0 && !TypedCall.IsMissing(Args.Cell(args[1])))
        {
            if (AdoArgs.Int32(args[1]) is not int parsed)
            {
                return Fail(AdoErrors.ArgumentsWrongType);
            }
            options = parsed;
        }
        if (options is not (AdWriteChar or AdWriteLine))
        {
            return NotModelled("NotModelled.AdoStreamWriteTextOption", $"Stream.WriteText Options={options} is not recorded");
        }

        if (!IsUnicodeCharset(_charset))
        {
            return NotModelled("NotModelled.AdoStreamWriteTextCharset", $"Stream.WriteText under Charset='{_charset}' is not recorded");
        }

        string payload = options == AdWriteLine
            ? text + (_lineSeparator == AdLf ? "\n" : "\r\n")
            : text;

        WriteUnicodeTextCore(payload);
        return CallResult.Nothing;
    }

    private CallResult ReadText(ReadOnlySpan<nint> args)
    {
        if (!_isOpen)
        {
            // r-q45-querydef-stream-rollback/stream-read-bounds #5, r-str-ado-act/ado-stream #35.
            return Fail(AdoErrors.ObjectClosed);
        }
        if (_type != AdTypeText)
        {
            // r-q-ado-misc/stream-file-ops #18.
            return Fail(AdoErrors.NotAllowedInContext);
        }
        if (_mode == AdModeWrite)
        {
            // r-q45-querydef-stream-rollback/stream-open-modes-reopen #70.
            return Fail(AdoErrors.NotAllowedInContext);
        }

        int numChars = AdReadAll;
        if (args.Length > 0 && args[0] != 0 && !TypedCall.IsMissing(Args.Cell(args[0])))
        {
            if (AdoArgs.Int32(args[0]) is not int parsed)
            {
                return Fail(AdoErrors.ArgumentsWrongType);
            }
            numChars = parsed;
        }

        if (numChars < AdReadLine)
        {
            // r-q45-querydef-stream-rollback/stream-read-bounds #70 (-5): fails 0x800A0BB9 leaving Position intact.
            return Fail(AdoErrors.ArgumentsWrongType);
        }

        if (_position >= _bytes.Count)
        {
            // r-q45-querydef-stream-rollback/stream-read-bounds #54, r-q-ado-misc/stream-text-ops #25, r-str-ado-act/ado-stream #31.
            return Text("");
        }

        if (IsUnicodeCharset(_charset))
        {
            return ReadUnicodeText(numChars);
        }
        if (IsUtf8Charset(_charset))
        {
            return ReadUtf8Text(numChars);
        }
        if (IsAsciiCharset(_charset))
        {
            return ReadAsciiText(numChars);
        }

        return NotModelled("NotModelled.AdoStreamCharset", $"Stream.ReadText under Charset='{_charset}' is not recorded");
    }

    private CallResult ReadUnicodeText(int numChars)
    {
        // Skip UTF-16LE BOM at Position 0 even for ReadText(0) (r-q45-querydef-stream-rollback/stream-read-bounds #58-59: Position 0 -> 2).
        if (_position == 0 && _bytes.Count >= 2 && _bytes[0] == 0xFF && _bytes[1] == 0xFE)
        {
            _position = 2;
        }
        if (numChars == 0 || _position >= _bytes.Count)
        {
            return Text("");
        }

        int remainingBytes = _bytes.Count - _position;
        int availChars = remainingBytes / 2;
        if (numChars == AdReadAll)
        {
            string decoded = DecodeUtf16Le(_position, availChars);
            // Trailing odd byte is consumed to EOS (r-str-ado-act/ado-stream #47-49: 410042 -> "A", Position=3, EOS=-1).
            _position = _bytes.Count;
            return Text(decoded);
        }
        if (numChars == AdReadLine)
        {
            string all = DecodeUtf16Le(_position, availChars);
            // r-q-ado-misc/stream-text-ops #21 ("a b...\0yL1\r\nL2\r\nL3\n" with LineSeparator=10 -> full string, Position=40)
            // and r-q45-querydef-stream-rollback/stream-read-bounds #66 ("hello" -> "hello", Position=12):
            // msado15 scans for LineSeparator up to the first embedded NUL; multi-line text with a separator before any NUL has no record.
            int nulIdx = all.IndexOf('\0');
            ReadOnlySpan<char> scanPrefix = nulIdx >= 0 ? all.AsSpan(0, nulIdx) : all.AsSpan();
            string sep = _lineSeparator == AdLf ? "\n" : "\r\n";
            if (scanPrefix.IndexOf(sep.AsSpan(), StringComparison.Ordinal) >= 0)
            {
                return NotModelled(
                    "NotModelled.AdoStreamReadLineWithSeparator",
                    "Stream.ReadText(adReadLine) on text containing a line separator before any NUL is not recorded");
            }
            _position = _bytes.Count;
            return Text(all);
        }

        int takeChars = Math.Min(numChars, availChars);
        string chunk = DecodeUtf16Le(_position, takeChars);
        _position += takeChars * 2;
        if (takeChars == availChars && (remainingBytes & 1) == 1)
        {
            _position = _bytes.Count;
        }
        return Text(chunk);
    }

    private CallResult ReadUtf8Text(int numChars)
    {
        if (numChars != AdReadAll)
        {
            return NotModelled("NotModelled.AdoStreamReadTextUtf8Count", $"Stream.ReadText({numChars}) under Charset='utf-8' is not recorded");
        }
        int start = _position;
        if (start == 0 && _bytes.Count >= 3 && _bytes[0] == 0xEF && _bytes[1] == 0xBB && _bytes[2] == 0xBF)
        {
            start = 3;
        }
        byte[] slice = new byte[_bytes.Count - start];
        _bytes.CopyTo(start, slice, 0, slice.Length);
        try
        {
            string decoded = StrictUtf8.GetString(slice);
            _position = _bytes.Count;
            return Text(decoded);
        }
        catch (DecoderFallbackException)
        {
            // r-str-ado-act/ado-stream #24, r-q-ado-misc/stream-text-ops #32: Provider 0x80070057 "The parameter is incorrect."
            return CallResult.Fail(ProviderFailure(HResultInvalidArg, "The parameter is incorrect.", Decl.Iid));
        }
    }

    private CallResult ReadAsciiText(int numChars)
    {
        if (numChars != AdReadAll)
        {
            return NotModelled("NotModelled.AdoStreamReadTextAsciiCount", $"Stream.ReadText({numChars}) under Charset='ascii' is not recorded");
        }
        string decoded = DecodeAscii(_position, _bytes.Count - _position);
        _position = _bytes.Count;
        return Text(decoded);
    }

    private CallResult SkipLine()
    {
        if (!_isOpen)
        {
            return Fail(AdoErrors.ObjectClosed);
        }
        if (_type != AdTypeText)
        {
            // r-q45-querydef-stream-rollback/stream-write-on-text-and-types #32.
            return Fail(AdoErrors.NotAllowedInContext);
        }
        if (_position >= _bytes.Count)
        {
            // r-q-ado-misc/stream-text-ops #23: SkipLine at EOS is a no-op.
            return CallResult.Nothing;
        }
        return NotModelled("NotModelled.AdoStreamSkipLineBeforeEos", "Stream.SkipLine before EOS is not recorded");
    }

    private CallResult CopyTo(ReadOnlySpan<nint> args)
    {
        if (!_isOpen)
        {
            return Fail(AdoErrors.ObjectClosed);
        }
        if (args.Length == 0 || AdoArgs.ObjectOf<StreamObject>(args[0]) is not { } dest)
        {
            return NotModelled("NotModelled.AdoStreamCopyToTarget", "Stream.CopyTo with a non-CalcDb Stream destination is not recorded");
        }
        if (!dest._isOpen)
        {
            return Fail(AdoErrors.ObjectClosed);
        }
        if (ReferenceEquals(this, dest))
        {
            return NotModelled("NotModelled.AdoStreamCopyToSelf", "Stream.CopyTo onto the same Stream instance is not recorded");
        }

        int charNumber = AdReadAll;
        if (args.Length > 1 && args[1] != 0 && !TypedCall.IsMissing(Args.Cell(args[1])))
        {
            if (AdoArgs.Int32(args[1]) is not int parsed)
            {
                return Fail(AdoErrors.ArgumentsWrongType);
            }
            charNumber = parsed;
        }
        if (charNumber != AdReadAll)
        {
            return NotModelled("NotModelled.AdoStreamCopyToCount", $"Stream.CopyTo with CharNumber={charNumber} is not recorded");
        }

        if (_type != AdTypeText || dest._type != AdTypeText)
        {
            return NotModelled("NotModelled.AdoStreamCopyToTypes", "Stream.CopyTo outside text-to-text streams is not recorded");
        }
        if (!IsAsciiCharset(_charset) || !IsUnicodeCharset(dest._charset))
        {
            return NotModelled(
                "NotModelled.AdoStreamCopyToCharsets",
                $"Stream.CopyTo from Charset='{_charset}' to Charset='{dest._charset}' is not recorded (only ascii -> Unicode in r-q-ado-misc/stream-text-ops)");
        }

        // r-q-ado-misc/stream-text-ops #46-47: 3 ascii bytes copied into open Unicode stream -> Size 8 (2B BOM + 6B UTF-16LE).
        string text = DecodeAscii(_position, _bytes.Count - _position);
        _position = _bytes.Count;
        dest.WriteUnicodeTextCore(text);
        return CallResult.Nothing;
    }

    private CallResult SaveToFile(ReadOnlySpan<nint> args)
    {
        if (!_isOpen)
        {
            return Fail(AdoErrors.ObjectClosed);
        }
        string? fileName = args.Length > 0 && args[0] != 0 ? Args.Text(args[0]) : null;
        if (string.IsNullOrEmpty(fileName))
        {
            return NotModelled("NotModelled.AdoStreamSaveToFilePath", "Stream.SaveToFile with empty or non-text FileName is not recorded");
        }

        int options = AdSaveCreateNotExist;
        if (args.Length > 1 && args[1] != 0 && !TypedCall.IsMissing(Args.Cell(args[1])))
        {
            if (AdoArgs.Int32(args[1]) is not int parsed)
            {
                return Fail(AdoErrors.ArgumentsWrongType);
            }
            options = parsed;
        }
        if (options is not (AdSaveCreateNotExist or AdSaveCreateOverWrite))
        {
            return NotModelled("NotModelled.AdoStreamSaveOptions", $"Stream.SaveToFile Options={options} is not recorded");
        }

        if (options == AdSaveCreateNotExist && File.Exists(fileName))
        {
            // r-q-ado-misc/stream-file-ops #10: adSaveCreateNotExist onto existing file fails 0x800A0BBC.
            return Fail(AdoErrors.WriteToFileFailed);
        }

        byte[] output;
        if (_type == AdTypeBinary)
        {
            // r-q-ado-misc/stream-file-ops #9/#11, r-ado-1b/persist-stream-roundtrip #56, r-cplx-rebuild/att-*.
            output = [.. _bytes];
        }
        else if (IsUtf8Charset(_charset))
        {
            // r-str-ado-act/ado-stream #25-26: text stream with Charset="utf-8" writes UTF-8 BOM EF BB BF prepended
            // to raw buffer bytes when not already present (14B Unicode buffer -> 17B file sha256 a802d5ea9691...).
            bool hasUtf8Bom = _bytes.Count >= 3 && _bytes[0] == 0xEF && _bytes[1] == 0xBB && _bytes[2] == 0xBF;
            if (hasUtf8Bom)
            {
                output = [.. _bytes];
            }
            else
            {
                output = new byte[_bytes.Count + 3];
                output[0] = 0xEF;
                output[1] = 0xBB;
                output[2] = 0xBF;
                _bytes.CopyTo(output, 3);
            }
        }
        else
        {
            return NotModelled("NotModelled.AdoStreamSaveTextCharset", $"Stream.SaveToFile on a text stream with Charset='{_charset}' is not recorded");
        }

        try
        {
            FileMode fileMode = options == AdSaveCreateNotExist ? FileMode.CreateNew : FileMode.Create;
            using var fs = new FileStream(fileName, fileMode, FileAccess.Write, FileShare.None);
            fs.Write(output, 0, output.Length);
        }
        catch (IOException)
        {
            return Fail(AdoErrors.WriteToFileFailed);
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(AdoErrors.WriteToFileFailed);
        }

        return CallResult.Nothing;
    }

    private CallResult LoadFromFile(nint arg)
    {
        if (!_isOpen)
        {
            return Fail(AdoErrors.ObjectClosed);
        }
        string? fileName = arg != 0 ? Args.Text(arg) : null;
        if (string.IsNullOrEmpty(fileName))
        {
            return NotModelled("NotModelled.AdoStreamLoadFromFilePath", "Stream.LoadFromFile with empty or non-text FileName is not recorded");
        }
        if (_type != AdTypeBinary)
        {
            return NotModelled("NotModelled.AdoStreamLoadFromFileText", "Stream.LoadFromFile on a text stream is not recorded");
        }
        if (!File.Exists(fileName))
        {
            // r-q-ado-misc/stream-file-ops #34: missing file fails 0x800A0BBA leaving Size unchanged.
            return Fail(AdoErrors.FileCouldNotBeOpened);
        }

        try
        {
            using var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > int.MaxValue)
            {
                return NotModelled("NotModelled.AdoStreamLargeFile", "Stream.LoadFromFile above 2 GB is not recorded");
            }
            byte[] data = new byte[(int)fs.Length];
            fs.ReadExactly(data);
            _bytes.Clear();
            _bytes.AddRange(data);
            _position = 0;
            return CallResult.Nothing;
        }
        catch (IOException)
        {
            return Fail(AdoErrors.FileCouldNotBeOpened);
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(AdoErrors.FileCouldNotBeOpened);
        }
    }

    private void WriteUnicodeTextCore(string text)
    {
        if (_position == 0 && _bytes.Count == 0)
        {
            // r-str-ado-act/ado-stream #12/#19, r-q-ado-misc/stream-text-ops #8: first Unicode write emits UTF-16LE BOM FF FE.
            _bytes.Add(0xFF);
            _bytes.Add(0xFE);
            _position = 2;
        }
        if (text.Length > 0)
        {
            byte[] encoded = Encoding.Unicode.GetBytes(text);
            WriteRawBytes(encoded);
        }
    }

    private void WriteRawBytes(ReadOnlySpan<byte> data)
    {
        int end = _position + data.Length;
        if (end <= _bytes.Count)
        {
            for (int i = 0; i < data.Length; i++)
            {
                _bytes[_position + i] = data[i];
            }
        }
        else
        {
            int overwrite = _bytes.Count - _position;
            for (int i = 0; i < overwrite; i++)
            {
                _bytes[_position + i] = data[i];
            }
            for (int i = overwrite; i < data.Length; i++)
            {
                _bytes.Add(data[i]);
            }
        }
        _position = end;
    }

    private string DecodeUtf16Le(int byteOffset, int charCount)
    {
        if (charCount == 0)
        {
            return "";
        }
        char[] chars = new char[charCount];
        for (int i = 0; i < charCount; i++)
        {
            int idx = byteOffset + (i * 2);
            chars[i] = (char)(_bytes[idx] | (_bytes[idx + 1] << 8));
        }
        return new string(chars);
    }

    /// <summary>
    /// Windows CP 20127 (US-ASCII) decoding as observed in <c>r-q-ado-misc/stream-text-ops</c> #38:
    /// each byte maps to <c>(char)(b &amp; 0x7F)</c> (e.g. <c>0xFF -> 0x7F</c>, <c>0xFE -> '~'</c>, <c>0xD8 -> 'X'</c>).
    /// </summary>
    private string DecodeAscii(int byteOffset, int byteCount)
    {
        if (byteCount == 0)
        {
            return "";
        }
        char[] chars = new char[byteCount];
        for (int i = 0; i < byteCount; i++)
        {
            chars[i] = (char)(_bytes[byteOffset + i] & 0x7F);
        }
        return new string(chars);
    }

    private static bool IsUnicodeCharset(string charset) =>
        string.Equals(charset, "Unicode", StringComparison.OrdinalIgnoreCase);

    private static bool IsUtf8Charset(string charset) =>
        string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase);

    private static bool IsAsciiCharset(string charset) =>
        string.Equals(charset, "ascii", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecordedCharset(string charset) =>
        IsUnicodeCharset(charset) || IsUtf8Charset(charset) || IsAsciiCharset(charset);

    private static CallResult NotModelled(string predicate, string detail) =>
        CallResult.Fail(Failure.NotModelled(predicate, detail));
}
