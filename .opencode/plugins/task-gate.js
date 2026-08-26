// task-gate.js — OpenCode plugin (Tier 2 harness hook), Task 3 raw-selection.
//
// Thin adapter ONLY. tool.execute.before captures the FULL pre-write bytes of a
// spec tasks.md; file.edited reads the post-write bytes from disk; canonical
// ranges come from `spec_contract.py diff-ranges`; the verdict comes from
// `.ai/bin/gate-task.sh` (Evidence v2 + command resolution + safe cache +
// build/test with its exit mapping). No diff algorithm, no checkbox counting and
// no Evidence parsing lives here — parity means the SAME verdict as Claude,
// pre-commit and CI for identical raw selections (design §Adapter Seams).
//
// Timing note: file.edited fires after the write, so hard-block support is
// best-effort in this runtime; a red gate is surfaced loudly via throw and the
// git pre-commit Evidence gate + CI remain the durable floor.
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const SNAPSHOTS = new Map();

const TASKS_MD = /(?:^|\/)(?:\.ai|\.claude)\/specs\/[^/]+\/tasks\.md$/;

export const TaskGate = async ({ $ }) => ({
  "tool.execute.before": async (_input, output) => {
    // Pending tool call args carry the target path for Edit/Write-style tools.
    const args = output?.args ?? output?.input ?? {};
    const path = args.file_path ?? args.path ?? args.filePath ?? "";
    const normalized = String(path).replaceAll("\\", "/");
    if (!path || !TASKS_MD.test(normalized)) return;
    try {
      SNAPSHOTS.set(path, {
        before_exists: true,
        before_b64: readFileSync(path).toString("base64"),
      });
    } catch {
      SNAPSHOTS.set(path, { before_exists: false, before_b64: "" });
    }
  },

  "file.edited": async (input, output) => {
    const file =
      input?.file ?? input?.path ?? input?.filepath ??
      output?.file ?? output?.path ?? "";
    if (!file || !TASKS_MD.test(String(file).replaceAll("\\", "/"))) return;

    if (!SNAPSHOTS.has(file)) {
      const reason =
        `GATE_SNAPSHOT_MISSING: no pre-tool snapshot captured for ${file}; ` +
        "refusing to guess task/Evidence state (fail-closed)";
      console.error(reason);
      throw new Error(reason);
    }
    const snap = SNAPSHOTS.get(file);
    SNAPSHOTS.delete(file);

    const work = mkdtempSync(join(tmpdir(), "sdd-opencode-gate-"));
    try {
      const beforeFile = snap.before_exists
        ? join(work, "before.bin")
        : "-";
      if (snap.before_exists) {
        writeFileSync(beforeFile, Buffer.from(snap.before_b64, "base64"));
      }
      const afterFile = join(work, "after.bin");
      writeFileSync(afterFile, readFileSync(file));

      const emptyBase = join(work, "empty-base");
      writeFileSync(emptyBase, Buffer.alloc(0));
      const rangesFile = join(work, "ranges.json");
      await $`python3 scripts/spec_contract.py diff-ranges --before-file ${
        snap.before_exists ? beforeFile : emptyBase
      } --after-file ${afterFile} > ${rangesFile}`.nothrow();

      const verdict = await $`bash ./.ai/bin/gate-task.sh ${String(file)} ${
        snap.before_exists ? beforeFile : "-"
      } ${afterFile} ${rangesFile} opencode`
        .nothrow()
        .quiet();
      if (verdict.exitCode !== 0) {
        const stderrText = verdict.stderr?.toString?.() ?? "";
        const reason =
          stderrText.trim() ||
          `Task gate blocked for ${file}: Evidence/build/test red (exit ${verdict.exitCode})`;
        console.error(reason);
        throw new Error(reason);
      }
    } finally {
      await $`rm -rf ${work}`.nothrow().quiet();
    }
  },
});
