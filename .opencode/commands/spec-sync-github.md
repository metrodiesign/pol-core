---
description: Mirror a spec's tasks onto GitHub as an Epic issue + native sub-issues, idempotently, preserving the REQ spine. Use to give teammates visibility of spec progress.
agent: build
---

Run the spec-sync-github phase for $ARGUMENTS. Follow .agents/skills/spec-sync-github/SKILL.md (and the canonical .claude/skills/spec-sync-github/SKILL.md).

Note: the GitHub transport is harness-specific. The canonical steps assume a GitHub MCP server; under OpenCode, use a GitHub MCP server configured in opencode.json, or fall back to the `gh` CLI for issue I/O. The idempotency contract (manifest-keyed, body-hash-skipped, sub-issue link state) is unchanged.
