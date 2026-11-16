Below are project reports, one per repository read: an embedded SQL engine and the async runtime and other building blocks it uses. Write a single synthesis for the CalcDb team. Follow the firewall rules strictly.

Structure:

1. Executive summary: the five to ten most valuable lessons, one paragraph each.
2. How the projects fit together: which building block provides what to the engine, and the boundaries between them.
3. A recommended CalcDb architecture outline informed by these lessons. Keep it at the level of layers and responsibilities, covering: VFS/I/O, pager/cache, Jet file format, lock-file protocol and in-process MVCC, ISAM, catalog, SQL front end/planner/executor, expression service via CalcVBA, DAO/COM, test harness and oracles, runtime/async. For each, say what to adopt as an idea, what to do differently because of Jet/ACE, and what to avoid.
4. Building-block decision: for each building block the engine relies on, describe the capabilities CalcDb would need from an equivalent (so the team can choose: build its own, use another library, or seek permission to use the original). Do not recommend copying code.
5. Testing and oracle strategy for CalcDb, adapted from these projects, with real ACE/Access on Windows as the black-box oracle.
6. Risks and pitfalls these projects ran into that CalcDb should anticipate.
7. Suggested follow-up reading passes (topics to read in more depth).

REPORTS FOLLOW.
