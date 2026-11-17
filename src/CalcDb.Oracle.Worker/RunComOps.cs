using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Worker;

/// <summary>
/// The worker operations beyond plain IDispatch calls: IEnumVARIANT enumeration, by-reference arguments, raw activation
/// and type-library creation, a recording event sink, and suspending another participant. Each records every COM call
/// it makes, in order, with its HRESULT; none dismisses, retries or interprets an outcome.
/// </summary>
internal sealed unsafe partial class Run
{
    private string _currentPath = "";
    private readonly Dictionary<string, (nint Point, uint Cookie, NativeObjects.Sink Sink)> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<nint>> _suspended = new(StringComparer.Ordinal);
    private readonly List<NativeObjects.Outer> _outers = [];

    /// <summary>A by-reference argument's referenced storage: a VARIANT (untyped) or a 16-byte payload (typed).</summary>
    private readonly record struct ByRefSlot(int Index, nint Buffer, bool Typed, ushort InnerVt);

    private Variant MakeByRef(ByRefArg b, int index, List<ByRefSlot> byRefs)
    {
        Variant inner = BuildOne(b.Inner, index, byRefs);
        if (!b.Typed)
        {
            nint buffer = (nint)NativeMemory.AllocZeroed((nuint)sizeof(Variant));
            *(Variant*)buffer = inner;
            byRefs.Add(new ByRefSlot(index, buffer, false, inner.Vt));
            return new Variant { Vt = 0x4000 | 12, Ptr = buffer };
        }
        nint payload = (nint)NativeMemory.AllocZeroed(16);
        if (inner.Vt == 14)
        {
            Buffer.MemoryCopy(&inner, (void*)payload, 16, 16);
        }
        else
        {
            *(long*)payload = inner.I8;
        }
        byRefs.Add(new ByRefSlot(index, payload, true, inner.Vt));
        return new Variant { Vt = (ushort)(0x4000 | inner.Vt), Ptr = payload };
    }

    private CapturedValue CaptureByRefSlot(ByRefSlot slot)
    {
        if (!slot.Typed)
        {
            return _dispatch.CaptureValue((Variant*)slot.Buffer);
        }
        var v = new Variant { Vt = (ushort)(0x4000 | slot.InnerVt), Ptr = slot.Buffer };
        return _dispatch.CaptureMaybeByRef(&v, out _);
    }

    /// <summary>Frees what the referenced storage holds after the call, including a BSTR the callee may have replaced.</summary>
    private static void FreeByRefs(List<ByRefSlot> byRefs)
    {
        foreach (ByRefSlot slot in byRefs)
        {
            if (!slot.Typed)
            {
                _ = Native.VariantClear((Variant*)slot.Buffer);
            }
            else if (slot.InnerVt == 8)
            {
                Native.SysFreeString(*(nint*)slot.Buffer);
            }
            NativeMemory.Free((void*)slot.Buffer);
        }
    }

