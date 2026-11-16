# Evidence summaries

Digests of the retained records, card by card. They were written by sub-agents (Sonnet) that read every
record of their card with `calcdb-oracle show --record`, and were spot-checked against the records (the
GUID read-back form, bookmark bytes and the system-table permission errors matched). **The records under
`evidence/` are the authority**; a summary that disagrees with a record is wrong. Each section lists what ACE
answered, probe or fixture defects found in the records, and what to revisit. Environment of all records:
see [evidence-log.md](evidence-log.md).

### r-fmt-1

104 records: 52 `create-*` (27 ACCDB, 25 MDB) and 52 `read-*`. Records come from engine `DAO.DBEngine.120`, `ACEDAO.DLL` 16.0.20527.20196, `eng.Version()` "16.0". The card has no branch IDs, so the groups below follow its fixture list.

**Observed**
- **Creation route.** `CreateDatabase(path, ";LANGID=0x0409;CP=1252;COUNTRY=0", 128)` gives `db.Version()` "12.0" (ACCDB). The same call with option 64 gives "4.0" (MDB). 48 of the 52 creations are ReturnedSuccess (`create-*-empty`).
- **File sizes of the bare fixtures.** Empty file: ACCDB 172032 B, MDB 77824 B. Empty table T (Long PK): ACCDB 184320 B, MDB 90112 B (`create-*-empty`, `create-*-table-empty`).
- **Reopen.** All 52 read fixtures have identical SHA-256 prefixes at creation, before-open and after-close. In these probes a read-only open (`OpenDatabase(path, False, True)`) did not change the file.
- **Lock files.** After open, ACCDB has `.laccdb` and MDB has `.ldb`, both 64 B with the same hash. Neither is listed after close.
- **System tables.**
  - ACCDB lists MSysACEs, MSysComplexColumns, MSysObjects, MSysQueries and MSysRelationships; MDB lists the same without MSysComplexColumns (`read-*-empty`).
  - Empty-file RecordCounts: ACCDB MSysACEs 36 and MSysObjects 18; MDB 16 and 8.
  - With table T (`read-accdb-long`): MSysACEs 38, MSysObjects 19.
  - Each system table reports `Fields.Count()` 0.
- **Field families.** Identical in both formats unless stated. Each family had a Null row stored, which reads back as VT_NULL except Yes/No.

| Family | Type, Size, Attributes | Read back |
|---|---|---|
| byte | 2, 1, 1 | VT_UI1 0 and 255 |
| integer | 3, 2, 1 | VT_I2 -32768 and 32767 |
| long | 4, 4, 1 | VT_I4 extremes |
| single | 6, 4 | VT_R4 1.5, -0, 1E-45, 3.4028235E+38 |
| double | 7, 8 | VT_R8 0.1, -0, 5E-324, 1.7976931348623157E+308 |
| currency | 5, 8 | VT_CY at both limits and 1.2345 |
| datetime | 8, 8 | VT_DATE 0, -657434, 2958465.99998843, 1.5, -1.5 |
| text(50) | 10, 50, 2 | "", "alpha", "Bä€", "𝄞 surrogate pair" and 50×"x" all round-trip |
| memo | 12, 0, 2 | "", short, 5000×"m" |
| yesno | 1, 1, 1 | The Null row reads VT_BOOL 0; the others VT_BOOL -1 and 0 |
| binary(50) | 9, 50, 1 | The empty array reads VT_NULL; the others read VT_ARRAY\|VT_UI1 |
| longbinary | 11, 0, 2 | Rows read VT_ARRAY\|VT_UI1 |
| guid | 15, 16, 2 | VT_BSTR "{guid {00112233-4455-6677-8899-AABBCCDDEEFF}}" (extra "guid" wrapper) |

- **Single and double.** Both read -0.0 back as `-0`.
- **File sizes with long values.** memo: ACCDB 221184 B, MDB 126976 B. longbinary: ACCDB 196608 B, MDB 102400 B.
- **Format-gated types (`create-*-bigint`, `create-*-datetime2`).**
  - ACCDB BIGINT: Type 16, Size 8, Attributes 2. Rows read VT_I8 9223372036854775807 and VT_I8 -1.
  - ACCDB DATETIME2: Type 26, Size 42, Attributes 1. The stored 1.5 reads as VT_BSTR "12/31/1899 12:00:00.0000000".
  - MDB: both DDL statements fail with 0x80020009, scode 0x800A0BB9 (3001) "Invalid argument."
- **AutoNumber (`read-*-counter`).** ID has Attributes 17, and PK_T is Primary and Unique. After three inserts and a delete of 'b', RecordCount is 2 with ids 1 and 3.
- **Indexes (`read-*-index`).**
  - Enumeration order is IX_BA, PK_T, UX_A.
  - IX_BA is non-unique with fields B (Attributes 1, DESC) and A (Attributes 0). UX_A is Unique. PK_T is Primary.
- **Column order.**
  - `read-*-ordinal`: after the OrdinalPosition change, the fields list B, A, ID with OrdinalPosition 0, 1, 2, and the row reads "b", "a", 1.
  - `read-*-renamed-dropped`: fields ID, A_renamed, C, D with OrdinalPosition 0 to 3. Row 1 has D Null; row 2 has D VT_R8 2.5.
- **Width (`read-*-wide`).** `Fields.Count()` is 120. The row reads "first" and "last" with 117 Nulls between them. File size: ACCDB 196608 B, MDB 102400 B.
- **Calculated (`read-accdb-calculated`).** Field C has Type 10, Size 243, Attributes 2, and value "x-calc". Its CollatingOrder is 1024, and so is ID's (created through CreateField). Table-column CollatingOrder is 1033 in the DDL-created fixtures.
- **Collation.** `db.CollatingOrder` is 1033 in every fixture that reads it, including `accdb-collating-general`.
- **QueryDefs (`read-*-querydef`).** Count 2 in both formats, names QParam and QSelect, both Type 0. SQL comes back reformatted with line breaks, e.g. "SELECT ID, V\nFROM T\nORDER BY ID;\n".
- **Database properties (`read-*-properties`).** 11 built-ins plus the user property CalcDbProbe (Type 10, value "value of a user property"). `Updatable` is False on the read-only open.

**Probe or fixture defects**
- **All 52 `read-*` probes are ReturnedFailure for a harness reason.** `ReadBack` also enumerates the system tables, and those calls fail:
  - `Indexes.Count()` fails with scode 0x800A0C26 (3110, "no read definitions permission").
  - `OpenRecordset` fails with 0x800A0C28 (3112, "no read permission").
  - The `Connection` property `Value` fails with 0x800A0CB3 (3251).
  - That gives 11 failures per ACCDB read and 9 per MDB read. User-table data was still captured in full.
- **DECIMAL(18,4) DDL is wrong.** In `create-accdb-decimal` and `create-mdb-decimal`, `CREATE TABLE ... DECIMAL(18,4)` fails with 0x800A0CDC (3292) "Syntax error in field definition." Table T was never created, and the next `OpenRecordset("T")` fails with 0x800A0BC3 (3011). The fixtures and `read-*-decimal` contain only system tables. This says nothing about decimal storage.
- **Table T is absent in `create-mdb-bigint` and `create-mdb-datetime2`.** The 3001 answer is a real ACE answer, but the fixtures hold no T, and the follow-up open fails with 3011.
- **`accdb-collating-general` has no contrast.** Every creation passes the same LANGID 0x0409, so the probe only reads 1033 back.
- **Table `Description` is never read back in `*-properties`.** `ReadBack` enumerates only database properties, so the TableDef property was written (calls succeeded) but not observed.
- **The binary array contents are not visible in the compact `show` view.** I could not confirm the bytes read back.

**Revisit**
- Not covered: reopening through Access (the probe comment says tier 2), and any raw-byte checks (II-03 geometry, page-0 mask, page kinds, II-04 column blocks, TableDef continuation mapping). The files are kept as artifacts, but these records do not contain byte analysis.
- Not covered: non-General sort orders or other LANGIDs, and any file version beyond option 64/128.
- Not covered: calculated Boolean, Numeric and Memo fields, Large Number via DDL variants, and DECIMAL (fix the DDL).
- Not covered: malformed fixtures and populated tables large enough to span many pages.
- Open: whether the `{guid {...}}` string wrapper comes from ACE or from the harness's formatting, and what VT_ARRAY\|VT_UI1 holds for the 5000-byte longbinary row.

### r-cursor-1

All records are ACCDB fixtures made by `CreateDatabase` (`db.Version` "12.0"), through one MainSTA .NET 11 lane. `ACEDAO.DLL` loaded is 16.0.20527.20196, not the 20016 target. Every ACE refusal below is `hr=0x80020009` with EXCEPINFO `scode=0x800A0xxx`. The DAO number is the low 16 bits of that scode.

**Observed**

*CUR-POS*
- A dynaset, snapshot and table-type recordset on T gave the same move sequence (`pos-moves-dynaset/snapshot/table`). Table-type lacks `AbsolutePosition`, which gives 3251.
  - `MovePrevious` on row 1 gives BOF with no error. A second `MovePrevious` gives 3021 (`0x800A0BCD`, "No current record.") and BOF stays set.
  - `MoveNext` from BOF returns row 1. `MoveNext` from the last row gives EOF with no error, and a second `MoveNext` gives 3021.
  - `Move 0` at EOF or BOF gives 3021. `Move 10` past the end gives EOF with no error, and `Move -10` gives BOF with no error. `Move -1` from EOF gives the last row, and `Move 1` from BOF gives the first.
  - `AbsolutePosition` is -1 at BOF and EOF, and 0-based otherwise.
- Forward-only (`pos-moves-forwardonly`, `pos-empty-singleton`):
  - `MovePrevious`, `MoveFirst`, `MoveLast`, `Move -1/-2/-10` and `AbsolutePosition` all give 3219 (`0x800A0C93`, "Invalid operation.").
  - `Move 0` and `Move 10` work. `Move 10` past the end gives EOF with no error.
  - At EOF, `Move 1` and `Move 0` give 3021, but `Move -1` gives 3219. `MoveLast` leaves the current row unchanged.
- Empty table E: BOF and EOF are both true for every type, and `RecordCount` is 0. Every move gives 3021 (dynaset `AbsolutePosition` -1). Forward-only gives 3219 for `MovePrevious`, `MoveFirst`, `MoveLast` and `Move -1`, and 3021 for `MoveNext`, `Move 0` and `Move 1`.
- Singleton S: `MoveNext` gives EOF. `MovePrevious` or `Move -1` from EOF returns the row. `RecordCount` is 1, but 1 only for table, dynaset and snapshot; forward-only `RecordCount` is -1 here.
- Deletes (`pos-delete-ends`):
  - Deleting the current row on a dynaset leaves BOF/EOF false, `AbsolutePosition` -1 and a field read giving 3021. On the table type the field read gives 3167 (`0x800A0C5F`, "Record is deleted.").
  - `MoveNext` then returns the next row. After deleting the last row, `MoveNext` gives EOF and `MovePrevious` returns the previous row.
- Find (`pos-find`):
  - A failed `FindNext` or `FindPrevious` sets `NoMatch` -1 and leaves the position unchanged. A later `Move` resets `NoMatch` to 0.
  - A malformed criterion gives 3077 (`0x800A0C05`) and a Null criterion gives 3421 (`0x800A0D5D`); position is unchanged in both.
  - Snapshot `FindFirst` works. Table-type gives 3251, forward-only gives 3219.

*CUR-EMPTY-PAGES*
- Table M (228 rows left of 512; table-type RecordCount 228) read in both directions gave IDs 101-199 then 321-449 and the reverse, on the table type and the dynaset (`pages-traverse-table/dynaset`). After the forward walk BOF=0 and EOF=-1; after the reverse walk BOF=-1 and EOF=0.
- Table MA (256 rows, all deleted) behaved as empty on table, table with `Index PK_MA`, dynaset, snapshot and forward-only (`pages-all-deleted`): BOF=EOF=-1, `RecordCount` 0, moves give 3021, forward-only gives 3219 for `MoveFirst`, `MoveLast` and `MovePrevious`.

*CUR-POP*
- `RecordCount` (`pop-count-*`, M):
  - Dynaset and snapshot give 1 at open and after a field read, then 228 after the first `MoveNext`. Table-type gives 228 at open.
  - Forward-only grows with rows fetched (1, 1, 2, 52 after `Move 50`).
  - `Requery` gives 1 again on dynaset, snapshot and forward-only. Table-type `Requery` gives 3251.
  - `MoveLast` with no earlier reads also gave 228 (`pop-count-minimal-dynaset`).
