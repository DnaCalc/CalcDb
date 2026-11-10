#!/usr/bin/env python3
"""Clean-room reader: send source repos to non-restricted models via OpenRouter and
collect *conceptual* notes in a quarantine folder for human review.

Stdlib only. Run on kode-fi from a plain shell/tmux (never from a Claude/Codex harness).

  python3 reader.py plan   [--profile survey|rest|full] [--model M]     # estimate, no API calls
  python3 reader.py spend                                               # OpenRouter key usage
  python3 reader.py map    --run r1 --profile survey --model M          # per-unit notes
  python3 reader.py reduce --run r1 --reduce-model M                    # per-repo reports
  python3 reader.py synth  --run r1 --reduce-model M                    # cross-repo synthesis
  python3 reader.py leakcheck --run r1                                  # identifiers/code in outputs
  python3 reader.py ask    --run r1 --name mvcc --question-file q.md [--paths GLOB ...]
  python3 reader.py all    --run r1 ...                                 # map, reduce, synth, leakcheck

Stdout carries progress, sizes and cost only, never model output or source text.
"""
import argparse, concurrent.futures as cf, datetime as dt, fnmatch, hashlib, json, os, re
import subprocess, sys, threading, time, urllib.request, urllib.error
from pathlib import Path

ROOT = Path.home() / "cleanroom"
SOURCES = ROOT / "sources"
OUT = ROOT / "out"
PROMPTS = Path(__file__).resolve().parent / "prompts"
KEYFILE = ROOT / "openrouter.env"
API = "https://openrouter.ai/api/v1"

PRO = "deepseek/deepseek-v4-pro"
FLASH = "deepseek/deepseek-v4-flash"
# Never route to these model families (clean-room rule), whatever --model says.
FORBIDDEN_PREFIXES = ("openai/", "anthropic/", "openrouter/auto")
# Avoid first-party DeepSeek hosting (data in China) and Azure.
IGNORE_PROVIDERS = ["DeepSeek", "Azure"]

SKIP_DIRS = {".git", "target", "node_modules", ".beads", "artifacts", "rust_out", "baselines",
             "sample_sqlite_db_files", "legacy_sqlite_code", "dist", "pkg", ".github", "_headers"}
TEXT_EXT = {".rs", ".md", ".toml", ".txt", ".c", ".h", ".py", ".sh", ".ts", ".js", ".yml", ".yaml"}
MAX_FILE = 400_000           # skip single files larger than this (generated data, dumps)
CHUNK_CHARS = 450_000        # ~130k tokens per map call
REDUCE_CHARS = 1_200_000     # ~350k tokens per reduce call; larger inputs are reduced in stages
ASK_CHARS = 900_000          # source budget for one ask
CHARS_PER_TOKEN = 3.4
TESTISH = {"tests", "benches", "fuzz", "examples", "e2e", "conformance", "corpus", "fixtures", "golden", "snapshots"}
DOCS_ONLY_IN_SURVEY = {"asupersync"}

lock = threading.Lock()
spent = {"usd": 0.0, "in": 0, "out": 0, "calls": 0}
state = {"last_key_check": 0.0, "month_used": None}
ARGS = None


def log(*a):
    print(*a, flush=True)


# ---------------------------------------------------------------- inventory

def in_profile(repo, rel_path, profile):
    """survey: docs everywhere, non-test code (docs only for DOCS_ONLY_IN_SURVEY).
    rest: everything survey leaves out. full: everything."""
    if profile == "full":
        return True
    is_doc = rel_path.suffix.lower() in (".md", ".toml")
    testish = bool(set(rel_path.parts[:-1]) & TESTISH)
    survey = is_doc or (repo not in DOCS_ONLY_IN_SURVEY and not testish)
    return survey if profile == "survey" else not survey