    // IEnumVARIANT vtable slots: Next 3, Skip 4, Reset 5, Clone 6.
    private static int EnumNext(nint e, uint count, Variant* items, uint* fetched) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, Variant*, uint*, int>)(*(nint**)e)[3])(e, count, items, fetched);

    private static int EnumSkip(nint e, uint count) => ((delegate* unmanaged[Stdcall]<nint, uint, int>)(*(nint**)e)[4])(e, count);

    private static int EnumReset(nint e) => ((delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)e)[5])(e);

    private static int EnumClone(nint e, nint* clone) => ((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)e)[6])(e, clone);

    private void DoEnumerate(Enumerate en, string path)
    {
        if (!_bound.TryGetValue(en.Target, out nint dispatch))
        {
            _events.Add(new SkippedEvent { Seq = ++_seq, ProgramPath = path, Reason = $"Target '{en.Target}' is not bound." });
            return;
        }
        Trace(path, $"{en.Target}._NewEnum");
        // DISPID_NEWENUM with DISPATCH_METHOD | DISPATCH_PROPERTYGET, as Automation clients call it.
        Dispatch.Outcome outcome = _dispatch.Invoke(dispatch, -4, 3, [], _question.ObservationRecipe);
        nint enumerator = 0;
        string? qiHr = null;
        if (outcome.Hr >= 0 && outcome.ResultObject != 0)
        {
            qiHr = Native.Hr(Native.QueryInterface(outcome.ResultObject, Native.IidIEnumVARIANT, out enumerator));
            _dispatch.ReleaseOwned(outcome.ResultObject);
        }
        else
        {
            _anyFailure = true;
        }
        _events.Add(new EnumEvent { Seq = ++_seq, ProgramPath = path, Target = en.Target, NewEnumHresult = Native.Hr(outcome.Hr), NewEnumResult = outcome.Result, EnumVariantHresult = qiHr, Steps = [] });
        if (enumerator == 0)
        {
            return;
        }
        bool ended = false;
        for (int i = 0; i < en.MaxItems; i++)
        {
            string itemPath = $"{path}[{i.ToString(CultureInfo.InvariantCulture)}].";
            Variant v = default;
            uint fetched = 0;
            Trace(itemPath + "next", "IEnumVARIANT.Next");
            int hr = EnumNext(enumerator, 1, &v, &fetched);
            CapturedValue? item = fetched > 0 ? _dispatch.CaptureValue(&v) : null;
            _events.Add(new EnumEvent { Seq = ++_seq, ProgramPath = itemPath + "next", Target = en.Target, NewEnumHresult = Native.Hr(outcome.Hr), Steps = [new EnumStep("Next", Native.Hr(hr)) { Fetched = fetched, Item = item }] });
            if (hr != 0 || fetched == 0)
            {
                _ = Native.VariantClear(&v);
                ended = true;
                break;
            }
            if ((v.Vt == 9 || v.Vt == 13) && v.Ptr != 0)
            {
                Bind(en.Bind, v.Ptr); // ownership of the item's reference moves to the binding
            }
            else
            {
                _ = Native.VariantClear(&v);
            }
            RunProgram(en.Body, itemPath);
            if (_bound.ContainsKey(en.Bind))
            {
                DoRelease(en.Bind, itemPath + "release", isImplicit: true);
            }
        }
        var probe = new List<EnumStep>();
        if (!ended)
        {
            probe.Add(new EnumStep("Next (after the declared maximum)", "not called"));
        }
        else
        {
            Variant past = default;
            uint pastFetched = 0;
            int pastHr = EnumNext(enumerator, 1, &past, &pastFetched);
            probe.Add(new EnumStep("Next past the end", Native.Hr(pastHr)) { Fetched = pastFetched, Item = pastFetched > 0 ? _dispatch.CaptureValue(&past) : null });
            _ = Native.VariantClear(&past);
        }
        probe.Add(new EnumStep("Reset", Native.Hr(EnumReset(enumerator))));
        Variant again = default;
        uint againFetched = 0;
        int againHr = EnumNext(enumerator, 1, &again, &againFetched);
        probe.Add(new EnumStep("Next after Reset", Native.Hr(againHr)) { Fetched = againFetched, Item = againFetched > 0 ? _dispatch.CaptureValue(&again) : null });
        _ = Native.VariantClear(&again);
        probe.Add(new EnumStep("Skip(1)", Native.Hr(EnumSkip(enumerator, 1))));
        nint clone = 0;
        int cloneHr = EnumClone(enumerator, &clone);
        probe.Add(new EnumStep("Clone", Native.Hr(cloneHr)));
        if (cloneHr >= 0 && clone != 0)
        {
            Native.Release(clone);
        }
        Native.Release(enumerator);
        _events.Add(new EnumEvent { Seq = ++_seq, ProgramPath = path + ".probe", Target = en.Target, NewEnumHresult = Native.Hr(outcome.Hr), Steps = probe });
    }

    // IClassFactory: CreateInstance 3, LockServer 4.
    private static int FactoryCreate(nint f, nint outer, Guid* iid, nint* ppv) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)f)[3])(f, outer, iid, ppv);

    private static int FactoryLock(nint f, int lockIt) => ((delegate* unmanaged[Stdcall]<nint, int, int>)(*(nint**)f)[4])(f, lockIt);

    private void DoActivate(Activate a, string path)
    {
        var steps = new List<string>();
        Guid clsid;
        if (a.Class.StartsWith('{'))
        {
            clsid = Guid.Parse(a.Class);
        }
        else
        {
            int hr = Native.CLSIDFromProgID(a.Class, out clsid);
            steps.Add("CLSIDFromProgID " + Native.Hr(hr));
            if (hr < 0)
            {
                _anyFailure = true;
                _events.Add(new ActivationEvent { Seq = ++_seq, ProgramPath = path, Route = a.Route, Class = a.Class, ClsCtx = a.ClsCtx, Iid = a.Iid, Aggregate = a.Aggregate, Steps = steps });
                return;
            }
        }
        Guid iid = Guid.Parse(a.Iid);
        NativeObjects.Outer? outer = null;
        if (a.Aggregate)
        {
            outer = new NativeObjects.Outer();
            _outers.Add(outer);
        }
        nint obj = 0;
        Trace(path, $"activate {a.Route} {a.Class}");
        if (a.Route == "cocreate")
        {
            int hr = Native.CoCreateInstance(clsid, outer?.Pointer ?? 0, a.ClsCtx, iid, out obj);
            steps.Add("CoCreateInstance " + Native.Hr(hr));
        }
        else
        {
            int hr = Native.CoGetClassObject(clsid, a.ClsCtx, 0, Native.IidIClassFactory, out nint factory);
            steps.Add("CoGetClassObject(IClassFactory) " + Native.Hr(hr));
            if (hr >= 0 && factory != 0)
            {
                steps.Add("LockServer(TRUE) " + Native.Hr(FactoryLock(factory, 1)));
                nint created = 0;
                steps.Add("CreateInstance " + Native.Hr(FactoryCreate(factory, outer?.Pointer ?? 0, &iid, &created)));
                obj = created;
                steps.Add("LockServer(FALSE) " + Native.Hr(FactoryLock(factory, 0)));
                Native.Release(factory);
            }
        }
        CapturedValue? captured = null;
        string? bound = null;
        if (obj != 0)
        {
            // Bind only a non-aggregated object that answers IDispatch; an aggregated inner object is released here,
            // because its delegating interfaces call back into the worker's outer.
            int qi = Native.QueryInterface(obj, Native.IidIDispatch, out nint disp);
            steps.Add("QueryInterface(IDispatch) " + Native.Hr(qi));
            if (qi >= 0 && disp != 0 && !a.Aggregate)
            {
                Bind(a.Bind, disp);
                bound = a.Bind;
                var v = new Variant { Vt = 9, Ptr = disp };
                captured = _dispatch.CaptureValue(&v) with { Object = a.Bind };
            }
            else if (qi >= 0 && disp != 0)
            {
                Native.Release(disp);
            }
            steps.Add("Release " + Native.Release(obj).ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            _anyFailure = true;
        }
        _events.Add(new ActivationEvent
        {
            Seq = ++_seq, ProgramPath = path, Route = a.Route, Class = a.Class, Clsid = clsid.ToString("B").ToUpperInvariant(), ClsCtx = a.ClsCtx,
            Iid = a.Iid, Aggregate = a.Aggregate, Steps = steps, OuterQueries = outer?.Queries.ToList(), Object = captured, Bind = bound,
        });
    }

    private void DoTypeCreate(TypeCreate t, string path)
    {
        var steps = new List<string>();
        if (!_bound.TryGetValue(t.Target, out nint dispatch))
        {
            _events.Add(new SkippedEvent { Seq = ++_seq, ProgramPath = path, Reason = $"Target '{t.Target}' is not bound." });
            return;
        }
        nint typeInfo = 0, typeLib = 0, coclass = 0, obj = 0;
        CapturedValue? captured = null;
        string? bound = null;
        Trace(path, $"typeCreate {t.Clsid}");
        // IDispatch::GetTypeInfo (slot 4), ITypeInfo::GetContainingTypeLib (18), ITypeLib::GetTypeInfoOfGuid (6),
        // ITypeInfo::CreateInstance (16).
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, int, nint*, int>)(*(nint**)dispatch)[4])(dispatch, 0, _question.InvokeLcid, &typeInfo);
        steps.Add("IDispatch.GetTypeInfo " + Native.Hr(hr));
        if (hr >= 0 && typeInfo != 0)
        {
            uint index = 0;
            hr = ((delegate* unmanaged[Stdcall]<nint, nint*, uint*, int>)(*(nint**)typeInfo)[18])(typeInfo, &typeLib, &index);
            steps.Add("ITypeInfo.GetContainingTypeLib " + Native.Hr(hr));
        }
        if (hr >= 0 && typeLib != 0)
        {
            Guid clsid = Guid.Parse(t.Clsid);
            hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)typeLib)[6])(typeLib, &clsid, &coclass);
            steps.Add("ITypeLib.GetTypeInfoOfGuid " + Native.Hr(hr));
        }
        if (hr >= 0 && coclass != 0)
        {
            Guid iid = Guid.Parse(t.Iid);
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)coclass)[16])(coclass, 0, &iid, &obj);
            steps.Add("ITypeInfo.CreateInstance " + Native.Hr(hr));
        }
        if (obj != 0)
        {
            int qi = Native.QueryInterface(obj, Native.IidIDispatch, out nint disp);
            steps.Add("QueryInterface(IDispatch) " + Native.Hr(qi));
            if (qi >= 0 && disp != 0)
            {
                Bind(t.Bind, disp);
                bound = t.Bind;
                var v = new Variant { Vt = 9, Ptr = disp };
                captured = _dispatch.CaptureValue(&v) with { Object = t.Bind };
            }
            Native.Release(obj);
        }
        else
        {
            _anyFailure = true;
        }
        foreach (nint p in new[] { coclass, typeLib, typeInfo })
        {
            if (p != 0)
            {
                Native.Release(p);
            }
        }
        _events.Add(new ActivationEvent { Seq = ++_seq, ProgramPath = path, Route = "typecreate", Class = t.Clsid, Clsid = t.Clsid, Iid = t.Iid, Steps = steps, Object = captured, Bind = bound });
    }

    private void DoAdvise(Advise a, string path)
    {
        var steps = new List<string>();
        if (!_bound.TryGetValue(a.Target, out nint obj))
        {
            _events.Add(new SkippedEvent { Seq = ++_seq, ProgramPath = path, Reason = $"Target '{a.Target}' is not bound." });
            return;
        }
        Guid eventIid = Guid.Parse(a.EventIid);
        int hr = Native.QueryInterface(obj, Native.IidIConnectionPointContainer, out nint container);
        steps.Add("QueryInterface(IConnectionPointContainer) " + Native.Hr(hr));
        nint point = 0;
        uint cookie = 0;
        if (hr >= 0 && container != 0)
        {
            // IConnectionPointContainer::FindConnectionPoint (slot 4); IConnectionPoint::Advise (5).
            hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)container)[4])(container, &eventIid, &point);
            steps.Add("FindConnectionPoint " + Native.Hr(hr));
            Native.Release(container);
        }
        if (hr >= 0 && point != 0)
        {
            var sink = new NativeObjects.Sink(eventIid, (dispId, flags, parameters) => RecordSinkCall(a.Name, dispId, flags, (DispParams*)parameters));
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)(*(nint**)point)[5])(point, sink.Pointer, &cookie);
            steps.Add("Advise " + Native.Hr(hr));
            if (hr >= 0)
            {
                _connections[a.Name] = (point, cookie, sink);
            }
            else
            {
                Native.Release(point);
            }
        }
        if (hr < 0)
        {
            _anyFailure = true;
        }
        _events.Add(new ConnectionEvent { Seq = ++_seq, ProgramPath = path, Op = "advise", Name = a.Name, Target = a.Target, EventIid = a.EventIid, Steps = steps, Cookie = hr >= 0 ? cookie : null });
    }

    private void RecordSinkCall(string name, int dispId, ushort flags, DispParams* parameters)
    {
        var args = new List<CapturedValue>();
        var byRef = new List<bool>();
        if (parameters != null && parameters->Args != null)
        {
            for (int i = (int)parameters->ArgCount - 1; i >= 0; i--)
            {
                args.Add(_dispatch.CaptureMaybeByRef(parameters->Args + i, out bool isByRef));
                byRef.Add(isByRef);
            }
        }
        _events.Add(new SinkCallEvent
        {
            Seq = ++_seq, ProgramPath = _currentPath, Connection = name, DispId = dispId, Flags = flags, Args = args, ByRef = byRef,
            ThreadId = (int)Native.GetCurrentThreadId(), AtUtc = DateTimeOffset.UtcNow,
        });
    }

    private void DoUnadvise(Unadvise u, string path)
    {
        if (!_connections.Remove(u.Name, out var c))
        {
            _events.Add(new SkippedEvent { Seq = ++_seq, ProgramPath = path, Reason = $"No connection named '{u.Name}'." });
            return;
        }
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, int>)(*(nint**)c.Point)[6])(c.Point, c.Cookie);
        Native.Release(c.Point);
        _events.Add(new ConnectionEvent { Seq = ++_seq, ProgramPath = path, Op = "unadvise", Name = u.Name, Steps = ["Unadvise " + Native.Hr(hr)], Cookie = c.Cookie, CallsRecorded = c.Sink.Calls });
    }

    /// <summary>
    /// Find another participant's worker process and prove it is this attempt's own (R-ORACLE-1 OR-OWN), as
    /// <see cref="KillParticipant"/> and <see cref="SuspendParticipant"/> require.
    /// </summary>
    private Process? OwnParticipant(string name, out string why)
    {
        string receipt = Path.Combine(_attemptDir, "receipts", name == "a" ? "started.json" : $"started.{name}.json");
        try
        {
            StartedReceipt started = EvidenceJson.Read<StartedReceipt>(receipt);
            Process process = Process.GetProcessById(started.ProcessId);
            bool ours = started.AttemptId == _attempt.AttemptId
                && process.ProcessName.Equals("CalcDb.Oracle.Worker", StringComparison.OrdinalIgnoreCase)
                && process.StartTime.ToUniversalTime() >= _attempt.PreparedUtc.UtcDateTime.AddSeconds(-1);
            if (!ours)
            {
                why = $"refused: process {started.ProcessId} is not this attempt's worker";
                process.Dispose();
                return null;
            }
            why = "";
            return process;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or EvidenceFormatException)
        {
            why = "not performed: " + ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// R-ORACLE-1 OR-OWN: claim the server process behind a bound object by the window handle its member returns. The
    /// process must run the named image and have started after the attempt was prepared; only then is it written to
    /// this participant's owned-children receipt, which the runner reads while the worker runs.
    /// </summary>
    private void DoClaim(ClaimProcess c, string path)
    {
        CapturedValue? value = DoInvoke(c.Target, c.Member, DispatchKind.MethodOrGet, [], null, path);
        int? pid = null;
        string? image = null, version = null;
        DateTimeOffset? started = null;
        string outcome;
        if (value?.Integer is not { } text || !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long hwnd) || hwnd == 0)
        {
            outcome = "refused: the member did not return a window handle";
        }
        else if (Native.GetWindowThreadProcessId((nint)hwnd, out uint owner) == 0 || owner == 0)
        {
            outcome = "refused: no process owns that window";
        }
        else
        {
            try
            {
                using Process process = Process.GetProcessById((int)owner);
                pid = process.Id;
                image = process.MainModule?.FileName;
                version = image is null ? null : FileVersionInfo.GetVersionInfo(image).FileVersion;
                started = process.StartTime.ToUniversalTime();
                if (!string.Equals(Path.GetFileName(image), c.Image, StringComparison.OrdinalIgnoreCase))
                {
                    outcome = $"refused: process {pid} runs {image}, not {c.Image}";
                }
                else if (started < _attempt.PreparedUtc.AddSeconds(-1))
                {
                    outcome = $"refused: process {pid} started before this attempt was prepared";
                }
                else
                {
                    RecordOwnedChild(new OwnedChild(_participant, process.Id, c.Image, started.Value, DateTimeOffset.UtcNow));
                    outcome = "claimed";
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                outcome = "refused: " + ex.GetType().Name + ": " + ex.Message;
            }
        }
        _events.Add(new ClaimEvent
        {
            Seq = ++_seq,
            ProgramPath = path,
            Target = c.Target,
            Member = c.Member,
            Window = value?.Integer,
            ProcessId = pid,
            ImagePath = image,
            ImageFileVersion = version,
            StartedUtc = started,
            Outcome = outcome,
        });
    }

    private readonly List<OwnedChild> _ownedChildren = [];

    /// <summary>Rewrite the owned-children receipt whole, through a temporary file, so the runner never reads a partial list.</summary>
    private void RecordOwnedChild(OwnedChild child)
    {
        _ownedChildren.Add(child);
        string receipt = Path.Combine(_attemptDir, "receipts", _participant == "a" ? "owned-children.json" : $"owned-children.{_participant}.json");
        string temp = receipt + ".tmp";
        File.WriteAllText(temp, EvidenceJson.Serialize(_ownedChildren));
        File.Move(temp, receipt, overwrite: true);
    }

    private SyncEvent DoSuspend(SuspendParticipant s, string path)
    {
        Thread.Sleep(s.DelayMs);
        string outcome;
        using (Process? process = OwnParticipant(s.Name, out string why))
        {
            if (process is null)
            {
                outcome = why;
            }
            else if (process.HasExited)
            {
                outcome = $"process {process.Id} had already exited";
            }
            else
            {
                const uint ThreadSuspendResume = 0x0002;
                var handles = _suspended.TryGetValue(s.Name, out var list) ? list : (_suspended[s.Name] = []);
                int count = 0, failed = 0;
                foreach (ProcessThread thread in process.Threads)
                {
                    nint h = Native.OpenThread(ThreadSuspendResume, false, (uint)thread.Id);
                    if (h == 0)
                    {
                        failed++;
                        continue;
                    }
                    if (Native.SuspendThread(h) == uint.MaxValue)
                    {
                        failed++;
                        _ = Native.CloseHandle(h);
                        continue;
                    }
                    handles.Add(h);
                    count++;
                }
                outcome = $"{count} threads of process {process.Id} suspended{(failed > 0 ? $", {failed} not" : "")}";
            }
        }
        return new SyncEvent { Seq = ++_seq, ProgramPath = path, Kind = "suspend", Name = $"{s.Name} after {s.DelayMs} ms: {outcome}", AtUtc = DateTimeOffset.UtcNow };
    }

    private SyncEvent DoResume(string name, string path)
    {
        string outcome;
        if (_suspended.Remove(name, out var handles))
        {
            int resumed = 0;
            foreach (nint h in handles)
            {
                if (Native.ResumeThread(h) != uint.MaxValue)
                {
                    resumed++;
                }
                _ = Native.CloseHandle(h);
            }
            outcome = $"{resumed} of {handles.Count} threads resumed";
        }
        else
        {
            outcome = "nothing suspended";
        }
        return new SyncEvent { Seq = ++_seq, ProgramPath = path, Kind = "resume", Name = $"{name}: {outcome}", AtUtc = DateTimeOffset.UtcNow };
    }

    private void ResumeAllSuspended()
    {
        foreach (string name in _suspended.Keys.ToList())
        {
            _events.Add(DoResume(name, "end"));
        }
    }
}
