# GPT-6-Pro: revision of The Book of CalcDb to v1.1

Instructions for the owner. This file is not part of the book.

## Approach

**Use the running GPT-6-Pro session that wrote the book.** It keeps the voice and the reasons behind the design, and it has already run a reconciliation (Stage 3, then v1.0) successfully. Attach the committed v1.0 files, so that it works from the released text and not from its memory of earlier drafts.

**Split the work into two phases:**

1. **Response A, author's response and plan.** No file changes. GPT-6-Pro reviews the review: it says, item by item, whether it accepts each work item, disputes it with reasons, or needs a clarification. It then gives a revision plan: which files change, in which response, and how many responses the work takes.
2. **Responses B1 to Bn, the revision.** These follow the agreed plan. The final response returns the complete v1.1 pack.

The split keeps disagreements and surprises in a short response you can check, before any text changes. The closed work list stops the revision from turning into an open-ended rewrite.

**After v1.1:** Fable checks it against the closed list only; it does not do a new open review. The question is whether each item landed and broke nothing. Anything new that is not critical goes to errata.

**Fall back to a new session only if the running session shows signs of losing context**, such as quoting v1.0 text that isn't there, or reopening settled decisions. A new session would need the full pack attached, about 515,000 words, and it would have to work cluster by cluster.

**Version class.** The release record's policy puts substantive refinement in v1.1, and "approved incompatible scope" in v2.0.

- Some of the owner decisions remove scope. These are:
  - PRD-04, MDB user-level security;
  - PRD-09, optional undo;
  - PRD-03, which narrows ADO.
- These narrow the approved scope; they do not replace the product or architecture commitment. So v1.1 is the right class.
- The prompt asks GPT-6-Pro to record the scope removals explicitly in the release entry.

## Attachments for Response A

Attach these files from the repository at commit `e3564ac1` or later:

1. The v1.0 pack: `calcdb-book.v1.0.zip`.
   - Recreate it from the repository with `git archive --format=zip -o calcdb-book.v1.0.zip book-v1.0:docs/book`.
   - Or attach the files in `docs/book/`, which are unchanged since the `book-v1.0` tag.
2. `review/A-04-review.claude-fable-5.2.v1.0.md`: the independent review.
3. `review/post-review/decisions.md`: the owner decisions PRD-01 to PRD-09.
4. `review/post-review/work.md`: the closed work list PRW-01 to PRW-43.
5. `docs/calcdb-one-pager.md`: the project one-pager, which is the source for the scope preamble (PRW-41).

## Message for Response A (paste into the running session)