def walk(repo):
    for dirpath, dirnames, filenames in os.walk(repo):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS and not d.startswith(".")]
        for fn in filenames:
            p = Path(dirpath) / fn
            if p.suffix.lower() not in TEXT_EXT:
                continue
            n = p.stat().st_size
            if 0 < n <= MAX_FILE:
                yield p.relative_to(repo), n


def repos_selected():
    names = ARGS.repos or [p.name for p in SOURCES.iterdir() if p.is_dir()]
    return [SOURCES / n for n in sorted(names)]


def inventory(profile):
    """Return {repo: [unit]} where unit = {name, files:[(rel, chars)], chars}."""
    out = {}
    for repo in repos_selected():
        groups = {}
        for rel, n in walk(repo):
            if not in_profile(repo.name, rel, profile):
                continue
            parts = rel.parts
            if len(parts) == 1:
                g = "_root"
            elif parts[0] in ("crates", "packages") and len(parts) > 2:
                g = parts[0] + "-" + parts[1]
            else:
                g = parts[0]
            groups.setdefault(g, []).append((rel.as_posix(), n))
        units = []
        for g, fl in sorted(groups.items()):
            fl.sort(key=lambda x: (not x[0].endswith((".md", ".toml")), x[0]))  # intent before code
            cur, size, k = [], 0, 1
            for rel, n in fl:
                if cur and size + n > CHUNK_CHARS:
                    units.append({"name": f"{g}__{k}", "files": cur, "chars": size}); k += 1
                    cur, size = [], 0
                cur.append((rel, n)); size += n
            if cur:
                units.append({"name": f"{g}__{k}" if k > 1 else g, "files": cur, "chars": size})
        out[repo.name] = units
    return out


def repo_commit(repo):
    try:
        return subprocess.check_output(["git", "-C", str(repo), "log", "-1", "--format=%H %cI"], text=True).strip()
    except Exception:
        return "unknown"


# ---------------------------------------------------------------- OpenRouter

def key():
    if os.environ.get("OPENROUTER_API_KEY"):
        return os.environ["OPENROUTER_API_KEY"]
    if KEYFILE.exists():
        for line in KEYFILE.read_text().splitlines():
            if line.startswith("OPENROUTER_API_KEY="):
                return line.split("=", 1)[1].strip().strip('"')
    sys.exit(f"No OpenRouter key: create {KEYFILE} (see ~/cleanroom/README.md)")


def get(path, auth=True):
    req = urllib.request.Request(API + path, headers={"Authorization": f"Bearer {key()}"} if auth else {})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.load(r)


def prices(model):
    try:
        for m in get("/models", auth=False)["data"]:
            if m["id"] == model:
                return float(m["pricing"]["prompt"]), float(m["pricing"]["completion"])
    except Exception:
        pass
    return None, None


def key_usage():
    d = get("/key")["data"]
    return {k: d.get(k) for k in ("label", "usage", "usage_monthly", "usage_daily", "limit", "limit_remaining", "limit_reset", "is_free_tier")}


def month_used(force=False):
    """Monthly spend on this key, refreshed at most every 60 s."""
    now = time.time()
    if force or state["month_used"] is None or now - state["last_key_check"] > 60:
        try:
            u = key_usage()
            state["month_used"] = float(u.get("usage_monthly") if u.get("usage_monthly") is not None else u.get("usage") or 0)
        except Exception as e:
            log(f"  (could not read key usage: {e}; relying on per-run budget)")
            state["month_used"] = state["month_used"] or 0.0
        state["last_key_check"] = now
    return state["month_used"]


def check_money():
    with lock:
        if ARGS.budget and spent["usd"] >= ARGS.budget:
            raise RuntimeError(f"run budget ${ARGS.budget} reached")
    used = month_used()
    if used >= ARGS.month_cap - ARGS.reserve:
        raise RuntimeError(f"monthly spend ${used:.2f} is at cap ${ARGS.month_cap} minus reserve ${ARGS.reserve}")


