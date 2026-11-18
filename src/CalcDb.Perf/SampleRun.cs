using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using CalcDb.Evidence;
using CalcDb.Oracle.Worker;

namespace CalcDb.Perf;

/// <summary>
/// One sample in its own new process (II-15 §29 F-204 "New process"): copy the fixture to a new path, activate the
/// implementation (ACE through the machine registration, CalcDb through its compatibility manifest and the host
/// bootstrap, as the oracle worker does), open with IDispatch, run the case's passes on its surface with raw QPC
/// readings around each, read CalcDb's cost counters outside every timed interval, close, and write the sample.
/// Open and Close use IDispatch on every surface; only the pass body uses the case's surface.
/// </summary>
internal static unsafe class SampleRun
{
    public static int Run(Options o)
    {
        string casePath = o.Require("case");
        PerformanceCase c = ReadCase(casePath);
        string variant = o.Require("variant");
        string implementation = variant.Split('~')[0];
        string outPath = o.Require("out");
        string repo = Program.RepositoryRoot();
        _ = Native.QueryPerformanceFrequency(out long frequency);
        long processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        var passes = new List<Pass>();
        FixtureCopy? copy = null;
        Interval? open = null, acquire = null, close = null;
        Shape? shape = null;
        RunConfiguration? config = null;
        string outcome = "completed";
        string? failure = null;
        string? workDir = null;
        CandidateDeployment? candidate = null;
        nint engine = 0, db = 0, rs = 0, rsTyped = 0, field = 0, fields = 0;
        int coHr = Native.CoInitializeEx(0, 2); // COINIT_APARTMENTTHREADED: the priority caller is one STA
        try
        {
            if (coHr < 0)
            {
                throw new InvalidOperationException("CoInitializeEx failed: " + Native.Hr(coHr));
            }
            workDir = Path.Combine(Path.GetTempPath(), "calcdb-perf", o.Require("run-id"), $"b{o.Require("block")}-p{o.Require("position")}");
            Directory.CreateDirectory(workDir);
            copy = CopyFixture(repo, c.Fixture, workDir);
            if (implementation == "calcdb")
            {
                string deployment = Path.GetFullPath(o.Get("deployment") ?? Path.Combine(repo, "artifacts", "native"));
                string module = Path.Combine(deployment, "CalcDb.dll");
                var errors = new List<string>();
                candidate = CandidateDeployment.Activate(new ImplementationSelection("calcdb-local", deployment, Hashing.Sha256File(module)), errors);
                if (candidate is null)
                {
                    throw new InvalidOperationException(string.Join(" ", errors));
                }
            }
            else if (implementation != "ace")
            {
                throw new InvalidDataException("unknown implementation " + implementation);
            }
            int hr = Native.CLSIDFromProgID("DAO.DBEngine.120", out Guid clsid);
            hr = hr < 0 ? hr : Native.CoCreateInstance(clsid, 0, 1, Native.IidIDispatch, out engine);
            if (hr < 0)
            {
                throw new MeasuredCallException("create DAO.DBEngine.120", hr);
            }
            string engineModule = Native.ModuleOfObject(engine) ?? "(unknown)";
            config = Configuration(implementation, engineModule, o.Get("desktop") ?? "unknown");

            long t0 = Native.Qpc();
            db = InvokeObject(engine, "OpenDatabase", [Bstr(copy.Destination), Bool(false), Bool(true)]);
            rs = InvokeObject(db, "OpenRecordset", [Bstr(c.Table), I4(c.RecordsetType)]);
            long t1 = Native.Qpc();
            open = new Interval(t0, t1);

            t0 = Native.Qpc();
            fields = InvokePropertyObject(rs, "Fields");
            field = InvokeObject(fields, "Item", [Bstr(c.Field)], Native.DispatchMethod | Native.DispatchPropertyGet);
            t1 = Native.Qpc();
            acquire = new Interval(t0, t1);
            int fieldType = (int)InvokeScalar(field, "Type");
            int fieldSize = (int)InvokeScalar(field, "Size");
            nint typedField = field, typedFields = fields;
            if (c.Surface == "native-vtable/x64")
            {
                Check(Native.QueryInterface(rs, VtableLane.IidRecordset, out rsTyped), "QueryInterface Recordset");
                Check(Native.QueryInterface(field, VtableLane.IidField, out typedField), "QueryInterface Field");
                Check(Native.QueryInterface(fields, VtableLane.IidFields, out typedFields), "QueryInterface Fields");
            }
            // VBA passes a string literal as a BSTR and a numeric literal 0 as VT_I2; one index for the whole run.
            Variant index = c.Body == "named" ? Bstr(c.Field) : new Variant { Vt = Native.VtI2, Int64 = c.FieldOrdinal };
            try
            {
                for (int p = 0; p < c.Passes; p++)
                {
                    if (p > 0)
                    {
                        InvokeVoid(rs, "MoveFirst");
                    }
                    CounterSnapshot before = CounterSnapshot.Read(implementation);
                    PassResult result;
                    long start, end;
                    if (c.Surface == "native-vtable/x64")
                    {
                        var lane = new VtableLane(rsTyped);
                        start = Native.Qpc();
                        result = Bodies.Run(ref lane, c, typedField, typedFields, &index);
                        end = Native.Qpc();
                    }
                    else
                    {
                        var lane = new DispatchLane(rs);
                        start = Native.Qpc();
                        result = Bodies.Run(ref lane, c, field, fields, &index);
                        end = Native.Qpc();
                    }
                    CounterSnapshot after = CounterSnapshot.Read(implementation);
                    passes.Add(new Pass(p, new Interval(start, end), result.Rows, result.Sink, result.TextUnits, result.Entries, result.Releases, CounterSnapshot.Vector(before, after)));
                }
            }
            finally
            {
                if (index.Vt == Native.VtBstr)
                {
                    Native.SysFreeString(index.Pointer);
                }
                if (typedField != field)
                {
                    _ = Native.Release(typedField);
                }
                if (typedFields != fields)
                {
                    _ = Native.Release(typedFields);
                }
            }
            shape = new Shape(passes.Count > 0 ? passes[0].Rows : 0, fieldType, fieldSize, c.Fixture.Length, c.Fixture.Length / 4096, c.RecordsetType);

            t0 = Native.Qpc();
            Release(ref field);
            Release(ref fields);
            Release(ref rsTyped);
            InvokeVoid(rs, "Close");
            Release(ref rs);
            InvokeVoid(db, "Close");
            Release(ref db);
            t1 = Native.Qpc();
            close = new Interval(t0, t1);
        }
        catch (Exception e) when (e is MeasuredCallException or InvalidOperationException or InvalidDataException or IOException)
        {
            outcome = "failed";
            failure = e.Message;
        }
        finally
        {
            Release(ref field);
            Release(ref fields);
            Release(ref rsTyped);
            Release(ref rs);
            Release(ref db);
            Release(ref engine);
            candidate?.Dispose();
            if (coHr >= 0)
            {
                Native.CoUninitialize();
            }
        }
        var sample = new Sample(Sample.CurrentSchema, o.Require("run-id"), c.Name, int.Parse(o.Require("block"), System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(o.Require("position"), System.Globalization.CultureInfo.InvariantCulture), variant, outcome, frequency, processStart, passes)
        {
            Failure = failure, Copy = copy, Open = open, Acquire = acquire, Close = close, Shape = shape, Configuration = config,
        };
        EvidenceJson.WriteAtomic(outPath, sample);
        if (workDir is not null)
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException)
            {
                // A lock file still held is left for the next run's directory; the sample is already written.
            }
        }
        return outcome == "completed" ? 0 : 1;
    }

    public static PerformanceCase ReadCase(string path)
    {
        PerformanceCase c = EvidenceJson.Read<PerformanceCase>(path);
        c.Validate();
        return c;
    }

    private static FixtureCopy CopyFixture(string repo, CaseFixture fixture, string workDir)
    {
        string source = Path.Combine(repo, "evidence", "artifacts", fixture.Sha256);
        string destination = Path.Combine(workDir, fixture.FileName);
        long t0 = Native.Qpc();
        File.Copy(source, destination, overwrite: false);
        long t1 = Native.Qpc();
        string sha = Hashing.Sha256File(destination);
        if (sha != fixture.Sha256)
        {
            throw new InvalidDataException($"fixture copy {destination} is {sha}, the case names {fixture.Sha256}");
        }
        return new FixtureCopy(source, destination, sha, new Interval(t0, t1));
    }

    private static RunConfiguration Configuration(string implementation, string engineModule, string desktop)
    {
        string client = Environment.ProcessPath ?? "(unknown)";
        string source = typeof(SampleRun).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "CalcDbSource")?.Value ?? "(unknown)";
        return new RunConfiguration(
            implementation,
            Identity(engineModule),
            Identity(client),
            source,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "(unknown)",
            $"server {GCSettings.IsServerGC}, latency {GCSettings.LatencyMode}",
            "client: .NET JIT with tiered compilation as configured: " + (AppContext.GetData("System.Runtime.TieredCompilation") ?? "default") + ", PGO " + (AppContext.GetData("System.Runtime.TieredPGO") ?? "default") + "; CalcDb.dll: NativeAOT",
            implementation == "calcdb" ? "CalcDb cost counters always on (plain thread-local increments, no formatting); read outside timed intervals" : "no engine counters; client counts only",
            "new process; fixture copied to a new path (not a cold-cache claim); engine session new at open; OS cache state not controlled",
            desktop);
    }

    private static ModuleIdentity Identity(string path) =>
        File.Exists(path) ? new ModuleIdentity(path, Hashing.Sha256File(path)) { FileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion } : new ModuleIdentity(path, "(missing)");

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new MeasuredCallException(what, hr);
        }
    }

    private static void Release(ref nint p)
    {
        if (p != 0)
        {
            _ = Native.Release(p);
            p = 0;
        }
    }

    private static Variant Bstr(string text) => new() { Vt = Native.VtBstr, Pointer = Native.SysAllocString(text) };

    private static Variant Bool(bool value) => new() { Vt = Native.VtBool, Int64 = value ? -1 : 0 };

    private static Variant I4(int value) => new() { Vt = Native.VtI4, Int64 = value };

    private static int Id(nint target, string member)
    {
        Check(Native.GetId(target, member, out int id), "GetIDsOfNames " + member);
        return id;
    }

    /// <summary>Invoke a method that returns an object; BSTR arguments are freed after the call.</summary>
    private static nint InvokeObject(nint target, string member, Variant[] args, ushort flags = Native.DispatchMethod)
    {
        Variant r = default;
        int hr = Native.Invoke(target, Id(target, member), flags, args, &r);
        foreach (Variant a in args)
        {
            if (a.Vt == Native.VtBstr)
            {
                Native.SysFreeString(a.Pointer);
            }
        }
        Check(hr, member);
        return r.Pointer;
    }

    private static nint InvokePropertyObject(nint target, string member)
    {
        Variant r = default;
        Check(Native.Invoke(target, Id(target, member), Native.DispatchPropertyGet, [], &r), member);
        return r.Pointer;
    }

    private static long InvokeScalar(nint target, string member)
    {
        Variant r = default;
        Check(Native.Invoke(target, Id(target, member), Native.DispatchPropertyGet, [], &r), member);
        Variant i4 = default;
        Check(Native.VariantChangeType(&i4, &r, 0, Native.VtI4), member + " as Long");
        _ = Native.VariantClear(&r);
        return (int)i4.Int64;
    }

    private static void InvokeVoid(nint target, string member)
    {
        Variant r = default;
        Check(Native.Invoke(target, Id(target, member), Native.DispatchMethod, [], &r), member);
        _ = Native.VariantClear(&r);
    }
}

