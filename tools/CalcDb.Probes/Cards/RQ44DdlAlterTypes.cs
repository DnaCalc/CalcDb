namespace CalcDb.Probes.Cards;

/// <summary>
/// Wave-2/3 ready-gate probes (r-q44-ddl-alter-types):
/// (A) <c>ALTER TABLE</c> (<c>ADD COLUMN</c>, <c>ALTER COLUMN</c>, <c>DROP COLUMN</c>, <c>DROP CONSTRAINT</c>,
/// plus <c>DROP INDEX</c> comparator) on empty and populated ACCDB and Jet 4 MDB tables across DAO and ADO
/// (<c>NotModelled.DdlAlterTable</c>, 25 wide stops);
/// (B) <c>BIGINT</c>/<c>LARGEINT</c> and <c>DATETIME2</c> DDL (<c>CREATE TABLE</c> and <c>ALTER TABLE ADD COLUMN</c>)
/// on empty ACCDB (format code 2 -&gt; 5/6 and <c>MSysObjects.LvProp</c>) and Jet 4 MDB (expected 3001 refusal)
/// across DAO and ADO (<c>NotModelled.DdlBigIntOrDateTime2</c>, 17 wide stops);
/// (C) <c>CREATE [UNIQUE] INDEX</c> on <c>Double</c> (<c>0x07</c>), <c>DateTime</c> (<c>0x08</c>),
/// <c>GUID</c> (<c>0x0F</c>), and <c>Memo</c> (<c>0x0C</c>) on empty and populated ACCDB and Jet 4 MDB tables
/// (<c>NotModelled.DdlIndexKeyType</c>, 10 wide stops).
/// Every probe keeps ALL file pages (<c>nativeRead keep:true</c>) before and after every DDL statement and after <c>Close</c>.
/// </summary>
public sealed class RQ44DdlAlterTypes : ICard
{
    private const string Card = "r-q44-ddl-alter-types";
    private const string EmptyAccdb = "r-cat-1-accdb-empty";
    private const string EmptyMdb = "r-cat-1-mdb-empty";
    private const string AceProvider = "Microsoft.ACE.OLEDB.12.0";
    private const int FullReadBytes = 524288;

    private static readonly string[] AlterRefs = ["W-07", "II-04", "II-06", "II-11", "R-CAT-1", "R-FMT-2"];
    private static readonly string[] ExtTypeRefs = ["W-07", "II-04", "II-06", "II-11", "R-FMT-1", "R-CAT-1"];
    private static readonly string[] IndexTypeRefs = ["W-07", "II-04", "II-05", "II-06", "II-11", "R-SORT-3", "R-CURSOR-1"];

    public IEnumerable<Probe> Probes()
    {
        foreach (Probe p in AlterAddColumnProbes()) yield return p;
        foreach (Probe p in AlterColumnProbes()) yield return p;
        foreach (Probe p in AlterDropColumnProbes()) yield return p;
        foreach (Probe p in AlterDropConstraintProbes()) yield return p;
        foreach (Probe p in ExtTypeDdlProbes()) yield return p;
        foreach (Probe p in IndexKeyTypeProbes()) yield return p;
    }

    // ------------------------------------------------------------------------------------------------
    // Part A1: ALTER TABLE ADD COLUMN

    private static IEnumerable<Probe> AlterAddColumnProbes()
    {
        yield return DaoSteps(
            "add-long-empty-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: ALTER TABLE T ADD COLUMN B LONG on empty table with PK. Keeps all pages before and after.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, A LONG)"],
            ["ALTER TABLE T ADD COLUMN B LONG"]);

