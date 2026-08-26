---
name: spec-retro
description: Create a lean session retrospective at the end of a work session. Use when wrapping up, before /clear, while session history is still in context.
disable-model-invocation: true
allowed-tools:
  - Bash
  - Read
  - Write
  - Glob
---

# Session Retrospective (lean)

Produce a SHORT retrospective. Run at the END of a work session, BEFORE /clear or
compaction, while the full session history is still in context.

**Skip entirely (no file, no commit) if this session changed no files AND produced no
new durable lesson** — e.g. pure research / conversation. Do not manufacture an empty retro.

Design goal: capture only the durable, reusable signal — **cost, files changed,
and lessons** — and skip long reflective narrative. Output should be ~6-9k tokens,
not 20k. The expensive tier is output; do not pad. Every section below is the
WHOLE template. Do not add ceremony sections (AI Diary, Co-Creation Map,
Communication Dynamics, Seeds, Teaching Moments, etc.) — those were removed on
purpose to cut output cost.

## Steps

1. **Gather session data**:
   - Changed files — check BOTH tracked and untracked (this repo keeps `app/` as
     untracked `??`, so `git diff --stat` alone misses real code):
     `git status --short` and `git log --oneline -10`
   - Timestamp: `TZ='Asia/Bangkok' date +"%Y-%m-%d %H:%M"` (GMT+7)
   - This session's literal id (capture the VALUE, not the env-var name — a retro is
     a one-session historical record, so the resolved uuid makes the cost reconcilable
     later even after the env changes): `echo $CLAUDE_CODE_SESSION_ID`
     → fills **Session ID** in **Session Cost**.
   - This session's real cost from the authoritative ledger (Claude Code's own
     statusline field `.cost.total_cost_usd` — never recompute from transcript):
     `cat ~/.claude/cost-sessions/$CLAUDE_CODE_SESSION_ID.json 2>/dev/null`
     → fills **Session Cost**. If absent, write "cost unavailable (ledger not
     active)". Subscription bills nothing per token; this is an estimate.
   - Per-model / cache-tier breakdown (tokens from transcript, cost allocated from
     the ledger total — never recomputed):
     `python3 scripts/session-cost.py --breakdown-only "$CLAUDE_CODE_SESSION_ID"`
     Paste its markdown verbatim at `<BREAKDOWN>`. If it prints the
     "breakdown ไม่พร้อม" fallback, paste that line as-is.
   - รายละเอียด/ข้อห้าม cost (dedup, การปันส่วน, ข้อจำกัด ledger, multi-session) ดู
     `references/cost-accounting.md` — อ่านเมื่อต้องตีความตัวเลขเกิน template ปกติ.

