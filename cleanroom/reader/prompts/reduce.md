Below are conceptual notes, written by readers of the separate units of the repository "{{REPO}}". Combine them into one coherent report on the whole project, for the CalcDb team. Follow the firewall rules strictly. Where the notes contradict each other, say so. Remove duplication.

Structure:

1. Purpose and overall architecture: the layers and how a request flows through them, by role.
2. What the project does that is novel or notably strong, and the evidence that it works (implemented, measured, or only designed).
3. Concurrency and transaction model, in depth.
4. Storage, durability and I/O model, including platform-specific paths.
5. Query processing: parsing, planning, execution model, expression and function evaluation.
6. Runtime and async model: cancellation, structured concurrency, background work, deterministic testing support.
7. The performance campaign: what was tried, in what order, what helped, what hurt or was reverted, and the lessons.
8. Correctness and testing methodology, described so it can be rebuilt.
9. Building blocks and dependencies: what each provides, and how replaceable it is.
10. Development process signals: how the project is organised and run, and what looks productive vs overhead.
11. Lessons for CalcDb: a prioritised list. For each item, the idea, where it fits in CalcDb (format, ISAM, locking/MVCC, SQL, expression service, DAO/COM, test harness, runtime), and how confident you are that it transfers.
12. Open questions worth a follow-up reading pass.

NOTES FOLLOW.