def call(model, system, user, max_tokens):
    if model.startswith(FORBIDDEN_PREFIXES):
        sys.exit(f"Refusing model {model}: OpenAI/Anthropic models are excluded by the clean-room rule")
    check_money()
    free = model.endswith(":free")
    # Prompt logging by providers is acceptable for this open project, so all providers are allowed.
    provider = {"ignore": IGNORE_PROVIDERS, "allow_fallbacks": True, "data_collection": "allow"}
    body = {"model": model, "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}],
            "max_tokens": max_tokens, "temperature": 0.2, "provider": provider, "usage": {"include": True}}
    req = urllib.request.Request(API + "/chat/completions", data=json.dumps(body).encode(), headers={
        "Authorization": f"Bearer {key()}", "Content-Type": "application/json", "X-Title": "CalcDb clean-room reader"})
    d = None
    for attempt in range(8 if free else 5):
        try:
            with urllib.request.urlopen(req, timeout=1200) as r:
                d = json.load(r)
            if "choices" in d and (d["choices"][0]["message"].get("content") or "").strip():
                break
            err = d.get("error") or "empty content"
        except urllib.error.HTTPError as e:
            err = f"HTTP {e.code}: {e.read().decode(errors='replace')[:300]}"
            if e.code not in (408, 429, 500, 502, 503, 504):
                raise RuntimeError(err)
        except (urllib.error.URLError, TimeoutError) as e:
            err = str(e)
        wait = (60 if free else 15) * (attempt + 1)
        log(f"  retry in {wait}s ({str(err)[:120]})")
        time.sleep(wait)
    else:
        raise RuntimeError(f"giving up: {str(err)[:300]}")
    u = d.get("usage", {})
    with lock:
        spent["usd"] += float(u.get("cost") or 0)
        spent["in"] += int(u.get("prompt_tokens") or 0)
        spent["out"] += int(u.get("completion_tokens") or 0)
        spent["calls"] += 1
        if state["month_used"] is not None:
            state["month_used"] += float(u.get("cost") or 0)
    meta = {"model": d.get("model"), "provider": d.get("provider"), "id": d.get("id"), "usage": u}
    return d["choices"][0]["message"]["content"], meta


# ---------------------------------------------------------------- helpers

def prompt(name):
    return (PROMPTS / name).read_text()


def run_dir():
    d = OUT / ARGS.run
    for sub in ("map", "reduce", "ask"):
        (d / sub).mkdir(parents=True, exist_ok=True)
    return d


def provenance(d, rec):
    rec["at"] = dt.datetime.now(dt.timezone.utc).isoformat()
    with lock, open(d / "provenance.jsonl", "a") as f:
        f.write(json.dumps(rec) + "\n")


def read_files(repo, rels):
    parts = []
    for rel in rels:
        try:
            parts.append(f"===== FILE: {repo}/{rel}\n" + (SOURCES / repo / rel).read_text(errors="replace"))
        except Exception:
            pass
    return "\n\n".join(parts)


def batches(items, size_of, limit):
    cur, size = [], 0
    for it in items:
        n = size_of(it)
        if cur and size + n > limit:
            yield cur
            cur, size = [], 0
        cur.append(it); size += n
    if cur:
        yield cur


# ---------------------------------------------------------------- phases

def cmd_spend():
    u = key_usage()
    log(json.dumps(u, indent=1))
    log(f"month cap ${ARGS.month_cap}, reserve ${ARGS.reserve}")


def cmd_plan():
    inv = inventory(ARGS.profile)
    pin, pout = prices(ARGS.model)
    total, nunits = 0, 0
    for repo, units in inv.items():
        c = sum(u["chars"] for u in units)
        total += c; nunits += len(units)
        log(f"{repo}: {len(units)} units, {sum(len(u['files']) for u in units)} files, {c/1e6:.1f}M chars, commit {repo_commit(SOURCES/repo)}")
        if ARGS.verbose:
            for u in units:
                log(f"   {u['name']:<40} {len(u['files']):>5} files {u['chars']/1e3:>8.0f}k chars")
    tok = total / CHARS_PER_TOKEN
    log(f"profile {ARGS.profile}: ~{tok/1e6:.1f}M input tokens, {nunits} map calls")
    if pin is not None:
        log(f"model {ARGS.model}: est. map cost ~${tok*pin + nunits*8000*pout:.2f}")


