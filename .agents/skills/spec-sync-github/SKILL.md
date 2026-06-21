---
name: spec-sync-github
description: Mirror a spec's tasks onto GitHub as an Epic issue + native sub-issues, idempotently, preserving the REQ spine. Use to give teammates visibility of spec progress.
---

This is the GitHub-sync phase of the spec-driven workflow.

Read and follow the canonical procedure in `.claude/skills/spec-sync-github/SKILL.md`
(the authoritative steps) together with `.ai/shared/TASK_PROTOCOL.md`. The spec files
stay the SOURCE OF TRUTH; the GitHub issues are an idempotent PROJECTION. Re-running
must UPDATE, never duplicate (identity comes from the per-feature manifest, with a
marker search as fallback).

Run the trace gate (`scripts/spec-trace.sh <feature>`) before publishing; never publish
a broken REQ spine, and never publish unapproved scope (a `> Status: draft` spec)
unless `--epic-only`.

Harness note: the GitHub transport is harness-specific. The canonical steps assume a
GitHub MCP server (`mcp__plugin_github_github__*`). Under Codex / OpenCode, use a GitHub
MCP server configured for that harness, or fall back to the `gh` CLI for issue I/O. The
idempotency contract (manifest-keyed, `bodyHash`-skipped, `subIssueLinked` state) is
unchanged across harnesses.
