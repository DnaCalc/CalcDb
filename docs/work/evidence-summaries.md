# Evidence summaries

Digests of the retained records, card by card. They were written by sub-agents (Sonnet) that read every
record of their card with `calcdb-oracle show --record`, and were spot-checked against the records (the
GUID read-back form, bookmark bytes and the system-table permission errors matched). **The records under
`evidence/` are the authority**; a summary that disagrees with a record is wrong. Query notation in a summary can be shorthand; the exact SQL of every call is in its record. Each section lists what ACE
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

### r-sort-3

All reads below come from `evidence/r-sort-3/**` (38 records, 19 probe names × ACCDB/MDB where paired). Loaded engine in every record: `ACEDAO.DLL 16.0.20527.20196`; `eng.Version()` = "16.0". Unless stated, the ACCDB and MDB records of a probe pair gave identical ID sequences, seek results, equality classes and failure lists. Corpus T has 203 rows, with ID 1 = Null.

**Observed**

*Descriptor dispatch*
- `create-version-256` (accepted, attempt `a2`): `CreateDatabase(…, ";LANGID=0x0409;CP=1252;COUNTRY=0", 256)` returned `db.Version()` "14.0", `CollatingOrder` VT_I4 1033. A dbVersion 128 file gives "12.0", a 64 file "4.0".
- `create-version-32`: the same call with 32 returned 0x800A0C62, "Could not find installable ISAM".
- `create-locale-spellings`:
  - `""` gave 0x800A0BB9 "Invalid argument".
  - `"LANGID=0x0409;CP=1252;COUNTRY=0"` (no leading `;`) gave 0x800A0C62.
  - `";LANGID=0x0000;CP=1252;COUNTRY=0"` gave 0x800A0C9C "Selected collating sequence not supported by the operating system".
  - `";LANGID=0x0409"` and `";LANGID=0x0409;CP=1252"` gave CollatingOrder 1033. `;LANGID=0x0809;CP=1252;COUNTRY=0` gave 2057.
  - All three accepted files returned the same SQL order of the six values: IDs 2,1,4,3,6,5.
- `compact-*` (`CompactDatabase`, `Errors.Count` VT_I2 0 in all):
  - German `;LANGID=0x0407` gave CollatingOrder 1031.
  - Czech `0x0405` gave 1029.
  - Japanese `0x0411` gave 1041.
  - Swedish `0x041D` gave 1053.
  - `accdb-to-accdb-general`, `accdb-to-mdb-general` (db.Version "4.0") and `mdb-to-accdb-general` kept 1033.
- The IX_K walk, the IX_KD walk and SQL ORDER BY K on the compacted copies were identical to the source for general, accdb-to-mdb, mdb-to-accdb and german. They differed for czech, swedish and japanese:
  - Czech: U+0308 sorts before U+0301.
  - Japanese: Han order differs. U+6F22 and U+56FD come before U+4E09, U+4E2D and U+4E01.
  - Swedish: ø (U+00D8) sorts apart from o.
- All 40 `Seek "="` calls on each compacted copy returned NoMatch False.
- `create-version-256` (dbVersion 256) versus the dbVersion 128 corpus:
  - U+1D11E, U+1D400, U+1F600, U+20000 and U+20001 sort between the ligatures and the Han values, with U+20000 and U+20001 after Han.
  - In the 128 files, all supplementary-plane values equal `""` (see Text levels).
  - Sample of 40 values only; SQL order ≠ IX_K walk (tie order not examined).

*Text levels, equality, order*
- `equality-accdb`, `equality-mdb`: SQL `=` partitions T into 125 classes (`COUNT(DISTINCT K)` VT_I4 125). Selected classes:
  - `""` ≡ " " ≡ "  " ≡ U+1E9E, U+3400, U+9FFF, U+20000, U+20001, U+1D11E, U+1F600, U+1D400, U+20000×2, U+FFFD, U+FFFE, U+FFFF, U+D7FF, and the ID 178 value.
  - A ≡ a ≡ U+FF21 ≡ U+FF41 ≡ "a " ≡ "a  ", and also "a"+U+20000 and U+20000+"a".
  - é ≡ É ≡ e+U+0301 ≡ E+U+0301.
  - U+FB01 ≡ "fi", U+FB00 ≡ "ff", U+FB02 ≡ "fl", U+FB03 ≡ "ffi", U+0153 ≡ U+0152 ≡ "oe".
  - Kana, half-width and hiragana: U+3042 ≡ U+30A2 ≡ U+FF71.
  - ß is unequal to "ss"; "ss" ≡ "SS".
  - U+0001 is not equal to `""`.
  - `W` (unique K, one INSERT per row) kept 125 rows. The first ID of each class survives.
- `order-index-*` (T, IX_K, built after the rows):
  - Walk is NULL first, then class order, with ties in ascending ID.
  - IX_K backward is the exact reverse.
  - IX_KD forward is class order reversed, NULL last, ties still ascending ID.
  - Incrementally built `T2` gives walks identical to T.
  - `order-sql`: `ORDER BY K` gives the same class order as IX_K, but tie order is not ID order, and `ORDER BY K DESC` is not the reverse of ASC at tie level. `ORDER BY K, ID` equals the IX_K walk. `T2` equals `T` in SQL order too.
- `seek-*` on T IX_K (402 seeks):
  - NoMatch was False for all of them, including key Null → ID 1.
  - Where I could align a key with a stored value (198 `=` and 198 `>=`), `=` returned the lowest ID of the equality class in all 198, and `>=` did the same in 197.
- `seek-*` on `DG` (1,536 rows, three repeated 100-char keys; IDs a≡1, b≡2, c≡0 mod 3):
  - `=` b×100 gave ID 2 (first of group); `>` gave 3; `<=` gave 2; `<` gave 1534.
  - Absent key between a and b: `>=` and `>` gave 2; `<=` and `<` gave 1534.
  - `=` on an absent key set NoMatch True.
- `ado-order-seek-accdb` (`ADODB.Connection`, Mode 1, `cn.Version` "10.0"):
  - `ORDER BY K`, `K DESC`, `K, ID` and the equal-K self-join returned sequences identical to the DAO SQL ones.
  - Seek EOF was False for all 72 seeks.
  - adSeekFirstEQ gave the lowest class ID (e.g. "A" → 3); adSeekLastEQ the highest ("A" → 197, `""` → 203); adSeekAfterEQ equalled FirstEQ.
- `like-prefix-accdb`: `K LIKE 'a'` gave IDs 3,4, while `K = 'a'` gave 8 IDs (3,4,163,164,170,171,196,197).
  - `LIKE U+FB01` gave 94 only, while `= U+FB01` gave 94 and 95.
  - `LIKE U+20000*` and `LIKE '*'` each returned 202 rows.
  - `LIKE 'A*'`, `LIKE 'a*'` and `LIKE U+FF21*` each returned 42 rows.

*Positions and controls, UTF-16 boundaries*
- Table U (37 values, ID 1–37):
  - Embedded U+0000 values were stored as given (e.g. ID 1 `"a\x00b"`).
  - Lone-surrogate BSTRs display as bare `VT_BSTR` in `show`, so I could not confirm the stored units for those rows.
  - In U, "a\0b" ≡ "a\0\0b" ≡ "a\0b\0" ≡ "A\0b" ≡ "a\0B" ≡ "A"+DC00+"B", and "a\0", "\0a", D800+"a", "a"+D800, "a"+DC00, D840+"a"+DC00 are one class.
  - "\0", "\0\0", "\0"+U+20000 and every lone-surrogate-only value are one class, which sorts first after Null.
  - `W2` kept IDs 1,2,4,10,11,12,31,32,33,36,37.
- Boundary values:
  - K of 255 UTF-16 units was accepted: A×253+U+20000 (ID 35) and A×254+U+D840 (ID 34).
  - K of 256 units was refused with 0x800A0C5B (3163, "The field is too small…") for A×254+U+20000 (ID 36) and A×256 (ID 37). Both rows stayed ID-only with Null K.
  - A×253 ≡ A×253+U+20000, and A×254 ≡ A×254+U+D840. A×253, A×254 and A×255 are mutually distinct.
  - `seek-*` on U: all `=` seeks matched except A×256, which gave NoMatch True. The 3021 on reading ID, 0x800A0BCD "No current record", comes after that. `>=` A×256 returned ID 1.

*Compound framing, Length510, Shortened-key collisions*
- `keys-uniqueness-meta-*`:
  - `LU` (unique K,N) and `LU2` (unique K,N DESC) kept 32 rows each. Refused were IDs 27 and 28 (e+U+0301×127 ≡ é×127) and 31 and 32 (a×255 ≡ A×255), plus the 4 repeated IDs.
  - `LU3` (unique K) kept 16 rows (the N=1 row of each distinct K).
  - `DistinctCount` of every LK compound index is 35.
- `keys-order-index-*`, `keys-order-sql-*`, `keys-seek-*` (LK, 39 rows):
  - For K = é×255 (IDs 19,20), ß×255 (25,26) and Han×255 (33,34), IX_KN lists the N=2 row first; IX_KND lists IDs ascending (19,20). SQL ORDER BY (K,N) shows the same pairs.
  - `=` with (K,1) and (K,2) returned the right rows for all 18 keys.
  - `>=` K-only returned 29 for é×255 (not 19 or 20), 26 for ß×255 and 34 for Han×255 (the first row in IX_KN order).
  - IX_KN (K,N) and the (K,D), (K,DT) and (K,GU) indexes order the long-K rows differently: `IX_KM` and `IX_KG` list 19,20, while `IX_KN`, `IX_KD` and `IX_KT` list 20,19. The cause is not established.
  - `IX_KN` on fresh `LK` equals `LK2` (incremental) in walk order.
- FR (60 rows): "\0" rows (37–42) are returned by seeks for `""` (7–12) and interleave with `""` rows in the IX_FKN walk. Seek `(K)` alone returned the N=Null row of the group.

*Maintained indexes*
- `maintain-*` (delete, reverse reinsert, update away and back): IX_K and IX_KD keep the same class order as the fresh fixture, but within-class ties are descending ID (78 of 78 tie steps; fixture is ascending). Reinsert and update-back sequences are identical. Intermediate whole-file images were read (sizes 688128 → 700416 → 708608 B, hashes recorded).

*Null policy / aliases* (`keys-uniqueness-meta-*`; INSERT…SELECT without `dbFailOnError`, so refusals are silent)
- NP1 and NP2, unique (K,N) with or without IGNORE NULL: 10 of 12 rows survive. Only the repeated ('a',1) and ('',1) are refused; the Null-bearing tuples are all kept.
- NP3 (PK K,N) and NP4 (DISALLOW NULL): 2 rows (('a',1), ('',1)).
- NP5 (K NOT NULL): 6 rows.
- NP6 and NP7 (unique K): 6 rows. All four Null-K rows are kept.
- NP9 (DAO unique K, Required, IgnoreNulls): `Append` hr 0, 2 rows survive. Index reports U R IgnNull.
- NS reports IX_A and IX_B as two separate logical indexes (same field K); IX_C has Attributes 1; NSC reports FK_NSC with Foreign true.

*Typed keys* (`create-nums-*`, forward IX_ walks, NULLs first)
- Byte: 256 as VT_I4 and -1 as VT_I2 → 0x800A0D5D, "Data type conversion error".
- Single and Double: NaN and ±Infinity are accepted and read back as NaN or Infinity.
- Double walk: negative-sign NaN (ID 39), then −Inf (42), −max (31)…+max (37), +Inf (41), then positive-sign NaNs (38, 40).
- Single walk: NaN (29) first, +Inf (30) last.
- DATE: −0 (ID 57) sorts before +0 (ID 56); `show` prints the values as "-0" and "0".
- Currency: R8, string and Cy inputs of 1.2345 all read VT_CY 1.2345.
- GUID order follows the textual groups.
- MDB:
  - `ALTER TABLE … DATETIME2` gave 0x800A0BB9.
  - `CREATE INDEX IX_DX` gave 0x800A0D51.
  - `w.Index "IX_DX"` gave 0x800A0ED8.
  - ACCDB `DX` reads as a string with the .0000000 fraction.
- All typed walks agree between ACCDB and MDB.

*SORT-DERIVE start* (`create-derive-*`): U+0000–U+00FF in six contexts (1,536 rows).
- The anomaly query returned EOF at once: no Null K and no LEN mismatch.
- SQL `ORDER BY K` and the IX_K walk each hold 1,536 distinct IDs. They differ, but tie versus class order was not examined.

**Probe or fixture defects**
- **Compression table:** `CREATE TABLE C … TEXT(255) WITH COMPRESSION` returned 0x800A0CDA ("Syntax error in CREATE TABLE statement") in both formats (`create-corpus-accdb`, `create-corpus-mdb`). Compression is therefore not covered. The follow-on opens of `C` (0x800A0BC3, 0x800A0C06) make `order-sql-*` and `order-index-*` end `ReturnedFailure`.
- **Silent refusals:** the INSERT…SELECT loads into `W`, `W2`, `LU*` and `NP*` show hr 0 for every call, so refusal causes and error numbers are not recorded. Only surviving rows show refusals.
- **Intermediate images:** the intermediate `files[…]` captures in all `create-*` probes are size-only (`unread: IOException 0x80070020`, file locked); only the after-close image carries a hash. The fresh-versus-incremental build stages are therefore not retained as images.
- **FR index name collision:** `CREATE INDEX IX_FKND` on `FR` returned 0x800A0D2F because `IX_FKNd` already exists (names are case-insensitive). Walking "IX_FKND" in `keys-order-index-*` therefore walked `IX_FKNd`: identical sequence. `FR (K,N,D)` is untested.
- **NP10:** `dtd.Item("NP10")` returned 0x800A0CC1, so the DAO Required / non-IgnoreNulls index was never created. The 12 NP10 inserts ran on a table with no index. The card's refused-property creation case is not captured.
- **Decimal X:** `CreateField("X", 20)` has the default scale, so six values failed with 0x800A0EAF ("Scaling of decimal value resulted in data truncation"). Reading the four stored values failed with 0x800A0BB8 (−1038). Decimal keys are not observed.
- **`seek-*` status:** `seek-*` is `ReturnedFailure`. The eight 3021 results follow NoMatch True and are ACE answers, not a defect.
- **`show` display:** I cannot tell lone-surrogate or NUL content from the display. Rows with U+2028 or CR/LF break my line-based parse; I did not rely on them.

**Revisit**
- **Engine build:** the card cites ACE16.0.20527.20016, but the records load 16.0.20527.20196.
- **Key bytes:** no record contains key bytes or page extraction. Sort-descriptor identity, General97 or General–Legacy, and shortened-key bytes (the 510 cutoff and its digest) are unobserved. No A×253/254/255 General + Long1/Long2 pair was run. No 509/510/511/513-byte encoded-length series was run, except the K sets above.
- **Locales and builds:** only 0x0409, 0x0809 and the compaction locales listed above. No other Windows locales or builds.
- **Untested branches:** mixed-direction suffix controls beyond IX_KND, FR `K,N,D` / `D,K`, DAO Null-option refusals, NUMERIC, extended-date and the Text compression row-format interaction.
- **Open anomalies:**
  - The N-tail order inversion for the long-K rows (é, ß, Han ×255) in IX_KN, IX_KND and SQL.
  - The `>=` K-only seek that skipped the é×255 group.
  - Why supplementary-plane values equal `""` in dbVersion 128 files but not in dbVersion 256.
- **SORT-DERIVE:** only U+0000–U+00FF is captured, not all 65,536 units, and no weights were derived from index bytes.

### r-lock-3-4-txn

All records are two harness processes `a` and `b` on one local ACCDB (MDB where named), via DAO from .NET 11 x64. The loaded `ACEDAO.DLL` is 16.0.20527.**20196** (the card names 20016; `ACEERR.DLL` is 20016). `fixtures --failed-only` lists no failed creation call for this card. Fixture `T` has 64 rows (ID, N=10·ID, V=0, S) with `PK_T` and a non-unique `IX_N`. Row 64 is on a different page from rows 1 and 2. All fixture rows are written in one step per statement.

**Observed**

*READ-ROLE*
- Readers opened before or during `a`'s pessimistic `Edit` on row 1 (`ra.Edit`, `EditMode` 1) read row 1 as (N=10, V=0). This held for snapshot, dynaset, forward-only and table cursors, both held and newly opened (`read-role-edit-existing-readers`, `read-role-edit-then-update-new-reader`, `write-timing-pessimistic-matrix`).
- `Transactions()` reads: snapshot 0, dynaset -1, forward-only 0, table -1 (`read-role-edit-existing-readers`, `txn-view-readers`).
- After `a`'s published `Update` (N=5555), `b`'s held cursors and new cursors still read N=10, including after `Move 0` and `Requery`. The file hash was still the initial `d13aa3a9aa41` at the capture after `Update`, with `.laccdb` 64→128 B (`read-role-edit-existing-readers`).
- A `Requery` on the table-type recordset returned 0x800A0CB3 (3251) in every probe that tried it.

*READ-REFRESH, READ-PATH, READ-SNAPSHOT*
- Reader-side reads taken 20–1000 ms after `b`'s committed or implicit updates returned pre-change values from every cursor kind, including after `Move 0`, `Requery`, `MoveFirst` and a new snapshot (`read-snapshot-cursor-kinds`, `read-path-lv-replace`, `read-refresh-ladder`).
- `read-refresh-elapsed`: after `b` set N=2001, `a`'s immediate read gave N=10 for the dynaset, snapshot and table cursors. After a 6.5 s wait with no move, the dynaset and table cursors read 2001 and the snapshot read 10.
- `read-refresh-ladder`: `b` wrote N=1001 to 1005 in turn. After `Idle(dbRefreshCache)` the dynaset and table cursors read **1001** (not 1002); the snapshot read 10. Later steps (`Move 0`, `Requery`, `MoveFirst`) never returned more than 1001. After `Requery` the snapshot read 1001. A fresh dynaset read 1001.
- `read-path-index-moved`: `b` changed N of rows 3, 62 and 64. `a`'s table walk on `IX_N` returned IDs 2 to 64 in the old order, with row 3 at the old N=30 and rows 62 and 64 with the new N (5 and 15) at their old positions. `Seek =15` and `Seek =100000` gave NoMatch True. Errors collection 3021 appears, after field reads that raised 0x800A0BCD.
- `read-path-lv-replace`: after `b` replaced three memos and deleted and reinserted row 3, `a`'s snapshot, dynaset, `MoveFirst` and `Requery` all returned the original memo texts. No error.
- `read-snapshot-cursor-kinds`: forward-only `MoveFirst` returned 0x800A0C93 (Errors 3219). The ID 0 and 65 inserts were not seen by any cursor.

