using CalcDb.Com.Com;
using CalcDb.Evidence;
using static CalcDb.Com.Tests.AdoTestHarness;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-09 / wzq4:
/// exercises ADO client-cursor batch editing (<c>(3, 3, 4)</c> <c>adLockBatchOptimistic</c>) and
/// immediate optimistic client parity (<c>(3, 3, 3)</c> <c>adLockOptimistic</c>) against the published
/// <c>CalcDb.dll</c> under <c>CalcDb.Own.manifest</c> on both ACCDB and Jet 4 MDB, checking every rule
/// against retained <c>r-q48-ado-batch-edits</c>, <c>r-ado-1b</c>, <c>r-q-ado-misc</c>, and
/// <c>r-q2-ado-misc</c> records and verifying zero native ACE/ADO DLLs load.
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoClientBatchEditTests
{
    private const string AccdbFixture = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7"; // r-ado-1b-main.accdb
    private const string MdbFixture = "4a447e7517e51e0ed2b734fd8a20d32ab22e83f427667a4f0e8fb2ff0b544bf7";   // r-ado-1b-mdb.mdb

    [Fact]
    public void CalcDb_ADO_client_batch_and_immediate_client_edits_match_retained_ACE_records()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");

        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string tempDir = Path.Combine(Path.GetTempPath(), $"calcdb-ado-wzq4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));

                ClientPropertiesInventoryAndValidation(CopyFixture(store, AccdbFixture, tempDir, "props.accdb"));
                MarshalOptionsLifecycle(CopyFixture(store, AccdbFixture, tempDir, "mo.accdb"));
                MarshalOptionsLifecycle(CopyFixture(store, MdbFixture, tempDir, "mo.mdb"));
                DeferredVsImmediateAndAddNewChannels(CopyFixture(store, AccdbFixture, tempDir, "def-imm.accdb"), isMdb: false);
                DeferredVsImmediateAndAddNewChannels(CopyFixture(store, MdbFixture, tempDir, "def-imm.mdb"), isMdb: true);
                DeleteCancelUpdateBatchScopes(CopyFixture(store, AccdbFixture, tempDir, "scopes.accdb"), isMdb: false);
                DeleteCancelUpdateBatchScopes(CopyFixture(store, MdbFixture, tempDir, "scopes.mdb"), isMdb: true);
                FilterGroupsSortFindCloneRequery(CopyFixture(store, AccdbFixture, tempDir, "views.accdb"));
                ConflictsUpdateCriteriaAndConstraints(CopyFixture(store, AccdbFixture, tempDir, "conf.accdb"), isMdb: false);
                ConflictsUpdateCriteriaAndConstraints(CopyFixture(store, MdbFixture, tempDir, "conf.mdb"), isMdb: true);
                ResyncTriggerAndUnderlyingVsAll(CopyFixture(store, AccdbFixture, tempDir, "resync.accdb"), isMdb: false);
                ResyncTriggerAndUnderlyingVsAll(CopyFixture(store, MdbFixture, tempDir, "resync.mdb"), isMdb: true);
                TransactionsWrappingUpdateBatch(CopyFixture(store, AccdbFixture, tempDir, "txn.accdb"), isMdb: false);
                TransactionsWrappingUpdateBatch(CopyFixture(store, MdbFixture, tempDir, "txn.mdb"), isMdb: true);
                DisconnectActiveConnectionAndReadOnlyMode(CopyFixture(store, AccdbFixture, tempDir, "disc.accdb"));

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

    private static void ClientPropertiesInventoryAndValidation(string dbPath)
    {
        // r-q48-ado-batch-edits/client-props-inventory-accdb:
        // 103 properties on (3,3,4) and (3,3,3); Update Criteria 0..3; Update Resync 0..15;
        // out-of-range integer -> 0x80040E21 + 2 Connection.Errors; non-integer -> 0x800A0D5D + 0 Errors;
        // Status at BOF/EOF -> 0x800A0BCD.
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 3).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID", new Dispatch(cn), 3, 4, 1).Failure);

            nint props = (nint)Get(rs, "Properties")!;
            Assert.Equal(103, Get(props, "Count"));

            nint pUc = (nint)Get(props, "Item", "Update Criteria")!;
            Assert.Equal(2, Get(pUc, "Value"));
            AssertError(Put(pUc, "Value", -1), unchecked((int)0x80040E21), "ADODB.Property", expectedHelpContext: 1240640);
            nint errs = (nint)Get(cn, "Errors")!;
            Assert.Equal(2, Get(errs, "Count"));
            nint e0 = (nint)Get(errs, "Item", 0)!;
            Assert.Equal("Microsoft Cursor Engine", Get(e0, "Source"));
            Release(e0);
            Assert.Null(Call(errs, "Clear").Failure);

            AssertError(Put(pUc, "Value", "bad"), unchecked((int)0x800A0D5D), "ADODB.Property", expectedHelpContext: 1240652);
            Assert.Equal(0, Get(errs, "Count"));
            Release(pUc);

            nint pUr = (nint)Get(props, "Item", "Update Resync")!;
            Assert.Equal(1, Get(pUr, "Value"));
            Assert.Null(Put(pUr, "Value", 3).Failure);
            Assert.Equal(3, Get(pUr, "Value"));
            AssertError(Put(pUr, "Value", 16), unchecked((int)0x80040E21), "ADODB.Property", expectedHelpContext: 1240640);
            Assert.Equal(2, Get(errs, "Count"));
            Assert.Null(Call(errs, "Clear").Failure);
            Release(pUr);
            Release(errs);
            Release(props);

            // BOF / EOF Status -> 0x800A0BCD
            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Equal(8, Get(rs, "Status"));
            Assert.Null(Call(rs, "MovePrevious").Failure);
            Assert.Equal(true, Get(rs, "BOF"));
            AssertError(TryGet(rs, "Status"), unchecked((int)0x800A0BCD), "ADODB.Recordset");
            Assert.Null(Call(rs, "MoveLast").Failure);
            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Equal(true, Get(rs, "EOF"));
            AssertError(TryGet(rs, "Status"), unchecked((int)0x800A0BCD), "ADODB.Recordset");
            AssertError(TryGet(rs, "EditMode"), unchecked((int)0x800A0BCD), "ADODB.Recordset");
            AssertError(Call(rs, "CancelUpdate"), unchecked((int)0x800A0BCD), "ADODB.Recordset");

            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);

            // Client (3,3,3) also exposes the 103 dynamic properties.
            nint rso = New(OwnRecordset);
            Assert.Null(Put(rso, "CursorLocation", 3).Failure);
            Assert.Null(Call(rso, "Open", "SELECT ID, S FROM W ORDER BY ID", new Dispatch(cn), 3, 3, 1).Failure);
            nint propsO = (nint)Get(rso, "Properties")!;
            Assert.Equal(103, Get(propsO, "Count"));
            Release(propsO);
            Assert.Null(Call(rso, "Close").Failure);
            Release(rso);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void MarshalOptionsLifecycle(string dbPath)
    {
        // r-q48-ado-batch-edits/client-marshal-options-accdb, client-marshal-options-mdb:
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Equal(0, Get(rs, "MarshalOptions"));
            AssertError(Put(rs, "MarshalOptions", 1), unchecked((int)0x800A0E78), "ADODB.Recordset");

            Assert.Null(Put(rs, "CursorLocation", 3).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID", new Dispatch(cn), 3, 4, 1).Failure);
            Assert.Equal(0, Get(rs, "MarshalOptions"));
            Assert.Null(Put(rs, "MarshalOptions", 1).Failure);
            Assert.Equal(1, Get(rs, "MarshalOptions"));
            AssertError(Put(rs, "MarshalOptions", 2), unchecked((int)0x800A0BB9), "ADODB.Recordset");
            AssertError(Put(rs, "MarshalOptions", -1), unchecked((int)0x800A0BB9), "ADODB.Recordset");
            AssertError(Put(rs, "MarshalOptions", "bad"), unchecked((int)0x80020005));

            // Clone inherits MarshalOptions and stays independent thereafter.
            nint rc = (nint)Call(rs, "Clone").Value!;
            Assert.Equal(1, Get(rc, "MarshalOptions"));
            Assert.Null(Put(rc, "MarshalOptions", 0).Failure);
            Assert.Equal(0, Get(rc, "MarshalOptions"));
            Assert.Equal(1, Get(rs, "MarshalOptions"));
            Assert.Null(Call(rc, "Close").Failure);
            Release(rc);

            // Survives Close.
            Assert.Null(Call(rs, "Close").Failure);
            Assert.Equal(1, Get(rs, "MarshalOptions"));
            Release(rs);

            // Server cursor refuses MarshalOptions put with 0x800A0CB3.
            nint rks = New(OwnRecordset);
            Assert.Null(Call(rks, "Open", "SELECT ID, S FROM W ORDER BY ID", new Dispatch(cn), 1, 3, 1).Failure);
            AssertError(Put(rks, "MarshalOptions", 1), unchecked((int)0x800A0CB3), "ADODB.Recordset");
            Assert.Null(Call(rks, "Close").Failure);
            Release(rks);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void DeferredVsImmediateAndAddNewChannels(string dbPath, bool isMdb)
    {
        // r-q48-ado-batch-edits/client-deferred-vs-immediate-accdb, client-deferred-vs-immediate-mdb,
        // client-addnew-channels-status-accdb, r-ado-1b/batch-update-resync, r-ado-1b/edit-pending-ops.
        byte[] beforeBytes = File.ReadAllBytes(dbPath);
        nint cn = OpenAceConnection(dbPath);
        try
        {
            // (3,3,4) deferred local staging: 0 B disk write until UpdateBatch.
            nint rb = New(OwnRecordset);
            Assert.Null(Put(rb, "CursorLocation", 3).Failure);
            Assert.Null(Call(rb, "Open", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID", new Dispatch(cn), 3, 4, 1).Failure);
            Assert.Equal(true, Call(rb, "Supports", 65536).Value); // adUpdateBatch
            Assert.Equal(12, Get(rb, "RecordCount"));

            nint bf = (nint)Get(rb, "Fields")!;
            nint bId = (nint)Get(bf, "Item", "ID")!;
            nint bCode = (nint)Get(bf, "Item", "Code")!;
            nint bS = (nint)Get(bf, "Item", "S")!;
            Assert.Equal(0, Get(bS, "Status")); // Field.Status == 0 on live (3,3,4) row (r-q-ado-misc/values-witness-client-batch)

            Assert.Null(Put(bS, "Value", "staged1").Failure);
            Assert.Equal(1, Get(rb, "EditMode"));
            Assert.Equal(2, Get(rb, "Status"));
            Assert.Null(Call(rb, "Update").Failure);
            Assert.Equal(1, Get(rb, "EditMode")); // (3,3,4) keeps EditMode=1 after local Update
            Assert.Equal(2, Get(rb, "Status"));
            Assert.Equal("staged1", Get(bS, "Value"));
            Assert.Equal("s1", Get(bS, "OriginalValue"));
            Assert.Equal("s1", Get(bS, "UnderlyingValue"));

            // AddNew + CancelUpdate -> EditMode/Status fail 0x800A0BCD
            Assert.Null(Call(rb, "AddNew").Failure);
            Assert.Null(Put(bId, "Value", 99).Failure);
            Assert.Null(Put(bCode, "Value", "w99").Failure);
            Assert.Equal(2, Get(rb, "EditMode"));
            Assert.Equal(1, Get(rb, "Status"));
            Assert.Null(Call(rb, "CancelUpdate").Failure);
            AssertError(TryGet(rb, "EditMode"), unchecked((int)0x800A0BCD), "ADODB.Recordset");
            AssertError(TryGet(rb, "Status"), unchecked((int)0x800A0BCD), "ADODB.Recordset");

            // AddNew + Update -> EditMode=2, Status=1, then UpdateBatch flushes to disk.
            Assert.Null(Call(rb, "AddNew").Failure);
            Assert.Null(Put(bId, "Value", 31).Failure);
            Assert.Null(Put(bCode, "Value", "w31").Failure);
            Assert.Null(Put(bS, "Value", "bat31").Failure);
            Assert.Null(Call(rb, "Update").Failure);
            Assert.Equal(2, Get(rb, "EditMode"));
            Assert.Equal(1, Get(rb, "Status"));
            Assert.Equal(13, Get(rb, "RecordCount"));

            // Still 0 B written to DB before UpdateBatch: check via query on same connection!
            Assert.Equal("s1", QuerySingleString(cn, "SELECT S FROM W WHERE ID = 1"));

            Assert.Null(Call(rb, "UpdateBatch", 3).Failure);
            Assert.Equal(0, Get(rb, "EditMode"));
            Assert.Equal(8, Get(rb, "Status"));
            Assert.Equal("staged1", QuerySingleString(cn, "SELECT S FROM W WHERE ID = 1"));
            Assert.Equal("bat31", QuerySingleString(cn, "SELECT S FROM W WHERE ID = 31"));

            Release(bS);
            Release(bCode);
            Release(bId);
            Release(bf);
            Assert.Null(Call(rb, "Close").Failure);
            Release(rb);

            // (3,3,3) immediate client optimistic parity (ACCDB & MDB):
            nint ro = New(OwnRecordset);
            Assert.Null(Put(ro, "CursorLocation", 3).Failure);
            Assert.Null(Call(ro, "Open", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID", new Dispatch(cn), 3, 3, 1).Failure);
            nint of = (nint)Get(ro, "Fields")!;
            nint oS = (nint)Get(of, "Item", "S")!;
            Assert.Equal(8, Get(ro, "Status")); // before first mutation
            Assert.Null(Put(oS, "Value", "imm1").Failure);
            Assert.Equal(1, Get(ro, "EditMode"));
            // Close and Requery while editing on (3,3,3) fail 0x800A0C93 (r-ado-1b/edit-pending-ops #147/#160).
            AssertError(Call(ro, "Requery"), unchecked((int)0x800A0C93), "ADODB.Recordset");
            AssertError(Call(ro, "Close"), unchecked((int)0x800A0C93), "ADODB.Recordset");
            Assert.Null(Call(ro, "Update").Failure);
            Assert.Equal(0, Get(ro, "EditMode"));
            Assert.Equal(0, Get(ro, "Status")); // adRecOK after immediate flush
            Assert.Equal("imm1", QuerySingleString(cn, "SELECT S FROM W WHERE ID = 1"));

            // Delete on (3,3,3) flushes immediately and reports Status=262144.
            Assert.Null(Call(ro, "MoveLast").Failure);
            Assert.Null(Call(ro, "Delete").Failure);
            Assert.Equal(262144, Get(ro, "Status"));
            Assert.Equal(12, QuerySingleInt(cn, "SELECT Count(*) FROM W"));
            Release(oS);
            Release(of);
            Assert.Null(Call(ro, "Close").Failure);
            Release(ro);

            // Update Resync = 8 without bit 1 on AutoNumber PK table T purges inserted row locally (r-ado-1b/batch-update-resync #183-185).
            if (!isMdb)
            {
                nint ur8 = New(OwnRecordset);
                Assert.Null(Put(ur8, "CursorLocation", 3).Failure);
                Assert.Null(Call(ur8, "Open", "SELECT ID, Code, L, S FROM T ORDER BY ID", new Dispatch(cn), 3, 4, 1).Failure);
                nint ur8Props = (nint)Get(ur8, "Properties")!;
                nint ur8P = (nint)Get(ur8Props, "Item", "Update Resync")!;
                Assert.Null(Put(ur8P, "Value", 8).Failure);
                Release(ur8P);
                Release(ur8Props);

                nint ur8f = (nint)Get(ur8, "Fields")!;
                nint ur8c = (nint)Get(ur8f, "Item", "Code")!;
                nint ur8s = (nint)Get(ur8f, "Item", "S")!;
                nint ur8i = (nint)Get(ur8f, "Item", "ID")!;
                Assert.Null(Call(ur8, "AddNew").Failure);
                Assert.Null(Put(ur8c, "Value", "r8x").Failure);
                Assert.Null(Put(ur8s, "Value", "ins").Failure);
                Assert.Null(Call(ur8, "Update").Failure);
                Assert.Null(Call(ur8, "UpdateBatch", 3).Failure);
                Assert.Equal(262144, Get(ur8, "Status"));
                AssertError(TryGet(ur8i, "Value"), unchecked((int)0x80040E23), "Microsoft Cursor Engine");
                Release(ur8i);
                Release(ur8s);
                Release(ur8c);
                Release(ur8f);
                Assert.Null(Call(ur8, "Close").Failure);
                Release(ur8);
            }
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
        Assert.False(File.ReadAllBytes(dbPath).AsSpan().SequenceEqual(beforeBytes));
    }

    private static void DeleteCancelUpdateBatchScopes(string dbPath, bool isMdb)
    {
        // r-q48-ado-batch-edits/batch-delete-scopes-accdb, r-q48-ado-batch-edits/batch-delete-scopes-mdb,
        // r-q48-ado-batch-edits/batch-cancel-scopes-accdb, r-q48-ado-batch-edits/batch-update-scopes-args-accdb,
        // r-q48-ado-batch-edits/client-batch-pending-ops-accdb.
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 3).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID", new Dispatch(cn), 3, 4, 1).Failure);

            // Delete(2) and Delete(3) -> 0x800A0C93; Delete(99) -> 0x800A0BB9.
            AssertError(Call(rs, "Delete", 2), unchecked((int)0x800A0C93), "ADODB.Recordset");
            AssertError(Call(rs, "Delete", 3), unchecked((int)0x800A0C93), "ADODB.Recordset");
            AssertError(Call(rs, "Delete", 99), unchecked((int)0x800A0BB9), "ADODB.Recordset");

            // CancelBatch(0|99) -> 0x800A0BB9; CancelBatch(2) without Filter -> 0x800A0C93.
            AssertError(Call(rs, "CancelBatch", 0), unchecked((int)0x800A0BB9), "ADODB.Recordset");
            AssertError(Call(rs, "CancelBatch", 99), unchecked((int)0x800A0BB9), "ADODB.Recordset");
            AssertError(Call(rs, "CancelBatch", 2), unchecked((int)0x800A0C93), "ADODB.Recordset");

            // UpdateBatch(0|99) -> 0x800A0BB9; UpdateBatch(2) without Filter -> 0x800A0C93.
            AssertError(Call(rs, "UpdateBatch", 0), unchecked((int)0x800A0BB9), "ADODB.Recordset");
            AssertError(Call(rs, "UpdateBatch", 99), unchecked((int)0x800A0BB9), "ADODB.Recordset");
            AssertError(Call(rs, "UpdateBatch", 2), unchecked((int)0x800A0C93), "ADODB.Recordset");

            // CancelBatch(3) while on pending AddNew leaves Status=262144 (client-batch-pending-ops-accdb #576).
            nint f = (nint)Get(rs, "Fields")!;
            nint fId = (nint)Get(f, "Item", "ID")!;
            nint fCode = (nint)Get(f, "Item", "Code")!;
            Assert.Null(Call(rs, "AddNew").Failure);
            Assert.Null(Put(fId, "Value", 60).Failure);
            Assert.Null(Put(fCode, "Value", "w60").Failure);
            Assert.Null(Call(rs, "CancelBatch", 3).Failure);
            Assert.Equal(0, Get(rs, "EditMode"));
            Assert.Equal(262144, Get(rs, "Status"));
            Release(fCode);
            Release(fId);
            Release(f);

            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);

            // Read-only (3,3,1) refuses UpdateBatch with 0x800A0CB3.
            nint rro = New(OwnRecordset);
            Assert.Null(Put(rro, "CursorLocation", 3).Failure);
            Assert.Null(Call(rro, "Open", "SELECT ID, S FROM W ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
            AssertError(Call(rro, "UpdateBatch"), unchecked((int)0x800A0CB3), "ADODB.Recordset");
            Assert.Null(Call(rro, "Close").Failure);
            Release(rro);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
        _ = isMdb;
    }

    private static void FilterGroupsSortFindCloneRequery(string dbPath)
    {
        // r-q48-ado-batch-edits/batch-filter-groups-lifecycle-accdb, batch-filter-sort-find-edits-accdb,
        // batch-clone-filtered-cancel-accdb, r-ado-1b/view-filter-addnew, r-q2-ado-misc/ado-events-clone-propagation.
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 3).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, L, S FROM T ORDER BY ID", new Dispatch(cn), 3, 4, 1).Failure);
            nint f = (nint)Get(rs, "Fields")!;
            nint fL = (nint)Get(f, "Item", "L")!;
            nint fC = (nint)Get(f, "Item", "Code")!;

            // view-filter-addnew: non-matching AddNew stays current with AbsolutePosition=-1, RecordCount unchanged.
            Assert.Null(Put(rs, "Filter", "L = 20").Failure);
            Assert.Equal(2, Get(rs, "RecordCount"));
            Assert.Null(Call(rs, "AddNew").Failure);
            Assert.Null(Put(fL, "Value", 99).Failure);
            Assert.Null(Put(fC, "Value", "n9").Failure);
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(2, Get(rs, "RecordCount"));
            Assert.Equal(-1, Get(rs, "AbsolutePosition"));
            Assert.Equal(1, Get(rs, "Status"));

            // Matching AddNew joins view, then editing out of filter drops RecordCount back to 2 and AbsolutePosition=-1.
            Assert.Null(Call(rs, "AddNew").Failure);
            Assert.Null(Put(fL, "Value", 20).Failure);
            Assert.Null(Put(fC, "Value", "n10").Failure);
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(3, Get(rs, "RecordCount"));
            Assert.Equal(3, Get(rs, "AbsolutePosition"));
            Assert.Null(Put(fL, "Value", 21).Failure);
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Equal(2, Get(rs, "RecordCount"));
            Assert.Equal(-1, Get(rs, "AbsolutePosition"));
            Assert.Null(Call(rs, "Delete").Failure);
            Assert.Equal(262144, Get(rs, "Status"));

            // Filter=0 shows all 8 rows (7 original + n9); Filter=1 shows 1 pending row; Filter=2 -> 0; Filter=3 -> 0 when empty.
            Assert.Null(Put(rs, "Filter", 0).Failure);
            Assert.Equal(8, Get(rs, "RecordCount"));
            Assert.Null(Put(rs, "Filter", 1).Failure);
            Assert.Equal(1, Get(rs, "RecordCount"));
            Assert.Null(Put(rs, "Filter", 2).Failure);
            Assert.Equal(0, Get(rs, "RecordCount"));
            Assert.Null(Put(rs, "Filter", 3).Failure);
            Assert.Equal(0, Get(rs, "RecordCount"));
            Assert.Null(Put(rs, "Filter", 0).Failure);

            // Sort + live reposition + Find + Clone shared buffer.
            Assert.Null(Put(rs, "Sort", "L DESC").Failure);
            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Equal(99, Get(fL, "Value"));
            Assert.Null(Call(rs, "Find", "Code = 'n9'").Failure);
            Assert.Equal(false, Get(rs, "EOF"));

            nint clone = (nint)Call(rs, "Clone").Value!;
            Assert.Equal(8, Get(clone, "RecordCount"));
            Assert.Null(Call(clone, "CancelBatch", 3).Failure);
            Assert.Equal(7, Get(rs, "RecordCount"));
            Assert.Null(Call(clone, "Close").Failure);
            Release(clone);

            // Requery invalidates held Field wrappers (0x800A0D5C).
            Assert.Null(Call(rs, "Requery").Failure);
            AssertError(TryGet(fL, "Value"), unchecked((int)0x800A0D5C), "ADODB.Field");

            Release(fC);
            Release(fL);
            Release(f);
            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);

            // Client static read-only (3,3,1) also supports Clone() (r-q2-ado-misc/ado-events-clone-propagation).
            nint rro = New(OwnRecordset);
            Assert.Null(Put(rro, "CursorLocation", 3).Failure);
            Assert.Null(Call(rro, "Open", "SELECT ID, Code FROM T ORDER BY ID", new Dispatch(cn), 3, 1, 1).Failure);
            nint rroClone = (nint)Call(rro, "Clone").Value!;
            Assert.Equal(7, Get(rroClone, "RecordCount"));
            Assert.Null(Call(rroClone, "Close").Failure);
            Release(rroClone);
            Assert.Null(Call(rro, "Close").Failure);
            Release(rro);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
    }

    private static void ConflictsUpdateCriteriaAndConstraints(string dbPath, bool isMdb)
    {
        // r-q48-ado-batch-edits/conflict-update-criteria-mdb, conflict-update-delete-mixed-accdb,
        // conflict-local-delete-ext-mod-accdb, conflict-unique-dup-and-null-accdb.
        nint cn = OpenAceConnection(dbPath);
        nint cn2 = OpenAceConnection(dbPath);
        try
        {
            // Update Criteria = 0 (Key): external update to same column still succeeds.
            nint r0 = New(OwnRecordset);
            Assert.Null(Put(r0, "CursorLocation", 3).Failure);
            Assert.Null(Call(r0, "Open", "SELECT ID, V2, S FROM W WHERE ID = 1", new Dispatch(cn), 3, 4, 1).Failure);
            SetClientProperty(r0, "Update Criteria", 0);
            SetField(r0, "S", "uc0-local");
            Assert.Null(Call(r0, "Update").Failure);

            // Update Criteria = 1 (AllCols): external update to DIFFERENT column conflicts (0x80040E38).
            nint r1 = New(OwnRecordset);
            Assert.Null(Put(r1, "CursorLocation", 3).Failure);
            Assert.Null(Call(r1, "Open", "SELECT ID, V2, S FROM W WHERE ID = 2", new Dispatch(cn), 3, 4, 1).Failure);
            SetClientProperty(r1, "Update Criteria", 1);
            SetField(r1, "S", "uc1-local");
            Assert.Null(Call(r1, "Update").Failure);

            // Update Criteria = 2 (UpdCols default): external update to DIFFERENT column succeeds!
            nint r2 = New(OwnRecordset);
            Assert.Null(Put(r2, "CursorLocation", 3).Failure);
            Assert.Null(Call(r2, "Open", "SELECT ID, V2, S FROM W WHERE ID = 3", new Dispatch(cn), 3, 4, 1).Failure);
            SetField(r2, "S", "uc2-local");
            Assert.Null(Call(r2, "Update").Failure);

            // External writes via cn2:
            Assert.Null(Call(cn2, "Execute", "UPDATE W SET S = 'ext1' WHERE ID = 1", Missing, 129).Failure);
            Assert.Null(Call(cn2, "Execute", "UPDATE W SET V2 = 902 WHERE ID = 2", Missing, 129).Failure);
            Assert.Null(Call(cn2, "Execute", "UPDATE W SET V2 = 903 WHERE ID = 3", Missing, 129).Failure);

            Assert.Null(Call(r0, "UpdateBatch", 3).Failure);
            AssertError(Call(r1, "UpdateBatch", 3), unchecked((int)0x80040E38), "Microsoft Cursor Engine");
            Assert.Equal(2050, Get(r1, "Status"));
            Assert.Null(Put(r1, "Filter", 5).Failure);
            Assert.Equal(1, Get(r1, "RecordCount"));
            Assert.Null(Put(r1, "Filter", 0).Failure);
            Assert.Null(Put(r1, "Filter", 5).Failure);
            Assert.Equal(0, Get(r1, "RecordCount")); // Filter=0 cleared Filter=5
            Assert.Null(Call(r2, "UpdateBatch", 3).Failure);

            Assert.Null(Call(r0, "Close").Failure);
            Assert.Null(Call(r1, "Close").Failure);
            Assert.Null(Call(r2, "Close").Failure);
            Release(r0);
            Release(r1);
            Release(r2);

            // Unique key violation (3022) on ICommand does NOT set 2048 bit and does NOT enter Filter=5.
            nint rDup = New(OwnRecordset);
            Assert.Null(Put(rDup, "CursorLocation", 3).Failure);
            Assert.Null(Call(rDup, "Open", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID", new Dispatch(cn), 3, 4, 1).Failure);
            nint df = (nint)Get(rDup, "Fields")!;
            nint dId = (nint)Get(df, "Item", "ID")!;
            nint dCode = (nint)Get(df, "Item", "Code")!;
            Assert.Null(Call(rDup, "AddNew").Failure);
            Assert.Null(Put(dId, "Value", 1).Failure); // duplicate PK
            Assert.Null(Put(dCode, "Value", "dup").Failure);
            Assert.Null(Call(rDup, "Update").Failure);
            AssertError(Call(rDup, "UpdateBatch", 3), unchecked((int)0x80004005), "Microsoft Access Database Engine", expectedHelpContext: 5003022, expectedInfoGuid: IidCommand);
            Assert.Equal(1, Get(rDup, "Status")); // no 2048 concurrency bit
            Assert.Null(Put(rDup, "Filter", 5).Failure);
            Assert.Equal(0, Get(rDup, "RecordCount")); // not in Filter=5
            Release(dCode);
            Release(dId);
            Release(df);
            Assert.Null(Call(rDup, "Close").Failure);
            Release(rDup);
        }
        finally
        {
            Assert.Null(Call(cn2, "Close").Failure);
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn2);
            Release(cn);
        }
        _ = isMdb;
    }

    private static void ResyncTriggerAndUnderlyingVsAll(string dbPath, bool isMdb)
    {
        // r-q48-ado-batch-edits/resync-underlying-trigger-accdb, resync-all-vs-underlying-mdb,
        // r-q2-ado-misc/resync-underlying-clean-row.
        nint cn = OpenAceConnection(dbPath);
        nint cn2 = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 3).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID", new Dispatch(cn), 3, 4, 1).Failure);
            nint f = (nint)Get(rs, "Fields")!;
            nint fV = (nint)Get(f, "Item", "V")!;

            // Row 1: posted pending edit (Update called) -> Resync(3, 1) refreshes UnderlyingValue.
            Assert.Null(Put(fV, "Value", 120).Failure);
            Assert.Null(Call(rs, "Update").Failure);
            // Row 2: unposted edit (NO Update called) -> Resync(3, 1) leaves UnderlyingValue unchanged.
            Assert.Null(Call(rs, "MoveNext").Failure);
            Assert.Null(Put(fV, "Value", 220).Failure);
            // Row 4: clean -> Resync(3, 1) leaves UnderlyingValue unchanged.

            Assert.Null(Call(cn2, "Execute", "UPDATE W SET V = 110 WHERE ID = 1", Missing, 129).Failure);
            Assert.Null(Call(cn2, "Execute", "UPDATE W SET V = 210 WHERE ID = 2", Missing, 129).Failure);
            Assert.Null(Call(cn2, "Execute", "UPDATE W SET V = 410 WHERE ID = 4", Missing, 129).Failure);
            Assert.Null(Call(cn2, "Execute", "DELETE FROM W WHERE ID = 5", Missing, 129).Failure);

            Assert.Null(Call(rs, "Resync", 3, 1).Failure);
            // Row 2 (unposted put) stays Underlying=20
            Assert.Equal(20, Get(fV, "UnderlyingValue"));
            // Row 1 (posted pending) refreshed Underlying=110
            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Equal(120, Get(fV, "Value"));
            Assert.Equal(10, Get(fV, "OriginalValue"));
            Assert.Equal(110, Get(fV, "UnderlyingValue"));
            // Row 4 (clean) stays Underlying=40
            Assert.Null(Call(rs, "Move", 3).Failure);
            Assert.Equal(40, Get(fV, "UnderlyingValue"));

            // Resync(3, 2) refreshes all live rows and deletes externally deleted row 5 with 0x80040E23 NativeError 1029.
            AssertError(Call(rs, "Resync", 3, 2), unchecked((int)0x80040E23), "Microsoft Cursor Engine");
            nint errs = (nint)Get(cn, "Errors")!;
            Assert.Equal(1, Get(errs, "Count"));
            nint e0 = (nint)Get(errs, "Item", 0)!;
            Assert.Equal(1029, Get(e0, "NativeError"));
            Release(e0);
            Assert.Null(Call(errs, "Clear").Failure);
            Release(errs);
            Assert.Equal(11, Get(rs, "RecordCount"));
            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Equal(110, Get(fV, "Value"));

            Release(fV);
            Release(f);
            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);
        }
        finally
        {
            Assert.Null(Call(cn2, "Close").Failure);
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn2);
            Release(cn);
        }
        _ = isMdb;
    }

    private static void TransactionsWrappingUpdateBatch(string dbPath, bool isMdb)
    {
        // r-q48-ado-batch-edits/txn-updatebatch-commit-accdb, r-q48-ado-batch-edits/txn-updatebatch-commit-mdb,
        // r-q48-ado-batch-edits/txn-updatebatch-rollback-accdb, r-q48-ado-batch-edits/txn-updatebatch-rollback-mdb.
        nint cn = OpenAceConnection(dbPath);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 3).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, Code, V, V2, S FROM W ORDER BY ID", new Dispatch(cn), 3, 4, 1).Failure);
            nint f = (nint)Get(rs, "Fields")!;
            nint fId = (nint)Get(f, "Item", "ID")!;
            nint fCode = (nint)Get(f, "Item", "Code")!;
            nint fS = (nint)Get(f, "Item", "S")!;

            // RollbackTrans after UpdateBatch: DB reverts, client cache holds clean rows until Resync(3,2)/Requery().
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            Assert.Null(Put(fS, "Value", "tr1").Failure);
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Null(Call(rs, "AddNew").Failure);
            Assert.Null(Put(fId, "Value", 70).Failure);
            Assert.Null(Put(fCode, "Value", "w70").Failure);
            Assert.Null(Put(fS, "Value", "tr70").Failure);
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Null(Call(rs, "UpdateBatch", 3).Failure);
            Assert.Null(Call(cn, "RollbackTrans").Failure);

            Assert.Equal(13, Get(rs, "RecordCount"));
            AssertError(Call(rs, "Resync", 3, 2), unchecked((int)0x80040E23), "Microsoft Cursor Engine");
            Assert.Equal(12, Get(rs, "RecordCount"));
            Assert.Null(Call(rs, "MoveFirst").Failure);
            Assert.Equal("s1", Get(fS, "Value"));

            // CommitTrans after UpdateBatch persists both edit and insert.
            Assert.Equal(1, Call(cn, "BeginTrans").Value);
            Assert.Null(Put(fS, "Value", "tc1").Failure);
            Assert.Null(Call(rs, "Update").Failure);
            Assert.Null(Call(rs, "UpdateBatch", 3).Failure);
            Assert.Null(Call(cn, "CommitTrans").Failure);
            Assert.Equal("tc1", QuerySingleString(cn, "SELECT S FROM W WHERE ID = 1"));

            Release(fS);
            Release(fCode);
            Release(fId);
            Release(f);
            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);
        }
        finally
        {
            Assert.Null(Call(cn, "Close").Failure);
            Release(cn);
        }
        _ = isMdb;
    }

    private static void DisconnectActiveConnectionAndReadOnlyMode(string dbPath)
    {
        // r-q48-ado-batch-edits/disconnect-put-empty-reconnect-accdb, r-ado-1b/rs-readonly-mode-edit.
        nint cn = OpenAceConnection(dbPath);
        nint cnRo = OpenAceConnection(dbPath, mode: 1);
        try
        {
            nint rs = New(OwnRecordset);
            Assert.Null(Put(rs, "CursorLocation", 3).Failure);
            Assert.Null(Call(rs, "Open", "SELECT ID, S FROM W ORDER BY ID", new Dispatch(cn), 3, 4, 1).Failure);
            // ActiveConnection = VT_EMPTY -> 0x800A0BB9; ActiveConnection = "" -> E_NOTIMPL (non-ACE MSDASQL refusal).
            AssertError(Put(rs, "ActiveConnection", Missing), unchecked((int)0x800A0BB9), "ADODB.Recordset");
            Assert.Equal(HResults.E_NOTIMPL, Put(rs, "ActiveConnection", "").Failure!.Value.Scode);
            Assert.Null(Call(rs, "Close").Failure);
            Release(rs);

            // Mode=1 read-only connection: client (3,3,4) stages locally and fails 3073 on UpdateBatch.
            nint rro = New(OwnRecordset);
            Assert.Null(Put(rro, "CursorLocation", 3).Failure);
            Assert.Null(Call(rro, "Open", "SELECT ID, S FROM W ORDER BY ID", new Dispatch(cnRo), 3, 4, 1).Failure);
            SetField(rro, "S", "ro-batch");
            AssertError(Call(rro, "UpdateBatch", 3), unchecked((int)0x80004005), "Microsoft Access Database Engine", expectedHelpContext: 5003073, expectedInfoGuid: IidCommand);
            Assert.Equal(2, Get(rro, "Status"));
            Assert.Null(Call(rro, "Close").Failure);
            Release(rro);
        }
        finally
        {
            Assert.Null(Call(cnRo, "Close").Failure);
            Assert.Null(Call(cn, "Close").Failure);
            Release(cnRo);
            Release(cn);
        }
    }

    private static void SetClientProperty(nint rs, string name, int value)
    {
        nint props = (nint)Get(rs, "Properties")!;
        nint prop = (nint)Get(props, "Item", name)!;
        Assert.Null(Put(prop, "Value", value).Failure);
        Release(prop);
        Release(props);
    }

    private static void SetField(nint rs, string name, object value)
    {
        nint fields = (nint)Get(rs, "Fields")!;
        nint field = (nint)Get(fields, "Item", name)!;
        Assert.Null(Put(field, "Value", value).Failure);
        Release(field);
        Release(fields);
    }

    private static string QuerySingleString(nint cn, string sql)
    {
        nint rs = (nint)Call(cn, "Execute", sql, Missing, 1).Value!;
        nint fields = (nint)Get(rs, "Fields")!;
        nint f0 = (nint)Get(fields, "Item", 0)!;
        string val = (string)Get(f0, "Value")!;
        Release(f0);
        Release(fields);
        Assert.Null(Call(rs, "Close").Failure);
        Release(rs);
        return val;
    }

    private static int QuerySingleInt(nint cn, string sql)
    {
        nint rs = (nint)Call(cn, "Execute", sql, Missing, 1).Value!;
        nint fields = (nint)Get(rs, "Fields")!;
        nint f0 = (nint)Get(fields, "Item", 0)!;
        int val = Convert.ToInt32(Get(f0, "Value")!);
        Release(f0);
        Release(fields);
        Assert.Null(Call(rs, "Close").Failure);
        Release(rs);
        return val;
    }
}