def cmd_map():
    d = run_dir()
    system, task = prompt("system.md"), prompt("map.md")
    jobs = []
    for repo, units in inventory(ARGS.profile).items():
        commit = repo_commit(SOURCES / repo)
        for u in units:
            outp = d / "map" / f"{repo}__{ARGS.profile}__{u['name']}.md"
            if not outp.exists():
                jobs.append((repo, commit, u, outp))
    jobs_n = 1 if ARGS.model.endswith(":free") else ARGS.jobs
    log(f"map: {len(jobs)} units, profile {ARGS.profile}, model {ARGS.model}, {jobs_n} parallel")

    def work(job):
        repo, commit, u, outp = job
        user = task.replace("{{REPO}}", repo).replace("{{UNIT}}", u["name"]) + "\n\n" + read_files(repo, [r for r, _ in u["files"]])
        text, meta = call(ARGS.model, system, user, 24000)
        outp.write_text(text)
        provenance(d, {"phase": "map", "repo": repo, "commit": commit, "profile": ARGS.profile, "unit": u["name"],
                       "files": len(u["files"]), "chars": u["chars"], "sha256": hashlib.sha256(user.encode()).hexdigest(), **meta})

    stop = False
    with cf.ThreadPoolExecutor(jobs_n) as ex:
        futs = {ex.submit(work, j): j for j in jobs}
        for f in cf.as_completed(futs):
            j = futs[f]
            try:
                f.result()
                log(f"  done {j[0]}/{j[2]['name']}  (run ${spent['usd']:.2f}, month ~${state['month_used'] or 0:.2f})")
            except Exception as e:
                log(f"  FAIL {j[0]}/{j[2]['name']}: {e}")
                if "budget" in str(e) or "cap" in str(e):
                    stop = True
            if stop:
                for g in futs:
                    g.cancel()
    log(f"map finished: {spent}")


def reduce_notes(d, label, notes, task, system):
    """Reduce [(name, text)] to one report, in stages if it is too large."""
    model = ARGS.reduce_model or ARGS.model
    stage = 0
    while sum(len(t) for _, t in notes) > REDUCE_CHARS:
        stage += 1
        parts = list(batches(notes, lambda x: len(x[1]), REDUCE_CHARS))
        new = []
        for i, b in enumerate(parts, 1):
            outp = d / "reduce" / f"{label}.stage{stage}.part{i}.md"
            if not outp.exists():
                body = "\n\n".join(f"===== NOTES: {n}\n{t}" for n, t in b)
                head = task + f"\n\n(This is partial batch {i} of {len(parts)}; another pass will merge the batches. Keep all substantive detail.)"
                text, meta = call(model, system, head + "\n\n" + body, 48000)
                outp.write_text(text)
                provenance(d, {"phase": "reduce-partial", "label": label, "stage": stage, "part": i, "inputs": len(b), **meta})
                log(f"  reduce {label} stage {stage} part {i}/{len(parts)} (run ${spent['usd']:.2f})")
            new.append((outp.stem, outp.read_text()))
        notes = new
    body = "\n\n".join(f"===== NOTES: {n}\n{t}" for n, t in notes)
    text, meta = call(model, system, task + "\n\n" + body, 48000)
    return text, meta


