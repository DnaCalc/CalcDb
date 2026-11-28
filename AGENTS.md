# Working on CalcDb

Before any work, read:

1. [README.md](README.md): the charter and the map of the design book.
2. [The Plan](docs/the-plan.md), **in full** (about 6k tokens). It says how we undertake
   the work: tooling, the Windows oracle runner, retained evidence, the C# reference
   implementation, agent coordination and process improvement, and the first
   milestone. Its rules apply to all work and are not repeated here.

The book ([docs/book](docs/book)) is the design authority; its
[scope statement](docs/book/00-scope.md) governs. Where The Plan and the book differ on
scope or design, the book wins.

## The book leads

- Build what the book designs. Cite book IDs (work package, slice, card, requirement,
  test) in code comments, tests, evidence records and commit messages where they explain
  why.
- A change of scope or design goes into the book first, as a new book version.
- Do not read the book whole. Use the README's reading order, then search Part II and
  the appendices by ID.

## Repository

- The integration checkout stays on master: `git pull --no-rebase`, commit, push. No rebase, force push or
  `reset --hard` there.
- Four persistent worktrees sit beside the integration checkout (owner, 2026-11-24; adapted 2026-11-26 for Antigravity
  workers). The check worktree (`CalcDb-check`, detached) is used only by [tools/landing-check.ps1](tools/landing-check.ps1).
  Two writer slots, `CalcDb-w1` and `CalcDb-w2`, are persistent worktrees that keep their `bin`, `obj` and `artifacts`
  between tasks, so a code writer starts warm instead of with a cold build and NativeAOT publish: the organizer
  switches a free, clean slot to a new branch (`git -C <slot> switch -c alta/<topic> master`, keeping the `alta/`
  prefix expected by `landing-check.ps1`) and launches a worker (`invoke_subagent` with `calcdb-writer`,
  `EnableMCPTools: true`, so it has `roslynskills` MCP; use `swarm add` only when explicitly coordinating via `/swarm`);
  after `-Finish` (or manual cleanup), the slot returns to `git -C <slot> switch --detach master` and `git branch -d alta/<topic>`. A prober uses the
  persistent `CalcDb-probe` worktree (`git -C CalcDb-probe switch -c alta/<name> master`), switched back to detached
  `master` after its landing. Each child commits on its branch; the organizer lands it by a merge, never a rebase.
  Branches are not pushed.
- Only the organizer edits the bookkeeping files: [register.md](docs/work/register.md), `qualified.md`,
  `qualification-report.md`, `book-proposals.md`, `compare-baseline*.tsv` and the process log. A child gives the
  lines it would add in its report.
- Commit messages start with the book ID they serve where there is one, and end with the
  model's `Co-Authored-By` line. Agents commit as themselves.
- A follow-up task you find goes into [register.md](docs/work/register.md), which is the standing task queue in
  dependency/register order; no separate external issue inbox is used.
- A work item is in progress only while it is being worked on. At the end of a stretch a partial slice stays in the
  register with what it delivered and its open branches. Complete it only when its row says done. A self-contained
  open branch becomes a task of its own.
- MIT licence. Record third-party exact material (the Jackcess sort resources) in a licence
  ledger as it enters. GitHub CI stays disabled.

## Sub-sessions

- A slice goes through the gates of [docs/work/landing-check.md](docs/work/landing-check.md): ready, brief, write,
  review, land, compare. Run code writers in parallel, one per project at a time, each in its own worktree, and at
  most two that build at once; a prober, a read-only reviewer or a compare does not count (2026-11-24: three writers,
  a landing check and a compare ran 13 or more builds and swapped on 31 GB). The third place goes to a probe round or
  a review. A brief gives only what is the task's own (the deliverable, its records, the files the child owns, the
  solution filter, the `-Questions` folders, roslynskills/roscli guidance); the fixed parts are landing-check.md's
  standard brief, which the brief names.
