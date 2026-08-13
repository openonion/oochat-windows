# Documentation

| Document | What it is | Status |
|---|---|---|
| [DEVELOPMENT.md](./DEVELOPMENT.md) | Developer guide: architecture, repository layout, setup, build/test commands, README screenshot regeneration, CI, agent endpoints, and implementation status | Living |
| [PROJECT_BRIEF.md](./PROJECT_BRIEF.md) | The original client-supplied requirements (verbatim), plus a requirement → implementation traceability table and the current known limitations | Living — update the table, never the brief |
| [TEST_PLAN.md](./TEST_PLAN.md) | Layered test strategy, coverage ratchet, current test-count snapshot, 15 target E2E flows, UI-automation standards, and the owned backlog | Living |
| [SESSION_MESSAGE_STRUCTURE.md](./SESSION_MESSAGE_STRUCTURE.md) | The ConnectOnion wire protocol: core frames, current extension-frame coverage, streaming, reconnect, and session synchronization | Authoritative for the frames specified in §1–31; the implementation dispatch table defines the complete supported extension set |
| [GIT_COMMIT_CONVENTION.md](./GIT_COMMIT_CONVENTION.md) | Conventional Commits types and scopes used in this repo | Living |
| [OPTIMIZATION.md](./OPTIMIZATION.md) | Implemented architecture, performance, reliability, CI, localization, and diagnostics improvements | Living |
| [PERFORMANCE.md](./PERFORMANCE.md) | Launch-time and memory benchmark: method, budgets, current baseline, and how to run `scripts/Measure-Performance.ps1` | Living — re-ratify budgets when a run sets a new baseline |
| [PERFORMANCE_AUDIT_2026-07-25_EN.md](./PERFORMANCE_AUDIT_2026-07-25_EN.md) | Pre-release performance and trimming audit captured before Release trimming was enabled | **Historical** — preserve the measurements; current decisions live in `PERFORMANCE.md` and `TRIMMING.md` |
| [RELEASE.md](./RELEASE.md) | How a `v*` tag becomes a self-contained portable ZIP release: versioning, payload and size gates, checksums, and the validation matrix; MSIX publishing is currently paused | Living |
| [TRIMMING.md](./TRIMMING.md) | Release trimming decision, warning inventory, runtime evidence, and how to reproduce it with `scripts/Invoke-TrimAudit.ps1` | Living — the gate, not a recommendation |
| [MEMORY_LEAK_INVESTIGATION.md](./MEMORY_LEAK_INVESTIGATION.md) | Conversation-navigation leak investigation, root causes, measured before/after data, and the automated plateau test | Living — update when a new leak signature or threshold is established |
| [CONCURRENCY.md](./CONCURRENCY.md) | Where threads come from, which primitive guards which shared state, and the sharp edges (including the post-`Closed` dispatcher race) | Living |

## What is *not* here

- **[`../CLAUDE.md`](../CLAUDE.md)** — an authoritative, kept-current description of the architecture
  (solution layout, protocol/WinUI split, persistence model, XAML gotchas). It stays at the repo root
  because tooling looks for it there. **If a document in this folder disagrees with `CLAUDE.md`,
  `CLAUDE.md` is right.**
- **[`../AGENTS.md`](../AGENTS.md)** — the same architecture document, for Codex. Its substantive
  content is kept identical to `CLAUDE.md` apart from the tool-specific opening. **Edit both or neither**; two architecture
  documents that disagree is worse than one that is slightly out of date.
- **`ConnectOnion.WinUIClient/Shell/README.md`** and **`ConnectOnion.WinUIClient/Controls/README.md`**
  — folder-local conventions, including why those folders deliberately do not match their namespaces.
- **[`../README.md`](../README.md)** — the user-facing UI gallery, download, setup, usage, data-safety, update, and troubleshooting guide.
- `agent/` and `frontend/` — the vendored ConnectOnion agent examples and the legacy Electron client.
  **Both were deleted in `197a88d`**; the repo is C#-only now. The completed WinUI migration plan
  has also been removed from the current tree. Read them out of git history if you need the prior
  behavior or migration rationale.
