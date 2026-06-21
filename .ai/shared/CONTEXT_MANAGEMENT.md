# Context Management

> Vendor-neutral. How any agent manages its working memory so it saves tokens WITHOUT
> losing correctness. Distilled from the project's context-discipline rules and the
> promoted lessons in [LESSONS.md](LESSONS.md).

**Correctness outranks token savings.** If economizing would risk a wrong result, do
not economize — say so instead. The rules below are token-efficiency tactics that must
never trade away accuracy.

## The five context tiers

Treat context as five tiers with different lifetimes and different homes. Know which
tier a piece of information belongs to, and put it there.

| Tier | What it holds | Where it lives | Lifetime |
|---|---|---|---|
| 1. Permanent | Universal process / tooling / workflow lessons | [LESSONS.md](LESSONS.md), the protocol files in `.ai/shared/` | Across all sessions and features |
| 2. Project | Stack, structure, product intent, standards | [PROJECT_CONTEXT.md](PROJECT_CONTEXT.md), [ARCHITECTURE.md](ARCHITECTURE.md), [CODING_STANDARDS.md](CODING_STANDARDS.md), [EARS.md](EARS.md), [SECURITY_RULES.md](SECURITY_RULES.md) | Stable; changes only when the project does |
| 3. Task | The active spec: requirements, design, the task list and its state | `specs/<feature>/{requirements,design,tasks}.md` | The life of one feature |
| 4. Scratch | This conversation: reasoning, intermediate tool output, the internal TODO list | The conversation only | Until `/clear` or compaction — VOLATILE |
| 5. Handoff | The compacted state another agent (or your next session) picks up | A handoff note — see [AGENT_HANDOFF_PROTOCOL.md](AGENT_HANDOFF_PROTOCOL.md) | The seam between sessions |

The durable source of truth is Tiers 1-3 (files on disk). The conversation (Tier 4) is
temporary working memory; do not let load-bearing state live ONLY there.

## Before /clear or compaction

The spec files are durable; the conversation is not. Before clearing or before
compaction triggers, write the current state into the task tier (`tasks.md` /
`design.md`) and, when handing to another agent, a handoff note:

- active spec and active task ID
- decisions made and their rationale
- what is done, what is in progress, what is next
- the list of modified files
- the exact test / build / run commands

**Never clear or compact in the middle of an unfinished task whose state lives only in
the conversation.** When compaction runs, ALWAYS preserve the items above — do not drop
them even to save space.

## Token-efficiency rules

- **Prefer a fresh, focused session per cohesive task** over one long sprawling
  session. Reload context by reading the spec; a clean context is also more accurate
  (less drift).
- **Set model + effort at the start of a session and do not switch mid-stream.** Each
  model/effort switch (and each compaction) invalidates the prefix cache for the whole
  prefix; cache-reads are far cheaper than re-sending full input.
- **Multi-session is NOT a cost win for a coupled feature** (tasks sharing
  primitives/data/lib): separate sessions do not share cache, so each re-pays cold
  context acquisition (measured ~30-40% more expensive). Split into per-task sessions
  ONLY for genuinely independent tasks, or to isolate a core domain's accuracy from
  long-context drift — a conscious accuracy trade, not a cost saving.
- **Read narrowly.** Read only the part of a large file you need. Do not re-read a file
  you just edited to "verify" — the edit would have failed otherwise.
- **Keep the durable files lean, but never remove a rule that prevents a real
  mistake.** Pruning that risks a wrong result is not a saving.
- **Cite IDs and paths, not pasted blocks.** Reference REQ-IDs and file paths; quote
  exact text only when it is load-bearing (a signature, a bug, a guard regex).