2. **Write the file** at `retrospectives/YYYY-MM/DD/HH.MM_<scope-slug>.md`
   (`<scope-slug>` = short kebab-case of the primary focus, e.g. `task4-header-nav`;
   MANDATORY — a bare `HH.MM_retrospective.md` is indistinguishable by filename).

   **ภาษา (บังคับ)**: เนื้อหาเป็นภาษาไทย (คำอธิบาย, สรุป, ตาราง). คงอังกฤษเฉพาะ: code,
   path/ชื่อไฟล์, ชื่อ command, error message, technical term. **ห้าม emoji ทุกชนิด**
   ในไฟล์. อนุญาตลูกศร `→`.

   Use EXACTLY this template (all sections required, but keep each one tight):

   ```markdown
   # Session Retrospective

   **Session Date**: YYYY-MM-DD
   **Time**: ~HH:MM–HH:MM GMT+7 (~X min)
   **Primary Focus**: [สรุปสั้น 1 บรรทัด ภาษาไทย]
   **Type**: [Feature | Bugfix | Research | Refactor | Tooling]

   ## Session Cost

   - Session ID: `<resolved-uuid>` (ค่า literal จาก `echo $CLAUDE_CODE_SESSION_ID` — ไม่ใช่ชื่อ env var)
   - Total (estimated; subscription bills nothing per token): $X.XX
   - Duration ~Xm / Lines +A / -B (from ledger)
   - Source: `.cost.total_cost_usd` via `~/.claude/cost-sessions/<resolved-uuid>.json`

   <BREAKDOWN>
   (แทนด้วย output ของ `scripts/session-cost.py --breakdown-only` — ตาราง token/model
   + cost ปันส่วน. ถ้า transcript ไม่มี usage แปะบรรทัด fallback ที่ script พิมพ์)

   ## Summary

   [2-4 ประโยค: ทำอะไรเสร็จ, REQ ID ที่ปิด, ผล verify (เขียว/เลข). จบ.]

   ## Files Changed

   [รายการไฟล์จาก `git status --short` (รวม untracked) — path + (+X/-Y ถ้ารู้) +
   เหตุผลสั้นต่อไฟล์ 1 วรรค. โค้ดจริงใน untracked path ต้องไม่ตกหล่น.]

   ## Lessons Learned

   เพิ่มเฉพาะบทเรียน reusable, mistake-preventing จริง (0 ก็ได้ถ้าไม่มี). รูปแบบเดียวกับ
   `.ai/shared/LESSONS.md` เพื่อ promote ตรงในขั้นถัดไป:

   - **Pattern**: [สิ่งที่ทำ/กับดัก] — **Why**: [ทำไมถึงสำคัญ/กันพลาดอะไร]
   - **Discovery**: [สิ่งที่เพิ่งรู้] — **Why**: [นำไปใช้อย่างไร]

   ## Next Steps

   - [ ] [task ต่อไป / TODO ค้าง]
   ```

3. **Promote durable lessons (token-safe)**:
   Do NOT append lessons to CLAUDE.md. Add ONLY genuinely reusable, mistake-preventing
   lessons, and prune stale/duplicate ones. Route by scope:
   - Universal (process / workflow / git / CC tooling — applies on any task) →
     `.ai/shared/LESSONS.md` (always-on prefix — keep it lean).
   - Stack-specific implementation patterns (the project UI framework / styling system,
     its test runner, language/type tooling) → a `<stack>.md` profile under
     `.ai/shared/stack/` (the optional, project-supplied profile extension point — loads
     only when such a profile exists; empty by default).
   - Browser-verify / probe recipes (Playwright/MCP/viewport/probe methodology) →
     `.claude/skills/spec-implement/references/browser-verify.md` (loaded only
     during the verify phase).
   - Cost-accounting mechanics →
     `.claude/skills/spec-retro/references/cost-accounting.md` — NOT lessons.md
     (only the one-line kernel lives there).

4. **Steering sync** (before commit): compare ground truth against the canonical
   steering — the project's declared dependencies (e.g. `package.json` for a Node
   project) vs `.ai/shared/CODING_STANDARDS.md`, new files in the project source tree
   vs the layout in `.ai/shared/ARCHITECTURE.md`, and the `paths:` frontmatter globs in
   any path-scoped stack stub under `.claude/rules/` vs real paths. Fix any drift now,
   in the same commit.

5. **Stop before commit** (Task 5 contract): stage nothing and do NOT run `git commit`.
   Pane-loop treats the retro as complete when a `retrospectives/**/*.md` artifact is new
   or its bytes changed — no commit is expected from this skill. Committing belongs to the
   human-authorized Ship flow; leave staged changes for it.

## Critical requirements

- **Cost**: always fill from the ledger; never recompute from the transcript.
- **Files Changed**: include untracked (`??`) paths — `git status --short`, not just `--stat`.
- **Lessons**: only durable, reusable ones; quality over quantity (zero is fine).
- **Brevity**: no ceremony sections, no padding. Output is the most expensive tier.
- **Time Zone**: GMT+7 (Bangkok).
- **Sequencing**: manual skill — run before clearing/compacting, never after.