*READ-ID*
- `read-id-delete-reinsert`: after `b` deleted row 5 and reinserted the same key and values, `rd` and `cl` returned (5,50,0) with an unchanged bookmark `04020000` (`VT_ARRAY|VT_UI1 [0+4]`).
- After a second delete and reinsert with N=51, V=9, both cursors still returned (5,50,0), also after `Move 0`.
- `Requery` moved the dynaset to row 1, bookmark `00020000`. The reinserted row was never read.

*READ-SCHEMA* (`read-schema-held-descriptors`, `read-schema-index-held-cursor`)
- While `a` held cursors, `b`'s `ALTER TABLE U ADD COLUMN`, `CREATE INDEX` and `DROP INDEX` / `CREATE INDEX` on `T` failed with 0x800A0CBE (3262, "Could not lock table … currently in use by user 'admin'"). After `a` closed its cursors, they succeeded.
- `a`'s held `TableDef` still reported U with 3 fields (ID I4/4, N 4, W 10), 1 index and 12 properties. This held after U was dropped and recreated by `b`, and after `TableDefs.Refresh`.
- `a`'s `OpenRecordset("SELECT * FROM U")` returned 0x800A0BC3 (3011). A new `QueryDefs` binding returned `QT`'s old SQL (`ID <= 10`) although `b` had set `ID <= 3`.
- A table cursor opened after `b` dropped `IX_N` and created `IX_V2`: `Index="IX_N"` and `"IX_V"` returned 0x800A0ED8 (3800); `"IX_V2"` was accepted.

*WRITE-TIMING*
- `write-timing-pessimistic-matrix` (`a` Edit on row 1, `b` default DAO):
  - Blocked with 3260 (0x800A0CBC, "currently locked by user 'admin'"): UPDATE of rows 1 and 2, `rb2.Edit`.
  - Admitted: UPDATE of row 64 and of `U`, INSERT row 200, `rb64.Edit` with `Update`.
  - A blocked statement returned after about 2.2 s in the `txn-local-xproc-*` probes.
- `write-timing-optimistic-seechanges` (options 512, `LockEdits` 3, pending Edit on row 1): every `b` statement was admitted, including `Edit` and `Update` of row 2.
- `write-timing-release-boundaries`: `b`'s UPDATE of the held row was blocked (3260) only while `Edit` was pending. It was admitted after `CancelUpdate`, after `Edit`/assign/`Update`, after `Edit` then `Recordset.Close`, after a recordset released without `Close`, and after `Database.Close` with the recordset open.
- `write-timing-ado-lock-mode-page` / `-row` (ADO `Jet OLEDB:Database Locking Mode`, read back 0 and 1): `b` (DAO) was blocked on rows 1 and 2 and admitted on row 64 and the INSERT in both. The error was 3260 (0x800A0CBC, with user name) for mode 0 and 3218 (0x800A0C92, "Could not update; currently locked.") for mode 1.
- `write-deny-options`: with `dbDenyWrite`, `b`'s snapshot open and read worked; UPDATE and table-open returned 3262. With `dbDenyRead`, all three returned 0x800A0CBD (3261, "Table 'T' is exclusively locked"). After `a` closed, all three worked.

*WRITE-ORIGINAL*
- Optimistic `a` with a pending Edit (N=100), then `b` changed the same row by statement (N=222), on both ACCDB and MDB (`write-original-same-row[-mdb]`).
  - The first `Update` returned 0x800A0C7D (3197) and `EditMode` stayed 1.
  - A second `Update` returned success, `EditMode` 0, and `a` read N=100.
  - `CancelUpdate` then returned 3020.
- `b` changing only V (`write-original-unconflicting`, phase 1): same sequence, first `Update` 3197. `b`'s final read, taken after phase 2, showed (1,100,0), so V=77 was gone.
- `b` changing N and restoring it (`write-original-changed-then-restored`): the first `Update` succeeded.
- `write-original-unconflicting`, phase 2: after `b`'s UPDATE of row 2 returned, `a`'s `Update` on row 1 returned 3260 twice, `EditMode` stayed 1, and `Move 0` discarded the edit.

*WRITE-ALLOC, WRITE-TREE*
- While `a` held an uncommitted INSERT in a transaction, `b`'s INSERTs of keys 202, 203 (low N), 212 and 213 (`write-alloc-insert-txn`), and 300, 301, 310 and 311 (`write-tree-unique-candidate`) all returned 3260. Key 300, equal to `a`'s uncommitted key, also gave 3260, not 3022.
- After `a`'s rollback, `b`'s INSERTs of 201 and 300 succeeded. After `a`'s commit, `b`'s INSERT of 310 returned 0x800A0BCE (3022).
- `write-alloc-insert-txn`, after `a`'s commit: `b`'s `COUNT/MAX` read (65, 201) while `b`'s `ID > 200` read returned 201 and 211 (`a`'s own read: 66, 211).
- `write-alloc-counter-rollback`: `AddNew` ID read 3 for `a` (in transaction) and 4 for `b`. `b`'s `Update` returned 3260. After `a`'s rollback `b` read 5, then `a` read 6. Final rows: 1, 2, 5, 6.

*WRITE-LV* (`write-lv-replace-reuse`)
- `b`'s first two memo replaces on row 4, while `a`'s dynaset sat on row 4, returned 0x800A0C72 (3186). File unchanged at 290816 B.
- After `a` closed, replaces and appends succeeded. File sizes (B): 290816, 290816 (replace), 311296, 331776 (after `SetOption(65,1)` and replace), 352256, 372736 (after close).

*WRITE-DDL* (`write-ddl-vs-pending-edit`)
- With `a`'s pessimistic `Edit` pending, and again after `CancelUpdate` with the recordset open, `b`'s ADD COLUMN, CREATE INDEX and DROP TABLE on U returned 3262. After `a` closed the recordset all three succeeded (U dropped).

*WRITE-PROMOTE* (`write-promote-bulk-update`)
- `a`'s transaction updated 64 rows (RecordsAffected 64). `b`'s row-64 UPDATE returned 3260. `b`'s UPDATE of U row 1 and INSERT of row 400 were admitted.
- With `SetOption(62, 8)` in `a`, `a`'s bulk UPDATE returned 0x800A0BDB (3035, "System resource exceeded"), RecordsAffected 0. `Rollback` then worked, and `b` was admitted on row 64.

*TXN-LOCAL*
- Two transactions on the same row, same page (rows 1 and 2) or via an implicit `b`: `b`'s UPDATE returned 3260 in all 2, 6 and 6 phases, on ACCDB and MDB (`txn-local-xproc-same-row`, `-same-page`, `-same-page-mdb`).
- Rows 1 and 64, and tables T and U: both owners updated and committed or rolled back (`-far-row`, `-other-table`).
- `-conflict-settings`: three `SetOption` conditions (`dbLockRetry` 1 with `dbLockDelay` 1; 5 with 100; then `dbPageTimeout` 100) all ended in 3260. The three blocked calls took about 1.1 s in total by timestamps.

*TXN-NEST, TXN-MULTI, TXN-STMT, TXN-CLOSE, TXN-FLUSH-LIVE*
- Three nested `BeginTrans` updating rows 1 to 3, then `CommitTrans` (inner) and `Rollback` (middle): `a` read (1,1010), (2,20), (3,30). After the outer commit, `CommitTrans` and `Rollback` each returned 0x800A0BDA (3034) (`txn-nest-observed`).
- Two files, one transaction: `a`'s later transaction touching T2 then T was rolled back in both. Committed values survived: T row 1=1010, T2 row 1=1100 (`txn-multi-two-files`).
- Blocked statements (`a`'s Edit on the middle row):
  - Multi-row UPDATE with no options (`txn-stmt-blocked-no-scope`, `-in-txn`): the call returned success, RecordsAffected 0, Errors 0, and no row of the range changed, including the unblocked ones.
  - With `dbFailOnError`: 3260, RecordsAffected 0. Earlier and later single-row statements were applied; inside the transaction, `Rollback` followed.
  - In `-in-txn` phase 1, `a`'s read after `b`'s `CommitTrans` showed row 1 V=0 and row 50 V=1.
- `txn-range-close`:
  - `b`'s UPDATE of row 2 returned 3260 in both phases (during `a`'s transaction on row 1).
  - Closing `Workspace w2` with an open transaction: `a` read N=10.
  - Closing and reopening the Database with the transaction open: `a` read 1010, and `Rollback` restored 10.
- `txn-flush-live-images` (`a`'s file image per step):

| Step | Main-file hash |
| --- | --- |
| start, inside the first transaction | unchanged `d13a…` |
| after ordinary `CommitTrans` | changed (`139bde…`) |
| inside the second transaction | unchanged |
| after `CommitTrans(dbForceOSFlush)` | changed (`a0080c…`) |
| after inner forced commit, after outer `Rollback`, after an implicit UPDATE, after `Idle(dbRefreshCache)` | all unchanged |
| after close | changed (`cf8f8f…`) |

  - `b`'s first read, about 0.9 s after the first commit, returned 1010. Its next two reads, 59 to 95 ms after later commits to 2010 and 3010, still returned 1010.

*TXN-VIEW* (`txn-view-readers`): `b`'s cursors never read the committed 1010 (reads 17 to 60 ms after `a`'s commit), for held and new cursors, with and without `Requery`.

**Probe or fixture defects**
- Read-visibility results are confounded by `b`'s read cache. Most reads came 20 to 1000 ms after `b`'s own earlier read. `read-refresh-elapsed` shows the staleness lifts after 6.5 s without a move. The engine's `dbPageTimeout` default was never read. Affected: `txn-view-readers`, `txn-nest-observed`, `txn-multi-two-files` (all `b` reads stale), `read-path-*`, `read-id-delete-reinsert`, `read-snapshot-cursor-kinds`, `read-role-edit-existing-readers`.
- `write-alloc-counter-rollback`: the ID read after `Update` returned row 1 for every writer (cursor stays on the old record), so the generated value after `Update` is not captured.
- `write-ddl-vs-open-transaction`: `a`'s own `ALTER TABLE` and `CREATE INDEX` after its insert in its own transaction returned 3211 (0x800A0C8B) before `b` ran. No catalog change existed to be rolled back; `b`'s field names are identical throughout.
- `txn-local-xproc-same-page[-mdb]`, `-same-row` and `txn-range-close`: `b`'s competing write was always refused (3260) and never retried after release. The resolution orders (`B:C A:R` and so on) and "does an unrelated update survive rollback" are not exercised, despite the `WRITE-REBASE` tag.
- `write-tree-unique-candidate`: `IX_N` is non-unique in the fixture, so the secondary-index uniqueness candidate is untested. The `read-schema-held-descriptors` DDL while cursors were open was refused (3262), and `DROP INDEX IX_U1` found no index (3372).
- `txn-local-xproc-conflict-settings`: the record holds no per-call timings (the description says the worker records them). `txn-local-xproc-same-row` contains no read of engine defaults, although `-conflict-settings` says it does.
- `DBEngine.Errors` is not cleared by later successful calls. Many records show Errors count 1 (3260, 3262, 3372) after a successful call. The `Errors` reads there are not per-call evidence.
- `write-original-same-row[-mdb]` and `-changed-then-restored`: nobody reads the row after close, and `b`'s final read comes 0.6 s or less after `a`'s second `Update`. The final stored value is not established, because `b` read 222 and 10 while `a` read 100.
- `write-timing-release-boundaries`: no check shows that the `Edit` was still held at the release and close steps.