def cmd_reduce():
    d = run_dir()
    system = prompt("system.md")
    by_repo = {}
    for p in sorted((d / "map").glob("*.md")):
        by_repo.setdefault(p.name.split("__", 1)[0], []).append(p)
    for repo, ps in by_repo.items():
        outp = d / "reduce" / f"{repo}.md"
        if outp.exists():
            continue
        text, meta = reduce_notes(d, repo, [(p.stem, p.read_text()) for p in ps],
                                  prompt("reduce.md").replace("{{REPO}}", repo), system)
        outp.write_text(text)
        provenance(d, {"phase": "reduce", "repo": repo, "inputs": len(ps), **meta})
        log(f"  reduce {repo} done (run ${spent['usd']:.2f})")


def cmd_synth():
    d = run_dir()
    reps = sorted(p for p in (d / "reduce").glob("*.md") if ".stage" not in p.name)
    body = "\n\n".join(f"===== REPORT: {p.stem}\n{p.read_text()}" for p in reps)
    text, meta = call(ARGS.reduce_model or ARGS.model, prompt("system.md"), prompt("synth.md") + "\n\n" + body, 48000)
    (d / "SYNTHESIS.md").write_text(text)
    provenance(d, {"phase": "synth", "inputs": [p.stem for p in reps], **meta})
    log(f"  synthesis done (run ${spent['usd']:.2f})")


def cmd_ask():
    """Targeted deep dive: a question, answered from selected files.
    Without --paths, a cheap model picks files from the path list (names only)."""
    d = run_dir()
    if not ARGS.name or not ARGS.question_file:
        sys.exit("ask needs --name and --question-file")
    question = Path(ARGS.question_file).read_text()
    outp = d / "ask" / f"{ARGS.name}.md"
    files = []  # (repo, rel, n)
    for repo in repos_selected():
        for rel, n in walk(repo):
            files.append((repo.name, rel.as_posix(), n))
    if ARGS.paths:
        chosen = [f for f in files if any(fnmatch.fnmatch(f"{f[0]}/{f[1]}", g) for g in ARGS.paths)]
    else:
        listing = "\n".join(f"{r}/{p}\t{n}" for r, p, n in files)
        pick = prompt("pick.md").replace("{{BUDGET}}", str(ASK_CHARS))
        text, meta = call(ARGS.pick_model, "You select files. Reply with JSON only.",
                          pick + "\n\nQUESTION:\n" + question + "\n\nFILES (path<TAB>bytes):\n" + listing, 8000)
        provenance(d, {"phase": "ask-pick", "name": ARGS.name, **meta})
        m = re.search(r"\[.*\]", text, re.S)
        wanted = set(json.loads(m.group(0))) if m else set()
        chosen = [f for f in files if f"{f[0]}/{f[1]}" in wanted]
    chosen.sort(key=lambda f: (not f[1].endswith((".md", ".toml")), f[0], f[1]))
    picked, size = [], 0
    for f in chosen:
        if size + f[2] > ASK_CHARS:
            continue
        picked.append(f); size += f[2]
    log(f"ask {ARGS.name}: {len(picked)} files, {size/1e3:.0f}k chars")
    src = "\n\n".join(read_files(r, [p]) for r, p, _ in picked)
    user = prompt("ask.md") + "\n\nQUESTION:\n" + question + "\n\nFILES FOLLOW.\n\n" + src
    text, meta = call(ARGS.reduce_model or PRO, prompt("system.md"), user, 32000)
    outp.write_text(text)
    provenance(d, {"phase": "ask", "name": ARGS.name, "files": len(picked), "chars": size,
                   "commits": {r.name: repo_commit(r) for r in repos_selected()},
                   "sha256": hashlib.sha256(user.encode()).hexdigest(), **meta})
    log(f"  ask {ARGS.name} done (run ${spent['usd']:.2f})")


IDENT = re.compile(r"\b[A-Za-z_][A-Za-z0-9_]{5,}\b")


