// spec-edit-guard.js — OpenCode plugin (Tier 2 harness hook).
//
// Thin adapter ONLY: when an APPROVED requirements.md is edited while its sibling
// tasks.md still has open tasks, surface a warning. Delegates the decision to the
// single-source engine .ai/bin/check-spec-edit.sh — no logic lives here, same engine
// Claude/Codex use. Advisory ONLY: it console.error's and DOES NOT throw, so it never
// blocks the edit (mirrors Claude's additionalContext warning and Codex's stderr).
//
// file.edited fires AFTER the write — fine for a post-hoc advisory. (Unlike task-gate,
// there is nothing to hard-block here; this is a reminder, not a gate.)
//
// Runtime: OpenCode runs plugins under Bun and injects a `$` shell tag.
// NOTE: confirm the file.edited payload shape against opencode.ai/docs/plugins — the key
// that carries the edited file path may differ by version, so we probe likely shapes
// (same probing as task-gate.js).
export const SpecEditGuard = async ({ $ }) => ({
  "file.edited": async (input, output) => {
    const file =
      input?.file ??
      input?.path ??
      input?.filepath ??
      output?.file ??
      output?.path ??
      "";
    if (!file) return;

    // Only an approved-spec requirements.md is interesting; the engine re-checks the
    // approved/open-task conditions and prints nothing when they do not hold.
    if (!/\.(ai|claude)\/specs\/.*\/requirements\.md$/.test(file)) return;

    const r = await $`./.ai/bin/check-spec-edit.sh ${file}`.nothrow().quiet();
    const msg = r.stdout.toString().trim();
    // Advisory only: surface, never throw/block.
    if (msg) console.error(msg);
  },
});
