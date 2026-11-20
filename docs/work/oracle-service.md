# The distributed oracle service (proposed design)

**Status:** topology and the use of ACE for CalcDb testing, research and development approved by the owner
(2026-11-17, review round 1); the macro policy below is the owner's. It settles the transport and worker arrangement that
[The Plan](../the-plan.md) §§2.3 and 7 left open, using what the DnaCalc Excel service
(Touchstone v2) learned in production ([lessons](C:/Work/DnaCalc/codex-runs/excel-service-lessons.md),
its code in `C:\Work\DnaCalc\DnaCalc\tools\excel-service`). The code is rewritten for CalcDb in
C#; the design ideas carry over. The book's decisions stand: **D-ORACLE-1** (one long-lived
batched service owns its native workers), **D-EVIDENCE-1**, and the R-ORACLE-1 branches.

The first slice ([oracle-runner.md](oracle-runner.md)) runs one fresh worker process per
attempt with full identity capture. That is right for experiments with effects, and it is
the arrangement Touchstone v1 found too slow for everyday questions. This design keeps it as
one tier and adds a fast tier beside it.

## What carries over from Touchstone, and what does not

| Touchstone lesson | In CalcDb |
| --- | --- |
| Long-lived engine processes answer typed calls in batches; environment recorded once per environment | Tier 1 below. The environment receipt and OR-IDENT identity are taken when a worker starts and when its module list changes, and are named by an `envId` hash in every answer. |
| Typed requests, never free text | Questions stay typed operation programs (`calcdb.question/1`). SQL is text the engine must see, so tier 1 accepts SQL questions only through an admission policy (below). |
| Connections start from Windows: the broker opens ssh to each Linux host and runs a relay there | Same. The relay serves a Unix socket to lanes on that host; nothing listens on Windows or on Linux beyond that socket. |
| Failure is data: a crash is an answer; crashed batches are split until the crashing call stands alone; isolation worker; crash budget; restart cool-down and cap; a missed deadline kills one engine | Same for questions declared effect-free. A question with effects is never re-run: a crash leaves it `Uncertain` (II-15 §3). |
| Look for hidden state on purpose: replay in a fresh engine, reverse order on another worker | This is **OR-REUSE** run continuously; differences are leakage candidates and change no answer. |
| Log every wait as a defect | A `waits.jsonl` with the same classes, used for The Plan §4 process measurements. |
| Desktop isolation must cover every start path; a guard file must read zero windows on the owner's desktop | One launcher for every start and restart of the broker, the workers and any Office process; its guard record is checked at every broker start (OR-DESKTOP). |
| COM attaches to whatever instance is registered | In-process DAO cannot bind to a foreign instance, so tier 1 does not have this problem. Access automation in tier 2 binds only through the launched process's own window (`hWndAccessApp`), asserting PID and start time (OR-OWN). |
| Lanes without the oracle produced unverified work | Every lane on every host reaches the service directly and can upload its own fixture bytes by hash. |
| Python broker and relay sent over ssh | Rewritten in C# (.NET 11): the broker ships its own built relay to the host by content hash, so relay and broker always match. Lanes already have the .NET SDK to build CalcDb. |

## Two tiers

**Tier 1, the question service.** Effect-free questions against immutable fixtures: reads,
metadata, Seek and Find, SQL `SELECT` and expression evaluation through DAO (R-EXPR-1, R-SQL-1,
R-CURSOR-1 read branches, R-DAO-1 read members). They are answered by a pool of long-lived ACE
workers, each holding one `DBEngine` in its own process on an STA and opening fixtures
read-only. Most of the first research cards can be asked here, without `msaccess.exe` and its UI.

**Tier 2, owned experiments.** Everything with effects or a context that cannot be shared:
writes and DDL, transactions and flush, locks and two processes (R-LOCK-*, R-TXN-1), crash and
freeze (R-CRASH-1, OR-FREEZE), Compact & Repair (T-COMPACT-1), Access-hosted routes, and every
case of the R-ORACLE-1 job/fault matrix. These keep the first slice's model: one owned process
per attempt, fresh input copies, receipts, sealing and `Uncertain` without replay. Batching here
means several experiments per broker request, not shared engine state.

A question declares which tier may run it, through two fields that are part of its key:

| Field | Values | Meaning |
| --- | --- | --- |
| `effects` | `none`, `fixture`, `external` | `none`: read-only opens of immutable fixtures; may be re-run (bisection, leakage checks). `fixture`: changes its own input copies. `external`: touches anything else (a second process, the lock protocol, a crash). Only `none` is admitted to tier 1. |
| `isolation` | `pooled`, `fresh-worker`, `fresh-attempt` | `pooled`: any tier 1 worker after its declared reset. `fresh-worker`: a new tier 1 worker. `fresh-attempt`: tier 2. OR-REUSE evidence decides which questions may be `pooled`; until a reset is qualified for a card, its questions run `fresh-worker`. |

## Topology

```mermaid
flowchart LR
    subgraph Linux lane host
        L1["lane (agent)"] -- frames --> S["Unix socket ~/.calcdb-oracle/oracle.sock"]
        S --> R["relay (shipped by the broker)"]
    end
    subgraph Windows oracle host, private desktop
        B["broker: queue, batcher, answer cache, logs"]
        W1["ACE worker w0 (pooled)"]
        W2["ACE worker w1 (pooled)"]
        WS["ACE worker w2 (serial)"]
        WI["ACE worker w3 (isolation)"]
        J["tier 2: owned attempt runner"]
        B --> W1 & W2 & WS & WI
        B --> J
    end
    B -- "ssh opened from Windows" --> R
    L2["lane on Windows"] -- named pipe --> B
```

## Protocol `calcdb-oracle/1`

Touchstone's framing, which held up under load: a 4-byte big-endian length, a type byte and a
compact UTF-8 JSON body, zlib-compressed on the ssh stream (type byte + 0x80), frames under 64 KB.

| Type | Direction | Body |
| --- | --- | --- |
| hello | both | protocol version, client name, the broker's environment entries |
| submit | client → service | request `id`, `seq`, `last`; questions (`calcdb.question/1`) or references to them by key; options: `priority` (`interactive`, `bulk`), `capture` (`answer` or `record`) |
| put / get | both | an artifact by SHA-256, in chunks with offset; verified by digest on arrival |
| answer | service → client | per question: execution, typed results (`CapturedValue`), error channels, `envId`, worker, batch |
| env | service → client | an environment entry, before the first answer naming it |
| done, error, ping/pong, wait | as in Touchstone | `error` codes include `refused`, `ace-crashed`, `crash-budget`, `timeout`, `worker-down` |

A resent `(id, seq)` is answered from the broker's cache and never evaluated twice, which is
the network form of the existing token rule (T-EVID-02). Fixture bytes travel by hash, so a
lane can ask about a database it just made, and a fresh clone can rerun retained questions.

## Evidence

- **Answers are captured by the tooling, cheaply.** Every answered question goes to a daily
  compact log (`log/YYYY-MM-DD.jsonl.gz`): question key, `envId`, worker, batch and the typed
  result. There are no per-request folders in tier 1.
- **Retention stays a choice.** `capture: "record"` (or `calcdb-oracle retain` on a logged
  answer) produces a `calcdb.evidence/1` record with the question, the answer, the full
  environment entry the `envId` names, and the worker's prehistory since its last reset
  (II-15 §3: stateful reuse puts the prehistory in the semantic key). Tier 2 records are as now.
- **Identity once, not per call.** A worker takes its OR-IDENT observation after start and its
  first activation, and repeats it at batch boundaries only when its module list hash changes.
  A worker that cannot complete identity serves nothing. The environment entry is retained by
  hash with the first record that uses it.
- Leakage checks and crash lists are evidence about ACE (a crash on an input is a recorded
  behaviour) and are retained when they show something.

## Failure handling

- Deadline per batch from learned per-question costs. A miss kills only that worker.
- A crashed `effects: none` batch is split until the crashing question stands alone; it then
  runs on the isolation worker and is answered `ace-crashed`. Each question kind has a crash
  budget per hour; past it, its questions are refused, not run.
- A crashed tier 2 attempt is sealed `Uncertain` or `Terminated` with its partial artifacts and
  never re-run (OR-JOB).
- A down worker restarts after a cool-down, at most three times an hour. The last worker still
  up is never capped. `calcdb-oracle status` gives one line: OK, DEGRADED or DOWN.

## Admission policy

- Tier 1 opens fixtures only through DAO, read-only, from fixture copies by hash. DAO does not run
  AutoExec, startup forms or VBA. Corpus files are never opened in Access in tier 1.
- SQL and expression questions run in ACE's sandbox state as the machine's setup left it. The service records that
  state in the environment entry and never changes it during a run. Functions with external effects (`Shell` and the
  like) are refused at admission, by a deny list that names each refused function.
