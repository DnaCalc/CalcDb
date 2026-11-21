namespace CalcDb.Storage;

/// <summary>
/// The conservation state of a cache after a trace (II-03 §8): every count the book asks to be conserved, and the
/// invariant violations found. A quiescent cache has no pins, tokens, permits, reservations, hold or gate.
/// </summary>
public sealed record CacheConservation
{
    public required int Pins { get; init; }
    public required int LoadTokens { get; init; }
    public required int NativePermits { get; init; }
    public required int ReservedFrames { get; init; }
    public required int Views { get; init; }

    /// <summary>Image records whose source is a spill record; equal to the store's live records when conserved.</summary>
    public required int SpillRecords { get; init; }

    /// <summary>Owner references held (overlay revisions, scope-before images, view history, preservation).</summary>
    public required int Owners { get; init; }

    public required bool AdmissionHeld { get; init; }
    public required bool GateHeld { get; init; }
    public required string? Restriction { get; init; }
    public required IReadOnlyList<string> Violations { get; init; }

    public bool Quiescent => Pins == 0 && LoadTokens == 0 && NativePermits == 0 && ReservedFrames == 0 && !AdmissionHeld && !GateHeld;
}

public sealed partial class PageCache
{
    /// <summary>Audit the cache's ownership state: counts, and every invariant it breaks (none when conserved).</summary>
    public CacheConservation CheckConservation()
    {
        lock (_latch)
        {
            List<string> violations = [];
            int pins = 0, reserved = 0, free = 0, scanFrames = 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                FrameSlot f = _slots[i];
                pins += f.Pins;
                if (f.State == FrameState.Immutable && f.Class == AdmissionClass.Scan)
                {
                    scanFrames++;
                }
                switch (f.State)
                {
                    case FrameState.Free:
                        free++;
                        if (f.Record is not null || f.Pins != 0 || f.IoReferences != 0 || f.Handoff != 0)
                        {
                            violations.Add($"Free frame {i} still has a binding, pins, I/O or a handoff.");
                        }
                        break;
                    case FrameState.Reserved:
                        reserved++;
                        break;
                    case FrameState.Loading:
                        if (f.Record?.Loading is not { } token || token.Frame.Slot != i)
                        {
                            violations.Add($"Loading frame {i} has no load token naming it.");
                        }
                        break;
                    default:
                        if (f.Record?.Ready is not { } ready || ready.Slot != i || ready.Generation != f.Generation)
                        {
                            violations.Add($"Resident frame {i} is not the ready frame of its image.");
                        }
                        if (f.IoReferences != 0 && f.State != FrameState.SpillInFlight)
                        {
                            violations.Add($"Frame {i} keeps an I/O reference outside I/O.");
                        }
                        break;
                }
            }
            if (free != _free.Count)
            {
                violations.Add($"{free} free frames but {_free.Count} on the free list.");
            }
            if (pins != _livePins)
            {
                violations.Add($"Frames hold {pins} pins but {_livePins} pins are live.");
            }
            if (reserved != _reserved)
            {
                violations.Add($"{reserved} reserved frames but {_reserved} reservations.");
            }
            if (scanFrames != _scanFrames)
            {
                violations.Add($"{scanFrames} Scan-class immutable frames but _scanFrames is {_scanFrames}.");
            }

            int tokens = 0, nativeTokens = 0, spillRecords = 0, owners = 0;
            foreach (ImageRecord record in _directory.Values)
            {
                if (record.Loading is { } token)
                {
                    tokens++;
                    nativeTokens += token.Kind == LoadKind.Native ? 1 : 0;
                }
                if (record.Source == ImageSource.Spill)
                {
                    spillRecords++;
                    if (record.Spill is null)
                    {
                        violations.Add($"Image {record.Id} has a spill source but no locator.");
                    }
                }
                owners += record.Owners;
                if (record.Owners > 0 && record.Ready is null && record.Loading is null && record.Source != ImageSource.Spill)
                {
                    violations.Add($"Image {record.Id} is owned but has no frame, load or spill record: its bytes are lost.");
                }
                if (record.Ready is { } ready && _slots[ready.Slot].Record != record)
                {
                    violations.Add($"Image {record.Id} names frame {ready.Slot}, which is bound elsewhere.");
                }
            }
            if (tokens != _liveTokens)
            {
                violations.Add($"{tokens} load tokens installed but {_liveTokens} live.");
            }
            int permits = _pages.Values.Sum(e => e.ActiveLoads);
            if (permits != nativeTokens)
            {
                violations.Add($"{permits} native-source permits for {nativeTokens} native loads.");
            }
            if (_spill is not null && _spill.LiveRecords != spillRecords)
            {
                violations.Add($"The spill store holds {_spill.LiveRecords} records but {spillRecords} images name one.");
            }

            // Every owner reference has a holder: an overlay revision or scope-before, a non-current history version,
            // or a publication's preservation.
            Dictionary<ImageId, int> expected = [];
            void Expect(ImageId image) => expected[image] = expected.GetValueOrDefault(image) + 1;
            foreach (PrivateOverlay overlay in _overlays)
            {
                Dictionary<int, HashSet<ulong>> expectedScopePages = [];
                foreach ((ulong pageNumber, OverlayPage page) in overlay.Pages)
                {
                    Expect(page.Current);
                    int prevScope = 0;
                    int prevDeltaStart = 0;
                    foreach (ScopeBefore entry in page.Befores)
                    {
                        if (entry.Scope <= prevScope || entry.Scope > overlay.Depth)
                        {
                            violations.Add($"Overlay page {pageNumber} has out-of-order scope-before {entry.Scope} (prev {prevScope}, depth {overlay.Depth}).");
                        }
                        if (entry.DeltaStart < prevDeltaStart || entry.DeltaStart > page.Deltas.Count)
                        {
                            violations.Add($"Overlay page {pageNumber} scope {entry.Scope} has invalid DeltaStart {entry.DeltaStart} (prev {prevDeltaStart}, count {page.Deltas.Count}).");
                        }
                        prevScope = entry.Scope;
                        prevDeltaStart = entry.DeltaStart;
                        if (!expectedScopePages.TryGetValue(entry.Scope, out HashSet<ulong>? set))
                        {
                            expectedScopePages[entry.Scope] = set = [];
                        }
                        set.Add(pageNumber);
                        if (entry.Before is { } image)
                        {
                            Expect(image);
                        }
                    }
                }
                for (int i = 0; i < overlay.ScopePages.Count; i++)
                {
                    int scope = i + 1;
                    List<ulong> recorded = overlay.ScopePages[i];
                    if (scope > overlay.Depth)
                    {
                        if (recorded.Count != 0)
                        {
                            violations.Add($"Overlay inactive scope {scope} retains {recorded.Count} pages (depth {overlay.Depth}).");
                        }
                        continue;
                    }
                    HashSet<ulong>? expectedForScope = expectedScopePages.GetValueOrDefault(scope);
                    int expectedCount = expectedForScope?.Count ?? 0;
                    if (recorded.Count != expectedCount || (expectedForScope is not null && !expectedForScope.SetEquals(recorded)))
                    {
                        violations.Add($"Overlay scope {scope} tracked {recorded.Count} pages but {expectedCount} pages hold a scope-before for it.");
                    }
                }
            }
            foreach (ulong pageNumber in _historyPages)
            {
                List<PageVersion> versions = _pages[pageNumber].Versions!;
                for (int i = 0; i < versions.Count - 1; i++)
                {
                    Expect(versions[i].Image);
                }
            }
            if (_gate is { } attempt)
            {
                foreach (AttemptPage page in attempt.Pages.Where(p => p.OwnsOld))
                {
                    Expect(page.Old);
                }
            }
            foreach (ImageRecord record in _directory.Values)
            {
                if (record.Owners != expected.GetValueOrDefault(record.Id))
                {
                    violations.Add($"Image {record.Id} has {record.Owners} owners; its holders account for {expected.GetValueOrDefault(record.Id)}.");
                }
                expected.Remove(record.Id);
            }
            foreach (ImageId missing in expected.Keys)
            {
                violations.Add($"Image {missing} is held but has no record.");
            }

            int views = _viewSerials.Values.Sum();
            if (views != _viewCount)
            {
                violations.Add($"{views} view registrations by serial but {_viewCount} views.");
            }
            return new CacheConservation
            {
                Pins = pins,
                LoadTokens = tokens,
                NativePermits = permits,
                ReservedFrames = reserved,
                Views = _viewCount,
                SpillRecords = spillRecords,
                Owners = owners,
                AdmissionHeld = _hold is not null,
                GateHeld = _gate is not null,
                Restriction = _restriction,
                Violations = violations,
            };
        }
    }
}
