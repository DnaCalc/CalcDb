using System.Globalization;

using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Storage;
using CalcDb.Values.Scalar;
using Xunit.Abstractions;

namespace CalcDb.Expressions.Tests;

/// <summary>
/// The Find records replayed on the ISAM populated cursor over their own fixture (R-CURSOR-1 CUR-FIND; qualified.md
/// "R-EXPR-1 EXPR-BIND, EXPR-INVENTORY (Find criteria)"). Every expected value is read from the record: the Find's
/// error number and message, NoMatch, BOF, EOF, AbsolutePosition and the ID read after it. A not-modelled outcome ends
/// the replay of that recordset (its state is then unknown) and is counted, never matched. The COM argument channel
/// (Null, Missing) is the DAO adapter's and is left to the corpus comparison.
/// </summary>
public sealed class FindReplayTests(ITestOutputHelper output)
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    public static TheoryData<string> Records => new()
    {
        "r-cursor-1/pos-find/r-cursor-1.pos-find.ded4e4c9ef.a1.json",
        "r-cursor-1b/find-all-directions-other-types/r-cursor-1b.find-all-directions-other-types.ea2bf97865.a1.json",
        "r-cursor-1b/find-date-forms/r-cursor-1b.find-date-forms.ee7428cf5c.a1.json",
        "r-cursor-1b/find-duplicates-sequence/r-cursor-1b.find-duplicates-sequence.b5b83430ed.a1.json",
        "r-cursor-1b/find-empty-and-ends/r-cursor-1b.find-empty-and-ends.5c5b94a267.a1.json",
        "r-cursor-1b/find-expression-forms/r-cursor-1b.find-expression-forms.fb106aaff6.a1.json",
        "r-cursor-1b/find-like-patterns/r-cursor-1b.find-like-patterns.933b207186.a1.json",
        "r-cursor-1b/find-null-forms/r-cursor-1b.find-null-forms.8ba762ea70.a1.json",
        "r-cursor-1b/find-text-quotes/r-cursor-1b.find-text-quotes.48a175800c.a1.json",
        "r-fix-cursor-dao/find-like-patterns-unindexed/r-fix-cursor-dao.find-like-patterns-unindexed.2112df40bc.a1.json",
        "r-fix-cursor-dao/find-text-quotes-unindexed/r-fix-cursor-dao.find-text-quotes-unindexed.568398af80.a1.json",
    };

    [Theory]
    [MemberData(nameof(Records))]
    public void Find_record_replays_on_the_cursor(string record)
    {
        var replay = new Replay(record);
        replay.Run();
        output.WriteLine($"{record}: {replay.Checked} checks matched, {replay.Mismatches.Count} mismatched, {replay.Open.Count} not modelled");
        foreach (string open in replay.Open)
        {
            output.WriteLine("  open: " + open);
        }
        foreach (string m in replay.Mismatches)
        {
            output.WriteLine("  MISMATCH: " + m);
        }
        Assert.Empty(replay.Mismatches);
        Assert.True(replay.Checked > 0);
    }

    internal static string Root()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        return root;
    }

    /// <summary>A database image from the artifact store: its cache, catalog and the means to open a table's populated cursor.</summary>
    internal sealed class Fixture
    {
        private readonly PageCache _cache;
        private readonly ImagePageSource _image;
        private readonly CatalogSnapshot _catalog;

        public Fixture(string sha256)
        {
            byte[] file = ArtifactStore.ForRepository(Root()).ReadAll(sha256);
            _image = new ImagePageSource(file, D.PageSize);
            _cache = new PageCache(_image, 1, D.PageSize, 256);
            _catalog = CatalogBootstrap.Open(_cache, D, _image.PageCount, out _)!;
            Header = DatabaseHeader.Decode(file.AsSpan(0, D.PageSize), D);
        }

        public DatabaseHeader Header { get; }

        public TableDefinition? Table(string name)
        {
            CatalogEntry? entry = _catalog.LocalTables(D).FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
            return entry is null ? null : TableDefDecoder.Decode(TableDefSegmentView.Open((uint)entry.Id, p => _cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!, (uint)entry.Id, D, out _);
        }

        public PopulationCursor Open(TableDefinition table) => PopulationCursor.Open(_cache, D, table, _image.PageCount, forwardOnly: false, out _)!;

        public ExpressionContext Context() => new(7177, TextCompareMode.Database, DatabaseCollation.For(Header.SortOrder), ExpressionUse.Find);

        private static ReadOnlyMemory<byte> Copy(PagePin pin)
        {
            using (pin)
            {
                return pin.Bytes.ToArray();
            }
        }
    }

    private sealed class Replay(string record)
    {
        private PopulationCursor? _cursor;
        private TableDefinition? _table;
        private ExpressionSchema? _schema;
        private FindCriteria? _criteria;
        private string? _field;

        public int Checked { get; private set; }

        public List<string> Mismatches { get; } = [];

        public List<string> Open { get; } = [];

        public void Run()
        {
            EvidenceRecord r = EvidenceJson.Read<EvidenceRecord>(Path.Combine(Root(), "evidence", record));
            IReadOnlyList<CaptureEvent> events = r.Capture!.Events;
            string fixture = events.OfType<FilesEvent>().First().Files.First(f => f.RelativePath.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase)).Sha256!;
            var db = new Fixture(fixture);
            ExpressionContext context = db.Context();
            foreach (InvokeEvent e in events.OfType<InvokeEvent>())
            {
                if (e.Member == "OpenRecordset")
                {
                    Close();
                    int type = int.Parse(e.Args[1].Integer!, CultureInfo.InvariantCulture);
                    _table = type is 2 or 4 ? db.Table(e.Args[0].Text!) : null;
                    if (_table is not null)
                    {
                        _cursor = db.Open(_table);
                        _schema = new ExpressionSchema(_table);
                    }
                    continue;
                }
                if (_cursor is null)
                {
                    continue;
                }
                string where = $"#{e.Seq} {e.Target}.{e.Member}({string.Join(", ", e.Args.Select(a => a.Text ?? a.Integer ?? a.VtName))})";
                switch (e.Member)
                {
                    case "FindFirst" or "FindLast" or "FindNext" or "FindPrevious":
                        Find(e, context, where);
                        break;
                    case "MoveFirst":
                        _cursor.MoveFirst();
                        break;
                    case "MoveLast":
                        _cursor.MoveLast();
                        break;
                    case "MoveNext":
                        _cursor.MoveNext();
                        break;
                    case "MovePrevious":
                        _cursor.MovePrevious();
                        break;
                    case "Move":
                        _cursor.Move(long.Parse(e.Args[0].Integer!, CultureInfo.InvariantCulture));
                        break;
                    case "NoMatch":
                        Check(where, Bool(e), _cursor.NoMatch);
                        break;
                    case "BOF":
                        Check(where, Bool(e), _cursor.Bof);
                        break;
                    case "EOF":
                        Check(where, Bool(e), _cursor.Eof);
                        break;
                    case "AbsolutePosition" when e.ExcepInfo is null:
                        Check(where, int.Parse(e.Result!.Integer!, CultureInfo.InvariantCulture), _cursor.Ordinal ?? -1);
                        break;
                    case "Item":
                        _field = e.Args[0].Text;
                        break;
                    case "Value" when _field is not null:
                        Value(e, where);
                        break;
                    case "Close":
                        Close();
                        break;
                }
            }
            Close();
        }

        private void Find(InvokeEvent e, ExpressionContext context, string where)
        {
            CapturedValue arg = e.Args[0];
            string? text = arg.Vt switch
            {
                8 => arg.Text ?? "",
                11 => arg.Integer == "0" ? "False" : "True", // VARIANT_ALPHABOOL, as the DAO adapter coerces it (CUR-FIND)
                0 => "",
                3 or 5 => arg.Display ?? arg.Integer,
                _ => null, // Null and Missing fail in the argument binder, which moves nothing
            };
            if (text is null)
            {
                return;
            }
            FindDirection direction = Enum.Parse<FindDirection>(e.Member["Find".Length..]);
            ExpressionFailure? failure = null;
            if (_criteria?.Text != text)
            {
                _criteria?.Dispose();
                _criteria = FindCriteria.Bind(text, _schema!, context, out failure);
            }
            if (failure is { IsNotModelled: true })
            {
                Stop(where, failure.ToString());
                return;
            }
            FindResult result = _criteria is null ? _cursor!.CriteriaFailed(direction) : _cursor!.Find(direction, _criteria);
            if (result.Outcome == FindOutcome.Failed && _criteria?.LastFailure is { IsNotModelled: true } open)
            {
                Stop(where, open.ToString());
                return;
            }
            if (result.Outcome == FindOutcome.NotModelled)
            {
                Stop(where, "position " + result.Open);
                return;
            }
            ExpressionFailure? actual = result.Outcome == FindOutcome.Failed ? failure ?? _criteria!.LastFailure : null;
            string expected = e.ExcepInfo is { } x ? $"{x.Scode} {x.Description?.Text}" : "success";
            string got = actual is null ? "success" : $"0x{unchecked((uint)(int)0x800A0000 | (uint)actual.Number):X8} {actual.Message}";
            Check(where, expected, got);
        }

        private void Value(InvokeEvent e, string where)
        {
            ColumnDescriptor column = _table!.Columns.Single(c => string.Equals(c.Name, _field, StringComparison.OrdinalIgnoreCase));
            FieldResult read = _cursor!.Read(column);
            if (e.ExcepInfo is { } x)
            {
                Check(where, x.Scode == "0x800A0BCD" ? "no current record" : x.Scode, read.Failure == CursorFailure.NoCurrentRecord ? "no current record" : read.Value.ToString());
                return;
            }
            if (e.Result!.Vt == 3)
            {
                Check(where, e.Result.Integer!, read.Value.Integer.ToString(CultureInfo.InvariantCulture));
            }
        }

        private void Check<T>(string where, T expected, T actual)
        {
            if (EqualityComparer<T>.Default.Equals(expected, actual))
            {
                Checked++;
            }
            else
            {
                Mismatches.Add($"{where}: ACE {expected}, CalcDb {actual}");
            }
        }

        private static bool Bool(InvokeEvent e) => e.Result!.Integer != "0";

        private void Stop(string where, string why)
        {
            Open.Add($"{where}: {why}");
            Close();
        }

        private void Close()
        {
            _cursor?.Dispose();
            _cursor = null;
            _criteria?.Dispose();
            _criteria = null;
            _field = null;
        }
    }
}
