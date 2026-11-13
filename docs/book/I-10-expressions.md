# I-10. Expressions, sandbox and CalcVBA functions

Revision 3 · Part I review · Stage 2, response 1 · 2026-11-13

**Requirement trace:** Primary: RQ-SQL-07, RQ-SEM-01, RQ-VAL-02, RQ-EXPR-01, RQ-EXPR-02, RQ-EXPR-03, RQ-CALL-03. Shared obligations: RQ-SQL-01, RQ-SEM-02, RQ-SEM-03, RQ-SIM-01, RQ-CALL-02, RQ-TYPE-01. Full requirement text and Part II ownership are in I-16.

## One semantic service with explicit context

**D-EXPR-1 — Recommended.** Shared semantics are decided; the scalar execution form below is recommended. Bind expressions to compact typed nodes with stable node IDs under **K-EXPR**. Use a simple scalar evaluator over those nodes first; register-based instructions or compiled specialization remain later measured substitutions for the same service. SQL, Find, defaults, validation and QueryDef execution do not implement independent coercions. Bounded encoded field views stay attached to their page pins; semantic values and public results use the platform's real Automation VARIANT and BSTR under I-02, with ownership materialized when the called contract requires it.

An evaluation context supplies expression use, field/parameter bindings, locale/collation, wildcard mode, sandbox policy, function-registry generation and injected time/random services. Context-sensitive Access adapters reuse shared primitives. Access documents different omitted-replacement behavior for Nz in VBA and query fields; unqualified forwarding to a VBA routine would therefore be insufficient.[^nz] Database-aware functions use the same ISAM/SQL services and visibility context, not another evaluator.

Keep Null, Empty, missing arguments, errors and zero-length text distinct. Shared And/Or operations retain their logical/bitwise rules; SQL predicate acceptance is a separate context rule. Validation has its own acceptance policy rather than reusing WHERE's treatment of Null. Default and validation expressions also have different name/function admission rules.[^context] Required, AllowZeroLength and validation checks remain distinct constraints.

## Effects constrain evaluation

Each bound node/function records return-type constraints, Null behavior, determinism, possible errors, database reads, host calls and clock/random effects. Unknown effects are conservative. Metadata is sufficient to reject an unsafe transformation, not to declare a pure but erroring expression freely reorderable.

VBA and Access documentation describe eager evaluation of IIf's result arms.[^iif] Use that as the provisional baseline; R-EXPR-1 distinguishes literal, row-dependent and host-call cases before admitting folding, branch elimination or moved evaluation. It also determines argument/error order and coercion timing. A query must not become safer or faster by skipping an error that ACE exposes. Calls to time/random services occur at the evidenced evaluation points; injection does not imply one timestamp or random value per query.

The shared pattern service supports both Access wildcard modes over the required UTF-16/collation semantics. Prefer bounded compiled matching state cached by pattern, mode, collation and semantic generation; do not route arbitrary BSTR units through a UTF-8-only regex conversion. LIKE-to-range access needs an exact inclusion argument and any required residual test. Comparison, conversion and index-key construction retain I-05's distinct contracts.

## Sandbox and host functions

Sandbox admission is an explicit capability check attached to function binding and the evidenced invocation point. Resolve the registered function's identity, not merely its textual alias, and invalidate plans when registry/policy changes. Microsoft distinguishes Access-hosted and non-Access sandbox modes, and trust does not simply remove sandbox restrictions.[^sandbox] Capture the actual host/policy; do not alter Trust Center, macro security or HKLM to make an experiment pass.

**D-UDF-1 — Provisional.** Prefer an explicit versioned host callback; R-UDF-1/I-HOST-1 confirm or change it. CalcDb requires function resolution, argument/result ownership, effect metadata and error delivery from the host. These are requirements on an integration boundary, not invented existing CalcVBA ABI entry points. If observation requires in-process runtime discovery, replace the resolver adapter without changing the evaluator.

**K-THREAD** keeps VBA invocation on the caller thread. Prepare stable arguments and release internal latches before entry; on return, revalidate cursor, catalog and transaction generations and retained-plan dependencies, including function registry, sandbox and semantic context. Never repeat a callback to recover from stale physical preparation. Reentrancy may alter or close objects used by the outer query, so keep the outer operation's ownership distinct from its continuing validity. R-UDF-1 determines permitted reentry and public failures. Access.Application.Eval's host-UDF support does not prove standalone ACE can locate arbitrary VBA projects.[^eval]

Figure F-13 in [I-11](I-11-sql.md) shows the optional worker path converging on this caller-thread ownership and continuation contract.

DNA Calc's extra host functions are opt-in and mark dependent queries as nonportable. Stored Access modules remain preserved/readable and are not executed by CalcDb. Domain functions and other database-context functions use explicit host/database adapters with measured name, scope and visibility behavior; unknown availability is an exposed compatibility gap, not a fabricated result.

## Costs, experiments and reopening

Cache binding and constant patterns, reuse an operation-local value arena and avoid per-node heap allocation on the scalar path. Count conversions, BSTR materializations, node visits, comparisons, database reentries and host crossings. A compiled evaluator is justified only by measured savings after exact result/error/effect equivalence; it is not required for initial correctness.

The mutation path develops needed default/validation adapters early in this same service; W-06 adds full SELECT binding/execution. Repeated differences extend the shared service or its explicit context policy; they do not justify divergent SQL and VBA arithmetic implementations.

## Deciding experiments

The full fixtures, captures, branch choices and effort estimates are in [A-02](A-02-experiments.md); the complete source ledger is in [A-01](A-01-sources.md). No experiment result is claimed here.

**R-EXPR-1 (L)** starts with the R-ORACLE-1 scalar/context baseline, then uses R-UDF-1's routing instrumentation for the host-effect extension. Differences select context adapters or prohibit rewrites; early mutation defaults/validation and later W-06 SELECT work use this same service under the [full card](A-02-experiments.md#r-expr-1--shared-values-with-access-context).

**R-UDF-1 (M)** follows that scalar baseline and I-HOST-1 to decide function resolution, ownership, caller-thread delivery and continuation after reentry. The preferred explicit host callback changes only if the [full card](A-02-experiments.md#r-udf-1--resolver-ownership-and-reentry) shows a required discovery route or different continuation policy; unsupported host routes remain visible.

[^nz]: Microsoft, [Application.Nz](https://learn.microsoft.com/en-us/office/vba/api/access.application.nz), accessed 2026-11-12; documented context difference, not an observed ACE capture.
[^context]: Microsoft, [And](https://learn.microsoft.com/en-us/office/vba/language/reference/user-interface-help/and-operator), [Or](https://learn.microsoft.com/en-us/office/vba/language/reference/user-interface-help/or-operator), [DefaultValue](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/field-defaultvalue-property-dao) and [ValidationRule](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/field-validationrule-property-dao), accessed 2026-11-12.
[^iif]: Microsoft, [VBA IIf](https://learn.microsoft.com/en-us/office/vba/language/reference/user-interface-help/iif-function) and [Access IIf](https://support.microsoft.com/en-gb/access/functions/iif-function), accessed 2026-11-12.
[^sandbox]: Microsoft, [Functions and properties blocked by sandbox mode](https://support.microsoft.com/en-us/access/functions-and-properties-in-access-blocked-by-sandbox-mode), accessed 2026-11-12; current support guidance, not permission to change machine security settings.
[^eval]: Microsoft, [Application.Eval](https://learn.microsoft.com/en-us/office/vba/api/access.application.eval), accessed 2026-11-12. Access host contract only; portable host requirements derive from the supplied CalcVBA note and one-pager.