- **Security settings are setup, not run state.** The owner allows macro and Trust Center settings on the oracle
  machine to be changed so that CalcDb's own code can run. A change is made as a setup step, between runs, by a script
  that records the old and new values; the environment entry of every later answer names the resulting state.

## Macro policy (tier 2 Access-hosted routes)

The owner's policy (2026-11-17):

| Code | May run as macros | Condition |
| --- | --- | --- |
| Generated by the CalcDb project (probes, fixtures, harness VBA) | yes | Under the setup's permissions; its source is in the repository, and the record names its commit. |
| From outside: downloaded samples, corpus files with VBA, third-party modules | only after review | An agent of at least Sonnet level with high reasoning reads every line, with a mild bias to caution. Clearly safe: it may run. Any doubt: it is queued and the owner reviews it. |

- A review is retained as an artifact: the SHA-256 of the code reviewed, the reviewer's model and reasoning level, the
  verdict (safe, doubt) and its reasons. A run of outside code names its review; changed code needs a new review.
- Until its review says safe, an outside file is opened only through DAO, or in Access with startup and AutoExec
  bypassed and macros disabled.
- The queue of files waiting for the owner is `docs/work/macro-review-queue.md`, one line per file with its hash and
  the doubt.

## Build order

Each step is a CalcDb slice with its own test, and the existing tasks attach where shown.

1. **Long-lived worker protocol.** Refactor the worker so the same interpreter reads framed
   batches on stdio, keeping today's attempt-directory mode for tier 2. Reset declaration and
   prehistory recording. (Starts `tdt9`, OR-REUSE.) Built: one question per `submit` frame, as described in
   [oracle-runner.md](oracle-runner.md#long-lived-worker-oracle-service-step-1-or-reuse).
2. **Broker on Windows**: queue, priorities, batcher, answer cache, named-pipe endpoint, worker
   pool with restart caps, waits and daily logs, `status`. Tier 2 jobs submitted through the same
   broker run on the existing owned-attempt runner.
3. **Crash bisection, isolation worker, crash budget.** (Feeds `npmg`, OR-JOB, for tier 2.)
4. **Relay and Linux client**: broker-opened ssh, relay shipped by hash, Unix socket, a C#
   client in a platform-neutral assembly. Measure a single question from a Linux host.
5. **Private-desktop launcher and guard** for every start path, before any Office process runs
   (`b8sx`, OR-DESKTOP). The owned-attempt runner already has both since 2026-11-17 (oracle-runner.md); the broker,
   the pooled workers and Office processes still need them. Dialog handling through UI Automation is not built.
6. **Leakage checks**: fresh-engine replay and reverse-order runs, as continuous OR-REUSE.

The `.accdb` evidence index, the question and capture schemas, the sealing rules and the value
capture are unchanged. They are the one evidence path that both tiers write.

## Isolated environments to evaluate (process work)

Part of the improvement work of The Plan §4, at the owner's request: learn which environment suits which tier, as
the DnaCalc Excel service lessons did for Excel. The output is a report, `docs/work/isolated-environments.md`, from
experiments on this machine, and a recommendation per tier. Two candidates:

- **Windows Sandbox**, a disposable Windows desktop that starts clean each time, and the MCP server that drives it.
- **[microsoft/mxc](https://github.com/microsoft/mxc)**, another isolated environment; what it offers is to be read
  from its repository before any experiment.

Questions each experiment answers, with measurements, not impressions:

| Question | Why it matters here |
| --- | --- |
| Can Office Click-to-Run and ACE be installed or mapped in, and under which licence terms? | Without the same ACE build the environment answers nothing (OR-IDENT). |
| Start time, warm-start time, and the cost of one question | Tier 1 needs long-lived workers; tier 2 pays per attempt. |
| How files move in and out, and whether byte images and lock files survive the boundary | Fixtures by hash, OR-FREEZE images, lock-file evidence. |
| Whether a crash, hang or modal dialog stays inside | OR-DESKTOP and the conduct rules; the night's ODBC dialog is the test case. |
| Whether a reset is a true fresh state | OR-REUSE; a disposable environment may be the cheapest `fresh-attempt`. |
| Whether outside macros can run there safely before or instead of review | The macro policy above. |

Rules for the experiments: they change nothing on the owner's desktop session, install only what the report
names, and record every setup step so it can be undone.

## Resolved with the owner

- Topology: approved, including the ssh client on the Windows host (2026-11-17).
- Running ACE for CalcDb testing, research and development: approved (2026-11-17).
- The macro policy: above.
