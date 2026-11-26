using System.Security.Cryptography;
using CalcDb.Com.Com;
using CalcDb.Evidence;
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
        // r-ado-1b/persist-malformed-files
        string emptyPath = Path.Combine(tempDir, "empty.dat");
        string zerosPath = Path.Combine(tempDir, "zeros.dat");
        string badXmlPath = Path.Combine(tempDir, "badxml.dat");
        string missingPath = Path.Combine(tempDir, "missing.dat");
        File.WriteAllBytes(emptyPath, []);
        File.WriteAllBytes(zerosPath, new byte[512]);
        File.WriteAllText(badXmlPath, "<xml/>");

        nint rBad = New(OwnRecordset);
        try
        {
            Outcome oEmpty = Call(rBad, "Open", emptyPath, Missing, 3, 1, 256);
            Assert.Equal(unchecked((int)0x80004005), oEmpty.Failure!.Value.Scode);
            Assert.Equal("Microsoft OLEDB Persistence Provider", oEmpty.Failure.Value.Source);
            Assert.Contains("empty", oEmpty.Failure.Value.Description);

            Outcome oZeros = Call(rBad, "Open", zerosPath, Missing, 3, 1, 256);
            Assert.Equal(unchecked((int)0x80004005), oZeros.Failure!.Value.Scode);
            Assert.Contains("XML or ADTG", oZeros.Failure.Value.Description);

            Outcome oBadXml = Call(rBad, "Open", badXmlPath, Missing, 3, 1, 256);
            Assert.Equal(unchecked((int)0x80004005), oBadXml.Failure!.Value.Scode);
            Assert.Contains("incomplete or invalid", oBadXml.Failure.Value.Description);

            Outcome oMissing = Call(rBad, "Open", missingPath, Missing, 3, 1, 256);
            Assert.Equal(unchecked((int)0x80030002), oMissing.Failure!.Value.Scode);
            Assert.Equal($"File {missingPath} cannot be found.", oMissing.Failure.Value.Description);
        }
        finally
        {
            Release(rBad);
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
        // 1) r-str-ado-act/ado-persist: (3,3,4) with OLE B, edit T="pending", Save ADTG & XML, reopen + MSPersist.
        string kAdtg = Path.Combine(tempDir, "k.adtg");
        string kPendAdtg = Path.Combine(tempDir, "k-pending.adtg");
        string kPendXml = Path.Combine(tempDir, "k-pending.xml");
        nint cn4 = OpenAceConnection(ado4Path);
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
                try
                {
                    Assert.Null(Put(ft, "Value", "pending").Failure);
                }
                finally
                {
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

            // Reopen disconnected, reconnect ActiveConnection, set Unique Table, UpdateBatch(3).
            nint r2 = New(OwnRecordset);
            try
            {
                Assert.Null(Call(r2, "Open", savePath, Missing, 3, 4, 256).Failure);
                Assert.Equal(12, Get(r2, "RecordCount"));
                Assert.Equal(2, Get(r2, "Status")); // Row 1 modified
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
        // Unrecorded Stream charsets, URL Open, multi-line ReadText(-2) without prior NUL, SkipLine before EOS, non-utf8 text SaveToFile.
        nint st = New(OwnStream);
        try
        {
            AssertNotModelled(Put(st, "Charset", "iso-8859-1"), "NotModelled.AdoStreamCharset");
            AssertNotModelled(Call(st, "Open", "file://temp.bin"), "NotModelled.AdoStreamOpenSource");

            Assert.Null(Call(st, "Open").Failure);
            Assert.Null(Call(st, "WriteText", "line1\nline2", 0).Failure);
            Assert.Null(Put(st, "LineSeparator", 10).Failure);
            Assert.Null(Put(st, "Position", 0).Failure);
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
        }
        finally
        {
            Release(st);
        }

        // Unrecorded Recordset.Save on server cursor and omitted destination without prior file Save.
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

            nint cliRs = New(OwnRecordset);
            try
            {
                Assert.Null(Put(cliRs, "CursorLocation", 3).Failure);
                Assert.Null(Call(cliRs, "Open", "SELECT ID FROM T ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
                AssertNotModelled(Call(cliRs, "Save"), "NotModelled.AdoRecordsetSaveNoPriorDestination");
                Assert.Null(Call(cliRs, "Close").Failure);
            }
            finally
            {
                Release(cliRs);
            }
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
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
