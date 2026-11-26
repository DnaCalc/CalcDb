using System.Security.Cryptography;
using CalcDb.Com.Ado;
using CalcDb.Com.Com;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-09/events-persistence-inventory (<c>prfx</c> Stretch 2):
/// exercises <c>ADODB.Stream</c>, <c>Recordset.Save</c> / <c>Recordset.Open</c> from ADTG/XML files and Streams,
/// <c>Provider=MSPersist</c>, and disconnected persisted Recordset reconnect + <c>UpdateBatch</c> against the
/// published <c>CalcDb.dll</c> under <c>CalcDb.Own.manifest</c>, checking every rule against retained
/// <c>r-q45-querydef-stream-rollback</c>, <c>r-str-ado-act</c>, <c>r-q-ado-misc</c>, <c>r-ado-1b</c>,
/// <c>r-fix-wire-ado</c>, and <c>r-oledb-odbc</c> records with zero native ACE/ADO DLLs loaded.
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoStreamAndPersistTests
{
    private const string AccdbMainFixture = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7"; // r-ado-1b-main.accdb
    private const string AccdbAdo4Fixture = "98b792c86d8ea840c1028fd0a067ef6b9917e4dfbeb18ee78cb1acb4309cf310"; // r-str-ado-act-ado4-accdb.accdb
    private const string ExpectedAdtg568Sha256 = "03cdca6d816d2b678fd0d48189d39f88692aedf1aded2efe8c69a79074074139";
    private const string ExpectedAdoStreamBinSha256 = "a802d5ea969157e5a017b26b9ac130ae3f28d10732e3ae69d69ad8d3cf5307dd";
    private const string ExpectedScratchStBinSha256 = "76fe3925c7167317f2df68454339f5ec3650e4062178b4f2be219b105a507907";

    private static readonly Guid OwnStream = new("95C5FB19-3B72-4FFA-868F-12A68495FF37");
    private static readonly Guid OwnRecord = new("2CA37905-9986-469E-8E8D-8E14E85E606E");
    private static readonly Guid IidStream = new("00001565-0000-0010-8000-00AA006D2EA4");

    [Fact]
    public void CalcDb_ADO_Stream_and_Persistence_match_retained_ACE_records()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-prfx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                StreamLifecycleModesFlushCancelAndClosedRecord();
                StreamTextBinaryBoundsCharsetAndFileOps(tempDir);
                PersistStreamSaveIntoOpenAndExactAdtgXmlBytes(
                    CopyFixture(store, AccdbMainFixture, tempDir, "stream-rt.accdb"),
                    tempDir);
                PersistMalformedFilesAndOverwriteFlags(
                    CopyFixture(store, AccdbMainFixture, tempDir, "overwrite.accdb"),
                    tempDir);
                PersistPendingChangesMsPersistAndReconnect(
                    CopyFixture(store, AccdbAdo4Fixture, tempDir, "ado4.accdb"),
                    CopyFixture(store, AccdbMainFixture, tempDir, "reconn-adtg.accdb"),
                    CopyFixture(store, AccdbMainFixture, tempDir, "reconn-xml.accdb"),
                    tempDir);
                UnrecordedEdgesRefusedByName(
                    CopyFixture(store, AccdbMainFixture, tempDir, "refusals.accdb"),
                    tempDir);

                Assert.False(IsLoaded("msado15.dll"));
                Assert.False(IsLoaded("ACEOLEDB.DLL"));
                Assert.False(IsLoaded("ACECORE.DLL"));
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch
        {
        }

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static void StreamLifecycleModesFlushCancelAndClosedRecord()
    {
        // r-q45-querydef-stream-rollback/stream-open-modes-reopen
        nint st = New(OwnStream);
        try
        {
            Assert.Equal(0, Get(st, "State"));
            Assert.Equal(2, Get(st, "Type"));
            Assert.Equal(0, Get(st, "Mode"));
            Assert.Equal(-1, Get(st, "LineSeparator"));
            Assert.Equal("Unicode", Get(st, "Charset"));

            Assert.Null(Call(st, "Open").Failure);
            Assert.Equal(1, Get(st, "State"));
            AssertAdoErr(Call(st, "Open"), unchecked((int)0x800A0E79), "ADODB.Stream", 1240654, IidStream);
            AssertAdoErr(Put(st, "Mode", 3), unchecked((int)0x800A0E79), "ADODB.Stream", 1240654, IidStream);
            Assert.Null(Call(st, "Close").Failure);
            Assert.Equal(0, Get(st, "State"));

            // Open(Missing, Mode != 0) or Open(Missing, 0, Options != -1) fails 0x800A0BB9 leaving Stream closed.
            AssertAdoErr(Call(st, "Open", Missing, 3, -1), unchecked((int)0x800A0BB9), "ADODB.Stream", 1240641, IidStream);
            Assert.Equal(0, Get(st, "State"));
            AssertAdoErr(Call(st, "Open", Missing, 0, 1), unchecked((int)0x800A0BB9), "ADODB.Stream", 1240641, IidStream);
            Assert.Equal(0, Get(st, "State"));

            // Mode = 1 (adModeRead) before Open: WriteText fails Provider 0x80030005 "Access Denied." (helpContext 1240640, IErrorInfo _Stream).
            Assert.Null(Put(st, "Mode", 1).Failure);
            Assert.Null(Call(st, "Open").Failure);
            Outcome writeDenied = Call(st, "WriteText", "denied", 0);
            Assert.NotNull(writeDenied.Failure);
            Assert.Equal(unchecked((int)0x80030005), writeDenied.Failure!.Value.Scode);
            Assert.Equal("Provider", writeDenied.Failure.Value.Source);
            Assert.Equal("Access Denied.", writeDenied.Failure.Value.Description);
            Assert.Equal(1240640u, writeDenied.Failure.Value.HelpContext);
            Assert.Equal(IidStream, writeDenied.Failure.Value.InfoGuid);
            Assert.Null(Call(st, "Close").Failure);

            // Mode = 2 (adModeWrite) before Open: WriteText succeeds, ReadText fails 0x800A0C93.
            Assert.Null(Put(st, "Mode", 2).Failure);
            Assert.Null(Call(st, "Open").Failure);
            Assert.Null(Call(st, "WriteText", "wonly", 0).Failure);
            Assert.Null(Put(st, "Position", 0).Failure);
            AssertAdoErr(Call(st, "ReadText", -1), unchecked((int)0x800A0C93), "ADODB.Stream", 1240646, IidStream);
            Assert.Null(Call(st, "Close").Failure);
        }
        finally
        {
            Release(st);
        }

        // r-q45-querydef-stream-rollback/stream-flush-cancel
        nint fc = New(OwnStream);
        try
        {
            AssertAdoErr(Call(fc, "Flush"), unchecked((int)0x800A0E78), "ADODB.Stream", 1240653, IidStream);
            AssertAdoErr(Call(fc, "Cancel"), unchecked((int)0x800A0E78), "ADODB.Stream", 1240653, IidStream);
            Assert.Null(Call(fc, "Open").Failure);
            Assert.Null(Call(fc, "WriteText", "abc", 0).Failure);
            Assert.Null(Call(fc, "Flush").Failure);
            Assert.Null(Call(fc, "Cancel").Failure);
            Assert.Null(Put(fc, "Position", 0).Failure);
            Assert.Equal("abc", GetText(Call(fc, "ReadText", -1)));
            Assert.Null(Call(fc, "Close").Failure);
            AssertAdoErr(Call(fc, "Close"), unchecked((int)0x800A0E78), "ADODB.Stream", 1240653, IidStream);
        }
        finally
        {
            Release(fc);
        }

        // r-oledb-odbc/stream-record-from-row: Stream.Open(closedRecord, 1, 4) -> 0x800A0E78.
        nint rec = New(OwnRecord);
        nint sr = New(OwnStream);
        try
        {
            AssertAdoErr(Call(sr, "Open", new Dispatch(rec), 1, 4), unchecked((int)0x800A0E78), "ADODB.Stream", 1240653, IidStream);
        }
        finally
        {
            Release(sr);
            Release(rec);
        }
    }

    private static void StreamTextBinaryBoundsCharsetAndFileOps(string tempDir)
    {
        // r-str-ado-act/ado-stream: text with NUL, Euro, G-clef, Charset="utf-8", SaveToFile -> exact 17B SHA-256.
        string streamBinPath = Path.Combine(tempDir, "stream.bin");
        nint s = New(OwnStream);
        try
        {
            Assert.Null(Call(s, "Open").Failure);
            Assert.Null(Call(s, "WriteText", "a\0b\u20AC\uD834\uDD1E", 0).Failure);
            Assert.Equal(14, Get(s, "Size"));
            Assert.Equal(14, Get(s, "Position"));
            Assert.Equal(true, Get(s, "EOS"));
            Assert.Null(Put(s, "Position", 0).Failure);
            Assert.Equal("a\0b\u20AC\uD834\uDD1E", GetText(Call(s, "ReadText", -1)));

            Assert.Null(Put(s, "Position", 0).Failure);
            Assert.Null(Put(s, "Charset", "utf-8").Failure);
            Assert.Null(Call(s, "SaveToFile", streamBinPath, 2).Failure);
            byte[] savedBytes = File.ReadAllBytes(streamBinPath);
            Assert.Equal(17, savedBytes.Length);
            Assert.Equal(ExpectedAdoStreamBinSha256, Convert.ToHexStringLower(SHA256.HashData(savedBytes)));
            Assert.Null(Call(s, "Close").Failure);
        }
        finally
        {
            Release(s);
        }

        // r-q-ado-misc/stream-text-ops: CP 20127 ASCII high-bit stripping, ReadText(-2) embedded NUL stop, SetEOS, and CopyTo.
        nint st = New(OwnStream);
        nint st2 = New(OwnStream);
        try
        {
            Assert.Null(Call(st, "Open").Failure);
            string payload = "a b\uD834\uDD1Ex\0yL1\r\nL2\r\nL3\n";
            Assert.Null(Call(st, "WriteText", payload, 0).Failure);
            Assert.Equal(40, Get(st, "Size"));
            Assert.Null(Put(st, "LineSeparator", 10).Failure);
            Assert.Null(Put(st, "Position", 0).Failure);
            Assert.Equal(payload, GetText(Call(st, "ReadText", -2)));
            Assert.Equal(40, Get(st, "Position"));
            Assert.Null(Put(st, "Position", 0).Failure);
            Assert.Null(Put(st, "Charset", "ascii").Failure);
            string ascii = GetText(Call(st, "ReadText", -1));
            Assert.Equal(40, ascii.Length);
            Assert.Equal('\u007F', ascii[0]);
            Assert.Equal('~', ascii[1]);

            Assert.Null(Put(st, "Position", 3).Failure);
            Assert.Null(Call(st, "SetEOS").Failure);
            Assert.Equal(3, Get(st, "Size"));
            Assert.Null(Call(st2, "Open").Failure);
            Assert.Null(Put(st, "Position", 0).Failure);
            Assert.Null(Call(st, "CopyTo", new Dispatch(st2)).Failure);
            Assert.Equal(8, Get(st2, "Size"));
            Assert.Null(Call(st, "Close").Failure);
            Assert.Null(Call(st2, "Close").Failure);
        }
        finally
        {
            Release(st2);
            Release(st);
        }

        // r-q-ado-misc/stream-file-ops: binary SaveToFile (1 -> 0x800A0BBC on existing, 2 -> overwrite),
        // LoadFromFile (missing -> 0x800A0BBA), ReadText on binary -> 0x800A0C93, Type put at Position > 0 -> 0x800A0C93.
        string scratchBin = Path.Combine(tempDir, "scratch-st.bin");
        nint sb = New(OwnStream);
        nint sl = New(OwnStream);
        try
        {
            Assert.Null(Put(sb, "Type", 1).Failure);
            Assert.Null(Call(sb, "Open").Failure);
            Assert.Null(Call(sb, "Write", new byte[] { 0x41, 0x00, 0x42 }).Failure);
            Assert.Equal(3, Get(sb, "Size"));
            Assert.Null(Call(sb, "SaveToFile", scratchBin, 1).Failure);
            AssertAdoErr(Call(sb, "SaveToFile", scratchBin, 1), unchecked((int)0x800A0BBC), "ADODB.Stream", 1240644, IidStream);
            Assert.Null(Call(sb, "SaveToFile", scratchBin, 2).Failure);
            Assert.Equal(ExpectedScratchStBinSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(scratchBin))));

            Assert.Null(Put(sl, "Type", 1).Failure);
            Assert.Null(Call(sl, "Open").Failure);
            Assert.Null(Call(sl, "LoadFromFile", scratchBin).Failure);
            Assert.Equal(3, Get(sl, "Size"));
            Assert.Equal(new byte[] { 0x41, 0x00, 0x42 }, Assert.IsType<byte[]>(Call(sl, "Read", 3).Value));
            AssertAdoErr(Call(sl, "ReadText", -1), unchecked((int)0x800A0C93), "ADODB.Stream", 1240646, IidStream);
            Assert.Null(Put(sl, "Position", 2).Failure);
            AssertAdoErr(Put(sl, "Type", 2), unchecked((int)0x800A0C93), "ADODB.Stream", 1240646, IidStream);
            AssertAdoErr(Call(sb, "LoadFromFile", Path.Combine(tempDir, "does-not-exist.bin")), unchecked((int)0x800A0BBA), "ADODB.Stream", 1240642, IidStream);
            Assert.Null(Call(sb, "Close").Failure);
            Assert.Null(Call(sl, "Close").Failure);
        }
        finally
        {
            Release(sl);
            Release(sb);
        }

        // r-q45/stream-read-bounds & stream-write-on-text-and-types
        nint rb = New(OwnStream);
        try
        {
            Assert.Null(Call(rb, "Open").Failure);
            Assert.Null(Call(rb, "WriteText", "hi", 0).Failure);
            Assert.Null(Put(rb, "Position", 0).Failure);
            Assert.Equal("", GetText(Call(rb, "ReadText", 0)));
            Assert.Equal(2, Get(rb, "Position")); // ReadText(0) advances past UTF-16LE BOM
            AssertAdoErr(Call(rb, "ReadText", -5), unchecked((int)0x800A0BB9), "ADODB.Stream", 1240641, IidStream);
            AssertAdoErr(Call(rb, "Write", new byte[] { 1, 2 }), unchecked((int)0x800A0C93), "ADODB.Stream", 1240646, IidStream);
            Assert.Null(Call(rb, "Close").Failure);

            Assert.Null(Put(rb, "Type", 1).Failure);
            Assert.Null(Call(rb, "Open").Failure);
            Assert.Null(Call(rb, "Write", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }).Failure);
            Assert.Null(Put(rb, "Position", 0).Failure);
            Assert.Equal(DBNull.Value, Call(rb, "Read", 0).Value); // VT_NULL
            AssertAdoErr(Call(rb, "Read", -2), unchecked((int)0x800A0BB9), "ADODB.Stream", 1240641, IidStream);
            AssertAdoErr(Call(rb, "Write", "not-bytes"), unchecked((int)0x800A0BB9), "ADODB.Stream", 1240641, IidStream);
            AssertAdoErr(Call(rb, "WriteText", "bad", 0), unchecked((int)0x800A0C93), "ADODB.Stream", 1240646, IidStream);
            AssertAdoErr(Call(rb, "SkipLine"), unchecked((int)0x800A0C93), "ADODB.Stream", 1240646, IidStream);
            Assert.Null(Call(rb, "Close").Failure);
        }
        finally
        {
            Release(rb);
        }
    }

    private static void PersistStreamSaveIntoOpenAndExactAdtgXmlBytes(string dbPath, string tempDir)
    {
        nint cn = OpenAceConnection(dbPath, mode: 1);
        try
        {
            nint rs = New(OwnRecordset);
            try
            {
                Assert.Null(Put(rs, "CursorLocation", 3).Failure);
                Assert.Null(Call(rs, "Open", "SELECT ID, Code, L, S FROM T ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);

                // r-ado-1b/persist-stream-roundtrip: Save into closed Streams -> exact 568B ADTG SHA-256 and exact 2922B XML.
                nint sa = New(OwnStream);
                nint sx = New(OwnStream);
                try
                {
                    Assert.Null(Call(rs, "Save", new Dispatch(sa), 0).Failure);
                    Assert.Equal(1, Get(sa, "State"));
                    Assert.Equal(1, Get(sa, "Type"));
                    Assert.Equal(568, Get(sa, "Size"));
                    Assert.Equal(0, Get(sa, "Position"));
                    byte[] adtgBytes = Assert.IsType<byte[]>(Call(sa, "Read", -1).Value);
                    Assert.Equal(568, adtgBytes.Length);
                    Assert.Equal(ExpectedAdtg568Sha256, Convert.ToHexStringLower(SHA256.HashData(adtgBytes)));

                    Assert.Null(Call(rs, "Save", new Dispatch(sx), 1).Failure);
                    Assert.Equal(1, Get(sx, "State"));
                    Assert.Equal(2, Get(sx, "Type"));
                    Assert.Equal("Unicode", Get(sx, "Charset"));
                    Assert.Equal(2922, Get(sx, "Size"));
                    Assert.Equal(0, Get(sx, "Position"));
                    string xmlText = GetText(Call(sx, "ReadText", -1));
                    Assert.Equal(1460, xmlText.Length);
                    Assert.StartsWith("<xml xmlns:s='uuid:BDC6E3F0-6DA3-11d1-A2A3-00AA00C14882'\r\n\txmlns:dt=", xmlText);
                    Assert.Contains("S='it&#x27;s'", xmlText);

                    // Reopen Recordset from both Streams
                    Assert.Null(Put(sa, "Position", 0).Failure);
                    nint rsa = New(OwnRecordset);
                    try
                    {
                        Assert.Null(Call(rsa, "Open", new Dispatch(sa), Missing, 3, 1, Missing).Failure);
                        Assert.Equal(1, Get(rsa, "State"));
                        Assert.Equal(7, Get(rsa, "RecordCount"));
                        Assert.Null(Call(rsa, "Close").Failure);
                    }
                    finally
                    {
                        Release(rsa);
                    }

                    Assert.Null(Put(sx, "Position", 0).Failure);
                    nint rsx = New(OwnRecordset);
                    try
                    {
                        Assert.Null(Call(rsx, "Open", new Dispatch(sx), Missing, 3, 1, Missing).Failure);
                        Assert.Equal(1, Get(rsx, "State"));
                        Assert.Equal(7, Get(rsx, "RecordCount"));
                        Assert.Null(Call(rsx, "Close").Failure);
                    }
                    finally
                    {
                        Release(rsx);
                    }
                }
                finally
                {
                    Release(sx);
                    Release(sa);
                }

                // r-q45/stream-save-into-open: Save into prefilled open Stream & adModeRead Stream resets from byte 0.
                nint sOpen = New(OwnStream);
                try
                {
                    Assert.Null(Put(sOpen, "Type", 1).Failure);
                    Assert.Null(Put(sOpen, "Mode", 1).Failure); // adModeRead
                    Assert.Null(Call(sOpen, "Open").Failure);
                    Assert.Null(Call(rs, "Save", new Dispatch(sOpen), 0).Failure);
                    Assert.Equal(568, Get(sOpen, "Size"));
                    Assert.Equal(0, Get(sOpen, "Position"));
                    Assert.Null(Call(sOpen, "Close").Failure);
                }
                finally
                {
                    Release(sOpen);
                }

                Assert.Null(Call(rs, "Close").Failure);
            }
            finally
            {
                Release(rs);
            }
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
        _ = tempDir;
    }

    private static void PersistMalformedFilesAndOverwriteFlags(string dbPath, string tempDir)
    {
        // r-ado-1b/persist-malformed-files: all 9 malformed inputs + nonexistent path, checking post-failure State=0, Fields.Count=0, Close->0x800A0E78.
        (string FileName, byte[]? Bytes, int ExpectedScode, string ExpectedFragment)[] malformedCases =
        [
            ("mal-0.dat", [], unchecked((int)0x80004005), "empty"),
            ("mal-1.dat", [0x41], unchecked((int)0x80004005), "empty"),
            ("mal-2.dat", "abc"u8.ToArray(), unchecked((int)0x80004005), "empty"),
            ("mal-3.dat", new byte[512], unchecked((int)0x80004005), "XML or ADTG"),
            ("mal-4.dat", "<xml"u8.ToArray(), unchecked((int)0x80004005), "empty"),
            ("mal-5.dat", [0xEF, 0xBB, 0xBF], unchecked((int)0x80004005), "empty"),
            ("mal-6.dat", "<xml/>"u8.ToArray(), unchecked((int)0x80004005), "incomplete or invalid"),
            ("mal-7.dat", "<?xml version=\"1.0\"?>"u8.ToArray(), unchecked((int)0x80004005), "XML or ADTG"),
            ("mal-8.dat", "<xml xmlns:s='uuid:BDC6E3F0-6DA3-11d1-A2A3-00AA00C14882' xmlns:dt='uuid:C2F41010-65B3-11d1-A29F-00AA00C14882' xmlns:rs='urn:schemas-microsoft-com:rowset' xmlns:z='#RowsetSchema'></xml>"u8.ToArray(), unchecked((int)0x80004005), "incomplete or invalid"),
            ("mal-9-missing.dat", null, unchecked((int)0x80030002), "cannot be found."),
        ];

        foreach (var mc in malformedCases)
        {
            string p = Path.Combine(tempDir, mc.FileName);
            if (mc.Bytes is not null)
            {
                File.WriteAllBytes(p, mc.Bytes);
            }
            nint rBad = New(OwnRecordset);
            try
            {
                Outcome o = Call(rBad, "Open", p, Missing, 3, 4, 256);
                Assert.NotNull(o.Failure);
                Assert.Equal(mc.ExpectedScode, o.Failure!.Value.Scode);
                Assert.Equal("Microsoft OLEDB Persistence Provider", o.Failure.Value.Source);
                Assert.Contains(mc.ExpectedFragment, o.Failure.Value.Description ?? "");
                Assert.Equal(0, Get(rBad, "State"));
                nint flds = (nint)Get(rBad, "Fields")!;
                try
                {
                    Assert.Equal(0, Get(flds, "Count"));
                }
                finally
                {
                    Release(flds);
                }
                AssertAdoErr(Call(rBad, "Close"), unchecked((int)0x800A0E78), "ADODB.Recordset", 1240653, IidRecordset);
            }
            finally
            {
                Release(rBad);
            }
        }

        // F11: unknown <rs:data> child element and invalid XML boolean fail with InvalidXmlMessage.
        string badChildXml = Path.Combine(tempDir, "bad-child.xml");
        File.WriteAllText(
            badChildXml,
            "<xml xmlns:s='uuid:BDC6E3F0-6DA3-11d1-A2A3-00AA00C14882' xmlns:dt='uuid:C2F41010-65B3-11d1-A29F-00AA00C14882' xmlns:rs='urn:schemas-microsoft-com:rowset' xmlns:z='#RowsetSchema'>"
            + "<s:Schema id='RowsetSchema'><s:ElementType name='row' content='eltOnly'><s:AttributeType name='B' rs:number='1'><s:datatype dt:type='boolean' dt:maxLength='2'/></s:AttributeType></s:ElementType></s:Schema>"
            + "<rs:data><rs:bogus/></rs:data></xml>");
        string badBoolXml = Path.Combine(tempDir, "bad-bool.xml");
        File.WriteAllText(
            badBoolXml,
            "<xml xmlns:s='uuid:BDC6E3F0-6DA3-11d1-A2A3-00AA00C14882' xmlns:dt='uuid:C2F41010-65B3-11d1-A29F-00AA00C14882' xmlns:rs='urn:schemas-microsoft-com:rowset' xmlns:z='#RowsetSchema'>"
            + "<s:Schema id='RowsetSchema'><s:ElementType name='row' content='eltOnly'><s:AttributeType name='B' rs:number='1'><s:datatype dt:type='boolean' dt:maxLength='2'/></s:AttributeType></s:ElementType></s:Schema>"
            + "<rs:data><z:row B='maybe'/></rs:data></xml>");
        foreach (string p in new[] { badChildXml, badBoolXml })
        {
            nint rx = New(OwnRecordset);
            try
            {
                Outcome o = Call(rx, "Open", p, Missing, 3, 1, 256);
                Assert.NotNull(o.Failure);
                Assert.Equal(unchecked((int)0x80004005), o.Failure!.Value.Scode);
                Assert.Contains("incomplete or invalid", o.Failure.Value.Description ?? "");
            }
            finally
            {
                Release(rx);
            }
        }

        // r-ado-1b/persist-overwrite-flags & r-fix-wire-ado/persist-save-two-parameter
        string oDat = Path.Combine(tempDir, "o.dat");
        string newXml = Path.Combine(tempDir, "new-xml.dat");
        nint cn = OpenAceConnection(dbPath, mode: 1);
        try
        {
            nint rs = New(OwnRecordset);
            try
            {
                Assert.Null(Put(rs, "CursorLocation", 3).Failure);
                Assert.Null(Call(rs, "Open", "SELECT ID, Code, L, S FROM T ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                Assert.Null(Call(rs, "Save", oDat).Failure);
                Assert.Equal(ExpectedAdtg568Sha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(oDat))));

                // Existing path fails 0x80030050 STG_E_FILEALREADYEXISTS leaving Connection.Errors empty.
                Outcome existsErr = Call(rs, "Save", oDat, 0);
                Assert.NotNull(existsErr.Failure);
                Assert.Equal(unchecked((int)0x80030050), existsErr.Failure!.Value.Scode);
                Assert.Equal(0, ConnectionErrorCount(cn));

                // 3-arg Save(oDat, 0, 1) fails DISP_E_BADPARAMCOUNT (0x8002000E) (r-fix-wire-ado/persist-save-two-parameter).
                Outcome badParamCount = Call(rs, "Save", oDat, 0, 1);
                Assert.NotNull(badParamCount.Failure);
                Assert.Equal(unchecked((int)0x8002000E), badParamCount.Failure!.Value.Scode);

                // Invalid PersistFormat=99 fails 0x800A0BB9 even on existing path.
                AssertAdoErr(Call(rs, "Save", oDat, 99), unchecked((int)0x800A0BB9), "ADODB.Recordset", 1240641, IidRecordset);

                // Save to new XML path then Save() with omitted destination overwrites it in place.
                Assert.Null(Call(rs, "Save", newXml, 1).Failure);
                Assert.Null(Call(rs, "Save").Failure);

                Assert.Null(Call(rs, "Close").Failure);
                AssertAdoErr(Call(rs, "Save", Path.Combine(tempDir, "closed.dat"), 0), unchecked((int)0x800A0E78), "ADODB.Recordset", 1240653, IidRecordset);
            }
            finally
            {
                Release(rs);
            }
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void PersistPendingChangesMsPersistAndReconnect(string ado4Path, string reconnAdtgDb, string reconnXmlDb, string tempDir)
    {
        // 1) r-str-ado-act/ado-persist: Mode=1 (3,3,4) with OLE B, edit T="pending", Save ADTG & XML, reopen + MSPersist.
        string kAdtg = Path.Combine(tempDir, "k.adtg");
        string kPendAdtg = Path.Combine(tempDir, "k-pending.adtg");
        string kPendXml = Path.Combine(tempDir, "k-pending.xml");
        nint cn4 = OpenAceConnection(ado4Path, mode: 1);
        try
        {
            nint rs = New(OwnRecordset);
            try
            {
                Assert.Null(Put(rs, "CursorLocation", 3).Failure);
                Assert.Null(Call(rs, "Open", "SELECT * FROM K ORDER BY ID", new Dispatch(cn4), 3, 4, 1).Failure);
                Assert.Null(Call(rs, "Save", kAdtg, 0).Failure);

                nint flds = (nint)Get(rs, "Fields")!;
                nint ft = (nint)Get(flds, "Item", "T")!;
                nint fbLive = (nint)Get(flds, "Item", "B")!;
                try
                {
                    // F6: live (3,3,4) SELECT guards OLE Value/OriginalValue/UnderlyingValue with NotModelled.AdoSqlBinaryCursor.
                    AssertNotModelled(Invoke(fbLive, "Value", 2), "NotModelled.AdoSqlBinaryCursor");
                    AssertNotModelled(Invoke(fbLive, "OriginalValue", 2), "NotModelled.AdoSqlBinaryCursor");
                    AssertNotModelled(Invoke(fbLive, "UnderlyingValue", 2), "NotModelled.AdoSqlBinaryCursor");

                    Assert.Null(Put(ft, "Value", "pending").Failure);
                }
                finally
                {
                    Release(fbLive);
                    Release(ft);
                    Release(flds);
                }
                Assert.Null(Call(rs, "Update").Failure);
                Assert.Equal(2, Get(rs, "Status"));
                Assert.Null(Call(rs, "Save", kPendAdtg, 0).Failure);
                Assert.Null(Call(rs, "Save", kPendXml, 1).Failure);
                Assert.Null(Call(rs, "Close").Failure);
            }
            finally
            {
                Release(rs);
            }
        }
        finally
        {
            Assert.Null(Call(cn4, "Close").Failure);
            Release(cn4);
        }

        // Reopen k-pending.adtg and verify OLE bytes and pending Status=2.
        nint ra = New(OwnRecordset);
        try
        {
            Assert.Null(Call(ra, "Open", kPendAdtg, Missing, 3, 4, 256).Failure);
            Assert.Equal(4, Get(ra, "RecordCount"));
            Assert.Equal(2, Get(ra, "Status"));
            nint flds = (nint)Get(ra, "Fields")!;
            nint fb = (nint)Get(flds, "Item", "B")!;
            nint ft = (nint)Get(flds, "Item", "T")!;
            try
            {
                Assert.Equal("pending", Get(ft, "Value"));
                Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0xFF, 0x00 }, Assert.IsType<byte[]>(Get(fb, "Value")));
            }
            finally
            {
                Release(ft);
                Release(fb);
                Release(flds);
            }
            Assert.Null(Call(ra, "Close").Failure);
        }
        finally
        {
            Release(ra);
        }

        // MSPersist Connection route (r-str-ado-act/ado-persist #59-77)
        nint cm = New(OwnConnection);
        try
        {
            Assert.Null(Put(cm, "Provider", "MSPersist").Failure);
            Assert.Null(Call(cm, "Open").Failure);
            Assert.Equal(1, Get(cm, "State"));
            Assert.Equal("MSPersist.1", Get(cm, "Provider"));

            nint rm = New(OwnRecordset);
            try
            {
                Assert.Null(Call(rm, "Open", kAdtg, new Dispatch(cm), 3, 4, 256).Failure);
                Assert.Equal(1, Get(rm, "State"));
                Assert.Equal(4, Get(rm, "RecordCount"));
                Assert.Null(Call(rm, "Close").Failure);
            }
            finally
            {
                Release(rm);
            }

            nint re = (nint)Call(cm, "Execute", kAdtg).Value!;
            try
            {
                Assert.Equal(1, Get(re, "State"));
                Assert.Equal(4, Get(re, "RecordCount"));
            }
            finally
            {
                Release(re);
            }

            Assert.Equal(0, ConnectionErrorCount(cm));

            // F1: MSPersist guards on DefaultDatabase and Command binding.
            AssertNotModelled(Invoke(cm, "DefaultDatabase", 2), "NotModelled.AdoMsPersistDefaultDatabase");
            AssertNotModelled(Call(cm, "Execute", kAdtg, 0), "NotModelled.AdoMsPersistExecuteRecordsAffected");
            nint cmdMs = New(OwnCommand);
            try
            {
                AssertNotModelled(Invoke(cmdMs, "ActiveConnection", 8, new Dispatch(cm)), "NotModelled.AdoMsPersistCommand");
            }
            finally
            {
                Release(cmdMs);
            }

            // F4: cm.Execute(missingPath) fails 0x80030002 and populates cm.Errors with NativeError 130 (r-str-ado-act/ado-persist #73-84).
            string missingMsPath = Path.Combine(tempDir, "no-such-mspersist.adtg");
            Outcome missingExec = Call(cm, "Execute", missingMsPath);
            Assert.NotNull(missingExec.Failure);
            Assert.Equal(unchecked((int)0x80030002), missingExec.Failure!.Value.Scode);
            Assert.Equal(1, ConnectionErrorCount(cm));
            nint cmErrs = (nint)Get(cm, "Errors")!;
            nint cmErr0 = (nint)Get(cmErrs, "Item", 0)!;
            try
            {
                Assert.Equal(unchecked((int)0x80030002), Get(cmErr0, "Number"));
                Assert.Equal("Microsoft OLEDB Persistence Provider", Get(cmErr0, "Source"));
                Assert.Equal($"File {missingMsPath} cannot be found.", Get(cmErr0, "Description"));
                Assert.True(string.IsNullOrEmpty((string?)Get(cmErr0, "SQLState")));
                Assert.Equal(130, Get(cmErr0, "NativeError"));
            }
            finally
            {
                Release(cmErr0);
                Release(cmErrs);
            }

            Assert.Null(Call(cm, "Close").Failure);
            Assert.Equal(0, Get(cm, "State"));
        }
        finally
        {
            Release(cm);
        }

        // 2) r-ado-1b/persist-reconnect-adtg & persist-reconnect-xml
        VerifyPersistReconnect(reconnAdtgDb, Path.Combine(tempDir, "reconn.adtg"), persistFormat: 0);
        VerifyPersistReconnect(reconnXmlDb, Path.Combine(tempDir, "reconn.xml"), persistFormat: 1);
    }

    private static void VerifyPersistReconnect(string dbPath, string savePath, int persistFormat)
    {
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            try
            {
                Assert.Null(Put(rs, "CursorLocation", 3).Failure);
                Assert.Null(Call(rs, "Open", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID", new Dispatch(cn), 3, 4, 1).Failure);
                nint flds = (nint)Get(rs, "Fields")!;
                nint fid = (nint)Get(flds, "Item", "ID")!;
                nint fc = (nint)Get(flds, "Item", "Code")!;
                nint fs = (nint)Get(flds, "Item", "S")!;
                try
                {
                    Assert.Null(Put(fs, "Value", "pr1").Failure);
                    Assert.Null(Call(rs, "Update").Failure);
                    Assert.Null(Call(rs, "MoveNext").Failure);
                    Assert.Null(Call(rs, "Delete").Failure);
                    Assert.Null(Call(rs, "AddNew").Failure);
                    Assert.Null(Put(fid, "Value", 40).Failure);
                    Assert.Null(Put(fc, "Value", "w40").Failure);
                    Assert.Null(Call(rs, "Update").Failure);
                }
                finally
                {
                    Release(fs);
                    Release(fc);
                    Release(fid);
                    Release(flds);
                }
                Assert.Null(Call(rs, "Save", savePath, persistFormat).Failure);
                Assert.Null(Call(rs, "Close").Failure);
            }
            finally
            {
                Release(rs);
            }

            // Reopen disconnected, verify OriginalValue/UnderlyingValue (F7), reconnect ActiveConnection, set Unique Table, UpdateBatch(3).
            nint r2 = New(OwnRecordset);
            try
            {
                Assert.Null(Call(r2, "Open", savePath, Missing, 3, 4, 256).Failure);
                Assert.Equal(12, Get(r2, "RecordCount"));
                Assert.Equal(2, Get(r2, "Status")); // Row 1 modified

                // Verify modified row (ID=1) has Value="pr1" and OriginalValue != "pr1", and new row (ID=40) has DBNull OriginalValue/UnderlyingValue (F7).
                nint flds2 = (nint)Get(r2, "Fields")!;
                nint fid2 = (nint)Get(flds2, "Item", "ID")!;
                nint fs2 = (nint)Get(flds2, "Item", "S")!;
                try
                {
                    Assert.Equal("pr1", Get(fs2, "Value"));
                    Assert.NotEqual("pr1", Get(fs2, "OriginalValue"));
                    Assert.Null(Call(r2, "MoveLast").Failure);
                    Assert.Equal(40, Get(fid2, "Value"));
                    Assert.Equal(1, Get(r2, "Status")); // adRecNew
                    Assert.Equal(DBNull.Value, Get(fid2, "OriginalValue"));
                    Assert.Equal(DBNull.Value, Get(fid2, "UnderlyingValue"));
                    Assert.Null(Call(r2, "MoveFirst").Failure);
                }
                finally
                {
                    Release(fs2);
                    Release(fid2);
                    Release(flds2);
                }

                Assert.Null(Invoke(r2, "ActiveConnection", 8, new Dispatch(cn)).Failure);
                Assert.Null(Call(r2, "UpdateBatch", 3).Failure);
                Assert.Equal(0, ConnectionErrorCount(cn));

                nint props = (nint)Get(r2, "Properties")!;
                nint ut = (nint)Get(props, "Item", "Unique Table")!;
                try
                {
                    Assert.Equal("", Get(ut, "Value"));
                    Assert.Null(Put(ut, "Value", "W").Failure);
                    Assert.Equal("W", Get(ut, "Value"));
                }
                finally
                {
                    Release(ut);
                    Release(props);
                }
                Assert.Null(Call(r2, "UpdateBatch", 3).Failure);
                Assert.Equal(0, ConnectionErrorCount(cn));
                Assert.Null(Call(r2, "Close").Failure);
            }
            finally
            {
                Release(r2);
            }

            // Verify DB now has ID=1 S="pr1", ID=2 deleted, ID=40 Code="w40".
            nint chk = New(OwnRecordset);
            try
            {
                Assert.Null(Put(chk, "CursorLocation", 3).Failure);
                Assert.Null(Call(chk, "Open", "SELECT ID, Code, S FROM W WHERE ID IN (1, 2, 40) ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                Assert.Equal(2, Get(chk, "RecordCount"));
                nint flds = (nint)Get(chk, "Fields")!;
                nint fid = (nint)Get(flds, "Item", "ID")!;
                nint fc = (nint)Get(flds, "Item", "Code")!;
                nint fs = (nint)Get(flds, "Item", "S")!;
                try
                {
                    Assert.Equal(1, Get(fid, "Value"));
                    Assert.Equal("pr1", Get(fs, "Value"));
                    Assert.Null(Call(chk, "MoveNext").Failure);
                    Assert.Equal(40, Get(fid, "Value"));
                    Assert.Equal("w40", Get(fc, "Value"));
                }
                finally
                {
                    Release(fs);
                    Release(fc);
                    Release(fid);
                    Release(flds);
                }
                Assert.Null(Call(chk, "Close").Failure);
            }
            finally
            {
                Release(chk);
            }
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void UnrecordedEdgesRefusedByName(string dbPath, string tempDir)
    {
        // Unrecorded Stream charsets, URL Open, LineSeparator on closed stream, non-text Open credentials,
        // mid-stream WriteText overwrite, valid UTF-8 ReadText, multi-line ReadText(-2) without prior NUL, SkipLine before EOS, non-utf8 text SaveToFile.
        nint st = New(OwnStream);
        try
        {
            AssertNotModelled(Put(st, "Charset", "iso-8859-1"), "NotModelled.AdoStreamCharset");
            AssertNotModelled(Put(st, "LineSeparator", 10), "NotModelled.AdoStreamLineSeparatorContext");
            AssertNotModelled(Call(st, "Open", "file://temp.bin"), "NotModelled.AdoStreamOpenSource");
            AssertNotModelled(Call(st, "Open", Missing, 0, -1, 123), "NotModelled.AdoStreamOpenCredentials");

            Assert.Null(Call(st, "Open").Failure);
            Assert.Null(Call(st, "WriteText", "line1\nline2", 0).Failure);
            Assert.Null(Put(st, "LineSeparator", 10).Failure);
            Assert.Null(Put(st, "Position", 0).Failure);
            AssertNotModelled(Call(st, "WriteText", "ov", 0), "NotModelled.AdoStreamWriteTextOverwrite");
            AssertNotModelled(Call(st, "ReadText", -2), "NotModelled.AdoStreamReadLineWithSeparator");
            AssertNotModelled(Call(st, "SkipLine"), "NotModelled.AdoStreamSkipLine");
            AssertNotModelled(Call(st, "SaveToFile", Path.Combine(tempDir, "unicode-text.bin"), 2), "NotModelled.AdoStreamSaveTextCharset");
            nint dest = New(OwnStream);
            try
            {
                Assert.Null(Call(dest, "Open").Failure);
                AssertNotModelled(Call(st, "CopyTo", new Dispatch(dest), 4), "NotModelled.AdoStreamCopyToCount");
                Assert.Null(Call(dest, "Close").Failure);
            }
            finally
            {
                Release(dest);
            }
            Assert.Null(Call(st, "Close").Failure);

            // F11: valid UTF-8 bytes under Charset="utf-8" refuse ReadText by name (only invalid UTF-8 -> 0x80070057 is recorded).
            Assert.Null(Put(st, "Type", 1).Failure);
            Assert.Null(Call(st, "Open").Failure);
            Assert.Null(Call(st, "Write", "hello"u8.ToArray()).Failure);
            Assert.Null(Put(st, "Position", 0).Failure);
            Assert.Null(Put(st, "Type", 2).Failure);
            Assert.Null(Put(st, "Charset", "utf-8").Failure);
            AssertNotModelled(Call(st, "ReadText", -1), "NotModelled.AdoStreamReadValidUtf8");
            Assert.Null(Call(st, "Close").Failure);

            // F11: Recordset.Open from closed Stream refuses by name.
            nint rsClosedStream = New(OwnRecordset);
            try
            {
                AssertNotModelled(Call(rsClosedStream, "Open", new Dispatch(st), Missing, 3, 1, Missing), "NotModelled.AdoRecordsetOpenClosedStream");
            }
            finally
            {
                Release(rsClosedStream);
            }
        }
        finally
        {
            Release(st);
        }

        // Unrecorded Recordset.Save on server cursor, Execute-returned client cursor (F10), empty path (F9),
        // omitted destination without prior file Save, and persisted Open with LockType=3 (F11).
        nint cn = OpenAceConnection(dbPath, mode: 1);
        try
        {
            nint srvRs = New(OwnRecordset);
            try
            {
                Assert.Null(Call(srvRs, "Open", "SELECT ID FROM T ORDER BY ID", new Dispatch(cn), 0, 1, 1).Failure);
                AssertNotModelled(Call(srvRs, "Save", Path.Combine(tempDir, "srv.adtg"), 0), "NotModelled.AdoRecordsetSaveServerCursor");
                Assert.Null(Call(srvRs, "Close").Failure);
            }
            finally
            {
                Release(srvRs);
            }

            string refFile = Path.Combine(tempDir, "ref-lock.adtg");
            nint cliRs = New(OwnRecordset);
            try
            {
                Assert.Null(Put(cliRs, "CursorLocation", 3).Failure);
                Assert.Null(Call(cliRs, "Open", "SELECT ID FROM T ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                AssertNotModelled(Call(cliRs, "Save"), "NotModelled.AdoRecordsetSaveNoPriorDestination");
                AssertNotModelled(Call(cliRs, "Save", "", 0), "NotModelled.AdoRecordsetSaveEmptyPath");
                Assert.Null(Call(cliRs, "Save", refFile, 0).Failure);
                Assert.Null(Call(cliRs, "Close").Failure);
            }
            finally
            {
                Release(cliRs);
            }

            nint rsLock3 = New(OwnRecordset);
            try
            {
                AssertNotModelled(Call(rsLock3, "Open", refFile, Missing, 3, 3, 256), "NotModelled.AdoPersistLockType");
            }
            finally
            {
                Release(rsLock3);
            }

            // F10: Connection.Execute client Recordset refuses Save.
            nint cnClient = OpenAceConnection(dbPath, mode: 1);
            try
            {
                Assert.Null(Put(cnClient, "CursorLocation", 3).Failure);
                nint execRs = (nint)Call(cnClient, "Execute", "SELECT ID FROM T ORDER BY ID").Value!;
                try
                {
                    AssertNotModelled(Call(execRs, "Save", Path.Combine(tempDir, "exec.adtg"), 0), "NotModelled.AdoRecordsetSaveFromExecute");
                    Assert.Null(Call(execRs, "Close").Failure);
                }
                finally
                {
                    Release(execRs);
                }
            }
            finally
            {
                Assert.Null(Call(cnClient, "Close").Failure);
                Release(cnClient);
            }
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    [Fact]
    public void Persisted_ADTG_and_XML_codecs_roundtrip_all_15_ADO_types_and_pending_states()
    {
        // F2, F5, F7, F11, F12: direct roundtrip of all 15 persist-roundtrip-typed ADO types across Clean/Modified/New/Deleted.
        PersistedColumn[] cols =
        [
            new("ID", 3, 4, 10, 255, 16, "AllTypes", "ID", true),
            new("C_Txt", 202, 50, 255, 255, 96, "AllTypes", "C_Txt", false),
            new("C_I2", 2, 2, 5, 255, 112, "AllTypes", "C_I2", false),
            new("C_UI1", 17, 1, 3, 255, 112, "AllTypes", "C_UI1", false),
            new("C_R4", 4, 4, 7, 255, 112, "AllTypes", "C_R4", false),
            new("C_R8", 5, 8, 15, 255, 112, "AllTypes", "C_R8", false),
            new("C_Cur", 6, 8, 19, 255, 112, "AllTypes", "C_Cur", false),
            new("C_Date", 7, 8, 255, 255, 112, "AllTypes", "C_Date", false),
            new("C_Bool", 11, 2, 255, 255, 112, "AllTypes", "C_Bool", false),
            new("C_Dec", 131, 19, 18, 4, 112, "AllTypes", "C_Dec", false),
            new("C_Guid", 72, 16, 255, 255, 112, "AllTypes", "C_Guid", false),
            new("C_Memo", 203, 536870910, 255, 255, 224, "AllTypes", "C_Memo", false),
            new("C_Ole", 205, 536870910, 255, 255, 224, "AllTypes", "C_Ole", false),
            new("C_Bin", 204, 16, 255, 255, 112, "AllTypes", "C_Bin", false),
            new("C_Null", 3, 4, 10, 255, 112, "AllTypes", "C_Null", false),
        ];

        static OwnedVariant MakeBytes(params byte[] data)
        {
            nint psa = SafeArrays.Create(VarType.UI1, (0, data.Length));
            if (data.Length > 0)
            {
                data.CopyTo(new Span<byte>(SafeArrays.ElementPointer(psa, 0), data.Length));
            }
            return OwnedVariant.TakeArray(psa, VarType.UI1);
        }

        static OwnedVariant[] MakeRow(int id, string txt, short i2, byte ui1, bool flag) =>
        [
            OwnedVariant.FromInt32(id),
            OwnedVariant.FromText(txt),
            OwnedVariant.FromInt16(i2),
            OwnedVariant.FromByte(ui1),
            OwnedVariant.FromSingle(1.5f),
            OwnedVariant.FromDouble(3.25),
            OwnedVariant.FromCurrency(123456), // 12.3456
            OwnedVariant.FromDate(new DateTime(2026, 6, 15, 12, 30, 0).ToOADate()),
            OwnedVariant.FromBoolean(flag),
            OwnedVariant.FromDecimal(DecimalValue.FromDecimal(987.6543m)),
            OwnedVariant.FromText("{12345678-1234-1234-1234-1234567890AB}"),
            OwnedVariant.FromText("memo line 1\r\nline 2"),
            MakeBytes(0x00, 0x01, 0xFF),
            MakeBytes(0xDE, 0xAD),
            OwnedVariant.Null(),
        ];

        bool[] modMask = new bool[cols.Length];
        modMask[1] = true;
        var rows = new List<PersistedRow>
        {
            new(ClientRowState.Clean, MakeRow(1, "clean", -12, 7, true), MakeRow(1, "clean", -12, 7, true), new bool[cols.Length], 0),
            new(ClientRowState.Modified, MakeRow(2, "after", 99, 8, false), MakeRow(2, "before", 99, 8, false), modMask, 1),
            new(ClientRowState.New, MakeRow(3, "inserted", 5, 2, true), AdoAdtgCodec.CreateNullVariants(cols.Length), new bool[cols.Length], 2),
            new(ClientRowState.Deleted, MakeRow(4, "deleted", 1, 1, false), MakeRow(4, "deleted", 1, 1, false), new bool[cols.Length], 3),
        };

        using var input = new PersistedRecordsetData(cols, rows, "AllTypes", isUpdatable: true);

        // ADTG roundtrip + verify MS-ADTG row tokens (F5: 0x08 Insert, 0x09 Delete, 0x0A Change)
        byte[] adtg = AdoAdtgCodec.Encode(input, out Failure? adtgEncFail);
        Assert.Null(adtgEncFail);
        Assert.True(AdoAdtgCodec.TryDecode(adtg, out PersistedRecordsetData? fromAdtg, out Failure? adtgDecFail));
        Assert.Null(adtgDecFail);
        Assert.NotNull(fromAdtg);
        using (fromAdtg)
        {
            Assert.Equal(15, fromAdtg.Columns.Length);
            Assert.Equal(4, fromAdtg.Rows.Count);
            Assert.Equal(ClientRowState.Clean, fromAdtg.Rows[0].State);
            Assert.Equal(ClientRowState.Modified, fromAdtg.Rows[1].State);
            Assert.True(fromAdtg.Rows[1].ModifiedColumns[1]);
            Assert.False(fromAdtg.Rows[1].ModifiedColumns[2]);
            Assert.Equal(ClientRowState.New, fromAdtg.Rows[2].State);
            Assert.All(fromAdtg.Rows[2].Original, v => Assert.Equal(VarType.Null, v.Type)); // F7
            Assert.Equal(ClientRowState.Deleted, fromAdtg.Rows[3].State);
        }

        // XML roundtrip (including F2 narrow-scalar diff detection and F7 null Original on New)
        string xml = AdoXmlPersistCodec.Encode(input, out Failure? xmlEncFail);
        Assert.Null(xmlEncFail);
        byte[] xmlBytes = System.Text.Encoding.UTF8.GetBytes(xml);
        Assert.True(AdoXmlPersistCodec.TryDecode(xmlBytes, out PersistedRecordsetData? fromXml, out Failure? xmlDecFail));
        Assert.Null(xmlDecFail);
        Assert.NotNull(fromXml);
        using (fromXml)
        {
            Assert.Equal(15, fromXml.Columns.Length);
            Assert.Equal(4, fromXml.Rows.Count);
            Assert.Equal(ClientRowState.Clean, fromXml.Rows[0].State);
            Assert.Equal(ClientRowState.Modified, fromXml.Rows[1].State);
            Assert.True(fromXml.Rows[1].ModifiedColumns[1]);
            Assert.False(fromXml.Rows[1].ModifiedColumns[2]); // I2 equal (F2)
            Assert.False(fromXml.Rows[1].ModifiedColumns[3]); // UI1 equal (F2)
            Assert.False(fromXml.Rows[1].ModifiedColumns[8]); // Bool equal (F2)
            Assert.Equal(ClientRowState.New, fromXml.Rows[2].State);
            Assert.All(fromXml.Rows[2].Original, v => Assert.Equal(VarType.Null, v.Type)); // F7
            Assert.Equal(ClientRowState.Deleted, fromXml.Rows[3].State);
        }

        // F11: non-NCName column name refused by XML encoder
        PersistedColumn[] badCol = [new("bad col", 3, 4, 10, 255, 16, null, null, false)];
        using var badInput = new PersistedRecordsetData(badCol, [], null, isUpdatable: false);
        _ = AdoXmlPersistCodec.Encode(badInput, out Failure? badNameFail);
        Assert.NotNull(badNameFail);
        Assert.Contains("NotModelled.AdoPersistXmlColumnName", badNameFail!.Description ?? "");
    }

    private static string GetText(Outcome outcome)
    {
        Assert.Null(outcome.Failure);
        return Assert.IsType<string>(outcome.Value);
    }

    private static void AssertAdoErr(Outcome outcome, int scode, string source, uint helpContext, Guid iid)
    {
        Assert.NotNull(outcome.Failure);
        Assert.Equal(scode, outcome.Failure!.Value.Scode);
        Assert.Equal(source, outcome.Failure.Value.Source);
        Assert.Equal(helpContext, outcome.Failure.Value.HelpContext);
        Assert.Equal(iid, outcome.Failure.Value.InfoGuid);
    }

    private static void AssertNotModelled(Outcome outcome, string expectedToken)
    {
        Assert.NotNull(outcome.Failure);
        Assert.Equal(unchecked((int)0x80004001), outcome.Failure!.Value.Scode);
        Assert.Contains(expectedToken, outcome.Failure.Value.Description ?? "");
    }

    private static int ConnectionErrorCount(nint cn)
    {
        nint errs = (nint)Get(cn, "Errors")!;
        try
        {
            return (int)Get(errs, "Count")!;
        }
        finally
        {
            Release(errs);
        }
    }
}