```text
The Book of CalcDb v1.0 has had an independent review by Claude Fable 5.2.
I have settled every finding that needed my decision. The result is a
closed work list for v1.1. Attached:

- calcdb-book.v1.0.zip: the released v1.0 pack, exactly as tagged. Work
  from these files, not from earlier drafts in this conversation.
- A-04-review.claude-fable-5.2.v1.0.md: the review (findings REV-F52-001
  to REV-F52-031).
- decisions.md: my decisions PRD-01 to PRD-09. These are final. Do not
  reopen them; write the book to match them.
- work.md: the closed list PRW-01 to PRW-43. It maps every finding to
  work. PRW-37 is dropped. PRW-41 to PRW-43 are my own additions (see
  below).
- calcdb-one-pager.md: the project one-pager that set the initial scope.

PRW-41, the scope preamble. Add a short front-matter file, 00-scope.md,
"Scope and context", of one or two pages. Reword the one-pager's material
in the book's own context and voice, so that the book stands on its own.
It should reflect my decisions: where a PRD decision changed the scope,
the preamble states the scope as it now is. Number its sections S1, S2,
and so on. Because it is front matter, no chapter is renumbered. Place it
after the release record in the reading order. Turn every reference to
the one-pager in the book into a reference to the preamble section it
means. The one-pager stays in A-01 as the original source. The authority
statements in 00-stage0-plan.md and A-01 name the preamble as the book's
scope statement.

PRW-42 and PRW-43, two corrections along the way:

- The book is rendered, so the "not render-verified" caveats make no
  sense. Remove them from the figure captions and notes, and from the
  status column of the figures table in book-record.md. The release
  record says the figures are rendered by the PDF build.
- CalcVBA's move to the platform VARIANT is done. Remove every reference
  to its old private 16-byte value form, its COM-boundary conversion and
  "being corrected". State the shared value plainly. Leave unrelated
  16-byte facts (format payloads, decimal magnitudes and the like) alone.

The goal is v1.1: a coherent book with a stable specification, in the
same voice as v1.0, that carries out the work list and nothing else.

This response: the author's response and the revision plan. Do not
change any book file yet.

1. Review the review. For each PRW item (PRW-41 to PRW-43 included), give one line: accept, dispute
   (with your reason and the text that supports it), or clarify (what you
   need from me). You may dispute an item that does not come from a PRD
   decision if you think the reviewer is wrong. For items that implement
   a PRD decision, raise only a conflict that the decision creates
   elsewhere in the book.
2. List any consequence of the decisions that the work list misses: a
   section, test family, figure, ID or summary that must also change for
   the book to stay consistent. These are added to the list. They are
   not a licence for other changes.
3. Give the revision plan:
   - the files each response will return, grouping Part I and Part II
     chapters that change together;
   - the number of responses;
   - what the final response contains.

Rules for the revision itself (for the plan now, and for the later
responses):

- Book level. Keep corrections at design level. Where a decision says a
  matter is settled during implementation (PRD-07 crash parity, PRD-08
  completed-write acceptance), state the goal and the preferred
  direction, and say plainly what is left to implementation. Do not add
  owners, schedules, interim workarounds or a platform split to the
  plan (PRD-06).
- Behave like ACE. Where the book needs a behaviour that ACE has, the
  target is ACE's observable behaviour, defined by black-box experiments.
  CalcDb's spec is decoupled from the CalcVBA and DNA Calc host specs
  (PRD-02, PRD-03).
- Experiments are specified, not run. No result may be presented as
  observed.
- Clean-room rules are unchanged. No decompiling of proprietary
  binaries; observation and public documentation are fine. Jackcess for
  exact material under its licence, mdbtools for facts only. The cleared
  research notes are concept input only.
- Identity. Keep chapter filenames and RQ/D/K/R/W/figure IDs. Retired
  IDs are recorded as retired, not reused: for example D-UNDO-2, and the
  user-level security test families. New IDs continue the existing
  series.
- Change only what the list, plus the consequences you list in point 2,
  requires. Correct each owning chapter together with its dependent
  summaries: the Part I and Part II pair, I-16, the working record and
  A-02 where affected.
- Write in plain English, as in v1.0. Prefer shorter sentences where you
  are rewriting anyway.

The final response returns the complete v1.1 pack as before, a full set
of files with nothing left out:

- every Markdown file, with the header updated to v1.1;
- the new 00-scope.md;
- 00-book-release.md with a v1.1 entry. The entry names the reasons (the
  A-04 review, the new scope preamble and the owner corrections), the
  affected IDs and files, the scope removals (MDB
  user-level security, optional undo D-UNDO-2, and ADO limited to ACE
  strings with refusal of all others), and the verification state;
- A-03 refreshed for v1.1, including the corrections from REV-F52-029
  and A-04 §5.4;
- book-record.md updated;
- book-manifest.v1.1.json with byte counts and SHA-256 hashes;
- all of it as calcdb-book.v1.1.zip.

Don't round off the requested scope or the file list, and don't stop
early. If the work needs more responses than planned, say so at the end
of a response and continue in the next.
```

## Message for each revision response (B1 to Bn)

```text
Plan accepted [with these changes: ...]. Continue with response B<k> as
planned. Return the complete replacement files for that response in a
zip, and list at the end which PRW items each file carries out.
```

After each response, the zip is extracted, diffed against `docs/book` and checked against the plan, as with the earlier stages, before the next message is sent.

## After v1.1

- Commit the v1.1 pack, tag it `book-v1.1`, and rebuild the PDF from v1.1.
- Run the Fable check against the closed list: did each PRW item land, do the seven traces still hold, do the manifest hashes match, and are the A-03 claims true. Findings that are not critical go to errata for v1.1.x or v1.2. They do not reopen v1.1.
