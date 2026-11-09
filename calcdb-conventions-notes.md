# CalcDb: conventions to adopt from DNA Calc (notes for AGENTS.md / CHARTER.md)

Source: the DNA Calc orchestrator session (2026-11-09), plus CalcVBA's AGENTS.md and CHARTER.md. The fuller source is DnaCalc/DnaCalc AGENTS.md.

## Repo and process

- Work on master only: `git pull --no-rebase`, commit, push. No branches, worktrees, rebase, force push or `reset --hard`.
- Commit messages start with the bead id. They end with an `Agent-Log: <host>:<log path>` trailer and the model's Co-Authored-By line. Agents commit as themselves.
- Tracker: `br` (beads) with a repo prefix (e.g. `cdb-`). Write sequence: pull, `br sync --import-only`, write, `br sync --flush-only`, then commit and push at once. On Windows, set `RUST_MIN_STACK=67108864` for `br sync`.
- One root AGENTS.md. Every lane brief says "read AGENTS.md first".
- MIT licence, with `license.workspace = true` in every package. Keep a licence ledger wherever third-party code enters; for CalcDb that means the Jackcess (Apache 2.0) ports.
- Banned words in all our text: "gate" in any form, and "production ready".
- Light process: one light check, periodic sweeps, keepers. No certificates, receipts, evidence zips or handoff documents.
- GitHub CI stays disabled.

## Honesty rules (in AGENTS.md from day one)

- A refusal is a typed not-modelled result that names its owner, and it counts as a failure in every statistic.
- pass = matched the oracle. fail = mismatched + refused + missing, counted over all cases.
- No hard-coded answers, weakened assertions or regenerated goldens.
- One of everything (evaluator, oracle per family, harness, verdict path): extend it, never duplicate it.
- Name subtle oracle behaviours explicitly in types and design notes from the start.

## Rust workspace

- Pinned stable toolchain in `rust-toolchain.toml` (1.94.1 + rustfmt, clippy, wasm32-unknown-unknown), edition 2024, one Cargo workspace.
- Crates are named by layer (e.g. `calcdb-format`, `calcdb-isam`, `calcdb-sql`, `calcdb-dao`).
- `[package.metadata.dnacalc] portability = "portable" | "native-only"`. Portable crates build for wasm32 and touch no std::fs/process/os/net. Layering rules live as data (`tools/xtask/architecture.json`) and are checked mechanically.
  - CalcDb: the format, ISAM and SQL crates should be portable over an abstract VFS. The native VFS, the lock file and COM are native-only.
- `[profile.dev] debug = false`, plus a `perf` profile for benchmarks only. Counts must match between debug and perf before any performance change.
- All tooling goes through `cargo xtask` (check, health, keep, corpus, sweep, bench, capture). `check` is the light per-commit check. `sweep` is periodic.
- Note: asupersync requires nightly. This is another reason to keep any runtime dependency behind an adapter, or to write our own.

## Oracles (ACE/Access/DAO on Windows)

- Recorded oracle answers are committed as captures keyed by their inputs. The ACE/Access build is provenance, never a condition.
- One long-lived oracle answer service (like tools/excel-service): several ACE/Access processes, batching, a private desktop. Build it first, as infrastructure.
- Office automation rules:
  - Drive only processes our scripts started.
  - Never touch Trust Center, macro security or HKLM.
  - Run on a private desktop, guarded to 0 windows on the owner's desktop.
  - Dismiss dialogs through UI Automation, with no mouse, keyboard or focus.
- Reverse engineering: never decompile proprietary binaries. Black-box observation and public docs are fine.
- Measured-behaviour research: find the shape with designed probes, and never steer on match percentage.

## Agents and machines

- An orchestrator Claude session on Windows writes briefs to `codex-runs/<lane>.md`, with reports in `<lane>-report-N.md` and status lines in `codex-runs/status.txt`.
- Codex lanes run on the Linux hosts (dna-koderbot, dna-firehorse) in tmux. Gemini Flash suits bounded, diff-scoped porting.
- One clone per writer.
- Clean-room rule specific to CalcDb: no Codex or Claude lane may read `kode-fi:~/cleanroom` or any FrankenSqlite/asupersync checkout. Research notes enter only through `docs/research/`, after human review.