DICT = Path("/usr/share/dict/words")
# Names of the source projects and their crates/modules: any hit containing one is high.
PROJECT_TOKENS = {"fsqlite", "franken", "frankensqlite", "asupersync", "asup"}
WORD = re.compile(r"[a-z0-9]+")
SHINGLE = 12          # words in a row shared with a source file count as verbatim text


def ident_parts(t):
    """Split snake_case / CamelCase into lowercase parts."""
    parts = []
    for chunk in t.strip("_").split("_"):
        parts += re.findall(r"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", chunk)
    return [p.lower() for p in parts if p and not p.isdigit()]


def is_word(w, words):
    if w in words or len(w) <= 2:
        return True
    for suf in ("s", "es", "ed", "ing", "er", "ers"):
        if w.endswith(suf) and w[: -len(suf)] in words:
            return True
    return False


def severity(t, parts, words, in_code):
    """high: names a source project, or quotes a coined name; medium: long compound name; low: generic."""
    if any(p in PROJECT_TOKENS or p.startswith(("fsqlite", "franken", "asupersync")) for p in parts):
        return "high"
    coined = any(not is_word(p, words) for p in parts)
    if coined:
        return "high" if in_code or len(parts) >= 3 else "medium"
    if in_code or len(parts) >= 3:
        return "medium"
    return "low"


def shingles(text):
    w = WORD.findall(text.lower())
    return {hash(" ".join(w[i:i + SHINGLE])): i for i in range(len(w) - SHINGLE + 1)}, w


def cmd_leakcheck():
    """Rank source identifiers found in outputs by severity, find verbatim source text, count code.

    Only counts go to stdout. Terms and passages go to leakcheck.json for the human reviewer.
    """
    d = run_dir()
    allow = set((PROMPTS / "allow_terms.txt").read_text().lower().split()) if (PROMPTS / "allow_terms.txt").exists() else set()
    words = set(DICT.read_text(errors="replace").lower().split()) if DICT.exists() else set()
    words |= allow
    if not words:
        log("leakcheck: no word list at /usr/share/dict/words (apt install wamerican); severities will be pessimistic")

    outputs = sorted(d.rglob("*.md"))
    out_sh = {}                      # shingle hash -> list of (output index, word position)
    out_words = []
    for k, p in enumerate(outputs):
        sh, w = shingles(p.read_text(errors="replace"))
        out_words.append(w)
        for h, i in sh.items():
            out_sh.setdefault(h, []).append((k, i))

    idents = {}                      # identifier -> occurrences in source code
    verbatim = {}                    # output index -> {word position: source file}
    for repo in SOURCES.iterdir():
        for p in repo.rglob("*"):
            if not p.is_file() or any(s in p.parts for s in SKIP_DIRS) or p.suffix not in TEXT_EXT:
                continue
            if p.stat().st_size > 5_000_000:
                continue
            txt = p.read_text(errors="replace")
            if p.suffix == ".rs":
                for t in IDENT.findall(txt):
                    if ("_" in t.strip("_") or re.search(r"[a-z][A-Z]", t)) and t.lower() not in allow:
                        idents[t] = idents.get(t, 0) + 1
            w = WORD.findall(txt.lower())
            for i in range(len(w) - SHINGLE + 1):
                hits = out_sh.get(hash(" ".join(w[i:i + SHINGLE])))
                if hits:
                    for k, j in hits:
                        verbatim.setdefault(k, {}).setdefault(j, f"{repo.name}/{p.relative_to(repo)}")

    report, totals = [], {"high": 0, "medium": 0, "low": 0, "verbatim_passages": 0, "code_fences": 0}
    files_with = {k: 0 for k in totals}
    for k, p in enumerate(outputs):
        txt = p.read_text(errors="replace")
        code_spans = set(re.findall(r"`([^`\n]+)`", txt))
        in_code = {t for s in code_spans for t in IDENT.findall(s)}
        hits = {"high": [], "medium": [], "low": []}
        for t in sorted({t for t in IDENT.findall(txt) if t in idents}):
            hits[severity(t, ident_parts(t), words, t in in_code)].append(
                {"term": t, "source_count": idents[t], "in_code_span": t in in_code})
        # merge overlapping shingles into passages
        passages, pos = [], sorted(verbatim.get(k, {}))
        for j in pos:
            if passages and j <= passages[-1]["end"]:
                passages[-1]["end"] = j + SHINGLE
            else:
                passages.append({"start": j, "end": j + SHINGLE, "source": verbatim[k][j]})
        w = out_words[k]
        passages = [{"words": p_["end"] - p_["start"], "source": p_["source"],
                     "text": " ".join(w[p_["start"]:p_["end"]])} for p_ in passages]
        fences = txt.count("```") // 2
        entry = {"file": str(p.relative_to(d)), "high": hits["high"], "medium": hits["medium"],
                 "low": hits["low"], "verbatim": passages, "code_fences": fences}
        report.append(entry)
        for key, n in (("high", len(hits["high"])), ("medium", len(hits["medium"])), ("low", len(hits["low"])),
                       ("verbatim_passages", len(passages)), ("code_fences", fences)):
            totals[key] += n
            files_with[key] += 1 if n else 0
    report.sort(key=lambda e: (-len(e["verbatim"]), -len(e["high"]), -len(e["medium"]), e["file"]))
    (d / "leakcheck.json").write_text(json.dumps(report, indent=1))
    key_files = [e for e in report if e["file"] == "SYNTHESIS.md" or re.fullmatch(r"reduce/[^/.]+\.md", e["file"])]
    log(f"leakcheck: {len(report)} files -> {d/'leakcheck.json'} (review before release)")
    for key in totals:
        log(f"  {key:18} {totals[key]:6} in {files_with[key]} files")
    for e in sorted(key_files, key=lambda e: e["file"]):
        log(f"  {e['file']:28} high {len(e['high'])}  medium {len(e['medium'])}  low {len(e['low'])}"
            f"  verbatim {len(e['verbatim'])}  fences {e['code_fences']}")


