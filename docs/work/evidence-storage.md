# Evidence storage: what we keep (agreed and done, 2026-11-18)

**Principle (owner, 2026-11-18).** The precious result is the answer to each question the book asks about ACE, with
enough metadata to know where and how it was answered. Execution traces and database binaries are by-products: if
we doubt an answer (a tool bug, say), we run the question again. We are not building an evidence-management system
with full provenance.

## What the database images are

A question runs against a fresh copy of its input database (the fixture) in its attempt folder. The runner records
the files of the attempt around the run, and the record lists them as artifacts:

| Kind | What it is | Retained today |
| --- | --- | --- |
| input fixture | the database a question starts from, usually made by a `create-*` question and frozen in `probes/fixtures.json` | 183 files, 124 MB |
| file snapshot | a copy of a file at a labelled point (`observeFiles`: before open, after open, after close), plus lock files | 583 files, 1.16 GB |
| changed input | the input copy as the question left it (after edits, DDL, compaction) | 592 files, 745 MB |
| output file | a file the question made (a new database, a compacted copy, generated VBA) | 539 files, 174 MB |
| traces | ETW file I/O, worker lifecycle | 23 files, 9 MB |

The JSON record (1.1 GB in all) holds the question, the environment, and the call trace: every call with its typed
result or error. The trace is what `batch --compare` compares CalcDb against.

What development reads back: the call results (the comparison), the input fixtures (to run questions again, and in
tests), and a few named fixtures in tests. Two tests sweep every retained image as an improvised format corpus
(`NativeCatalogTests`, `NativeFixtureTests`). Nothing reads the snapshots, changed inputs or outputs as such.

## Proposal

**1. Retain an answer record per question, not a capture.** One JSON file per question with:
- the header: probe and question key, book IDs, date, machine, ACE/Access identities (OR-IDENT), caller surface,
  tooling and book revisions, outcome, retention note;
- the answer: the typed results and errors of the calls, in compact form (exact values; text as text, hex only where
  bytes are odd; no default-valued fields; no repeated lists);
- file effects as facts, not files: exists, length, SHA-256, and for format cards the pages or byte ranges that
  changed. A card whose question is the bytes (R-FMT-2, R-PRESERVE-1, CAT-NEW, crash and flush cards) keeps exactly
  the bytes it asks about, inline, not whole databases.

The full capture stays in the local runner store (`.oracle-runs`), which may be pruned at any time.

**2. Keep only the input fixtures as binaries**, compressed (`<sha256>.gz`, the hash of the raw bytes), about 124 MB
raw and a few MB compressed. They are what makes a question repeatable without re-keying every question, and tests
use them. Everything else goes.

**3. When an answer is in doubt, run it again and replace the record.** The commit says why; git history keeps the
old one. No supersession bookkeeping.

**4. Convert what we have once.** Rewrite the 2,757 records as answer records, delete the artifacts that are not
input fixtures, and make the compare harness and the evidence index read the new form (comparison rules
unchanged). History is not rewritten: the old files stay in git history, about 60 MB packed on GitHub.

**5. Give the format sweep a real corpus.** The two tests that sweep every retained image move to the fixtures in
`probes/fixtures.json`, plus an optional local folder for a larger sweep. A deliberate generated corpus (README: the
file corpus is a product in its own right) is W-01/W-04 work, not a side effect of retention.

**6. Housekeeping.** `git gc --prune=now` here (325 MB of unreachable local blobs, never pushed), and a
`calcdb-oracle prune` for the runner store (9.7 GB today).

**Not needed then:** Git LFS, a separate evidence repository, sparse checkouts. Expected working tree for evidence:
records in the low hundreds of MB at most (long loop traces dominate; to be measured on the converted form) and
fixtures of a few MB.

**The Plan.** §2.4–2.5 describe retained evidence as records plus artifacts with hashes. They would say instead:
retained evidence is the answer records and the input fixtures; captures are local and disposable; a doubted answer
is run again and replaced.

## Done (task `hvhm`, 2026-11-18)

- Records are `calcdb.evidence/2`: compact JSON, every artifact kept as a fact, `path` only where the bytes are kept.
  `retain` writes this form; `freeze` keeps a fixture's bytes; `ArtifactStore` is the one reader and writer of
  `evidence/artifacts/<sha256>.gz` (raw copies cached in `.oracle-runs/cas`).
- Bytes kept: 260 fixtures and the ETW, lifecycle-trace and lock-file artifacts, 288 files, 155 MB raw, 9.2 MB stored.
  The other 1,463 artifacts (1.98 GB, the 130 MB file among them) are gone from the tree.
- Byte cards: none keeps whole database images now. The format and preservation images were never interpreted; the
  slice that needs bytes (W-04, W-05) runs its questions again and keeps the pages it reads in the answer.
- Records: 1,101 MB to 681 MB (2,758 records; 110 over 1 MB hold 242 MB; the largest 10.5 MB). `evidence/` is
  699 MB in the working tree instead of 3.2 GB.
- `calcdb-oracle prune [--days N]` removes the file copies of older sealed attempts from the runner store.
- The image sweeps in the tests now cover the stored fixtures (260) instead of every retained image (1,686).

## Decided (owner, 2026-11-18)

- The proposal is agreed; it is work item `hvhm`, after the performance commit.
- Which cards keep bytes in their answers is decided card by card in the conversion, from what each card asks.
- Long loop traces stay whole for now. If the converted records are still very large files, ask the owner again.
