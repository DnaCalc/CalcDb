# Working on CalcDb

Read [README.md](README.md) first: it is the charter and the map of the design book.
The book ([docs/book](docs/book)) is the design authority; its
[scope statement](docs/book/00-scope.md) governs.

## The book leads

- Build what the book designs. Cite book IDs (work package, slice, card, requirement)
  in code comments, tests and commit messages where they explain why.
- A change of scope or design goes into the book first, as a new book version. Tracker
  items are then reconciled to it, never edited independently.
- Do not read the book whole. Use the README's reading order, then search Part II and
  the appendices by ID.

## Repository and process

- Work on master only: `git pull --no-rebase`, commit, push. No branches, worktrees,
  rebase, force push or `reset --hard`.
- Commit messages start with the bead id when the commit belongs to a bead, and end with
  the model's `Co-Authored-By` line. Agents commit as themselves.
- Tracker: `br` (beads) with the prefix `cdb-`. Write sequence: pull,
  `br sync --import-only`, write, `br sync --flush-only`, then commit and push at once.
  On Windows, set `RUST_MIN_STACK=67108864` for `br sync`.
- Beads are chunky. They manage dependencies and cover every intended work area; they do
  not track individual edits. They follow the book:
  - one epic per work package (W-01 to W-12, W-PROBE);
  - one bead per construction-index slice (II-16 §24), sized as the book sizes it;
  - one bead per experiment card (A-02), linked as an input to the slices it decides.
- Bead dependencies copy the construction index's start and completion edges. Each bead
  names its book IDs, so every requirement in I-16 can be traced to beads.
- MIT licence, with `license.workspace = true` in every package. Record third-party
  exact material (the Jackcess ports) in a licence ledger as it enters.
- Light process: one light check, periodic sweeps. No certificates, receipts, evidence
  zips, handoff documents or document-consistency checks. GitHub CI stays disabled.

## Honesty rules

- Pass means matched the oracle. Fail means mismatched, refused or missing, counted over
  all cases.
- No hard-coded answers, weakened assertions or regenerated expected results.
- One of everything (format description, ISAM, evaluator, oracle, harness, verdict
  path): extend it, never duplicate it.
- Name subtle oracle behaviours explicitly in types and design notes from the start.
- In measured-behaviour research, find the shape with designed probes; never steer on
  match percentage.

## Rust workspace

- One Cargo workspace, edition 2024, with a pinned stable toolchain in
  `rust-toolchain.toml` (1.94.1 with rustfmt, clippy and `wasm32-unknown-unknown`).
- Crates are named by layer: `calcdb-format`, `calcdb-isam`, `calcdb-sql`,
  `calcdb-dao` and so on.
- Each crate declares `[package.metadata.dnacalc] portability = "portable"` or
  `"native-only"`. Portable crates build for wasm32 and touch no `std::fs`, process,
  OS or network APIs; a wasm32 build of the portable crates enforces this. The format,
  ISAM, catalog, expression and SQL crates are portable over an abstract VFS; the native
  VFS, lock file and COM are native-only. Layering rules become data with a mechanical
  check once there are enough crates to get the layering wrong.
- `[profile.dev] debug = false`, plus a `perf` profile for benchmarks only. Counts must
  match between debug and perf before any performance change.
- All tooling goes through `cargo xtask`. Start with `check`; add subcommands (corpus,
  capture, bench and so on) when each is first needed.

## Check cadence

- Work in large coding batches. During a batch the only check is that the work compiles.
  Commit as the batch progresses.
- After a batch, run one large check (tests, oracle comparisons, clippy), then fix and
  re-check until clean.
- `cargo xtask check` stays light enough for every commit: build plus fast unit tests.
  Full corpus and oracle runs belong to the batch check.

## Oracles (ACE, Access and DAO on Windows)

- Recorded oracle answers are committed as captures keyed by their inputs. The ACE or
  Access build is provenance, never a condition.
- Start with a simple in-process capture harness (DAO and ACE run in process). Grow it
  into a long-lived oracle service when throughput or Access automation calls for it.
- Drive only processes our scripts started. Never touch Trust Center, macro security or
  HKLM during a run. Oracle-machine setup, including any kernel driver for crash
  observation, is a separate setup step the owner approves.
- Run Office on a private desktop, with no windows on the owner's desktop. Dismiss
  dialogs through UI Automation, with no mouse, keyboard or focus.
- Never decompile or disassemble proprietary binaries. Black-box observation and public
  documentation are fine.

## Clean room

- No agent reads `kode-fi:~/cleanroom` or any FrankenSqlite or asupersync checkout, and
  no agent runs [tools/cleanroom-reader](tools/cleanroom-reader/README.md).
- Their ideas enter only as reviewed notes in `docs/research/`, as concepts.

## Agents and machines

- An orchestrator session on Windows writes lane briefs to `codex-runs/<lane>.md`, with
  reports in `<lane>-report-N.md` and status lines in `codex-runs/status.txt`.
- Codex lanes run on the Linux hosts (dna-koderbot, dna-firehorse) in tmux. Gemini Flash
  suits bounded, diff-scoped porting. One clone per writer.
