---
name: spec-sync-github
description: Mirror a spec's tasks onto GitHub as an Epic issue + native sub-issues, idempotently, preserving the REQ spine. Use to give teammates visibility of spec progress.
argument-hint: <feature> [--dry-run] [--epic-only]
---

# Sync spec to GitHub Issues: $ARGUMENTS

Mirror a spec's `tasks.md` onto GitHub Issues so teammates see progress. The spec
files stay the SOURCE OF TRUTH; the issues are an idempotent PROJECTION. Re-running
must UPDATE, never duplicate.

Parse `$ARGUMENTS`: first token is `<feature>` (a folder under `.ai/specs/`);
flags `--dry-run` (preview only, never write) and `--epic-only` (epic, no sub-issues).

Transport: use the GitHub MCP tools (`mcp__plugin_github_github__*`), NOT `gh` in
Bash — the RTK hook rewrites bash stdout into a summary, which mangles `gh --json`
output and loses issue numbers. MCP tools return structured fields directly and skip
permission prompts. Use Bash ONLY for read-only spec parsing and writing the manifest file.

Repository binding (REQ-4.7–4.9): derive the target repo from the current checkout,
never from a hardcoded value.

```bash
ORIGIN_URL="$(git remote get-url origin 2>/dev/null)" || { echo "REPO_ORIGIN_MISSING" >&2; exit 2; }
OWNER_REPO="$(python3 -c 'import re,sys; u=sys.argv[1]; m=re.search(r"[:/]([^/:]+/[^/]+?)(?:\.git)?/?$",u); print(m.group(1) if m else "")' "$ORIGIN_URL")"
[[ -n "$OWNER_REPO" ]] || { echo "REPO_ORIGIN_MISSING" >&2; exit 2; }
```

Then compare against `.github-sync.json`'s `"repo"` field BEFORE any GitHub I/O;
a mismatch stops with `REPO_MANIFEST_MISMATCH` (exit 1). Sync only ever targets
the repo recorded in the feature's own manifest AND resolved from `origin`.

## Steps

1. Resolve + guard. Resolve `<feature>` like spec-implement: if the conversation does
   not name one and `.ai/specs/` holds many, ask — never guess. Refuse to run if
   `requirements.md` (or `tasks.md`) is still `> Status: draft`, unless `--epic-only`:
   do not publish unapproved scope to teammates. State the reason and stop.

2. Trace gate (blocking). Run `scripts/spec-trace.sh <feature>`. If it exits non-zero
   (REQ spine incomplete), STOP — never publish a broken spine. A bugfix spec with no
   `## REQ-N:` headings makes the script exit 0 with a skip message; that is expected
   — proceed but omit the REQ coverage table (step 6).

3. Parse artifacts (read-only Bash). From `tasks.md`, per task: the number
   (`- [ ] N.` / `- [x] N.`), the headline (text up to the first ` — `), the
   scope/done text, the `Satisfies:` IDs, `Depends on:` task numbers, `Verify:`, the
   checkbox state, and the `Evidence:` block. Read the `> Status:` line from
   `requirements.md`; read the `## Requirement Traceability` table from `design.md`
   (if present). A bugfix spec has no `Satisfies:`/`design.md` — take the task title +
   its sub-bullets as scope, and the inline B-IDs (e.g. `(B1.1, B1.2)`) as the
   "Satisfies" content.

4. Load or init the manifest `.ai/specs/<feature>/.github-sync.json` (schema
   below). If it is missing, BEFORE creating anything, recover: `search_issues` for
   the body marker `spec-sync: feature=<feature>` and rebuild the manifest from any
   hits. This is the duplicate guard when the manifest was lost.

5. Order tasks topologically by `Depends on:` so a dependency's issue exists before a
   dependent references its `#NN`.

6. Build the desired issue set and compute a sha256 `bodyHash` per issue (epic + each
   task) from its rendered body. Diff against the manifest to label each:
   `create` / `update` (hash changed) / `close` (task now `[x]`, issue still open) /
   `skip` (unchanged). Render bodies from
   `.claude/skills/spec-sync-github/references/body-templates.md`.

7. Preview (default on the first sync of a feature, or whenever `--dry-run`). Print a
   table `action | task | title | issue# (or NEW) | reason`, plus the rendered epic
   body. If `--dry-run`, STOP here. Otherwise, on the FIRST real sync of a feature,
   show the preview and wait for my confirmation before any write.

8. Ensure labels exist — run `scripts/bootstrap-labels.sh <feature>` (idempotent), or
   get-or-create via the label tools: `spec:<feature>`, `spec-epic`, `spec-task`,
   `req-spine` (epic only).

9. Execute in dependency order:
   a. Ensure the epic issue via `issue_write` (create or update). Body = epic
      template. Labels `spec:<feature>`, `spec-epic`, `req-spine`. Record number +
      node id + hash in the manifest immediately.
   b. For each task in DAG order: `issue_write` create/update the task issue (body =
      sub-issue template; labels `spec:<feature>`, `spec-task`). Set its state to
      match the checkbox: `[x]` -> closed, `[ ]` -> open. If the manifest entry is not
      yet `subIssueLinked`, attach it under the epic with `sub_issue_write`, then set
      `subIssueLinked: true`. Write the manifest entry (number, node id, hash, state)
      INSIDE the loop, not batched at the end — a mid-run crash must not lose what was
      created.

10. Write the manifest and report (Thai): a table of created / updated / closed /
    skipped with clickable `#NN`, the epic URL, and the live "N of M tasks done"
    count. Optionally drop one `add_issue_comment` on the epic with the sync time.

## Idempotency contract

Identity comes from the manifest (fallback: marker search). Re-running UPDATES.
`bodyHash` skips no-op writes. `subIssueLinked` prevents a double attach (attaching
twice errors). State mirrors the checkbox, so flipping a task to `[x]` and re-syncing
closes its sub-issue and advances the epic progress bar.

## Manifest schema (`.ai/specs/<feature>/.github-sync.json`, git-committed)

```json
{
  "schemaVersion": 1,
  "feature": "<feature>",
  "repo": "<owner/repo ที่ resolve จาก git remote get-url origin>",
  "epic": { "issue": 0, "nodeId": "", "bodyHash": "sha256:..." },
  "tasks": {
    "1": { "issue": 0, "nodeId": "", "subIssueLinked": true, "bodyHash": "sha256:...", "state": "open|closed" }
  },
  "lastSync": "<ISO-8601, stamped at run>",
  "labelsEnsured": ["spec:<feature>", "spec-epic", "spec-task", "req-spine"]
}
```

Key tasks by their stable number (`N.` in tasks.md) — the only stable task identity.

## Do NOT

- Do NOT write issue numbers into `tasks.md` — it would trip `task-gate.sh` (fires on
  any edit adding `[x]`) and risk `spec_trace.py`'s `Satisfies:` parser. All link
  state lives in the sidecar manifest only.
- Do NOT run `gh` for issue I/O (RTK rewrites its output); if you must, wrap it in
  `rtk proxy`.
- Do NOT re-slice a task into smaller issues — one task = one sub-issue, preserving
  the coarse-task model and the REQ spine. (This is why the matt-pocock `to-issues`
  skill is intentionally NOT used here.)