- After `Move 50` the dynaset and snapshot `PercentPosition` is 22.368422 (`AbsolutePosition` 51). After `MoveLast` it is 99.5614 (`AbsolutePosition` 227). Table-type gives 24.206348 and 99.55357.
- Other-cursor changes in the same Database (`pop-visibility-*`; cursor A had read only ID 1):
  - Dynaset A showed the visited row's K as 111 and the unvisited K as 444. It skipped the deleted ID 3 and showed the new ID 6 without `Requery`. `RecordCount` was 5 after the walk.
  - Snapshot A showed the visited row's K as stale 10, including on the second walk. It showed the unvisited row's 444, skipped ID 3 and showed ID 6. After `Requery` the visited row's K was 111.

*CUR-ID*
- Bookmarks are VT_ARRAY|VT_UI1, 4 bytes, lower bound 0 (`id-bookmark-shape`).
  - Table: row 1 is `002d0000`, row 2 is `012d0000`. Dynaset: `00020000`, `01020000`. Snapshot: `00030000`, `01030000`. Revisiting a row returned equal bytes.
  - Table and dynaset `LastModified` is a null SAFEARRAY on a fresh recordset. Snapshot and forward-only give 3251.
  - Forward-only `Bookmarkable` is 0 and `Bookmark` gives 3219.
- Assigning bookmarks to the dynaset:
  - 4 zero bytes gives 3159 (`0x800A0C57`) and leaves no current row (field read 3021) with BOF/EOF false and `AbsolutePosition` 1.
  - `Long 1`, Null and Empty give 3421 (`0x800A0D5D`).
- The bookmark bytes stayed the same across a key change `ID 2 → 20` (`01020000`, `id-key-change`) and after row growth to 255 characters (`13020000`, `id-relocation`).
- After a key change the dynaset keeps its slot: the walk is 1, 20, 3, 4, 5. The table-type cursor on the moved row reads 20, then `MoveNext` gives 3 and `MovePrevious` gives 1.
- After external `DELETE` then re-`INSERT` of ID 3 (`id-delete-reinsert`) the dynaset position stays a deleted slot with field reads 3167. The walk is 1, 2, deleted slot, 4, 5; the new row is not shown.
- Held dynaset Field showed an external `UPDATE` at once (`changed`), with no move (`id-field-held`). The snapshot kept `b` after `Move 0` and after `MoveNext`/`MovePrevious`.
- `Index` switch on a table (`id-index-switch`): the current row became the first row of the new index (1,10) from (1,20). `Index "NO_SUCH_INDEX"` gives 3800 (`0x800A0ED8`) and the current row is unchanged.
- Rows 0 and 1 gave byte 0 = 00 and 01 in both indexes.

*CUR-SEEK*
- Operators on `PK_C` (A ASC, B DESC) and `IX_AA`, key (1,20) (`seek-operators`): `<`, `<=`, `=`, `>=` and `>` each select by index order, not by value order.
  - On `PK_C`, `<` gives (1,30) and `>` gives (1,10).
  - On `IX_AA`, `<` gives (1,10) and `>` gives (1,30).
  - `<>` gives 3001 (`0x800A0BB9`) on both.
- Duplicate group (`seek-duplicates`; Null sorts first in `IX_K`):
  - `=`, `>=` and `<=` with 2 give ID 2 (the first duplicate). `>` gives ID 5 and `<` gives ID 1.
  - `Seek =, Null` on `IX_KN` gives NoMatch=-1; the `IX_KN` walk is 1-5 and does not include ID 6.
- Duplicate group across leaves (`seek-duplicates-multileaf`; 256 rows): `=`, `>=` and `<=` give ID 1. `>` gives ID 1002 and `<` gives ID 1001.
- Arguments (`seek-arguments`):
  - `Seek =` with no key, `"=="`, and a non-numeric string for a Long key give 3001, 3001 and 3421.
  - Three keys on a two-column index gives 3250 (`0x800A0CB2`, "Could not build key").
  - String keys `"1"`,`"20"` match (1,20).
  - A Null, Empty or 1.5 component gives NoMatch=-1 with no current row; `MoveNext` still reads 3021.
  - After a failed `Seek =,9,9`, a later `Seek =,1,30` works.
  - Seek with no Index set gives 3019 (`0x800A0BCB`); a dynaset gives 3251.

*CUR-EDIT*
- `AddNew` buffer (`edit-addnew-generated`): the ID is generated at `AddNew` (3), N shows the default 5, and T and U are Null. After `CancelUpdate` the next `AddNew` gave ID 4. After `Update` the current row is still the old row (ID 1) and `LastModified` is `02020000`.
- Buffer lifecycle (`edit-buffer-lifecycle`): a second `Edit` leaves `EditMode` 1 and reverts the buffer to the stored value. A second `CancelUpdate` or `Update` gives 3020 (`0x800A0BCC`, "Update or CancelUpdate without AddNew or Edit.").
- After `Update`, `LastModified` equals `Bookmark` (`00020000`).
- Movement or `Close` while buffered discards the pending `Edit` or `AddNew`; `EditMode` reads 0 and the stored rows are unchanged (`edit-move-close-buffered`).
- Validation (`edit-validation-order`): a violation at `Update` leaves `EditMode` 2 (AddNew) and the buffer intact.
  - T unset with U "bad" gives 3314 (`0x800A0CF2`) first. U "bad" with T set gives 3316 (`0x800A0CF4`). A duplicate U gives 3022 (`0x800A0BCE`). N=2000 gives 3316 from the table rule.
  - After a successful `Update`, `DBEngine.Errors.Count` still reported the previous 3316.
  - `ValidateOnSet True` raises 3316 (Source "DAO.Field") at assignment; `EditMode` stays 1 and the field keeps "u1".
- Optimistic conflict, both databases from the default workspace (`edit-conflict-optimistic`): A's `Update` gave 3197 (`0x800A0C7D`) when B changed the same field, and also when B changed a different field.
  - A's `EditMode` stays 1 and the buffer is kept.
  - A second explicit `Update` returned success after the same-field conflict, with Errors still showing 3197. In the other-field case `Move 0` showed `u2` and N 77.
- Rollback (`edit-cancel-rollback`): after `Rollback`, `EditMode` reads 0, but field reads, `Move 0` and `MoveFirst` give 3420 (`0x800A0D5C`, "Object invalid or no longer set.") and `LastModified` gives a null array. Stored rows were back to `u1`/`u2`.

*CUR-LIFE*
- Clone (`life-clone`): the clone starts at row 1 (`AbsolutePosition` 0) while the parent is on ID 3. The clone's `MoveFirst` leaves the parent on ID 3.
  - Clone `Bookmark` is `00020000` and parent `Bookmark` is `02020000`.
  - After a clone update, parent `LastModified` is a null array and clone `LastModified` is `00020000`.
  - The clone keeps working after the parent closes (`RecordCount` 5).
  - A new clone's `RecordCount` is 1; after its `MoveLast` both parent and clone report 5.
- Closing a clone inside `BeginTrans` did not undo the parent's edit; the committed row reads `txn` and Errors count is 0 (`life-clone-close-transaction`).
- `Filter` and `Sort` on a dynaset leave its own walk unchanged (IDs 1-5). `OpenRecordset()` gives a derived recordset of IDs 5, 4, 3 (`RecordCount` 3) and `Requery` returns 1-5. Table-type `Filter` gives 3251 (`life-filter-sort`).
- `Requery` (`life-requery`): the dynaset and snapshot return to row 1 with `RecordCount` 1, growing to 6 after `MoveLast`. New bookmarks are `00020000` (dynaset) and `00030000` (snapshot); the old dynaset `02020000` was not tested. `LastModified` after `Requery` is null on the dynaset and 3251 on the snapshot.
- Held objects after `rs.Close()` or `db.Close()` give 3420 for field reads, `Fields.Count`, `EOF`, `MoveNext` and a second `Close`. `db.Close()` with an open recordset gave no error (`life-close-held`).

**Probe or fixture defects**
- `id-bookmark-shape`: the assignments ran one after another on one recordset. The failed 4-zero-byte assignment left no current row, so the later `BSTR ""` (success) and `Long`/Null/Empty (3421) effects on position are confounded. Only the first, empty-array assignment is clean, and it is indistinguishable from a no-op.
- `seek-arguments`: a fresh recordset on `PK_C` already sits at (1,30). The results of `Seek =,1`, `>=,1`, `>,1` and `=,Missing,20` all land on (1,30), so they cannot be told from "Seek did not move". Do not read `>`,1 → (1,30) as a policy. `seek-duplicates` `Seek =,Null` on `IX_K` (ID 6) has the same weakness: ID 6 is the first row.
- `edit-conflict-pessimistic`: both databases come from the default workspace, so the pessimistic case did not discriminate. B's `Edit` on A's locked row raised no error and Errors was empty. It cannot answer "what a pessimistic edit locks".
- `edit-conflict-optimistic`: the stored result after A's second `Update` was never read back. Only A's own buffer was read.
- `pages-*`, `create-pages`: page emptiness is assumed from the deleted ID ranges. No retained page inspection shows the pages are empty. In `pages-traverse-*`, storage order equals ID order, so table-type and dynaset cannot be told apart. `id-relocation` kept the file but no inspector result shows the row body moved.
- `DBEngine.Errors` is not cleared by a successful call. Count readouts after success (`edit-conflict-optimistic`, `edit-validation-order` last `Update`) are stale and not an answer for that call.

**Revisit**
- Not covered by any record: CUR-PROV and CUR-BULK entirely (no `GetRows`, `Updatable`, `SourceField`, join or query fixtures).
- CUR-EMPTY-PAGES: no overflow-only pages, sparse maps, budget or I/O failures.
- CUR-POP: no same-Database controls on table or forward-only, no other-Workspace or other-process runs, no clone or Bookmark reads in `pop-*`.
- CUR-ID: the following are missing.
  - rollback or allocation reuse, and a restored file copy;
  - a leaf split;
  - held Field after delete or reinsert;
  - old Bookmark after `Requery`, and a Bookmark moved between parent and clone.
- CUR-SEEK: no mixed-direction value-order discrimination beyond the (1,20) witness, no long-key collision or shortened-key, and no leading or middle omission other than `Missing`.
- CUR-EDIT original-evidence controls (c)-(f) (row on the same page, body-only relocation, delete/reinsert incarnation, restored value) are not run. Nothing covers FK or permissions, reversed assignment order, generated-value rollback or reopen, or a Memo/Required `AddNew`. Whether the table rule or the unique index is checked first is untested; the two failures were never paired.
- CUR-LIFE: no clone after population, no nested transaction levels, no parent/clone close order beyond parent-first, `LastModified` straight after `Clone` unread.
- Only ACCDB via direct automation was captured (no Jet MDB, no VBA lane, no .NET Framework lane).

### r-txn-1

44 records: 3 fixture creations (no failed calls), 41 probes (`ReturnedSuccess` for `release-db-in-transaction`; the rest are `ReturnedFailure`, i.e. ACE error answers). ACEDAO 16.0.20527.20196, ACCDB fixtures except the two `*-mdb` probes. DAO error numbers below are the `Errors(0).Number` values, with scodes in hex where useful.

**Observed**

*TXN-NEST*
- `nest-inner-rollback`, `nest-inner-rollback-mdb`: B a B b B c R, then C, C. After the inner R, rows are 101/202/30. The two commits keep 101/202; c is gone. MDB gives the same rows.
- `nest-middle-rollback-after-inner-commit`: after C then R, a survives and b and c (merged) are gone (101/20/30).
- `nest-outer-rollback-after-inner-commits`: after C C then R, all of a, b, c are reversed.
- `nest-same-row-all-levels`: after inner C then R, row 1 is 1001, the outer-level image. A final C keeps it.
- `nest-empty-levels`: empty BeginTrans/Commit/Rollback levels raise no error. An empty inner commit over a changed outer level leaves the outer Rollback reversing the change.
- `nest-resolve-without-level`: CommitTrans and Rollback with no level raise 3034 (scode 0x800A0BDA), both at the start and after the only level was committed. The Workspace stays usable.
- `nest-exhaustion`: 7 BeginTrans calls succeed (7 Rollbacks succeed). The 8th raises 3003 (0x800A0BBB, source DAO.Workspace). `UPDATE` at levels 1–5 succeeds. At levels 6 and 7 the `Execute` raises 3003 (source DAO.Database), and so does `OpenRecordset` at depth 6. The Rollback images are 105, 104, 103, 102, 101, then 40. Rollbacks 8 and 9 raise 3034. Errors(0) stays 3003 through every successful Rollback.
- `nest-late-file-b`: B opened via `ws.OpenDatabase` after the 2nd BeginTrans, changed (5001), inner C, outer R. Both A (row 1 back to 10) and B (back to 10) revert; the fresh Workspace confirms this.

