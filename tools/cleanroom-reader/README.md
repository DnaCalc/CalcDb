# CalcDb clean-room reading workspace (kode-fi)

Purpose: learn ideas from FrankenSqlite, asupersync and related projects without CalcDb's
implementers (Claude/Codex agents) ever seeing their code or documents. Those projects'
licence carries an OpenAI/Anthropic rider, so their sources must never be sent to
OpenAI or Anthropic services.

## Rules

- `sources/` holds the original repos. Only the reader script (via OpenRouter, using
  non-OpenAI/Anthropic models) and humans read it. Never point Claude Code, Codex,
  Copilot or Cursor at this machine's `~/cleanroom`.
- `out/<run>/` is quarantine. A human reviews it (and `leakcheck.json`) before anything
  leaves. Approved, rewritten conceptual notes may then be copied to the CalcDb repo
  (for example `docs/research/`), with the provenance line of the run.
- Do not run `reader.py` from an agent harness made by OpenAI or Anthropic. Use a plain
  shell or tmux.

## Setup

OpenRouter key (stored only here, mode 600):

    read -rsp 'OpenRouter key: ' k; printf 'OPENROUTER_API_KEY=%s\n' "$k" > ~/cleanroom/openrouter.env; unset k; chmod 600 ~/cleanroom/openrouter.env

## Budget: $50 per month on OpenRouter

The OpenRouter key has a $50 monthly limit as a hard backstop. reader.py also stops starting
new calls at `--month-cap` (50) minus `--reserve` (5), and at `--budget` per invocation.
`python3 reader.py spend` shows the key's usage.

| Step | What | Model | Est. cost |
|---|---|---|---|
| 1 | map, profile `survey`: FrankenSqlite docs + non-test code, asupersync docs | deepseek-v4-pro | ~$13.5 |
| 2 | map, profile `rest`: asupersync code + tests, FrankenSqlite tests | deepseek-v4-flash (or a `:free` model) | ~$5.5 (or $0) |
| 3 | reduce (staged) + synth over all notes | deepseek-v4-pro | ~$4 |
| 4 | targeted `ask` deep dives during CalcDb planning | v4-flash picks files, v4-pro answers | ~$0.3–0.8 each |
| | Total for steps 1–3: about $23; about $20 left for 25+ deep dives, plus the $5 reserve | | |

Gemini (via its own access, later) can re-read the same units for a second opinion, which
helps catch a single model's misreadings. Free OpenRouter models (e.g.
`nvidia/nemotron-3-ultra-550b-a55b:free`) work with any phase via `--model`; they are
rate-limited, so the reader runs them one call at a time with longer retries.
Provider prompt logging is acceptable for this open project, so routing allows all providers
except DeepSeek's own hosting and Azure.

## Run

    cd ~/cleanroom/reader
    python3 reader.py plan --profile survey -v       # inventory + estimate, no API calls
    tmux new -d -s r1 ~/cleanroom/reader/run-r1.sh   # steps 1-3, log in ~/cleanroom/out/r1.log
    tmux attach -t r1                                 # watch; Ctrl+B then D to detach

    # a deep dive (question in a file; files picked automatically, or give --paths globs)
    python3 reader.py ask --run r1 --name mvcc-conflicts --question-file questions/mvcc.md

Re-running a phase skips finished units, so it is safe to stop and resume.

Phases can be run separately (map, reduce, synth, leakcheck); finished units are skipped
on re-run. Change model with `--model` (default deepseek/deepseek-v4-pro). 

## Outputs

- `out/<run>/map/*.md`: per-unit notes
- `out/<run>/reduce/<repo>.md`: per-repo report
- `out/<run>/SYNTHESIS.md`: lessons for CalcDb
- `out/<run>/provenance.jsonl`: model, provider, repo commit, input hash, usage and cost per call
- `out/<run>/leakcheck.json`: per output file, worst first:
  - source identifiers found in it, ranked `high` (names a source project, or a coined name
    that is quoted as code or has three or more parts), `medium` (other coined names, quoted
    names, long compound names) or `low` (two ordinary English words, e.g. a generic
    `page_size`); dictionary from `/usr/share/dict/words` (`apt install wamerican`);
  - `verbatim`: runs of 12 or more words shared with a source file, with the file;
  - code fences.
  Stdout shows only counts. Add generic terms to `prompts/allow_terms.txt` to silence them.
