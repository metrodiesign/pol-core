#!/usr/bin/env python3
"""Task 6 fixtures — scripts/spec_retrofit.py planning + guarded writer."""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))

import spec_contract as sc
import importlib.util

_rf_spec = importlib.util.spec_from_file_location(
    "spec_retrofit", SCRIPTS / "spec-retrofit.py")
rf = importlib.util.module_from_spec(_rf_spec)
sys.modules["spec_retrofit"] = rf
_rf_spec.loader.exec_module(rf)


def run_cli(argv: list[str], repo: Path):
    env = dict(os.environ, SDD_RETROFIT_REPO=str(repo), PYTHONDONTWRITEBYTECODE="1")
    return subprocess.run(
        [sys.executable, str(SCRIPTS / "spec-retrofit.py"), *argv],
        capture_output=True, text=True, env=env,
    )


def git(repo: Path, *args: str, text_payload: tuple[str, str] | None = None) -> None:
    proc = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True)
    assert proc.returncode == 0, proc.stderr


class RetrofitSandbox(unittest.TestCase):
    """Temp git repository carrying .ai/specs/<feature>/ fixtures."""

    def setUp(self):
        self.repo = Path(tempfile.mkdtemp(prefix="retrofit-t6-"))
        git(self.repo, "init", "-q")
        git(self.repo, "config", "user.email", "t@t")
        git(self.repo, "config", "user.name", "t")
        self.features = self.repo / ".ai" / "specs"

    def tearDown(self):
        shutil.rmtree(self.repo, ignore_errors=True)

    def commit_all(self, message: str) -> None:
        git(self.repo, "add", "-A")
        git(self.repo, "commit", "-qm", message)

    def feature(self, name: str) -> Path:
        target = self.features / name
        target.mkdir(parents=True, exist_ok=True)
        return target


class CliContractTest(RetrofitSandbox):

    def test_mode_and_batch_validation_exit_two(self):
        for argv in (
            ["--batch", "canonical-complete"],
            ["--dry-run", "--apply-safe", "--check", "--batch", "evidence"],
            ["--dry-run"],
            ["--dry-run", "--batch", "bogus-batch"],
            ["--dry-run", "--batch", "final-all-spec"],      # read-only-only batch
            ["--apply-safe", "--batch", "final-all-spec"],
        ):
            with self.subTest(argv=argv):
                proc = run_cli(argv, self.repo)
                self.assertEqual(proc.returncode, 2, (argv, proc.stdout, proc.stderr))

    def test_scope_mismatch_is_reported_blocker(self):
        proc = run_cli(["--check", "--batch", "final-all-spec"], self.repo)
        self.assertEqual(proc.returncode, 1)
        payload = json.loads(proc.stdout)
        self.assertEqual(payload["verdict"], "policy-fail")