*TXN-STMT*
- Duplicate key in `INSERT…SELECT` with dbFailOnError: middle, last and only candidate all raise 3022 (0x800A0BCE) and `RecordsAffected` is 0. No candidate row survives. Earlier A (V=101) survives and later C applies. Same at depth 0, 1 and 2 and on MDB (`stmt-unique-middle-fail-d0/d1/d2/d1-mdb`, `stmt-unique-last-fail-d1`, `stmt-unique-all-fail-d1`).
- `stmt-saved-querydef-fail-d1`, `stmt-temp-querydef-fail-d1`: `QueryDef.Execute` raises 3022 (source DAO.QueryDef) and `qd.RecordsAffected` is 0. `db.RecordsAffected` then reads 1, which I read as the earlier A, not these statements. No candidate rows survive.
- `stmt-unique-middle-plain-d1` (no dbFailOnError): no error, `RecordsAffected` 2, Errors count 0. Rows 11 and 15 are inserted and 13 is skipped silently.
- `stmt-validation-plain-d1` (no dbFailOnError): `UPDATE N=N-2` returns no error, `RecordsAffected` 4, Errors count 0. Row 1 (N would be -1) is left at N=1 and rows 2–5 are updated.
- `stmt-expression-fail-d1`: `V=100/(ID-3)` raises error 11 (0x800A000B "Division by zero") with `RecordsAffected` 0. Rows 1 and 2 are left changed (-50, -100) and rows 3–5 are untouched. This is a surviving subset after a failing call, unlike the 3022 cases. C applies and the commit keeps -50/-100.
- `stmt-fk-counter-fail-d1`: FK failure raises 3201. No C rows survive. A later `INSERT` gets C.ID 5, so counter values 3 and 4 were consumed (rows c1 and c2 hold 1 and 2).
- `ddl-nested-failure-outer-rollback`: an outer-level `INSERT` that was later rolled back also consumed a COUNTER value. The "after" row gets C.ID 4.

*TXN-CLOSE*
- `close-db-held-recordset`: `Database.Close` at depth 1 raises nothing, Errors count 0. The held dynaset then gives 3420 on `Fields.Count` and `MoveNext`. `ws.Rollback` still succeeds (no error) and A is reversed.
- `release-db-in-transaction`: releasing `db` without Close leaves `ws.Databases.Count` 0, Errors count 0. Rollback succeeds and A is reversed.
- `clone-close-in-transaction`: a clone's `Close()` after Edit/Update(777) raises 3246 (0x800A0CAE). After it, `rs` reads V=10 and `ws.Rollback()` raises 3034, so no level remains. 777 is not in the fresh Workspace.
- `pending-edit-across-rollback`: `EditMode` goes 1 to 0 across Rollback. A later `Update` raises 3020. Reading `rs` fields raises 3420, and both changes (A and 2222) are gone.
- `workspace-close-depth-1`, `workspace-close-depth-3`: `ws2.Close()` with 1 or 3 open levels returns success with Errors count 0. All levels are rolled back. `db2.Name` and `rs2.Fields` then raise 3420.

*TXN-TEMP (lock exhaustion only)*
- `exhaust-locks-commit/-rollback/-close`: `SetOption(62, 5)`; `UPDATE Big` raises 3035 (0x800A0BDB, helpFile "-", helpContext 4294967295) with `RecordsAffected` 0. Big is still 1000 rows with SUM 0 inside the transaction. Commit keeps a/b/c (101/202/303). Rollback reverts them. `Database.Close` with the level open also leaves 10/20/30. Errors still holds 3035 after each continuation.

*TXN-DDL*
- `ddl-create-table-rollback`: CREATE TABLE X plus INSERT are visible in-transaction and gone after Rollback. `SELECT FROM X` raises 3078.
- `ddl-alter-index-drop-rollback`: in-transaction, T gains Z and IX_V, UX_K is dropped and S is dropped. After Rollback the catalog and S rows are restored. Fresh Workspace agrees.
- `ddl-relation-querydef-property-rollback`: FK drop, QNew and the QInsMid SQL change are all reversed. The database property count is 12 on the same handle (TxnProbe still listed) but 11 through the fresh Workspace (see defects).
- `ddl-nested-failure-outer-rollback`: `CREATE TABLE T` fails with 3010. Errors(0)=3010 is still there after CommitTrans and Rollback. The catalog returns to the original after Rollback.

*TXN-LOCAL*
- `local-same-row`, `local-option-retry` (`SetOption` 57=1, 63=1): B's `UPDATE` on row 1 raises 3188 (0x800A0C74, "locked by another session on this machine") with `RecordsAffected` 0. The result is the same with and without the options. After both commit, row 1 = 9.
- `local-two-rows-*` (B edits row 5 while A holds row 1): B raises 3188 as well. Row 1 ends at 9 (`a-commit-b-commit`) or 10 (`b-commit-a-rollback`); row 5 stays 50.
- `local-locked-statement`: with B holding row 3, A's `UPDATE ID=1` and `UPDATE … WHERE ID<=5` both raise 3188 with `RecordsAffected` 0. After `ws2.Rollback`, the repeat succeeds with `RecordsAffected` 5. Final rows are 11/21/31/41/51.
- `local-dao-ado-two-rows`: ADO `BeginTrans` returns 1. The ADO `UPDATE` of row 5 fails: Number -2147467259 (0x80004005), NativeError -533791822, SQLState "3260", "Could not update; currently locked by user 'admin'…". `CommitTrans` and `Rollback` raise nothing. Final rows are unchanged.
- `local-visibility` (B's dynaset opened before A's edit): during A's open level, B's dynaset and a new snapshot both show row 2 = 20. After A commits, with no `Requery`, B's dynaset after `MoveFirst` shows 202 for row 2. `Requery` and a new snapshot also show 202. `Transactions` is True on both `rsd` and `db2`.

**Probe or fixture defects**
- `stmt-expression-fail-d1`: A (ID 1) and the failing statement both write row 1, so row 1 = -50 cannot show whether A survived. The test of A's survival is lost for this probe.
- `ddl-*` probes: the `Catalog` helper reads the `MSys*` `Indexes.Count` (3110) and the property `Connection` value (3251). The Errors read after Rollback therefore returns 3110 (`ddl-alter-index-drop-rollback`, step 29) or 3251 (`ddl-relation-querydef-property-rollback`, step 41), which is harness output and not a Rollback answer.
- `ddl-relation-querydef-property-rollback`: `Catalog` refreshes TableDefs, Relations and QueryDefs but not `Properties`. The same-handle count of 12 after Rollback may be a stale collection; only the fresh-Workspace count of 11 is trustworthy.
- `local-two-rows-*`, `local-dao-ado-two-rows`: B's change is never admitted (3188 / 3260 on a 5-row single-page fixture). The commit-order and rollback-order preservation these probes were built to test is therefore not exercised.
- `local-option-retry`: the record has no timestamps, so retry and delay timing cannot be read. Only attempt a2 is retained.
- `nest-exhaustion`: levels 6 and 7 never wrote a value, so only the 7 Rollback successes witness them. Explicit depth capacity cannot be separated from the statement-failure at those levels.

**Revisit**
- `clone-close-in-transaction`: which call ended the level (the failed Close or the preceding Update) is not isolated. The row was not read between them.
- TXN-STMT is not probed for: first-blocked candidate, validation or lock failures with dbFailOnError, an `Execute` that fails inside a statement on resource or I/O error.
- TXN-MULTI is covered only by `nest-late-file-b`. Not covered: A/B order swapped, aliases to one file, two Workspaces with opposite order, failure admitting or resolving the second file.
- TXN-LOCAL is not covered for: distinct data pages, shared index leaf, different tables, `dbPageTimeout`, optimistic/pessimistic edits, two ADO Connections, COM threads. The 3188 versus 3260 split is unexplained.
- TXN-VIEW has only the dynaset case, and `Transactions` was read only on the dynaset and Database. Not covered: table/forward-only, snapshots and own-Workspace readers, the opened-before versus after-BeginTrans cases.
- TXN-CLOSE has no two-participating-Databases case and no callback or reentry. TXN-FLUSH is not run at all. TXN-TEMP covers only lock-count exhaustion, not TEMP-volume or disk faults. TXN-SUPPORT (ISAM/ODBC, ADO `Attributes`) is not covered.
- No `*-mdb` probe covers DDL, TXN-LOCAL or TXN-CLOSE.

### r-cat-1

All 38 records were read. They are ACE 16.0.20527.20196 through DAO (ACEDAO.DLL 16.0.20527.20196 in the records read). Every failing call returned `hr=0x80020009` with the EXCEPINFO scode named below (decimal Jet number in parentheses). In every create-* probe, `ReturnedFailure` comes only from `Properties("Connection").Value` → 0x800A0CB3 (3251). `parameter-values` has no record under `evidence/r-cat-1/`: the worker died in `Recordset.Requery(QueryDef)` (see docs/work/evidence-log.md, `evidence/r-crash-requery/`).

**Observed**

CAT-NEW (creation, all with `;LANGID=0x0409;CP=1252;COUNTRY=0`)
- `create-accdb-empty`, `-option-omitted`, `-workspace-route`: option 128 (dbVersion120), omitted Option, and `Workspace.CreateDatabase` all give `Database.Version` "12.0", CollatingOrder VT_I4 1033, a 172032 B file, and 5 TableDefs (MSysACEs, MSysComplexColumns, MSysObjects, MSysQueries, MSysRelationships; Attributes -2147483648). The four same-size files have four different sha prefixes (ea467b4c, bb34e59f, b701dfeb, c905a6ec).
- `create-mdb-empty`: option 64 gives Version "4.0", 77824 B, 5 system tables listed in `create-mdb-catalog`'s sibling probes, but 4 here.
- `create-accdb-ext-v40`: an `.accdb` name with option 64 gives Version "4.0", 77824 B, 4 TableDefs (no MSysComplexColumns). The extension did not choose the format.
- `create-mdb-version30`: option 32 → 0x800A0C62 (3170) "Could not find installable ISAM." (source `DAO.Workspace`); no file.
- `create-accdb-existing-destination`: a second CreateDatabase on the same path → 0x800A0C84 (3204) "Database already exists." (source `DAO.Workspace`); `eng.Errors` Count 1; `Databases.Count` 0; file unchanged (172032 B, sha bb34e59f).
- `create-accdb-in-transaction`: `ws.BeginTrans` then `ws.CreateDatabase` → 0x800A0CAE (3246) "Operation not supported in transactions."; no file before or after Rollback; `Databases.Count` 0.
- All created Databases have 11 Properties (Name, Connect, Transactions, Updatable, CollatingOrder, QueryTimeout, Version, RecordsAffected, ReplicaID, DesignMasterID, Connection).

CAT-B
- `catalog-accdb` / `catalog-mdb`: TableDefs Count 9 (ACCDB) and 8 (MDB; no MSysComplexColumns). User tables H, P2, T, U have Attributes 0. `MSysObjects.Fields.Count` is 0 and `.Indexes.Count` → 0x800A0C26 (3110).
- DAO reads of MSysObjects, MSysACEs, MSysQueries and MSysRelationships → 0x800A0C28 (3112) in both formats. MSysComplexColumns gives 3112 in ACCDB and 0x800A0C06 (3078) "cannot find" in MDB. MSysAccessStorage, MSysResources, MSysNavPaneGroups and MSysAccessObjects give 3078 in both. `OpenRecordset("MSysQueries", 1)` also gives 3112 (`msys-queries`).
- `names` (empty ACCDB):
  - `Tbl` then `TBL` → 0x800A0BC2 (3010) "already exists" at TableDefs.Append. `Té` (U+00E9) then decomposed `Te`+U+0301 → 3010. `TÉ` after `Té` → 3010. `Tä` and `a'b` are accepted.
  - ` Lead`, `a.b`, `a!b`, `[br]` and a backquote name → 0x800A0C35 (3125) at Append. `Trail ` (trailing space) is accepted and returned as "Trail ".
  - A 64-character name is accepted. A 65-character name → `CreateTableDef` 0x800A0BB9 (3001).
  - Lookups on the Refresh'd collection: "tbl" → "Tbl", "TÄ" → "Tä", "tÉ" → "Té", and "TRAIL " → "Trail " are found. Decomposed "Te"+U+0301 → 0x800A0CC1 (3265) not found, even though the same string was rejected at Append as a duplicate. "Trail" and "Lead" → 3265. The Refresh'd collection lists a'b, MSys*, Tä, Tbl, Té, "Trail ", xxx… (11 items).
  - `CREATE TABLE C2 (a LONG, A LONG)` → 0x800A0D34 (3380). `CREATE INDEX IX1` after `ix1` on the same table → 0x800A0D2F (3375).
  - `CreateQueryDef("Tbl")` when table Tbl exists → 0x800A0BC4 (3012). `QCASE` after `qcase` → 3012.
