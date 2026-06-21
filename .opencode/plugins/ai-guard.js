// ai-guard.js — OpenCode plugin (Tier 2 harness hook).
//
// Thin adapter ONLY: intercepts the `bash` tool before it executes and delegates the
// decision to the single-source check engine in .ai/bin/. No guard logic lives here —
// keep all regex/policy in .ai/bin/check-*.sh so Claude, Codex, OpenCode and CI enforce
// byte-for-byte the same rules.
//
// Runtime: OpenCode runs plugins under Bun and injects a `$` shell tag.
// Block semantics: .ai/bin/check-*.sh exit 2 = block; we surface a block by throwing,
// which OpenCode treats as "deny this tool call".
//
// NOTE: confirm `output.args.command` against OpenCode's bash tool schema — the key that
// holds the command string for the bash tool may differ by OpenCode version. The git+CI
// floor (.githooks/ + ci.yml) still applies as the hard guarantee if this needs tuning.
export const AiGuard = async ({ $ }) => ({
  "tool.execute.before": async (input, output) => {
    if (input.tool !== "bash") return;

    // CONFIRM AGAINST OPENCODE BASH TOOL SCHEMA: key holding the command string.
    const cmd = output.args.command;
    if (!cmd) return;

    for (const c of ["check-destructive", "check-bypass"]) {
      const r = await $`./.ai/bin/${c}.sh ${cmd}`.nothrow().quiet();
      // exit 2 = block; throw to deny the tool call. (exit 0 = ok)
      if (r.exitCode === 2) {
        throw new Error(r.stderr.toString().trim() || `Blocked by .ai/bin/${c}.sh`);
      }
    }
  },
});