class DryRunPlanningTest(RetrofitSandbox):

    def prepare_alias_with_history(self) -> Path:
        directory = self.feature("alias-demo")
        tasks = directory / "tasks.md"
        # first historical state carried the explicit approved line
        tasks.write_text(
            "> Status: approved 2020-01-01\n\n- [ ] A1. work.\n     Satisfies: REQ-1.1\n",
            encoding="utf-8",
        )
        self.commit_all("seed approved")
        current = ("# Alias demo\n\n> Status: implemented 2025-05-05 (unit-verified)\n\n"
                   "- [ ] A1. work.\n     Satisfies: REQ-1.1\n")
        tasks.write_text(current, encoding="utf-8")
        self.commit_all("switched to alias")
        return directory

    def test_deterministic_sorted_reports_without_writes(self):
        directory = self.prepare_alias_with_history()
        before_bytes = (directory / "tasks.md").read_bytes()
        first = run_cli(["--dry-run", "--batch", "approved-aliases", "--format", "json"], self.repo)
        second = run_cli(["--dry-run", "--batch", "approved-aliases", "--format", "json"], self.repo)
        self.assertEqual(first.returncode, second.returncode)
        self.assertEqual(first.stdout, second.stdout, "dry-run must be deterministic")
        self.assertEqual((directory / "tasks.md").read_bytes(), before_bytes,
                         "dry-run must never write")

    def test_alias_action_carries_historical_proof(self):
        self.prepare_alias_with_history()
        payload = json.loads(run_cli(
            ["--dry-run", "--batch", "approved-aliases", "--format", "json"], self.repo,
        ).stdout)
        status_actions = [a for a in payload["actions"] if a["targetField"] == "status.line"]
        self.assertEqual(len(status_actions), 1)
        action = status_actions[0]
        self.assertEqual(action["action"], "rewrite")
        import base64
        self.assertIn(b"implemented", base64.b64decode(action["beforeBytesBase64"]))
        after = base64.b64decode(action["afterBytesBase64"])
        self.assertEqual(after.strip(), b"> Status: approved 2020-01-01")
        proof = action["proofs"][0]
        self.assertEqual(proof["kind"], "historical")
        self.assertEqual(proof["snippet"], "> Status: approved 2020-01-01")
        self.assertEqual(proof["sha256"], rf.sha256(proof["snippet"].encode()))
        commits = subprocess.run(
            ["git", "-C", str(self.repo), "log", "--format=%H"],
            capture_output=True, text=True,
        ).stdout.split()
        self.assertIn(proof["commit"], commits)

    def test_missing_history_becomes_missing_proof_blocker(self):
        directory = self.feature("ghost-alias")
        (directory / "tasks.md").write_text(
            "> Status: implemented 2026-01-01\n\n- [ ] Z. pending.\n", encoding="utf-8",
        )
        self.commit_all("seed unrelated")   # file itself stays untracked
        proc = run_cli(["--dry-run", "--batch", "approved-aliases"], self.repo)
        self.assertEqual(proc.returncode, 1)
        payload = json.loads(proc.stdout)
        codes = {blocker["code"] for blocker in payload["blockers"]}
        self.assertIn("MIGRATION_PROOF_MISSING", codes)

    def test_conflicting_approved_variants_fail_closed(self):
        directory = self.feature("conflicted")
        tasks = directory / "tasks.md"
        tasks.write_text(
            "> Status: approved 2020-01-01\n> Status: approved 2021-02-02\n\n- [ ] B. x.\n",
            encoding="utf-8",
        )
        self.commit_all("two truths")
        proc = run_cli(["--dry-run", "--batch", "approved-aliases"], self.repo)
        self.assertEqual(proc.returncode, 1)
        codes = {blocker["code"] for blocker in json.loads(proc.stdout)["blockers"]}
        self.assertIn("MIGRATION_PROOF_CONFLICT", codes)

    def test_annotated_canonical_splits_line_and_note(self):
        directory = self.feature("annotated")
        (directory / "requirements.md").write_text(
            "> Status: draft\n## REQ-1: X\n- 1.1 THE SYSTEM SHALL x\n", encoding="utf-8",
        )
        (directory / "design.md").write_text(
            "> Status: approved 2026-06-23, amended 2026-06-23\n## Build\n"
            "\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
            encoding="utf-8",
        )
        (directory / "tasks.md").write_text(
            "> Status: approved 2026-06-23, amended 2026-06-23\n"
            "- [ ] A. t.\n     Satisfies: REQ-1.1\n     Batch: core\n",
            encoding="utf-8",
        )
        self.commit_all("annotated corpus")
        payload = json.loads(run_cli(["--dry-run", "--batch", "approved-aliases"], self.repo).stdout)
        from collections import Counter
        field_counter = Counter((a["targetField"], a["action"]) for a in payload["actions"])
        self.assertEqual(field_counter,
                         Counter({("status.line", "rewrite"): 2, ("status.note", "insert"): 2}))
        import base64
        notes = [base64.b64decode(a["afterBytesBase64"]) for a in payload["actions"]
                 if a["targetField"] == "status.note"]
        self.assertTrue(all(note.startswith(b"> Notes:") and b"amended 2026-06-23" in note
                            for note in notes))
        blockers = [b for b in payload["blockers"]]
        self.assertEqual(blockers, [])

    def test_pending_review_annotation_conflicts_approval(self):
        directory = self.feature("pending-note")
        (directory / "tasks.md").write_text(
            "> Status: approved 2026-01-01, pending review by ops\n- [ ] Q. t.\n",
            encoding="utf-8",
        )
        self.commit_all("annotated pending")
        payload = json.loads(run_cli(["--dry-run", "--batch", "approved-aliases"], self.repo).stdout)
        codes = {blocker["targetField"]: blocker["code"] for blocker in payload["blockers"]}
        self.assertEqual(codes.get("status.note"), "MIGRATION_PROOF_CONFLICT")


