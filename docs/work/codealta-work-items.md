# CodeAlta work items as the sole tracker (6v2c)

The owner asked on 2026-11-20 whether CodeAlta work items can replace [register.md](register.md): "We can use tags,
edit the in-work item text, and we can write a detailed work suggestion to cover what we would like in work items for
our purposes, as a suggested improvement." This page is the result of the experiment of 2026-11-21. It holds the
recommendation, what the experiment found, the owner's decisions, and the improvement suggestion for CodeAlta (CodeAlta
4.4.1).

## Recommendation

Not yet. Work items can carry every field of a register row today, but three facts stop them from being the only
record:

1. **Completed tasks are deleted.** The setting `work_items.completed_tasks` is `delete` (the default; `keep` exists).
   Of ten `done` rows sampled from the register, nine have no task file left. A finished slice would lose what it
   delivered, and a dependency on it could no longer be resolved.
2. **`.alta/` is not in git** (`git status`: `?? .alta/`). The Plan §5 says CodeAlta's tasks must not be the sole
   surviving record of completion claims. Uncommitted files are also invisible to an agent on another machine
   (The Plan §2.3).
3. **In progress is not in the file.** `alta task start` shows `in-progress`, but the file then says
   `status: pending`, the same as the Inbox. The live state is only in CodeAlta.

With the owner's two decisions below, the migration is mechanical: one Sonnet session over the 81 slice rows. The
register then shrinks to what is not a work item.

## Owner decisions

- Set **When a task is completed** to *keep* (Settings › Agent & models › Work items; `work_items.completed_tasks`).
- Decide whether `.alta/tasks/` is committed, as one Markdown file per task, beside the code it tracks.
- Create the proposed tag `p1`, and say whether priorities should be tags (`p1`–`p4`) or only a field (below).

## What the register carries, and where it would go

| Register content | Today | In a work item |
| --- | --- | --- |
| Slice name, book row, acceptance families, deciding branches | the row and the task's sections | already in the task text (written 2026-11-17) |
| Priority P1–P4 | column | frontmatter `priority:`; a tag only for filtering in the window |
| Level, size, phase | columns | frontmatter `level:`, `size:` |
| Start inputs and completion adds | slice names | frontmatter `depends_on:` and `completion_adds:` as short ids |
| Status (done, partial, usable, ready, blocked, not started) | leading words of a prose cell | frontmatter `state:`; ready and blocked are computed, never written |
| Delivered and open branches | a prose cell of up to 4 KB | a `## Status` section of the task, edited in place |
| Milestone steps, oracle-service steps, R-ORACLE-1 branches, the native observation graph | tables of their own | stay in docs: they are not work items |
| Performance items and found items | tables of short ids | already tasks; the tables go |

## The experiment (2026-11-21)

**Frontmatter.** Five tasks got register fields: `tc26`, `537t`, `x866`, `zfrp` and `6v2c`:

```yaml
wp: W-07
priority: P2
level: 12
size: L
state: not-started
depends_on: [7tp3, mpvx]
completion_adds: [wqzr]
```

CodeAlta kept the extra keys through `later` and `start` and rewrote only `status:`. `alta task show` and
`alta task list` do not show them.

**Tags.** The project has none. An agent can only propose one (`alta tag propose`), and only the owner creates it.
`alta task list` filters by one tag and one status. `p1` was proposed with the 14 open P1 slices.

**The three questions, from CodeAlta alone.** One `exec` script read the frontmatter of all 90 task files and the live
status from `alta task list`, then resolved each `depends_on`. It took 254 ms in one tool call. The register needs
one grep per question too, so neither is faster for an agent. The differences:

| Question | Register | Work items |
| --- | --- | --- |
| What is next? | read Pri and Status of 82 rows; "usable" is a judgment in prose | computed from `state` and `depends_on`; no view in the window shows it |
| What blocks `tc26`? | look up two slice names | resolve two short ids; a deleted (completed) dependency cannot be told from a wrong id |
| What did `zfrp` deliver? | its row | its `## Status` section, gone once completed under `delete` |

The register's value is not speed. It is one sorted page in git that survives completion. Its cost is keeping the
same status in two places: the organizer of 2026-11-20 spent 33 tool calls on register and task updates.

## Suggested improvement for CodeAlta

This is what work items would need for a project that plans by a dependency graph. The list follows the experiment,
and it details the extension The Plan §2.1 names.

1. **Dependencies.** A `depends_on` list of task ids in the frontmatter. From it, CodeAlta computes *ready* (every
   dependency done, or marked usable), *blocked by* (the open dependencies) and the reverse list (*blocks*). It
   refuses a cycle when the field is written and reports the cycle. `alta task list --ready`, `--blocked` and
   `alta task deps <id>` (the tree, with states) expose it, and the Work items tab shows the same columns.
2. **Kept after completion, with references.** When a project keeps completed tasks, `done` is a listed status
   (`alta task list --status done`), a dependency on a done task resolves, and a done task keeps its text. A
   per-project default of *keep* when any task has `depends_on`.
3. **A usable state.** Our slices are delivered in stretches. A slice that is partial but *usable as a start input*
   unblocks its dependents. Either a flag (`usable: true`) that readiness honours, or project-defined states with a
   "satisfies dependencies" property.
4. **Priority as an ordered field.** `priority: P1..P4` (or numeric), shown, sortable and filterable
   (`--sort priority,ready`), so "what next" is the first ready item by priority.
5. **The file tells the truth.** Write `in-progress` (and the session doing it) into the file, so a file read, a
   grep or git agrees with the window.
6. **Tags agents may use.** Filter by several tags (all of, any of). Let a project allow agents to create tags, or
   tags in named namespaces (`wp-…`, `p…`), so a migration of 81 items does not need 81 owner clicks.
7. **Progress without hand-editing.** `alta task note <id> --stdin` appends a dated entry (delivered, open, commit) to
   a `## Progress` section. A status-change history (when, by which session, from what to what) is kept in the file.
8. **Fields in the output.** `alta task show` and `list` return the frontmatter fields they do not know, so scripts
   need not parse files.
9. **Versioning guidance.** Say whether `.alta/tasks/` is meant to be committed. If it is, keep the format
   merge-friendly: one file per task, stable key order, no volatile fields.
10. **Session metrics of ACP providers.** For the Gemini 4 Neon session `01a1fabc…` (three turns, 99 min, 344 calls),
    `alta session report --scope session` gives 1,678.6M input and 17.7M output tokens. That is impossible: the last
    turn alone shows 0.6M. Session totals of a multi-turn ACP session look summed from cumulative reports, and no
    cost is given. Process measurement (The Plan §4) depends on these numbers.

## Migration, once decided

1. Write the frontmatter fields into the 81 slice tasks from the register's columns. Map slice names to short ids
   with the register's Work item column.
2. Move each row's delivered and open text into the task's `## Status` section. Leave a one-line `state:` in the
   frontmatter.
3. Add a small read-only view: a script that prints the ready items by priority and the blockers of each. It stays
   only until CodeAlta computes this itself.
4. Shrink register.md to the milestone, oracle-service and branch tables and the native observation graph. Point
   AGENTS.md, README and The Plan §5 to the work items.
