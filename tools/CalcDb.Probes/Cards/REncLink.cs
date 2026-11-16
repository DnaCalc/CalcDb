namespace CalcDb.Probes.Cards;

/// <summary>
/// R-ENC-1 (native discovery: database passwords and encoding through ACE DAO and OLE DB) and R-LINK-1
/// (native discovery: persistent linked tables between two native files). Fixtures are created in three passes:
/// the enc-* files and the link sources (src, src2, srcpwd) first, then the linked file (it needs src and srcpwd
/// as inputs), then every read/mutation probe. Link targets are written as relative paths from the worker's
/// working directory (the attempt directory), so ";DATABASE=inputs\r-enc-link-src.accdb" resolves for the copies
/// every attempt receives; whether ACE resolves a relative path that way is itself recorded by the creation probe.
/// Passwords in fixtures are test values only. The probes ask; they do not judge.
/// </summary>
public sealed class REncLink : ICard
{
    private const string Card = "r-enc-link";
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const string Pwd1 = "secret1", Pwd2 = "secret2", Pwd3 = "secret3";
    private const int DbEncrypt = 2, DbDecrypt = 4;
    private const int AttachSavePwd = 131072;

    private static readonly string[] Formats = ["mdb", "accdb"];

    private sealed record Try(string Tag, string? Connect, bool ReadOnly = true);

    public IEnumerable<Probe> Probes() => EncProbes().Concat(LinkProbes());

    private static int Ver(string ext) => ext == "mdb" ? Dao.Version40 : Dao.Version120;

    private static string EncFx(string ext, string kind) => $"{Card}-enc-{ext}-{kind}";

    private static string Fx(string name) => $"{Card}-{name}";

    // ===================================================================================== R-ENC-1