import base64  # noqa: E402  (used in tests below)


class EvidenceProbeTest(RetrofitSandbox):

    def build_feature(self) -> tuple[Path, bytes]:
        directory = self.feature("legacy-evidence")
        body = (
            "> Status: approved 2026-01-01\n"
            "# Legacy demo\n"
            "- [x] L1. shipped.\n"
            "     - test: `pytest -q` -> 9 passed\n"
            "     - test: `ruff .` -> All checks passed!\n"
            "     viewports: 375 / 768 / 1440 observed\n"
        )
        (directory / "tasks.md").write_text(body, encoding="utf-8")
        self.commit_all("legacy evidence seed")
        return directory, body.encode()

    def test_field_level_actions_require_same_task_owner(self):
        self.build_feature()
        payload = json.loads(run_cli(["--dry-run", "--batch", "evidence"], self.repo).stdout)
        fields = {action["targetField"] for action in payload["actions"]}
        self.assertIn("evidence.observations", fields)
        self.assertIn("evidence.viewports", fields)
        blockers_by_field = {blocker["targetField"] for blocker in payload["blocksrs"]} \
            if False else {blocker["targetField"] for blocker in payload["blockers"]}
        self.assertIn("evidence.deviations", blockers_by_field)

    def test_observations_rewrite_keeps_result_verbatim(self):
        self.build_feature()
        payload = json.loads(run_cli(["--dry-run", "--batch", "evidence"], self.repo).stdout)
        obs = [a for a in payload["actions"] if a["targetField"] == "evidence.observations"][0]
        rebuilt = base64.b64decode(obs["afterBytesBase64"]).decode()
        self.assertIn("Evidence:", rebuilt)
        self.assertIn("`pytest -q` -> 9 passed", rebuilt)
        self.assertIn("`ruff .` -> All checks passed!", rebuilt)

    def test_empty_directory_is_ambiguous_blocker_only(self):
        directory_hollow = self.feature("hollow")
        del directory_hollow
        payload = json.loads(run_cli(
            ["--dry-run", "--batch", "ambiguous-directories"], self.repo).stdout)
        self.assertEqual(payload["verdict"], "policy-fail")
        self.assertTrue(all(blocker["targetField"] == "directory.shape"
                            for blocker in payload["blockers"]))
        empty_codes = {blocker["code"] for blocker in payload["blockers"]}
        self.assertIn("MIGRATION_PROOF_MISSING", empty_codes)


class TraceAndContainerTest(RetrofitSandbox):

    def prepare_trace_fixture(self) -> Path:
        directory = self.feature("tracey")
        (directory / "requirements.md").write_text(
            "> Status: approved 2026-01-01\n## REQ-7: Cap\n- 7.1 THE SYSTEM SHALL cap\n",
            encoding="utf-8",
        )
        (directory / "design.md").write_text(
            "> Status: approved 2026-01-01\n## Build\n"
            "\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n"
            "| 7.1 | Build |\n| 9.9 | Build |\n",
            encoding="utf-8",
        )
        (directory / "tasks.md").write_text(
            "> Status: approved 2026-01-01\n- [ ] T. d.\n     Satisfies: REQ-7.1\n",
            encoding="utf-8",
        )
        self.commit_all("trace seed")
        return directory

    def test_exact_dotted_ref_gets_ref_action_fuzzy_is_blocker(self):
        self.prepare_trace_fixture()
        payload = json.loads(run_cli(["--dry-run", "--batch", "canonical-complete"], self.repo).stdout)
        import base64
        refs = {(a["path"].endswith("design.md"),
                 base64.b64decode(a["afterBytesBase64"])) for a in payload["actions"]
                if a["targetField"] == "trace.ref"}
        self.assertIn((True, b"REQ-7.1"), refs)
        sections = [b for b in payload["blockers"] if b["targetField"] == "trace.ref"]
        self.assertTrue(any("9.9" in b["message"] for b in sections))

    def test_legacy_container_round_trip_preserves_bytes(self):
        payloads = [
            b"- test: note without results\n",
            "line with emoji 🎯 and ``` triple backticks\n".encode(),
            "```sdd-legacy marker collision\n".encode(),
        ]
        for index, payload in enumerate(payloads):
            with self.subTest(index=index):
                container = rf.build_legacy_container(payload)
                self.assertTrue(rf.container_roundtrip_ok(container, payload),
                                container.decode())
                # canonical parser ignores fenced block entirely
                diagnostics = sc.parse_task_blocks(
                    f"- [x] K. d.\n{container.decode()}".encode(), Path("t.md"),
                )[1]
                self.assertFalse([diag for diag in diagnostics
                                  if diag.code.startswith(("TASK_",))])

    def test_unmappable_legacy_text_becomes_container_action(self):
        directory = self.feature("container-demo")
        (directory / "tasks.md").write_text(
            "> Status: approved 2026-01-01\n"
            "- [x] C1. done.\n"
            "     - test: freeform note, no command/result shape here\n",
            encoding="utf-8",
        )
        self.commit_all("container seed")
        payload = json.loads(run_cli(["--dry-run", "--batch", "evidence"], self.repo).stdout)
        containers = [a for a in payload["actions"] if a["targetField"] == "legacy.container"]
        self.assertEqual(len(containers), 1)
        decoded = base64.b64decode(containers[0]["afterBytesBase64"])
        self.assertTrue(rf.container_roundtrip_ok(decoded, base64.b64decode(
            containers[0]["beforeBytesBase64"])))