/// <summary>
/// CalcDb's cost counters read through its <c>CalcDbCostCounters</c> export (calcdb.cost-counters/1). ACE has no such
/// counters: its vector names them unavailable (T-EVID-41), never zero.
/// </summary>
internal sealed unsafe class CounterSnapshot
{
    public const string AceUnavailable = "engine work (calls, allocations, decodes, page I/O): ACE exposes no counters and native attribution is not available (T-EVID-41)";

    private CounterSnapshot(string? schema, List<(string Name, string Unit, string Exactness, string Scope, long Value)> values)
    {
        Schema = schema;
        Values = values;
    }

    public string? Schema { get; }

    public List<(string Name, string Unit, string Exactness, string Scope, long Value)> Values { get; }

    public static CounterSnapshot Read(string implementation)
    {
        if (implementation != "calcdb")
        {
            return new CounterSnapshot(null, []);
        }
        nint module = Native.GetModuleHandleW("CalcDb.dll");
        nint export = module == 0 ? 0 : Native.GetProcAddress(module, "CalcDbCostCounters");
        if (export == 0)
        {
            throw new InvalidOperationException("CalcDb.dll is not loaded or has no CalcDbCostCounters export");
        }
        var read = (delegate* unmanaged<byte*, int, int*, int>)export;
        int needed = 0;
        _ = read(null, 0, &needed);
        byte[] buffer = new byte[needed];
        int hr;
        fixed (byte* b = buffer)
        {
            hr = read(b, buffer.Length, &needed);
        }
        if (hr < 0)
        {
            throw new InvalidOperationException("CalcDbCostCounters failed: " + Native.Hr(hr));
        }
        using JsonDocument doc = JsonDocument.Parse(buffer);
        var values = new List<(string, string, string, string, long)>();
        foreach (JsonElement e in doc.RootElement.GetProperty("counters").EnumerateArray())
        {
            values.Add((e.GetProperty("name").GetString()!, e.GetProperty("unit").GetString()!, e.GetProperty("exactness").GetString()!, e.GetProperty("scope").GetString()!, e.GetProperty("value").GetInt64()));
        }
        return new CounterSnapshot(doc.RootElement.GetProperty("schema").GetString(), values);
    }

    public static CostVector Vector(CounterSnapshot before, CounterSnapshot after)
    {
        if (before.Schema is null)
        {
            return new CostVector(CostVector.NoCounters, [], [AceUnavailable]);
        }
        var readings = new List<CounterReading>();
        for (int i = 0; i < before.Values.Count && i < after.Values.Count; i++)
        {
            (string name, string unit, string exactness, string scope, long start) = before.Values[i];
            readings.Add(new CounterReading(name, unit, exactness, scope, start, after.Values[i].Value));
        }
        // CalcDb subtracts the counter export's own managed allocations; the client process's allocations are not here.
        return new CostVector(before.Schema, readings, ["native allocator capacity and frees by the client (R-STRING-1: external-free visibility)", "CPU branches and cache misses (no hardware profile in this lane)"]);
    }
}