- When the owner asks to pause: instruct active children (`send_message` to running subagents, or `@all` on the swarm
  board if in `/swarm`) to let running checks finish, start nothing new, commit on their branch, stop any Roslyn daemon,
  and return/post a structured status (commits, done and checked, left in order, next step, undecided bytes). Record each
  status in its register row, let a running compare finish (it only reads), and update `plan.json` if in `/swarm`. On
  resume, message each existing subagent via `send_message` (or wake on the board) to continue from its status.
- A slice is ready when no new byte decision lacks a record: a prober closes its open questions one slice ahead of the
  writers. A child that still meets a byte no record decides, or a record that contradicts a note, stops and reports.
  The organizer then probes: a single discriminator itself, a larger round through a prober child that writes only
  cards, probes and evidence, and gives its qualified.md note in its report (process log, change 6, 2026-11-21).
  A question that writes keeps all page bytes, catalog pages included, before and after each statement it asks about
  (`nativeRead` with `keep: true`), so a later writer can read where ACE put what (94fd met a record without them; the
  r-q29 to r-q31 setups kept no catalog pages, so their DDL had to be probed again, r-q34); a fixture a round creates is registered with
  `calcdb-oracle freeze --name` into `evidence/artifacts` and `probes/fixtures.json`, so its questions run again from
  the repository (63z8).
- Launch writers and Gate 4 reviewers via `define_subagent(EnableMCPTools: true)` + `invoke_subagent` (`calcdb-writer`
  and `calcdb-reviewer`) by default so workers receive `call_mcp_tool` / `roslynskills` MCP (`mcp-p7`); empirically
  (2026-11-27 `@mcp-probe`), `swarm add` workers do not receive `<mcp_servers>` or `call_mcp_tool` and therefore fall back
  to `roscli` via `run_command` or `rg`. Under `invoke_subagent`, pick `Model` (`inherit`, `flash`, `pro`) appropriately
  and keep stretches to one deliverable each. When running under `/swarm` explicitly (`swarm add`), use `swarm models`
  (`gemini-4-neon-medium` for focused slices/probes, `gemini-4-neon-high` for standard writers/reviews/tooling,
  `gemini-4-neon-xhigh` for complex multi-layer slices; balance Claude when available and switch with `swarm model` on
  429 rather than killing/re-spawning).
- A child ends with the fixed report of landing-check.md returned directly to the parent (or posted to `@coordinator`
  if in `/swarm`): change, rules as a domain table (rule, the record that shows its case, the tuple it admits, the
  neighbours it refuses by name), tests, review, bookkeeping lines, open branches. Because MCP-enabled subagents receive
  lazy tool names without server `instructions`, `AGENTS.md` and `landing-check.md` keep the zero-schema-read
  `roslynskills` MCP parameter shapes inline.