- `id-reuse`, `msys-add-objects`: only the DDL side is observed. `CREATE TABLE Z (ID LONG, ID LONG)` → 3380. A `CREATE TABLE R` inside BeginTrans/Rollback ran without error. No ID values were readable (3112).

CAT-Q
- `querydefs` (28 saved queries, collection in name order). QueryDef.Type: select 0, append 64, crosstab 16, DDL 96, delete 32, make-table 80, pass-through 112, union 128, update 48. Saved SQL is returned reformatted with line breaks, e.g. `"SELECT A\r?\nFROM T;\n"`-style text (newlines as logged). Reformatting seen:
  - `[Label]` → `Label`; `DELETE FROM` → `DELETE *\nFROM`; `ORDER BY A DESC, ID ASC` → `ORDER BY A DESC , ID`; `INSERT INTO W (ID, A)` → `INSERT INTO W ( ID, A )`.
  - QUnion3 and QDdl keep their text unchanged; QText keeps `"q"` double quotes.
  - QUnicode keeps `Bä€ 𝄞` and drops the brackets.
  - QPass: SQL as set, Connect "ODBC;DSN=CalcDbProbeNoSuchDsn;", Fields.Count 0, Parameters.Count 0.
- The explicit `PARAMETERS` line keeps the declared names and type spellings (`Short`, `Text ( 10 )`). QImplicit lists one parameter `pMin`. QCrosstab fields: A, "1".."4" (types 10, 7×4). Stored properties: 14 per query, 15 for QProp (extra `Description`). StillExecuting, CacheSize and Prepare → 3251.
- `sql-round-trip`: setting the SQL getter's own text back is stored verbatim with no reformatting (QText, QUnicode, QParam, QOuter, QCrosstab). After reopen the original reformatting (newlines) is back for the ones read again. `SELECT ID FROM T; ` (trailing space) and `SELECT ID FROM T` (no semicolon) both read back `SELECT ID\nFROM T;\n`. No U+0000 appears in any returned text.
- `order-variants`: `PARAMETERS p1, p2` gives Parameters p1, p2 and fields A, B. `PARAMETERS p2, p1` gives p2, p1 and fields B, A. Delete/recreate of QOrdA gives the same order. MSysQueries was unreadable (3112), so no Order bytes.
- `create-accdb-queries-compacted`: CompactDatabase of the queries fixture gives 241664 B → 241664 B with a different sha; `Errors.Count` 0.

CAT-P
- `properties` (read-only reopen, 29 DB properties): types read back as Boolean 1, Byte 2, Integer 3, Long 4, Currency 5, Single 6, Double 7, Date 8 (45000.5), Text 10, Memo 12, Binary 9, LongBinary 11, GUID 15. `PGuidType` (created as `{00112233-…}`, type 15) reads `{guid {00112233-4455-6677-8899-AABBCCDDEEFF}}`. `GUID` and `PBinary16` read as type 9. Query `QP` custom `Description` persists. TableDef T has 11 built-in properties; ConflictTable and ReplicaFilter → 0x800A0BD8.
- `property-inputs`:
  - Null, VT_EMPTY, "" and an empty byte array → 0x800A0D39 (3385) "do not support a Null value".
  - Name only, VT_ERROR (missing) and omitted Value → Append 0x800A0D3A (3386) "Value must be set". An empty Name → 3386 "Name must be set".
  - Boolean from I2 1 or I4 -2 reads back VT_BOOL -1. "yes" as Boolean and "abc" as Long → 0x800A0D5D (3421).
  - Text from I4 5 reads "5". A duplicate name → 0x800A0D27 (3367). Type 99 → 0x800A0CBB (3259). A 64-character name is accepted; 65 → 0x800A0BB9 (3001).
  - Read-only reopen shows Updatable false.
- `property-edits`:
  - Same-length, longer and shorter Text assignments are accepted. `Value=Null` → 3385. Changing `Type` → 0x800A0C93 (3219). `Delete("Version")` → 0x800A0D38 (3384).
  - A deleted Property's Name/Value → 0x800A0D5C (3420) on the held object. `Properties.Count` was 28 after Delete and 28 after Refresh.
  - After `BeginTrans`, `PLong=999`, append `PInTransaction`, `Rollback`: in-session reads give 999 and the new property is present. After reopen PLong is 123456 and PInTransaction is absent (Count 28).
- `field-rules`:
  - Field `Required=True` is accepted even though row 2 has B Null. `Field.ValidationRule("[B] >")` → 0x800A0CF8 (3320) (text says "table-level"); the property reads "". "[B] > 100" is accepted. After reopen it reads "[B] > 100 " (trailing space).
  - `DefaultValue("1 +")` → 3320; `DefaultValue("NoSuchFunction()")` → 0x800A0D3C (3388). Both read back "".
  - TableDef `ValidationRule "[B] <> [ID]"` persists.
  - INSERT without B → 0x800A0CF2 (3314); B=5 → 0x800A0CF4 (3316) "B too small"; B=500 succeeds.
  - `DBEngine.Errors` Count stays 1 (the old error) after later successful calls here and in `drop-column`, `unresolved-identifier` and `nested-querydef`.

CAT-V
- `temporary-querydef`: `CreateQueryDef("", sql)` gives Name "#Temporary QueryDef#" and SQL unformatted. `qdfs.Append` of it → 0x800A0C93 (3219). `CreateQueryDef()` plus SQL, Name and Append succeeds (count 28→29) and the SQL is reformatted on read. A named CreateQueryDef bumps the held count immediately (30).
- `collection-refresh`: QueryDefs.Count updates immediately on the creating Database (28→29). TableDefs.Count stays 8 after `Execute("CREATE TABLE TRefresh")` until `Refresh` (9). A second Database and a second Workspace opened after the create see 29. After one more create, their held QueryDefs.Count stays 29 until `Refresh` (30).
- `held-sql-replace`: after `qd.SQL(newText)`, the held Parameters keep order pId, pB (pId VT_I2 4, Value 1 kept; pB Empty). The same object (id u4) returns the new SQL unformatted. After reopen the order is pB(4), pId(10) and the SQL is reformatted.
- `held-querydef-lifetime`:
  - `qd.Name("QOneRenamed")` succeeds; `qdfs.Item("QOne")` → 3265. `qdfs.Delete` then the held qd/Field → 0x800A0D5C (3420) on Name, SQL, Field and OpenRecordset.
  - Recreating the same name does not revive the held qd (still 3420).
  - After `db.Close`, held `qn.SQL` and `qdfs.Count` → 3420 (source `DAO.QueryDef` / `DAO.Database`).

CAT-I
- `select-star`: the held QueryDef's Fields stays at 4 (ID, A, B, C) after `ADD COLUMN E` and after `DROP COLUMN C`. Fresh recordsets show 5 (…E) and then ID, A, B, E.
- `rename-column`: renaming T.A to A2 succeeds. QRen then reads Parameters.Count 1 ("A", type 10), SQL unchanged, and OpenRecordset → 0x800A0BF5 (3061) "Too few parameters. Expected 1." The same happens for `db.OpenRecordset("QOne")`.
- `drop-column`: `DROP COLUMN C` with a dynaset open → 0x800A0C8B (3211). After Close, `DROP COLUMN B` succeeds although QDep and QGroup use B. The held qd.SQL is unchanged, held Fields still lists ID (4) and B (type 9). Opening via the held QueryDef, a fresh lookup, or QGroup → 3061.
- `column-type-change`: `ALTER COLUMN B TEXT(10)` is accepted. QType's X goes VT_I2 4 → 7 and values VT_I4 11/21/Null/41 → VT_R8 11/21/Null/41.
- `unresolved-identifier`: `SELECT ID, Z FROM T` has Parameters.Count 1 ("Z", 10) and open → 3061. After `ADD COLUMN Z LONG` the held Parameters still lists Z, but OpenRecordset succeeds with Z as a Long column (Null values).
- `nested-querydef`: after `QBase.SQL = "SELECT ID FROM T;"`, QNested open → 3061 and its Fields shows B with Type 0. Restoring `SELECT ID, B FROM T;` makes QNested open again (10, 20, Null, 40).
- `recreate-table`: `DROP TABLE W` and a recreate with ID TEXT(5), A LONG succeed with QW and QUnion3 saved. A new open of QW shows ID type 10, A type 4.
- `wildcard-mode`: through DAO, QLike89 (`LIKE 'a*'`) returns IDs 1, 2, 4 and QLike92 (`LIKE 'a%'`) returns none. Through ADO/ACE.OLEDB.12.0 the result is reversed (QLike89 none, QLike92 1, 2, 4). The same holds for the inline SQL (`a*`, `a%`).