class PlannerSafetyTest(unittest.TestCase):

    def test_overlapping_spans_rejected_as_conflict(self):
        base = b"one\ntwo\nthree\n"
        action_a = rf.RetrofitAction(
            batch_id="evidence", path="p.md", target_field="evidence.viewports", task_id="1",
            field_span=(0, 8), before_bytes=base[:8], after_bytes=b"x", proofs=(),
        )
        action_b = rf.RetrofitAction(
            batch_id="evidence", path="p.md", target_field="status.line", task_id="",
            field_span=(4, 12), before_bytes=base[4:12], after_bytes=b"y", proofs=(),
        )
        blockers = rf.validate_planned_actions([action_a, action_b])
        self.assertTrue(any(blocker.code == "MIGRATION_PROOF_CONFLICT" for blocker in blockers))

    def test_compose_applies_descending_without_drift(self):
        before = b"aXc\nkeep\ndYf\n"
        rewrite_top = rf.RetrofitAction(
            batch_id="b", path="f", target_field="status.line", task_id="", field_span=(0, 3),
            before_bytes=b"aXc", after_bytes=b"AAA", proofs=(),
        )
        rewrite_bottom = rf.RetrofitAction(
            batch_id="b", path="f", target_field="evidence.deviations", task_id="1",
            field_span=(9, 12), before_bytes=b"dYf", after_bytes=b"ZZZ", proofs=(),
        )
        composed = rf.compose_file(before, [rewrite_top, rewrite_bottom])
        self.assertEqual(composed, b"AAA\nkeep\nZZZ\n")