**Revisit**
- No native lock, handle or byte trace is in these records, only public DAO answers. So `READ-ROLE` lock purpose and `READ-PATH` validation or retry are unobserved. Nothing pauses ACE mid-acquisition.
- Uncovered: `READ-LIFE` (close, cancel, process death), `WRITE-REBASE` (no CalcDb preparation pause), `WRITE-FAIL`, and promotion by lock count beyond the 3035 error. Also uncovered are `TXN-TEMP`, `TXN-SUPPORT` and the `TXN-CLOSE` clone and multi-participant cases. `TXN-NEST` has no `BeginTrans` exhaustion and no empty-level or same-page-at-all-levels cases. The `TXN-DDL` rollback restoration, the SMB lanes and the COM-thread and ADO-vs-DAO `TXN-LOCAL` lanes are also open.
- Open: whether `b` sees commits at all within ~1 s without Idle or an elapsed page timeout. The elapsed-time probe only varies one delay, and the `read-refresh-ladder` 1001-vs-1002 result is unexplained. The `read-schema-held-descriptors` 3011 for U (while `b`'s recreate had returned) and `far-row`'s stale N=10 versus `other-table`'s fresh 1010 are also unexplained.
- Row 2 blocked while row 1 was pessimistically edited, in default ACCDB and in `Locking Mode` 0 and 1. Whether that is page locking or `b`'s own mode is untested.

### r-enc-link

All 43 retained records were read. Loaded `ACEDAO.DLL` is 16.0.20527.20196 (`ACEERR.DLL` 16.0.20527.20016), not the card's 20016. DAO was `DBEngine.120`; ADO was `Microsoft.ACE.OLEDB.12.0`.

**Observed: R-ENC-1**

- **ENC-PROFILE, creation.** `CreateDatabase` succeeded for these:
  - plain MDB (option 64) and plain ACCDB (128);
  - MDB with `dbEncrypt` (66);
  - MDB and ACCDB with `;pwd=secret1` in the locale string (`create-enc-*-pwd`);
  - MDB and ACCDB given a password afterwards by `NewPassword("", "secret2")` (`create-enc-*-newpwd`).

  ACCDB with `dbEncrypt` (130) failed: `hr=0x80020009`, scode `0x800A0BB9` "Invalid argument.", no `IErrorInfo`, no file (`create-enc-accdb-enc`). All MDB fixtures are 655360 B and all ACCDB fixtures 749568 B.
- **ENC-PROFILE, DAO credentials.** On the four password fixtures (`profile-pwd-*`, `profile-newpwd-*`), `OpenDatabase` failed with Errors number 3031 "Not a valid password." (scode `0x800A0BD7`) when the password was omitted, `;pwd=`, wrong, or the right one in upper case. It succeeded with the exact password, read-only and read/write.
- **ENC-PROFILE, stale Errors.** `DBEngine.Errors` still held the earlier 3031 (count 1) after a later successful open in the same probe. Counts were 0 in probes with no earlier failure, for example `update-pwd-*`.
- **ENC-PROFILE, ADO.** `Connection.Open` on `enc-mdb-pwd` failed with no password and with a wrong one:
  - `hr=0x80020009`, EXCEPINFO scode `0x80040E4D` "Not a valid password.";
  - `Errors` count 1, Number -2147217843, SQLState "3031", NativeError -124782449;
  - the following `Execute` failed with `0x800A0E78` "Operation is not allowed when the object is closed."

  The open with `secret1` worked and `COUNT(*)`/`SUM(LEN(M))` returned VT_I4 64 and VT_R8 192000 (`ado-open-pwd-mdb`).
- **ENC-UPDATE.** With `;pwd=secret1`, six statements all succeeded on MDB and ACCDB: updates of rows 1, 32 and 64 with 3000-character memos, an insert, `DELETE ID BETWEEN 10 AND 20`, and a bulk insert. `Errors` count 0 and the reopen counts matched. Counts went from N=64, LM=192000 to N=108, LM=330000. MDB went from 655360 B to 1024000 B and ACCDB from 749568 B to 1118208 B (`update-pwd-mdb`, `update-pwd-accdb`).
- **ENC-PASSWORD, in place.** `NewPassword` returned `hr=0` with Errors count 0 in these cases, and the file size was unchanged, with a different SHA-256 prefix:
  - set `"" -> secret3` on plain MDB (`54628728f059` to `cf72285e6375`) and plain ACCDB (`081c957990db` to `0c2f2003be39`) (`newpassword-set-*`);
  - change `secret2 -> secret3` on MDB (`e8453dbcd84d` to `55f5b1cf66e3`) and ACCDB (`a91330cdd819` to `2a295fc15d9e`);
  - remove `secret1 -> ""` on MDB (`c396c631ea52` to `4e2373a0bce5`), after which the file opened with no password and also with `;pwd=secret1` (`newpassword-remove-mdb`).

  After set and change, opening with no password gave 3031. After change, the old password gave 3031 and `secret3` opened.
- **ENC-PASSWORD, wrong old password.** On MDB and ACCDB (`password-wrong-old`), `NewPassword("not-the-old","x1")` and `NewPassword("","x2")` both failed with `hr=0x80020009` and Errors 3031. File hashes were unchanged, the original password still opened, and `x1` and `x2` gave 3031.
- **ENC-LIFE (MDB only).** `NewPassword("secret1","secret3")` on an open exclusive database did not stop the held dynaset: `MoveNext` read row 3 ("r3", 3000) and `Bookmark` returned VT_ARRAY|VT_UI1 `02020000`. `Execute UPDATE` ran with `RecordsAffected` VT_I4 1, and a fresh snapshot read "after-password". After close, the old password gave 3031 and `secret3` opened (`life-held-cursor-mdb`).
- **ENC-REKEY.** `CompactDatabase` returned `hr=0` with Errors count 0 in each of these:
  - Source password in SrcLocale and destination `;pwd=other1`, MDB and ACCDB: the destination opened only with `other1` (none and `secret1` gave 3031) and the source file was unchanged (`rekey-pwd-*`).
  - Plain MDB with option 66: the destination opened with no password.
  - Plain MDB with option 66 and `;pwd=encpw`: no password gave 3031 and `encpw` opened.
  - `dbEncrypt`-created MDB with option 68 (`dbDecrypt`): the destination opened with no password (`rekey-options-mdb`).

**Observed: R-LINK-1**

- **LINK-META.**
  - Attributes are VT_I4 1073741824 for `LK` and 1073872896 for `LKP` (after setting 131072 before `Append`).
  - `Connect` after `Append` in the creating session read back relative (`;DATABASE=inputs\r-enc-link-src.accdb`). In later sessions it reads back as an absolute path (see defects). `LKP` Connect is `MS Access;PWD=secret1;DATABASE=...`.
  - The `TableDefs` count is 8, in the order `L, LK, LKP, MSysACEs, MSysComplexColumns, MSysObjects, MSysQueries, MSysRelationships`.
  - Fields (ID I4/17, V Text, N Text, D I4, M Memo type 12) carry `SourceField` and `SourceTable` "SRC". Indexes `IX_SRC_D` and `PK_SRC` are present.
  - `RecordCount` is VT_I4 -1 (`create-linked`, `meta-read-through-link`, `meta-source-absent`).
  - `Updatable` was VT_BOOL 0 for `LK` in a read-only open.
- **LINK-META, public views.** DAO `SELECT ... FROM MSysObjects` failed with 3112 "Record(s) cannot be read; no read permission on 'MSysObjects'." (scode `0x800A0C28`). ADO `OpenSchema(adSchemaTables)` returned 8 rows, with `L` as TABLE and `LK` and `LKP` as LINK (`public-views`).
- **LINK-RELINK, unchanged.** `RefreshLink` with Connect unchanged returned `hr=0`, Errors 0, and Connect, Attributes, fields and the 2 indexes unchanged. `linked.accdb` went from 221184 B to 229376 B in that read/write probe (`relink-refresh-unchanged`).
- **LINK-RELINK, second source.** Setting Connect to src2 read back unchanged, and `RefreshLink` returned `hr=0`.
  - A retained `Fields` collection then reported D as type 10 (was 4), and the index names stayed `IX_SRC_D, PK_SRC`.
  - The held dynaset (opened before the relink) kept returning old rows (ID 2, D VT_I4 10).
  - A new recordset returned IDs 11–14 with D as VT_BSTR "10"/"20" (`relink-to-second-version`).
- **LINK-OPEN / LINK-RELINK, bad targets.** `RefreshLink` results:
  - missing file: 3024 "Could not find file";
  - the linked file itself: 3011 ("could not find the object 'SRC'");
  - Connect `garbage`: 3170 "Could not find installable ISAM.";
  - unknown option `;FOO=bar;DATABASE=inputs\r-enc-link-src.accdb`: `hr=0`;
  - restore: `hr=0`.

  After every step, including the failed ones, `SELECT * FROM LK` opened and read the original 4 rows. The Connect property read back the bad value (`relink-bad-targets`).
- **LINK-OPEN, credentials (on `LKP`).** `RefreshLink` with `PWD=wrong-pass` and with no PWD both failed with 3031. With `PWD=secret1` it returned `hr=0`, but Errors still showed the stale 3031. All three following recordsets read 4 rows. `Attributes(0)` read back as 1073741824, so the `dbAttachedTable` bit stayed and the `dbAttachSavePWD` bit cleared (`link-credentials`).
- **LINK-PUSHDOWN (native lane).** In all 8 queries (equality `'a'` with an `'A'` row, IS NULL, LIKE, ORDER BY with Null and empty text, TOP 2, `CLng(N)`, DISTINCT, GROUP BY), link and local gave identical rows and order. The two lanes also matched on errors. Details:
  - `TOP 2 ... ORDER BY D` returned 3 tied rows, IDs 3, 2, 1, in both lanes.
  - `CLng(N)` raised at `Field.Value`, not at open: row 2 `0x800A0D88` "Data type mismatch in criteria expression.", row 3 (Null) `0x800A005E` "Invalid use of Null". Errors held a single entry, Number 94 (`pushdown-link-vs-local`).
- **LINK-UPDATE.** Through `LK`:
  - `UPDATE ... WHERE D = 10` gave `RecordsAffected` 3;
  - a full insert, an insert without the counter key (generated ID 6), a delete, and a 5000-character memo update each gave 1;
  - `AddNew/Update` gave `LastModified` VT_ARRAY|VT_UI1 `08020000` and row ID 7;
  - `Edit/Update` on the first row worked.

  Errors count 0 throughout (`update-through-link`).
- **LINK-TXN / LINK-SESSION.** `LK` and local `L` both took part in `BeginTrans`/`Rollback`/`CommitTrans`/nesting: in-transaction counts went up, rollback restored them, commit and nested commit kept them.
  - For `LK` the counts were 5, 6, 5, 6, 7 in order (start, in-transaction, after rollback, after commit, after nested).
  - `IsolateODBCTrans` read VT_I2 0, then -1 after setting 1.
  - In a second Workspace, `d2` saw 4 while `d1` saw 5 mid-transaction, and 4/4 after rollback and 5/5 after commit (`txn-local-and-link`, `session-two-workspaces`).

**Probe or fixture defects**

- **Relative link path resolves to the fixture-creation attempt.** Every probe that reads `LK`/`LKP` through the stored Connect goes to `...\attempts\r-enc-link.create-linked.92c44b92f7.a1\inputs\`, not to its own `inputs\` copy. Evidence: `meta-source-absent` and `meta-read-through-link` show that absolute path in `tdf.Connect()`, and that directory still exists on disk.
- **Consequences of that defect:**
  - `meta-source-absent` did not have an absent source: the `OpenRecordset` calls on `LK` and `LKP` returned `hr=0`.
  - `update-through-link`, `txn-local-and-link` and `session-two-workspaces` mutated one shared source across attempts. Reads in `update-through-link` returned IDs 101, 103 and 201, inserted by other probes, and `LK` counts at start differ between probes (5 versus 4). Mutation arms are therefore not independently restored.
  - The independent readers (`update-through-link`, `txn-local-and-link`) read the attempt-local `inputs\r-enc-link-src.accdb`, which stayed at 4 rows with hash `e330c1b86c6c`. They do not show source effects.
  - `schema-change-under-cursor` altered the attempt-local `inputs\r-enc-link-src.accdb` (hash `e330c1b86c6c` to `03aef834c844`), while the link read the unchanged creation-dir copy. LK still showed column N and 4 rows after `RefreshLink`, so the LINK-SCHEMA question is unanswered.
  - `link-credentials` and `relink-bad-targets` read their recordsets through the stored link. This is probably why the recordsets still opened after failed `RefreshLink`; relative Connect values set in-session do resolve to the attempt directory, so these were not isolated. The cause is inferred, not shown.
- **Missing `accdb-enc` fixture.** `create-enc-accdb-enc` failed (3001), so `profile-unprotected` (which opens an `accdb_enc` role) has no retained record. I did not confirm that this is the reason.
- `meta-source-absent` checks "enumeration without connecting" only against a source that was present.

**Revisit**

- **ENC-PROFILE:** ACCDB encryption via a supported route (the `dbEncrypt` option is refused, 3001); header discriminators in malformed copies; unprotected-file open with wrong or empty password.
- **ENC-PAGE:** the records hold hashes and sizes only; raw versus decoded pages were not interpreted here.
- **ENC-UPDATE:** only DAO with the password given; no torn or crash arms.
- **ENC-PASSWORD:** no secured-workgroup refusal control, no ADO or Access route; `NewPassword` on ACCDB only set and change.
- **ENC-REKEY:** ACCDB option variants; the `dbEncrypt` route for `CompactDatabase` was tried only on MDB.
- **ENC-LIFE:** only an MDB held cursor, no ACCDB held cursor.
- **Not covered:** ENC-FAIL, ENC-TEMP; the card's DLL version 20016 versus the loaded 20196.
- **LINK-RELINK:**
  - Index preservation is not discriminated: src and src2 both define `IX_SRC_D`.
  - The 221184 B to 229376 B growth in `relink-refresh-unchanged` cannot be attributed to `RefreshLink` versus read/write open (no control).
  - Native catalog byte deltas were not examined.
- **LINK-SCHEMA, LINK-IDENTITY (bookmark across sessions, duplicate non-key rows beyond `UPDATE D=10`), LINK-TXN source states, LINK-SESSION connection counts:** all need valid per-attempt sources, which the path defect currently breaks.
- **Not covered:** LINK-CANCEL, LINK-BROWSER, ODBC/ISAM families, a concurrent second writer, denied access, expired credential, a wrong file at the same path, and `Updatable` on `LKP` and in read/write opens.

### r-expr-1

Scope: 83 records. They cover the main2 fixture (39 records), the superseded `r-expr-1-main` fixture (the other attempts), the three `create-*` records and 2 records with no capture. Unless stated, results come from the main2 attempts. Every attempt ran once (`a1`), so there are no A/A repeats. Types are `Field.Type()` numbers as returned (DAO DataTypeEnum). Environment in all records: loaded ACEDAO.DLL 16.0.20527.20196 (the book expects ...20016; only ACEERR.DLL shows 20016), `eng.Version()` "16.0", `db.Version()` "16.7", collating order 1033, process lcid 7177, x64.

**Observed**

- **Branch A, Null/Empty/Error/omission**
  - `a-null-empty-error-literals`: `IsNull(Null)` is VT_I2 -1 and `IsNull('')` is 0. `IsEmpty(Null)` and `IsEmpty('')` are both 0 and `IsError(1)` is 0. For all of these Field.Type is 3.
  - `a-null-empty-error-literals`: `Null + 1`, `Null & Null`, `Null = Null`, `Null <> 1`, `-Null` and `Not Null` return VT_NULL (Type 9). `Null & 'x'` and `'x' & Null` return "x". `Len(Null)` is VT_NULL and `Len('')` is VT_I4 0.
  - `a-null-empty-error-literals`, `d-predicate-consumers`: `CVErr` fails with 0x800A0C0D "Undefined function 'CVErr' in expression." (in a SELECT list, inside `IsError`, and in WHERE). `FindFirst("CVErr(5)")` fails with 0x800A0C00.
  - `a-optional-arguments`: `Nz` fails 0x800A0C0D "Undefined function" (direct, and via `[pv]` QueryDefs).
  - `a-optional-arguments`: `Mid('abcd',2,Null)` and `InStr(Null,...)` fail 0x800A005E "Invalid use of Null". `Mid('abcd',Null)` and `Round(2.5,Null)` fail 0x800A0D88. `Round(2.5)` is VT_R8 2 and `InStr(1,'abcd','C',0)` is 0 (compare 1 gives 3).
  - `a-parameter-inputs`, declared Long parameter: Null gives Null. Empty gives VT_I4 0 (`IsNull` 0, `pl+1` = 1). The VT_ERROR marker (-2147352572) gives Null. R8 2.5 reads back VT_I4 2. BSTR "q" and BSTR "" fail at the set with 0x800A0D5D. An unset declared parameter fails at OpenRecordset with 0x800A0BF5 "Too few parameters. Expected 1."
  - `a-parameter-inputs`, declared Text parameter: Null, Empty, the marker and empty BSTR all read back Null (`Len` Null, `pt & '|'` = "|"). "A\0B" gives `Len` 3 and a lone U+D800 gives `Len` 1. I4 7 reads back BSTR "7".
  - `a-parameter-inputs`, undeclared `[pv]`: the same four inputs give Null. I4, R8, BOOL, CY, UI1 and I2 inputs read back as BSTR ("7", "2.5", "-1", "1.5", "255"), and DATE 0.25 reads back "06:00:00".
  - `b-plus-versus-concat`: an undeclared parameter set to Empty or Null gives `[pe] + 7` Null and `[pe] & 'x'` "x".
- **Branch B, operator dispatch and types**
  - `b-numeric-text-compare`: `7 = '7'`, `7 < 'q'`, `'10' < 7`, `T = 7`, `L > T` and `D = '2.5'` open fine but fail on `Field.Value()` with 0x800A0D88. `'7' < '10'` is 0 and `7 < 10` is -1. A declared Long/Text parameter pair fails at OpenRecordset with 0x800A0D88. With undeclared `[ua]`/`[ub]`, both sides read back as text: (7,"7") gives eq -1; (7,"q") gives lt -1; (10,"9") gives lt -1.
  - `b-plus-versus-concat`: `7 + '7'` is R8 14 (Type 7), `'7' + '7'` is "77", `7 & '7'` is "77", and `7 & Null` is "7". `'q' + 7` fails on Value with 0x800A0D88. `N + T` is Null and `N & T` is "7". `True & ''` is "-1" and `#2024-01-01# & ''` is "2024/01/01".
  - `b-integer-widening`: `B + CByte(1)` is VT_I4 256, `I + CInt(1)` is 32768, and `L + CLng(1)`, `L * CLng(2)` and `I + L` fail 0x800A0006 on Value. `2147483647 + 1` is R8 2147483648 and `32767 + 1` is VT_I4. BIGINT (Type 16): `LL + 1` and `LL * 2` fail on Value for the max row, and `LL + 0.5` fails 0x800A0D88 on that row. The -1 row gives VT_I8 0 for `LL + 1` and `LL + 0.5`.
  - `b-mixed-precedence` (VarType, B−F are Single=S, Currency=C, Double=D, Long=L): C+D and D+C are Currency (6), C*D and D*C are Double (5), S+L and S+D are Double (5), C+L is Currency (6). `Y+1` is 3, `Y*Y` is 2, DT+1 and DT+C are Date (7), `DT-DT` is Double (5), B+B and I+B are 3.
  - `b-integer-conversion`: `CInt(2.5)`=2, `CInt(-2.5)`=-2, `CInt(3.5)`=4, `CInt(2.5000000000000004)`=3. `CInt(32767.5)`, `CLng(2147483647.5)`, `CByte(255.5)` and `CByte(-0.6)` fail with 0x800A0006. `CByte(1.5)`=2 (type 3, VT_I2). An undeclared parameter holding 2.5000000000000004 reads back "2.5", so `CInt([px])` = 2.
  - `b-intdiv-mod`: `7.5 \ 2`=4, `7 \ 2.5`=3, `-7.5 \ 2`=-4, `7.5 Mod -2`=0. `5 \ 0.4`, `1 \ 0`, `1 / 0` and `1 Mod 0` fail 0x800A000B on `Value()`. `0 / 0` fails 0x800A0006 and `2147483648 \ 1` fails 0x800A0006.
  - `b-fixed-point-fidelity`: `CCur(0.00015)` and `CCur(0.00025)` are both 0.0002; `CCur(0.00005)` is 0. A Currency parameter at max/min round-trips exactly. Max + CCur(0.0001), min − CCur(0.0001) and max*2 fail 0x800A0006 at OpenRecordset.
  - `b-fixed-point-fidelity`: `C + 0.00005` returns VT_DECIMAL (Type 20; `show` does not print the payload). `CDec(...)` always fails 0x800A0C03 "Wrong number of arguments...".
  - `b-floating-fidelity`: parameters 0.1 vs 0.10000000000000002 give eq 0, lt -1, diff -1.3877787807814457E-17. Single 1.1 vs Double 1.1 gives eq -1 and diff 2.3841857821338408E-08. `0.1 + 0.2 = 0.3` is -1. An undeclared parameter given NaN, ±Infinity or -0 reads back "nan", "inf", "-inf" or "-0"; the three non-finite values fail 0x800A0D88 at OpenRecordset.
- **Branch C, DATE**
  - `c-date-payloads`: DATE and R8 parameters with identical payloads behave identically. For -1.25 vs -1.5, lt is 0 and `CDbl` returns the raw payloads. `Hour(-0.25)`=6 and `Day(-0.25)`=30, while `DateValue(-0.25)`=0 and `TimeValue`=0.25. `DateDiff('h',-0.25,0.25)` is VT_I4 1. I did not investigate that value.
  - `c-date-payloads`: undeclared parameters holding DATE read back as text "06:00:00", and (DATE -0.25, DATE 0.25) fails 0x800A0D88.
  - `c-date-stored-predicates`: against Dts rows (-1.5 ... 45000.5), `D = CDate(-0.25)` selects Id 1 and `D = #12/30/1899 06:00#` selects Id 2. `D BETWEEN #12/29/1899# AND #12/31/1899#` selects 1,2,5,6,7 (not 3,4: -1.25 and -1.5), the same set as the numeric range. ORDER BY D is numeric.
  - `c-date-text-and-literals`: `CDate('1/2/2003')` is 37623, `CDate('2003-02-01')` is 37653, `IsDate('2/30/2003')` is 0. `CDate('2/30/2003')`, `CDate(-657435)` and `CDate(2958466)` fail 0x800A0D88.
- **Branch D, logical/bitwise**
  - `d-logical-bitwise`: the grid (order TT,TF,TN,FT,FF,FN,NT,NF,NN) is: And -1,0,N,0,0,0,N,0,N. Or -1,-1,-1,-1,0,N,-1,N,N. Xor 0,-1,N,-1,0,N,N,N,N. Eqv -1,0,N,0,-1,N,N,N,N. Imp -1,0,N,-1,-1,-1,-1,N,N. Not gives 0,-1,Null.
  - `d-logical-bitwise`: `6 And 3`, `6 Or 3`, `6 Eqv 3` and `6 Imp 3` are VT_I2 -1; `6 Xor 3` and `Not 6` are 0. `Null And 0` is 0 and `Null Or -1` is -1.
  - `d-predicate-consumers`: WHERE returns the row for 1, -1, 2, 2.5, '', '1', 'q' and 'True', and no row for Null, 0 and '0'. IIf agrees except `IIf('', ...)` selects the true arm. `FindFirst("Null")` gives NoMatch -1; every other criterion fails 0x800A0BB9 with NoMatch -1.
  - `mut-validation-rules`: field rule `[V] > 0` rejects V=-1 (3316) and accepts omitted V (Null). Required=True rejects omitted and explicit Null with 3314 (0x800A0CF2). Table rule `[V] < [R]` rejects (2,1) with 3316 and accepts (1,NULL). A table rule "Null" accepts (1,2).
  - `mut-validation-rules`: field rule `1/0` fails Update with 0x800A0EDA (3802 "Error evaluating CHECK constraint"). With V=5, the bare field rules 0, 2, '', '1', 'q', True and False each fail Update with 0x800A0CF4 (3316, EditMode 2), but rule "Null" accepts (the rule reads back as "Null " with a trailing space).
- **Branch E, conditional functions**
  - `e-eager-failures`: `IIf(True,1,1/0)` is R8 1, `IIf(False,1/0,2)` is R8 2, and `IIf(True,1,CInt('q'))` is VT_I4 1. The same holds in rows (`IIf(True,1,1/[Denominator])` on Denominator=0 gives R8 1).
  - `e-eager-failures`: `Switch(True,1,True,1/0)` and `Choose(1,'a',1/0)` fail 0x800A000B at OpenRecordset. In `1/0 + CInt('q')` the first operand's error is reported, and reversing the operands reports the other error (0x800A0D88 / 0x800A000B).
  - `e-conditional-selection`: `IIf(Null,1,'a')` is "a" and `IIf(True,1,'a')` is BSTR "1". `IIf('',1,2)`=1. `Switch(False,1)` is Null. `Switch(True)` and `Switch(True,1,False)` fail 0x800A0C03, and `Switch()` fails 0x800A0005. `Choose(0)`, `Choose(3)`, `Choose(0.5)` and `Choose(-0.5)` are Null; `Choose(1.5)`="a", `Choose(2.5)`="b", `Choose(Null,...)` fails 0x800A0D88.
  - `e-failure-phase`: `1/[Denominator]` opens with no error and fails 0x800A000B on the Value read of row 2 (Denominator 0), for types 2, 4 and 8. Row 3 then reads Null. `CInt([TextValue])` fails 0x800A0D88 (row 2) and 0x800A005E (row 3). A WHERE that matches no rows (`Id = 999`) opens and returns no rows for `1/0` and `1/[Denominator]`. A traversal that never reads the failing column (types 4 and 2 on `CInt`) completes, including MoveLast.
- **Defaults and ValidateOnSet**
  - `mut-default-values`: DefaultValue `2+3` is visible after AddNew as VT_I4 5; `'x' & 'y'` gives "xy"; `#2024-01-31# + 1` gives DATE 45323. Setting DefaultValue `1/0` fails 0x800A000B, and `'q'` on a Long field fails 0x800A000D (both at the property set). An explicit Null assignment in AddNew persists as Null, and SQL INSERT omitting A stores 5.
  - `stage-validate-on-set`: with ValidateOnSet True, V=-1 fails 0x800A0CF4 on the Value set (source DAO.Field), V stays 5 and EditMode is 1. With False, the set succeeds (V reads -1) and Update fails 0x800A0CF4 (source DAO.Recordset), and CancelUpdate restores the old value. With W=-1 and V=-1 both invalid, the V message is returned in both assignment orders. `Errors` keeps the 3316 entry after a later successful Update.
- **Patterns**
  - `pat-01-wildcard-modes`: DAO `LIKE 'Q%'` matches only the literal "Q%" (Id 3), and `LIKE 'Q*'` matches all 7 subjects. DAO `ALIKE` and ADO `LIKE` give the reverse: `'Q%'` matches all 7, `'Q*'` matches only "Q*", and `'Q_'` matches "Q*", "Q%" and "Q7". These are single-fixture results.
  - `pat-02-bracket-classes`: `LIKE` and `ALIKE` are identical across all ten patterns. `[]` matches only "", `[[]` matches "[", `[[]]` matches "[]", `[!a]` matches `[ ] - ! ^` but not "", and `[!]` and `[^]` are literal.
  - `pat-03-ranges`: `[C-A]` fails every row with 0x800A005D "Invalid pattern string". `[a-C]` matches A–C and a–c. `[à-é]` matches à, é and æ.
  - `pat-04-05-structural-diagnostics`: `q[`, `q*[` and `q*[C-A]` (and ALIKE `q%[...`) fail 0x800A005D only for subjects "qx" and "qabc" (not "x", "q", "" or "yq"); `xq[` fails only for "xqx". Constant `'x' LIKE 'q['` returns 0.
  - `pat-06-null-and-operand-order`: `Null LIKE 'q['` is Null with no error. `'' LIKE '*'` is -1 and `'' LIKE '?'` is 0. An undeclared (ps="5", pp="5") gives -1; Empty and "" inputs give Null.
  - `pat-07-08-expansion-and-classes`: `LIKE` treats "æ" as one unit (matches `?`, not `??`, `ae` or `a?`), while `S = 'ae'` is -1 for æ and Æ. `[æa]e` does not match "æ", `[æa]?` matches "ae", and `a[ef]` matches "ae" only.
  - `pat-09-contraction` (General vs Spanish): `?`, `??` and `c?` give identical results in both sorts. `S > 'cz'` is 0 for "ch" in General and -1 in Spanish. ORDER BY S places "ch" after "cz" in Spanish only. `StrComp(S,'d',2)` fails 0x800A0005 on every row.
  - `pat-10-13-character-boundaries`: `?` consumes one UTF-16 unit. The surrogate pair gives `??` -1 and Len 2, and lone D800 and DC00 each match `?` with Len 1. `#` matches U+FF11 but not U+0661, and `[0-9]` matches only ASCII.
  - `pat-12-14-17-literal-shape`: `'A ' = 'A'` is -1 and `StrComp` gives 1. `LIKE 'A'` does not match "A " (it matches "A"/"a"), and NBSP and tab give eq 0. `LIKE 'MN'` matches only the whole subject (case-insensitive), and the p17 outcomes are exact with no failures.
  - `pat-15-strcomp-selector`: `StrComp('a','A')`=0, selector 0 gives 1, selector 1 gives 0. Selectors 2, -1 and 1.5 fail 0x800A0005, 3 gives 0, 0.5 gives 1, and Null or 'x' fails 0x800A0D88.
  - `pat-18-composition-and-folding`: decomposed e+U+0301 equals precomposed é under `=` and `StrComp(...,1)` (Len 2 vs 1), but `LIKE 'é'` does not match it. `ß = 'ss'` is -1. `ı` and `İ` equal neither "i" nor "I"; `UCase('ı')` and `LCase('İ')` return the same character. Half-width ｱ equals ア.
- **Inventory/runtime**
  - `host-routes-domain-eval`: `DLookup`, `DSum` and `DCount` work, but `Eval` fails 0x800A0C0D "Undefined function".
  - `runtime-rnd-now`: `Rnd()` returns the same value in every row within one execution and different values across executions. `Rnd(Id)` differs per row, and `Rnd(0)` equals the preceding `Rnd()` in the same row. `Now()` is identical across rows and columns.
  - `inv-string-functions`, `inv-date-and-info`, `inv-number-conversion-financial`: see the records for individual values. `IsArray` is undefined, and `VarType(1.5)` and `VarType(2147483648)` are 14.
  - `bind-names-and-literals`: `[a!b]` as an alias fails 0x800A0C35. Four nesting forms fail 0x800A0C03 "Expression too complex" (at 32 levels or more).

**Probe or fixture defects**

- `r-expr-1-main` (`create-main`): `CREATE TABLE Vals ... DECIMAL(28,4)` failed 0x800A0CDC (3292), so Vals is missing. The 34 `r-expr-1-main` attempts that touch Vals report "cannot find input table or query 'Vals'" (0x800A0C06). Probes that do not touch Vals are identical to main2 apart from the attempt path (0–4 diff lines). Treat the `-main` records as superseded.
- `r-expr-1-main2` (`create-main2`): the Decimal `Vals.M` value set failed 0x800A0EAF "Scaling of decimal value resulted in data truncation" and the row was still inserted. M is therefore Null everywhere (VarType 1, Type 20). All Decimal field results in `b-mixed-precedence` and `b-fixed-point-fidelity` measure a Null, so the Decimal fidelity discriminators are not answered.
- `b-integer-conversion`: `SELECT ... CByte(D) AS d FROM Vals` fails 0x800A0C1F (alias `d` collides with field D). It tests the alias, not conversion.
- `bind-parameter-lifecycle`, `pat-11-16-counted-nul-and-reuse`: the worker died with 0xC0000005 inside `lrs.Requery` and `prs.Requery`. The records have `execution=Uncertain` and `capture=Incomplete` and no trace, so `show` prints "record has no capture". They are blocked by the crash recorded in `r-crash-requery` (Requery on a QueryDef-opened recordset).
- `d-predicate-consumers`: `FindFirst` ran on one recordset for every criterion in sequence, and each call fails 0x800A0BB9 with NoMatch -1. This does not separate "criterion not accepted" from recordset state after the first NoMatch.
- `mut-validation-rules`: with V=5 only, bare-literal field rules (0, 2, True, False, ...) cannot show whether the rule value is read as a truth value or as a comparison to the field.
- The first query of `a-null-empty-error-literals` on main (not main2) and several `a-optional-arguments` branches only record "undefined function" for `Nz` and `CVErr`.

**Revisit**

- Branch A: no real Error value was ever made (`CVErr` undefined), so `IsError(Error)`, Error consumers, optional-argument markers for functions that exist, and Find/validation with Error remain open. Array/object inputs are not covered.
- Branch B: Decimal fidelity (high word, scale), numeric pairs through declared parameters, native fields and host code (only fields, literals and some parameters were run), and cases pairing the same payload with a different declared origin.
- Branch C: Date/Time Extended, an indexed access versus a residual predicate, formatting.
- Branch D: And/Or results fed to Find and to validation/default contexts, AllowZeroLength, Error-valued predicates.
- Branch E: the `Nz` replacement matrix, marker-based effect order (no host route), prepared-plan/reprepare status, object default-member coercion.
- EXPR-BIND: parameter lifecycle, SQL replacement and schema-change invalidation (crash), and the lack of an A/A repeat for any main2 attempt.
- EXPR-STAGE and EXPR-MUTATION: no host markers, no clock/random defaults on a field, and no transaction rollback or `CancelUpdate`-after-default combinations.
- Patterns: no second database option setting for PAT-01; ADO only for PAT-01; Binary/Text/Database profiles for PAT-03; PAT-11/16 (crash). PAT-17 timing, resource use and memo-state counters are not captured.
- The loaded ACEDAO.DLL is 16.0.20527.20196, not the build the card names (20016). The card's expected-build wording needs reconciling.

### r-crash-1-kill

Scope: process termination only, with the OS and cache alive (`TerminateProcess` on writer A, the exit code recorded in every killed record is 32343). This is not power loss, and no write recording was done. Each kill point has an ACCDB (ACE 16.0.20527.20196) and an MDB variant. After the kill, process B waits 1.5 s, freezes the file bytes and then opens. All 32 kill records read as `execution=Terminated capture=Complete`. The 2 `create-*` records are `ReturnedSuccess` fixture builds, and no fixture with failed creation calls belongs to this card.

**Observed**

*Common to every ACE-first and Compact-first opener (B) in these probes*
- B's `eng.OpenDatabase` returned hr=0 (shared read/write), and `eng.Errors().Count` was VT_I2 0 before and after the reads. B did not see an error, a repair request or an ACE error number in any record. Every compact-first `CompactDatabase` also returned hr=0 with Errors count 0, and the compacted output opened (read-only flag `VT_BOOL -1`).
- Frozen lock file: 64 B in all shared-writer kills, hash `1f4a82a98356`, identical for `.laccdb` and `.ldb`. B read it natively (`ok read 64 bytes`). Its bytes are not displayed by `show`. The `after-opener` file listings list only the database file (plus the compacted file), so the lock file was absent after B's close.
- A's database file was byte-identical (same hash) at the kill, in B's frozen copy and after B's open and close. B did not change the database file. In the compact-first probes the source stayed at its pre-kill hash, so nothing here says ACE repaired the source.

*Kill with changes uncommitted or not yet committed (the file stayed at the fixture hash)*
- `accdb-kill-txn-uncommitted-ace-first` / `mdb-kill-txn-uncommitted-ace-first`: after BeginTrans plus an UPDATE of ten rows (returned hr=0), B read COUNT 100 (VT_I4), SUM(N) 5050 (VT_R8), MAX(LEN(M)) 500, MAX(ID) 100, and rows 1–10 with N=ID. The fixture file hash was unchanged (ACCDB `18c44ffaf110`, MDB `8fd13bd956e2`).
- `*-addnew-pending-*`: with AddNew and fields set but no Update, B saw the same 100/5050/500/100, with no row 9001 (MAX(ID)=100).
- `*-edit-updated-ace-first`: after Edit, Value and Update returned hr=0 on a dynaset kept open, B still saw row 1 with `S="row"`, not the edited text. The file hash was unchanged.
- `*-implicit-update-ace-first`: after `Execute` of the UPDATE (hr=0, dbFailOnError, no explicit transaction) and the kill, B saw SUM(N)=5050 and N=ID for rows 1–10. The file hash was unchanged at kill, at freeze and after B. The update was therefore not in the file 1.5 s after the kill, even though `Execute` had returned.
- `*-lv-replace-ace-first`: after the 40,000-character memo UPDATE returned, B saw MAX(LEN(M))=500, so the replacement was not visible. The file hash was unchanged.
- `*-bulk-insert-ace-first`: after `INSERT … SELECT` of 100 rows returned, B saw COUNT 100 (not 200) and MAX(ID) 100. The file hash was unchanged.
- Compact-first from the uncommitted, AddNew-pending and LV-replace kill states (all 6 probes): the compacted output read 100 / 5050 / 500 / 100, with row 1 N=1. Compacted sizes were ACCDB 294,912 B and MDB 200,704 B, equal to the sources.

*Kill after commit or Close*
- `*-txn-committed-ace-first` (CommitTrans default): the file changed before the kill (ACCDB `24b5a4d29bf1`, MDB `51ec79af5fa0`). B saw SUM(N)=15050 and N=1001..1010.
- `*-txn-committed-forceflush-ace-first` (CommitTrans(dbForceOSFlush), arg VT_I4 1): same hashes and same B result as the default commit.
- `*-after-close-ace-first` (Close before the kill, control): same hashes and values as committed. No lock file was listed at kill or freeze.
- In these probes, the default commit and the dbForceOSFlush commit are not distinguished by file hash or by what B saw.

*DDL*
- `*-ddl-create-ace-first`: after CREATE TABLE X and CREATE INDEX, the file had grown at kill (ACCDB 311,296 B `99fe4d3d1615`; MDB 217,088 B `72581f1683d1`). B's `TableDefs.Count` was VT_I2 7 (ACCDB) and 6 (MDB), against 6 and 5 for every other kill point. B's T data was unchanged (100 / 5050). The probe does not query table X itself.

*Exclusive writer (`open-only`, `implicit-update`; both formats)*
- B's native read of the lock file failed with Win32 error 2 at open, so no lock file was present after the kill. B's open succeeded and read the unchanged fixture (hash `18c44ffaf110` / `8fd13bd956e2`; SUM(N) 5050, N=ID). For `implicit-update-exclusive` the update was not visible, as in the shared case.
- A's own `before-kill` file read failed with `IOException 0x80070020` (sharing violation), so the file state at kill was not captured. See below.

*`open-only`*: reads were the unchanged fixture (100 / 5050 / 500 / 100), with hash unchanged and the lock file as above.

**Probe or fixture defects**
- Exclusive probes: A's `files[before-kill]` shows the database as `unread: IOException (HRESULT 0x80070020)` because the file is exclusively held. In these four records the pre-kill file state is missing. Only B's frozen hash is available.
- No other harness or fixture defect was found. The `modules[deliberate-truncation]` lines are the intended kill instrumentation, not an error.

**Revisit**
- The aggregate rows and the first ten rows show nothing about whether the lost writes were really in ACE's own buffers. No write trace exists: the card's recording route and offline reconstruction are not covered.
- Not covered: power loss, torn, partial or reordered writes, Access-first openers, and CalcDb cut images. Only ACE-first (shared, plus two exclusive points) and Compact-first (three points) were run. Compact-first was not run for the committed, forceflush, DDL, implicit, bulk-insert or edit-updated points.
- The 1.5 s wait before freezing is a single timing sample. Whether the unwritten implicit-update, LV-replace and bulk-insert state could still reach the file later is untested. The fact that `Execute` returned without the data being on disk by 1.5 s is a single observation per format.
- Process-kill points are untested for map-capacity transitions and splits, multi-file or nested Workspaces, concurrent lockers, rollback, and a kill during commit. The kill occurs only at API call boundaries, not between internal writes.
- The 64-byte lock-file bytes were not compared between probes (only hashes). The lock file's content with the exclusive writer was not observable (no file).

### r-str-ado-act

Observations come from the newest fixture generation (`ado4`, tooling 8a24599e) where one exists. Probes without an `ado*` fixture (`ado-describe`, `ado-provider-non-ace`, `ado-stream`, `act-*`, `str-*`) have a single record from tooling 61edefeb+dirty. Engine identity: ACEDAO.DLL, ACEOLEDB.DLL and ACEES.DLL are 16.0.20527.20196 and ACEERR.DLL is 16.0.20527.20016. The book's initial oracle is 16.0.20527.20016, so those builds differ. Records carry no msado15.dll file version.

**Observed**

*R-STRING-1*
- Read by ordinal (`str-ordinal-accdb`, `str-ordinal-mdb`): the value text and per-value BSTR byte lengths are identical in the MDB and ACCDB runs, and between the DAO-written `S` and ADO-written compressed `C` tables. Row-by-row T/M byte lengths:

| Row | T | M |
| --- | --- | --- |
| 1 | `""`: VT_BSTR, 0 | `""`: VT_BSTR, 0 |
| 2 | VT_NULL | VT_NULL |
| 3 (`a\0b`) | 6 | 6 |
| 4 (`\0`) | 2 | 2 |
| 5 | 8 (`abc\0`) | 16002 (4000+NUL+4000 units) |
| 6 | 10 | 20 |
| 7 | 510 | 10000 |
| 8 (`Bä€`) | 510 | 18000 |
| 9 (U+1D11E) | 508 (254 units) | 4000 |
| 10 | 8 | 140000 |
| 11 | 32 | 6 (`\0\0\0`) |

- Retained and fresh Field objects return identical values on all 11 rows (`str-named-retained`): `Fields("T")` against `Fields(1)`, and retained `M` against fresh `Fields("M")`. Repeated T and M reads agree on every row.
- Projection width and duplicated columns do not change returned values (`str-projection`). `M` in `SELECT M` equals `M` in the 9-column query, and `M AS M2` equals `M` on all rows.
  - Engine values, with positions counted in UTF-16 units:
    - Row 3: Len(T)=3, InStr(T,Chr(0))=2.
    - Row 4: 1 and 1.
    - Row 5: Len(T)=4, Len(M)=8001, Mid(M,3999,4)="nn\0n", InStr=4.
    - Row 9: Len(T)=254, Len(M)=2000.
    - Row 10: Len(M)=70000, Mid(M,3999,4)="8901".
    - Row 11: Len(T)=16, Len(M)=3, InStr=0.
  - Row 2: `T & ''` returns `""` while T is Null. LT, LM, MM and NT are Null.
- Revisiting rows (`str-revisit`):
  - Re-finding row 10 returns the same T and M.
  - `FindFirst "InStr(T, Chr(0)) > 0"` lands on ID 3 and `FindNext` on ID 4, both with NoMatch=0.
  - `FindPrevious "Len(M) > 1000"` from ID 10 lands on ID 9.
  - AbsolutePosition after `MoveFirst` on ID 1 is 0; after `MoveLast` on ID 11 it is 10.
- Retained Field state (`str-after-eof-close`):
  - At EOF, `Value` returns 0x800A0BCD "No current record." for T and M.
  - After `MoveFirst`, both read normally.
  - After `Recordset.Close`, `Value`, `Name` and `Fields.Count` return 0x800A0D5C "Object invalid or no longer set." `MoveFirst` on the closed recordset returns 0x800A0D5C.
- `Field.FieldSize` is refused on all 9 snapshot calls, T included, with 0x800A0CB3 (`str-memo-chunks`). `GetChunk` on Text field T returns 0x800A0CBB "Invalid field data type."
  - On Memo, `GetChunk` offset and size are in characters. Row 5: (7999,5) returns "nn", (8000,4) returns "n", (1000000,4) returns `""`. A Null Memo returns VT_NULL.
- Edits (`str-edit-requery`):
  - T=`x\0y\0` and M=6000 `e`+NUL survive Update and Requery. A fresh snapshot gives Len(T)=4, Len(M)=6001.
  - A 256-unit T assignment returns 0x800A0C5B. `Update` then succeeds with EditMode 0.
  - `CancelUpdate` returns 0x800A0BCC and T keeps its old value.
  - T=Null with M=`""` stores Null and `""`.
- Schema change (`str-schema-change`):
  - `ALTER TABLE S ADD COLUMN X` while a dynaset on S is open returns 0x800A0C8B (`Errors(0).Number` 3211).
  - The retained T Field still reads after `MoveNext` and `Requery`. `Fields.Count` is 2.
  - After the recordset is closed, the same ALTER succeeds. `Errors.Count` is still 1 with Number 3211 after that successful ALTER.
  - `UPDATE X = Left(T,10)` stores `a\0b` (LX=3), `\0` (1) and `abc\0` (4).
- ADO read of the same rows (`str-ado-read`, Mode=Read, server forward-only):
  - Value tags and text equal the DAO text for both `S` and `C`.
  - ActualSize equals the DAO byte lengths (0, 6, 2, 8, 16002, … and 4 for ID).
  - `GetString(clip, -1, "|", "\n", "<null>")` renders both row 1 (`""`) and row 2 (Null) as `<null>`. NUL is kept in rows 3, 4, 5 and 11.

*ADO-ASYNC* (`ado-async`)
- `Connection.Execute(..., adCmdText|adAsyncExecute)` on a 10^7-row cross join returns a Recordset at once. Immediately after, `cn.State`=5 and `ra.State`=4, which is a pending-execution state.
- `cn.Cancel()` returns hr 0. Then `cn.State`=1, `ra.State`=1, `EOF`=0 and `Value`=10000000, so the query was not aborted.
- Client-cursor `Recordset.Open(..., adAsyncFetch)` returns `State`=1 and `RecordCount`=1 immediately. `Cancel` returns hr 0.

*ADO-CONN / ADO-PROPS / ADO-PROVIDER*
- A fresh Connection reports `Provider`="MSDASQL", `Mode`=0, `CursorLocation`=2, `CommandTimeout`=30, `ConnectionTimeout`=15, `Version`="10.0", `IsolationLevel`=4096 (`ado-open-provider-property`).
- After `Properties` has been read, `cn.Provider = "Microsoft.ACE.OLEDB.12.0"` returns 0x800A0C94 "Supplied provider is different from the one already in use." `Provider()` then returns "MSDASQL.1". Whether the `Provider()` read or the `Properties()` read triggers this is not separated. Setting Provider first, as in `ado-async`, works.
- `Open` after that returns ODBC 0x80004005 "Data source name too long". Reading the retained `Data Source` wrapper after the failed `Open` returns 0x800A0D5C.
- Accepted ACE forms (`ado-open-alt-orders`, `ado-connstring-keys`):
  - `ConnectionString="Provider=…;Mode=Read"` then `Open()`.
  - `Provider` property then `Open(path)`.
  - A duplicated Provider key with whitespace around `Mode = Read`.
  - A quoted `Provider="…";Mode='Read'`. The `Provider()` getter returns the value with its quotes (`"Microsoft.ACE.OLEDB.12.0"`) before `Open` and without them after.
  - `Jet OLEDB:Database Password=` empty.
  - After `Open`, `ConnectionString` is expanded to `User ID=Admin;Data Source=…;Jet OLEDB:Engine Type=6;…`.
- Refusals: unknown key `CalcDbUnknownOption=1` returns 0x80004005 "Could not find installable ISAM." A string with Provider and Mode but no Data Source returns 0x80040E4D "Authentication failed." The connection is then closed and `Provider()` returns "MSDASQL".
- Non-ACE (`ado-provider-non-ace`):
  - A string with `Provider=MSDASQL` and one with Data Source only, where `Provider()` already returns "MSDASQL", both reach the ODBC driver manager and fail with 0x80004005 IM002 "Data source name not found…".
  - `Open()` with no string behaves the same.
  - Each leaves `State`=0, and `Errors.Count`=1 with Number -2147467259 and SQLState "IM002".

*ADO-RESULTS / ADO-CURSOR / ADO-SESSION*
- `Connection.Execute` (`ado-execute-results`):
  - `SELECT 1 AS A; SELECT 2 AS B` returns 0x80040E14 "Characters found after end of SQL statement."
  - With `adExecuteNoRecords` (129), `Execute` returns a null interface pointer.
  - A Recordset from `QEmpty` or `WHERE 1=0` has `State`=1, BOF and EOF both true, and 10 fields.
- Action statements (`ado-execute-action`):
  - UPDATE, INSERT and DELETE return a Recordset with `State`=0 or a null pointer.
  - `SELECT @@IDENTITY` returns 5.
  - Deleting a parent row that has related rows returns 0x80004005, SQLState 3200.
- Effective cursor against requested (`ado-rs-cursors`, Mode=1). Effective `LockType` was 1 in every row of this list.
  - Server forward-only, keyset and dynamic requests all give effective `CursorType` 0, with `RecordCount` -1 and `MoveLast` returning 0x80040E24 "Rowset does not support fetching backward."
  - Server static gives type 3 with `RecordCount` 4 and `MoveLast` works.
  - Client static with batch-optimistic lock gives type 3, `LockType` 4 and `RecordCount` 4.
  - Client forward-only gives type 3.
  - Server keyset on table `K` via `adCmdTable` or `adCmdTableDirect` gives type 1.
- Session (`ado-session`):
  - Three objects on one Connection share `ActiveConnection`.
  - `cn.Close` leaves `rs1`/`rs2`/`rs3`, the retained ActiveConnection and `cmd` at `State`=0, and `cmd.ActiveConnection` returns a null pointer.
  - A second Connection stays open, and a bad statement on it records its own error (SQLState "3000").
- Errors (`ado-errors`):
  - Misspelled SQL, a missing table and a missing column return 0x80040E14, 0x80040E37 and 0x80040E10.
  - `Recordset.Open "SELECT FROM WHERE"` returns 0x80040E14.

*ADO-PARAM / ADO-EDIT / ADO-BATCH / ADO-TXN / ADO-VIEW / ADO-SCHEMA / ADO-BULK / ADO-STREAM / ADO-PERSIST*
- Parameters (`ado-params`):
  - Saved query `QParam` with `adCmdStoredProc`: `Parameters.Count`=0 before and after `Refresh`. `Execute` returns 0x80040E10 "Too few parameters. Expected 1."
  - `SELECT … WHERE Code = ? OR Code = ?`: `Refresh` gives Count=2 (adVarWChar-sized 510, Precision 255, Empty value). Two Appends make Count=4 and `Execute` returns 0x80040E10 "Parameter ?_1 has no default value."
- Edit (`ado-edit`, server keyset, optimistic):
  - A value assigned and then `MoveNext` posts the edit (`EditMode` 0 afterwards, "moved" persisted).
  - `CancelUpdate` restores the old value.
  - `AddNew` plus `Update` gives ID 5.
  - `Field.UnderlyingValue` returns 0x800A0CB3 "Current provider does not support refreshing underlying values." both before and after `Update`.
- Batch (`ado-batch`, client cursor, batch-optimistic):
  - Three staged changes: row 1 T→b1, row 2 Code→a1 (a unique-key duplicate), row 3 T→b3.
  - `UpdateBatch(adAffectAll)` returns 0x80004005 (duplicate key). Afterwards the source holds b1 and b3, and row 2 is unchanged.
  - Statuses read back: row 2 = 2, the others = 8.
  - After `Filter(adFilterConflictingRecords)`, `RecordCount`=0.
- Transactions (`ado-txn`):
  - `BeginTrans` returns 1 and then 2. Inner rollback and outer commit leave "t1".
  - A third `CommitTrans` or `RollbackTrans` returns 0x8004D00E.
  - `IsolationLevel=1048576` is accepted. The next `BeginTrans` returns 0x8004D008 "Neither the isolation level nor a strengthening of it is supported." The following UPDATE autocommits and is visible on a second connection.
- View (`ado-view`, client static):
  - Filter `T LIKE 'a*'` and `'a%'` both give `RecordCount` 1.
  - `Find "ID = 3"` and `"Code LIKE 'b*'"` both land on b3.
  - `Find "ID = = 3"` returns 0x800A0BB9 with the position unchanged.
  - A Clone starts at `AbsolutePosition` 1 with `Filter` 0 and `Sort` `""`. After the parent is filtered to 1 row, the clone still has `RecordCount` 4. `Bookmark` returns VT_R8 on both parent and clone.
- Schema (`ado-schema`): `OpenSchema` ids 20, 4, 12, 27, 16, 23, 22 succeed. Id 9999 returns 0x800A0BB9. Restrictions `Empty` and `Null` on id 20 are accepted.
- Bulk (`ado-bulk`):
  - `GetRows(-1)` returns a 2-D VT_ARRAY|VT_VARIANT with bounds 0+4 × 0+4. `GetRows(2,1,"T")` also succeeds. Element values are not captured.
  - `GetString` with `|`, `\n` and `<null>` renders Null as `<null>`.
  - Default `GetString(adClipString)` is tab-delimited with rows run together.
- Stream (`ado-stream`):
  - A Unicode `WriteText` of "a b€𝄞" gives `Position`=`Size`=14. Binary `Read` returns `fffe` plus UTF-16 LE.
  - `ReadText` after `Charset="utf-8"` returns 0x80070057, and `SaveToFile` then writes 17 bytes.
  - `SetEOS` at position 0 gives `Size`=0.
  - After `Close`, `Size` and `ReadText` return 0x800A0E78.
  - Switching a binary stream with data to text returns 0x800A0C93 while `Position` is not 0; after `Position`=0 it succeeds. Reading the 3-byte binary stream `410042` as Unicode returns "A".
- Persist (`ado-persist`, MSPersist):
  - ADTG files are 985 B (`k.adtg`) and 1005 B (`k-pending.adtg`). The XML file is 3430 B.
  - Reopening both from file (`adCmdFile`, client, batch) keeps `Status`=2 on row 1, `Value`="pending", `OriginalValue`="alpha", `RecordCount`=4 and `ActiveConnection` null. Both reopened files read back identical value tags: I4, BSTR, R8, CY, DATE, UI1 array and Null.
  - `Provider="MSPersist"` gives `Provider()`="MSPersist.1". `Recordset.Open(file, conn)` and `Connection.Execute(file)` both return 4 rows.
  - The Decimal column `N` returns VT_DECIMAL with scale 0, sign 0 and lo64 12884901888 (=3·2^32) for a1. It returns 0 for b3 and b4. ADO reports `N` as Precision 38, NumericScale 0, DefinedSize 19.

*R-ACT-1*
- `DAO.DBEngine.120` returns `_DBEngine` {00000021…} from ACEDAO.DLL (registered "apartment"), `Version`="16.0" (`act-dao120`).
- ADODB classes (`act-ado-classes`): `Connection` is Apartment (`_Connection` {00001550…}, `Version` "10.0"); `Recordset` is Both ({00001556…}); `Command` is Both ({986761E8-7269-4890-AA65-AD7C03697A6D}); `Stream` is Apartment ({00001565…}). Type library ADODB 6.1, 120 types (`ado-describe`).
- Two `DAO.DBEngine.120` creations return the same object identity label (`u1`), and `Workspaces` is shared. After `e1.CreateWorkspace` and `Append`, both report Count=2. An `OpenDatabase` error 3024 (0x800A0BD0) appears in both `Errors` collections (`act-two-engines`).

**Probe or fixture defects**
- `create-ado`, `create-ado2` and `create-ado3` lost tables to DAO DDL refusals. Evidence: 0x800A0CDC "Syntax error in field definition." (DECIMAL; `Note` column) followed by 0x800A0C78 "Could not find output table 'K'."; `create-ado3` also gets 0x800A0BC3 on `rels.Append`. Every `ado-*` record from those generations is invalid for table content. `create-ado4` succeeds (`N` via `CreateField`, `[Note]` bracketed).
- `ado-open-provider-property`: reading `Properties` on a fresh Connection pins MSDASQL, so the probe never inventories ACE. Its `Open` failure is ODBC, not ACE, and the probe is order-confounded.
- `ado-execute-results`, `ado-rs-cursors`, `ado-view`, `ado-params` and `ado-session` set `cn.Mode`=1 (read), and their UPDATE and LockType results are confounded. `ado-execute-results` returns 0x80004005 "Operation must use an updateable query." for UPDATE.
- The `ado-params` Refresh-then-Append sequence double-counts parameters, so its `?_1` failure says nothing about named mapping.
- `ado-bulk` `GetRows` stores only array bounds ("not yet captured element-wise"). `show` also does not render the type-library `type` event of `ado-describe` (JSON only).
- `ado-execute-results` and `ado-params` leave `r4`/`r5`/`r6` unbound after an error, and the dependent steps are skipped (notes "Target … is not bound").
- Fixture-state sharing is not controlled in `ado-view`/`ado-edit`: `ado-edit` read back `n6` as ID 5 on a mutated copy.
- The `ado-provider-missing-names` probe is defined but has no retained record.
- In `str-memo-chunks`, `FieldSize` is refused on snapshots. Whether that is a snapshot limit or a probe-choice limit is unknown.

**Revisit**
- ADO events are not probed at all: `ado-async` has no sink, and ADO-EVENT, ADO-REENTRY and ADO-PARAM-EVENT are uncovered. `ado-async` also records no elapsed time.
- Uncovered ADO branches: ADO-DISCONNECT, ADO-RECORD, ADO-RESYNC, ADO-BATCH-SCOPE, ADO-PROPS wrapper-after-Close/Refresh, and ADO-PERSIST ADTG byte parsing against MS-ADTG and malformed or truncated files. ADO-BOOK and ADO-SCHEMA restriction positions are only partly covered.
- ADO-CURSOR must be re-run with a writable Mode before effective Lock/Supports can be trusted.
- The decimal `lo64=3·2^32` result needs a byte-level or overlay check against the ADO and capture layouts.
- R-STRING-1 has no allocation, pin or timing measurements, no inbound odd-byte BSTR, no ByRef, no SAFEARRAY or Decimal boundary, and no large-value allocation-failure case. ACCDB covers named/retained, revisit, memo, edit and schema probes; MDB has only the ordinal pass.
- R-ACT-1: ACT-THREAD, ACT-CLSID, ACT-FACTORY, ACT-AGGREGATE, ACT-RETAIN, ACT-UNLOAD, ACT-XLL and ACT-MANAGED have no records, and there are no CalcDb-side comparisons. Only a same-STA `act-two-engines` (the ENGINE-IDENTITY seed) exists.

### r-fmt-2-pres-1

Basis: 52 retained records, one attempt (`a1`) each. All R-FMT-2 probes ran on `world-accdb` (one generation). Every R-PRESERVE-1 probe has two records; only the `pres4-accdb` ones are used, and the `pres3` ones are stale (see Defects). Typed readbacks, HRESULT/EXCEPINFO and file size/hash per step are read. Page-level byte images are not in `show` output and were not interpreted.

**Observed: R-FMT-2**

- **Boolean / Null**
  - `bool-ladder`: `UPDATE T SET Y = True` (ID 2) and `= False` (ID 1) read back -1 and 0.
  - `bool-ladder`: `UPDATE T SET Y = Null` (ID 3, was -1) returned success and read back `VT_BOOL 0`. An insert omitting Y stored `VT_BOOL 0` for row 50.
  - `var-null-empty-ladder`: `UPDATE T SET N = ''` succeeded and read back `BSTR ""`. No step was refused. Only the end state is read back, so V's intermediate states are known from file hashes only.
- **Scalar boundaries** (`scalar-admission`, `Sc` via recordset `Value`)
  - Refused with `0x800A0D5D` "Data type conversion error": Byte 256 and -1; Integer 32768; Currency string "922337203685477.5808"; GUID unbraced string; GUID as 16-byte array.
  - Read back as written: Byte 255; Integer -32768 and 32767; Long min; Single 1E-45, NaN, Infinity; Double -0, 5E-324, NaN, Infinity; Currency 922337203685477.5807.
  - Currency strings "0.00005" → 0 and "0.00015" → 0.0002.
  - Dates -1.5, -1, -0.5, 0 and 1.500000001 read back unchanged; 2958466 accepted.
  - Braced GUID string read back as `"{guid {00112233-4455-6677-8899-AABBCCDDEEFF}}"`.
  - Refused rows still ended up as rows with only ID set (`AddNew` then `Update`).
  - All three Decimal (`Fm`) writes were refused with `0x800A0EAF` "Scaling of decimal value resulted in data truncation" ("12345678901234.5678", "-0.0001", `VT_CY 1.5`).
- **Extended date/time** (`scalar-admission`, `Fx` read back as `VT_BSTR`)
  - "0001-01-01 00:00:00.0000001" → `"01/01/0001 00:00:00.0000001"`; "9999-12-31 23:59:59.9999999" → `"12/31/9999 23:59:59.9999999"`.
  - "2024-01-02 03:04:05.12345675" (8 digits): `Value` returned hr 0 but the row read back `VT_NULL`.
- **Text grammar** (`text-grammar`, recordset)
  - Read back unchanged: "wide €", "mixed ab€cd", the surrogate pair (𝄞), embedded NUL ("a\0b", bytes confirmed), leading U+FEFF (bytes `ef bb bf` confirmed), and 50 narrow or 50 wide characters.
  - 51 narrow characters refused with `0x800A0C5B` "field is too small".
  - The two memos (4000 chars, one with € at each end) read back non-null.
  - Lone high/low surrogate rows read back as `VT_BSTR` with no text in `show`; the units are not verified.
  - `alt-drop-ladder`: SQL `UPDATE Alt SET A = 'a-longer-value-than-before'` (25 chars, `A TEXT(20)`) was not refused and read back `"a-longer-value-than-"` (20 chars).
- **Historical row coordinates** (`alt-drop-ladder`, `alt-append-ladder`)
  - After dropping B and D, old rows read back as ID, A, C with values intact; a new row 99 (`'new'`, 777) was added.
  - After adding E, F and G and moving E to `OrdinalPosition` 0, old rows read back `NULL, ID, A, B, C, D, NULL, BOOL 0`. The appended Yes/No column reads False, not Null, for old rows.
- **Failed / generated** (`failed-ladder`, `dbFailOnError`)
  - Duplicate unique key refused with `0x800A0BCE`; validation with `0x800A0CF4` "P must be positive"; Null in NOT NULL with `0x800A0CF2` "You must enter a value in the 'Fv.Q' field".
  - Valid insert omitting P read back P = 1.
  - File image: `before` → `after-duplicate-unique` changed (same 503808 B). The following two refusals left the hash identical to it.
- **Transactions** (`txn-ladder`)
  - After `CommitTrans`: `T.B` of ID 1 reads 111. The `Rollback` and the nested inner-commit/outer-rollback steps left the hash equal to `after-commit`, and rows 2 and 3 unchanged.
- **Overflow / deletion**
  - `grow-ladder`: Dense `LEN(C)` row 3: 1 → 20, row 4: 1 → 255. File size stayed 503808 B in both steps. `mdb-grow-relocate`: row 4 1 → 255, size stayed 413696 B.
  - `del-reuse-ladder`: after `DELETE T ID 20`, T no longer lists ID 20 and DistinctCount (`PK_T`, `UX_T_A`) is 40 → 41 after the reinsert (ID 500). G went 5 → 1 rows after delete-all plus one insert. Sizes were constant at 503808 B for all four steps.
- **Map growth and file size** (file size per step; no page analysis)
  - `ins-data-ladder` (G, 520-byte rows): +4096 B at insert 102 and +4096 B at insert 105. Inserts 101, 103, 104 and 106 added no size.
  - `sl-ladder`: size unchanged to 255 rows, +4096 B at 256 rows, unchanged at 257. Count reads 257. The row delete and the reinsert changed the hash but not the size.
  - `ix-append-ladder`: +8192 B at insert 603; the earlier four inserts (599–602) added none. `ix-low-ladder`: `append-599-602` 0 growth; first single insert after it (-1) +8192 B.
  - `ix-append-ladder` and `ix-low-ladder` read DistinctCount of `PK_IxL` equal to row count: 604 and 607.
  - `idx-key-ladder`: `IX_Ix_W` DistinctCount 30 → 33 (low, mid, high keys new; duplicate "k100" not counted); `PK_Ix` 30 → 34.
  - `upd-indexed-value`: `IX_T_N` 36, `PK_T` 40, `UX_T_A` 40 before and after the key replacements.
- **Short root** (`shortroot-append-text`, `shortroot-grow-old`)
  - `ALTER TABLE Bo ADD COLUMN Tx` changed the hash, not the size. Old row 31 then took a 100-char Tx (`"ttt…"`); all other rows read Null, and the three Boolean columns of old rows are unchanged. Size stayed 503808 B in both probes.
- **LV form** (typed values read back; `FieldSize` for memos is 2 × char count, for OLE exact bytes)
  - `lv-ole-small-ladder`: 63–66 B inserts read back; no size change.
  - `lv-ole-page-ladder`: 4075 +8192 B, 4076 +8192, 4077 +8192, 4080 +8192, 4081 +8192, 9000 +12288.
  - `mdb-lv-ole-page-ladder`: +4096, +8192, +8192, +8192, +8192, +12288 in the same order.
  - `lv-memo-ladder`: memo 62 chars = FieldSize 124, 64 = 128, 66 = 132; 2040 × "€" = 4080; 4078 = 8156; 4082 = 8164. Sizes: first memo +4096 B, next two 0; 4078 +12288; 4082 +12288; wide +8192.
  - `lv-transition-ladder`: grow 50→5000 +8192, shrink 5000→50 0, grow →9000 +4096, shrink 9000→5000 0. Typed values read back correct.
  - `lv-chain-ladder`: same-size replace +20480 B; grow to 13000 +8192; shrink to 100 and set Null 0. Row 7 reads Null/Null at the end.
  - `lv-delete-reuse-ladder`: file size constant (503808 B) through delete row 12, insert 100 B, delete chain row 5, insert 9000 B; rows 20 and 22 read back.

**Observed: R-PRESERVE-1 (pres4, ACE DAO catalog view before vs. after)**

- `pres-noop`: read-write open/close with no statement: hash and size identical to `before` (548864 B), and the two views are identical.
- `pres-row-edit`, `pres-grow-relocate`: the hash changes; the whole DAO catalog view is identical before and after, including every `LastUpdated`.
- `pres-lv-delete-retire`: only `Lv` RecordCount 13 → 12 changes. `pres-ins-pressure`: only `Dense` RecordCount 14 → 414; size 548864 → 1368064 B.
- `pres-lv-replace-chain`: size +12288 B; view identical.
- `pres-property-ladder`: size constant; each of the four edits is visible in the view, no other property changes, and `T.LastUpdated` is unchanged. QueryDef `QSel` and `QParam` (over `T`) list `T`'s `CalcDbUnknown` and `Description` in their own `Properties`, and show the new value; `QAct` and `QJoin` do not.
- `pres-catalog-ladder`: `CREATE INDEX` +4096 B. Only `G.LastUpdated` changed (add/drop column); `T.LastUpdated` did not. `CREATE TABLE NewT` gave +12288 B, `TableDefs` 21 → 22, a `Containers` document `NewT`, and `MSysACEs` RecordCount 78 → 80, `MSysObjects` 39 → 40.
- `pres-rename-referenced`: `Relation.Table` changes `Parent` → `Parent2` (`FK_Child_Parent`; `ForeignTable` stays `Child`). The `Name`, `LastUpdated` and `Containers` document names of `Parent2` and `T2` change. Saved-query SQL and QueryDef dates are unchanged: `QJoin` still `FROM Parent INNER JOIN Child`; `QSel`, `QParam`, `QAct` still reference `T`. `QSel` and `QParam` `Properties.Count` 16 → 14. `tdfs.Item("T")` → `0x800A0CC1` after the rename.
- `pres-txn-failed-ladder`: `Rollback` leaves the hash equal to `before`. The FK-violation insert (`0x800A0C81`, "related record is required in table 'Parent'") and the unique-key insert (`0x800A0BCE`) each changed the hash, size constant; the view is unchanged.
- `pres-compact`: `CompactDatabase` leaves the source hash unchanged. The compacted file is 548864 B (same size, different hash). Differences in the public view: `Database.Name`, and later `LastUpdated` for `Child` and `Parent` and their `Containers` documents; all else equal.
- All `pres-*` views: `SELECT … MSysObjects`, `MSysRelationships`, `MSysQueries` refused with `0x800A0C28` "no read permission". `Connection` (`0x800A0CB3`), `ConflictTable` and `ReplicaFilter` (`0x800A0BD8`) values are refused too; that is why each record is ReturnedFailure.

**Probe or fixture defects**

- `create-pres-accdb`, `-pres2-`, `-pres3-`: `Note` unbracketed, `0x800A0CDC`; `INSERT INTO Child` `0x800A0C3E`; pres3 also `rels.Append` `0x800A0BC3` (listed by `fixtures --failed-only`). The pres3 `pres-*` records (`pres-catalog-ladder.8691d34a88`, `-compact.f6b1949a7e`, `-grow-relocate.e1fb501d03`, `-ins-pressure.658120f10c`, `-lv-delete-retire.300837de11`, `-lv-replace-chain.a42b9ffb92`, `-noop.3939c33a88`, `-property-ladder.02a9fe217c`, `-rename-referenced.6930dd6892`, `-row-edit.ad4cd234ef`, `-txn-failed-ladder.a373618570`) are superseded and not used.
- `create-world-mdb`: `ALTER TABLE Sc ADD COLUMN Fx DATETIME2` refused with `0x800A0BB9` (so `Sc` in the MDB fixture lacks `Fx`). No retained MDB probe uses `Sc`.
- `Fm` Decimal is appended with `CreateField(name, 20)` without Precision or Scale (`tools/CalcDb.Probes/Cards/RFmt2Preserve1.cs`, "The Decimal scalar" block); the stored precision/scale is not read. The refusals of all three Decimal writes therefore do not map the card's precision/scale endpoints.
- `scalar-admission` labels date 2958466 "out-of-range", but it is accepted as the upper endpoint; the overflow side is not probed.
- `var-null-empty-ladder` description says one attempt is refused; none was.
- ReturnedFailure on the `pres-*`, `scalar-admission`, `text-grammar` and `failed-ladder` records comes from ACE refusals the probes ask for or from read-refusal in the catalog view. Probes still counted as failed by status.

**Revisit**

- Interpretation of page images is outstanding; this section states file size and hash only. The byte-level questions (FMT-SLOTS256 slot count at 255/256, LV forms at 63/64/65 and 4075–4081, short-root repacking, DistinctCount word, +8192 B at 603rd index entry) need the inspector.
- Not probed: calculated wrappers; AutoNumber/GUID generation, reseed, CancelUpdate; LV malformed/stream; map-declaration/ownership transitions; II-05 deletion, high keys and tails, aliases, retirement, statistics under rollback or failed Update; row-mask boundaries (Alt has 5 columns); NUMERIC bytes and sign forms; negative-zero Decimal; Fx scale/precision reread.
- Not probed: unreadable `MSys*` via SQL means no catalog-row comparison, only DAO projections. No Access-created forms, reports, macros, modules; unknown properties are DAO-created. No repair-marked Compact & Repair copy, no Name AutoCorrect.
- Open: why did the first failed insert (`failed-ladder`) change the hash while the next two did not, and why did both failed inserts in `pres-txn-failed-ladder` do so? Not yet explained by this card's captures.
- Single attempt per probe; hashes not shown to be stable under rerun.

### r-oledb-odbc
Basis: the newest (v2) record of each probe (39 probes with v2 records, plus `act-describe-companion-libraries` and `odbc-path-free-connections`, which have one record only). All answers are from ADO 10.0 (msado15) over `Microsoft.ACE.OLEDB.12.0`, ACE 16.0.20527.20196, on .NET 11 x64 STA. Probes run through ADO only. No ODBC connection succeeded.

**Observed**
- **OLE-ACT:**
  - `act-provider-identity`: the loaded module is `ACEOLEDB.DLL`, with `mso.dll`, `ACECORE.DLL` and `ACEWSTR.DLL`. `ACEES.DLL` and `ACEERR.DLL` appear by `after-close` (`act-describe-adodb`).
  - After Open, the provider reports Provider Name `MSJETOLEDB40.DLL`, version `04.00.8015`, friendly name "Microsoft OLE DB Provider for Jet", DBMS `MS Jet` `04.00.0000`, OLE DB Version `02.10`.
  - Before Open those named properties do not exist (`0x800A0CC1`).
  - Active Sessions stays 128 with one or two connections open, and after one closes.
  - `act-services-variants`: OLE DB Services -1, -2, -4, -5 and 0 all open, and client static and server keyset Recordsets both give RecordCount 5. For -5 and 0 the property "OLE DB Services" does not exist after Open.
  - `act-describe-companion-libraries`: ADOX.Catalog creates (`msadox.dll`); `JRO.JetEngine` fails with `0x80040154`.
- **OLE-PROP:**
  - `prop-connection-inventory`: 31 connection properties before Open, 101 after. Engine Type is 6 for ACCDB and 5 for MDB. After Open: Isolation Levels 4096, Isolation Retention 9, Transaction DDL 16, Multiple Results 0.
  - `prop-modes`, default Mode is 16:
    - Mode 1 opens, and UPDATE fails `0x80004005` "Operation must use an updateable query." (SQLState 3073, native -198839259).
    - Modes 3, 16 and 19 open and UPDATE works.
    - Modes 2, 5, 11 and 15 fail Open with `0x80040E21`.
    - A second Mode 19 connection opens alongside each.
  - `prop-jet-options`:
    - Locking Mode 1/0, Global Partial Bulk Ops 2 and Encrypt Database True are accepted and read back.
    - `Jet OLEDB:Transaction Commit Mode=1` in the connection string fails "Could not find installable ISAM."
    - Engine Type=5 on the ACCDB reads back 6.
    - Database Password `wrongpw` opens with State 1.
    - An absent System database path is accepted.
  - `prop-rowset-requests` (request → effective): forward-only/read-only → 0/1; keyset/optimistic → 1/3; dynamic/pessimistic → keyset/pessimistic; forward-only/batch-optimistic → keyset/4; forward-only/pessimistic → keyset/2; client static/batch → 3/4. CmdTableDirect keyset opens. Connection Errors count is 0 for all eight.
  - `prop-command-set-execute`:
    - Setting Maximum Rows (attr 513), Immobile Rows or Literal Bookmarks fails `0x80040E21` "Read-only property value was not set."
    - Bookmarkable=True makes the cursor static (type 3); Others' Changes Visible=True makes it keyset. Both persist on the Command.
    - All 10 Executes fetch 32 rows.
    - An unknown property name gives `0x800A0CC1`.
- **OLE-ACCESS:** `access-prepared-deferred`: with Prepared False or True, `SELEC bad` fails at Execute with `0x80040E14`, and a missing table with `0x80040E37`. Parameters.Count reads 0 without error for the missing table and 1 for `?`. Execute of `?` fails `0x80040E10` "Parameter ?_1 has no default value." Re-execution after a text change works.
- **OLE-BUFFER:** `buffer-typed-server` and `-client`:
  - ADO types: ID 3, Code/Txt 202, Dt 7, M 203 (size 536870910), B 205 (1073741823), Bin 204 (16), Y 11, Sh 2, Bt 17, Sg 4, G 72, N 131 (precision 38, scale 0).
  - Server cursor: Byte reads `VT_I2`, memo `ActualSize` 24000 for 12000 characters, Bin is zero-padded to 16 bytes, GUID reads as a braced string.
  - Client cursor: Byte reads `VT_UI1`. Only the column-property sets differ (BASECATALOGNAME, BASESCHEMANAME, KEYCOLUMN, RELATIONCONDITIONS, CALCULATIONINFO, OPTIMIZE added; ISCASESENSITIVE and COLLATINGSEQUENCE dropped).
  - Empty binary reads `VT_NULL`, empty text `""`.
  - `schema-columns`: ORDINAL_POSITION and COLUMN_FLAGS are declared type 19 but delivered as `VT_R8`. Items DATA_TYPE: B and Bin 128, Code/Txt/M 130, N 131. ACCDB and MDB Items rows are equal; the compressed `Cmp` columns carry the same flags (106/234).
- **OLE-FETCH:**
  - `fetch-batches`, forward-only: CacheSize 1 and -1 accepted, 0/2/3/40 refused `0x800A0BB9`; GetRows(0) → `0x800A0C93`; EOF read → `0x80040E24` "Rowset does not support fetching backward."
  - `fetch-batches`, keyset: CacheSize 2, 3, 40 and -1 accepted, 0 refused. GetRows(1) → AbsolutePosition 2, GetRows(2) → 4, GetRows(100) → EOF True with position -3; RecordCount 32.
  - `fetch-moves`, forward-only: MovePrevious → `0x800A0C93`; Move(-1) and MoveLast → `0x80040E24`; MoveFirst after three MoveNext returns ID 1.
  - `fetch-moves`, keyset: Move(5) → ID 7, Move(100) → EOF with position -3, MovePrevious from EOF → ID 32, MovePrevious at first row → BOF with position -2, a further MovePrevious → `0x800A0BCD`.
- **OLE-ROWLIFE:** `rowlife-clone-delete-requery`:
  - After Delete, Status 262144 and reading the field gives `0x80040E23`.
  - Resync gives `0x800A0CB3`.
  - After Requery the retained Field gives `0x800A0D5C` "Object is no longer valid."
  - The clone keeps reading ID 4 after the original Recordset closes.
  - `stream-record-from-row`: `Record.Open(Recordset)` gives `0x800A0CB3`; Stream.Open on the closed Record gives `0x800A0E78`; Record.Open on the connection gives `0x800A0E7D`.
- **OLE-STREAM:**
  - `stream-getchunk`: forward-only 3/3/3/1 then `VT_NULL`; then Field.Value → `0x8000FFFF`. Client static gives the same chunks and a Value of 10 bytes. GetChunk 0 or -1 → `0x800A0BB9`; 65536 returns all 12000 bytes.
  - `stream-appendchunk`:
    - 3+4 bytes store 7 bytes.
    - AppendChunk(2) onto an existing 10-byte value stores 2 bytes (replace, not append).
    - AppendChunk on Text(50) → `0x800A0C93`.
    - A cancelled AddNew still consumes an AutoNumber (ID 7 missing).
    - AddNew+AppendChunk then MoveNext without Update stores the row.
    - An empty AppendChunk stores `VT_NULL`.
- **OLE-PARAM:**
  - `param-derived`: stored queries via adCmdStoredProc show Parameters.Count 0, and Refresh gives 0.
  - Text `?` markers derive as Name `?`, Type 202, Size 510, input. `PARAMETERS pX Long, pY Text(5)` derives pX Type 3 Size 4 and pY Type 202 Size 255.
  - `param-order-binding`: stored-query parameters set by name fail `0x800A0CC1`, and Execute fails `0x80040E10` "Too few parameters. Expected 5." (SQLState 3061).
  - Binding of `?` markers is by position. NamedParameters=True does not change that.
  - In the `PARAMETERS` form, appending pA before pB fails `0x80004005` "Unspecified error"; appending in declaration order runs.
  - `param-types-insert`: ic0 (typical values) and ic1 (all Null but Code) execute. "abc" or a 60-character text fails at CreateParameter (`0x800A0D5D`); Double 3e9 fails at CreateParameter (`0x8002000A` "Out of present range.").
  - `param-edge`: Output, InputOutput and ReturnValue directions on a `?` command fail `0x80040E21`.
- **OLE-UPDATE:**
  - `update-errors`:
    - "abc" for a Long fails at field assignment with `0x80020005`; 3e9 fails with `0x8002000A`.
    - A text that is too long fails at assignment with `0x80040E21` (SQLState 3163).
    - Unique conflict fails at Update, Status 4096 (SQLState 3022).
    - Delete of a referenced row fails (3200).
    - Missing required value gives Status 131072 (3314), and the CHECK gives 131072 (3316).
  - `update-optimistic-conflict`:
    - A stale Update fails `0x80040E21` (SQLState 3197, Status 16).
    - After CancelUpdate the Value reads `B-value`.
    - On the deleted row the field assignment fails `0x80004005` (3197), and a later read gives `0x80040E23` "Record is deleted." (3167).
  - `update-pessimistic-lock`: B's UPDATE of rows 1 and 3 while A edits row 1 fails `0x80004005` "Could not update; currently locked." (SQLState 3218, native -72156238). B's SELECT still reads `alpha`. B's UPDATE of row 1 succeeds after A's Update. B's UPDATE of row 2 fails while A edits row 2 and succeeds after CancelUpdate. A's Errors count is 0.
  - `update-others-visible`: B's UPDATE/INSERT/DELETE all returned success, yet A's four Recordsets (keyset, static, dynamic, client static) show the old values and 5 rows, also after Requery.
- **OLE-TXN:**
  - `txn-held-rows`:
    - BeginTrans returns 1.
    - Held keyset, forward-only and client static Recordsets stay State 1 and readable across commit and rollback.
    - Preserve on Commit is True for all three; Preserve on Abort is False for the two server Recordsets and True for client static.
    - With Attributes 393216, a transaction stays open after commit or rollback, and Close fails `0x800A0CAE`.
    - The property named "Isolation Level" does not exist.
  - `txn-ddl-rollback`: CREATE TABLE, INSERT and ALTER TABLE all roll back (field count 17→16; later SELECT → `0x80040E37`). Commit keeps them.
  - `txn-two-process`: a second process's UPDATE of rows 1 and 3 fails `0x80004005` "Could not update; currently locked." (SQLState 3218) while an uncommitted transaction exists. Its reads show neither A's update nor A's new row, and still show `alpha` after A commits.
- **OLE-ERROR:**
  - `error-record-channels`: each failure leaves exactly one Errors entry, never accumulating, with SQLState, native error and help context set. `SELECT NoSuch FROM Items` gives `0x80040E10` "No value given for one or more required parameters." with SQLState "". `SELECT 1/0` succeeds.
  - `error-retained-lifetime`: a retained Error keeps its Number, Description and SQLState after Clear, Command release, Close and collection release.
  - `error-open-failures`: an absent file gives `0x80004005` (SQLState 3024). No Data Source gives `0x80040E4D` "Authentication failed." (source "Provider"). Excel/Text extended properties give SQLState 3274 and 3044. User ID/Password gives `0x80040E4D` (3028). Setting Mode 99 gives `0x800A0C93`, and Open then uses 16.
  - `error-ado-origin`: ADO-raised errors leave Errors count 0; a provider error gives count 1.
  - `schema-keys-indexes`: ConstraintTableUsage (7) → `0x800A0CB3`.
  - `schema-procedures-views`: ProcedureParameters, ProcedureColumns, ViewColumnUsage and ViewTableUsage → `0x800A0CB3`.
  - `schema-others`: every other schema value tried except 6 and 19 → `0x800A0CB3`; 9999 and -1 → `0x800A0BB9`.
  - `schema-tables`: QParam, QMulti and QAction are procedures only; QSel and QEmpty are views and procedure-less.
- **OLE-MANAGED:** not covered.
- **ODBC-LOAD (only connection attempts):**
  - `odbc-path-free-connections`: all 7 MSDASQL/ODBC attempts fail.
  - No Dbq gives `0x80040E4E` "Operation was canceled."
  - Unknown driver name, `(*.mdb)`-only driver name and unknown DSN give `0x80004005` "[ODBC Driver Manager] Data source name not found…".
  - A nonexistent Dbq gives "'(unknown)' is not a valid path".
  - `ACEODBC.DLL` 16.0.20527.20196 loads after the first attempt.
  - Errors count is 0 in every case.
- All other ODBC arms: no records.

**Probe or fixture defects**
- `create-v2-*`: `Items.N` (type 20) refuses "3.125" and "-0.0001" (`0x80020009` / `0x800A0EAF`), so N is Null in IDs 1 and 4. v1 records lost table `Child` (`0x80040E37` in `access-ordinals`) and are not used.
- `access-ordinals`: `Code AS Dup, Code AS Dup` is refused `0x80040E14`, so all 16 ordinal arms (o1–o16) never ran.
- `param-derived`, `param-order-binding` (a), `param-edge`: stored queries expose no parameters, so the QMulti/QParam/QAction arms only show "Too few parameters". The Null/'a1'/'b3' and QAction-effect arms are unexercised.
- `param-order-binding` (c): the two values match no row (b3 has L=0), so an empty result cannot discriminate bindings.
- `param-types-insert`: ic2–ic4 fail in `CreateParameter`, so provider-side conversion is untested. Decimal values print as bare `VT_DECIMAL`.
- `fetch-batches`: the GetRows SAFEARRAY is "not yet captured element-wise", so rows returned per call are unknown.
- `stream-getchunk`: the interleaved B/M loop returns 4096 bytes and about 5000 characters four times. The B pattern repeats every 256 bytes, so the offset is not shown.
- `error-open-failures`: Mode 15 (exclusive) is refused even for a lone connection, so the first-exclusive/second-shared conflict is untested.
- `schema-others`: restrictions were passed as scalars (`VT_I4 0`, `"Items"`) and refused `0x800A0CB3`. ADO documents Criteria as an array, so restriction handling is untested.
- `update-others-visible`: no independent reader checks that B's writes reached the file.
- `act-services-variants`: five services run in one process, so module counts only grow.
- Module identification covers only 17–24 of 154–164 modules, so `odbc32`/`msdasql` are not identified. `act-describe-adodb` renders no type-library inventory in `show` (its JSON is about 1.7 MB and was not read).
- `odbc-path-free-connections`: attempt o1 has no Dbq and returned "Operation was canceled." The record does not say whether a driver prompt appeared. Check against the private-desktop rule.

**Revisit**
- Why B still reads `alpha` after A commits in `txn-two-process`, and why A sees nothing after Requery in `update-others-visible`. Both need an independent reader or a fresh connection.
- Capture the GetRows array contents; make the long-value chunk loop distinguishable (non-periodic data).
- Rerun the stored-query parameter arms with parameters set from the card text, the QMulti name-order arm, and the Mode-15 exclusive conflict.
- Retry schema restrictions as Variant arrays; the DECIMAL fixture value needs a form ACE accepts.
- Uncovered: OLE-MANAGED (Framework and .NET 10 clients), raw IAccessor/DBBINDING and 48-byte buffer layouts, HROW ownership, DBTYPE_IUNKNOWN callbacks, multi-set stride, raw IErrorRecords, aggregation.
- Uncovered: every ODBC arm past connection attempts (ABI, state, descriptors, arrays, GetData, need-data, transactions, cancel, diagnostics, clients). Needs a Dbq path and a native consumer; no ODBC connection has succeeded.

### r-cplx-rebuild

Based on the newest fixture generation: `rb-rich2-*`, and `cx-*` as created. The `rb-rich-*` runs (`compact-rich.5c48684077`, `options-*.35e7b9a59f`/`.5d659a295e`/`.66d4807485`, `compact-counters.61bfd63dd1`, `publish-*.811bd87497`/`.f4eff7bcaf`/`.0463aad159`, `repair-clean-control.da5d3ae332`, `negative-open-and-compact.011d613735`) are superseded and not used. All "same"/"differs" comparisons below are on enumerated public state, not on bytes.

**Observed**

*CX-DESC*
- `read-cx-mv-text`: `Tags` Field2 is Type 109, Size 4, Attributes 1, `IsComplex` -1, 24 properties. Its child recordset has one field `Value`: Type 10, Size 255, SourceTable `T.Tags`. `MSysComplexColumns` `TableDef.RecordCount` is 1.
- All fixture reads (`read-cx-*`, `desc-rename-drop`, `compact-complex-fixtures`): `SELECT` on `MSysComplexColumns` or `MSysObjects` returns 0x800A0C28 "no read permission". No association rows were captured.
- `read-cx-attach`: `Att` is Type 101. The child fields are `FileData` 11, `FileFlags` 4, `FileName` 10, `FileTimeStamp` 8, `FileType` 10 and `FileURL` 12, all with SourceTable `T.Att`.
- `desc-rename-drop`: all accepted, with the group still readable under the new names:
  - rename `Tags`→`Tags2`
  - rename table `T`→`T2`
  - append MV Long `Extra`
  - `ALTER TABLE … DROP COLUMN` on `Extra`, then on `Tags2` (which held data)
  - `DROP TABLE`

*CX-READ*
- `read-cx-mv-text`:
  - The child recordset is Type 2 with `RecordCount` 1 before `MoveLast` and 2 after. After `MoveLast`, `AbsolutePosition` is 1.
  - `ParentRecordset().Name` is the parent's SQL text.
  - Group order is alpha, beta. p3 and the filler rows give `RecordCount` 0 with BOF and EOF true. p5 (deleted) gives 0x800A0BCD.
- `read-cx-sql-surface`:
  - `SELECT Tags` returns VT_DISPATCH, Type 109.
  - `Tags.Value` joins p1 as beta, then alpha. p3 gives Null.
  - `Tags.Value = 'alpha'` matches p1 and p2.
  - `GROUP BY Tags.Value`: Null 94, alpha 2, beta 1, same 1, six 1.
  - `Att.FileFlags`, `FileTimeStamp` and `FileURL` are all VT_NULL.
  - `COUNT(Att.FileName)` is 5.
- `read-cx-ado`:
  - The `Tags` column is Type 203 with value "alpha;beta", Attributes 234. Binding `Field.Value` as a child recordset fails (no object).
  - `Tags.Value` rows are Type 202, and p1 again gives beta then alpha.
  - The `Att` column is Type 203, "att-page.txt;att-small.txt".
  - ADO join order for p2 is small then page. DAO join order is page then small.
- `read-cx-attach`:
  - `FileData` through DAO is a wrapper: `14000000 01000000 04000000 "txt\0"` (UTF-16, 20 bytes) followed by the content.
  - `FieldSize` is 25 for "hello" and 20020 for the 20,000-byte file. `SaveToFile` writes 5 B and 20000 B (the unwrapped content).
  - The second DWORD is 1 in all four wrappers captured: p1, p2 (both) and p4 (the noise file).
- `read-cx-types`: only `MByte` has a complex field. Type 102, Size 4. Children are Type 2 `VT_UI1` 1 and 255. p2 gives 0 rows.

*CX-HOLD*
- `hold-surfaces-parent-movement`, `hold-parent-requery-delete-close`:
  - After a parent `MoveNext`, `Requery`, `Delete` or INSERT, the held child and its clone fail on `RecordCount`, BOF, EOF, Bookmark and `Close` with 0x800A0D5C "Object invalid or no longer set". `Fields()` still returns an object, but `Count` on it fails with the same code.
  - `c1.ParentRecordset` fails with 0x800A0C93.
  - Re-reading `ft.Value()` gives a fresh child: p2 has 1 row, p3 has 0 rows with Bookmark 0x800A0BCD, and p1 again has "alpha" with Bookmark `00030000`.
  - The held child stays invalid after the parent moves back to p1.
- `hold-parent-requery-delete-close`: parent `Delete` of p1 (with a held child) succeeded. The recreated p1 has 0 children.
- `hold-release-orders`:
  - (b) Close of the parent and Database, then the held child: `RecordCount`, Bookmark and `Close` give 0x800A0D5C.
  - (c) Releasing Database and parent with no `Close`: the held child stays readable (count 1, "alpha").
  - (d) Closing child, parent, then Database: no failures.

*CX-EDIT*
- `edit-child-admission`:
  - Child `AddNew` or `Edit` without a parent `Edit` gives 0x800A0F0C. The following `Value`, `Update` and `CancelUpdate` give 0x800A0BCC.
  - Under parent `Edit`, a 300-char value (put 0x800A0C5B), a Null value, a `Byte()` value (put 0x800A0D5D) and no value each end with `Update` 0x800A0BF2 (3058).
  - After parent `CancelUpdate`, the held child gives 0x800A0D5C.
- `edit-parent-update-vs-cancel`: child `Update` under parent `Edit` succeeds (count 3). The held child is invalid after the parent `Update` or `CancelUpdate`. p1 then holds "new-update" (3 rows). p2 does not hold "new-cancel" (1 row).
- `edit-child-edit-delete`: child `Edit`, `Update` and `Delete` under parent `Edit` succeed (result: "changed", 1 row). With no parent `Edit`, child `Edit` gives 0x800A0F0C, but child `Delete` succeeds (p4 group → 0 rows).
- `edit-parent-addnew-counter`: `AddNew` reads ID 100, 101 and 102 for p7, p8 and p9. The `CancelUpdate`d p8 consumed 101. Saved IDs are 100 and 102. A child `AddNew`/`Update` under the unsaved parent persisted ("n1").
- `create-cx-mv-text`, `att-load-variants`: a duplicate multivalue value is refused with 0x800A0EEC. The case-only name pairs `ext.TXT` and `ext.ZIP` were refused (the second of each pair) while `ext.zip` (same bytes) was accepted. `ext.JPG` was refused as well (it has the same name as `ext.jpg` apart from case).

*CX-ATT*
- `att-load-variants`:
  - Empty file gives a 20-byte wrapper only.
  - File with no extension gives wrapper `0e000000 01000000 01000000 0000` (14 B), `FileType` "".
  - The 9,000-byte patterned content gives `FileData` of 9020 B, with `FileFlags` Null.
  - Read order is not load order.
- `att-replace-remove`:
  - `LoadFromFile` plus `FileName`="renamed.dat" gives `FileData` of 5020 B. Its wrapper extension is still "bin", while `FileType` is "dat".
  - Assigning raw `FileData` bytes (`010203`) gives 0x800A0BB9. The row still saves with `FileData` Null and `FileName` "bytes.bin".
  - Deleting both p2 attachments under parent `Edit` works (0 rows left).
- `att-file-failures`:
  - `SaveToFile` onto an existing file gives 0x800A0EFF (3839). That file is unchanged.
  - `LoadFromFile` of a missing file gives 0x800A0BD0 (3024).
  - `SaveToFile` on the child of an empty group gives 0x800A0BCD (3021).

*CX-CALC*
- `create-cx-calc`: appending `DateAdd("d",1,[D])`, `CCur([A])*1.1` or `Now()` is refused with 0x800A0F13. Seven calculated fields were created.
- `calc-source-data-change`:
  - A held Field read the new `C_long` (200) right after `Execute UPDATE`, without `Requery`.
  - `UPDATE`/`INSERT` that overflow are admitted: A=2147483647, D=9999-12-31, A=0.
  - Reading then gives `C_long` 0x800A0006 "Overflow" and `C_div` 0x800A000B "Division by zero". With A=0, `C_long` is 0.
  - Null A gives `C_text` "-x" and `C_iif` "null".
- `calc-schema-change`:
  - Setting `Field.Expression` on an existing calculated field gives 0x800A0C77.
  - Renaming A→A2 is accepted. The dependants keep "[A]…". `SELECT *` then fails with 0x800A0F2B.
  - `ALTER COLUMN B` then fails with 0x800A0ED7. `DROP COLUMN D` and `DROP COLUMN C_text` succeed.
- `calc-admission`, query side only: `Eval` and `NoSuchFunction` give 0x800A0C0D. `[X]+1` gives 0x800A0C1F. `CLng('abc')` gives 0x800A0D88 at the value read. `DLookup` returns VT_BSTR "10".

*CX-HISTORY*
- `history-append-cancel-rollback`: on an ordinary Memo (`AppendOnly` False at start), `Edit`/`Update` gives "r1", `CancelUpdate` leaves it, and `Rollback` leaves it. `CommitTrans` gives "t3".
- `history-toggle-delete`: setting `AppendOnly` True is accepted and reads back -1. After that, `UPDATE`, `DELETE` of ID 2 and re-`INSERT` (new ID 4) all succeed.

*RB-CLEAN / RB-RELOC / RB-OPAQUE*
- `compact-rich`, `compact-fmt1-features`, `compact-bloat-repeated`:
  - Compaction output is the same size as the source (ACCDB 315392 B, MDB 221184 B; all `r-fmt-1` fixtures except one).
  - Rows of Parent, Child, W and the user-table enumeration are identical to the source.
  - The only property differences are `LastUpdated` on two tables.
  - One exception: fixture `a-wide` grew from 196608 B to 294912 B.
  - Bloat: ACCDB 4444160 B→368640 B, MDB 4349952 B→274432 B. Sizes were stable over generations 1 to 3. Content stayed N=40, MaxId=40, L=80000, Keep=3.
- `compact-counters`: source `Child` (MAX 50) inserts at ID 65 (`@@IDENTITY` and MAX). The compacted copy inserts at 51, in both ACCDB and MDB.
- `compact-complex-fixtures`: same-format compaction of `cx-mv-text`, `cx-types`, `cx-attach`, `cx-calc` and `cx-history` succeeded with the same size and the same group, row and export results (`export-attsame.bin` 5 B, hash prefix 2cf24dba5fb0).

*RB-OPTIONS*
- `compact-complex-fixtures`: to Jet4 (`dbVersion40`), `cx-mv-text`, `cx-types`, `cx-attach` and `cx-calc` give 0x800A0EEA "uses attachments or multi-valued lookup fields". `cx-calc` has no such fields. `cx-history` succeeds (98304 B).
- `options-version`:
  - ACCDB to 128, 256 and 64 gives Version 12.0, 14.0 and 4.0.
  - ACCDB→64 drops `MSysComplexColumns` and has `MSysACEs` 28 vs 48.
  - MDB→64 is identical to the source. MDB→128 gives Version 12.0 and adds `MSysCompactError` (4 fields, 1 row) and `MSysComplexColumns`.
  - MDB→256 gives 0x800A0F4A "collating sequence not supported with the specified file format".
- `options-collation`:
  - Outputs give `CollatingOrder` 1033, 1034 and 1053.
  - Swedish orders ä, ö after z. The Spanish output is identical to General.
- `options-encrypt-password`:
  - ACCDB with `dbEncrypt` gives 0x800A0BB9 (3001).
  - `;pwd=` in DstLocale produces a file that opens only with the password (else 0x800A0BD7 "Not a valid password"), with Version 14.0.
  - Compacting it back with the password only as SrcLocale gives an output that still fails to open without a password.
  - MDB `dbEncrypt` and `dbDecrypt` succeed. All opened outputs have the same rows.

*RB-PUBLISH*
- `publish-existing-and-aliases`: an existing destination, destination equal to the source, and an existing alias give 3204 "Database already exists". The source and existing files are unchanged. The `\\?\` source gives 3043 "network access was interrupted". Upper-case, slash and dot spellings of the source work.
- `publish-held-inproc`: source held by a shared Database gives 3704. Held exclusive, exclusive read-only, or by a native handle (read/write or read) gives 3045. No output file in any case. A destination held by a Database gives 3204.
- `publish-held-xproc` (participant `b` holds, `a` compacts): ACCDB shared read/write and shared read-only holders give 3704. ACCDB exclusive holders give 3045. All four MDB states give 3045. `b`'s own opens succeed.

*RB-NEGATIVE*
- `negative-open-and-compact`: a missing source gives 3024. A text file or an empty file gives 3343, for both open and compact. A missing or wrong password gives 3031. An invalid locale gives 3228, with no output. Undefined option bit 1024 gave no error and produced an output file.
- `DBEngine.Errors` is not cleared on success (stale 3343, 3228, 3204 and 3420 entries seen after successful calls).

*RB-REPAIR*
- `repair-clean-control`: `RepairDatabase` on clean ACCDB and MDB returns 0x800A0CB3 (3251). The files are unchanged.

**Probe or fixture defects**
- `create-cx-types`: only `MByte` received `V`. For the other seven kinds, `TableDefs.Item("M<Kind>")` returned 0x800A0CC1 right after the DDL, consistent with a stale TableDefs collection (no `Refresh`). The same defect is in `calc-admission` (`N1`–`N16`, so no calculated column was ever appended; table-side admission is only `N0` = `Now()`).
- `create-cx-history`: `CreateProperty("AppendOnly")` gave 0x800A0D27 (already exists), so the fixture never had `AppendOnly` True.
- Every `ColumnHistory(...)` read gives 0x800A0C0D (not a function in this SQL). No history content was read.
- The `MSysCompactErrors` queries use the plural name. The table that exists (MDB→128 output) is `MSysCompactError`, so its rows were never read.
- `edit-workspace-transactions`: `Rollback` and `CommitTrans` after `BeginTrans` gave 0x800A0BDA, and the held `c2` was invalidated earlier. The "tx-commit" step is not exercised. The same `Workspaces(0)` rolled back correctly in `history-append-cancel-rollback`.
- `att-replace-remove`: p1's delete used a child invalidated by the earlier parent `Update` (0x800A0D5C). That deletion did not happen.
- `att-file-failures`: the last two steps (database file as `LoadFromFile` source and as `SaveToFile` destination) hit 0x800A0D5C on a stale handle.
- `options-collation`: the strings (`ch`, `ll`) do not separate Spanish traditional from General.
- `compact-fmt1-features` and the rich enumerations: `TableDef.RecordCount` and `Indexes` on system tables give 0x800A0BD8 and 0x800A0C26 (enumeration noise).

**Revisit**
- Complex-column association evidence (`MSysComplexColumns` rows) is unreadable through DAO SQL. It needs R-FMT-1 raw reads.
- Retry the `cx-types` fixture with a TableDefs refresh. Redo the table-side `calc-admission` matrix.
- Reproduce the history probes with an `AppendOnly`-True fixture and a working history reader.
- Resolve what `Rollback` and `CommitTrans` do to child edits.
- Redo the `att-replace-remove` p1 delete and the two `att-file-failures` steps with re-acquired objects.
- Open: whether 0x800A0EEA for `cx-calc` is calculated-field related.
- Uncovered:
  - CX-FAIL and RB-FAIL (resource and I/O failure).
  - RB-REPAIR on damaged files.
  - RB-OPAQUE (stored front-end objects) and the Access Compact & Repair lane.
  - Compressed attachments, since every wrapper captured has the second DWORD 1.
  - Page and relocation tracking (no byte-level evidence in this card).

### r-lock-perm

Scope: 10 records (5 `accdb-*`, 5 `mdb-*`), one fixture generation, all attempt `a1`. `fixtures --failed-only` lists none for this card. Environment: ACEDAO.DLL 16.0.20527.20196, x64, .NET 11 STA. Every call below is `DBEngine.OpenDatabase(path, Options=Exclusive, ReadOnly)`. "Shared rw" is (0,0), "shared ro" is (0,-1), "excl rw" is (-1,0), "excl ro" is (-1,-1). The table has one keyed table `T` and the probe reads `SELECT COUNT(*) FROM T` = 2048. ACCDB and MDB gave the same answers in every probe, with `.laccdb` and `.ldb` as the lock file. Several probes end as `ReturnedFailure`, which is ACE error answers, not harness failure.

**Observed**

*EX-PERM / RO-PERM: database file read-only attribute, no lock file* (`*-db-readonly-all-modes`)
- Shared rw: error 3051 (0x800A0BEB) "…cannot open or write to the file… already opened exclusively by another user, or you need permission to view and write its data". No lock file appears.
- Shared ro: opens. `Updatable()` is False and the count is 2048. A 64-byte lock file (sha 1f4a82a98356) exists while open and is gone after Close.
- Excl rw: 3051, same as shared rw.
- Excl ro: opens, `Updatable()` False, count 2048. No lock file appears at any point.
- Each Errors collection read after a successful open still shows 3051, left over from the earlier failed open.

*Read-only attribute on both database and existing 64-byte lock file* (`*-db-and-lockfile-readonly`)
- Shared rw: 3051.
- Shared ro: 3050 (0x800A0BEA) "Could not lock file." This differs from the database-only case, where shared ro opened.
- The lock file stays 64 bytes with its pre-open hash f5a5fd42d16a (harness-written) throughout.
- Only the two shared modes were probed (`RLockPerm.cs:41`).

*Read-only attribute on the lock file only, database writable* (`*-readonly-lockfile-all-modes`)
- Shared rw: 3051. Shared ro: 3050.
- Excl rw: opens, `Updatable()` -1. The database file is not readable by the harness during the open (IOException 0x80070020).
- Excl ro: opens, `Updatable()` False.
- The lock file keeps hash f5a5fd42d16a through every open and after Close.
- Errors still shows 3050 after both successful exclusive opens, left over from the shared-ro failure.

*Stale lock file, 64 bytes harness-written, writable* (`*-stale-lockfile-all-modes`)
- All four modes open: shared rw, shared ro, excl rw and excl ro all return a Database. This is the only `ReturnedSuccess` record.
- The lock file's content hash changes from f5a5fd42d16a to 1f4a82a98356 during the shared opens, and the file is absent after each Close.
- With the exclusive opens the lock file is absent: it was gone before the excl rw open and was not recreated.

*RO-PAIR: both readers read-only attribute, in two processes* (`*-readonly-db-two-readers`)
- A opens shared ro and holds a recordset; the lock file is 64 bytes. B opens shared ro, reads 2048 and `Updatable()` is False. The lock file is 128 bytes (sha 98f5debeb353) and stays 128 bytes after B closes, while A is still open.
- While A is open, B's shared rw returns 3051.
- While A is open, B's excl ro opens, reads 2048 and `Updatable()` is False. B's Errors then still shows 3051, left over from the shared-rw failure.
- A reads 2048 again after B is done. After A closes, the lock file is absent.
- No exclusive read/write attempt from B. No third reader.

**Probe or fixture defects**
- In `*-db-and-lockfile-readonly`, the last `after-close` lock-file capture failed with IOException 0x80070050 "artifacts\lockfile_after-close_… already exists" (step 40, both formats). This is an artifact-name collision, not an ACE answer, so the final lock-file state of those two records is unread. This is a harness defect, since the identical `after-close` key is written twice. Evidence: the `files[after-close]` lines at steps 38 and 40.
- In `*-db-and-lockfile-readonly` the excl rw and excl ro modes were never probed (`Modes.Where(m => !m.Exclusive)`, `RLockPerm.cs:41`). The "database and lock file both read-only" row is therefore incomplete.
- `files[open-excl-rw]` in the lock-file and stale probes shows "unread: IOException 0x80070020". That is the harness's own read being denied while ACE holds the file, not a defect.
- Errors-collection reads after a successful call return the earlier failure's entry (3050 or 3051). Any "error after success" in these records is stale carry-over, not an answer to the call it follows.

**Revisit**
- Excl ro admitted beside an existing shared-ro reader (`*-readonly-db-two-readers`). The book says an incompatible exclusive request must still be refused (RO-PAIR), so this contradicts R-LOCK-2's expectation for that mode.
- The database is not tested with attribute read-only plus an ACL or read-only/non-creatable folder. Both the read-only-folder and absent-lock-file-with-folder-denied cases are uncovered, so creation/deletion requirements (RO-PERM) are only partly answered.
- No writer participant. RO-LATE (writer denied through reader handoff, order both ways), RO-REOPEN and RO-FAIL are not run. All opens come from ACE/DAO. The record holds no native access/share of the handles, no CalcDb probe lane, no Access UI or ADO lane. R-LOCK-1 EX-OPEN (both orders, probe), EX-DEFAULT, EX-CREATE, EX-ALIAS, EX-PROBE, EX-RACE, EX-PUBLIC, EX-AUTH, EX-MAP, EX-LIFE and EX-RANGE are not covered.
- ACE's 64-byte and 128-byte lock-file contents are retained only as hashes; the raw owner fields are not interpreted. The relation of the 3050 versus 3051 split to ACE's lock-file access needs a native-access observation (W-PROBE) before any claim.
- Why a read-only lock file makes shared ro fail (3050) but not the exclusive modes is not established from these records alone.

### r-ddl-limits

One generation only (`a1`); there are no ado/ado2/… generations on this card. 20 records, 10 statements × {ACCDB, MDB}. Each statement runs by `db.Execute` through DAO.DBEngine.120 (ACEDAO 16.0.20527.20196) on a fresh `CreateDatabase`, which already holds P(ID LONG PK) and P2(ID LONG PK, PID LONG). Each record then lists TableDefs, their fields and types, and `Relations.Count`. Every record is capture=Complete.

**Observed**

CAT-D / SQL-ACT-DDL: refusals (`Execute` hr=0x80020009, `DBEngine.Errors` Count 1, Source "DAO.Database")
- Unbracketed field name `Note` is refused on both formats: scode 0x800A0CDC, error 3292 "Syntax error in field definition." (`accdb-note-column`, `mdb-note-column`). Table C is absent afterwards.
- `[Note]` in the same position succeeds on both formats, Errors Count 0. The field is listed as "Note", Type 10 (`accdb-note-bracketed`, `mdb-note-bracketed`).
- `V DECIMAL(18,4)` and bare `V DECIMAL` are both refused on both formats, with the same 0x800A0CDC / 3292 (`accdb-decimal`, `accdb-decimal-plain`, `mdb-decimal`, `mdb-decimal-plain`). Table C is absent afterwards.
- `T TEXT(255) WITH COMPRESSION` is refused on both formats: scode 0x800A0CDA, error 3290 "Syntax error in CREATE TABLE statement." (`accdb-compression`, `mdb-compression`). Table C is absent afterwards.
- In all refused cases TableDefs lists only the system tables plus P and P2, and `Relations.Count` is 0.

CAT-D: relationships
- Column-level `PID LONG CONSTRAINT FK_C REFERENCES P (ID)` succeeds on both formats: Errors Count 0, `Relations.Count` 1 (`accdb-fk-column-level`, `mdb-fk-column-level`). C has ID/PID (Type 4) and Txt (Type 10).
- Table-level `CONSTRAINT FK_C FOREIGN KEY (PID) REFERENCES P (ID)` in CREATE TABLE succeeds on both formats: Errors Count 0, `Relations.Count` 1, same fields (`accdb-fk-table-level`, `mdb-fk-table-level`).
- `ALTER TABLE P2 ADD CONSTRAINT FK_P2 FOREIGN KEY (PID) REFERENCES P (ID)` succeeds on both formats: Errors Count 0, `Relations.Count` 1 (`accdb-fk-alter`, `mdb-fk-alter`).
- So `REFERENCES` is not refused. Only column-level and table-level FK in CREATE TABLE and ALTER ADD CONSTRAINT were exercised.

SQL-ACT-DDL: accepted controls
- `plain`: `ID LONG PK, PID LONG, Txt TEXT(20)` succeeds on both formats. Txt has Type 10 (`accdb-plain`, `mdb-plain`).
- Column-level `Code TEXT(10) CONSTRAINT UX_C UNIQUE` succeeds on both formats, with Code as Type 10 (`accdb-unique-column-level`, `mdb-unique-column-level`).

File and catalog observations
- Final TableDefs counts (accdb / mdb): refused cases 7 / 6, successful CREATE TABLE cases 8 / 7, `fk-alter` 7 / 6. Per count, ACCDB has one more system table than MDB (MSysComplexColumns is listed in ACCDB). I filtered the MSys rows out of my extract, so this is from the counts and one record's listing, not a full check of every record.
- `scratch.*` sizes at close, ACCDB: refused cases 196608 B, `plain` 208896, `note-bracketed` 208896, `unique-column-level` 212992, `fk-alter` 204800, FK in CREATE TABLE 217088.
- `scratch.*` sizes at close, MDB: refused cases 102400 B, `plain` 114688, `note-bracketed` 114688, `unique-column-level` 118784, `fk-alter` 110592, FK in CREATE TABLE 122880.

**Probe or fixture defects**
- None found. Every statement ran on its own fresh database, so no refusal masked another. The error-3290 / 3292 refusals are answers to the statements, not fixture failures.
- The probe reads `DBEngine.Errors` after `Execute` and lists fields only by Name and Type. It does not read Field attributes, so Decimal (Type 20) and text-compression state could not be seen even where creation succeeded.

**Revisit**
- Not inspected: the relationship catalog object, `Relation.Attributes`, MSysRelationships rows, reciprocal indexes, and physical-tree ownership (the CAT-D requirement). The probes list only `Relations.Count`; the retained `scratch.*` files may hold the rest.
- Uncovered CAT-D branches: duplicate names, invalid definitions, cascades, SetNull, NoIndex, multi-column and self-references, existing violating rows, rename and drop with held readers, transactions and rollback.
- Uncovered SQL-ACT-DDL branches: ALTER COLUMN/DROP, CREATE/DROP INDEX, CHECK, VIEW/PROCEDURE, and TEMPORARY.
- Untested: whether DECIMAL or a column named `Note` can be created by another route (`CreateField` / `CreateTableDef`, ADO/OLEDB). That DDL refusal was only observed through DAO `Execute`.
- Only the exact tokens tried are covered: `Note`, `DECIMAL` and `DECIMAL(18,4)`, and `WITH COMPRESSION` on TEXT(255). Other reserved words, other DECIMAL forms and other compression placements are untested.
- Not read: how the refusal message differs between the 3290 and 3292 cases beyond the text above.

### r-crash-requery

Five records, one generation (`a1`), all on fixture `r-cat-1-accdb-queries` (sha256 prefix `d53c7ee6e0f1`, 241664 B). Environment: DAO.DBEngine.120, ACEDAO.DLL 16.0.20527.20196, native-idispatch/x64, invoke LCID 1033. The three records for `Requery(QueryDef)` have no capture (`execution=Uncertain`, `capture=Incomplete`), so `calcdb-oracle show` prints only "record has no capture". I read their manifest and retention note from the JSON instead.

**Observed** (card branches R-ORACLE-1 / OR-REENTRY; R-CURSOR-1 CUR-LIFE Requery, the card-source ID given by the probe's `Refs`; R-CAT-1)

Setup common to all probes: `OpenDatabase(path, False, True)`. `QueryDefs.Item("QParam")`, then `Parameters.Item("pId")` and `Item("pName")`. `pId.Value = VT_I4 1` and `pName.Value = VT_BSTR "zzz"`. `qd.OpenRecordset(VT_I4 2)` returns a `Recordset2` (dynaset).

- **Read to EOF, no Requery** (`open-read`, `ReturnedSuccess`, `Capture=Complete`, exit 0). All calls returned `hr=0`:
  - The first `rs.EOF` is `VT_BOOL 0`.
  - `Fields.Count` is `VT_I2 2`.
  - `Field[0].Value` is `VT_I4 1` and `Field[1].Value` is `VT_BSTR "a"`.
  - After `MoveNext`, `rs.EOF` is `VT_BOOL -1`.
  - `eng.Errors.Count` is `VT_I2 0`.
  - `rs.Close` and `db.Close` return `hr=0`, and the file is unchanged before and after (same size and hash prefix).
- **`Recordset.Requery()` with no argument** (`requery-plain`, `ReturnedSuccess`, exit 0):
  - `rs.Requery()` returns `hr=0x00000000` as `VT_EMPTY`.
  - The second read is identical to the first: one row (`VT_I4 1`, `VT_BSTR "a"`), then `EOF=-1`.
  - `rs.Fields()` after Requery returns the same capture object id (u9) as before it. The two Field3 objects fetched afterwards are new ids (u12, u13).
  - `Errors.Count` is 0 and the file is unchanged.
- **`Requery(QueryDef)`** — three probes, one outcome:
  - `requery-same-qd`: `Requery(qd)` with the originating QueryDef. Last native call entered: `rs.Requery`, seq 26, path 14.
  - `requery-other-qd`: `Requery(qd2)` with a second `QueryDefs.Item("QParam")` instance whose pId is 2. Last call entered: `rs.Requery`, seq 30, path 18.
  - `param-then-requery-qd`: pId set to 2 on the same QueryDef, then `Requery(qd)`. Last call entered: `rs.Requery`, seq 27, path 15.
  - In all three, the worker exited with `0xC0000005` (`exitCode -1073741819`, `timedOut=false`, `killed=false`, `exitObserved=true`) and no receipt was returned. No HRESULT or EXCEPINFO exists for this call in any record.
  - Their retention notes say a scratch check through the DAO PIA (vtable) got `RPC_E_SERVERFAULT` from the same call. That check is not a retained record.

**Probe or fixture defects**

- None found. The fixture has no failed creation calls: all five inputs are the same R-CAT-1 accdb, and all rows and `QParam` lookups succeeded.
- The `Requery(QueryDef)` probes lack the observation that matters for the card. They have no capture, and the crash gives no ACE error answer or result for the parameter-value, new-QueryDef or population questions.
- In `requery-other-qd`, `pName` on the second QueryDef is not set; only `pId` is. This is by design per its description, but it confounds that probe: the three probes crash identically, so it cannot be separated from the others here.
- Each record ends with `modules[deliberate-truncation] complete=False ... buffer smaller than module list`. It looks like a deliberate recorder check; I did not verify it against the probe code.
- `eng.Errors.Count` was read only in the two successful runs. It is unobserved in the crashing runs.

**Revisit**

- Whether `Requery(QueryDef)` crashes only through IDispatch or also through other callers (PIA vtable, ADO). The `RPC_E_SERVERFAULT` observation sits only in the retention note.
- Which argument forms of `Requery` are safe: none beyond the no-argument form was observed to return.
- The R-CURSOR-1 CUR-LIFE items still have no record:
  - bookmark validity, child-object identity, parameter values and current position after Requery;
  - the effect of changing a parameter before a no-argument `Requery()`. Only the QueryDef-argument form was probed with changed parameters, and it crashed;
  - `LastModified` before and after Requery, population, and clones.
- The `Requery()` read is one row: the same row as before, with pId = 1 and pName = "zzz". Whether `Requery()` re-evaluates parameters is unobserved; no probe changes pId before a no-argument `Requery()`.
- The card source says CUR-LIFE should compare Requery on table/dynaset/snapshot/forward-only. Only dynaset (`OpenRecordset(2)`) was run.
- Whether the crash seen here is the same one that originally ended the `r-cat-1/parameter-values` worker: the probe docs say so, but I checked no other card's record.
- No file-state or lock effects were retained for the crashing runs: no after-crash file state, and the `rs`/`db` objects were never closed there.