        yield return AdoSteps(
            "add-long-empty-accdb-ado",
            EmptyAccdb,
            AlterRefs,
            "ACCDB ADO: ALTER TABLE T ADD COLUMN B LONG on empty table with PK. Keeps all pages before and after.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, A LONG)"],
            ["ALTER TABLE T ADD COLUMN B LONG"]);

        yield return DaoSteps(
            "add-long-empty-mdb-dao",
            EmptyMdb,
            AlterRefs,
            "Jet 4 MDB DAO: ALTER TABLE T ADD COLUMN B LONG on empty table with PK. Keeps all pages before and after.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, A LONG)"],
            ["ALTER TABLE T ADD COLUMN B LONG"]);

        yield return DaoSteps(
            "add-text-notnull-empty-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: ALTER TABLE T ADD COLUMN S TEXT(20) NOT NULL on empty table with PK (variable column + LvProp Required). Keeps all pages before and after.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, A LONG)"],
            ["ALTER TABLE T ADD COLUMN S TEXT(20) NOT NULL"]);

        yield return DaoSteps(
            "add-memo-empty-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: ALTER TABLE T ADD COLUMN M MEMO on empty table with PK (allocates LVAL usage maps). Keeps all pages before and after.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, A LONG)"],
            ["ALTER TABLE T ADD COLUMN M MEMO"]);

        yield return DaoSteps(
            "add-cols-rows-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: populated T (3 rows including Null S), ADD COLUMN B LONG, ADD COLUMN T2 TEXT(10), then INSERT row 4 and read all rows. Keeps all pages after each step.",
            [
                "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, A LONG, S TEXT(20))",
                "INSERT INTO T VALUES (1, 10, 'one')",
                "INSERT INTO T VALUES (2, 20, NULL)",
                "INSERT INTO T VALUES (3, NULL, 'three')",
            ],
            [
                "ALTER TABLE T ADD COLUMN B LONG",
                "ALTER TABLE T ADD COLUMN T2 TEXT(10)",
                "INSERT INTO T (ID, A, S, B, T2) VALUES (4, 40, 'four', 400, 't4')",
            ],
            readBackTable: "T");

        yield return DaoSteps(
            "add-cols-rows-mdb-dao",
            EmptyMdb,
            AlterRefs,
            "Jet 4 MDB DAO: populated T (3 rows including Null S), ADD COLUMN B LONG, ADD COLUMN T2 TEXT(10), then INSERT row 4 and read all rows. Keeps all pages after each step.",
            [
                "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, A LONG, S TEXT(20))",
                "INSERT INTO T VALUES (1, 10, 'one')",
                "INSERT INTO T VALUES (2, 20, NULL)",
                "INSERT INTO T VALUES (3, NULL, 'three')",
            ],
            [
                "ALTER TABLE T ADD COLUMN B LONG",
                "ALTER TABLE T ADD COLUMN T2 TEXT(10)",
                "INSERT INTO T (ID, A, S, B, T2) VALUES (4, 40, 'four', 400, 't4')",
            ],
            readBackTable: "T");

        yield return DaoSteps(
            "add-notnull-rows-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: populated T (2 rows), ALTER TABLE T ADD COLUMN N LONG NOT NULL, then read all rows. Keeps all pages before and after.",
            [
                "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, A LONG)",
                "INSERT INTO T VALUES (1, 10)",
                "INSERT INTO T VALUES (2, 20)",
            ],
            ["ALTER TABLE T ADD COLUMN N LONG NOT NULL"],
            readBackTable: "T");
    }

    // ------------------------------------------------------------------------------------------------
    // Part A2: ALTER TABLE ALTER COLUMN

    private static IEnumerable<Probe> AlterColumnProbes()
    {
        yield return DaoSteps(
            "alt-text-widen-narrow-empty-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: empty T (S TEXT(10)), ALTER COLUMN S TEXT(30) then ALTER COLUMN S TEXT(5). Keeps all pages after each step.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, S TEXT(10))"],
            [
                "ALTER TABLE T ALTER COLUMN S TEXT(30)",
                "ALTER TABLE T ALTER COLUMN S TEXT(5)",
            ]);

        yield return DaoSteps(
            "alt-text-widen-narrow-rows-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: populated T (S TEXT(10) with 'abc' and '1234567890'), ALTER COLUMN S TEXT(30) then ALTER COLUMN S TEXT(5), then read all rows. Keeps all pages after each step.",
            [
                "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, S TEXT(10))",
                "INSERT INTO T VALUES (1, 'abc')",
                "INSERT INTO T VALUES (2, '1234567890')",
                "INSERT INTO T VALUES (3, NULL)",
            ],
            [
                "ALTER TABLE T ALTER COLUMN S TEXT(30)",
                "ALTER TABLE T ALTER COLUMN S TEXT(5)",
            ],
            readBackTable: "T");

        yield return DaoSteps(
            "alt-long-double-text-empty-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: empty T (V LONG), ALTER COLUMN V DOUBLE (fixed->fixed) then ALTER COLUMN V TEXT(15) (fixed->var). Keeps all pages after each step.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)"],
            [
                "ALTER TABLE T ALTER COLUMN V DOUBLE",
                "ALTER TABLE T ALTER COLUMN V TEXT(15)",
            ]);

        yield return DaoSteps(
            "alt-long-double-text-rows-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: populated T (V LONG with 10, NULL, -5), ALTER COLUMN V DOUBLE, ALTER COLUMN V TEXT(15), INSERT row 4, then read all rows. Keeps all pages after each step.",
            [
                "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)",
                "INSERT INTO T VALUES (1, 10)",
                "INSERT INTO T VALUES (2, NULL)",
                "INSERT INTO T VALUES (3, -5)",
            ],
            [
                "ALTER TABLE T ALTER COLUMN V DOUBLE",
                "ALTER TABLE T ALTER COLUMN V TEXT(15)",
                "INSERT INTO T (ID, V) VALUES (4, 'new')",
            ],
            readBackTable: "T");

        yield return DaoSteps(
            "alt-long-text-rows-mdb-dao",
            EmptyMdb,
            AlterRefs,
            "Jet 4 MDB DAO: populated T (V LONG with 10, NULL, -5), ALTER COLUMN V TEXT(15), INSERT row 4, then read all rows. Keeps all pages after each step.",
            [
                "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)",
                "INSERT INTO T VALUES (1, 10)",
                "INSERT INTO T VALUES (2, NULL)",
                "INSERT INTO T VALUES (3, -5)",
            ],
            [
                "ALTER TABLE T ALTER COLUMN V TEXT(15)",
                "INSERT INTO T (ID, V) VALUES (4, 'new')",
            ],
            readBackTable: "T");

        yield return DaoSteps(
            "alt-notnull-toggle-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: empty T (V LONG), ALTER COLUMN V LONG NOT NULL then ALTER COLUMN V LONG NULL (LvProp Required toggle). Keeps all pages after each step.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)"],
            [
                "ALTER TABLE T ALTER COLUMN V LONG NOT NULL",
                "ALTER TABLE T ALTER COLUMN V LONG NULL",
            ]);

        yield return AdoSteps(
            "alt-long-text-empty-accdb-ado",
            EmptyAccdb,
            AlterRefs,
            "ACCDB ADO: empty T (V LONG), ALTER COLUMN V TEXT(15). Keeps all pages before and after.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V LONG)"],
            ["ALTER TABLE T ALTER COLUMN V TEXT(15)"]);
    }

    // ------------------------------------------------------------------------------------------------
    // Part A3: ALTER TABLE DROP COLUMN

    private static IEnumerable<Probe> AlterDropColumnProbes()
    {
        yield return DaoSteps(
            "dropcol-unindexed-empty-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: empty T (ID PK, A LONG, B TEXT(20), C LONG), DROP COLUMN A (fixed) then DROP COLUMN B (variable). Keeps all pages after each step.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, A LONG, B TEXT(20), C LONG)"],
            [
                "ALTER TABLE T DROP COLUMN A",
                "ALTER TABLE T DROP COLUMN B",
            ]);

        yield return DaoSteps(
            "dropcol-unindexed-rows-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: populated T (ID PK, A LONG, B TEXT(20), C LONG; 3 rows), DROP COLUMN A, DROP COLUMN B, INSERT row 4, then read all rows. Keeps all pages after each step.",
            [
                "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, A LONG, B TEXT(20), C LONG)",
                "INSERT INTO T VALUES (1, 10, 'one', 100)",
                "INSERT INTO T VALUES (2, NULL, 'two', 200)",
                "INSERT INTO T VALUES (3, 30, NULL, 300)",
            ],
            [
                "ALTER TABLE T DROP COLUMN A",
                "ALTER TABLE T DROP COLUMN B",
                "INSERT INTO T (ID, C) VALUES (4, 400)",
            ],
            readBackTable: "T");

        yield return DaoSteps(
            "dropcol-unindexed-rows-mdb-dao",
            EmptyMdb,
            AlterRefs,
            "Jet 4 MDB DAO: populated T (ID PK, A LONG, B TEXT(20), C LONG; 3 rows), DROP COLUMN A, DROP COLUMN B, INSERT row 4, then read all rows. Keeps all pages after each step.",
            [
                "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, A LONG, B TEXT(20), C LONG)",
                "INSERT INTO T VALUES (1, 10, 'one', 100)",
                "INSERT INTO T VALUES (2, NULL, 'two', 200)",
                "INSERT INTO T VALUES (3, 30, NULL, 300)",
            ],
            [
                "ALTER TABLE T DROP COLUMN A",
                "ALTER TABLE T DROP COLUMN B",
                "INSERT INTO T (ID, C) VALUES (4, 400)",
            ],
            readBackTable: "T");

        yield return DaoSteps(
            "dropcol-memo-empty-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: empty T (ID PK, M MEMO, C LONG), DROP COLUMN M (LVAL column drop). Keeps all pages before and after.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, M MEMO, C LONG)"],
            ["ALTER TABLE T DROP COLUMN M"]);

        yield return DaoSteps(
            "dropcol-indexed-err-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: T with PK on ID and index IX_K on K; DROP COLUMN K and DROP COLUMN ID (expected 3280). Keeps all pages before and after to verify whether ACE writes any page on 3280.",
            [
                "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, K LONG, V LONG)",
                "CREATE INDEX IX_K ON T (K)",
            ],
            [
                "ALTER TABLE T DROP COLUMN K",
                "ALTER TABLE T DROP COLUMN ID",
            ]);

        yield return AdoSteps(
            "dropcol-unindexed-empty-accdb-ado",
            EmptyAccdb,
            AlterRefs,
            "ACCDB ADO: empty T (ID PK, A LONG, B TEXT(20), C LONG), DROP COLUMN A then DROP COLUMN B. Keeps all pages after each step.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, A LONG, B TEXT(20), C LONG)"],
            [
                "ALTER TABLE T DROP COLUMN A",
                "ALTER TABLE T DROP COLUMN B",
            ]);
    }

    // ------------------------------------------------------------------------------------------------
    // Part A4: ALTER TABLE DROP CONSTRAINT (+ DROP INDEX comparator)

    private static IEnumerable<Probe> AlterDropConstraintProbes()
    {
        yield return DaoSteps(
            "dropcon-pk-empty-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: empty T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG), ALTER TABLE T DROP CONSTRAINT PK_T. Keeps all pages before and after.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)"],
            ["ALTER TABLE T DROP CONSTRAINT PK_T"]);

        yield return DaoSteps(
            "dropcon-uq-rows-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: populated T (PK_T + UQ_K UNIQUE, 2 rows), ALTER TABLE T DROP CONSTRAINT UQ_K. Keeps all pages before and after.",
            [
                "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K LONG CONSTRAINT UQ_K UNIQUE)",
                "INSERT INTO T VALUES (1, 10)",
                "INSERT INTO T VALUES (2, 20)",
            ],
            ["ALTER TABLE T DROP CONSTRAINT UQ_K"]);

        yield return DaoSteps(
            "dropcon-uq-rows-mdb-dao",
            EmptyMdb,
            AlterRefs,
            "Jet 4 MDB DAO: populated T (PK_T + UQ_K UNIQUE, 2 rows), ALTER TABLE T DROP CONSTRAINT UQ_K. Keeps all pages before and after.",
            [
                "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K LONG CONSTRAINT UQ_K UNIQUE)",
                "INSERT INTO T VALUES (1, 10)",
                "INSERT INTO T VALUES (2, 20)",
            ],
            ["ALTER TABLE T DROP CONSTRAINT UQ_K"]);

        yield return AdoSteps(
            "dropcon-check-accdb-ado",
            EmptyAccdb,
            AlterRefs,
            "ACCDB ADO: T with CHECK constraint CK_V (V >= 0), ALTER TABLE T DROP CONSTRAINT CK_V (MSysObjects Type 9 + LvProp removal). Keeps all pages before and after.",
            [
                "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)",
                "ALTER TABLE T ADD CONSTRAINT CK_V CHECK (V >= 0)",
            ],
            ["ALTER TABLE T DROP CONSTRAINT CK_V"]);

        yield return AdoSteps(
            "dropcon-check-mdb-ado",
            EmptyMdb,
            AlterRefs,
            "Jet 4 MDB ADO: T with CHECK constraint CK_V (V >= 0), ALTER TABLE T DROP CONSTRAINT CK_V (MSysObjects Type 9 + LvProp removal). Keeps all pages before and after.",
            [
                "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG)",
                "ALTER TABLE T ADD CONSTRAINT CK_V CHECK (V >= 0)",
            ],
            ["ALTER TABLE T DROP CONSTRAINT CK_V"]);

        yield return DaoSteps(
            "dropcon-fk-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: populated parent P and child C with FK_C_P REFERENCES P (ID), ALTER TABLE C DROP CONSTRAINT FK_C_P (MSysObjects Type 8, MSysRelationships, parent .rB & child FK indexes). Keeps all pages before and after.",
            [
                "CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY)",
                "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG CONSTRAINT FK_C_P REFERENCES P (ID))",
                "INSERT INTO P VALUES (1)",
                "INSERT INTO C VALUES (10, 1)",
            ],
            ["ALTER TABLE C DROP CONSTRAINT FK_C_P"]);

        yield return DaoSteps(
            "dropcon-fk-mdb-dao",
            EmptyMdb,
            AlterRefs,
            "Jet 4 MDB DAO: populated parent P and child C with FK_C_P REFERENCES P (ID), ALTER TABLE C DROP CONSTRAINT FK_C_P. Keeps all pages before and after.",
            [
                "CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY)",
                "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG CONSTRAINT FK_C_P REFERENCES P (ID))",
                "INSERT INTO P VALUES (1)",
                "INSERT INTO C VALUES (10, 1)",
            ],
            ["ALTER TABLE C DROP CONSTRAINT FK_C_P"]);

        yield return DaoSteps(
            "dropcon-drop-index-accdb-dao",
            EmptyAccdb,
            AlterRefs,
            "ACCDB DAO: populated T (PK_T + non-unique IX_K, 2 rows), DROP INDEX IX_K ON T (comparator alongside DROP CONSTRAINT). Keeps all pages before and after.",
            [
                "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K LONG)",
                "CREATE INDEX IX_K ON T (K)",
                "INSERT INTO T VALUES (1, 10)",
                "INSERT INTO T VALUES (2, 10)",
            ],
            ["DROP INDEX IX_K ON T"]);
    }

    // ------------------------------------------------------------------------------------------------
    // Part B: BIGINT / LARGEINT and DATETIME2 DDL

    private static IEnumerable<Probe> ExtTypeDdlProbes()
    {
        yield return DaoSteps(
            "ext-bigint-create-accdb-dao",
            EmptyAccdb,
            ExtTypeRefs,
            "ACCDB DAO (empty format code 2): CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, B BIGINT). Keeps all pages before and after (page 0 header format code + MSysObjects.LvProp + TDEF).",
            [],
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, B BIGINT)"]);

        yield return AdoSteps(
            "ext-bigint-create-accdb-ado",
            EmptyAccdb,
            ExtTypeRefs,
            "ACCDB ADO (empty format code 2): CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, B BIGINT). Keeps all pages before and after.",
            [],
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, B BIGINT)"]);

        yield return DaoSteps(
            "ext-bigint-alter-accdb-dao",
            EmptyAccdb,
            ExtTypeRefs,
            "ACCDB DAO (empty format code 2): CREATE TABLE T (ID PK), then ALTER TABLE T ADD COLUMN B BIGINT. Keeps all pages after setup and after ALTER.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY)"],
            ["ALTER TABLE T ADD COLUMN B BIGINT"]);

        yield return DaoSteps(
            "ext-largeint-create-accdb-dao",
            EmptyAccdb,
            ExtTypeRefs,
            "ACCDB DAO (empty format code 2): CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, B LARGEINT) to check LARGEINT synonym vs syntax error. Keeps all pages before and after.",
            [],
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, B LARGEINT)"]);

        yield return DaoSteps(
            "ext-datetime2-create-accdb-dao",
            EmptyAccdb,
            ExtTypeRefs,
            "ACCDB DAO (empty format code 2): CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, D DATETIME2). Keeps all pages before and after (page 0 header format code + MSysObjects.LvProp + TDEF).",
            [],
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, D DATETIME2)"]);

        yield return AdoSteps(
            "ext-datetime2-create-accdb-ado",
            EmptyAccdb,
            ExtTypeRefs,
            "ACCDB ADO (empty format code 2): CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, D DATETIME2). Keeps all pages before and after.",
            [],
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, D DATETIME2)"]);

        yield return DaoSteps(
            "ext-datetime2-alter-accdb-dao",
            EmptyAccdb,
            ExtTypeRefs,
            "ACCDB DAO (empty format code 2): CREATE TABLE T (ID PK), then ALTER TABLE T ADD COLUMN D DATETIME2. Keeps all pages after setup and after ALTER.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY)"],
            ["ALTER TABLE T ADD COLUMN D DATETIME2"]);

        yield return DaoSteps(
            "ext-both-accdb-dao",
            EmptyAccdb,
            ExtTypeRefs,
            "ACCDB DAO (empty format code 2): CREATE TABLE T1 with BIGINT, then CREATE TABLE T2 with DATETIME2 (observes format code 2 -> 5 -> 6 and LvProp FCMin*Ver progression). Keeps all pages after each step.",
            [],
            [
                "CREATE TABLE T1 (ID LONG CONSTRAINT PK1 PRIMARY KEY, B BIGINT)",
                "CREATE TABLE T2 (ID LONG CONSTRAINT PK2 PRIMARY KEY, D DATETIME2)",
            ]);

        yield return DaoSteps(
            "ext-bigint-mdb-dao",
            EmptyMdb,
            ExtTypeRefs,
            "Jet 4 MDB DAO: CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, B BIGINT) (expected 3001 refusal). Keeps all pages before and after.",
            [],
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, B BIGINT)"]);

        yield return AdoSteps(
            "ext-bigint-mdb-ado",
            EmptyMdb,
            ExtTypeRefs,
            "Jet 4 MDB ADO: CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, B BIGINT) (expected refusal). Keeps all pages before and after.",
            [],
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, B BIGINT)"]);

        yield return DaoSteps(
            "ext-datetime2-mdb-dao",
            EmptyMdb,
            ExtTypeRefs,
            "Jet 4 MDB DAO: CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, D DATETIME2) (expected 3001 refusal). Keeps all pages before and after.",
            [],
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, D DATETIME2)"]);

        yield return AdoSteps(
            "ext-datetime2-mdb-ado",
            EmptyMdb,
            ExtTypeRefs,
            "Jet 4 MDB ADO: CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, D DATETIME2) (expected refusal). Keeps all pages before and after.",
            [],
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, D DATETIME2)"]);
    }

    // ------------------------------------------------------------------------------------------------
    // Part C: CREATE INDEX on Double (0x07), DateTime (0x08), GUID (0x0F), Memo (0x0C)

    private static IEnumerable<Probe> IndexKeyTypeProbes()
    {
        // Double (0x07)
        yield return DaoSteps(
            "ixtype-double-empty-accdb-dao",
            EmptyAccdb,
            IndexTypeRefs,
            "ACCDB DAO: empty T (ID PK, V DOUBLE), CREATE INDEX IX_V ON T (V) then CREATE UNIQUE INDEX UQ_V ON T (V DESC). Keeps all pages after each step.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V DOUBLE)"],
            [
                "CREATE INDEX IX_V ON T (V)",
                "CREATE UNIQUE INDEX UQ_V ON T (V DESC)",
            ]);

        yield return DaoRowsAndIndex(
            "ixtype-double-rows-accdb-dao",
            EmptyAccdb,
            "ACCDB DAO: populated T (V DOUBLE: -10.5, -0.0, 0.0, 3.25, NULL, 100.0), CREATE INDEX IX_V (ASC), CREATE INDEX IX_VD (DESC), then CREATE UNIQUE INDEX UQ_V (checks -0.0/0.0 uniqueness). Keeps all pages after each step.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V DOUBLE)",
            p =>
            {
                AddTwoColRow(p, "rs0", "V", 1, A.R8(-10.5));
                AddTwoColRow(p, "rs0", "V", 2, A.R8Bits(0x8000_0000_0000_0000ul)); // -0.0
                AddTwoColRow(p, "rs0", "V", 3, A.R8(0.0));
                AddTwoColRow(p, "rs0", "V", 4, A.R8(3.25));
                AddTwoColRow(p, "rs0", "V", 5, A.Null);
                AddTwoColRow(p, "rs0", "V", 6, A.R8(100.0));
            },
            [
                "CREATE INDEX IX_V ON T (V)",
                "CREATE INDEX IX_VD ON T (V DESC)",
                "CREATE UNIQUE INDEX UQ_V ON T (V)",
            ]);

        yield return DaoRowsAndIndex(
            "ixtype-double-rows-mdb-dao",
            EmptyMdb,
            "Jet 4 MDB DAO: populated T (V DOUBLE: -10.5, 0.0, 3.25, NULL, 100.0), CREATE INDEX IX_V (ASC) then CREATE UNIQUE INDEX UQ_V (DESC). Keeps all pages after each step.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, V DOUBLE)",
            p =>
            {
                AddTwoColRow(p, "rs0", "V", 1, A.R8(-10.5));
                AddTwoColRow(p, "rs0", "V", 2, A.R8(0.0));
                AddTwoColRow(p, "rs0", "V", 3, A.R8(3.25));
                AddTwoColRow(p, "rs0", "V", 4, A.Null);
                AddTwoColRow(p, "rs0", "V", 5, A.R8(100.0));
            },
            [
                "CREATE INDEX IX_V ON T (V)",
                "CREATE UNIQUE INDEX UQ_V ON T (V DESC)",
            ]);

        // DateTime (0x08)
        yield return DaoSteps(
            "ixtype-datetime-empty-accdb-dao",
            EmptyAccdb,
            IndexTypeRefs,
            "ACCDB DAO: empty T (ID PK, D DATETIME), CREATE INDEX IX_D ON T (D) then CREATE UNIQUE INDEX UQ_D ON T (D DESC). Keeps all pages after each step.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, D DATETIME)"],
            [
                "CREATE INDEX IX_D ON T (D)",
                "CREATE UNIQUE INDEX UQ_D ON T (D DESC)",
            ]);

        yield return DaoRowsAndIndex(
            "ixtype-datetime-rows-accdb-dao",
            EmptyAccdb,
            "ACCDB DAO: populated T (D DATETIME: -1.5, 0.0, 45306.520833..., NULL, 46351.0), CREATE INDEX IX_D (ASC) then CREATE UNIQUE INDEX UQ_D (DESC). Keeps all pages after each step.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, D DATETIME)",
            p =>
            {
                AddTwoColRow(p, "rs0", "D", 1, A.Date(-1.5));
                AddTwoColRow(p, "rs0", "D", 2, A.Date(0.0));
                AddTwoColRow(p, "rs0", "D", 3, A.Date(new DateTime(2024, 1, 15, 12, 30, 0)));
                AddTwoColRow(p, "rs0", "D", 4, A.Null);
                AddTwoColRow(p, "rs0", "D", 5, A.Date(new DateTime(2026, 11, 25, 0, 0, 0)));
            },
            [
                "CREATE INDEX IX_D ON T (D)",
                "CREATE UNIQUE INDEX UQ_D ON T (D DESC)",
            ]);

        yield return DaoRowsAndIndex(
            "ixtype-datetime-rows-mdb-dao",
            EmptyMdb,
            "Jet 4 MDB DAO: populated T (D DATETIME: -1.5, 0.0, 2024-01-15 12:30, NULL, 2026-11-25), CREATE INDEX IX_D (ASC) then CREATE UNIQUE INDEX UQ_D (DESC). Keeps all pages after each step.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, D DATETIME)",
            p =>
            {
                AddTwoColRow(p, "rs0", "D", 1, A.Date(-1.5));
                AddTwoColRow(p, "rs0", "D", 2, A.Date(0.0));
                AddTwoColRow(p, "rs0", "D", 3, A.Date(new DateTime(2024, 1, 15, 12, 30, 0)));
                AddTwoColRow(p, "rs0", "D", 4, A.Null);
                AddTwoColRow(p, "rs0", "D", 5, A.Date(new DateTime(2026, 11, 25, 0, 0, 0)));
            },
            [
                "CREATE INDEX IX_D ON T (D)",
                "CREATE UNIQUE INDEX UQ_D ON T (D DESC)",
            ]);

        // GUID (0x0F)
        yield return DaoSteps(
            "ixtype-guid-empty-accdb-dao",
            EmptyAccdb,
            IndexTypeRefs,
            "ACCDB DAO: empty T (ID PK, G GUID), CREATE INDEX IX_G ON T (G) then CREATE UNIQUE INDEX UQ_G ON T (G DESC). Keeps all pages after each step.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, G GUID)"],
            [
                "CREATE INDEX IX_G ON T (G)",
                "CREATE UNIQUE INDEX UQ_G ON T (G DESC)",
            ]);

        yield return DaoRowsAndIndex(
            "ixtype-guid-rows-accdb-dao",
            EmptyAccdb,
            "ACCDB DAO: populated T (G GUID: 4 distinct GUIDs + NULL), CREATE INDEX IX_G (ASC) then CREATE UNIQUE INDEX UQ_G (DESC). Keeps all pages after each step.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, G GUID)",
            p =>
            {
                AddTwoColRow(p, "rs0", "G", 1, A.Str("{00000001-0002-0003-0004-000000000005}"));
                AddTwoColRow(p, "rs0", "G", 2, A.Str("{00000000-0000-0000-0000-000000000000}"));
                AddTwoColRow(p, "rs0", "G", 3, A.Null);
                AddTwoColRow(p, "rs0", "G", 4, A.Str("{80000000-0000-0000-0000-000000000001}"));
                AddTwoColRow(p, "rs0", "G", 5, A.Str("{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}"));
            },
            [
                "CREATE INDEX IX_G ON T (G)",
                "CREATE UNIQUE INDEX UQ_G ON T (G DESC)",
            ]);

        yield return DaoRowsAndIndex(
            "ixtype-guid-rows-mdb-dao",
            EmptyMdb,
            "Jet 4 MDB DAO: populated T (G GUID: 4 distinct GUIDs + NULL), CREATE INDEX IX_G (ASC) then CREATE UNIQUE INDEX UQ_G (DESC). Keeps all pages after each step.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, G GUID)",
            p =>
            {
                AddTwoColRow(p, "rs0", "G", 1, A.Str("{00000001-0002-0003-0004-000000000005}"));
                AddTwoColRow(p, "rs0", "G", 2, A.Str("{00000000-0000-0000-0000-000000000000}"));
                AddTwoColRow(p, "rs0", "G", 3, A.Null);
                AddTwoColRow(p, "rs0", "G", 4, A.Str("{80000000-0000-0000-0000-000000000001}"));
                AddTwoColRow(p, "rs0", "G", 5, A.Str("{FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF}"));
            },
            [
                "CREATE INDEX IX_G ON T (G)",
                "CREATE UNIQUE INDEX UQ_G ON T (G DESC)",
            ]);

        // Memo (0x0C) — note: ACE accepts CREATE INDEX on Memo (r-q-cursor-dao/index-nonindexable-types)!
        yield return DaoSteps(
            "ixtype-memo-empty-accdb-dao",
            EmptyAccdb,
            IndexTypeRefs,
            "ACCDB DAO: empty T (ID PK, M MEMO), CREATE INDEX IX_M ON T (M) then CREATE UNIQUE INDEX UQ_M ON T (M). Keeps all pages after each step.",
            ["CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, M MEMO)"],
            [
                "CREATE INDEX IX_M ON T (M)",
                "CREATE UNIQUE INDEX UQ_M ON T (M)",
            ]);

        string prefix255 = new string('A', 254) + "1";
        yield return DaoRowsAndIndex(
            "ixtype-memo-rows-accdb-dao",
            EmptyAccdb,
            "ACCDB DAO: populated T (M MEMO: 'beta', 'alpha', NULL, and two >255-char external LVAL memos sharing the same 255-char prefix), CREATE INDEX IX_M (ASC) then CREATE UNIQUE INDEX UQ_M (tests 255-char prefix uniqueness). Keeps all pages after each step.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, M MEMO)",
            p =>
            {
                AddTwoColRow(p, "rs0", "M", 1, A.Str("beta"));
                AddTwoColRow(p, "rs0", "M", 2, A.Str("alpha"));
                AddTwoColRow(p, "rs0", "M", 3, A.Null);
                AddTwoColRow(p, "rs0", "M", 4, A.Str(prefix255 + new string('X', 50)));
                AddTwoColRow(p, "rs0", "M", 5, A.Str(prefix255 + new string('Y', 50)));
            },
            [
                "CREATE INDEX IX_M ON T (M)",
                "CREATE UNIQUE INDEX UQ_M ON T (M)",
            ]);

        yield return DaoRowsAndIndex(
            "ixtype-memo-rows-mdb-dao",
            EmptyMdb,
            "Jet 4 MDB DAO: populated T (M MEMO: 'beta', 'alpha', NULL, and two >255-char external LVAL memos sharing the same 255-char prefix), CREATE INDEX IX_M (ASC) then CREATE UNIQUE INDEX UQ_M. Keeps all pages after each step.",
            "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, M MEMO)",
            p =>
            {
                AddTwoColRow(p, "rs0", "M", 1, A.Str("beta"));
                AddTwoColRow(p, "rs0", "M", 2, A.Str("alpha"));
                AddTwoColRow(p, "rs0", "M", 3, A.Null);
                AddTwoColRow(p, "rs0", "M", 4, A.Str(prefix255 + new string('X', 50)));
                AddTwoColRow(p, "rs0", "M", 5, A.Str(prefix255 + new string('Y', 50)));
            },
            [
                "CREATE INDEX IX_M ON T (M)",
                "CREATE UNIQUE INDEX UQ_M ON T (M)",
            ]);
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers

    private static Probe DaoSteps(
        string name,
        string fixture,
        string[] refs,
        string description,
        string[] setupSql,
        string[] targetSql,
        string? readBackTable = null) => new(
        Card,
        name,
        refs,
        description,
        p =>
        {
            if (setupSql.Length == 0)
            {
                KeepAllPages(p, "before-open", "pg0");
            }
            else
            {
                p.Files("before-open")
                    .Create("DAO.DBEngine.120", "eng0")
                    .Modules("after-create")
                    .CallBind("db0", "eng0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                foreach (string sql in setupSql)
                {
                    p.Call("db0", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
                }
                p.Call("db0", "Close").Release("db0").Release("eng0");
                KeepAllPages(p, "after-setup", "pg0");
            }

            for (int i = 0; i < targetSql.Length; i++)
            {
                string suffix = targetSql.Length == 1 ? "" : $"-{i + 1}";
                string eng = $"eng{i + 1}", db = $"db{i + 1}", pg = $"pg{i + 1}";
                p.Create("DAO.DBEngine.120", eng);
                if (setupSql.Length == 0 && i == 0)
                {
                    p.Modules("after-create");
                }
                p.CallBind(db, eng, "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call(db, "Execute", A.Str(targetSql[i]), A.I4(Dao.FailOnError))
                    .Call(db, "Close").Release(db).Release(eng);
                KeepAllPages(p, "after-ddl" + suffix, pg);
            }

            if (readBackTable is not null)
            {
                ReadBackTable(p, readBackTable);
            }
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe AdoSteps(
        string name,
        string fixture,
        string[] refs,
        string description,
        string[] setupSql,
        string[] targetSql) => new(
        Card,
        name,
        refs,
        description,
        p =>
        {
            if (setupSql.Length == 0)
            {
                KeepAllPages(p, "before-open", "pg0");
            }
            else
            {
                p.Files("before-open");
                AdoOpen(p, "cn0", observeModules: true);
                foreach (string sql in setupSql)
                {
                    p.Call("cn0", "Execute", A.Str(sql), A.Missing, A.I4(1));
                }
                p.Call("cn0", "Close").Release("cn0");
                KeepAllPages(p, "after-setup", "pg0");
            }

            for (int i = 0; i < targetSql.Length; i++)
            {
                string suffix = targetSql.Length == 1 ? "" : $"-{i + 1}";
                string cn = $"cn{i + 1}", pg = $"pg{i + 1}";
                AdoOpen(p, cn, observeModules: setupSql.Length == 0 && i == 0);
                p.Call(cn, "Execute", A.Str(targetSql[i]), A.Missing, A.I4(1))
                    .Call(cn, "Close").Release(cn);
                KeepAllPages(p, "after-ddl" + suffix, pg);
            }
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static Probe DaoRowsAndIndex(
        string name,
        string fixture,
        string description,
        string createSql,
        Action<P> populateRows,
        string[] targetSql) => new(
        Card,
        name,
        IndexTypeRefs,
        description,
        p =>
        {
            p.Files("before-open")
                .Create("DAO.DBEngine.120", "eng0")
                .Modules("after-create")
                .CallBind("db0", "eng0", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                .Call("db0", "Execute", A.Str(createSql), A.I4(Dao.FailOnError))
                .OpenRs("rs0", "db0", "T", Dao.OpenTable);
            populateRows(p);
            p.Call("rs0", "Close").Release("rs0")
                .Call("db0", "Close").Release("db0").Release("eng0");
            KeepAllPages(p, "after-setup", "pg0");

            for (int i = 0; i < targetSql.Length; i++)
            {
                string suffix = targetSql.Length == 1 ? "" : $"-{i + 1}";
                string eng = $"eng{i + 1}", db = $"db{i + 1}", pg = $"pg{i + 1}";
                p.Create("DAO.DBEngine.120", eng)
                    .CallBind(db, eng, "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .Call(db, "Execute", A.Str(targetSql[i]), A.I4(Dao.FailOnError))
                    .Call(db, "Close").Release(db).Release(eng);
                KeepAllPages(p, "after-ddl" + suffix, pg);
            }
        })
    {
        Fixtures = [("main", fixture)],
    };

    private static void AddTwoColRow(P p, string rs, string valCol, int id, Arg val)
    {
        p.Call(rs, "AddNew")
            .GetBind("fs", rs, "Fields")
            .Item("f0", "fs", A.Str("ID")).Put("f0", "Value", A.I4(id)).Release("f0")
            .Item("f1", "fs", A.Str(valCol)).Put("f1", "Value", val).Release("f1")
            .Release("fs")
            .Call(rs, "Update");
    }

    private static void ReadBackTable(P p, string table)
    {
        p.Create("DAO.DBEngine.120", "engR")
            .CallBind("dbR", "engR", "OpenDatabase", A.In(), A.Bool(false), A.Bool(true))
            .OpenRs("rsR", "dbR", $"SELECT * FROM [{table}] ORDER BY ID", Dao.OpenSnapshot)
            .Until("rsR", "EOF", 16, b => b
                .GetBind("rf", "rsR", "Fields")
                .Each("rf", "rx", 16, f => f.Get("rx", "Name").Get("rx", "Type").Get("rx", "Size").Get("rx", "Value"))
                .Release("rf")
                .Call("rsR", "MoveNext"))
            .Call("rsR", "Close").Release("rsR")
            .Call("dbR", "Close").Release("dbR").Release("engR");
    }

    private static void AdoOpen(P p, string cn, bool observeModules)
    {
        string props = $"{cn}_p", ds = $"{cn}_ds";
        p.Create("ADODB.Connection", cn);
        if (observeModules)
        {
            p.Modules("after-create");
        }
        p.Put(cn, "Provider", A.Str(AceProvider))
            .GetBind(props, cn, "Properties")
            .Item(ds, props, A.Str("Data Source"))
            .Put(ds, "Value", A.In())
            .Release(ds).Release(props)
            .Call(cn, "Open");
    }

    private static void KeepAllPages(P p, string label, string handle) =>
        p.FilePages(label, 4096)
            .NativeOpen(handle, "input:main", Win.GenericRead, Win.ShareAll, Win.OpenExisting)
            .NativeRead(handle, 0, FullReadBytes, keep: true)
            .NativeClose(handle);
}