**Probe or fixture defects**
- `create-accdb-catalog`, `create-mdb-catalog`: the TableDefs collection held from the creation view is reused after the `CREATE TABLE` statements. `tdfs.Item("H")` → 3265, so H was never marked dbHiddenObject; `catalog-accdb` and `catalog-mdb` show H Attributes 0. These fixtures have no hidden object. `collection-refresh` shows the cause (TableDefs not refreshed after DDL).
- `create-accdb-properties`: same cause (`tdfs.Item("T")` → 3265, step 109). No TableDef, Field or Index custom properties were created, so `properties` covers only database and query custom properties.
- `msys-queries`, `msys-queries-compacted`, `msys-add-objects`, `id-reuse`, `order-variants`, `recreate-table`, `names`, `catalog-*`: the MSys rows cannot be read through DAO for the default Admin (3112). The records hold only the refusals. Raw-file capture is needed for catalog IDs, flags and Order bytes.
- Not a defect, but a labelling note: the create-* records all show `ReturnedFailure`, only because of the Connection-property read.
- `mdb-empty` description line: the 5 vs 4 system-table counts I gave above were not both observed (the empty-MDB count is 4 from `create-accdb-ext-v40`'s sibling listing); please re-check with `calcdb-oracle show` before quoting it.

**Revisit**
- `parameter-values` (Requery with parameter changes, shared vs private parameter state across two QueryDef instances) is not retained as a normal record. CAT-V's Requery branch is uncovered.
- Not covered: General – Legacy / General97 / other locale strings, Password and dbEncrypt creation, and invalid credentials. Also not covered: denied folder/file access, creation races, partial-file cases and repeated-image checks. No raw byte, page or MSysObjects/MSysQueries data, and none for the CAT-D relationship rows.
- Held-object cases not run: different Workspace/Database for `Parameters` and `Fields` Refresh; `Connection.QueryDefs` and `QueryDef.Parameters.Refresh`.
- CAT-I: no function-registry, sort/locale or sandbox variants; no ID-reuse readout.
- `names`: the unexplained result that decomposed "Te"+U+0301 is a duplicate at Append but not found by Item. Only ASCII, a few accents and one 64/65 length pair were probed.
- Binary property contents (PBinary16, GUID, PLongBinary) show only `VT_ARRAY|VT_UI1` in `show`; their lengths and bytes need the raw record.

### r-sql-1

Environment for all 43 records: ACEDAO.DLL 16.0.20527.20196 (book seed says ...20016; ACEERR.DLL is ...20016), `db.Version` "16.7", `DBEngine.Version` "16.0". Fixture S/N/AG etc. were created through DAO. DAO type codes: 4 Long, 5 Currency, 6 Single, 7 Double, 10 Text, 12 Memo, 16 BigInt, 20 Decimal; Snapshot = option 4.

**Observed**

*SQL-B (syntax, scope, metadata)*
- Direct `OpenRecordset` (DAO) and ADO (`Microsoft.ACE.OLEDB.12.0`, `cn.Version` "10.0") give the same rows for a LEFT JOIN query. `LIKE 'a%'` returns id 1 in ADO and no rows in DAO; `LIKE 'a*'` is the reverse. ADO error for a missing table: Number -2147217865 (0x80040E37), NativeError -524420377, SQLState "3078". `b-ado`
- Brackets: `[Order]` works but unbracketed `Order` also opened and returned data. Backtick-quoted names and `[x]]y]` alias fail with 3141 (0x800A0C45). `b-brackets`
- Unresolved `nosuch`, `L.nosuch` and `x.id` all give 3061 "Too few parameters. Expected 1." at `OpenRecordset`. On a temp QueryDef, `Parameters` shows an implicit `nosuch` (Text); after setting it to 2, rows return it as VT_BSTR "2". An ambiguous `id` in `L, LD` gives 3079 (0xC07) at `OpenRecordset`; `CreateQueryDef` succeeded and reading its `Parameters` raised 3079. `b-name-resolution`
- Parameters: declared `[p B]` is listed as "[p B]" (Item("p B") works); undeclared `pC` appears as implicit Text. `PARAMETERS a Long, a Text(255)` gives 3060 at `Parameters.Count` and `OpenRecordset`. `[p]`/`[P]` give one parameter "[p]" (Text). A declared but unused `pA Long` still gives 3061 at open. `b-parameters`
- Join nesting: `(L LEFT JOIN R) INNER JOIN RX` and `L LEFT JOIN (R INNER JOIN RX)` both give 3296 (0xCE0) "JOIN expression not supported". `(L LEFT JOIN R) LEFT JOIN RX` works. Unparenthesised `INNER JOIN … INNER JOIN` gives 3075. `b-join-nesting`
- Join bound types: Memo=Memo gives 3118; `Left(m,255)=t` works; Single=Double and Currency=Double join (and Single 0.1 matched Double 0.1, Currency 0 matched Double -0); Long=Text gives 3615. `b-join-bound-types`
- IN contexts: `IN (1,3,Null)` returns 1,3; subquery `IN` returns 1,2; PIVOT with `IN (10,12,99)` gives fields lid,"10","12","99" (Double), without IN gives "10","11","12". `b-in-contexts`
- Output names: `SELECT *` join → id,name,rid,lid,qty; same-named columns from aliases → "a.id","b.id"; expressions → Expr1000, Expr1001; `L.*, R.lid, R.lid` → id,name,Expr1001,lid. An empty result keeps typed Fields and BOF=EOF=-1, RecordCount 0. `b-output-names`
- Limits: SQL length (spaces padding) 32000/63999/64000/64001 accepted via direct open and temp QueryDef; 65535/65536/65537/131072 give 3035 at both `OpenRecordset` and `CreateQueryDef`. `b-limit-length`. AND chains: 50 ANDs ok, 98 and 99 → 3072 "Expression too complex", 100/101/200/1000 → 3360 "Query is too complex". Paren depth 16 ok; 32, 50, 63, 64, 65, 100, 128, 256 → 3075 "Expression too complex in query expression". `b-limit-and-nesting`
- Saved QueryDef: `.SQL` comes back reformatted with line breaks and a trailing `;\n`; Type 0 (select), 128 (union). `b-routes`, `union-mixed-tokens`. Saved-query nesting (QL, QL2, derived tables) opens fine. `b-saved-nesting`

*SQL-J (joins)*
- `LEFT JOIN … WHERE R.qty > 0` returns one row (1,11,1,5). Every `ON … AND <predicate>` variant is refused with 3296 (5 of 6 `j-on-variants` queries plus `j-where-vs-on`). `WHERE CLng(L.name)>0` gives 3464; `WHERE R.qty > Null` opens with zero rows. `j-where-vs-on`, `j-on-variants`
- Null/duplicate keys: L1 pairs with RX 21 then 20; L2 and L3 get Null extensions (L3 matches RX 23 with Null qty); Null `LD.id` never matches. `j-null-keys-dups`

*SQL-EQ*
- Text (ACCDB = MDB records identical): GROUP BY/DISTINCT merge "abc", "abc ", "ABC", "Abc" (5 rows, count 5) and "ae"/"æ" (count 2); " abc", "äbc", "abc\0x", "abc\0y" and the two 55-char prefixes stay separate; Null and "" stay separate. The join `a.t=b.t` pairs the same groups. Representative differs: DISTINCT shows "æ" (GROUP BY shows "ae"); UNION name∪t shows "æ"; UNION t∪m shows "abc " and "ae". `eq-distinct-text(-mdb)`, `union-types(-mdb)`
- `DISTINCT m` (Memo) gives 3163 (0xC5B); `GROUP BY m` works. `eq-distinct-text`
- `GROUP BY v` over i/d/c UNION ALL: Double -0 and 0 are separate groups; 0.1 merges Double and Currency; result is Double. BigInt 9007199254740992/…993 stay distinct (VT_I8) alone but merge to 9007199254740992 (count 2) after UNION with Double. `eq-cross-type`, `eq-numeric-domains`
- `DISTINCT CLng(t)` and `GROUP BY CLng(t)` give 3464 at open; `DISTINCT Val(t)` returns 0,1,2. `eq-distinct-error`

*SQL-AGG*
- Empty/Null: no GROUP BY on empty → one row Count 0, Sum/Avg/Min/Max/First/Last Null; GROUP BY on empty → no rows; `HAVING Count(*)=0` → (0, Null); single Null row → Count(*) 1, Count(d) 0, Sum Null. `agg-empty`
- Count: Count(*) 6, Count(d)/(s)/(t) 5, `Count(Null)` 0, `Count('A & B')` 3, 4-table cross join 108, all VT_I4 (4/4); `Count(DISTINCT t)` → 3075; `Count(CLng(t))` → 3464. `agg-count`
- Types: Sum of Byte/Int/Long/Single/Double/Date → Double (7/8); Sum(Currency) → Currency (Overflow 6 for the max value); Avg same types; Min/Max keep source type except Byte → Long (4/4); `Sum(2.5)` → Decimal (20/16, VT_DECIMAL). Sum(Single) = 2.100000001490116. `Sum('3')` 18, `Sum(True)` -6 (Double); typed parameters: Sum(Currency) → CY 7.5, Double → R8 0.6, Long → R8 12. `agg-result-type`, `agg-arg-kinds`
- Order: g1 (2^53,1,-2^53) Sum 0; g6 reversed Sum 1, Avg 0.3333333333333333; g7 Sum 0; combined Sum 2, Avg 0.2222222222222222 — consistent with sequential Double accumulation in id order. A FROM-subquery `ORDER BY id` and `ORDER BY id DESC` both gave Sum 0 (no visible effect). `agg-order`
- First/Last: Null rows are returned, not skipped (g4 First/Last Null, g8 First Null/Last 4, g9 First 2/Last Null); same with the (g,d DESC) index table AGI; subquery ORDER BY DESC gave First 2/Last Null (no visible effect). `agg-first-last`
- Singletons: one value → Var Null, VarP 0, StDev Null, StDevP 0; zero values → all Null; (1,3) → Var 2, VarP 1, StDev 1.4142135623730951, StDevP 1. `agg-single`
- Same `Sum(d)` in SELECT, HAVING and ORDER BY works (g8 6, g2 5). `agg-occur`

*SQL-UNION*
- Names come from the first arm; arity mismatch → 3307 at open; Long+Text → Text 10/255; Date∪Double → Text ("1899/12/31", …); empty typed first arm still yields Text. `SELECT Null … UNION SELECT id` gives Text 10/255 whose values are the raw bytes as BSTR (Long 1 → U+0001 U+0000); `UNION SELECT c` (Currency) gives raw int64 as 4 UTF-16 units (0.1 → U+03E8,0,0,0). Reversed (`c … UNION SELECT Null`) stays Currency 5/8. `TABLE L UNION …` and saved-query arms work. `union-binding`, `union-types`
- Tokens: `UNION … UNION ALL` returns two rows; `UNION ALL … UNION` returns one; both are preserved in saved SQL. `union-mixed-tokens`
- UNION with a failing `CLng(t)` arm → 3464 at open; UNION ALL opens, and the failing row raises 3071 on `MoveNext`, then reads as Null and iteration continues (8 rows). `union-error-order`
- t∪m: UNION → Memo size 0, UNION ALL → Memo size 120. `union-types`

*SQL-SUB*
- Scalar: 0 rows → Null; 2 rows → 3354 (0xD1A) at open; 2 columns → 3306; correlated multi-row also 3354. `sub-scalar`
- IN/ANY/ALL (type 3/2, VT_I2 0/-1/Null): empty set → IN 0, NOT IN -1, `> ALL` -1, even for Null outer; all-Null set → Null for IN/NOT IN/SOME/`< ALL`; `NOT IN (… RX.lid)` with a Null member: 0 for matches, Null otherwise. `sub-in-quantified`
- Correlation: alias `L` shadows outer; `L2` alias with `L.id` inside → 3061; a declared parameter `lid` beat column `R.lid` (c=2 for all rows). `sub-correlated`
- EXISTS/IN with failing `CLng(t)`: succeeds when a match precedes the bad row (`=1`, `1 IN`), 3464 at open when it must reach it (`=2`, `2 IN`); unused projection `CLng(t)` is not evaluated. `sub-exists-demand`

*Demand/phase (SQL-REL)*
- `SELECT id, CLng(t)`: open succeeds; error 3464 appears on that Field's `.Value` for the bad row, next rows read fine; same for snapshot, dynaset (2) and forward-only (8); `MoveLast`/`MoveFirst` raise nothing and `RecordCount` is 5. With `WHERE CLng(t)>0`: `MoveNext` to the bad row raises 3071, `Value` then gives 3021, and the row is absent (ids 1,2,4,5). `demand-conversion`

**Probe or fixture defects**
- `ND` (`DECIMAL(18,4)`) was never created: `CREATE TABLE` gave 3292 (0xCDC) in both main fixtures, so every ND query answers 3078 "cannot find ND" (`agg-result-type` ×4, `b-ado`, `b-join-bound-types`, `eq-cross-type` ×2, `eq-numeric-domains`, `union-types(-mdb)` ×2). All Decimal-column branches are therefore empty. `create-accdb-main`, `create-mdb-main`
- `NB` (BIGINT) is refused in MDB (3001 "Invalid argument"); no MDB probe uses it, but the MDB fixture lacks it.
- `agg-numeric` is one statement over g10, g11 and g12 (1e308 twice); it fails with Overflow 6 at open, so no Var/StDev recurrence value was retained.
- `agg-occur`'s TX variants all fail 3464 at open, so they show nothing about shared aggregate evaluation or error phase.
- `b-parameters` never sets values for `[p]/[P]` and the unused-parameter query, so 3061 is only "unset".
- Field metadata: `Precision` returned VT_I2 1033 (Scale 0) only on fields with a SourceTable; on computed fields it raised 3021 (plain/aggregate), 3018 (PIVOT) or 3078 (temp QueryDef). `DecimalPlaces` is always 3270. 1033 looks like an LCID, not a precision; treat as noise.
- `DBEngine.Errors` is not cleared by later successful calls, so the "Errors" step after a success may repeat an earlier error (e.g. 3270, 3118).

**Revisit**
- Not covered: SQL-R (index ranges), external-file `IN`, OWNERACCESS, SQL-UNION-04 effects, SQL-SUB-06, SQL-AGG-SPILL, DISTINCT-with-trace (SQL-EQ-04), Decimal/Date boundaries, Memo/OLE/Hyperlink beyond Memo joins, ADO beyond `b-ado`, action queries, incremental pulls or Requery.
- Limits are not bracketed: AND 50→98, parens 16→32, length 64001→65535.
- ON-clause predicates are refused, so ON-vs-WHERE placement cannot be compared by this route.
- Tie order under ORDER BY is not id-stable (`ORDER BY d`: id 6 before 1; `ORDER BY t`: ids 3,4,5,6,16, reversed under DESC); A/A repeats were not run.
- Source of the raw-byte Text values in `UNION Null … UNION <non-text>` and of the DISTINCT/GROUP BY/UNION representative choice is unexplained.

### r-sql-2-3

42 retained records (ACCDB fixture unless noted). All calls were DAO `Execute` or `OpenRecordset`. Flags: no flag = options omitted, 128 = `dbFailOnError`. Unless noted, the observations are for ACE 16.0 on the `.accdb` fixture.

**Observed**

*R-SQL-2 action semantics*
- `UPDATE` assignments read the original row. `SET V=W, W=V` swaps, and the textual order does not matter. `V=W+1, W=V+1, S=V&'-'&W` gives V=31, W=11, S="10-30" for the row (V=10, W=30) (`act-upd-assign`).
- Two assignments to one field fail with 3063 "Duplicate output destination 'V'", RecordsAffected 0, nothing changed (`act-upd-assign`).
- With no flag, a failing candidate is skipped without an error and later candidates continue. The rows that did change are counted, and Errors stays 0 (`act-fail-unique`, `act-fail-validation`, `act-fail-relationship`, `act-fail-insert-required`, `act-ins-defaults`).
  - Unique: `UPDATE T SET U=U+1` changes only ID 4 (U 104→105); RecordsAffected 1, Errors 0 (`act-fail-unique`, `mdb-fail-unique`).
  - Validation: `W=W-15` skips the W=10 row; RecordsAffected 3 (`act-fail-validation`).
  - Relationship: `DELETE FROM P` deletes only childless P2; RecordsAffected 1 (`act-fail-relationship`).
  - Required: `INSERT…SELECT` appends 3 of 4 rows and skips the Null `R` row; RecordsAffected 3 (`act-fail-insert-required`).
  - Required: a single-row `INSERT … VALUES (Null)` gives RecordsAffected 0 and Errors 0 (`act-ins-defaults`).
- With 128, the surviving effect depends on the failure class (error numbers are the `Errors` numbers):
  - **Unique, 3022:** `UPDATE T2 SET U=U+1` leaves ID 4 at 104. ID 1 is the first candidate to fail, so stop-at-first and reversal look the same (`act-fail-unique`, `mdb-fail-unique`).
  - **Required, 3314, `INSERT…SELECT`:** nothing is appended, although the first candidate (ID 11) would have succeeded (`act-fail-insert-required`).
  - **Validation, 3316:** ID 1 stays changed (W 30→15). IDs 3 and 4 are untouched, so the statement stopped at ID 2 with no reversal (`act-fail-validation`).
  - **Relationship, 3201:** `UPDATE C2 SET PID=PID+1` leaves IDs 10 and 11 changed (1→2). ID 12 failed, and RecordsAffected is 0 (`act-fail-relationship`).
  - **Relationship, 3200:** `DELETE FROM P2` leaves all 3 rows (`act-fail-relationship`).
  - **Conversion, 3464:** `UPDATE T2 SET V=S` leaves V unchanged (`act-fail-conversion`).
- No-flag conversion: `UPDATE T SET V=S` sets V to Null for the row with S='x' and RecordsAffected is 3, not 4 (`act-fail-conversion`). The same pattern appears in `SELECT ID, CLng(S) INTO MF1`: 3 reported, 4 rows stored, Null for 'x' (`act-make`).
- Locks: a second Workspace held pessimistic `Edit` on ID 2 of `T` and `T2`.
  - `UPDATE T` with no flag returned success, RecordsAffected 0, Errors 0.
  - `UPDATE T2` with 128 failed with 3188.
  - All 4 rows of both tables were unchanged. The ACCDB and MDB results are identical (`act-lock-blocked`, `mdb-lock-blocked`).
- Transaction frames: in an open `BeginTrans`, a failed 128 `UPDATE` (3022) kept the earlier `INSERT` of row 50. Inner `Rollback` reverted the inner `INSERT` of row 51 and the `T2` update, and outer `CommitTrans` kept row 50 (`act-trans-frames`).
- RecordsAffected:
  - Counts include no-op assignments: `SET V=V` gives 4, `WHERE 1=0` gives 0 (`act-records-affected`).
  - `QueryDef.Execute` sets the QueryDef's count; `Database.RecordsAffected` stays 0 until `db.Execute` runs (`act-records-affected`, `act-qdef-routes`).
  - Cascades are not counted: update of `CP` PID 7→9 gives 1 and delete gives 1, while `CC` rows follow (`act-cascade`).
  - `db.RecordsAffected` read 4 after successful `ALTER`/`DROP` DDL (`DROP CONSTRAINT FK_C2` on a 3-row table), which looks stale (`act-ddl-columns-indexes`).
- Errors collection: after a failure, a later successful `Execute` still showed `Errors.Count=1` with the old error. Examples: 3078 stayed after the no-flag `V=S` (`act-fail-conversion`), and 3314 and 3058 stayed after later successes (`act-ins-defaults`, `act-upd-join`). That is consistent with the collection not being cleared; no probe isolated it.
- Joined updates:
  - `P INNER JOIN C SET P.Name='k'` gives RecordsAffected 3 (P1 matched twice). `SET P.Name=C.Tag` leaves P1 = "c11", the last child, and P3 = "c12".
  - `UPDATE DISTINCTROW` gives RecordsAffected 2.
  - `UPDATE QDistinctP` fails with 3073.
  - Two-target `SET P.Name, C.Tag` succeeds, RecordsAffected 3, and changes both tables.
  - `LEFT JOIN … SET C.Tag` fails with 3058.
  - With `dbConsistent` (160) and `dbInconsistent` (144), `SET C2.PID=2` succeeds and RecordsAffected is 1 each time (`act-upd-join`).
- `INSERT…SELECT` from the target table is finite. Direct gives 4, through `QAllT` gives 8, and joined to `P` gives 3. Appended rows do not re-enter the source (`act-ins-self`).
- INSERT mapping:
  - A `SELECT` without a column list maps by alias name: row 61 has U=700, V=2.
  - A short select fails with 3314 on required `R`; one extra value fails with 3346.
  - Insert through `QT15` (`WHERE V>15`) succeeded with V=1, and insert through the join `QPC` succeeded (`act-ins-mapping`).
- Defaults:
  - An omitted `D` gets 5 and an explicit Null stays Null.
  - Empty string `''` is accepted for the required `Req`.
  - `Req=Null` fails with 3314 when flagged and silently inserts 0 rows when not.
  - AutoNumber went 1, 2, 3, then 6, so the two failed inserts consumed 4 and 5.
  - After the colliding explicit ID 1 (3022), a plain `INSERT (Req)='h'` also failed with 3022 (`act-ins-defaults`).
- Delete with a held dynaset: after `DELETE` of its current row, every field read returned 3167 "Record is deleted.", also after `MovePrevious`. `DELETE C.* … WHERE P.Name='p1'` gives 2. `DELETE P.* … JOIN C` fails with 3200 and leaves P intact (`act-delete`).
- Parameters: an unassigned `DELETE` parameter fails with 3061 "Too few parameters. Expected 1.". Assigned 2 gives 1 row, 99 gives 0, and Text "3" gives 1. `db.Execute("QDel")` and the direct `PARAMETERS` string also give 3061 (`act-qdef-params`).
- Make-table (`act-make`):
  - Errors: existing target gives 3010; duplicate output name gives 3191; with 128, `MF2` and `MD` were not created.
  - Types in `MT`: `V*1.5` is Decimal (20), `S&''` is Text(255), `Null AS N` is type 9 (size 510), and `V>15` is type 3 with -1/0.
  - AutoNumber attributes carry into `MR` (Attributes 17).
- DDL (`act-ddl-columns-indexes`, `act-ddl-objects`):
  - `ALTER COLUMN X2 TEXT(3)` truncated 'abcde' to "abc".
  - `ALTER COLUMN S LONG` over 'x' fails with 3464; `ALTER COLUMN V TEXT(10)` on an indexed field succeeds and keeps `IX_V_T`.
  - `DROP COLUMN U` (unique index) fails with 3280; duplicate-creating unique index and constraint fail with 3022.
  - Re-adding a FOREIGN KEY over an orphan fails with 3379.
  - These fail through `DAO.Database.Execute`: `CHECK` with 3289; `WITH COMPRESSION`, `CREATE TEMPORARY TABLE`, `CREATE VIEW`/`PROCEDURE`, `CREATE USER`/`GROUP` with 3290; `DROP VIEW`/`PROCEDURE` with 3295; `GRANT` and `ALTER DATABASE PASSWORD` with 3129.
- `WITH OWNERACCESS OPTION` on a default-Admin file: accepted in SQL, and the clause is kept in the saved `QueryDef.SQL`. The saved SELECT is `Updatable` -1 and `Execute` on it gives 3065. Saved `INSERT` with it runs with RecordsAffected 1 (`act-owneraccess`).

*R-SQL-3 DISTINCTROW*
- `DISTINCTROW A.Label` returned 3 rows (DISTINCT 1, plain 6). `A` holds identical rows a1 and a2 and a3 differing only in an unprojected field, so this is per physical `A` row (`dr-whole-row`).
- The same per-row pattern held for `AK` (3 rows). `B.Tag` alone gives 2 rows, and projecting both families gives 6 (`dr-participation`).
- A constant projection `1 AS K` gives 6, `UCase(A.Label)` gives 3, and saved `QAB` projecting `Label` gives 6 (`dr-calc-saved-outer`).
- `LEFT JOIN`: `A.Label, B.Tag` gives 8 rows, including `z`/Null and the all-Null `A` row. `B.Tag` alone gives 3 rows: Null, b1, b2 (`dr-calc-saved-outer`).
- `SELECT DISTINCTROW A.Label, A.Hidden … JOIN B` has `Updatable` True. Edit changed one occurrence, and `Delete` removed one physical `A` row (x,h)#2. Plain join and `DISTINCT` are not updatable (`dr-updatable`).
- With `AM` (3 identical + 1 different Memo/Bin): `DISTINCT` with Memo fails with 3163, and with LongBinary fails with 3114 (`dr-long-values`).

*R-SQL-3 TOP*
- `TOP 0`, `TOP 0 PERCENT`, `TOP 101 PERCENT` and `TOP -1` fail with 3141. `TOP +2` returns 2 rows, `TOP 11` returns 10, and `TOP 2.5` returns 2 (`top-arguments`).
- PERCENT on 10 rows: 10→1, 15→2, 25→3, 99→10, 100→10, 2.5→1 (`top-arguments`).
- Ties at the cutoff are all returned. `TOP 3 ORDER BY K,T` returned 5 rows, with the b/B/b ties in order IDs 7, 3, 2. `TOP 2 … WHERE K>1 ORDER BY K` returned 4 rows, in order 7, 4, 3, 2. With no `ORDER BY`, `TOP 3` returned IDs 1, 2, 3 (`top-ties-order`).
- Null sorts first ascending (`top-ties-order`).
- Demand:
  - `TOP 5 … 1/(ID-5)` opens, and the error "Division by zero" (0x800A000B) appears only when row 5's field is read.
  - A failing sort key under `TOP 2` fails at open.
  - A failing projection beyond the cutoff (ID 9) raised nothing (`top-demand`).

*R-SQL-3 parameters and crosstab*
- An undeclared `[Which K]` appears as Parameter "[Which K]", type 10. It can be assigned by "Which K". An unassigned open fails with 3061 (`params-implicit-temp`).
- A duplicate `PARAMETERS pK` pair is accepted at `CreateQueryDef`, then reading `Parameters.Count` fails with 3060 (`params-implicit-temp`).
- The saved `QParam` shows `QueryDef.Fields` (3 fields) before parameters are assigned. Assigning "abc" to a Long parameter fails at assignment with 3421; the old value 4 stayed. `Requery` on a dynaset returned the same rows after `pK` changed, so the new value was not applied (`params-saved`).
- Crosstab headings: Null pivot values form the first column named `<>`. 'a' and 'A' merge. 3.0 is named "3_0", `.` `!` `]` become `_`, and dates render "2024/01/02". A 70-character value became a 64-character heading (`pivot-headings`).
- The fixed `IN ('A','a')` and `IN (...,'a')` duplicates fail with 0x800A0C20 (`pivot-headings`). `PIVOT CStr(PN)` fails with 0x800A005E "Invalid use of Null" (`pivot-headings`).
- Fixed headings keep list order: `QXFixed` fields are b, a, zz (`pivot-schema-lifetime`).
- Empty input: dynamic gives 1 field and 0 rows, and fixed gives its headings with 0 rows. Outside-list-only still returns all 4 row groups with Null cells (`pivot-empty-null`).
- Missing cells are Null for all value functions. A seen cell with all-Null `Amt` gives `Count(Amt)` 0 and Sum/Avg/First Null (`pivot-cells`).
- `QueryDef.Fields.Count` is 0 for a dynamic crosstab before `OpenRecordset`, and 4 for the fixed one. After `OpenRecordset` it was the same 4 at first fetch, `MoveNext`, `MoveLast` and after the insert (`pivot-schema-lifetime`).
- Heading limit: with a `Grp` column, 253 headings (254 fields) and 254 headings (255 fields) work. 255 headings fail with 0x800A0C85 "Too many crosstab column headers (256)"; 256, 300 and 1000 headings give (257), (301) and (1001) (`pivot-limit`).

**Probe or fixture defects**
- `act-fail-relationship`: the no-flag `UPDATE C SET PID=PID+1` was meant to move two children to existing parent 2. The probe's earlier `DELETE FROM P` had already removed P2, so all three candidates failed; the mixed case is untested.
- `pivot-schema-lifetime`: the `Requery` step failed with 0x800A0CB3 "Operation is not supported for this type of object" because the recordset was opened as type 4 (snapshot). The Requery branch is untested; `params-saved` shows `Requery` working on a dynaset.
- `pivot-headings`: the reversed-order fixed `IN ('c','b','a','zz','a')` includes a duplicate, so it fails at open and the order observation is lost there (order comes from `QXFixed`). `PIVOT CStr(PN)` fails only because `PN` contains Null.
- `act-fail-insert-required`: the description says "into T from T2", but the second statement is `INSERT INTO T2 … FROM T2`.
- `dr-long-values`: all 4 `AM` rows give 4 DISTINCTROW rows, which is the physical-row count, so the comparison scope for unprojected long values is not discriminated. The `DISTINCT` Memo error 3163 may also reflect Memo truncation at 255 characters.
- `act-ddl-objects` and `act-ddl-columns-indexes`: all go through DAO `Database.Execute`, so the ANSI-89 syntax refusals (CHECK, VIEW, PROCEDURE) may not reflect ANSI-92 or ADO. Several `RecordsAffected` and `Errors` readings after DDL or successes are stale carry-overs, so a read there is not that call's answer.

**Revisit**
- Not covered at all: `ACT-SEL-02` to `ACT-SEL-06` (leaf splits, callbacks, new or deleted rows during a statement, retention failure); `ACT-UPD-05` (reentrant calls); `ACT-INS-05` (two files); the `SQL-TOP-01` memory/spill case.
- `ACT-SEL-01` covers only RecordsAffected and final V for the BETWEEN cases. Repeated-key and revisit evidence is thin.
- Unresolved: whether 3022/3200 stop at the first failure or reverse. In those probes the first candidate is the failing one.
- Unresolved: whether the lock covers a row or a page. All 4 rows were blocked or unchanged in tables of 4 rows.
- Unresolved: why the `INSERT (Req)='h'` after the colliding ID 1 failed with 3022 (the AutoNumber after a failed explicit ID).
- Unresolved: what `Null AS N` in `SELECT INTO` produces (type 9, size 510), and `R.Req` AllowZeroLength (only `T2.R` was read, as False, yet `''` was accepted).
- Not isolated: whether a successful `Execute` clears Errors.

### r-dao-1-err-1

All 42 records were read, 11 of them through the `show` output and the rest from the JSON. The two manifest records carry their data only in the JSON (`describeType`), so I summarised their structure. Lane and engine for every record: one .NET 11 worker (`X64 MainSTA`), late-bound IDispatch, `DAO.DBEngine.120` (or `ADODB.Connection`). In the ACE-engine records, failures return `hr=0x80020009` with EXCEPINFO `scode=0x800A0000|n`. Every `Errors` read in the DAO probes below shows `Number == n` for the last entry.

**Observed**
- **Provenance.** Loaded `ACEDAO.DLL` is 16.0.20527.**20196** (sha256 `335ca676…`), not the book's 20016. `ACEERR.DLL` is 20016. `eng.Version()` returns "16.0". `CreateDatabase` with option 128 gives `db.Version()` "12.0" and with option 64 gives "4.0" (`create-accdb-base`, `create-mdb-base`).
- **DAO-MANIFEST** (`manifest-library`, `manifest-runtime-types`).
  - Library `{4AC9E1DA-5BAD-4AC7-86E3-24F4CDCECA28}` v12.0 "DAO" has 77 types: 26 enums, 1 module, 41 dispatch, 9 coclasses.
  - `Users`, `Groups`, `Connections` and `SystemDB` are declared.
  - Runtime types of 31 live objects:
    - `eng` is `_DBEngine`.
    - `OpenRecordset` returns `Recordset2`.
    - Table and recordset fields are `Field3`.
- **DAO-BOOT.**
  - First touch by Workspaces, `OpenDatabase` or `BeginTrans` each produced one default workspace named "#Default Workspace#", with `UserName` "admin" (`boot-first-*`).
  - `SystemDB()` returned "" before the first touch and "system.mdb" after it (`sec-logon-defaults` against `boot-first-workspaces`).
  - Setting `DefaultUser("Admin")` before first use made `ws.UserName()` "Admin" (`sec-logon-defaults`).
  - After closing the default Workspace: `Count` 1, same wrapper, `Name` still readable, `eng.OpenDatabase` still works, and the old Database gives 3420 (`boot-close-default`).
  - `CreateWorkspace` does not append (`Count` stays 1) and the unappended workspace can open a database. A duplicate "W1" appends (`Count` 3, `Item("W1")` returns the first). An empty name appends with 3271 and `Count` stays 3 (`boot-createworkspace`).
  - The same file opened twice in one Workspace gave 2 Database identities, `Count` 2, and `Item(name)` returned the first. A third open with Exclusive=True succeeded with 0 errors (`boot-open-twice`).
  - `Rollback` and `CommitTrans` without `BeginTrans` gave 3034 (`boot-first-begintrans`).
- **DAO-CREATE** (`create-workspace-route`).
  - `ws.CreateDatabase`: `dbs.Count` 1, system `TableDefs.Count` 5, and still 5 after `CREATE TABLE N1`. After Close and reopen the count is 6.
  - Same path again or an existing fixture gave 3204 with no file change. Version 12345 gave 3001 with no file created. Inside a transaction it gave 3246 with no file created.
  - `;LANGID=0xFFFF;CP=1` succeeded and created a 172032 B file.
  - After `eng.CreateDatabase` succeeded, the default `Databases.Count` was 0.
- **DAO-ID** (`id-twice`, `id-held-rename`, `recordset-state-inventory`).
  - Double retrieval returned the same harness identity token (`u#`) for Workspace, Database, TableDef, Field, Index, Relation, QueryDef, Parameter, Property and Document.
  - A second parent binding returned the same Parameters and Parameter, and the value 2 was shared.
  - A held TableDef survived rename and SQL `DROP TABLE` (`Name` "R2", Fields `Count` 2, `tdfs.Count` stale at 10).
  - After `tdfs.Delete` in a transaction, the held object gave 3420 and stayed 3420 after `Rollback`. After `Refresh`, `Item` returned a new identity.
  - A held Field followed the current row across MoveFirst, MoveNext, a failed FindFirst and Requery.
- **DAO-COLL** (`coll-*`).
  - Names are case-insensitive: "t", "CAFÉ" and "X Y" all matched.
  - Composed and decomposed é both matched `Café`.
  - "Cafe", "Caf", "T " (trailing space), " x y" and `x\0y` gave 3265. `T\0` found `T`. "" gave 3265 with source "DAO.TableDef".
  - Ordinals:
    - I2, UI1, R8 0 and 0.5, and BOOL 0 all gave index 0.
    - R8 1.5 and 2.5 and CY 1.5 gave index 2. DATE 1 gave index 1.
    - String "0", BOOL -1, I4 -1 and 10000, Null, Empty and Missing gave 3265.
    - A DISPATCH ordinal produced 2 `Errors` entries [3001, 3421], and EXCEPINFO was 3421.
  - Collections go stale until `Refresh`:
    - A second Database's TableDefs count went 9 to 10.
    - Indexes went 2 to 3 after `Relations.Append`.
    - Documents went 12 to 13 after `CreateQueryDef`.
    - `tdfs.Delete("M1")` before `Refresh` gave 3265.
- **DAO-DRAFT** (`draft-*`, `err-invalid-names`).
  - `TableDefs.Append` without fields gave 3264. `CreateField("FB",9999)` failed in the constructor with 3259. `CreateTableDef(Null)` gave 3421.
  - An unnamed type-0 Field and a second Field "A" both appended to a draft (`Count` 5). Re-appending the same Field object gave 3367.
  - Append with an empty name gave 3125, with `Count` unchanged at 9.
  - A reused Field in another TableDef gave 3367.
  - Table names `bad!name`, `a.b`, `[x]`, " Lead" and `ctl` all gave 3125. A 65-character name failed in `CreateTableDef` with 3001. "T" gave 3010. `CREATE TABLE [bad!name]` and `CreateQueryDef("Q!x")` also gave 3125.
  - Index append: 3264 (no fields), 3409 (missing field), 3283 (second primary), 3284 (duplicate name). `Indexes.Count` was unchanged after each.
  - Relation append: 3011 (missing table), 3366 (no fields).
  - `CreateQueryDef("")` gave "#Temporary QueryDef#" and `Count` stayed 2. Omitting the name gave "". A named query appended at once. A duplicate gave 3012. Bad SQL gave 3129 with `Count` unchanged. A missing table succeeded and appended. Renaming a temp query and `QueryDefs.Append` of it both gave 3219.
- **DAO-QUERY** (`query-*`).
  - Parameter `pId` starts `VT_EMPTY`. Set Empty then read gave `VT_I4 0`. Null reads `VT_NULL`. "abc" gave 3421 and the value stayed Null. "2" read back `VT_I4 2`.
  - `Execute` on a select gave 3065. `db.OpenRecordset("QParam")` with no value gave 3061.
  - `Parameters.Append` and `Parameters.Delete` fail in `GetIDsOfNames` with 0x80020006.
  - Setting identical SQL kept the Parameter (value 1). A renamed or retyped parameter, or a reordered declaration, gave 3420 on the held Parameter. Syntax-error SQL gave 3157 and the old SQL was kept.
- **DAO-PROP/META** (`prop-*`, `meta-containers-documents`).
  - `Properties` enumerate without reading `Value` (Database 12 entries, TableDef 12, Field at least 17). `Inherited` is False for every entry read.
  - Wrong-context calls: 3270 (unknown name), 3251 (`DefaultPassword`, `Version` as a method), 3219 (TableDef Field `.Value`).
  - Draft user property: `Value("")` and `Value(Null)` both gave 3385, and `Append` before a value gave 3386.
  - The user property, field property and changed database property all persisted on reopen.
  - Containers are exactly Databases, Relationships and Tables. Tables lists 11 Documents, including saved queries and MSys tables. `Owner` is "" and `UserName` is "admin".
- **DAO-SEC** (`sec-*`, `sec-logon-defaults`).
  - `Document.Permissions` and `AllPermissions` gave 3358 on both ACCDB and MDB, on first read. Both later returned `VT_I4 0` in the same session after the failed puts.
  - A fresh reopen failed again with 3358.
  - `CreateUser` succeeded. `Users` and `Groups` `Count` are 0 and file bytes are unchanged.
  - `CreateWorkspace` with a named user, or with Admin and "wrongpassword", gave 3028.
- **DAO-LIFE** (`life-*`).
  - After `db.Close()`, every held child gave 3420: TableDefs, TableDef, Field, Indexes, QueryDef, Parameters, Recordset, Property, Containers. A second `Close` and `Name` also gave 3420.
  - `Workspace.Close` gave 3420 for its Database and children, and a second `Close` gave 3420. The appended workspace left the collection (`Count` 1).
  - A Clone survived `rs.Close()`. A QueryDef-opened recordset survived `qd.Close()`, and `qd.SQL()` still reads.
  - A pending Edit was discarded on `db.Close()` (reopen reads "one").
- **Index slice** (`index-metadata-seek`). `PK_T` and `PK_C` are Primary, Unique and Required. `FK_C` is Foreign. `DistinctCount` was 5, 5, 2 and 3, unchanged by `Refresh`.
- **ERR-BASE** (`err-base-scope`).
  - `DBEngine.Errors` is a single collection. It held 1 entry (3024, 3078, …) and was replaced by each failure.
  - Successes left the old entry in place.
  - A second `CoCreate` returned the same `u1`, with the same Errors.
- **ERR-BIND** (`err-bind-*`).
  - Arity errors gave 3001 from the DAO source, and `IErrorInfo` was present (guid zero).
  - Unknown member name gave 0x80020006. `Version` called as a method and `Connect` put-ref gave 3251.
  - `OpenRecordset("T", Type, 0, 3)` with the Type cell Missing or Empty gave 3001. Null gave 3421.
  - Type "4" and R8 4.5 succeeded as snapshot (Type 4). BOOL -1 gave 3001. A byte-array source succeeded.
  - Swapped arguments gave 3421.
- **ERR-DAO-ORDER** (`err-dao-order`, `coll-tabledefs-index-variants`).
  - ODBC failure produced `Errors` [0] number 0 (driver-manager text) and [1] 3146. EXCEPINFO was 3146, the last entry. `Errors.Refresh` changed nothing.
  - The DISPATCH-ordinal case has the same last-entry pattern.
- **IErrorInfo presence.** Present with a zero guid in some DAO failures and absent in others. The records do not show the cause. ADO failures always carried a populated `IErrorInfo`.
- **ERR-SNAPSHOT** (`err-snapshot-held`). A held Error kept its original payload (3078 `NoSuchTable`) after a second failure, a success, `Errors.Refresh` and `db.Close`. `eng.Errors()` returned the same wrapper, and each `Item(0)` returned a new Error identity showing the latest error.
- **ERR-ADO** (`ado-*`).
  - Runtime errors (3704, `Fields.Item`) leave `Errors.Count` at 0.
  - Provider errors give `Count` 1:
    - 0x80040E14: SQLState "3000", NativeError -533138860.
    - 0x80040E37: SQLState "3078", NativeError -524420377.
  - A successful `Execute` after a failure showed `Count` 0.
  - A requested Dynamic/Optimistic recordset came back as `CursorType` 0 and `LockType` 1 with `Errors.Count` 0 (no warning surfaced). `MoveLast` on it gave provider error 0x80040E24, and `Errors` showed 1 entry after two later MoveNext calls.
  - `Connection.Close` twice gave 3704 on the second.
- **ERR-PARTIAL** (`err-partial-*`).
  - With `dbFailOnError`, an `INSERT … SELECT` with a duplicate in the first, middle or last row gave 3022 and added no rows (`RecordsAffected` 0). Bytes were unchanged for first and middle, and changed for last (same size, different hash).
  - Without it, 2 rows were inserted and no error was raised. `UPDATE T SET ID=ID+1` gave 3200 with no row change.
  - A second workspace's `Edit` or `Execute` on a locked row gave 3188, and `Field.Value` set without `Edit` gave 3020.

**Probe or fixture defects**
- `index-metadata-seek`: `rs.Index("PrimaryKey")` gave 3800 because the fixture's primary key is named `PK_T`. All later `Seek` calls then ran with no current index (3019 or 3001). The Seek arity and error-order evidence is void.
- `prop-user-lifecycle`: the held-after-Delete reads use `pd`, which was never appended (`Value("")` gave 3385, `Append` 3386). The appended and deleted object was `pdup`, so held-Property behaviour across Delete and same-name recreation is untested.
- `recordset-state-inventory`: `Sort "V DESC"` does not discriminate. The V values (three, four, five) in descending order give the same row order as ID ascending (3, 4, 5). `GetRows` is "not yet captured element-wise", so the capture is incomplete. A dynaset on a read-only open shows `Updatable` False.
- `ado-connection-errors`: `Errors.Clear` ran with `Count` already 0, so its clear effect is untested.
- `ado-recordset-warning-runtime`: no provider warning was induced. This is an unobserved case.
- `manifest-library` and `manifest-runtime-types`: `show` renders blank steps for the type data, which exists only in the JSON.
- `err-base-scope`: the "independent DBEngine" case is not independent (`eng2` is `u1`).

**Revisit**
- The ACEDAO build is 20196, not 20016. Decide whether the book's target needs updating, or whether a 20016 run is needed.
- Only the .NET 11 late-bound lane is covered. Typed native, explicit IDispatch, Access/VBA, CalcVBA, .NET Framework 4.8 and .NET 10 are not covered.
- Not covered:
  - IEnumVARIANT and DAO-ENUM.
  - Requery arguments and nested saved queries.
  - ERR-THREAD, ERR-SLOT-REENTRY, ERR-EXCEP (all `deferredFill=False`), ERR-NEST, ERR-MANAGED and ERR-OOM.
  - Held-Field cases for overflow and schema recreation.
  - Concurrent or reentrant Close, and failing create-publication boundaries.
  - A second Database-level or external-process mutation.
- Unexplained, needing smaller probes:
  - `Document.Permissions` failing 3358 on first read, then returning 0.
  - `IErrorInfo` present or absent per call.
  - `eng.CreateDatabase` not appearing in `Databases.Count`.
  - File bytes changed or grew after read-write opens with no successful write (`draft-index-relation`, `err-invalid-names`, and `draft-tabledef-field`, which grew 258048 to 266240 B).
  - The last-row duplicate rollback changing bytes.

### r-lock-1-2

136 records, all from one environment: ACEDAO.DLL 16.0.20527.20196, .NET 11 x64, Windows 11, local path, run on 2026-11-16. They hold 55 ReturnedFailure and 81 ReturnedSuccess; a ReturnedFailure is an ACE refusal on a participant, not a failed test. `show --record` renders only participant A. Participant B and C results below come from `show --attempt` on the same attempt ids. Fixture: ACCDB 8,663,040 B and MDB 8,597,504 B, table `T` with 2,048 rows and 2,000-char memos (`create-accdb`, `create-mdb`). Every successful open read back VT_I4 2048 and 2000.

**Observed: EX-OPEN, EX-DEFAULT, EX-PUBLIC, RO-PAIR, RO-ACTUAL across processes (`{fmt}-dao-X-then-dao-Y`, A holds X, B opens Y).** The open/refuse pattern is identical in ACCDB and MDB; only the error number differs.
- A = excl-rw: B refuses all four modes with 3045 (HRESULT 0x80020009, scode 0x800A0BE5, "Could not use '<path>'; file already in use.").
- A = excl-ro: B excl-ro and shared-ro open and read. B excl-rw and shared-rw refuse with 3045.
- A = shared-ro: B opens and reads in excl-ro, shared-ro and shared-rw. B excl-rw refuses, with 3704 in ACCDB (scode 0x800A0E78, "already opened by user 'admin' on machine …") and 3045 in MDB.
- A = shared-rw: B shared-ro and shared-rw open and read. B excl-ro and excl-rw refuse, with 3704 in ACCDB and 3045 in MDB.
- `…-dao-shared-rw-then-dao-shared-rw` opens in both processes. That is the only default-like case run, and it passes explicitly (exclusive=False, readOnly=False), not with omitted arguments.
- A reads only after B's attempt, so there is no read-before-B baseline. A's reads succeed in all 32 DAO pairs.

**Observed: native leases (W-PROBE) against DAO, both orders (`…-native-r-shareread-…`, `…-native-rw-share0-…`).**
- Native A `r-shareread` (access 0x80000000, share 1, fileId captured):
  - DAO B excl-ro and shared-ro open and read.
  - DAO B excl-rw and shared-rw refuse with 3045 (MDB and ACCDB).
- Native A `rw-share0` (0xC0000000, share 0): all four DAO modes refuse with 3045, including shared-ro and excl-ro.
- DAO A excl-ro or shared-ro: native `r-shareread` opens, and 4096 bytes read. Native `rw-share0` fails with Win32 32.
- DAO A excl-rw or shared-rw: native `r-shareread` and `rw-share0` both fail with Win32 32.
- Native `r-shareread` coexists with DAO shared-ro and with DAO excl-ro in these probes.

**Observed: RO-LATE (`accdb-ro-late`, `mdb-ro-late`).**
- A and B each hold a DAO shared-ro `Database`.
- Writer C attempts at three points: A and B open; A closed with B still open; both closed.
- C DAO shared-rw `OpenDatabase` succeeded at all three points. `UPDATE … WHERE ID = 1` returned RecordsAffected VT_I4 1 each time, including while both readers were open.
- C DAO exclusive open was refused at points 1 and 2 (3704 ACCDB, 3045 MDB). It succeeded at point 3.
- C native `rw-share0` failed with Win32 32 at points 1 and 2 (23:00:29.729 and .763 in ACCDB) and succeeded at point 3.
- In these probes the DAO shared-ro readers did not block a DAO shared-rw writer at any point. Only exclusive and native share-zero writers were denied.

**Observed: RO-INPROC and EX-PUBLIC in one process (`{fmt}-inproc-ws1-…` MDB and ACCDB, `accdb-inproc-ws2-…` ACCDB only, with a second Workspace).** Results are the same for ws1 and ws2 in ACCDB.
- Binding 1 excl-rw: binding 2 opens in all four modes, including a second exclusive-rw.
- Binding 1 shared-rw: binding 2 opens in all four modes, including excl-rw.
- Binding 1 excl-ro: binding 2 refuses excl-rw and shared-rw with 3045. It opens excl-ro and shared-ro.
- Binding 1 shared-ro: binding 2 refuses excl-rw, with 3704 in ACCDB and 3045 in MDB. It opens the other three modes.
- When binding 2 opened, all four reads succeeded, including after binding 1 was closed.
- In-process admission differs from cross-process admission for the same pairs. For example, excl-rw then excl-rw succeeds in-process and refuses (3045) across processes.

**Observed: EX-ALIAS (`{fmt}-alias-{inproc,xproc}-{upper,dot,slashes,extended}`).**
- Across processes, A holds the plain path exclusive. B shared-rw through the upper-case, `.\` and slash spellings refuses with 3045. The error text quotes the normalised path.
- A `\\?\`-prefixed spelling returns 3043 ("Your network access was interrupted…", scode 0x800A0BE3) in both `xproc` and `inproc`, both formats.
- In-process, the upper, dot and slash spellings open shared after the exclusive open, and again in the reversed order. `d3.Name()` for the upper spelling returns the spelled directory case with the on-disk file-name case.

**Observed: EX-LIFE (`{fmt}-life-close`, `{fmt}-life-release`).**
- While A holds excl-rw with a table recordset, B's `OpenDatabase` fails with 3045 and its native `r-shareread` fails with Win32 32.
- After A's `Recordset.Close` and `Database.Close`, B's DAO shared-rw open succeeds and reads.
- After A only released both objects (no Close), B's DAO shared-rw open succeeds just as quickly (B waited 37–44 ms for `a-released`).
- The harness's own file read of the fixture failed with IOException 0x80070020 while A held it exclusively (`files[a-open]`), and succeeded at `a-released` in both variants. The fixture hash was unchanged from before the open to after the close (`a867cc324683`).

**Observed: lock-file artifacts.**
- Shared opens leave a lock-file artifact: 64 B after one opener, 128 B after two shared openers (`.laccdb` / `.ldb`).
- No lock-file artifact was captured in the A-open observation when A was excl-rw or excl-ro.
- `excl-ro-then-dao-shared-ro` captured 64 B after B opened.
- Raw bytes were not interpreted.

**Probe or fixture defects**
- `Win32 error -1` on `native read`/`native close` after a failed native open is the harness sentinel for "no handle". It is not an ACE or Win32 answer. It appears in every refused native-open record.
- `*-life-close` and `*-life-release`: B's second native `r-shareread` open (`h2`) is made after B itself opened a DAO shared-rw `db2`, so its Win32 32 reflects B's own handle. It cannot show A's release state. In both life records the DAO result after release is the only usable one.
- `…-alias-{inproc,xproc}-extended`: ACE refuses the `\\?\` spelling with 3043, independent of any holder. There is no unheld control for that spelling, so it tells nothing about alias identity.
- `…-alias-inproc-{upper,dot,slashes}` pass the same way as the plain-path in-process baseline (`inproc-ws1-excl-rw-then-shared-rw` also opens). They do not discriminate alias recognition in-process. Only `xproc` does.
- The `*-ro-late` description says readers "keep a recordset", but A and B hold only a `Database` (the `Use` helper closes its recordset at once). No reader reads after C's attempts, so reader integrity during the overlap is unobserved.
- The pair descriptions say A reads before and after B. The program reads only after B's attempt.
- Despite the card's "Probe uses the exact proposed … pair", the native leases are only the two candidates. Delete/rename share bits are never varied.

**Revisit**
- EX-PROBE is not covered. `dao-{mode}-then-metadata-probe` is defined in `RLock12.cs` for both formats, but no record exists for it (8 probes).
- Not covered at all: three-reader RO-PAIR; RO-LATE with the writer already open before A or with a third reader; Access UI, ADO and other caller lanes; EX-CREATE, EX-RACE, EX-AUTH, EX-PERM, EX-MAP, EX-RANGE, RO-PERM, RO-REOPEN, RO-FAIL, RO-NATIVE; network paths. The probe header lists these as deferred.
- Actual ACE native access/share flags are inferred from refusals only (no external handle observation). Whether DAO shared-ro holds database write access is therefore not directly observed.
- Whether a DAO shared-ro reader is ever protected against a writer in a reader-only form is open: RO-LATE shows DAO shared-rw writers admitted beside shared-ro readers. No native-lease-based RO-LATE exists, so writer exclusion against a held `r-shareread` is only seen in the pair probes (3045 for DAO shared-rw).
- Why ACCDB gives 3704 and MDB 3045 for the same refusal is unexamined. Which participant holds the file (shared versus exclusive A) correlates with it in ACCDB.
- Whether the lock-file artifact is absent under exclusive opens, or only not captured, is unclear from these records (an observation-timing question).
- Release timing after process death and pending I/O is not tested.