    private static IEnumerable<Probe> EncProbes()
    {
        // Fixture creation: every route to an encoded or password-protected native file.
        foreach (string ext in Formats)
        {
            string up = ext.ToUpperInvariant();
            yield return EncCreation(ext, "plain", $"an unprotected {up} created by CreateDatabase(path, dbLangGeneral, {Ver(ext)})", Dao.LangGeneral, Ver(ext), null);
            yield return EncCreation(ext, "enc", $"a {up} created with the dbEncrypt option (2) added to the version option and no password", Dao.LangGeneral, Ver(ext) | DbEncrypt, null);
            yield return EncCreation(ext, "pwd", $"a {up} created with the password carried in the locale string (dbLangGeneral & \";pwd={Pwd1}\")", Dao.LangGeneral + ";pwd=" + Pwd1, Ver(ext), null);
            yield return EncCreation(ext, "newpwd", $"a {up} created unprotected, filled, then given the password '{Pwd2}' by Database.NewPassword(\"\", \"{Pwd2}\") on the open exclusive database", Dao.LangGeneral, Ver(ext), Pwd2);
        }

        // ENC-PROFILE: right, wrong and missing credentials through OpenDatabase's Connect argument.
        foreach (string ext in Formats)
        {
            foreach ((string kind, string pwd) in new[] { ("pwd", Pwd1), ("newpwd", Pwd2) })
            {
                string fixture = EncFx(ext, kind);
                yield return new Probe(Card, $"profile-{kind}-{ext}", ["R-ENC-1", "ENC-PROFILE", "RQ-LATE-01"],
                    $"Open {fixture} through DBEngine.OpenDatabase(path, False, True[, Connect]) with Connect omitted, ';pwd=', a wrong password, the right password, the right password in upper case, and the right password read/write. After each attempt DBEngine.Errors is read; an open that works reports the table and row counts.",
                    p =>
                    {
                        p.Engine();
                        OpenSeries(p, () => A.In(), "o_", Series(pwd));
                        p.Finish();
                    })
                { Fixtures = [("main", fixture)] };
            }
        }

        string[] plainRoles = ["mdb_plain", "mdb_enc", "accdb_plain", "accdb_enc"];
        yield return new Probe(Card, "profile-unprotected", ["R-ENC-1", "ENC-PROFILE"],
            "Open the four files without a database password (plain and dbEncrypt-created, MDB and ACCDB) read-only with Connect omitted, with a wrong password ';pwd=wrong-pass' and with ';pwd='. Errors are read after each attempt; an open that works reports counts.",
            p =>
            {
                p.Engine();
                foreach (string role in plainRoles)
                {
                    OpenSeries(p, () => A.In(role), role + "_", [new("none", null), new("wrong", ";pwd=wrong-pass"), new("empty", ";pwd=")]);
                }
                p.Finish();
            })
        {
            Fixtures = [("mdb_plain", EncFx("mdb", "plain")), ("mdb_enc", EncFx("mdb", "enc")), ("accdb_plain", EncFx("accdb", "plain")), ("accdb_enc", EncFx("accdb", "enc"))],
        };

        // OLE DB: the same credential question through ADODB.Connection and the ACE provider.
        yield return new Probe(Card, "ado-open-pwd-mdb", ["R-ENC-1", "ENC-PROFILE"],
            $"Open {EncFx("mdb", "pwd")} through ADODB.Connection (Provider and Data Source set as properties, then Open with no arguments) with no 'Jet OLEDB:Database Password', a wrong one and the right one. Connection.Errors is read after each open; an open that works runs a count query.",
            p =>
            {
                p.Modules("before-create");
                AdoOpen(p, "none", "main", null);
                AdoOpen(p, "wrong", "main", "wrong-pass");
                AdoOpen(p, "right", "main", Pwd1);
                p.Modules("after-close").Files("after-close");
            })
        { Fixtures = [("main", EncFx("mdb", "pwd"))] };

        // ENC-UPDATE: edits at the start, middle and end of the data, adds, deletes and an index-splitting bulk insert.
        foreach (string ext in Formats)
        {
            string fixture = EncFx(ext, "pwd");
            yield return new Probe(Card, $"update-pwd-{ext}", ["R-ENC-1", "ENC-UPDATE"],
                $"Open {fixture} read/write shared with ';pwd={Pwd1}'; update rows 1, 32 and 64 (start, middle, end) with 3,000-character memos, insert a row, delete a range, bulk-insert a copy of the table (index split), then close. The database bytes before and after are kept; the file is reopened with the password and counted.",
                p =>
                {
                    p.Engine().Files("before-edit", keep: true)
                        .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false), A.Str(";pwd=" + Pwd1));
                    Verify(p, "db", "pre");
                    foreach (string sql in new[]
                    {
                        $"UPDATE T SET S = 'edited-first', M = '{new string('a', 3000)}' WHERE ID = 1",
                        $"UPDATE T SET S = 'edited-middle', M = '{new string('b', 3000)}' WHERE ID = 32",
                        $"UPDATE T SET S = 'edited-last', M = '{new string('c', 3000)}' WHERE ID = 64",
                        $"INSERT INTO T (ID, S, M) VALUES (100, 'inserted', '{new string('d', 6000)}')",
                        "DELETE FROM T WHERE ID BETWEEN 10 AND 20",
                        "INSERT INTO T (ID, S, M) SELECT ID + 1000, 'z' & ID, M FROM T",
                    })
                    {
                        p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError));
                    }
                    p.Errors();
                    Verify(p, "db", "post");
                    p.Call("db", "Close").Release("db").Files("after-edit", keep: true);
                    OpenSeries(p, () => A.In(), "r_", [new("right", ";pwd=" + Pwd1)]);
                    p.Finish();
                })
            { Fixtures = [("main", fixture)] };
        }

        // ENC-PASSWORD: set, change and remove through the actual open-database method, with before/after images.
        foreach (string ext in Formats)
        {
            yield return PasswordOp($"set-{ext}", EncFx(ext, "plain"), null, "", Pwd3,
                $"Open the unprotected {ext.ToUpperInvariant()} exclusive read/write and call Database.NewPassword(\"\", \"{Pwd3}\"); close; reopen read-only with no password and with the new password.",
                [new("none", null), new("new", ";pwd=" + Pwd3)]);
            yield return PasswordOp($"change-{ext}", EncFx(ext, "newpwd"), ";pwd=" + Pwd2, Pwd2, Pwd3,
                $"Open the {ext.ToUpperInvariant()} protected with '{Pwd2}' exclusive read/write and call Database.NewPassword(\"{Pwd2}\", \"{Pwd3}\"); close; reopen read-only with the old, the new and no password.",
                [new("none", null), new("old", ";pwd=" + Pwd2), new("new", ";pwd=" + Pwd3)]);
        }

        yield return PasswordOp("remove-mdb", EncFx("mdb", "pwd"), ";pwd=" + Pwd1, Pwd1, "",
            $"Open the MDB protected with '{Pwd1}' exclusive read/write and call Database.NewPassword(\"{Pwd1}\", \"\"); close; reopen read-only with no password and with the old password.",
            [new("none", null), new("old", ";pwd=" + Pwd1)]);

        yield return new Probe(Card, "password-wrong-old", ["R-ENC-1", "ENC-PASSWORD"],
            $"On the password-protected MDB and ACCDB (both '{Pwd1}'): open exclusive read/write, call NewPassword with a wrong old password, then with an empty old password; read Errors after each; close; reopen read-only with the original password and with each attempted new password.",
            p =>
            {
                p.Engine();
                foreach (string role in new[] { "mdb", "accdb" })
                {
                    p.Files("before-" + role, keep: true)
                        .CallBind(role + "_db", "eng", "OpenDatabase", A.In(role), A.Bool(true), A.Bool(false), A.Str(";pwd=" + Pwd1))
                        .Call(role + "_db", "NewPassword", A.Str("not-the-old"), A.Str("x1")).Errors()
                        .Call(role + "_db", "NewPassword", A.Str(""), A.Str("x2")).Errors();
                    Verify(p, role + "_db", role);
                    p.Call(role + "_db", "Close").Release(role + "_db").Files("after-" + role, keep: true);
                    OpenSeries(p, () => A.In(role), role + "_", [new("orig", ";pwd=" + Pwd1), new("x1", ";pwd=x1"), new("x2", ";pwd=x2")]);
                }
                p.Finish();
            })
        { Fixtures = [("mdb", EncFx("mdb", "pwd")), ("accdb", EncFx("accdb", "pwd"))] };

        // ENC-LIFE: a cursor held across the credential change.
        yield return new Probe(Card, "life-held-cursor-mdb", ["R-ENC-1", "ENC-LIFE"],
            $"Open the MDB protected with '{Pwd1}' exclusive read/write, open a dynaset and read its first row, call NewPassword(\"{Pwd1}\", \"{Pwd3}\"), then keep reading the held recordset, run an Execute and a fresh recordset on the same Database, close, and reopen with the old and the new password.",
            p =>
            {
                p.Engine()
                    .CallBind("db", "eng", "OpenDatabase", A.In(), A.Bool(true), A.Bool(false), A.Str(";pwd=" + Pwd1))
                    .OpenRs("held", "db", "SELECT ID, S, LEN(M) AS LM FROM T ORDER BY ID", Dao.OpenDynaset)
                    .ReadRow("held").Call("held", "MoveNext").ReadRow("held")
                    .Call("db", "NewPassword", A.Str(Pwd1), A.Str(Pwd3)).Errors()
                    .Call("held", "MoveNext").ReadRow("held").Get("held", "Bookmark")
                    .Call("db", "Execute", A.Str("UPDATE T SET S = 'after-password' WHERE ID = 2"), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected")
                    .OpenRs("fresh", "db", "SELECT ID, S FROM T WHERE ID = 2", Dao.OpenSnapshot).ReadRow("fresh").Call("fresh", "Close").Release("fresh")
                    .Call("held", "Close").Release("held").Call("db", "Close").Release("db").Files("after-close", keep: true);
                OpenSeries(p, () => A.In(), "r_", [new("old", ";pwd=" + Pwd1), new("new", ";pwd=" + Pwd3)]);
                p.Finish();
            })
        { Fixtures = [("main", EncFx("mdb", "pwd"))] };

        // ENC-REKEY: CompactDatabase from a closed source into a named destination.
        foreach (string ext in Formats)
        {
            string dst = $"{Fx("rekey-pwd")}.{ext}";
            yield return new Probe(Card, $"rekey-pwd-{ext}", ["R-ENC-1", "ENC-REKEY"],
                $"DBEngine.CompactDatabase of the {ext.ToUpperInvariant()} protected with '{Pwd1}' (source password in SrcLocale) into a new file with destination locale ';pwd=other1'; the destination is then opened with no password, the source password and the new password. Source and destination images are kept.",
                p =>
                {
                    p.Engine().Files("before-compact", keep: true)
                        .Call("eng", "CompactDatabase", A.In(), A.Out(dst), A.Str(Dao.LangGeneral + ";pwd=other1"), A.I4(Ver(ext)), A.Str(";pwd=" + Pwd1))
                        .Errors().Files("after-compact", keep: true);
                    OpenSeries(p, () => A.Out(dst), "d_", [new("none", null), new("src", ";pwd=" + Pwd1), new("new", ";pwd=other1")]);
                    p.Finish();
                })
            { Fixtures = [("main", EncFx(ext, "pwd"))] };
        }

        yield return new Probe(Card, "rekey-options-mdb", ["R-ENC-1", "ENC-REKEY"],
            "DBEngine.CompactDatabase with options on closed MDB sources: the unprotected file with dbEncrypt, the same with dbEncrypt and ';pwd=encpw' in the destination locale, and the dbEncrypt-created file with dbDecrypt. Each destination is opened with no password and the password; images are kept.",
            p =>
            {
                string a = Fx("rekey-encrypted") + ".mdb", b = Fx("rekey-encrypted-pwd") + ".mdb", c = Fx("rekey-decrypted") + ".mdb";
                p.Engine().Files("before-compact", keep: true)
                    .Call("eng", "CompactDatabase", A.In("plain"), A.Out(a), A.Str(Dao.LangGeneral), A.I4(Dao.Version40 | DbEncrypt)).Errors()
                    .Call("eng", "CompactDatabase", A.In("plain"), A.Out(b), A.Str(Dao.LangGeneral + ";pwd=encpw"), A.I4(Dao.Version40 | DbEncrypt)).Errors()
                    .Call("eng", "CompactDatabase", A.In("enc"), A.Out(c), A.Str(Dao.LangGeneral), A.I4(Dao.Version40 | DbDecrypt)).Errors()
                    .Files("after-compact", keep: true);
                OpenSeries(p, () => A.Out(a), "a_", [new("none", null)]);
                OpenSeries(p, () => A.Out(b), "b_", [new("none", null), new("right", ";pwd=encpw")]);
                OpenSeries(p, () => A.Out(c), "c_", [new("none", null)]);
                p.Finish();
            })
        { Fixtures = [("plain", EncFx("mdb", "plain")), ("enc", EncFx("mdb", "enc"))] };
    }

    private static Probe EncCreation(string ext, string kind, string what, string locale, int option, string? newPassword) =>
        new(Card, $"create-enc-{ext}-{kind}", ["R-ENC-1", "ENC-PROFILE", "ENC-PAGE", "ENC-PASSWORD"],
            $"R-ENC-1 fixture: {what}. Content: table T(ID, S indexed, M memo of 3,000 characters, 64 rows spanning pages), table U with a text key and a foreign key to T (one non-ASCII value), and saved query Q_T. The file image before NewPassword (when used) and after close is kept.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create").Get("eng", "Version")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{EncFx(ext, kind)}.{ext}"), A.Str(locale), A.I4(option)).Get("db", "Version");
                Content(p);
                Verify(p, "db", "pre");
                if (newPassword is not null)
                {
                    p.Files("before-newpassword", keep: true).Call("db", "NewPassword", A.Str(""), A.Str(newPassword)).Errors();
                    Verify(p, "db", "post");
                }
                p.Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close", keep: true);
            })
        { CreatesFixture = true };

    private static Probe PasswordOp(string name, string fixture, string? openConnect, string oldPw, string newPw, string what, Try[] reopen) =>
        new(Card, $"newpassword-{name}", ["R-ENC-1", "ENC-PASSWORD"], what + " The database bytes before and after the call are kept.",
            p =>
            {
                Arg[] open = openConnect is null ? [A.In(), A.Bool(true), A.Bool(false)] : [A.In(), A.Bool(true), A.Bool(false), A.Str(openConnect)];
                p.Engine().Files("before-op", keep: true).CallBind("db", "eng", "OpenDatabase", open);
                Verify(p, "db", "pre");
                p.Call("db", "NewPassword", A.Str(oldPw), A.Str(newPw)).Errors();
                Verify(p, "db", "post");
                p.Call("db", "Close").Release("db").Files("after-op", keep: true);
                OpenSeries(p, () => A.In(), "r_", reopen);
                p.Finish();
            })
        { Fixtures = [("main", fixture)] };

    /// <summary>The ENC content: multi-page memos, an index, a foreign key, non-ASCII text and a saved query.</summary>
    private static void Content(P p)
    {
        p.Call("db", "Execute", A.Str("CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, S TEXT(50), M MEMO)"))
            .Call("db", "Execute", A.Str("CREATE INDEX IX_S ON T (S)"))
            .Call("db", "Execute", A.Str($"INSERT INTO T (ID, S, M) VALUES (1, 'r1', '{new string('m', 3000)}')"));
        for (int shift = 1; shift < 64; shift *= 2)
        {
            p.Call("db", "Execute", A.Str($"INSERT INTO T (ID, S, M) SELECT ID + {shift}, 'r' & (ID + {shift}), M FROM T"));
        }
        p.Call("db", "Execute", A.Str("CREATE TABLE U (K TEXT(20) CONSTRAINT PK_U PRIMARY KEY, TID LONG)"))
            .Call("db", "Execute", A.Str("ALTER TABLE U ADD CONSTRAINT FK_U_T FOREIGN KEY (TID) REFERENCES T (ID)"))
            .Call("db", "Execute", A.Str("INSERT INTO U (K, TID) VALUES ('k1', 1)"))
            .Call("db", "Execute", A.Str("INSERT INTO U (K, TID) VALUES ('Bä€', 2)"))
            .CallBind("qd", "db", "CreateQueryDef", A.Str("Q_T"), A.Str("SELECT ID, S FROM T ORDER BY ID;")).Release("qd");
    }

    /// <summary>Counts of T and the rows of U, read through a snapshot on the named Database.</summary>
    private static void Verify(P p, string db, string tag)
    {
        p.OpenRs("vt_" + tag, db, "SELECT COUNT(*) AS N, SUM(LEN(M)) AS LM, MAX(ID) AS MX FROM T", Dao.OpenSnapshot)
            .ReadRow("vt_" + tag).Call("vt_" + tag, "Close").Release("vt_" + tag)
            .OpenRs("vu_" + tag, db, "SELECT K, TID FROM U ORDER BY K", Dao.OpenSnapshot)
            .ReadAll("vu_" + tag, 8, 4).Call("vu_" + tag, "Close").Release("vu_" + tag);
    }

    private static Try[] Series(string pwd) =>
    [
        new("none", null),
        new("empty", ";pwd="),
        new("wrong", ";pwd=wrong-pass"),
        new("right", ";pwd=" + pwd),
        new("right-case", ";pwd=" + pwd.ToUpperInvariant()),
        new("right-rw", ";pwd=" + pwd, false),
    ];

    /// <summary>OpenDatabase attempts in turn; each reads Errors, verifies on success, and closes.</summary>
    private static void OpenSeries(P p, Func<Arg> path, string prefix, Try[] tries)
    {
        foreach (Try t in tries)
        {
            string d = prefix + t.Tag.Replace('-', '_');
            Arg[] args = t.Connect is null ? [path(), A.Bool(false), A.Bool(t.ReadOnly)] : [path(), A.Bool(false), A.Bool(t.ReadOnly), A.Str(t.Connect)];
            p.CallBind(d, "eng", "OpenDatabase", args).Errors();
            Verify(p, d, d);
            p.Call(d, "Close").Release(d);
        }
    }

    private static void AdoOpen(P p, string tag, string role, string? password)
    {
        string cn = "cn_" + tag, props = "pr_" + tag, ers = "er_" + tag, rs = "rs_" + tag;
        p.Create("ADODB.Connection", cn).Put(cn, "Provider", A.Str(Ace)).GetBind(props, cn, "Properties")
            .Item("pd_" + tag, props, A.Str("Data Source")).Put("pd_" + tag, "Value", A.In(role));
        if (password is not null)
        {
            p.Item("pw_" + tag, props, A.Str("Jet OLEDB:Database Password")).Put("pw_" + tag, "Value", A.Str(password));
        }
        p.Call(cn, "Open")
            .GetBind(ers, cn, "Errors").Each(ers, "ae_" + tag, 8, b => b.Get("ae_" + tag, "Number").Get("ae_" + tag, "Description").Get("ae_" + tag, "SQLState").Get("ae_" + tag, "NativeError")).Release(ers)
            .CallBind(rs, cn, "Execute", A.Str("SELECT COUNT(*) AS N, SUM(LEN(M)) AS LM FROM T")).ReadRow(rs).Call(rs, "Close").Release(rs)
            .Call(cn, "Close").Release(cn);
    }

    // ===================================================================================== R-LINK-1

    /// <summary>Relative link target (from the attempt directory) of an input fixture file.</summary>
    private static string Rel(string fixture) => $";DATABASE=inputs\\{Fx(fixture)}.accdb";

    private static readonly (string Role, string Fixture)[] LinkedOnly = [("main", Fx("linked"))];
    private static readonly (string Role, string Fixture)[] LinkedSrc = [("main", Fx("linked")), ("src", Fx("src")), ("srcpwd", Fx("srcpwd"))];

    private static IEnumerable<Probe> LinkProbes()
    {
        // Source fixtures: unique key, nullable field, empty text, a/A, non-numeric text, duplicate non-key values, a long value, a counter.
        yield return SrcCreation("src", "LONG", 0, null, "the link source: table SRC(ID COUNTER primary key, V, N, D LONG, M memo) with four rows");
        yield return SrcCreation("src2", "TEXT(10)", 10, null, "the second source version: same table name SRC, keys replaced (11-14) and D changed from Long to Text");
        yield return SrcCreation("srcpwd", "LONG", 0, Pwd1, $"a password-protected link source ('{Pwd1}' in the locale string) with the same table SRC and rows");

        yield return new Probe(Card, "create-linked", ["R-LINK-1", "LINK-META", "LINK-OPEN"],
            $"R-LINK-1 fixture: native file A with local control table L (a copy of SRC's schema and rows) and two persistent links created by CreateTableDef(name, attributes, SourceTableName, Connect) and TableDefs.Append: LK to SRC in src ('{Rel("src")}'), and LKP to SRC in the password-protected srcpwd with the password in Connect and Attributes dbAttachSavePWD. Connect, SourceTableName, Attributes, fields, indexes and rows are read after each Append. Image kept.",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{Fx("linked")}.accdb"), A.Str(Dao.LangGeneral), A.I4(Dao.Version120));
                SrcRows(p, "L", "LONG", 0);
                p.GetBind("td", "db", "TableDefs")
                    .CallBind("lk", "db", "CreateTableDef", A.Str("LK"), A.I4(0), A.Str("SRC"), A.Str(Rel("src")))
                    .Get("lk", "Connect").Call("td", "Append", A.Obj("lk")).Errors();
                LinkMeta(p, "lk");
                ReadTable(p, "db", "LK", "lk");
                p.CallBind("lkp", "db", "CreateTableDef", A.Str("LKP"), A.I4(0), A.Str("SRC"), A.Str(Rel("srcpwd") + ";PWD=" + Pwd1))
                    .Put("lkp", "Attributes", A.I4(AttachSavePwd)).Get("lkp", "Attributes")
                    .Call("td", "Append", A.Obj("lkp")).Errors();
                LinkMeta(p, "lkp");
                ReadTable(p, "db", "LKP", "lkp");
                p.Release("lk").Release("lkp").Release("td").Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close", keep: true);
            })
        { CreatesFixture = true, Fixtures = [("src", Fx("src")), ("srcpwd", Fx("srcpwd"))] };

        // LINK-META and LINK-OPEN: the source is absent from the attempt; what does enumeration make observable, and what is the first call that demands it?
        yield return new Probe(Card, "meta-source-absent", ["R-LINK-1", "LINK-META", "LINK-OPEN"],
            "Open the linked file read-only with neither source file present. In order: enumerate TableDefs reading Name, Attributes, Connect, SourceTableName; then Fields of LK and LKP; then RecordCount; then OpenRecordset on LK and on LKP. Errors are read after each stage and the file bytes are kept after enumeration and after the open attempts.",
            p =>
            {
                p.Engine().Files("snap-0", keep: true).OpenDb()
                    .GetBind("tdfs", "db", "TableDefs")
                    .Each("tdfs", "tdf", 64, t => t.Get("tdf", "Name").Get("tdf", "Attributes").Get("tdf", "Connect").Get("tdf", "SourceTableName"))
                    .Errors().Files("after-enum", keep: true);
                foreach (string name in new[] { "LK", "LKP" })
                {
                    p.Item("t_" + name, "tdfs", A.Str(name)).GetBind("f_" + name, "t_" + name, "Fields")
                        .Each("f_" + name, "fd_" + name, 16, f => f.Get("fd_" + name, "Name").Get("fd_" + name, "Type")).Errors()
                        .Get("t_" + name, "RecordCount").Errors();
                }
                foreach (string name in new[] { "LK", "LKP" })
                {
                    p.OpenRs("o_" + name, "db", name, Dao.OpenDynaset).Errors();
                }
                p.Files("after-open-attempts", keep: true).Finish("db");
            })
        { Fixtures = LinkedOnly };

        // LINK-META with the sources present: the whole public metadata and reads through each link.
        yield return new Probe(Card, "meta-read-through-link", ["R-LINK-1", "LINK-META", "LINK-IDENTITY"],
            "Open the linked file read-only with both sources present. For LK and LKP read Name, Connect, SourceTableName, Attributes, Updatable, every Field (type, size, attributes, SourceField, SourceTable) and every Index; then read all rows through a dynaset and a snapshot, and RecordCount. Bytes before and after are kept.",
            p =>
            {
                p.Engine().Files("before", keep: true).OpenDb().GetBind("td", "db", "TableDefs");
                foreach (string name in new[] { "LK", "LKP" })
                {
                    p.Item("m_" + name, "td", A.Str(name));
                    LinkMeta(p, "m_" + name);
                    ReadTable(p, "db", name, name);
                    p.OpenRs("s_" + name, "db", $"SELECT * FROM {name} ORDER BY ID", Dao.OpenSnapshot).ReadAll("s_" + name, 8, 8).Call("s_" + name, "Close").Release("s_" + name)
                        .Get("m_" + name, "RecordCount");
                }
                p.Files("after", keep: true).Finish("db");
            })
        { Fixtures = LinkedSrc };

        // LINK-PUSHDOWN native lane: the same queries over the link and over the local copy.
        yield return new Probe(Card, "pushdown-link-vs-local", ["R-LINK-1", "LINK-PUSHDOWN"],
            "Run the same eight queries (equality on 'a' with an 'A' row, IS NULL, LIKE, ORDER BY with Null and empty text, TOP 2 with a tie, a failing CLng on non-numeric text, DISTINCT, GROUP BY) first against link LK and then against the local copy L, as snapshots read to EOF; Errors are read after each.",
            p =>
            {
                p.Engine().OpenDb();
                string[] queries =
                [
                    "SELECT ID, V FROM {0} WHERE V = 'a' ORDER BY ID",
                    "SELECT ID, V FROM {0} WHERE V IS NULL ORDER BY ID",
                    "SELECT ID, V FROM {0} WHERE V LIKE 'a*' ORDER BY ID",
                    "SELECT ID, V FROM {0} ORDER BY V, ID",
                    "SELECT TOP 2 ID, D FROM {0} ORDER BY D",
                    "SELECT ID, CLng(N) AS NN FROM {0} ORDER BY ID",
                    "SELECT DISTINCT D FROM {0}",
                    "SELECT D, COUNT(*) AS C FROM {0} GROUP BY D ORDER BY D",
                ];
                for (int i = 0; i < queries.Length; i++)
                {
                    foreach (string table in new[] { "LK", "L" })
                    {
                        string rs = $"q{i}_{table}";
                        p.OpenRs(rs, "db", string.Format(queries[i], table), Dao.OpenSnapshot).ReadAll(rs, 8, 4).Call(rs, "Close").Release(rs).Errors();
                    }
                }
                p.Finish("db");
            })
        { Fixtures = LinkedSrc };

        // LINK-RELINK
        yield return new Probe(Card, "relink-refresh-unchanged", ["R-LINK-1", "LINK-RELINK"],
            "Open the linked file read/write with both sources present, read the metadata of LK and LKP, call TableDef.RefreshLink on each with Connect unchanged, and read the metadata and rows again. Bytes before and after the calls are kept.",
            p =>
            {
                p.Engine().Files("before", keep: true).OpenDb("db", "main", false, false).GetBind("td", "db", "TableDefs");
                foreach (string name in new[] { "LK", "LKP" })
                {
                    p.Item("m_" + name, "td", A.Str(name));
                    LinkMeta(p, "m_" + name + "_0", "m_" + name);
                    p.Call("m_" + name, "RefreshLink").Errors();
                    LinkMeta(p, "m_" + name + "_1", "m_" + name);
                    ReadTable(p, "db", name, name);
                }
                p.Files("after", keep: true).Finish("db");
            })
        { Fixtures = LinkedSrc };

        yield return new Probe(Card, "relink-to-second-version", ["R-LINK-1", "LINK-RELINK", "LINK-SCHEMA"],
            "Read/write open; retain LK's Fields and Indexes collections and an open dynaset (first row read); set TableDef.Connect to src2 (keys replaced, D now Text) and read Connect before calling RefreshLink; call RefreshLink; then read through the retained Fields and recordset, re-read Fields, Indexes and rows through a new recordset. Bytes before and after are kept.",
            p =>
            {
                p.Engine().Files("before", keep: true).OpenDb("db", "main", false, false).GetBind("td", "db", "TableDefs").Item("lk", "td", A.Str("LK"))
                    .GetBind("old_f", "lk", "Fields").Each("old_f", "of", 16, f => f.Get("of", "Name").Get("of", "Type"))
                    .GetBind("old_i", "lk", "Indexes").Each("old_i", "oi", 8, i => i.Get("oi", "Name").Get("oi", "Primary"))
                    .OpenRs("held", "db", "SELECT * FROM LK ORDER BY ID", Dao.OpenDynaset).ReadRow("held")
                    .Put("lk", "Connect", A.Str(Rel("src2"))).Get("lk", "Connect")
                    .OpenRs("before_refresh", "db", "SELECT * FROM LK ORDER BY ID", Dao.OpenSnapshot).ReadAll("before_refresh", 8, 8).Call("before_refresh", "Close").Release("before_refresh").Errors()
                    .Call("lk", "RefreshLink").Errors().Get("lk", "Connect")
                    .Each("old_f", "of2", 16, f => f.Get("of2", "Name").Get("of2", "Type"))
                    .Each("old_i", "oi2", 8, i => i.Get("oi2", "Name"))
                    .Call("held", "MoveNext").ReadRow("held").Errors();
                LinkMeta(p, "lk");
                ReadTable(p, "db", "LK", "after");
                p.Call("held", "Close").Release("held").Files("after", keep: true).Finish("db");
            })
        { Fixtures = [("main", Fx("linked")), ("src", Fx("src")), ("srcpwd", Fx("srcpwd")), ("src2", Fx("src2"))] };

        yield return new Probe(Card, "relink-bad-targets", ["R-LINK-1", "LINK-OPEN", "LINK-RELINK"],
            "On LK, read/write: set Connect to a missing file, RefreshLink, read Connect and try a recordset; then to the linked file itself (no table SRC), then to a valid file prefixed with an unknown option ';FOO=bar', then to the text 'garbage'; finally restore the original Connect and RefreshLink. Errors, Connect and the file bytes are read after each step.",
            p =>
            {
                p.Engine().Files("before", keep: true).OpenDb("db", "main", false, false).GetBind("td", "db", "TableDefs").Item("lk", "td", A.Str("LK")).Get("lk", "Connect");
                (string Tag, string Connect)[] steps =
                [
                    ("missing-file", @";DATABASE=inputs\r-enc-link-no-such-file.accdb"),
                    ("no-table", Rel("linked")),
                    ("unknown-option", ";FOO=bar" + Rel("src")),
                    ("garbage", "garbage"),
                    ("restored", Rel("src")),
                ];
                foreach ((string tag, string connect) in steps)
                {
                    p.Put("lk", "Connect", A.Str(connect)).Get("lk", "Connect").Call("lk", "RefreshLink").Errors().Get("lk", "Connect")
                        .OpenRs("o_" + tag, "db", "SELECT * FROM LK ORDER BY ID", Dao.OpenSnapshot).ReadAll("o_" + tag, 8, 8).Call("o_" + tag, "Close").Release("o_" + tag).Errors()
                        .Files("after-" + tag, keep: true);
                }
                p.Finish("db");
            })
        { Fixtures = LinkedSrc };

        yield return new Probe(Card, "link-credentials", ["R-LINK-1", "LINK-META", "LINK-OPEN"],
            $"On LKP (stored password '{Pwd1}', Attributes with dbAttachSavePWD): set Connect with a wrong password, with no password, then with the right one again, each time reading Connect before and after RefreshLink, Attributes and a recordset; then set Attributes to 0 and re-read. Errors and file bytes after each step.",
            p =>
            {
                p.Engine().Files("before", keep: true).OpenDb("db", "main", false, false).GetBind("td", "db", "TableDefs").Item("lk", "td", A.Str("LKP")).Get("lk", "Connect").Get("lk", "Attributes");
                (string Tag, string Connect)[] steps =
                [
                    ("wrong", Rel("srcpwd") + ";PWD=wrong-pass"),
                    ("none", Rel("srcpwd")),
                    ("right", Rel("srcpwd") + ";PWD=" + Pwd1),
                ];
                foreach ((string tag, string connect) in steps)
                {
                    p.Put("lk", "Connect", A.Str(connect)).Get("lk", "Connect").Call("lk", "RefreshLink").Errors().Get("lk", "Connect").Get("lk", "Attributes")
                        .OpenRs("o_" + tag, "db", "SELECT * FROM LKP ORDER BY ID", Dao.OpenSnapshot).ReadAll("o_" + tag, 8, 8).Call("o_" + tag, "Close").Release("o_" + tag).Errors()
                        .Files("after-" + tag, keep: true);
                }
                p.Put("lk", "Attributes", A.I4(0)).Errors().Get("lk", "Attributes").Get("lk", "Connect").Files("after-attributes-0", keep: true).Finish("db");
            })
        { Fixtures = LinkedSrc };

        // LINK-UPDATE / LINK-IDENTITY
        yield return new Probe(Card, "update-through-link", ["R-LINK-1", "LINK-UPDATE", "LINK-IDENTITY"],
            "Read/write: through LK run an UPDATE that matches three rows by a duplicated non-key value, an INSERT with every field, an INSERT without the counter key, a DELETE, a 5,000-character memo UPDATE, and an AddNew/Update and Edit/Update on a dynaset (reading LastModified bookmark and fields); RecordsAffected after each. An independent Workspace then reads the source copy directly.",
            p =>
            {
                p.Engine().Files("before", keep: true).OpenDb("db", "main", false, false);
                (string Tag, string Sql)[] stmts =
                [
                    ("upd-dup", "UPDATE LK SET N = 'upd' WHERE D = 10"),
                    ("ins-full", "INSERT INTO LK (ID, V, N, D, M) VALUES (5, 'e', '1', 30, 'm5')"),
                    ("ins-nokey", "INSERT INTO LK (V, N, D) VALUES ('gen', '2', 40)"),
                    ("del", "DELETE FROM LK WHERE ID = 3"),
                    ("upd-memo", $"UPDATE LK SET M = '{new string('q', 5000)}' WHERE ID = 1"),
                ];
                foreach ((string tag, string sql) in stmts)
                {
                    p.Call("db", "Execute", A.Str(sql), A.I4(Dao.FailOnError)).Get("db", "RecordsAffected").Errors();
                }
                p.OpenRs("ed", "db", "SELECT * FROM LK ORDER BY ID", Dao.OpenDynaset).GetBind("fl", "ed", "Fields")
                    .Item("fv", "fl", A.Str("V")).Item("fid", "fl", A.Str("ID"))
                    .Call("ed", "AddNew").Put("fv", "Value", A.Str("added")).Call("ed", "Update").Get("ed", "LastModified").Get("fid", "Value").Errors()
                    .Call("ed", "MoveFirst").Call("ed", "Edit").Put("fv", "Value", A.Str("edited")).Call("ed", "Update").Errors().ReadRow("ed")
                    .Release("fv").Release("fid").Release("fl").Call("ed", "Close").Release("ed");
                ReadTable(p, "db", "LK", "after");
                p.CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""))
                    .CallBind("sdb", "ws2", "OpenDatabase", A.In("src"), A.Bool(false), A.Bool(true));
                ReadTable(p, "sdb", "SRC", "source");
                p.Files("after", keep: true).Call("sdb", "Close").Finish("db");
            })
        { Fixtures = LinkedSrc };

        yield return new Probe(Card, "schema-change-under-cursor", ["R-LINK-1", "LINK-SCHEMA"],
            "Read/write: hold a dynaset on LK (first row read) and a TableDef Fields collection; through a second Database opened on the source copy add a column to SRC and drop column N; then continue reading the held recordset, read the held Fields, open a new recordset on LK, call RefreshLink, and read Fields and a recordset again. Errors after each step.",
            p =>
            {
                p.Engine().Files("before", keep: true).OpenDb("db", "main", false, false).GetBind("td", "db", "TableDefs").Item("lk", "td", A.Str("LK"))
                    .GetBind("hf", "lk", "Fields").Each("hf", "hff", 16, f => f.Get("hff", "Name").Get("hff", "Type"))
                    .OpenRs("held", "db", "SELECT * FROM LK ORDER BY ID", Dao.OpenDynaset).ReadRow("held")
                    .CallBind("sdb", "eng", "OpenDatabase", A.In("src"), A.Bool(false), A.Bool(false))
                    .Call("sdb", "Execute", A.Str("ALTER TABLE SRC ADD COLUMN X LONG"), A.I4(Dao.FailOnError)).Errors()
                    .Call("sdb", "Execute", A.Str("ALTER TABLE SRC DROP COLUMN N"), A.I4(Dao.FailOnError)).Errors()
                    .Call("held", "MoveNext").ReadRow("held").Errors()
                    .Each("hf", "hff2", 16, f => f.Get("hff2", "Name").Get("hff2", "Type")).Errors()
                    .OpenRs("fresh", "db", "SELECT * FROM LK ORDER BY ID", Dao.OpenSnapshot).ReadAll("fresh", 8, 8).Call("fresh", "Close").Release("fresh").Errors()
                    .Call("lk", "RefreshLink").Errors();
                LinkMeta(p, "lk");
                ReadTable(p, "db", "LK", "after");
                p.Call("held", "Close").Release("held").Call("sdb", "Close").Files("after", keep: true).Finish("db");
            })
        { Fixtures = LinkedSrc };

        // LINK-TXN and LINK-SESSION
        yield return new Probe(Card, "txn-local-and-link", ["R-LINK-1", "LINK-TXN"],
            "In Workspaces(0) run BeginTrans, one INSERT into local L and one into LK, count both and the source (read through a second Workspace), Rollback and count again; then the same with CommitTrans; then nested BeginTrans/BeginTrans/Rollback/CommitTrans. Counts of L, LK and the source after each step.",
            p =>
            {
                p.Engine().GetBind("ws", "eng", "Workspaces", A.I4(0)).CallBind("db", "ws", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str(""))
                    .CallBind("sdb", "ws2", "OpenDatabase", A.In("src"), A.Bool(false), A.Bool(true));
                Counts(p, "start");
                p.Call("ws", "BeginTrans");
                InsertBoth(p, 100);
                Counts(p, "t1-open");
                p.Call("ws", "Rollback").Errors();
                Counts(p, "t1-rolled-back");
                p.Call("ws", "BeginTrans");
                InsertBoth(p, 101);
                p.Call("ws", "CommitTrans").Errors();
                Counts(p, "t2-committed");
                p.Call("ws", "BeginTrans").Call("ws", "BeginTrans");
                InsertBoth(p, 102);
                p.Call("ws", "Rollback").Call("ws", "BeginTrans");
                InsertBoth(p, 103);
                p.Call("ws", "CommitTrans").Call("ws", "CommitTrans").Errors();
                Counts(p, "t3-nested");
                p.Call("sdb", "Close").Files("after", keep: true).Finish("db");
            })
        { Fixtures = LinkedSrc };

        yield return new Probe(Card, "session-two-workspaces", ["R-LINK-1", "LINK-SESSION"],
            "Two Workspaces in one process each open the linked file; ws2.IsolateODBCTrans is read, set to 1 and read again before its Database is opened. ws1 runs BeginTrans and inserts through LK; counts of LK are read through both Databases before and after ws1 Rollback, then after a second insert and CommitTrans.",
            p =>
            {
                p.Engine().GetBind("ws1", "eng", "Workspaces", A.I4(0)).Get("ws1", "IsolateODBCTrans")
                    .CallBind("ws2", "eng", "CreateWorkspace", A.Str("w2"), A.Str("Admin"), A.Str("")).Get("ws2", "IsolateODBCTrans")
                    .Put("ws2", "IsolateODBCTrans", A.I2(1)).Get("ws2", "IsolateODBCTrans")
                    .CallBind("d1", "ws1", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false))
                    .CallBind("d2", "ws2", "OpenDatabase", A.In(), A.Bool(false), A.Bool(false));
                CountLk(p, "start");
                p.Call("ws1", "BeginTrans").Call("d1", "Execute", A.Str("INSERT INTO LK (ID, V, N, D, M) VALUES (200, 's1', '1', 1, 'x')"), A.I4(Dao.FailOnError)).Errors();
                CountLk(p, "open");
                p.Call("ws1", "Rollback").Errors();
                CountLk(p, "rolled-back");
                p.Call("ws1", "BeginTrans").Call("d1", "Execute", A.Str("INSERT INTO LK (ID, V, N, D, M) VALUES (201, 's2', '1', 1, 'x')"), A.I4(Dao.FailOnError)).Errors()
                    .Call("ws1", "CommitTrans").Errors();
                CountLk(p, "committed");
                p.Call("d2", "Close").Call("d1", "Close").Files("after", keep: true).Finish();
            })
        { Fixtures = LinkedSrc };

        // Public views of a linked table.
        yield return new Probe(Card, "public-views", ["R-LINK-1", "LINK-META"],
            "Read-only: through DAO select Name, Type, Flags, Connect, ForeignName and Database from MSysObjects for types 1, 4 and 6, and the row of LK; through ADODB.Connection with the ACE provider read OpenSchema(adSchemaTables) and run a SELECT against LK. Errors after each; file bytes kept.",
            p =>
            {
                p.Engine().Files("before", keep: true).OpenDb()
                    .OpenRs("sys", "db", "SELECT Name, Type, Flags, Connect, ForeignName, [Database] FROM MSysObjects WHERE Type IN (1, 4, 6) ORDER BY Name", Dao.OpenSnapshot)
                    .ReadAll("sys", 32, 8).Call("sys", "Close").Release("sys").Errors()
                    .OpenRs("lkrow", "db", "SELECT * FROM MSysObjects WHERE Name = 'LK'", Dao.OpenSnapshot)
                    .ReadAll("lkrow", 4, 24).Call("lkrow", "Close").Release("lkrow").Errors()
                    .Call("db", "Close").Release("db");
                p.Create("ADODB.Connection", "cn").Put("cn", "Provider", A.Str(Ace)).GetBind("pr", "cn", "Properties")
                    .Item("pd", "pr", A.Str("Data Source")).Put("pd", "Value", A.In())
                    .Call("cn", "Open")
                    .CallBind("sch", "cn", "OpenSchema", A.I4(20)).ReadAll("sch", 64, 16).Call("sch", "Close").Release("sch")
                    .CallBind("ars", "cn", "Execute", A.Str("SELECT ID, V, D FROM LK ORDER BY ID")).ReadAll("ars", 8, 4).Call("ars", "Close").Release("ars")
                    .GetBind("aer", "cn", "Errors").Each("aer", "ae", 8, b => b.Get("ae", "Number").Get("ae", "Description")).Release("aer")
                    .Call("cn", "Close").Release("cn");
                p.Files("after", keep: true).Finish();
            })
        { Fixtures = LinkedSrc };
    }

    private static Probe SrcCreation(string name, string dType, int idBase, string? password, string what) =>
        new(Card, $"create-{name}", ["R-LINK-1", "LINK-META"],
            $"R-LINK-1 fixture: {what}. Rows: a/A, empty text, Null, a non-numeric N, duplicate D values, a 3,000-character memo; created by CreateDatabase(path, dbLangGeneral{(password is null ? "" : ";pwd=...")}, {Dao.Version120}).",
            p =>
            {
                p.Modules("before-create").Create().Modules("after-create")
                    .CallBind("db", "eng", "CreateDatabase", A.Out($"{Fx(name)}.accdb"), A.Str(Dao.LangGeneral + (password is null ? "" : ";pwd=" + password)), A.I4(Dao.Version120));
                SrcRows(p, "SRC", dType, idBase);
                ReadTable(p, "db", "SRC", "src");
                p.Call("db", "Close").Release("db").Release("eng").Modules("after-close").Files("after-close", keep: true);
            })
        { CreatesFixture = true };

    /// <summary>Create <paramref name="table"/> with the link-source schema and its four rows in database <c>db</c>.</summary>
    private static void SrcRows(P p, string table, string dType, int b)
    {
        string d10 = dType == "LONG" ? "10" : "'10'", d20 = dType == "LONG" ? "20" : "'20'";
        p.Call("db", "Execute", A.Str($"CREATE TABLE {table} (ID COUNTER CONSTRAINT PK_{table} PRIMARY KEY, V TEXT(20), N TEXT(20), D {dType}, M MEMO)"))
            .Call("db", "Execute", A.Str($"CREATE INDEX IX_{table}_D ON {table} (D)"))
            .Call("db", "Execute", A.Str($"INSERT INTO {table} (ID, V, N, D, M) VALUES ({b + 1}, 'a', '12', {d10}, 'short memo')"))
            .Call("db", "Execute", A.Str($"INSERT INTO {table} (ID, V, N, D, M) VALUES ({b + 2}, 'A', 'x', {d10}, '{new string('l', 3000)}')"))
            .Call("db", "Execute", A.Str($"INSERT INTO {table} (ID, V, N, D, M) VALUES ({b + 3}, '', NULL, {d10}, '')"))
            .Call("db", "Execute", A.Str($"INSERT INTO {table} (ID, V, N, D, M) VALUES ({b + 4}, NULL, '7', {d20}, NULL)"));
    }

    /// <summary>Name, Connect, SourceTableName, Attributes, Updatable, Fields and Indexes of a bound TableDef.</summary>
    private static void LinkMeta(P p, string tdf) => LinkMeta(p, tdf, tdf);

    private static void LinkMeta(P p, string tag, string tdf)
    {
        p.Get(tdf, "Name").Get(tdf, "Connect").Get(tdf, "SourceTableName").Get(tdf, "Attributes").Get(tdf, "Updatable")
            .GetBind("lf_" + tag, tdf, "Fields")
            .Each("lf_" + tag, "lfd_" + tag, 16, f => f.Get("lfd_" + tag, "Name").Get("lfd_" + tag, "Type").Get("lfd_" + tag, "Size").Get("lfd_" + tag, "Attributes")
                .Get("lfd_" + tag, "SourceField").Get("lfd_" + tag, "SourceTable").Get("lfd_" + tag, "Required").Get("lfd_" + tag, "OrdinalPosition"))
            .Release("lf_" + tag)
            .GetBind("li_" + tag, tdf, "Indexes")
            .Each("li_" + tag, "lix_" + tag, 8, i => i.Get("lix_" + tag, "Name").Get("lix_" + tag, "Primary").Get("lix_" + tag, "Unique").Get("lix_" + tag, "Foreign")
                .GetBind("lxf_" + tag, "lix_" + tag, "Fields").Each("lxf_" + tag, "lxfd_" + tag, 4, x => x.Get("lxfd_" + tag, "Name").Get("lxfd_" + tag, "Attributes")).Release("lxf_" + tag))
            .Release("li_" + tag)
            .Errors();
    }

    /// <summary>All rows of a table through a dynaset (linked tables cannot be opened as table-type recordsets).</summary>
    private static void ReadTable(P p, string db, string table, string tag) =>
        p.OpenRs("rt_" + tag, db, table, Dao.OpenDynaset).ReadAll("rt_" + tag, 16, 8).Call("rt_" + tag, "Close").Release("rt_" + tag).Errors();

    private static void InsertBoth(P p, int id) =>
        p.Call("db", "Execute", A.Str($"INSERT INTO L (ID, V, N, D, M) VALUES ({id}, 'tl', '1', 1, 'x')"), A.I4(Dao.FailOnError))
            .Call("db", "Execute", A.Str($"INSERT INTO LK (ID, V, N, D, M) VALUES ({id}, 'tk', '1', 1, 'x')"), A.I4(Dao.FailOnError)).Errors();

    private static void Counts(P p, string tag)
    {
        foreach ((string Db, string Table) in new[] { ("db", "L"), ("db", "LK"), ("sdb", "SRC") })
        {
            string rs = $"c_{tag}_{Db}_{Table}";
            p.OpenRs(rs, Db, $"SELECT COUNT(*) AS N FROM {Table}", Dao.OpenSnapshot).ReadRow(rs).Call(rs, "Close").Release(rs);
        }
    }

    private static void CountLk(P p, string tag)
    {
        foreach (string db in new[] { "d1", "d2" })
        {
            string rs = $"c_{tag}_{db}";
            p.OpenRs(rs, db, "SELECT COUNT(*) AS N FROM LK", Dao.OpenSnapshot).ReadRow(rs).Call(rs, "Close").Release(rs);
        }
    }
}