class GuardedWriterTest(RetrofitSandbox):
    """Direct in-process engine calls must bind to the sandbox repo too."""

    def setUp(self):
        super().setUp()
        self._prev_repo_env = os.environ.get("SDD_RETROFIT_REPO")
        os.environ["SDD_RETROFIT_REPO"] = str(self.repo)

    def tearDown(self):
        if self._prev_repo_env is None:
            os.environ.pop("SDD_RETROFIT_REPO", None)
        else:
            os.environ["SDD_RETROFIT_REPO"] = self._prev_repo_env
        super().tearDown()

    def prepare_alias_with_history_local(self):
        directory = self.feature("apply-demo")
        tasks = directory / "tasks.md"
        tasks.write_text(
            "> Status: approved 2019-09-09\n\n- [ ] A1. work.\n", encoding="utf-8",
        )
        self.commit_all("seed approved apply")
        tasks.write_text(
            "> Status: implemented 2025-05-05\n\n- [ ] A1. work.\n", encoding="utf-8",
        )
        self.commit_all("alias for apply")
        return directory

    def test_dirty_tree_blocks_before_any_write(self):
        directory = self.prepare_alias_with_history_local()
        (directory / "untracked.txt").write_text("noise", encoding="utf-8")
        proc = run_cli(["--apply-safe", "--batch", "approved-aliases"], self.repo)
        self.assertEqual(proc.returncode, 2)
        self.assertIn("MIGRATION_DIRTY_TREE", proc.stdout)
        self.assertIn(b"implemented", (directory / "tasks.md").read_bytes())

    def test_apply_safe_full_cycle_then_journal_cleanup(self):
        directory = self.prepare_alias_with_history_local()
        proc = run_cli(["--apply-safe", "--batch", "approved-aliases"], self.repo)
        self.assertEqual(proc.returncode, 0, proc.stdout)
        text = (directory / "tasks.md").read_text(encoding="utf-8")
        self.assertIn("> Status: approved 2019-09-09", text)
        self.assertNotIn("implemented 2025-05-05", text)
        self.assertFalse(rf.journal_exists("approved-aliases"))

    def test_journal_presence_requires_recovery_before_modes(self):
        directory = self.prepare_alias_with_history_local()
        journal = rf.Journal(batch_id="approved-aliases", captured_head="deadbeef",
                             targets=[rf.JournalTarget(
                                 path=".ai/specs/apply-demo/tasks.md",
                                 before_sha256=rf.sha256(b"x"), planned_sha256=rf.sha256(b"y"),
                                 original_file="missing.bin",
                             )])
        rf.write_journal("approved-aliases", journal, {"missing": b""})
        try:
            for mode_flag in ("--dry-run", "--check"):
                proc = run_cli([mode_flag, "--batch", "approved-aliases"], self.repo)
                self.assertEqual(proc.returncode, 2, mode_flag)
                self.assertIn("MIGRATION_RECOVERY_REQUIRED", proc.stdout)
        finally:
            rf.clear_journal("approved-aliases")

    def test_restore_restores_when_current_matches_planned_and_keeps_unknown(self):
        (self.repo / ".ai/specs/apply-demo").mkdir(parents=True, exist_ok=True)
        target_rel = ".ai/specs/apply-demo/tasks.md"
        (self.repo / target_rel).write_bytes(b"BEFORE LINE\n")
        before = b"BEFORE LINE\n"
        planned = b"AFTER LINE\n"
        unknown = b"TAMPERED BY SOMEONE ELSE\n"
        journal = rf.Journal(batch_id="restory", captured_head="cafe",
                             targets=[rf.JournalTarget(
                                 path=target_rel, before_sha256=rf.sha256(before),
                                 planned_sha256=rf.sha256(planned), applied=True,
                                 original_file="o.bin",
                             )])
        rf.write_journal("restory", journal, {target_rel: before})
        try:
            restored = self.repo / target_rel
            restored.write_bytes(planned)               # tool wrote it last -> restore owed
            ok, failures = rf.restore_from_journal("restory")
            self.assertTrue(ok, failures)
            self.assertEqual(restored.read_bytes(), before)

            # concurrent-owner case: neither before nor planned -> preserved, keep journal
            rf.write_journal("restory", journal, {target_rel: before})
            restored.write_bytes(unknown)
            ok2, failures2 = rf.restore_from_journal("restory")
            self.assertFalse(ok2)
            self.assertEqual(failures2, [target_rel])
            self.assertEqual(restored.read_bytes(), unknown, "must NOT overwrite foreign edit")
        finally:
            rf.clear_journal("restory")

    def test_head_change_between_files_aborts_with_engine_code(self):
        directory = self.feature("apply-demo")
        tasks = directory / "tasks.md"
        tasks.write_text("> Status: approved 2018-08-08\n\n- [ ] H1. h.\n", encoding="utf-8")
        self.commit_all("seed head")
        tasks.write_text("> Status: implemented 2018-08-08\n\n- [ ] H1. h.\n", encoding="utf-8")
        self.commit_all("alias head")
        env_extra = {"SDD_RETROFIT_TEST_HEAD_MOVE": "1"}
        prev = {key: os.environ.get(key) for key in env_extra}
        os.environ.update(env_extra)
        try:
            proc = run_cli(["--apply-safe", "--batch", "approved-aliases"], self.repo)
        finally:
            for key, value in prev.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value
            rf.clear_journal("approved-aliases")
        self.assertEqual(proc.returncode, 2, proc.stdout)
        self.assertIn("MIGRATION_HEAD_CHANGED", proc.stdout)


if __name__ == "__main__":
    unittest.main()
