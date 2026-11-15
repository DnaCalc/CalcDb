# CalcDb: conventions to adopt from DNA Calc (notes for AGENTS.md / CHARTER.md)

Source: the DNA Calc orchestrator session (2026-11-09), plus CalcVBA's AGENTS.md and CHARTER.md. The fuller source is DnaCalc/DnaCalc AGENTS.md.

## Repo and process

- Work on master only: `git pull --no-rebase`, commit, push. No branches, worktrees, rebase, force push or `reset --hard`.
- Commit messages start with the bead id when the commit belongs to a bead. They end with the model's Co-Authored-By line. Agents commit as themselves.
- Tracker: `br` (beads) with a repo prefix (e.g. `cdb-`). Write sequence: pull, `br sync --import-only`, write, `br sync --flush-only`, then commit and push at once. On Windows, set `RUST_MIN_STACK=67108864` for `br sync`.
- Beads are chunky. They exist for dependency management and to cover every intended work area. They do not track individual edits. They follow the Book's structure faithfully:
  - one epic per Book work package;
  - one bead per construction-index slice (II-16), sized as the Book sizes it (S, M or L);
  - one bead per experiment card (A-02), linked as an input to the slices it decides.
- Bead dependencies copy the construction index's start and completion edges. Each bead names its Book IDs (work package, slice, card, requirements), so every requirement in I-16 can be traced to beads.
- The Book leads and the beads follow. A change of scope or design goes into the Book first. The beads are then reconciled to the new index, and are not edited independently.
- One root AGENTS.md. Every lane brief says "read AGENTS.md first".
- MIT licence, with `license.workspace = true` in every package. Keep a licence ledger wherever third-party code enters; for CalcDb that means the Jackcess (Apache 2.0) ports.
- Light process: one light check, periodic sweeps, keepers. No certificates, receipts, evidence zips or handoff documents.
- GitHub CI stays disabled.

## Honesty rules (in AGENTS.md from day one)

- pass = matched the oracle. fail = mismatched + refused + missing, counted over all cases.
- No hard-coded answers, weakened assertions or regenerated goldens.
- One of everything (evaluator, oracle per family, harness, verdict path): extend it, never duplicate it.
- Name subtle oracle behaviours explicitly in types and design notes from the start.

## Rust workspace

- Pinned stable toolchain in `rust-toolchain.toml` (1.94.1 + rustfmt, clippy, wasm32-unknown-unknown), edition 2024, one Cargo workspace.
- Crates are named by layer (e.g. `calcdb-format`, `calcdb-isam`, `calcdb-sql`, `calcdb-dao`).
- `[package.metadata.dnacalc] portability = "portable" | "native-only"`. Portable crates build for wasm32 and touch no std::fs/process/os/net. At first, a wasm32 build of the portable crates enforces portability. Layering rules as data (`tools/xtask/architecture.json`) with a mechanical check come once there are enough crates to get the layering wrong.
  - CalcDb: the format, ISAM and SQL crates should be portable over an abstract VFS. The native VFS, the lock file and COM are native-only.
- `[profile.dev] debug = false`, plus a `perf` profile for benchmarks only. Counts must match between debug and perf before any performance change.
- All tooling goes through `cargo xtask`. Start with `check` only. Add further subcommands (health, keep, corpus, sweep, bench, capture) when each is first needed.

## Check cadence (for development velocity)

- Work in large coding batches. During a batch, the only check is that the work compiles. Commit as the batch progresses.
- After a batch, do one large check run: tests, oracle comparisons, clippy. Then run fix-and-check loops until it is clean.
- Avoid small-step coding with an exhaustive test run after each edit. Fixing and bisecting the small bugs a large batch leaves is usually fast, and it costs far less than slow compile-and-test loops.
- `cargo xtask check` stays light enough for every commit: build plus fast unit tests. Full corpus and oracle runs belong to the batch check, not to each commit.

## Oracles (ACE/Access/DAO on Windows)

- Recorded oracle answers are committed as captures keyed by their inputs. The ACE/Access build is provenance, never a condition.
- Start with a simple in-process capture harness; DAO and ACE run in-process. Grow it into a long-lived oracle answer service (like tools/excel-service: several ACE/Access processes, batching, a private desktop) when throughput or Access automation calls for it.
- Office automation rules (for runs that drive Office or Access):
  - Drive only processes our scripts started.
  - Never touch Trust Center, macro security or HKLM during a run. Oracle-machine setup, including any kernel driver for crash observation, is a separate setup step that the owner approves.
  - Run on a private desktop, guarded to 0 windows on the owner's desktop.
  - Dismiss dialogs through UI Automation, with no mouse, keyboard or focus.
- Reverse engineering: never decompile proprietary binaries. Black-box observation and public docs are fine.
- Measured-behaviour research: find the shape with designed probes, and never steer on match percentage.

## Agents and machines

- An orchestrator Claude session on Windows writes briefs to `codex-runs/<lane>.md`, with reports in `<lane>-report-N.md` and status lines in `codex-runs/status.txt`.
- Codex lanes run on the Linux hosts (dna-koderbot, dna-firehorse) in tmux. Gemini Flash suits bounded, diff-scoped porting.
- One clone per writer.
- Clean-room rule specific to CalcDb: no Codex or Claude lane may read `kode-fi:~/cleanroom` or any FrankenSqlite/asupersync checkout. Research notes enter only through `docs/research/`, after human review.