- One review layer per landing: check every rule the child wrote against a record that shows the very case the rule
  names, not a neighbouring one, and every test assertion a child changed against its record. Expect one
  generalization beyond the records in a larger task; a Neon stretch of about 3,400 lines had eleven. A code writer
  starts this reviewer itself via `invoke_subagent` (`calcdb-reviewer`, `EnableMCPTools: true`, `Workspace: "inherit"`,
  strictly read-only on the writer's worktree — or `swarm add` when in `/swarm`) once its self-check passes; it applies
  the findings it agrees with, declines the others with a reason, and reports each with its disposition. A fix commit
  of more than about 1,000 lines gets a second pass. The organizer checks the dispositions, the scope, the landing check
  and the compare (vpfd trial, 2026-11-23: about 6 organizer calls for the landing against about 25 when the organizer
  ran the review round; process log). A stretch under about 300 lines, a probe round or a bookkeeping change has the
  organizer's review only.
- When coordinating subagents or landing jobs asynchronously, let reactive notifications wake you (no polling); if in
  `/swarm`, steer idle workers with `post_message` `@name` and sleep on `swarm_wait`.

## Tool practice

- Edit files with the editing tools, not with ad-hoc scripts. Python is not project tooling.
- Never push master while a `-Land` landing check runs: its fast-forward then refuses and the whole check runs again
  (g4tv-ado, 2026-11-23). Commit bookkeeping between landings; queued checks wait for the lock and read master only
  once they hold it.
- Never put junctions or links to the repository into the check worktree or a child's worktree: a recursive delete
  would follow them into `evidence/` and `probes/` (process log, 2026-11-21).
- **Disk hygiene:** on single-volume hosts especially, keep disposable capture/compare caches bounded. Run
  `calcdb-oracle prune --days 1 --store C:\Work\DnaCalc\CalcDb\.oracle-runs` periodically to remove file copies in sealed
  attempts older than a day; delete idle `<worktree>\.oracle-runs` directories (`CalcDb-w1`, `CalcDb-w2`, `CalcDb-check`)
  when resetting slots; and periodically purge disposable candidate state in
  `%LOCALAPPDATA%\CalcDb\compare-store\{attempts,jobs,questions,comparisons.jsonl}` while **preserving**
  `%LOCALAPPDATA%\CalcDb\compare-store\compare-sets\` and `cas\`. Keep only sealed `evidence/` and `compare-sets/` TSVs.
- Shell heredocs break on non-ASCII text (`§`, `ä`, `€`). Write such content with the file tools.
- Commit with `git commit -F <message file>`. A message file outside the repository works in
  every shell; give it a unique name (`$TEMP/msg-<topic>-<session>.txt`), never a fixed one.
- Inspect oracle results with `calcdb-oracle show`, not with one-off scripts; `--pages` and `--diff` print a record's
  kept page bytes and what changed against the input or an earlier point. It is not on the PATH: use a worktree's
  build or a compare snapshot's `runner\calcdb-oracle.exe`, and `show --record <record.json>` for a retained record
  ([oracle-runner.md](docs/work/oracle-runner.md), Use).
- At milestones and process checkpoints, record `pwsh tools/process-indicators.ps1` metrics, baseline verdicts, compare
  stops, and tooling benchmarks in [docs/work/process-log.md](docs/work/process-log.md). A full process review runs when
  an indicator crosses its line, or during ongoing ~15% daily process/tooling improvement work (The Plan §4).

## C# navigation and edits (roscli / roslynskills MCP)

`roscli` on `PATH` (`%LOCALAPPDATA%\RoslynSkills\bin\roscli.exe`, NativeAOT) is our fast C# client (~41–87 ms warm) and automatically falls back to managed `roscli-slow` (`.dotnet\tools\roscli-slow.exe`) when no daemon is bound or for lifecycle/help/stdin verbs. Together with `RoslynSkills.McpServer --mode slim` (`12` tools, `roslynskills` MCP server), it keeps `CalcDb.slnx` loaded in a `WorkspaceHost` daemon with a live in-memory `WorkspaceOverlayIndex` over cached `SourceText`/`SyntaxTree` (`ctx.search_text` `58.3 ms` CLI / `6.6 ms` MCP vs `94.7 ms` `rg`; zero-disk `ctx.read_window` `0.2–0.3 ms` host / `4.8 ms` MCP; 1-call `ctx.symbol_context` `64.1 ms` CLI / `13.3–15.6 ms` MCP; `--symbol` `find_invocations` `51.0 ms`, `find_references` `52.6 ms`, `nav.call_hierarchy` `854z` incoming `87.2 ms`). Every answer has the same JSON shape (`Data` payload, `Transport` metadata). A position is `<file> <line> <column>` or `file:line:col`; `--symbol Type.Member` / `--symbol-name` works across all 6 `nav.*` commands (with optional parameter-type disambiguation), `edit.rename_symbol`, `edit.change_signature`, `nav.call_path`, and `analyze.impact_slice`; `--symbols` batches `nav.find_references` and `nav.find_invocations`.

- **When to use `roslynskills` MCP or `roscli` vs built-in tools:**
  - Use **`roslynskills` MCP** (`call_mcp_tool` on `roslynskills`, ~5–15 ms warm over persistent pipe, auto-binds worktree on first workspace call) or **`roscli`** (`~50–70 ms` warm) whenever exploring/tracing C# symbols, reading member sources + `file_sha256`, finding callers/implementations (`nav.*`), checking multi-project compile errors (`roslyn_diag_snapshot`), or renaming/refactoring across files.
  - Use **`rg` / `read_file`** for non-C# files (`.md`, `.slnx`, `.csproj`, `.json`, `.tsv`, `.ps1`), literal strings, and quick isolated slices when no daemon is bound. Do not `rg` common C# names (`Execute`, `Kind`, `Open`) or trace interface callers by text.
- **Zero-schema-read `roslynskills` MCP quick reference (`ServerName: "roslynskills"`):**
  In a worktree (`CalcDb-w1`, `CalcDb-w2`, `CalcDb-probe`), **always pass `workspace_path`** (`"C:\\Work\\DnaCalc\\CalcDb-w1\\CalcDb.slnx"` or worktree root) so calls auto-bind and hit your own live index instead of the integration checkout. Call `call_mcp_tool` **directly** using these parameter shapes without spending a turn reading `<appDataDir>\mcp\roslynskills\*.json` unless an argument is rejected:
  - `roslyn_context`: `{ symbol | (file_path, line, column), workspace_path, brief?: true }` — 1-call mindspace (def + `file_sha256` + refs + 1-hop interface-aware callers/callees + impls + file diag; all three `file_path`, `line`, `column` required if not using `symbol`)
  - `roslyn_search`: `{ pattern | patterns?, mode?: "literal"|"regex", syntax_filter?: "code"|"declarations"|"invocations"|"identifiers"|"comments"|"strings", whole_word?: true, file_path?, roots?, workspace_path, max_results?, brief?: true }` — zero-disk overlay search (use `mode: "regex"` for `|` alternation; default is `"literal"`)
  - `roslyn_diag_snapshot`: `{ directory_path? | file_paths?, workspace_path, include_diagnostics?: true, severity_filter?: ["Error"|"Warning"], brief?: true, require_workspace?: true }` — `~110 ms` warm compile check without slow MSBuild (`severity_filter` is a string array, not a bare string)
  - `roslyn_nav_symbol`: `{ symbol | (file_path, line, column) | (file_path, symbol_name), workspace_path }`
  - `roslyn_nav_references` / `roslyn_nav_invocations`: `{ symbol | symbols | (file_path, line, column), workspace_path, max_results?, brief? }`
  - `roslyn_nav_call_hierarchy`: `{ symbol | (file_path, line, column), workspace_path, direction?: "incoming"|"outgoing"|"both", max_depth?: 1, brief?: true }` — captures `edges[].implemented_member` (`854z`)
  - `roslyn_nav_implementations`: `{ symbol | (file_path, line, column), workspace_path, max_results? }`
  - `roslyn_edit_rename`: `{ symbol | (file_path, line, column), new_name, apply: true, workspace_path }`
  - `roslyn_edit_transaction`: `{ operations, file_path?, expected_sha256?, expected_file_hashes?, apply: true, atomic: true, include_diagnostics: true, workspace_path }`
  - `roslyn_edit_code_fix`: `{ file_path, diagnostic_id?, line?, column?, fix_title?, apply: true, workspace_path }`
  - `roslyn_run`: `{ command_id, input: { ..., workspace_path } }` — any other command (`ctx.read_window` for zero-disk windows + `file_sha256`, `ctx.file_outline`, `edit.change_signature`, `nav.call_path`, `analyze.impact_slice`, `query.batch`, `workspace.*`, `daemon.list`, `daemon.stop`).
- **One-call compound mindspace & overlay reads:** Prefer `roslyn_context` / `ctx.symbol_context` over chaining `rg` + `read_file` + `nav.go_to_definition` + `nav.find_references` + `nav.call_hierarchy`, and `roslyn_search` / `ctx.search_text` + `ctx.read_window` over comment-noisy `rg` + repeated `read_file` when warm. Pair semantic search with `ctx.search_text` / `rg` where a string literal could carry a symbol name (82kf trial).
- **Semantic edits & fast diagnostics:** Use `roslyn_edit_rename` / `edit.rename_symbol --symbol`, `edit.change_signature --symbol`, atomic `roslyn_edit_transaction` with SHA-256 preconditions, `roslyn_edit_code_fix`, and `roslyn_diag_snapshot` / `diag.get_workspace_snapshot --directory-path <dir> --include-diagnostics true` instead of invoking MSBuild after small edits.
- **Who starts a daemon & setup/cleanup:** A writer tracing or refactoring across projects and a Gate 4 reviewer start/auto-bind a daemon; a prober, bookkeeping edit, or one-file fix does not (~20 s bind + ~33 s full 33-project warmup, ~1.2 GB RAM per worktree). For CLI loops in a worktree: `dotnet restore CalcDb.slnx`, `roscli workspace.use CalcDb.slnx --wait-warmup true`, then set `ROSCLI_DAEMON=required`. Run `roscli daemon.stop` (or `roslyn_run` `daemon.stop` with `workspace_path`) before finishing and verify `roscli daemon.list` shows your worktree stopped; `roscli daemon.prune` clears leftovers.
- **roscli is our tool** (owner, 2026-11-21): a wrong, slow or missing answer is fixed in RoslynSkills (`C:\Work\RoslynSkills`, landed on `main`, installed globally as `roscli` / `roscli-slow` + `mcp-p8`), never worked around in CalcDb.

## Honesty rules

- Pass means matched the oracle. Fail means mismatched, refused or missing, counted over
  all cases. An accepted difference (a documented design difference the owner approved,
  listed in the comparison rule) is counted on its own: everything else matched, and the
  difference is reported, never hidden.
- No hard-coded answers, weakened assertions or regenerated expected results.
- One of everything (format description, ISAM, evaluator, oracle, harness, verdict
  path): extend it, never duplicate it.
- Name subtle oracle behaviours explicitly in types and design notes from the start.
- In measured-behaviour research, find the shape with designed probes; never steer on
  match percentage.

## Check cadence

- Work in focused coding batches using Roslyn live diagnostics (`roslyn_diag_snapshot` / `diag.get_workspace_snapshot`)
  to stay compiled while editing without invoking MSBuild after small edits. Verify your new work with targeted
  `dotnet test <project> --no-build --filter ...` (and `-InPlace -Fast` or `-SkipNativePublish` for managed/DAO
  checks), skipping NativeAOT `CalcDb.Com` publishing and unrelated transitive or oracle suites on inner iterations.
- After a batch passes Gate 4 review, land clean managed/DAO slices quickly with `tools/landing-check.ps1 -Land
  -DetachSlot -Fast` (adding `-SkipNativePublish` when only managed code changed, or running full graph + NativeAOT
  checks when `CalcDb.Com` / `CalcDb.Com.Core` / `CalcDb.Com.Ado` native boundaries changed), immediately returning clean
  writer slots (`CalcDb-w1`, `CalcDb-w2`) to `master` without blocking on the oracle compare.
- Run full solution builds, NativeAOT `CalcDb.Com` publish, full transitive project-graph tests, and core compare
  (`-Snapshot` + `-Compare`) periodically at wave checkpoints (every 2–4 landings) in the background so regressions and
  NativeAOT differences are still caught promptly without dragging down everyday managed landings; run the wide
  compare set once a day and before recording wide baselines ([docs/work/landing-check.md](docs/work/landing-check.md)).

## Oracle conduct (ACE, Access and DAO on Windows)

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