def main():
    global ARGS
    ap = argparse.ArgumentParser()
    ap.add_argument("phase", choices=["plan", "spend", "map", "reduce", "synth", "leakcheck", "ask", "all"])
    ap.add_argument("--run", default="r1")
    ap.add_argument("--profile", choices=["survey", "rest", "full"], default="survey")
    ap.add_argument("--repos", nargs="*", help="limit to these source repos")
    ap.add_argument("--model", default=PRO, help="map model")
    ap.add_argument("--reduce-model", default=None, help="reduce/synth/ask model (default: --model; ask defaults to v4-pro)")
    ap.add_argument("--pick-model", default=FLASH, help="file picker for ask")
    ap.add_argument("--budget", type=float, default=20.0, help="per-invocation USD limit")
    ap.add_argument("--month-cap", type=float, default=50.0, help="monthly OpenRouter cap in USD")
    ap.add_argument("--reserve", type=float, default=5.0, help="keep this much of the monthly cap unspent")
    ap.add_argument("--jobs", type=int, default=3)
    ap.add_argument("--name"); ap.add_argument("--question-file"); ap.add_argument("--paths", nargs="*")
    ap.add_argument("-v", "--verbose", action="store_true")
    ARGS = ap.parse_args()
    if ARGS.phase in ("plan", "spend"):
        return globals()[f"cmd_{ARGS.phase}"]()
    log(f"month spend so far ~${month_used(force=True):.2f} of ${ARGS.month_cap} (reserve ${ARGS.reserve})")
    phases = ["map", "reduce", "synth", "leakcheck"] if ARGS.phase == "all" else [ARGS.phase]
    for ph in phases:
        log(f"== {ph}")
        globals()[f"cmd_{ph}"]()
    log(f"total spent this invocation: ${spent['usd']:.2f} over {spent['calls']} calls")


if __name__ == "__main__":
    main()
