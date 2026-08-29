#!/usr/bin/env python3
"""Task 6 fixtures — scripts/spec_retrofit.py planning + guarded writer."""
import contextlib
import errno
import io
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))

import spec_contract as sc
import importlib.util

_rf_spec = importlib.util.spec_from_file_location(
    "spec_retrofit", SCRIPTS / "spec-retrofit.py")
rf = importlib.util.module_from_spec(_rf_spec)
sys.modules["spec_retrofit"] = rf
_rf_spec.loader.exec_module(rf)


def _fixture_historical_features(repo: Path) -> tuple[str, ...]:
    root = repo / ".ai" / "specs"
    if not root.is_dir():
        return ()
    return tuple(sorted(
        path.name
        for path in root.iterdir()
        if path.is_dir() and not path.is_symlink()
        and path.name not in {rf.CURRENT_FEATURE, rf.ARCHIVE_CONTAINER}
    ))


def run_cli(argv: list[str], repo: Path):
    """เรียก public parser ใน process ทดสอบพร้อม historical tuple ของ fixture ที่ระบุชัด."""
    stdout = io.StringIO()
    stderr = io.StringIO()
    env = dict(os.environ, SDD_RETROFIT_REPO=str(repo), PYTHONDONTWRITEBYTECODE="1")
    with patch.dict(os.environ, env, clear=True), \
            patch.object(rf, "HISTORICAL_FEATURES", _fixture_historical_features(repo)), \
            contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
        rf._LEDGER_CACHE.clear()
        try:
            try:
                rc = rf.main(argv)
            except SystemExit as exit_signal:
                rc = int(exit_signal.code)
        finally:
            rf._LEDGER_CACHE.clear()
    return subprocess.CompletedProcess(argv, rc, stdout.getvalue(), stderr.getvalue())


def run_public_cli(argv: list[str], repo: Path):
    """เรียก script จริงโดยไม่มี test patch เพื่อพิสูจน์ public policy."""
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
        # header-less legacy tasks never get floating field rewrites; the
        # observations move ships, fields wait for a header-bearing pass or
        # surface as decided blockers.
        self.assertIn("evidence.observations", fields)
        self.assertNotIn("evidence.viewports", fields)
        blockers_by_field = {blocker["targetField"] for blocker in payload["blockers"]}
        self.assertIn("evidence.viewports", blockers_by_field)
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
        # line-rewrite form: afterBytes keeps the full row with REQ- prefix
        self.assertTrue(any(after == b"| REQ-7.1 | Build |\n" for _t, after in refs))
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

    @staticmethod
    def _tree_bytes(root: Path) -> dict[str, bytes]:
        return {
            path.relative_to(root).as_posix(): path.read_bytes()
            for path in sorted(root.rglob("*"))
            if path.is_file()
        }

    def _populate_public_alias(self, specs: Path, *, artifact_symlink: Path | None = None) -> Path:
        for feature in rf.CANONICAL_HISTORICAL_FEATURES:
            (specs / feature).mkdir(parents=True, exist_ok=True)
        target = specs / rf.CANONICAL_HISTORICAL_FEATURES[0] / "tasks.md"
        if artifact_symlink is None:
            target.write_text(
                "> Status: implemented 2099-01-01\n\n- [ ] A1. work.\n",
                encoding="utf-8",
            )
        else:
            artifact_symlink.write_text(
                "> Status: implemented 2099-01-01\n\n- [ ] A1. work.\n",
                encoding="utf-8",
            )
            target.symlink_to(artifact_symlink)
        owner = specs / rf.CURRENT_FEATURE
        owner.mkdir(parents=True, exist_ok=True)
        ledger = owner / "migration-resolutions.json"
        ledger.write_text(json.dumps({
            "decisions": [{
                "path": f".ai/specs/{rf.CANONICAL_HISTORICAL_FEATURES[0]}/tasks.md",
                "field": "status.line",
                "taskId": "",
                "disposition": "status-approved",
                "date": "2099-01-02",
            }]
        }), encoding="utf-8")
        return target

    def _assert_public_engine_failure(self, argv: list[str]) -> subprocess.CompletedProcess:
        proc = run_public_cli(argv, self.repo)
        self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
        payload = json.loads(proc.stdout)
        self.assertEqual("engine-fail", payload["verdict"])
        self.assertNotIn("Traceback", proc.stdout + proc.stderr)
        return proc

    def test_canonical_path_symlink_mutation_is_killed(self):
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        before = (
            "        if stat.S_ISLNK(current_stat.st_mode):\n"
            "            raise _unsafe_path(current, \"path component เป็น symlink\")\n"
        )
        self.assertEqual(1, source.count(before))
        mutated = source.replace(
            before,
            "        if False:\n"
            "            raise _unsafe_path(current, \"path component เป็น symlink\")\n",
        ).replace(
            'leaf_kind == "file" and stat.S_ISREG(current_stat.st_mode)',
            'leaf_kind == "file" and (stat.S_ISREG(current_stat.st_mode) or stat.S_ISLNK(current_stat.st_mode))',
            1,
        )
        module_path = self.repo / "path-guard-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_path_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(module_name, module_path)
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)
        canonical_repo = rf.repo_root()
        real = canonical_repo / "real.txt"
        real.write_text("inside\n", encoding="utf-8")
        linked = canonical_repo / "linked.txt"
        linked.symlink_to(real)

        with self.assertRaises(rf.EngineFailure):
            rf._guard_repo_file(linked)
        self.assertTrue(
            module._guard_repo_file(linked).is_symlink(),
            "sanity: symlink mutation ต้องเปิด canonical path guard",
        )

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

    def test_second_journal_owner_cannot_reset_first_owner_state(self):
        target_rel = ".ai/specs/apply-demo/tasks.md"
        target = self.feature("apply-demo") / "tasks.md"
        before = b"BEFORE\n"
        planned = b"PLANNED\n"
        target.write_bytes(before)
        winner = rf.Journal(
            batch_id="restory",
            captured_head="winner",
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(planned),
            )],
        )
        winner_claim = rf._claim_new_journal("restory")
        rf.write_journal(
            "restory", winner, {target_rel: before}, claim=winner_claim
        )
        loaded = rf.load_journal("restory")
        loaded.targets[0].pending = True
        rf._write_journal_manifest("restory", loaded, claim=winner_claim)
        target.write_bytes(planned)
        root = rf.journal_root("restory")
        winner_bytes = self._tree_bytes(root)
        loser = rf.Journal(
            batch_id="restory",
            captured_head="loser",
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(b"LOSER\n"),
            )],
        )
        try:
            with self.assertRaises(rf.MigrationRecoveryRequired) as failure:
                rf.write_journal("restory", loser, {target_rel: before})
            self.assertEqual("MIGRATION_RECOVERY_REQUIRED", str(failure.exception))
            self.assertEqual(planned, target.read_bytes())
            self.assertEqual(winner_bytes, self._tree_bytes(root))
            self.assertTrue(rf.load_journal("restory").targets[0].pending)
        finally:
            winner_claim.close()
            rf.clear_journal("restory")

    def test_exclusive_journal_claim_mutation_is_killed(self):
        duplicate_a = self._seed_generation(
            ".journal-" + "e" * 32, batch_id="duplicate"
        )
        duplicate_b = self._seed_generation(
            ".journal-" + "f" * 32, batch_id="duplicate"
        )
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        guard = (
            "    if duplicates:\n"
            "        raise MigrationRecoveryFailure(\n"
        )
        self.assertEqual(1, source.count(guard))
        mutated = source.replace(
            "    if malformed or active or duplicates or not claim_stale and unretired:\n",
            "    if malformed or active or not claim_stale and unretired:\n",
            1,
        ).replace(
            guard,
            "    if False:  # mutation: generation uniqueness ถูกตัด\n"
            "        raise MigrationRecoveryFailure(\n",
        )
        module_path = self.repo / "journal-owner-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_owner_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(module_name, module_path)
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)

        claim = module._claim_new_journal("target-batch")
        try:
            self.assertTrue((duplicate_a / module._RETIRED_MARKER).is_file())
            self.assertTrue((duplicate_b / module._RETIRED_MARKER).is_file())
        finally:
            module._retire_claimed_recovery_root(
                claim.root_fd, claim.lock_fd, "test-cleanup"
            )
            claim.close()

    def test_public_apply_rejects_active_same_batch_owner_at_start(self):
        directory = self.prepare_alias_with_history_local()
        with patch.object(rf, "HISTORICAL_FEATURES", _fixture_historical_features(self.repo)):
            plans, _actions, blockers = rf.build_apply_plan("approved-aliases")
        self.assertFalse(blockers)
        path_str, before, planned = plans[0]
        target = directory / "tasks.md"
        winner = rf.Journal(
            batch_id="approved-aliases",
            captured_head="winner",
            targets=[rf.JournalTarget(
                path=path_str,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(planned),
            )],
        )
        with rf._claim_new_journal("approved-aliases") as winner_claim:
            rf.write_journal(
                "approved-aliases", winner, {path_str: before}, claim=winner_claim
            )
            loaded = rf.load_journal("approved-aliases")
            loaded.targets[0].pending = True
            rf._write_journal_manifest("approved-aliases", loaded)
            target.write_bytes(planned)
            winner_bytes = self._tree_bytes(rf.journal_root("approved-aliases"))

            proc = run_cli(["--apply-safe", "--batch", "approved-aliases"], self.repo)

            self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
            self.assertEqual({
                "diagnostics": [{"code": "MIGRATION_RECOVERY_REQUIRED"}],
                "schemaVersion": 1,
                "verdict": "engine-fail",
            }, json.loads(proc.stdout))
            self.assertEqual(planned, target.read_bytes())
            self.assertEqual(
                winner_bytes,
                self._tree_bytes(rf.journal_root("approved-aliases")),
            )
        rf.clear_journal("approved-aliases")

    def test_public_apply_loser_preserves_winner_target_and_journal(self):
        directory = self.prepare_alias_with_history_local()
        target = directory / "tasks.md"
        target_rel = target.relative_to(self.repo).as_posix()
        real_build = rf.build_apply_plan
        winner_state: dict[str, object] = {}

        def interleave(batch_id: str):
            plans, actions, blockers = real_build(batch_id)
            path_str, before, planned = plans[0]
            winner = rf.Journal(
                batch_id=batch_id,
                captured_head="winner",
                targets=[rf.JournalTarget(
                    path=path_str,
                    before_sha256=rf.sha256(before),
                    planned_sha256=rf.sha256(planned),
                )],
            )
            winner_claim = rf._claim_new_journal(batch_id)
            winner_state["claim"] = winner_claim
            rf.write_journal(
                batch_id, winner, {path_str: before}, claim=winner_claim
            )
            loaded = rf.load_journal(batch_id)
            loaded.targets[0].pending = True
            rf._write_journal_manifest(
                batch_id, loaded, claim=winner_claim
            )
            target.write_bytes(planned)
            winner_state["planned"] = planned
            winner_state["journal"] = self._tree_bytes(rf.journal_root(batch_id))
            return plans, actions, blockers

        try:
            with patch.object(rf, "build_apply_plan", side_effect=interleave):
                proc = run_cli(["--apply-safe", "--batch", "approved-aliases"], self.repo)
            self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
            self.assertEqual({
                "diagnostics": [{"code": "MIGRATION_RECOVERY_REQUIRED"}],
                "schemaVersion": 1,
                "verdict": "engine-fail",
            }, json.loads(proc.stdout))
            self.assertEqual(winner_state["planned"], target.read_bytes())
            self.assertEqual(
                winner_state["journal"],
                self._tree_bytes(rf.journal_root("approved-aliases")),
            )
            self.assertEqual(target_rel, rf.load_journal("approved-aliases").targets[0].path)
            self.assertTrue(rf.load_journal("approved-aliases").targets[0].pending)
        finally:
            if "claim" in winner_state:
                winner_state["claim"].close()
            rf.clear_journal("approved-aliases")

    def test_public_apply_safe_preserves_future_feature_outside_canonical_membership(self):
        for feature in rf.CANONICAL_HISTORICAL_FEATURES:
            self.feature(feature)
        directory = self.feature("future-feature")
        tasks = directory / "tasks.md"
        tasks.write_text(
            "> Status: approved 2019-09-09\n\n- [ ] A1. work.\n", encoding="utf-8"
        )
        self.commit_all("seed future proof")
        tasks.write_text(
            "> Status: implemented 2025-05-05\n\n- [ ] A1. work.\n", encoding="utf-8"
        )
        self.commit_all("future alias")
        before = tasks.read_bytes()

        proc = run_public_cli(["--apply-safe", "--batch", "approved-aliases"], self.repo)
        payload = json.loads(proc.stdout)

        self.assertEqual(0, proc.returncode, proc.stdout + proc.stderr)
        self.assertEqual("allow", payload["verdict"])
        self.assertNotIn(".ai/specs/future-feature/tasks.md", payload.get("applied", []))
        self.assertEqual(before, tasks.read_bytes())

    def test_public_override_does_not_skip_scope_check_in_any_mode(self):
        for feature in rf.CANONICAL_HISTORICAL_FEATURES[:-1]:
            self.feature(feature)
        directory = self.feature("future-feature")
        tasks = directory / "tasks.md"
        tasks.write_text(
            "> Status: approved 2019-09-09\n\n- [ ] A1. work.\n", encoding="utf-8"
        )
        self.commit_all("seed future scope proof")
        tasks.write_text(
            "> Status: implemented 2025-05-05\n\n- [ ] A1. work.\n", encoding="utf-8"
        )
        self.commit_all("future scope alias")
        before = tasks.read_bytes()
        template = self.repo / "resolution-template.json"

        invocations = (
            ["--dry-run", "--batch", "approved-aliases"],
            ["--check", "--batch", "approved-aliases"],
            ["--apply-safe", "--batch", "approved-aliases"],
            ["--check", "--batch", "approved-aliases",
             "--emit-resolution-template", template.name],
        )
        for argv in invocations:
            with self.subTest(argv=argv):
                proc = run_public_cli(argv, self.repo)
                payload = json.loads(proc.stdout)
                self.assertEqual(1, proc.returncode, proc.stdout + proc.stderr)
                self.assertEqual("policy-fail", payload["verdict"])
                self.assertIn("MIGRATION_SCOPE_MISMATCH", {
                    blocker["code"] for blocker in payload["blockers"]
                })
                self.assertEqual(before, tasks.read_bytes())
        self.assertFalse(template.exists())

    def test_public_dry_run_rejects_non_directory_ai_parent(self):
        (self.repo / ".ai").write_text("not a directory\n", encoding="utf-8")
        self.commit_all("track non-directory ai parent")

        self._assert_public_engine_failure(
            ["--dry-run", "--batch", "approved-aliases"]
        )

    def test_public_dry_run_rejects_ai_parent_symlink(self):
        external = Path(tempfile.mkdtemp(prefix="retrofit-external-ai-"))
        self.addCleanup(shutil.rmtree, external, True)
        target = self._populate_public_alias(external / "specs")
        before = target.read_bytes()
        (self.repo / ".ai").symlink_to(external, target_is_directory=True)
        self.commit_all("track external ai link")

        self._assert_public_engine_failure(
            ["--dry-run", "--batch", "approved-aliases"]
        )

        self.assertEqual(before, target.read_bytes())

    def test_public_apply_safe_rejects_specs_root_symlink(self):
        external = Path(tempfile.mkdtemp(prefix="retrofit-external-specs-"))
        self.addCleanup(shutil.rmtree, external, True)
        target = self._populate_public_alias(external)
        before = target.read_bytes()
        ai_root = self.repo / ".ai"
        ai_root.mkdir()
        (ai_root / "specs").symlink_to(external, target_is_directory=True)
        self.commit_all("track external specs link")

        self._assert_public_engine_failure(
            ["--apply-safe", "--batch", "approved-aliases"]
        )

        self.assertEqual(before, target.read_bytes())

    def test_public_check_rejects_current_owner_symlink(self):
        target = self._populate_public_alias(self.features)
        before = target.read_bytes()
        owner = self.features / rf.CURRENT_FEATURE
        external = Path(tempfile.mkdtemp(prefix="retrofit-external-owner-"))
        self.addCleanup(shutil.rmtree, external, True)
        shutil.move(str(owner), str(external / "owner"))
        owner.symlink_to(external / "owner", target_is_directory=True)
        self.commit_all("track external owner link")

        self._assert_public_engine_failure(
            ["--check", "--batch", "approved-aliases"]
        )

        self.assertEqual(before, target.read_bytes())

    def test_public_emit_template_rejects_external_ledger_symlink(self):
        target = self._populate_public_alias(self.features)
        before = target.read_bytes()
        ledger = self.features / rf.CURRENT_FEATURE / "migration-resolutions.json"
        external = Path(tempfile.mkdtemp(prefix="retrofit-external-ledger-"))
        self.addCleanup(shutil.rmtree, external, True)
        external_ledger = external / "migration-resolutions.json"
        shutil.move(str(ledger), str(external_ledger))
        ledger.symlink_to(external_ledger)
        template = self.repo / ".pipeline" / "template.json"
        self.commit_all("track external ledger link")

        self._assert_public_engine_failure([
            "--check", "--batch", "approved-aliases",
            "--emit-resolution-template", ".pipeline/template.json",
        ])

        self.assertEqual(before, target.read_bytes())
        self.assertFalse(template.exists())

    def test_public_check_rejects_non_regular_ledger_leaf(self):
        for feature in rf.CANONICAL_HISTORICAL_FEATURES:
            self.feature(feature)
        owner = self.feature(rf.CURRENT_FEATURE)
        (owner / "migration-resolutions.json").mkdir()

        self._assert_public_engine_failure(
            ["--check", "--batch", "approved-aliases"]
        )

    def test_public_emit_template_rejects_repo_escape(self):
        for feature in rf.CANONICAL_HISTORICAL_FEATURES:
            self.feature(feature)
        outside = self.repo.parent / f"{self.repo.name}-escaped-template.json"
        self.addCleanup(outside.unlink, missing_ok=True)

        self._assert_public_engine_failure([
            "--check", "--batch", "approved-aliases",
            "--emit-resolution-template", f"../{outside.name}",
        ])

        self.assertFalse(outside.exists())

    def test_public_emit_template_rejects_precreated_target_symlink(self):
        for feature in rf.CANONICAL_HISTORICAL_FEATURES:
            self.feature(feature)
        external = Path(tempfile.mkdtemp(prefix="retrofit-external-emit-"))
        self.addCleanup(shutil.rmtree, external, True)
        external_target = external / "template.json"
        external_target.write_bytes(b"EXTERNAL\n")
        target = self.repo / ".pipeline" / "template.json"
        target.parent.mkdir()
        target.symlink_to(external_target)

        self._assert_public_engine_failure([
            "--check", "--batch", "approved-aliases",
            "--emit-resolution-template", ".pipeline/template.json",
        ])

        self.assertTrue(target.is_symlink())
        self.assertEqual(b"EXTERNAL\n", external_target.read_bytes())

    def test_public_emit_target_swap_cannot_write_external_file(self):
        for feature in rf.CANONICAL_HISTORICAL_FEATURES:
            self.feature(feature)
        external = Path(tempfile.mkdtemp(prefix="retrofit-external-emit-swap-"))
        self.addCleanup(shutil.rmtree, external, True)
        external_target = external / "template.json"
        external_target.write_bytes(b"EXTERNAL\n")
        target = rf.repo_root() / ".pipeline" / "template.json"
        target.parent.mkdir()
        real_replace = os.replace

        def swap_then_replace(source, destination, **kwargs):
            target.symlink_to(external_target)
            return real_replace(source, destination, **kwargs)

        with patch.object(rf.os, "replace", side_effect=swap_then_replace):
            proc = run_cli([
                "--check", "--batch", "approved-aliases",
                "--emit-resolution-template", ".pipeline/template.json",
            ], self.repo)

        self.assertEqual(0, proc.returncode, proc.stdout + proc.stderr)
        self.assertTrue(target.is_file())
        self.assertFalse(target.is_symlink())
        self.assertEqual(b"EXTERNAL\n", external_target.read_bytes())

    def test_public_modes_map_malformed_ledger_to_engine_envelope(self):
        for feature in rf.CANONICAL_HISTORICAL_FEATURES:
            self.feature(feature)
        owner = self.feature(rf.CURRENT_FEATURE)
        (owner / "migration-resolutions.json").write_text("{ malformed", encoding="utf-8")
        self.commit_all("seed malformed ledger")
        template = self.repo / ".pipeline" / "template.json"
        invocations = (
            ["--dry-run", "--batch", "approved-aliases"],
            ["--check", "--batch", "approved-aliases"],
            ["--apply-safe", "--batch", "approved-aliases"],
            [
                "--check", "--batch", "approved-aliases",
                "--emit-resolution-template", ".pipeline/template.json",
            ],
        )

        for argv in invocations:
            with self.subTest(argv=argv):
                self._assert_public_engine_failure(argv)

        self.assertFalse(template.exists())

    def test_public_apply_safe_rejects_external_historical_artifact_bytes(self):
        external = Path(tempfile.mkdtemp(prefix="retrofit-external-artifact-"))
        self.addCleanup(shutil.rmtree, external, True)
        external_artifact = external / "tasks.md"
        target = self._populate_public_alias(
            self.features, artifact_symlink=external_artifact
        )
        before = external_artifact.read_bytes()
        self.commit_all("track external artifact link")

        self._assert_public_engine_failure(
            ["--apply-safe", "--batch", "approved-aliases"]
        )

        self.assertTrue(target.is_symlink())
        self.assertEqual(before, external_artifact.read_bytes())

    def test_atomic_repo_write_rejects_symlink_target(self):
        canonical_repo = rf.repo_root()
        external = Path(tempfile.mkdtemp(prefix="retrofit-external-replace-"))
        self.addCleanup(shutil.rmtree, external, True)
        external_target = external / "tasks.md"
        external_target.write_bytes(b"external\n")
        target = canonical_repo / "target.md"
        target.symlink_to(external_target)

        with self.assertRaises(rf.EngineFailure):
            rf._atomic_write_repo_file(target, b"replacement\n")

        self.assertTrue(target.is_symlink())
        self.assertEqual(b"external\n", external_target.read_bytes())

    def test_atomic_repo_write_preserves_regular_edit_at_install_point(self):
        target = rf.repo_root() / "target.md"
        target.write_bytes(b"BEFORE\n")
        foreign = b"FOREIGN EDIT\n"

        with patch.object(
            rf,
            "_atomic_exchange",
            side_effect=self._exchange_after_foreign_replace(rf, target.name, foreign),
        ), self.assertRaisesRegex(rf.MigrationFileChanged, "MIGRATION_FILE_CHANGED"):
            rf._atomic_write_repo_file(target, b"PLANNED\n")

        self.assertEqual(foreign, target.read_bytes())

    def test_public_apply_preserves_regular_edit_at_install_point_and_keeps_journal(self):
        target = self._populate_public_alias(self.features)
        self.commit_all("seed apply CAS")
        foreign = b"FOREIGN APPLY EDIT\n"

        with patch.object(
            rf,
            "_atomic_exchange",
            side_effect=self._exchange_after_foreign_replace(rf, target.name, foreign),
        ):
            proc = run_cli(["--apply-safe", "--batch", "approved-aliases"], self.repo)

        self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
        self.assertIn("MIGRATION_FILE_CHANGED", proc.stdout)
        self.assertEqual(foreign, target.read_bytes())
        self.assertTrue(rf.journal_exists("approved-aliases"))
        rf.clear_journal("approved-aliases")

    def test_recovery_preserves_regular_edit_at_install_point_and_keeps_journal(self):
        target_rel = ".ai/specs/apply-demo/tasks.md"
        target = self.feature("apply-demo") / "tasks.md"
        before = b"BEFORE CAPTURED\n"
        planned = b"PLANNED\n"
        foreign = b"FOREIGN RECOVERY EDIT\n"
        target.write_bytes(planned)
        journal = rf.Journal(
            batch_id="restory",
            captured_head="cafe",
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(planned),
                applied=True,
            )],
        )
        rf.write_journal("restory", journal, {target_rel: before})

        with patch.object(
            rf,
            "_atomic_exchange",
            side_effect=self._exchange_after_foreign_replace(rf, target.name, foreign),
        ), self.assertRaisesRegex(rf.MigrationFileChanged, "MIGRATION_FILE_CHANGED"):
            rf.restore_from_journal("restory")

        self.assertEqual(foreign, target.read_bytes())
        self.assertTrue(rf.journal_exists("restory"))
        rf.clear_journal("restory")

    def test_manifest_write_preserves_regular_edit_at_install_point(self):
        target_rel = ".ai/specs/apply-demo/tasks.md"
        target = self.feature("apply-demo") / "tasks.md"
        before = b"BEFORE\n"
        target.write_bytes(before)
        journal = rf.Journal(
            batch_id="restory",
            captured_head="cafe",
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(b"PLANNED\n"),
            )],
        )
        rf.write_journal("restory", journal, {target_rel: before})
        loaded = rf.load_journal("restory")
        loaded.targets[0].pending = True
        manifest_path = rf.journal_root("restory") / "manifest.json"
        foreign = b'{"foreign": true}\n'

        with patch.object(
            rf,
            "_atomic_exchange",
            side_effect=self._exchange_after_foreign_replace(
                rf, "manifest.json", foreign
            ),
        ), self.assertRaisesRegex(rf.MigrationFileChanged, "MIGRATION_FILE_CHANGED"):
            rf._write_journal_manifest("restory", loaded)

        self.assertEqual(foreign, manifest_path.read_bytes())
        shutil.rmtree(rf._journal_base())

    def test_initial_journal_write_rejects_preexisting_root_without_touching_bytes(self):
        target_rel = ".ai/specs/apply-demo/tasks.md"
        before = b"BEFORE\n"
        target = self.feature("apply-demo") / "tasks.md"
        target.write_bytes(before)
        journal = rf.Journal(
            batch_id="restory",
            captured_head="cafe",
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(b"PLANNED\n"),
            )],
        )
        root = rf.journal_root("restory")
        originals_dir = root / "originals"
        originals_dir.mkdir(parents=True)
        original = originals_dir / f"{rf._stable_index(target_rel)}.bin"
        original.write_bytes(b"FOREIGN ORIGINAL\n")
        before_tree = self._tree_bytes(root)

        with self.assertRaises(rf.MigrationRecoveryFailure):
            rf.write_journal("restory", journal, {target_rel: before})

        self.assertEqual(before_tree, self._tree_bytes(root))
        self.assertEqual(before, target.read_bytes())
        shutil.rmtree(root)

    def test_public_apply_rejects_precreated_journal_original_symlink(self):
        target = self._populate_public_alias(self.features)
        self.commit_all("seed public journal original guard")
        target_rel = target.relative_to(self.repo).as_posix()
        external = Path(tempfile.mkdtemp(prefix="retrofit-external-journal-original-"))
        self.addCleanup(shutil.rmtree, external, True)
        external_target = external / "original.bin"
        external_target.write_bytes(b"EXTERNAL\n")
        root = rf.journal_root("approved-aliases")
        originals = root / "originals"
        originals.mkdir(parents=True)
        original_name = f"{rf._stable_index(target_rel)}.bin"
        (originals / original_name).symlink_to(external_target)
        self.addCleanup(rf.clear_journal, "approved-aliases")

        self._assert_public_engine_failure(
            ["--apply-safe", "--batch", "approved-aliases"]
        )

        self.assertEqual(b"EXTERNAL\n", external_target.read_bytes())
        self.assertTrue((originals / original_name).is_symlink())

    def test_public_apply_rejects_precreated_journal_manifest_symlink(self):
        self._populate_public_alias(self.features)
        self.commit_all("seed public journal manifest guard")
        external = Path(tempfile.mkdtemp(prefix="retrofit-external-journal-manifest-"))
        self.addCleanup(shutil.rmtree, external, True)
        external_target = external / "manifest.json"
        external_target.write_bytes(b"EXTERNAL\n")
        root = rf.journal_root("approved-aliases")
        root.mkdir(parents=True)
        (root / "manifest.json").symlink_to(external_target)
        self.addCleanup(rf.clear_journal, "approved-aliases")

        self._assert_public_engine_failure(
            ["--apply-safe", "--batch", "approved-aliases"]
        )

        self.assertEqual(b"EXTERNAL\n", external_target.read_bytes())
        self.assertTrue((root / "manifest.json").is_symlink())

    def test_public_recovery_rejects_symlinked_journal_target(self):
        for feature in rf.CANONICAL_HISTORICAL_FEATURES:
            self.feature(feature)
        target_rel = (
            f".ai/specs/{rf.CANONICAL_HISTORICAL_FEATURES[0]}/tasks.md"
        )
        target = self.repo / target_rel
        before = b"BEFORE\n"
        planned = b"PLANNED\n"
        target.write_bytes(before)
        self.commit_all("seed recovery target")
        journal = rf.Journal(
            batch_id="approved-aliases",
            captured_head=subprocess.run(
                ["git", "-C", str(self.repo), "rev-parse", "HEAD"],
                capture_output=True,
                text=True,
                check=True,
            ).stdout.strip(),
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(planned),
                applied=True,
            )],
        )
        rf.write_journal(
            "approved-aliases", journal,
            {target_rel: before},
        )
        external = Path(tempfile.mkdtemp(prefix="retrofit-external-recovery-"))
        self.addCleanup(shutil.rmtree, external, True)
        external_target = external / "tasks.md"
        external_target.write_bytes(planned)
        target.unlink()
        target.symlink_to(external_target)
        self.addCleanup(rf.clear_journal, "approved-aliases")

        self._assert_public_engine_failure(
            ["--apply-safe", "--batch", "approved-aliases"]
        )

        self.assertTrue(target.is_symlink())
        self.assertEqual(planned, external_target.read_bytes())

    def test_public_recovery_ignores_precreated_restoring_temp_symlink(self):
        for feature in rf.CANONICAL_HISTORICAL_FEATURES:
            self.feature(feature)
        target_rel = f".ai/specs/{rf.CANONICAL_HISTORICAL_FEATURES[0]}/tasks.md"
        target = self.repo / target_rel
        before = b"> Status: approved 2026-08-28\n"
        planned = b"> Status: draft\n"
        target.write_bytes(before)
        external = Path(tempfile.mkdtemp(prefix="retrofit-external-restoring-temp-"))
        self.addCleanup(shutil.rmtree, external, True)
        external_target = external / "restoring.tmp"
        external_target.write_bytes(b"EXTERNAL\n")
        deterministic_temp = target.with_suffix(".retrofit-restoring")
        deterministic_temp.symlink_to(external_target)
        self.commit_all("seed recovery temp guard")
        journal = rf.Journal(
            batch_id="approved-aliases",
            captured_head=subprocess.run(
                ["git", "-C", str(self.repo), "rev-parse", "HEAD"],
                capture_output=True,
                text=True,
                check=True,
            ).stdout.strip(),
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(planned),
                applied=True,
            )],
        )
        rf.write_journal("approved-aliases", journal, {target_rel: before})
        target.write_bytes(planned)
        self.addCleanup(rf.clear_journal, "approved-aliases")

        proc = run_public_cli(
            ["--apply-safe", "--batch", "approved-aliases"], self.repo
        )

        self.assertNotEqual(2, proc.returncode, proc.stdout + proc.stderr)
        self.assertEqual(before, target.read_bytes())
        self.assertTrue(deterministic_temp.is_symlink())
        self.assertEqual(b"EXTERNAL\n", external_target.read_bytes())

    def test_public_apply_ignores_precreated_applying_temp_symlink(self):
        target = self._populate_public_alias(self.features)
        external = Path(tempfile.mkdtemp(prefix="retrofit-external-applying-temp-"))
        self.addCleanup(shutil.rmtree, external, True)
        external_target = external / "applying.tmp"
        external_target.write_bytes(b"EXTERNAL\n")
        deterministic_temp = target.with_suffix(".retrofit-applying")
        deterministic_temp.symlink_to(external_target)
        self.commit_all("seed apply temp guard")

        proc = run_public_cli(
            ["--apply-safe", "--batch", "approved-aliases"], self.repo
        )

        self.assertEqual(0, proc.returncode, proc.stdout + proc.stderr)
        self.assertIn(b"> Status: approved 2099-01-02", target.read_bytes())
        self.assertTrue(deterministic_temp.is_symlink())
        self.assertEqual(b"EXTERNAL\n", external_target.read_bytes())

    def test_atomic_entry_guard_mutation_is_killed(self):
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        before = "    if stat.S_ISLNK(entry_stat.st_mode) or not stat.S_ISREG(entry_stat.st_mode):\n"
        self.assertEqual(1, source.count(before))
        mutated = source.replace(before, "    if False:\n").replace(
            "    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=directory_fd)\n",
            "    fd = os.open(name, os.O_RDONLY, dir_fd=directory_fd)\n",
            1,
        ).replace(
            "        if not stat.S_ISREG(opened_stat.st_mode) or not _same_inode(entry_stat, opened_stat):\n",
            "        if not stat.S_ISREG(opened_stat.st_mode):\n",
            1,
        ).replace(
            "        if not _same_inode(existing, opened):\n",
            "        if False:\n",
            1,
        ).replace(
            "        if not _same_inode(current_stat, existing) or sha256(current_payload) != expected_sha256:\n",
            "        if False:\n",
            1,
        ).replace(
            "        expected_stat=existing,\n",
            "        expected_stat=opened,\n",
            1,
        ).replace(
            "    if current.st_nlink != 1 or not _same_inode(current, expected):\n",
            "    if False:\n",
            1,
        ).replace(
            "        matched = retained.st_nlink == 1 and _same_inode(retained, expected)\n",
            "        matched = True\n",
            1,
        ).replace(
            "    if (\n"
            "        stat.S_ISLNK(current.st_mode)\n"
            "        or not stat.S_ISREG(current.st_mode)\n"
            "        or current.st_nlink != 1\n"
            "        or not _same_inode(current, expected)\n"
            "    ):\n",
            "    if False:\n",
            1,
        )
        module_path = self.repo / "atomic-entry-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_atomic_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(module_name, module_path)
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)
        external = Path(tempfile.mkdtemp(prefix="retrofit-external-atomic-mutant-"))
        self.addCleanup(shutil.rmtree, external, True)
        external_target = external / "target.json"
        external_target.write_bytes(b"EXTERNAL\n")
        target = module.repo_root() / "target.json"
        target.symlink_to(external_target)

        with self.assertRaises(rf.EngineFailure):
            rf._atomic_write_repo_file(target, b"replacement\n")
        module._atomic_write_repo_file(target, b"replacement\n")

        self.assertFalse(target.is_symlink(), "sanity: mutation ต้องยอม replace symlink entry")
        self.assertEqual(b"EXTERNAL\n", external_target.read_bytes())

    def test_initial_journal_missing_entry_mutation_is_killed(self):
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        before_guard = (
            "    if existing is not None:\n"
            "        if expected_missing:\n"
            "            raise MigrationRecoveryRequired(\"MIGRATION_RECOVERY_REQUIRED\")\n"
        )
        self.assertEqual(1, source.count(before_guard))
        mutated = source.replace(
            before_guard,
            "    if existing is not None:\n"
            "        if False:  # mutation: initial entry เขียนทับได้\n"
            "            raise MigrationRecoveryRequired(\"MIGRATION_RECOVERY_REQUIRED\")\n",
        )
        module_path = self.repo / "journal-missing-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_missing_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(module_name, module_path)
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)
        directory = module.repo_root() / "journal-entry-mutant"
        directory.mkdir()
        target = directory / "manifest.json"
        target.write_bytes(b"WINNER\n")
        directory_fd = os.open(
            directory,
            os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
        )
        try:
            module._atomic_write_at(
                directory_fd,
                target.name,
                b"LOSER\n",
                expected_missing=True,
                intent_anchor="repo",
                intent_path=target.relative_to(module.repo_root()).as_posix(),
            )
        finally:
            os.close(directory_fd)

        self.assertEqual(b"LOSER\n", target.read_bytes(), "sanity: mutation ต้องทับ winner")

    def test_atomic_swap_back_mutation_is_killed(self):
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        before = (
            "        if displaced[0] == \"foreign\":\n"
            "            _atomic_exchange(directory_fd, name, swap_name)\n"
        )
        self.assertEqual(1, source.count(before))
        mutated = source.replace(
            before,
            "        if displaced[0] == \"foreign\":\n"
            "            pass  # mutation: ไม่ swap foreign กลับ\n",
        )
        module_path = self.repo / "swap-back-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_swap_back_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(module_name, module_path)
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)
        target = module.repo_root() / "target.md"
        target.write_bytes(b"BEFORE\n")
        foreign = b"FOREIGN EDIT\n"

        with patch.object(
            module,
            "_atomic_exchange",
            side_effect=self._exchange_after_foreign_replace(
                module, target.name, foreign
            ),
        ), self.assertRaises(module.MigrationRecoveryFailure):
            module._atomic_write_repo_file(target, b"PLANNED\n")

        self.assertEqual(
            b"PLANNED\n", target.read_bytes(),
            "sanity: ตัด swap-back แล้ว foreign หลุดจาก canonical",
        )
        with self.assertRaises(module.MigrationFileChanged):
            module._reconcile_write_intents()
        self.assertEqual(foreign, target.read_bytes())

    def test_durable_intent_mutation_is_killed(self):
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        before = (
            "        intent = _load_write_intent(intent_claim.root_fd)\n"
            "        if existing is None:\n"
        )
        self.assertEqual(1, source.count(before))
        mutated = source.replace(
            before,
            "        intent = _load_write_intent(intent_claim.root_fd)\n"
            "        _delete_write_intent(intent_claim, 'mutation')  # mutation: intent ไม่ durable\n"
            "        if existing is None:\n",
        )
        module_path = self.repo / "durable-intent-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        target = self.repo / "durable-intent-target.md"
        target.write_bytes(b"BEFORE\n")
        target = target.resolve()

        self._kill_at_phase(
            "exchange",
            f"m._atomic_write_repo_file(pathlib.Path({str(target)!r}), b'PLANNED\\n')",
            module_path=module_path,
        )

        self.assertFalse(
            rf.write_intents_pending(),
            "sanity: ตัด durable intent แล้ว restart ไม่มี state ให้ reconcile",
        )
        self.assertEqual(b"PLANNED\n", target.read_bytes())

    def test_write_intent_owner_acquisition_mutation_is_killed(self):
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        before = (
            "        fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)\n"
            "        os.fchmod(lock_fd, 0o600)\n"
            "        os.fsync(lock_fd)\n"
            "        os.fsync(root_fd)\n\n"
            "        def publish_intent(opened: os.stat_result) -> None:\n"
        )
        self.assertEqual(1, source.count(before))
        mutated = source.replace(
            before,
            "        pass  # mutation: per-write owner ไม่ acquire lock\n"
            "        os.fchmod(lock_fd, 0o600)\n"
            "        os.fsync(lock_fd)\n"
            "        os.fsync(root_fd)\n\n"
            "        def publish_intent(opened: os.stat_result) -> None:\n",
        )
        module_path = self.repo / "write-owner-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        target = self.repo / "write-owner-target.md"
        target.write_bytes(b"BEFORE\n")
        target = target.resolve()
        process, ready_read, gate_write = self._start_phase_process(
            "intent-fsync",
            f"m._atomic_write_repo_file(pathlib.Path({str(target)!r}), b'PLANNED\\n')",
            module_path=module_path,
        )
        try:
            self.assertEqual(b"R", os.read(ready_read, 1))

            rf._reconcile_write_intents()

            self.assertFalse(
                rf.write_intents_pending(),
                "sanity: ไม่มี owner lock ทำให้ startup ลบ active intent",
            )
        finally:
            os.kill(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
            os.close(ready_read)
            os.close(gate_write)
            if process.stdout is not None:
                process.stdout.close()
            if process.stderr is not None:
                process.stderr.close()

    def test_cleanup_owner_acquisition_mutation_is_killed(self):
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        before = (
            "            if claim_stale:\n"
            "                try:\n"
            "                    fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)\n"
            "                except BlockingIOError:\n"
            "                    active = True\n"
            "                    continue\n"
            "            structural.append((name, legacy_batch, cleanup, root_fd, lock_fd))\n"
        )
        self.assertEqual(1, source.count(before))
        owner_guard = (
            "    try:\n"
            "        fcntl.flock(owner_lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)\n"
            "    except BlockingIOError as error:\n"
            "        raise MigrationRecoveryRequired(\"MIGRATION_RECOVERY_REQUIRED\") from error\n"
        )
        self.assertEqual(1, source.count(owner_guard))
        mutated = source.replace(
            before,
            "            if claim_stale:\n"
            "                try:\n"
            "                    pass  # mutation: cleanup ไม่ acquire owner lock\n"
            "                except BlockingIOError:\n"
            "                    active = True\n"
            "                    continue\n"
            "            structural.append((name, legacy_batch, cleanup, root_fd, lock_fd))\n",
        ).replace(
            owner_guard,
            "    pass  # mutation: retirement ไม่ยืนยัน owner lock\n",
        )
        module_path = self.repo / "cleanup-owner-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_cleanup_owner_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(module_name, module_path)
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)
        tombstone = rf._journal_base() / ".clearing-restory"
        tombstone.mkdir(parents=True)
        owner = tombstone / rf._OWNER_LOCK
        owner.write_bytes(b"OWNER\n")
        ready_read, ready_write = os.pipe()
        gate_read, gate_write = os.pipe()
        code = (
            "import fcntl, os\n"
            f"fd = os.open({str(owner)!r}, os.O_RDWR | os.O_NOFOLLOW)\n"
            "fcntl.flock(fd, fcntl.LOCK_EX)\n"
            f"os.write({ready_write}, b'R')\n"
            f"os.read({gate_read}, 1)\n"
        )
        process = subprocess.Popen(
            [sys.executable, "-c", code], pass_fds=(ready_write, gate_read)
        )
        os.close(ready_write)
        os.close(gate_read)
        try:
            self.assertEqual(b"R", os.read(ready_read, 1))

            module._resume_pending_cleanup()

            self.assertTrue(
                (tombstone / module._RETIRED_MARKER).is_file(),
                "sanity: ตัด cleanup lock แล้ว active tombstone ถูก retire",
            )
        finally:
            os.kill(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
            os.close(ready_read)
            os.close(gate_write)

    def test_exchange_binding_uses_named_platform_symbols_without_syscall_numbers(self):
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        exchange_source = source[
            source.index("def _atomic_exchange("):
            source.index("\ndef _probe_atomic_exchange(")
        ]
        self.assertIn('symbol_name = "renameatx_np"', exchange_source)
        self.assertIn('symbol_name = "renameat2"', exchange_source)
        self.assertIn("ctypes.CDLL(None, use_errno=True)", exchange_source)
        self.assertNotIn("syscall(", exchange_source)

    def test_original_hash_mutation_is_killed(self):
        target_rel = ".ai/specs/apply-demo/tasks.md"
        target = self.feature("apply-demo") / "tasks.md"
        before = b"BEFORE CAPTURED\n"
        planned = b"PLANNED\n"
        corrupted = b"CORRUPTED SNAPSHOT\n"
        target.write_bytes(planned)
        journal = rf.Journal(
            batch_id="restory",
            captured_head="cafe",
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(planned),
                applied=True,
            )],
        )
        rf.write_journal("restory", journal, {target_rel: before})
        original = rf.journal_root("restory") / "originals" / journal.targets[0].original_file
        original.write_bytes(corrupted)
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        before_guard = "            if sha256(original) != target.before_sha256:\n"
        self.assertEqual(1, source.count(before_guard))
        mutated = source.replace(before_guard, "            if False:\n")
        module_path = self.repo / "original-hash-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_hash_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(module_name, module_path)
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)

        ok, failures = module.restore_from_journal("restory")

        self.assertTrue(ok, failures)
        self.assertEqual(corrupted, target.read_bytes(), "sanity: hash mutation ต้อง restore bytes เสีย")
        self.assertFalse(module.journal_exists("restory"))

    def test_mutating_cleanup_resume_mutation_is_killed(self):
        self.prepare_alias_with_history_local()
        tombstone = rf._journal_base() / ".clearing-restory"
        tombstone.mkdir(parents=True)
        (tombstone / rf._OWNER_LOCK).write_bytes(b"STALE OWNER\n")
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        cleanup_call = "        _process_claimed_recovery_roots(recovery)\n"
        self.assertEqual(4, source.count(cleanup_call))
        mutated = source.replace(
            cleanup_call,
            "        pass  # mutation: recovery roots ไม่ถูก process\n",
        )
        module_path = self.repo / "cleanup-resume-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_cleanup_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(module_name, module_path)
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)
        module.HISTORICAL_FEATURES = _fixture_historical_features(self.repo)

        with contextlib.redirect_stdout(io.StringIO()), \
                self.assertRaises(module.MigrationRecoveryRequired):
            module.run_apply_safe("approved-aliases")

        self.assertFalse(
            (tombstone / module._RETIRED_MARKER).exists(),
            "sanity: mutation ต้องทิ้ง cleanup pending แบบ unretired",
        )
        shutil.rmtree(tombstone)

    def test_read_only_cleanup_mutation_is_killed(self):
        tombstone = rf._journal_base() / ".clearing-restory"
        tombstone.mkdir(parents=True)
        (tombstone / rf._OWNER_LOCK).write_bytes(b"STALE OWNER\n")
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        before_probe = (
            "def enforce_journal_clear(mode: str) -> int | None:\n"
            "    if write_intents_pending() or journal_exists():\n"
        )
        self.assertEqual(1, source.count(before_probe))
        mutated = source.replace(
            before_probe,
            "def enforce_journal_clear(mode: str) -> int | None:\n"
            "    _resume_pending_cleanup()  # mutation: read-only retire state\n"
            "    if write_intents_pending() or journal_exists():\n",
        )
        module_path = self.repo / "cleanup-read-only-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_cleanup_read_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(module_name, module_path)
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)

        with contextlib.redirect_stdout(io.StringIO()):
            module.run_check("approved-aliases")

        self.assertTrue(
            (tombstone / module._RETIRED_MARKER).is_file(),
            "sanity: mutation ต้องทำให้ read-only tree เปลี่ยน",
        )

    def test_journal_presence_requires_recovery_before_modes(self):
        directory = self.prepare_alias_with_history_local()
        journal = rf.Journal(batch_id="approved-aliases", captured_head="deadbeef",
                             targets=[rf.JournalTarget(
                                 path=".ai/specs/apply-demo/tasks.md",
                                 before_sha256=rf.sha256(b"x"), planned_sha256=rf.sha256(b"y"),
                                 original_file="missing.bin",
                             )])
        rf.write_journal(
            "approved-aliases",
            journal,
            {".ai/specs/apply-demo/tasks.md": b"x"},
        )
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

    def test_recovery_rejects_corrupted_original_before_any_mutation(self):
        target_rel = ".ai/specs/apply-demo/tasks.md"
        target = self.feature("apply-demo") / "tasks.md"
        before = b"BEFORE CAPTURED\n"
        planned = b"PLANNED\n"
        target.write_bytes(planned)
        journal = rf.Journal(
            batch_id="approved-aliases",
            captured_head="cafe",
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(planned),
                applied=True,
            )],
        )
        rf.write_journal("approved-aliases", journal, {target_rel: before})
        original = rf.journal_root("approved-aliases") / "originals" / journal.targets[0].original_file
        original.write_bytes(b"CORRUPTED SNAPSHOT\n")

        with self.assertRaisesRegex(rf.MigrationRecoveryFailure, "beforeSha256"):
            rf.restore_from_journal("approved-aliases")

        self.assertEqual(planned, target.read_bytes())
        with self.assertRaises(rf.MigrationRecoveryFailure):
            rf.journal_exists("approved-aliases")
        proc = run_cli(["--apply-safe", "--batch", "approved-aliases"], self.repo)
        self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
        self.assertIn("MIGRATION_RECOVERY_FAILED", proc.stdout)
        self.assertEqual(planned, target.read_bytes())
        with self.assertRaises(rf.MigrationRecoveryFailure):
            rf.journal_exists("approved-aliases")
        shutil.rmtree(rf._journal_base())

    def test_recovery_rejects_regular_original_swap_during_open(self):
        target_rel = ".ai/specs/apply-demo/tasks.md"
        target = self.feature("apply-demo") / "tasks.md"
        before = b"BEFORE CAPTURED\n"
        planned = b"PLANNED\n"
        target.write_bytes(planned)
        journal = rf.Journal(
            batch_id="restory",
            captured_head="cafe",
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(planned),
                applied=True,
            )],
        )
        rf.write_journal("restory", journal, {target_rel: before})
        original_name = journal.targets[0].original_file
        original = rf.journal_root("restory") / "originals" / original_name
        replacement = self.repo / "replacement-original.bin"
        replacement.write_bytes(before)
        real_open = rf.os.open
        swapped = False

        def swap_then_open(path, flags, *args, **kwargs):
            nonlocal swapped
            if path == original_name and not swapped:
                swapped = True
                replacement.replace(original)
            return real_open(path, flags, *args, **kwargs)

        with patch.object(rf.os, "open", side_effect=swap_then_open), \
                self.assertRaisesRegex(rf.MigrationRecoveryFailure, "ถูกสลับ"):
            rf.restore_from_journal("restory")

        self.assertEqual(planned, target.read_bytes())
        self.assertTrue(rf.journal_exists("restory"))
        rf.clear_journal("restory")

    def test_recovery_rejects_hard_linked_original_before_any_mutation(self):
        target_rel = ".ai/specs/apply-demo/tasks.md"
        target = self.feature("apply-demo") / "tasks.md"
        before = b"BEFORE CAPTURED\n"
        planned = b"PLANNED\n"
        target.write_bytes(planned)
        journal = rf.Journal(
            batch_id="restory",
            captured_head="cafe",
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(planned),
                applied=True,
            )],
        )
        rf.write_journal("restory", journal, {target_rel: before})
        original = rf.journal_root("restory") / "originals" / journal.targets[0].original_file
        peer = self.repo / "peer-original.bin"
        peer.write_bytes(before)
        original.unlink()
        os.link(peer, original)

        with self.assertRaisesRegex(rf.MigrationRecoveryFailure, "link เดียว"):
            rf.restore_from_journal("restory")

        self.assertEqual(planned, target.read_bytes())
        with self.assertRaises(rf.MigrationRecoveryFailure):
            rf.journal_exists("restory")
        shutil.rmtree(rf._journal_base())

    def test_recovery_rejects_duplicate_manifest_mapping_before_any_mutation(self):
        target_rel = ".ai/specs/apply-demo/tasks.md"
        target = self.feature("apply-demo") / "tasks.md"
        before = b"BEFORE CAPTURED\n"
        planned = b"PLANNED\n"
        target.write_bytes(planned)
        journal = rf.Journal(
            batch_id="restory",
            captured_head="cafe",
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(planned),
                applied=True,
            )],
        )
        rf.write_journal("restory", journal, {target_rel: before})
        manifest_path = rf.journal_root("restory") / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["targets"].append(dict(manifest["targets"][0]))
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")

        with self.assertRaisesRegex(rf.MigrationRecoveryFailure, "unique"):
            rf.restore_from_journal("restory")

        self.assertEqual(planned, target.read_bytes())
        with self.assertRaises(rf.MigrationRecoveryFailure):
            rf.journal_exists("restory")
        shutil.rmtree(rf._journal_base())

    def test_read_only_modes_report_cleanup_pending_without_mutation(self):
        tombstone = rf._journal_base() / ".clearing-restory"
        tombstone.mkdir(parents=True)
        owner = tombstone / rf._OWNER_LOCK
        owner.write_bytes(b"STALE OWNER\n")
        owner.chmod(0o600)
        before = self._tree_bytes(tombstone)
        template = self.repo / ".pipeline" / "template.json"
        invocations = (
            ["--dry-run", "--batch", "approved-aliases", "--format", "json"],
            ["--dry-run", "--batch", "approved-aliases", "--format", "text"],
            ["--check", "--batch", "approved-aliases"],
            ["--check", "--batch", "final-all-spec"],
            ["--batch", "approved-aliases",
             "--emit-resolution-template", ".pipeline/template.json"],
        )
        expected = {
            "diagnostics": [{"code": "MIGRATION_RECOVERY_REQUIRED"}],
            "schemaVersion": 1,
            "verdict": "engine-fail",
        }

        for argv in invocations:
            with self.subTest(argv=argv):
                proc = run_cli(argv, self.repo)
                self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
                self.assertEqual(expected, json.loads(proc.stdout))
                self.assertEqual(before, self._tree_bytes(tombstone))
                self.assertFalse(template.exists())

    def test_mutating_apply_resumes_pending_cleanup_before_work(self):
        directory = self.prepare_alias_with_history_local()
        tombstone = rf._journal_base() / ".clearing-restory"
        tombstone.mkdir(parents=True)
        (tombstone / rf._OWNER_LOCK).write_bytes(b"STALE OWNER\n")

        proc = run_cli(["--apply-safe", "--batch", "approved-aliases"], self.repo)

        self.assertEqual(0, proc.returncode, proc.stdout + proc.stderr)
        self.assertTrue((tombstone / rf._RETIRED_MARKER).is_file())
        self.assertEqual(b"STALE OWNER\n", (tombstone / rf._OWNER_LOCK).read_bytes())
        self.assertIn(
            "> Status: approved 2019-09-09",
            (directory / "tasks.md").read_text(encoding="utf-8"),
        )

    @staticmethod
    def _exchange_after_foreign_replace(module, target_name: str, foreign: bytes):
        real_exchange = module._atomic_exchange
        raced = False

        def replace_then_exchange(directory_fd, left, right):
            nonlocal raced
            if left == target_name and not raced:
                raced = True
                foreign_name = f".foreign-{os.getpid()}-{target_name}"
                foreign_fd = os.open(
                    foreign_name,
                    os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                    0o600,
                    dir_fd=directory_fd,
                )
                try:
                    os.write(foreign_fd, foreign)
                    os.fsync(foreign_fd)
                finally:
                    os.close(foreign_fd)
                os.rename(
                    foreign_name,
                    target_name,
                    src_dir_fd=directory_fd,
                    dst_dir_fd=directory_fd,
                )
            return real_exchange(directory_fd, left, right)

        return replace_then_exchange

    @staticmethod
    def _tree_snapshot(root: Path) -> dict[str, tuple[int, bytes | str]]:
        if not root.exists():
            return {}
        snapshot: dict[str, tuple[int, bytes | str]] = {}
        for path in sorted(root.rglob("*")):
            relative = path.relative_to(root).as_posix()
            mode = os.lstat(path).st_mode
            if path.is_symlink():
                value: bytes | str = os.readlink(path)
            elif path.is_file():
                value = path.read_bytes()
            else:
                value = "directory"
            snapshot[relative] = (mode, value)
        return snapshot

    def _seed_generation(
        self,
        name: str,
        *,
        batch_id: str | None,
        marker: bytes | None = None,
    ) -> Path:
        root = rf._journal_base() / name
        root.mkdir(parents=True, mode=0o700)
        root.chmod(0o700)
        owner = root / rf._OWNER_LOCK
        owner.write_bytes(b"OWNER\n")
        owner.chmod(0o600)
        if batch_id is not None:
            (root / "manifest.json").write_text(json.dumps({
                "batchId": batch_id,
                "capturedHead": "cafe",
                "schemaVersion": 1,
                "state": "preparing",
                "targets": [],
            }), encoding="utf-8")
        if marker is not None:
            retired = root / rf._RETIRED_MARKER
            retired.write_bytes(marker)
            retired.chmod(0o600)
        return root

    def _start_phase_process(
        self,
        phase: str,
        statement: str,
        *,
        module_path: Path | None = None,
    ) -> tuple[subprocess.Popen, int, int]:
        ready_read, ready_write = os.pipe()
        gate_read, gate_write = os.pipe()
        env = dict(
            os.environ,
            SDD_RETROFIT_REPO=str(self.repo),
            PYTHONDONTWRITEBYTECODE="1",
            SDD_RETROFIT_TEST_STOP_PHASE=phase,
            SDD_RETROFIT_TEST_READY_FD=str(ready_write),
            SDD_RETROFIT_TEST_GATE_FD=str(gate_read),
        )
        module_path = module_path or SCRIPTS / "spec-retrofit.py"
        code = (
            "import importlib.util, os, pathlib, sys\n"
            f"sys.path.insert(0, {str(SCRIPTS)!r})\n"
            f"p = pathlib.Path({str(module_path)!r})\n"
            "s = importlib.util.spec_from_file_location('phase_child', p)\n"
            "m = importlib.util.module_from_spec(s)\n"
            "sys.modules['phase_child'] = m\n"
            "s.loader.exec_module(m)\n"
            f"{statement}\n"
        )
        process = subprocess.Popen(
            [sys.executable, "-c", code],
            env=env,
            pass_fds=(ready_write, gate_read),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        os.close(ready_write)
        os.close(gate_read)
        return process, ready_read, gate_write

    def _kill_at_phase(
        self,
        phase: str,
        statement: str,
        *,
        module_path: Path | None = None,
    ) -> subprocess.Popen:
        process, ready_read, gate_write = self._start_phase_process(
            phase, statement, module_path=module_path
        )
        try:
            ready = os.read(ready_read, 1)
            if ready != b"R":
                stdout, stderr = process.communicate(timeout=5)
                self.fail(
                    f"child ไม่ถึง phase {phase}: rc={process.returncode}; "
                    f"stdout={stdout!r}; stderr={stderr!r}"
                )
            os.kill(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
            self.assertEqual(-signal.SIGKILL, process.returncode)
            return process
        finally:
            os.close(ready_read)
            os.close(gate_write)
            if process.poll() is None:
                process.kill()
                process.wait(timeout=5)
            if process.stdout is not None:
                process.stdout.close()
            if process.stderr is not None:
                process.stderr.close()

    def test_cross_kind_preflight_blocks_before_any_recovery_mutation(self):
        def hold_lock(path: Path) -> tuple[subprocess.Popen, int, int]:
            ready_read, ready_write = os.pipe()
            gate_read, gate_write = os.pipe()
            code = (
                "import fcntl, os\n"
                f"fd = os.open({str(path)!r}, os.O_RDWR | os.O_NOFOLLOW)\n"
                "fcntl.flock(fd, fcntl.LOCK_EX)\n"
                f"os.write({ready_write}, b'R')\n"
                f"os.read({gate_read}, 1)\n"
            )
            process = subprocess.Popen(
                [sys.executable, "-c", code], pass_fds=(ready_write, gate_read)
            )
            os.close(ready_write)
            os.close(gate_read)
            self.assertEqual(b"R", os.read(ready_read, 1))
            return process, ready_read, gate_write

        stale_target = rf.repo_root() / "cross-kind-stale-intent.md"
        stale_target.write_bytes(b"BEFORE\n")
        self._kill_at_phase(
            "intent-fsync",
            f"m._atomic_write_repo_file(pathlib.Path({str(stale_target)!r}), b'PLANNED\\n')",
        )
        tombstone = rf._journal_base() / ".clearing-active"
        tombstone.mkdir(parents=True)
        tombstone_owner = tombstone / rf._OWNER_LOCK
        tombstone_owner.write_bytes(b"ACTIVE\n")
        process, ready_read, gate_write = hold_lock(tombstone_owner)
        try:
            intent_before = self._tree_snapshot(rf._write_intent_root())
            mutation_lock = rf._journal_base() / rf._RECOVERY_MUTATION_LOCK
            mutation_lock.write_bytes(b"")
            mutation_lock.chmod(0o600)
            journal_before = self._tree_snapshot(rf._journal_base())

            proc = run_cli(["--apply-safe", "--batch", "approved-aliases"], self.repo)

            self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
            self.assertIn("MIGRATION_RECOVERY_REQUIRED", proc.stdout)
            self.assertEqual(intent_before, self._tree_snapshot(rf._write_intent_root()))
            self.assertEqual(journal_before, self._tree_snapshot(rf._journal_base()))
            self.assertEqual(b"BEFORE\n", stale_target.read_bytes())
        finally:
            os.kill(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
            os.close(ready_read)
            os.close(gate_write)
        rf._resume_pending_cleanup()
        rf._reconcile_write_intents()

        stale_tombstone = rf._journal_base() / ".clearing-stale"
        stale_tombstone.mkdir(parents=True)
        (stale_tombstone / rf._OWNER_LOCK).write_bytes(b"STALE\n")
        target_rel = ".ai/specs/apply-demo/tasks.md"
        target = self.feature("apply-demo") / "tasks.md"
        before = b"BEFORE\n"
        target.write_bytes(before)
        journal = rf.Journal(
            batch_id="approved-aliases",
            captured_head="active",
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(b"PLANNED\n"),
            )],
        )
        rf.write_journal("approved-aliases", journal, {target_rel: before})
        journal_owner = rf.journal_root("approved-aliases") / rf._OWNER_LOCK
        process, ready_read, gate_write = hold_lock(journal_owner)
        try:
            state_before = self._tree_snapshot(rf._journal_base())

            proc = run_cli(["--apply-safe", "--batch", "approved-aliases"], self.repo)

            self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
            self.assertIn("MIGRATION_RECOVERY_REQUIRED", proc.stdout)
            self.assertEqual(state_before, self._tree_snapshot(rf._journal_base()))
        finally:
            os.kill(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
            os.close(ready_read)
            os.close(gate_write)
        rf.clear_journal("approved-aliases")
        rf._resume_pending_cleanup()

    def test_fd_bound_retirement_ignores_replaced_parent_basename(self):
        target = rf.repo_root() / "intent-basename-race.md"
        target.write_bytes(b"BEFORE\n")
        self._kill_at_phase(
            "intent-fsync",
            f"m._atomic_write_repo_file(pathlib.Path({str(target)!r}), b'PLANNED\\n')",
        )
        intent_base = rf._write_intent_root()
        token_path = next(path for path in intent_base.iterdir() if path.is_dir())
        parked = intent_base / f"parked-{token_path.name}"
        base_fd = os.open(intent_base, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        root_fd = os.open(
            token_path.name,
            os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
            dir_fd=base_fd,
        )
        lock_fd = rf._open_owner_lock(root_fd)
        claim = rf.WriteIntentClaim(token_path.name, base_fd, root_fd, lock_fd)
        token_path.rename(parked)
        token_path.mkdir()
        (token_path / rf._OWNER_LOCK).write_bytes(b"FOREIGN\n")
        (token_path / "foreign.bin").write_bytes(b"FOREIGN\n")
        try:
            rf._delete_write_intent(claim, "uncommitted")
            self.assertTrue((parked / rf._RETIRED_MARKER).is_file())
            self.assertEqual(b"FOREIGN\n", (token_path / "foreign.bin").read_bytes())
        finally:
            claim.close()
        shutil.rmtree(token_path)
        shutil.rmtree(parked)

        cleanup = rf._journal_base() / ".clearing-race"
        cleanup.mkdir(parents=True, mode=0o700)
        owner = cleanup / rf._OWNER_LOCK
        owner.write_bytes(b"OWNER\n")
        owner.chmod(0o600)
        root_fd = os.open(cleanup, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        lock_fd = rf._open_owner_lock(root_fd)
        cleanup_claim = rf.CleanupClaim(cleanup.name, root_fd, lock_fd)
        parked_cleanup = cleanup.with_name(".parked-clearing-race")
        cleanup.rename(parked_cleanup)
        cleanup.mkdir()
        (cleanup / "foreign.bin").write_bytes(b"FOREIGN\n")
        try:
            rf._remove_cleanup_tombstone(cleanup_claim)
            self.assertTrue((parked_cleanup / rf._RETIRED_MARKER).is_file())
            self.assertEqual(b"FOREIGN\n", (cleanup / "foreign.bin").read_bytes())
        finally:
            cleanup_claim.close()
        shutil.rmtree(cleanup)
        shutil.rmtree(parked_cleanup)

        target_rel = ".ai/specs/apply-demo/tasks.md"
        journal_target = self.feature("apply-demo") / "tasks.md"
        before = b"BEFORE\n"
        journal_target.write_bytes(before)
        journal = rf.Journal(
            batch_id="clear-race",
            captured_head="cafe",
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(b"PLANNED\n"),
            )],
        )
        rf.write_journal("clear-race", journal, {target_rel: before})
        journal_claim = rf._claim_existing_journal("clear-race")
        journal_root = rf.journal_root("clear-race")
        parked_journal = journal_root.with_name("parked-clear-race")
        journal_root.rename(parked_journal)
        journal_root.mkdir()
        (journal_root / "foreign.bin").write_bytes(b"FOREIGN\n")
        try:
            rf.clear_journal(
                "clear-race", claim=journal_claim, operation="verified"
            )
            self.assertTrue((parked_journal / rf._RETIRED_MARKER).is_file())
            self.assertEqual(b"FOREIGN\n", (journal_root / "foreign.bin").read_bytes())
        finally:
            journal_claim.close()

    def test_retirement_never_opens_or_mutates_recovery_children(self):
        intent_base = rf._write_intent_root()
        intent_base.mkdir(parents=True, exist_ok=True, mode=0o700)
        root = intent_base / ("a" * 32)
        root.mkdir(mode=0o700)
        owner = root / rf._OWNER_LOCK
        owner.write_bytes(b"OWNER\n")
        owner.chmod(0o600)
        regular = root / "payload.bin"
        regular.write_bytes(b"ORIGINAL\n")
        directory = root / "nested"
        directory.mkdir()
        (directory / "nested.bin").write_bytes(b"NESTED\n")
        external = self.repo / "retirement-external.bin"
        external.write_bytes(b"EXTERNAL\n")
        (root / "link").symlink_to(external)
        before = self._tree_snapshot(root)
        root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        lock_fd = rf._open_owner_lock(root_fd)
        real_open = os.open
        opened_names: list[str] = []

        def record_open(path, flags, mode=0o777, *, dir_fd=None):
            if isinstance(path, str):
                opened_names.append(path)
            return real_open(path, flags, mode, dir_fd=dir_fd)

        try:
            with patch.object(os, "open", side_effect=record_open), \
                    patch.object(os, "unlink", side_effect=AssertionError("unlink forbidden")), \
                    patch.object(os, "rmdir", side_effect=AssertionError("rmdir forbidden")), \
                    patch.object(os, "rename", side_effect=AssertionError("rename forbidden")):
                rf._retire_claimed_recovery_root(root_fd, lock_fd, "uncommitted")
        finally:
            os.close(lock_fd)
            os.close(root_fd)

        self.assertNotIn("payload.bin", opened_names)
        self.assertNotIn("nested", opened_names)
        self.assertNotIn("link", opened_names)
        current = self._tree_snapshot(root)
        for name, value in before.items():
            self.assertEqual(value, current[name])
        self.assertEqual(b"EXTERNAL\n", external.read_bytes())

    def test_parent_mutation_lock_blocks_recovery_base_writers(self):
        def hold_parent_lock(path: Path) -> tuple[subprocess.Popen, int, int]:
            path.write_bytes(b"")
            path.chmod(0o600)
            ready_read, ready_write = os.pipe()
            gate_read, gate_write = os.pipe()
            code = (
                "import fcntl, os\n"
                f"fd = os.open({str(path)!r}, os.O_RDWR | os.O_NOFOLLOW)\n"
                "fcntl.flock(fd, fcntl.LOCK_EX)\n"
                f"os.write({ready_write}, b'R')\n"
                f"os.read({gate_read}, 1)\n"
            )
            process = subprocess.Popen(
                [sys.executable, "-c", code], pass_fds=(ready_write, gate_read)
            )
            os.close(ready_write)
            os.close(gate_read)
            self.assertEqual(b"R", os.read(ready_read, 1))
            return process, ready_read, gate_write

        intent_base = rf._write_intent_root()
        intent_base.mkdir(parents=True, mode=0o700)
        process, ready_read, gate_write = hold_parent_lock(
            intent_base / rf._RECOVERY_MUTATION_LOCK
        )
        target = rf.repo_root() / "parent-lock-create.md"
        target.write_bytes(b"BEFORE\n")
        try:
            with self.assertRaisesRegex(
                rf.MigrationRecoveryRequired, "MIGRATION_RECOVERY_REQUIRED"
            ):
                rf._atomic_write_repo_file(target, b"PLANNED\n")
            self.assertEqual(b"BEFORE\n", target.read_bytes())

            root = intent_base / ("c" * 32)
            root.mkdir(mode=0o700)
            owner = root / rf._OWNER_LOCK
            owner.write_bytes(b"OWNER\n")
            owner.chmod(0o600)
            (root / "preserve.bin").write_bytes(b"PRESERVE\n")
            root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            lock_fd = rf._open_owner_lock(root_fd)
            try:
                rf._retire_claimed_recovery_root(
                    root_fd, lock_fd, "uncommitted"
                )
            finally:
                os.close(lock_fd)
                os.close(root_fd)
            self.assertEqual(b"PRESERVE\n", (root / "preserve.bin").read_bytes())
            self.assertTrue((root / rf._RETIRED_MARKER).is_file())
        finally:
            os.kill(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
            os.close(ready_read)
            os.close(gate_write)

        journal_base = rf._journal_base()
        journal_base.mkdir(parents=True, exist_ok=True, mode=0o700)
        process, ready_read, gate_write = hold_parent_lock(
            journal_base / rf._RECOVERY_MUTATION_LOCK
        )
        try:
            with self.assertRaisesRegex(
                rf.MigrationRecoveryRequired, "MIGRATION_RECOVERY_REQUIRED"
            ):
                rf.write_journal(
                    "parent-locked-journal",
                    rf.Journal(batch_id="parent-locked-journal", captured_head="cafe"),
                    {},
                )
            self.assertFalse(rf.journal_root("parent-locked-journal").exists())
        finally:
            os.kill(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
            os.close(ready_read)
            os.close(gate_write)

    def test_recovery_roots_and_mutation_locks_use_private_modes(self):
        intent_base = rf._write_intent_root()
        intent_base.mkdir(parents=True, mode=0o755)
        intent_base.chmod(0o755)
        target = rf.repo_root() / "private-mode-intent.md"
        target.write_bytes(b"BEFORE\n")

        self._kill_at_phase(
            "intent-fsync",
            f"m._atomic_write_repo_file(pathlib.Path({str(target)!r}), b'PLANNED\\n')",
        )

        token_root = next(
            path for path in intent_base.iterdir()
            if path.is_dir() and rf._WRITE_INTENT_TOKEN_RE.fullmatch(path.name)
        )
        self.assertEqual(0o700, intent_base.stat().st_mode & 0o777)
        self.assertEqual(0o700, token_root.stat().st_mode & 0o777)
        self.assertEqual(
            0o600, (intent_base / rf._RECOVERY_MUTATION_LOCK).stat().st_mode & 0o777
        )
        self.assertEqual(0o600, (token_root / rf._OWNER_LOCK).stat().st_mode & 0o777)
        rf._reconcile_write_intents()

        journal_base = rf._journal_base()
        journal_base.mkdir(parents=True, exist_ok=True, mode=0o755)
        journal_base.chmod(0o755)
        journal = rf.Journal(batch_id="private-mode-journal", captured_head="cafe")
        rf.write_journal("private-mode-journal", journal, {})
        journal_root = rf.journal_root("private-mode-journal")
        self.assertEqual(0o700, journal_base.stat().st_mode & 0o777)
        self.assertEqual(0o700, journal_root.stat().st_mode & 0o777)
        self.assertEqual(
            0o600, (journal_base / rf._RECOVERY_MUTATION_LOCK).stat().st_mode & 0o777
        )
        self.assertEqual(0o600, (journal_root / rf._OWNER_LOCK).stat().st_mode & 0o777)
        rf.clear_journal("private-mode-journal")

    def test_recovery_children_are_retained_without_interpretation(self):
        intent_base = rf._write_intent_root()
        intent_base.mkdir(parents=True, mode=0o700)
        for kind in ("symlink", "hardlink"):
            with self.subTest(kind=kind):
                token = ("e" if kind == "symlink" else "f") * 32
                root = intent_base / token
                root.mkdir(mode=0o700)
                owner = root / rf._OWNER_LOCK
                owner.write_bytes(b"OWNER\n")
                owner.chmod(0o600)
                if kind == "symlink":
                    external = rf.repo_root() / "retained-external.bin"
                    external.write_bytes(b"FOREIGN\n")
                    (root / "retained").symlink_to(external)
                else:
                    first = root / "retained"
                    first.write_bytes(b"FOREIGN\n")
                    os.link(first, root / "peer.bin")
                before = self._tree_snapshot(root)
                root_fd = os.open(
                    root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
                )
                lock_fd = rf._open_owner_lock(root_fd)
                try:
                    rf._retire_claimed_recovery_root(
                        root_fd, lock_fd, "uncommitted"
                    )
                finally:
                    os.close(lock_fd)
                    os.close(root_fd)
                current = self._tree_snapshot(root)
                for name, value in before.items():
                    self.assertEqual(value, current[name])
                self.assertTrue((root / rf._RETIRED_MARKER).is_file())
                if kind == "symlink":
                    self.assertEqual(b"FOREIGN\n", external.read_bytes())

    def test_fd_bound_cleanup_mutation_is_killed(self):
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        guard = '        _write_phase_hook("retire-before-marker")\n'
        self.assertEqual(1, source.count(guard))
        mutated = source.replace(
            guard,
            '        os.unlink("owned.bin", dir_fd=claimed_fd)  # mutation: recovery child ถูกลบ\n'
            '        _write_phase_hook("retire-before-marker")\n',
        )
        module_path = self.repo / "fd-cleanup-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_fd_cleanup_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(module_name, module_path)
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)

        root = module._write_intent_root() / ("3" * 32)
        root.mkdir(parents=True, mode=0o700)
        owner = root / module._OWNER_LOCK
        owner.write_bytes(b"OWNER\n")
        owner.chmod(0o600)
        owned = root / "owned.bin"
        owned.write_bytes(b"OWNED\n")
        root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        lock_fd = module._open_owner_lock(root_fd)
        try:
            module._retire_claimed_recovery_root(root_fd, lock_fd, "mutation")
        finally:
            os.close(lock_fd)
            os.close(root_fd)
        self.assertFalse(owned.exists(), "sanity: mutation ต้องลบ recovery child")

    def test_parent_mutation_lock_mutation_is_killed(self):
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        replacements = {
            "mutation_lock = _recovery_mutation_lock(base_fd, \"write intent create\")":
                "mutation_lock = contextlib.nullcontext()",
            (
                "        locks.enter_context(\n"
                "            _recovery_mutation_lock(journal_base_fd, \"journal resolver\")\n"
                "        )\n"
            ): "        pass  # mutation: journal parent lock ถูกตัด\n",
        }
        mutated = source
        for before, after in replacements.items():
            self.assertEqual(1, mutated.count(before))
            mutated = mutated.replace(before, after, 1)
        module_path = self.repo / "parent-lock-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_parent_lock_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(module_name, module_path)
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)

        intent_base = module._write_intent_root()
        intent_base.mkdir(parents=True, mode=0o700)
        intent_base_fd = os.open(
            intent_base, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
        )
        target = module.repo_root() / "parent-lock-mutant.md"
        target.write_bytes(b"BEFORE\n")
        try:
            with rf._recovery_mutation_lock(intent_base_fd, "mutation holder"):
                module._atomic_write_repo_file(target, b"PLANNED\n")
            self.assertEqual(b"PLANNED\n", target.read_bytes())
        finally:
            os.close(intent_base_fd)

        journal_base = module._journal_base()
        journal_base.mkdir(parents=True, exist_ok=True, mode=0o700)
        journal_base_fd = os.open(
            journal_base, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
        )
        try:
            with rf._recovery_mutation_lock(journal_base_fd, "mutation holder"):
                module.write_journal(
                    "parent-lock-mutant",
                    module.Journal(batch_id="parent-lock-mutant", captured_head="cafe"),
                    {},
                )
            self.assertTrue(module.journal_root("parent-lock-mutant").is_dir())
            module.clear_journal("parent-lock-mutant")
        finally:
            os.close(journal_base_fd)

    def test_initial_no_clobber_publication_recovers_after_sigkill(self):
        cases = (
            (
                "original",
                [rf.JournalTarget(
                    path=".ai/specs/apply-demo/tasks.md",
                    before_sha256=rf.sha256(b"BEFORE\n"),
                    planned_sha256=rf.sha256(b"PLANNED\n"),
                )],
                {".ai/specs/apply-demo/tasks.md": b"BEFORE\n"},
            ),
            ("manifest", [], {}),
        )
        for batch_id, targets, originals in cases:
            with self.subTest(batch_id=batch_id):
                if targets:
                    target = self.feature("apply-demo") / "tasks.md"
                    target.write_bytes(b"BEFORE\n")
                statement = (
                    f"j = m.Journal(batch_id={batch_id!r}, captured_head='cafe', "
                    f"targets={[target.to_json() for target in targets]!r}); "
                    "j.targets = [m.JournalTarget("
                    "path=e['path'], before_sha256=e['beforeSha256'], "
                    "planned_sha256=e['plannedSha256']) for e in j.targets]; "
                    f"m.write_journal({batch_id!r}, j, {originals!r})"
                )

                self._kill_at_phase("no-clobber-publish", statement)

                rf._reconcile_write_intents()
                journal = rf.Journal(
                    batch_id=batch_id,
                    captured_head="cafe",
                    targets=targets,
                )
                rf.write_journal(batch_id, journal, originals)
                loaded = rf.load_journal(batch_id)
                self.assertEqual(len(targets), len(loaded.targets))
                root = rf.journal_root(batch_id)
                for path in root.rglob("*"):
                    if path.is_file():
                        self.assertEqual(1, os.stat(path, follow_symlinks=False).st_nlink)
                self.assertFalse(rf.write_intents_pending())
                rf.clear_journal(batch_id)

    def test_atomic_writer_preserves_explicit_mode_under_restrictive_umask(self):
        target = rf.repo_root() / "mode-preservation.md"
        target.write_bytes(b"BEFORE\n")
        target.chmod(0o664)
        previous = os.umask(0o077)
        try:
            rf._atomic_write_repo_file(target, b"PLANNED\n")
        finally:
            os.umask(previous)

        self.assertEqual(0o664, os.stat(target, follow_symlinks=False).st_mode & 0o777)

    def test_existing_writer_never_moves_recovery_children_into_target_directory(self):
        target = rf.repo_root() / "no-recovery-child-move.md"
        target.write_bytes(b"BEFORE\n")

        with patch.object(
            rf.os, "rename", side_effect=AssertionError("recovery child move forbidden")
        ):
            rf._atomic_write_repo_file(target, b"PLANNED\n")

        self.assertEqual(b"PLANNED\n", target.read_bytes())
        intent_roots = [
            path for path in rf._write_intent_root().iterdir() if path.is_dir()
        ]
        self.assertEqual(1, len(intent_roots))
        self.assertTrue((intent_roots[0] / rf._RETIRED_MARKER).is_file())
        self.assertFalse(any(
            name.startswith("planned-")
            for name in os.listdir(intent_roots[0])
        ))

    def test_existing_write_has_durable_intent_before_swap_can_reach_exchange(self):
        target = rf.repo_root() / "preintent-orphan.md"
        target.write_bytes(b"BEFORE\n")

        self._kill_at_phase(
            "planned-fsync",
            f"m._atomic_write_repo_file(pathlib.Path({str(target)!r}), b'PLANNED\\n')",
        )

        self.assertEqual(b"BEFORE\n", target.read_bytes())
        self.assertEqual(1, len(list(target.parent.glob(".sdd-retrofit-swap-*"))))
        self.assertTrue(rf.write_intents_pending())
        rf._reconcile_write_intents()
        self.assertEqual([], list(target.parent.glob(".sdd-retrofit-swap-*")))
        self.assertFalse(rf.write_intents_pending())
        self.assertEqual(b"BEFORE\n", target.read_bytes())

    def test_existing_file_writer_recovers_every_crash_phase(self):
        outcomes = {
            "intent-fsync": b"BEFORE\n",
            "exchange": b"PLANNED\n",
            "directory-fsync": b"PLANNED\n",
            "displaced-unlink": b"PLANNED\n",
        }
        for phase, expected in outcomes.items():
            with self.subTest(phase=phase):
                target = rf.repo_root() / f"phase-{phase}.md"
                target.write_bytes(b"BEFORE\n")
                statement = (
                    f"m._atomic_write_repo_file(pathlib.Path({str(target)!r}), "
                    "b'PLANNED\\n', expected_sha256=m.sha256(b'BEFORE\\n'))"
                )

                self._kill_at_phase(phase, statement)

                self.assertTrue(target.is_file(), "canonical basename ห้ามหาย")
                self.assertTrue(
                    rf.write_intents_pending(),
                    "ทุก crash phase ต้องมี durable intent ก่อน recovery",
                )
                rf._reconcile_write_intents()
                self.assertEqual(expected, target.read_bytes())
                rf._reconcile_write_intents()
                self.assertEqual(expected, target.read_bytes(), "recovery ต้อง idempotent")
                self.assertFalse(rf.write_intents_pending())

    def test_active_write_intent_owner_blocks_mutating_and_read_only_modes(self):
        target = rf.repo_root() / "active-intent.md"
        target.write_bytes(b"BEFORE\n")
        process, ready_read, gate_write = self._start_phase_process(
            "intent-fsync",
            f"m._atomic_write_repo_file(pathlib.Path({str(target)!r}), b'PLANNED\\n')",
        )
        try:
            self.assertEqual(b"R", os.read(ready_read, 1))
            intent_root = rf._write_intent_root()
            before = self._tree_snapshot(intent_root)

            with self.assertRaisesRegex(
                rf.MigrationRecoveryRequired, "MIGRATION_RECOVERY_REQUIRED"
            ):
                rf._reconcile_write_intents()
            template = self.repo / ".pipeline" / "intent-template.json"
            invocations = (
                ["--dry-run", "--batch", "approved-aliases", "--format", "json"],
                ["--dry-run", "--batch", "approved-aliases", "--format", "text"],
                ["--check", "--batch", "approved-aliases"],
                ["--check", "--batch", "final-all-spec"],
                ["--batch", "approved-aliases", "--emit-resolution-template",
                 ".pipeline/intent-template.json"],
            )
            for argv in invocations:
                with self.subTest(argv=argv):
                    proc = run_cli(argv, self.repo)
                    self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
                    self.assertIn("MIGRATION_RECOVERY_REQUIRED", proc.stdout)
                    self.assertEqual(before, self._tree_snapshot(intent_root))
                    self.assertEqual(b"BEFORE\n", target.read_bytes())
                    self.assertFalse(template.exists())
        finally:
            os.kill(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
            os.close(ready_read)
            os.close(gate_write)
            if process.stdout is not None:
                process.stdout.close()
            if process.stderr is not None:
                process.stderr.close()
        rf._reconcile_write_intents()
        self.assertFalse(rf.write_intents_pending())

    def test_active_write_owner_blocks_before_any_stale_intent_mutation(self):
        stale_target = rf.repo_root() / "stale-before-active.md"
        stale_target.write_bytes(b"BEFORE\n")
        self._kill_at_phase(
            "intent-fsync",
            f"m._atomic_write_repo_file(pathlib.Path({str(stale_target)!r}), b'PLANNED\\n')",
        )
        intent_root = rf._write_intent_root()
        stale_token = next(path for path in intent_root.iterdir() if path.is_dir())
        stale_token.rename(intent_root / ("0" * 32))

        active_target = rf.repo_root() / "active-after-stale.md"
        active_target.write_bytes(b"BEFORE\n")
        process, ready_read, gate_write = self._start_phase_process(
            "intent-fsync",
            f"m._atomic_write_repo_file(pathlib.Path({str(active_target)!r}), b'PLANNED\\n')",
        )
        try:
            self.assertEqual(b"R", os.read(ready_read, 1))
            active_token = next(
                path for path in intent_root.iterdir()
                if path.is_dir() and path.name != "0" * 32
            )
            active_token.rename(intent_root / ("f" * 32))
            before = self._tree_snapshot(intent_root)

            with self.assertRaises(rf.MigrationRecoveryRequired):
                rf._reconcile_write_intents()

            self.assertEqual(
                before,
                self._tree_snapshot(intent_root),
                "active owner ต้อง block ก่อน cleanup stale intent ตัวใด ๆ",
            )
        finally:
            os.kill(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
            os.close(ready_read)
            os.close(gate_write)
            if process.stdout is not None:
                process.stdout.close()
            if process.stderr is not None:
                process.stderr.close()
        rf._reconcile_write_intents()
        self.assertFalse(rf.write_intents_pending())

    def test_malformed_write_intent_owner_lock_preserves_state(self):
        variants = ("missing", "symlink", "directory")
        for variant in variants:
            with self.subTest(variant=variant):
                target = rf.repo_root() / f"malformed-intent-{variant}.md"
                target.write_bytes(b"BEFORE\n")
                self._kill_at_phase(
                    "intent-fsync",
                    f"m._atomic_write_repo_file(pathlib.Path({str(target)!r}), b'PLANNED\\n')",
                )
                intent_root = rf._write_intent_root()
                token_dir = next(path for path in intent_root.iterdir() if path.is_dir())
                owner = token_dir / rf._OWNER_LOCK
                owner.unlink()
                if variant == "symlink":
                    external = rf.repo_root() / f"external-owner-{variant}"
                    external.write_bytes(b"EXTERNAL\n")
                    owner.symlink_to(external)
                elif variant == "directory":
                    owner.mkdir()
                before = self._tree_snapshot(intent_root)

                with self.assertRaises(rf.MigrationRecoveryFailure):
                    rf._reconcile_write_intents()

                self.assertEqual(before, self._tree_snapshot(intent_root))
                self.assertEqual(b"BEFORE\n", target.read_bytes())
                shutil.rmtree(intent_root)

    def test_unknown_write_intent_state_is_preserved_and_fails_recovery(self):
        target = rf.repo_root() / "unknown-intent-state.md"
        target.write_bytes(b"BEFORE\n")
        self._kill_at_phase(
            "intent-fsync",
            f"m._atomic_write_repo_file(pathlib.Path({str(target)!r}), b'PLANNED\\n')",
        )
        intent_root = rf._write_intent_root()
        token_dir = next(path for path in intent_root.iterdir() if path.is_dir())
        intent = json.loads((token_dir / rf._WRITE_INTENT_FILE).read_text(encoding="utf-8"))
        swap = target.parent / intent["swapName"]
        swap.write_bytes(b"UNKNOWN\n")
        before_intent = self._tree_snapshot(intent_root)
        before_target = target.read_bytes()
        before_swap = swap.read_bytes()

        with self.assertRaises(rf.MigrationRecoveryFailure):
            rf._reconcile_write_intents()

        self.assertEqual(before_intent, self._tree_snapshot(intent_root))
        self.assertEqual(before_target, target.read_bytes())
        self.assertEqual(before_swap, swap.read_bytes())
        shutil.rmtree(intent_root)
        swap.unlink()

    def test_unsupported_exchange_fails_before_canonical_mutation(self):
        target = rf.repo_root() / "unsupported-exchange.md"
        target.write_bytes(b"BEFORE\n")
        before_stat = os.stat(target, follow_symlinks=False)

        with patch.object(
            rf,
            "_atomic_exchange",
            side_effect=OSError(errno.EOPNOTSUPP, "unsupported"),
        ), self.assertRaises(rf.EngineFailure):
            rf._atomic_write_repo_file(target, b"PLANNED\n")

        after_stat = os.stat(target, follow_symlinks=False)
        self.assertEqual(b"BEFORE\n", target.read_bytes())
        self.assertEqual((before_stat.st_dev, before_stat.st_ino),
                         (after_stat.st_dev, after_stat.st_ino))
        self.assertFalse(rf.write_intents_pending())

    def test_missing_exchange_symbol_fails_before_canonical_mutation(self):
        target = rf.repo_root() / "missing-exchange-symbol.md"
        target.write_bytes(b"BEFORE\n")
        before_stat = os.stat(target, follow_symlinks=False)

        with patch.object(rf, "_mount_identity", return_value=("test", 1)), \
                patch.object(rf.ctypes, "CDLL", return_value=object()), \
                self.assertRaisesRegex(rf.EngineFailure, "ATOMIC_EXCHANGE_UNSUPPORTED"):
            rf._atomic_write_repo_file(target, b"PLANNED\n")

        after_stat = os.stat(target, follow_symlinks=False)
        self.assertEqual(b"BEFORE\n", target.read_bytes())
        self.assertEqual((before_stat.st_dev, before_stat.st_ino),
                         (after_stat.st_dev, after_stat.st_ino))
        self.assertFalse(rf.write_intents_pending())

    def test_foreign_replace_during_exchange_is_swapped_back_and_preserved(self):
        target = rf.repo_root() / "foreign-race.md"
        target.write_bytes(b"BEFORE\n")
        foreign_entry = rf.repo_root() / "foreign-race-entry.md"
        foreign = b"FOREIGN\n"
        foreign_entry.write_bytes(foreign)
        real_exchange = rf._atomic_exchange
        raced = False

        def replace_then_exchange(directory_fd, left, right):
            nonlocal raced
            if left == target.name and not raced:
                raced = True
                os.rename(
                    foreign_entry.name,
                    target.name,
                    src_dir_fd=directory_fd,
                    dst_dir_fd=directory_fd,
                )
            return real_exchange(directory_fd, left, right)

        with patch.object(rf, "_atomic_exchange", side_effect=replace_then_exchange), \
                self.assertRaisesRegex(rf.MigrationFileChanged, "MIGRATION_FILE_CHANGED"):
            rf._atomic_write_repo_file(target, b"PLANNED\n")

        self.assertEqual(foreign, target.read_bytes())
        self.assertFalse(rf.write_intents_pending())

    def test_apply_target_exchange_crash_restarts_without_missing_canonical(self):
        target = self._populate_public_alias(self.features)
        self.commit_all("seed crash apply")
        statement = (
            "raise SystemExit(m.main(['--apply-safe', '--batch', 'approved-aliases']))"
        )

        self._kill_at_phase("exchange", statement)

        self.assertTrue(target.is_file())
        proc = run_public_cli(["--apply-safe", "--batch", "approved-aliases"], self.repo)
        self.assertEqual(0, proc.returncode, proc.stdout + proc.stderr)
        self.assertIn(b"> Status: approved 2099-01-02", target.read_bytes())
        self.assertFalse(rf.write_intents_pending())

    def test_recovery_restore_exchange_crash_is_idempotent(self):
        target_rel = ".ai/specs/apply-demo/tasks.md"
        target = self.feature("apply-demo") / "tasks.md"
        before = b"BEFORE\n"
        planned = b"PLANNED\n"
        target.write_bytes(planned)
        journal = rf.Journal(
            batch_id="restory",
            captured_head="cafe",
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(planned),
                applied=True,
            )],
        )
        rf.write_journal("restory", journal, {target_rel: before})

        self._kill_at_phase("exchange", "m.restore_from_journal('restory')")

        self.assertTrue(target.is_file())
        rf._reconcile_write_intents()
        ok, failures = rf.restore_from_journal("restory")
        self.assertTrue(ok, failures)
        self.assertEqual(before, target.read_bytes())
        self.assertFalse(rf.write_intents_pending())

    def test_manifest_exchange_crash_keeps_parseable_canonical_manifest(self):
        target_rel = ".ai/specs/apply-demo/tasks.md"
        before = b"BEFORE\n"
        target = self.feature("apply-demo") / "tasks.md"
        target.write_bytes(before)
        journal = rf.Journal(
            batch_id="restory",
            captured_head="cafe",
            targets=[rf.JournalTarget(
                path=target_rel,
                before_sha256=rf.sha256(before),
                planned_sha256=rf.sha256(b"PLANNED\n"),
            )],
        )
        rf.write_journal("restory", journal, {target_rel: before})
        statement = (
            "j = m.load_journal('restory'); "
            "j.targets[0].pending = True; m._write_journal_manifest('restory', j)"
        )

        self._kill_at_phase("exchange", statement)

        manifest = rf.journal_root("restory") / "manifest.json"
        json.loads(manifest.read_text(encoding="utf-8"))
        rf._reconcile_write_intents()
        loaded = rf.load_journal("restory")
        self.assertTrue(loaded.targets[0].pending)
        rf.clear_journal("restory")

    def test_resolution_report_exchange_crash_restarts_with_valid_json(self):
        for feature in rf.CANONICAL_HISTORICAL_FEATURES:
            self.feature(feature)
        report = self.repo / ".pipeline" / "template.json"
        report.parent.mkdir()
        report.write_text('{"old": true}\n', encoding="utf-8")
        statement = (
            "m.emit_resolution_template('.pipeline/template.json', ['approved-aliases'])"
        )

        self._kill_at_phase("exchange", statement)

        self.assertTrue(report.is_file())
        json.loads(report.read_text(encoding="utf-8"))
        rf._reconcile_write_intents()
        with contextlib.redirect_stdout(io.StringIO()):
            rf.emit_resolution_template(
                ".pipeline/template.json", ["approved-aliases"]
            )
        json.loads(report.read_text(encoding="utf-8"))
        self.assertFalse(rf.write_intents_pending())

    def test_active_cleanup_owner_is_preserved_and_blocks_mutating_apply(self):
        self.prepare_alias_with_history_local()
        tombstone = rf._journal_base() / ".clearing-restory"
        tombstone.mkdir(parents=True)
        owner = tombstone / rf._OWNER_LOCK
        owner.write_bytes(b"OWNER\n")
        ready_read, ready_write = os.pipe()
        gate_read, gate_write = os.pipe()
        code = (
            "import fcntl, os\n"
            f"fd = os.open({str(owner)!r}, os.O_RDWR | os.O_NOFOLLOW)\n"
            "fcntl.flock(fd, fcntl.LOCK_EX)\n"
            f"os.write({ready_write}, b'R')\n"
            f"os.read({gate_read}, 1)\n"
        )
        process = subprocess.Popen(
            [sys.executable, "-c", code],
            pass_fds=(ready_write, gate_read),
        )
        os.close(ready_write)
        os.close(gate_read)
        try:
            self.assertEqual(b"R", os.read(ready_read, 1))
            before = self._tree_snapshot(tombstone)

            proc = run_cli(["--apply-safe", "--batch", "approved-aliases"], self.repo)

            self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
            self.assertIn("MIGRATION_RECOVERY_REQUIRED", proc.stdout)
            self.assertEqual(before, self._tree_snapshot(tombstone))
        finally:
            os.kill(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
            os.close(ready_read)
            os.close(gate_write)
        rf._resume_pending_cleanup()
        self.assertTrue((tombstone / rf._RETIRED_MARKER).is_file())

    def test_active_cleanup_owner_blocks_before_any_stale_tombstone_mutation(self):
        base = rf._journal_base()
        stale = base / ".clearing-a"
        stale.mkdir(parents=True)
        (stale / rf._OWNER_LOCK).write_bytes(b"STALE\n")
        active = base / ".clearing-z"
        active.mkdir()
        owner = active / rf._OWNER_LOCK
        owner.write_bytes(b"ACTIVE\n")
        ready_read, ready_write = os.pipe()
        gate_read, gate_write = os.pipe()
        code = (
            "import fcntl, os\n"
            f"fd = os.open({str(owner)!r}, os.O_RDWR | os.O_NOFOLLOW)\n"
            "fcntl.flock(fd, fcntl.LOCK_EX)\n"
            f"os.write({ready_write}, b'R')\n"
            f"os.read({gate_read}, 1)\n"
        )
        process = subprocess.Popen(
            [sys.executable, "-c", code], pass_fds=(ready_write, gate_read)
        )
        os.close(ready_write)
        os.close(gate_read)
        try:
            self.assertEqual(b"R", os.read(ready_read, 1))
            mutation_lock = base / rf._RECOVERY_MUTATION_LOCK
            mutation_lock.write_bytes(b"")
            mutation_lock.chmod(0o600)
            before = self._tree_snapshot(base)

            with self.assertRaises(rf.MigrationRecoveryRequired):
                rf._resume_pending_cleanup()

            self.assertEqual(
                before,
                self._tree_snapshot(base),
                "active owner ต้อง block ก่อน cleanup stale tombstone ตัวใด ๆ",
            )
        finally:
            os.kill(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
            os.close(ready_read)
            os.close(gate_write)
        rf._resume_pending_cleanup()
        self.assertTrue((stale / rf._RETIRED_MARKER).is_file())
        self.assertTrue((active / rf._RETIRED_MARKER).is_file())

    def test_cleanup_owner_lock_malformed_entries_fail_closed(self):
        variants = ("missing", "symlink", "directory")
        for variant in variants:
            with self.subTest(variant=variant):
                tombstone = rf._journal_base() / f".clearing-{variant}"
                tombstone.mkdir(parents=True)
                owner = tombstone / rf._OWNER_LOCK
                if variant == "symlink":
                    external = rf.repo_root() / f"cleanup-external-{variant}"
                    external.write_bytes(b"EXTERNAL\n")
                    owner.symlink_to(external)
                elif variant == "directory":
                    owner.mkdir()
                before = self._tree_snapshot(tombstone)

                with self.assertRaises(rf.MigrationRecoveryFailure):
                    rf._resume_pending_cleanup()

                self.assertEqual(before, self._tree_snapshot(tombstone))
                shutil.rmtree(tombstone)

    def test_retirement_marker_is_fd_bound_append_only_and_idempotent(self):
        root = self._seed_generation(".journal-" + "1" * 32, batch_id="retire")
        evidence = root / "evidence.bin"
        evidence.write_bytes(b"PRESERVE\n")
        root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        lock_fd = rf._open_owner_lock(root_fd)
        try:
            rf._retire_claimed_recovery_root(root_fd, lock_fd, "verified")
            rf._retire_claimed_recovery_root(root_fd, lock_fd, "verified")
        finally:
            os.close(lock_fd)
            os.close(root_fd)

        marker = root / rf._RETIRED_MARKER
        marker_stat = os.stat(marker, follow_symlinks=False)
        self.assertTrue(root.is_dir())
        self.assertEqual(b"PRESERVE\n", evidence.read_bytes())
        self.assertEqual(b"", marker.read_bytes())
        self.assertEqual(1, marker_stat.st_nlink)
        self.assertEqual(0o600, marker_stat.st_mode & 0o777)

    def test_retirement_crash_phases_leave_recoverable_or_retired_root(self):
        phases = (
            ("retire-before-marker", False),
            ("retire-marker-entry", True),
            ("retire-marker-fsync", True),
            ("retire-directory-fsync", True),
        )
        for index, (phase, marker_expected) in enumerate(phases):
            with self.subTest(phase=phase):
                root = self._seed_generation(
                    ".journal-" + f"{index + 2:032x}", batch_id=f"phase-{index}"
                )
                evidence = root / "evidence.bin"
                evidence.write_bytes(b"PRESERVE\n")
                statement = (
                    f"root_fd = os.open({str(root)!r}, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW); "
                    "lock_fd = m._open_owner_lock(root_fd); "
                    "m._retire_claimed_recovery_root(root_fd, lock_fd, 'verified')"
                )

                self._kill_at_phase(phase, statement)

                self.assertEqual(b"PRESERVE\n", evidence.read_bytes())
                self.assertEqual(marker_expected, (root / rf._RETIRED_MARKER).exists())
                root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                lock_fd = rf._open_owner_lock(root_fd)
                try:
                    rf._retire_claimed_recovery_root(root_fd, lock_fd, "verified")
                finally:
                    os.close(lock_fd)
                    os.close(root_fd)
                self.assertEqual(b"", (root / rf._RETIRED_MARKER).read_bytes())

    def test_retirement_rejects_malformed_marker_without_touching_children(self):
        variants = ("symlink", "directory", "hardlink", "nonzero", "mode")
        for index, variant in enumerate(variants):
            with self.subTest(variant=variant):
                root = self._seed_generation(
                    ".journal-" + f"{index + 10:032x}", batch_id=f"bad-{index}"
                )
                evidence = root / "evidence.bin"
                evidence.write_bytes(b"PRESERVE\n")
                marker = root / rf._RETIRED_MARKER
                if variant == "symlink":
                    external = self.repo / f"marker-external-{index}"
                    external.write_bytes(b"EXTERNAL\n")
                    marker.symlink_to(external)
                elif variant == "directory":
                    marker.mkdir()
                else:
                    marker.write_bytes(b"X" if variant == "nonzero" else b"")
                    marker.chmod(0o600)
                    if variant == "hardlink":
                        os.link(marker, root / "marker-peer")
                    elif variant == "mode":
                        marker.chmod(0o644)
                before = self._tree_snapshot(root)
                root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                lock_fd = rf._open_owner_lock(root_fd)
                try:
                    with self.assertRaises(rf.MigrationRecoveryFailure):
                        rf._retire_claimed_recovery_root(root_fd, lock_fd, "verified")
                finally:
                    os.close(lock_fd)
                    os.close(root_fd)
                self.assertEqual(before, self._tree_snapshot(root))

    def test_total_generation_resolver_retires_all_stale_roots_before_new_claim(self):
        roots = (
            self._seed_generation(".journal-" + "1" * 32, batch_id="batch-b"),
            self._seed_generation(".journal-" + "2" * 32, batch_id=None),
            self._seed_generation(".journal-" + "3" * 32, batch_id="batch-a"),
            self._seed_generation(".journal-" + "4" * 32, batch_id=None),
        )
        before = {root.name: self._tree_snapshot(root) for root in roots}

        claim = rf._claim_new_journal("target-batch")
        try:
            self.assertRegex(claim.root_name, r"^\.journal-[0-9a-f]{32}$")
            self.assertNotIn(claim.root_name, {root.name for root in roots})
            new_root = rf._journal_base() / claim.root_name
            manifest = json.loads(
                (new_root / "manifest.json").read_text(encoding="utf-8")
            )
            self.assertEqual("target-batch", manifest["batchId"])
            self.assertEqual("preparing", manifest["state"])
            for root in roots:
                self.assertTrue((root / rf._RETIRED_MARKER).is_file())
                prior = before[root.name]
                current = self._tree_snapshot(root)
                for name, value in prior.items():
                    self.assertEqual(value, current[name])
        finally:
            rf._retire_claimed_recovery_root(claim.root_fd, claim.lock_fd, "test-cleanup")
            claim.close()

    def test_total_generation_resolver_precedence_preserves_stale_on_duplicate_or_malformed(self):
        duplicate_a = self._seed_generation(".journal-" + "5" * 32, batch_id="duplicate")
        duplicate_b = self._seed_generation(".journal-" + "6" * 32, batch_id="duplicate")
        stale = self._seed_generation(".journal-" + "7" * 32, batch_id="stale")
        mutation_lock = rf._journal_base() / rf._RECOVERY_MUTATION_LOCK
        mutation_lock.write_bytes(b"")
        mutation_lock.chmod(0o600)
        before = self._tree_snapshot(rf._journal_base())

        with self.assertRaises(rf.MigrationRecoveryFailure):
            rf._claim_new_journal("target-batch")
        self.assertEqual(before, self._tree_snapshot(rf._journal_base()))

        shutil.rmtree(duplicate_b)
        malformed = duplicate_a / rf._RETIRED_MARKER
        malformed.write_bytes(b"MALFORMED\n")
        before = self._tree_snapshot(rf._journal_base())
        with self.assertRaises(rf.MigrationRecoveryFailure):
            rf._claim_new_journal("target-batch")
        self.assertEqual(before, self._tree_snapshot(rf._journal_base()))
        self.assertFalse((stale / rf._RETIRED_MARKER).exists())

    def test_total_generation_resolver_validates_every_manifest_before_mutation(self):
        stale = self._seed_generation(
            ".journal-" + "0" * 32, batch_id="stale-valid"
        )
        malformed = self._seed_generation(
            ".journal-" + "1" * 32, batch_id="malformed"
        )
        manifest = json.loads(
            (malformed / "manifest.json").read_text(encoding="utf-8")
        )
        manifest["targets"] = [{"path": "missing-contract-fields"}]
        (malformed / "manifest.json").write_text(
            json.dumps(manifest), encoding="utf-8"
        )
        mutation_lock = rf._journal_base() / rf._RECOVERY_MUTATION_LOCK
        mutation_lock.write_bytes(b"")
        mutation_lock.chmod(0o600)
        before = self._tree_snapshot(rf._journal_base())

        with self.assertRaises(rf.MigrationRecoveryFailure):
            rf._claim_new_journal("target-batch")

        self.assertEqual(before, self._tree_snapshot(rf._journal_base()))
        self.assertFalse((stale / rf._RETIRED_MARKER).exists())

    def test_total_generation_resolver_active_owner_blocks_before_stale_mutation(self):
        stale = self._seed_generation(".journal-" + "8" * 32, batch_id="stale")
        active = self._seed_generation(".journal-" + "9" * 32, batch_id="active")
        ready_read, ready_write = os.pipe()
        gate_read, gate_write = os.pipe()
        code = (
            "import fcntl, os\n"
            f"fd = os.open({str(active / rf._OWNER_LOCK)!r}, os.O_RDWR | os.O_NOFOLLOW)\n"
            "fcntl.flock(fd, fcntl.LOCK_EX)\n"
            f"os.write({ready_write}, b'R')\n"
            f"os.read({gate_read}, 1)\n"
        )
        process = subprocess.Popen(
            [sys.executable, "-c", code], pass_fds=(ready_write, gate_read)
        )
        os.close(ready_write)
        os.close(gate_read)
        try:
            self.assertEqual(b"R", os.read(ready_read, 1))
            mutation_lock = rf._journal_base() / rf._RECOVERY_MUTATION_LOCK
            mutation_lock.write_bytes(b"")
            mutation_lock.chmod(0o600)
            before = self._tree_snapshot(rf._journal_base())
            with self.assertRaises(rf.MigrationRecoveryRequired):
                rf._claim_new_journal("target-batch")
            self.assertEqual(before, self._tree_snapshot(rf._journal_base()))
            self.assertFalse((stale / rf._RETIRED_MARKER).exists())
        finally:
            os.kill(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
            os.close(ready_read)
            os.close(gate_write)

    def test_total_generation_resolver_rescan_blocks_new_root_without_claiming_it(self):
        stale = self._seed_generation(".journal-" + "a" * 32, batch_id="stale")
        real_retire = rf._retire_claimed_recovery_root
        injected: Path | None = None

        def retire_then_inject(claimed_fd, owner_lock_fd, operation):
            nonlocal injected
            real_retire(claimed_fd, owner_lock_fd, operation)
            if injected is None:
                injected = self._seed_generation(
                    ".journal-" + "b" * 32, batch_id="late"
                )

        with patch.object(
            rf, "_retire_claimed_recovery_root", side_effect=retire_then_inject
        ), self.assertRaises(rf.MigrationRecoveryRequired):
            rf._claim_new_journal("target-batch")

        self.assertTrue((stale / rf._RETIRED_MARKER).is_file())
        assert injected is not None
        self.assertFalse((injected / rf._RETIRED_MARKER).exists())

    def test_read_only_resolver_is_byte_identical_and_ignores_valid_retired_root(self):
        retired = self._seed_generation(
            ".journal-" + "c" * 32, batch_id="retired", marker=b""
        )
        legacy_retired = self._seed_generation(
            ".legacy-retired", batch_id="legacy-retired", marker=b""
        )
        pending = self._seed_generation(".journal-" + "d" * 32, batch_id="pending")
        before = self._tree_snapshot(rf._journal_base())

        proc = run_cli(["--check", "--batch", "final-all-spec"], self.repo)

        self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
        self.assertIn("MIGRATION_RECOVERY_REQUIRED", proc.stdout)
        self.assertEqual(before, self._tree_snapshot(rf._journal_base()))
        self.assertTrue((retired / rf._RETIRED_MARKER).is_file())
        self.assertTrue((legacy_retired / rf._RETIRED_MARKER).is_file())
        self.assertFalse((pending / rf._RETIRED_MARKER).exists())

    def test_total_manifest_validation_mutation_is_killed(self):
        stale = self._seed_generation(
            ".journal-" + "0" * 32, batch_id="stale-valid"
        )
        malformed = self._seed_generation(
            ".journal-" + "1" * 32, batch_id="malformed"
        )
        manifest_path = malformed / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["targets"] = [{"path": "missing-contract-fields"}]
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        guard = (
            "                claim = JournalClaim(batch_id, name, root_fd, lock_fd)\n"
            "                _read_journal(\n"
            "                    batch_id,\n"
            "                    claim=claim,\n"
            "                    allowed_entries=allowed_swaps.get(name),\n"
            "                )\n"
        )
        self.assertEqual(1, source.count(guard))
        mutated = source.replace(
            guard,
            "                claim = JournalClaim(batch_id, name, root_fd, lock_fd)\n"
            "                pass  # mutation: full manifest validation ถูกตัด\n",
        )
        module_path = self.repo / "manifest-validation-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_manifest_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(module_name, module_path)
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)

        with self.assertRaises(module.MigrationRecoveryFailure):
            module._claim_new_journal("target-batch")
        self.assertTrue(
            (stale / module._RETIRED_MARKER).is_file(),
            "sanity: ตัด full classification แล้ว stale root ถูก mutate ก่อนเจอ malformed",
        )

    def test_retired_marker_validation_mutation_is_killed(self):
        malformed = self._seed_generation(
            ".journal-" + "2" * 32, batch_id="malformed-marker"
        )
        marker = malformed / rf._RETIRED_MARKER
        marker.write_bytes(b"MALFORMED\n")
        marker.chmod(0o600)
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        start = source.index("def _retired_marker_state(")
        end = source.index("\ndef ", start + 4)
        mutated = (
            source[:start]
            + "def _retired_marker_state(claimed_fd: int) -> bool:\n"
            + "    return True  # mutation: malformed marker ถูกยอมรับ\n"
            + source[end:]
        )
        module_path = self.repo / "marker-validation-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_marker_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(module_name, module_path)
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)

        claim = module._claim_new_journal("target-batch")
        try:
            self.assertFalse(
                (malformed / module._RETIRED_MARKER).read_bytes() == b"",
                "sanity: mutation ต้อง ignore malformed marker เดิม",
            )
        finally:
            claim.close()

    def test_noop_apply_does_not_add_generation_marker_or_retained_bytes(self):
        for _attempt in range(2):
            proc = run_cli(
                ["--apply-safe", "--batch", "approved-aliases"], self.repo
            )
            self.assertEqual(0, proc.returncode, proc.stdout + proc.stderr)
        journal_base = rf._journal_base()
        generations = [] if not journal_base.exists() else [
            path for path in journal_base.iterdir() if path.is_dir()
        ]
        self.assertEqual([], generations)
        self.assertFalse(rf._write_intent_root().exists())

    def test_retirement_none_owner_proves_absence_or_blocks_active_owner(self):
        root = rf._journal_base() / (".journal-" + "f" * 32)
        root.mkdir(parents=True, mode=0o700)
        root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            rf._retire_claimed_recovery_root(root_fd, None, "create-error")
        finally:
            os.close(root_fd)
        self.assertTrue((root / rf._RETIRED_MARKER).is_file())

        active = rf._journal_base() / (".journal-" + "e" * 32)
        active.mkdir(mode=0o700)
        owner = active / rf._OWNER_LOCK
        owner.write_bytes(b"OWNER\n")
        owner.chmod(0o600)
        ready_read, ready_write = os.pipe()
        gate_read, gate_write = os.pipe()
        code = (
            "import fcntl, os\n"
            f"fd = os.open({str(owner)!r}, os.O_RDWR | os.O_NOFOLLOW)\n"
            "fcntl.flock(fd, fcntl.LOCK_EX)\n"
            f"os.write({ready_write}, b'R')\n"
            f"os.read({gate_read}, 1)\n"
        )
        process = subprocess.Popen(
            [sys.executable, "-c", code], pass_fds=(ready_write, gate_read)
        )
        os.close(ready_write)
        os.close(gate_read)
        active_fd = os.open(active, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            self.assertEqual(b"R", os.read(ready_read, 1))
            before = self._tree_snapshot(active)
            with self.assertRaises(rf.MigrationRecoveryFailure):
                rf._retire_claimed_recovery_root(
                    active_fd, None, "create-error"
                )
            self.assertEqual(before, self._tree_snapshot(active))
        finally:
            os.close(active_fd)
            os.kill(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
            os.close(ready_read)
            os.close(gate_write)

    def test_retirement_none_owner_rejects_existing_unlocked_owner(self):
        root = rf._journal_base() / (".journal-" + "a" * 32)
        root.mkdir(parents=True, mode=0o700)
        owner = root / rf._OWNER_LOCK
        owner.write_bytes(b"FOREIGN UNLOCKED\n")
        owner.chmod(0o600)
        before = self._tree_snapshot(root)
        root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            with self.assertRaises(rf.MigrationRecoveryFailure):
                rf._retire_claimed_recovery_root(root_fd, None, "create-error")
        finally:
            os.close(root_fd)
        self.assertEqual(before, self._tree_snapshot(root))
        self.assertFalse((root / rf._RETIRED_MARKER).exists())

    def test_retired_marker_inode_mismatch_is_malformed(self):
        root = self._seed_generation(
            ".journal-" + "d" * 32, batch_id="marker-inode", marker=b""
        )
        root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            with patch.object(rf, "_same_inode", return_value=False), \
                    self.assertRaises(rf.MigrationRecoveryFailure):
                rf._retired_marker_state(root_fd)
        finally:
            os.close(root_fd)

    def test_retirement_callers_use_exact_fd_only_seam_without_root_deletion(self):
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        start = source.index("def _retire_claimed_recovery_root(")
        end = source.index("\ndef ", start + 4)
        helper = source[start:end]
        self.assertIn(
            "def _retire_claimed_recovery_root(\n"
            "    claimed_fd: int, owner_lock_fd: int | None, operation: str\n"
            ") -> None:",
            helper,
        )
        for forbidden in ("unlink(", "rmdir(", "rmtree(", "rename(", "replace("):
            self.assertNotIn(forbidden, helper)
        self.assertNotIn("base_fd", helper)
        self.assertNotIn("basename", helper)
        bodies = {}
        for caller in (
            "_create_write_intent",
            "_claim_new_journal",
            "_delete_write_intent",
            "_remove_cleanup_tombstone",
            "_remove_incomplete_journals",
            "clear_journal",
        ):
            caller_start = source.index(f"def {caller}(")
            caller_end = source.index("\ndef ", caller_start + 4)
            bodies[caller] = source[caller_start:caller_end]
            self.assertIn("_retire_claimed_recovery_root(", bodies[caller])
        self.assertIn('"create-error"', bodies["_create_write_intent"])
        self.assertIn('"create-error"', bodies["_claim_new_journal"])
        self.assertIn("claim.root_fd, claim.lock_fd, operation", bodies["_delete_write_intent"])
        self.assertIn('"legacy-cleaning"', bodies["_remove_cleanup_tombstone"])
        self.assertIn(
            '"incomplete-before-manifest"', bodies["_remove_incomplete_journals"]
        )
        self.assertIn("claim.root_fd, claim.lock_fd, operation", bodies["clear_journal"])
        for caller in (
            "_delete_write_intent",
            "_remove_cleanup_tombstone",
            "_remove_incomplete_journals",
            "clear_journal",
        ):
            for forbidden in ("unlink(", "rmdir(", "rmtree(", "rename(", "replace("):
                self.assertNotIn(forbidden, bodies[caller])

    def test_retired_intent_does_not_unlink_reused_swap_name(self):
        target = rf.repo_root() / "reused-swap-name.md"
        target.write_bytes(b"BEFORE\n")
        foreign = b"FOREIGN REUSED NAME\n"
        real_delete = rf._delete_write_intent
        reused: Path | None = None

        def retire_then_reuse(claim, operation):
            nonlocal reused
            intent = rf._load_write_intent(claim.root_fd)
            real_delete(claim, operation)
            reused = target.parent / str(intent["swapName"])
            reused.write_bytes(foreign)

        with patch.object(rf, "_delete_write_intent", side_effect=retire_then_reuse):
            rf._atomic_write_repo_file(target, b"PLANNED\n")

        assert reused is not None
        self.assertEqual(b"PLANNED\n", target.read_bytes())
        self.assertEqual(foreign, reused.read_bytes())

    def test_disposable_cleanup_rejects_replaced_inode(self):
        target = rf.repo_root() / ".disposable-swap"
        target.write_bytes(b"OWNED\n")
        expected = os.stat(target, follow_symlinks=False)
        replacement = rf.repo_root() / ".disposable-replacement"
        replacement.write_bytes(b"FOREIGN\n")
        target.unlink()
        replacement.replace(target)

        directory_fd = os.open(
            rf.repo_root(), os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
        )
        try:
            with self.assertRaises(rf.MigrationRecoveryFailure):
                rf._unlink_disposable_entry(directory_fd, target.name, expected)
        finally:
            os.close(directory_fd)

        self.assertEqual(b"FOREIGN\n", target.read_bytes())

    def test_total_resolver_uses_manifest_published_before_owner_lock(self):
        target_rel = "locked-manifest-target.md"
        target = rf.repo_root() / target_rel
        before = b"BEFORE\n"
        planned = b"PLANNED\n"
        target.write_bytes(planned)
        batch_id = "locked-snapshot"
        root = self._seed_generation(
            ".journal-" + "1" * 32, batch_id=batch_id
        )
        original_name = f"{rf._stable_index(target_rel)}.bin"
        manifest = {
            "batchId": batch_id,
            "capturedHead": "cafe",
            "schemaVersion": 1,
            "state": "preparing",
            "targets": [{
                "applied": False,
                "beforeSha256": rf.sha256(before),
                "originalFile": original_name,
                "path": target_rel,
                "pending": True,
                "plannedSha256": rf.sha256(planned),
            }],
        }
        real_open_lock = rf._open_structural_owner_lock
        published = False

        def publish_then_open(root_fd):
            nonlocal published
            if not published and rf._same_inode(
                os.fstat(root_fd), os.stat(root, follow_symlinks=False)
            ):
                published = True
                originals = root / "originals"
                originals.mkdir(mode=0o700)
                (originals / original_name).write_bytes(before)
                (root / "manifest.json").write_text(
                    json.dumps(manifest), encoding="utf-8"
                )
            return real_open_lock(root_fd)

        with patch.object(
            rf, "_open_structural_owner_lock", side_effect=publish_then_open
        ):
            claim = rf._claim_new_journal("target-batch")
        try:
            self.assertTrue(published)
            self.assertEqual(before, target.read_bytes())
            self.assertTrue((root / rf._RETIRED_MARKER).is_file())
        finally:
            rf._retire_claimed_recovery_root(
                claim.root_fd, claim.lock_fd, "test-cleanup"
            )
            claim.close()

    def test_total_resolver_rejects_foreign_direct_children_before_mutation(self):
        for root_kind in ("manifest", "opaque", "cleanup"):
            for attack in ("regular", "symlink", "hardlink"):
                with self.subTest(root_kind=root_kind, attack=attack):
                    base = rf._journal_base()
                    shutil.rmtree(base, ignore_errors=True)
                    stale = self._seed_generation(
                        ".journal-" + "0" * 32, batch_id="stale-valid"
                    )
                    name = (
                        ".clearing-malformed"
                        if root_kind == "cleanup"
                        else ".journal-" + "1" * 32
                    )
                    malformed = self._seed_generation(
                        name,
                        batch_id="malformed" if root_kind == "manifest" else None,
                    )
                    foreign = malformed / "foreign.bin"
                    if attack == "symlink":
                        external = self.repo / f"external-{root_kind}-{attack}"
                        external.write_bytes(b"EXTERNAL\n")
                        foreign.symlink_to(external)
                    else:
                        foreign.write_bytes(b"FOREIGN\n")
                        if attack == "hardlink":
                            os.link(foreign, malformed / "foreign-peer.bin")
                    mutation_lock = base / rf._RECOVERY_MUTATION_LOCK
                    mutation_lock.write_bytes(b"")
                    mutation_lock.chmod(0o600)
                    before_tree = self._tree_snapshot(base)

                    with self.assertRaises(rf.MigrationRecoveryFailure):
                        rf._claim_new_journal("target-batch")

                    self.assertEqual(before_tree, self._tree_snapshot(base))
                    self.assertFalse((stale / rf._RETIRED_MARKER).exists())

    def test_total_resolver_rejects_non_exact_original_inventory(self):
        target_rel = "original-inventory-target.md"
        target = rf.repo_root() / target_rel
        before = b"BEFORE\n"
        planned = b"PLANNED\n"
        target.write_bytes(planned)
        for attack in ("regular", "symlink", "hardlink"):
            with self.subTest(attack=attack):
                base = rf._journal_base()
                shutil.rmtree(base, ignore_errors=True)
                root = self._seed_generation(
                    ".journal-" + "2" * 32, batch_id="inventory"
                )
                original_name = f"{rf._stable_index(target_rel)}.bin"
                (root / "manifest.json").write_text(json.dumps({
                    "batchId": "inventory",
                    "capturedHead": "cafe",
                    "schemaVersion": 1,
                    "state": "preparing",
                    "targets": [{
                        "applied": False,
                        "beforeSha256": rf.sha256(before),
                        "originalFile": original_name,
                        "path": target_rel,
                        "pending": True,
                        "plannedSha256": rf.sha256(planned),
                    }],
                }), encoding="utf-8")
                originals = root / "originals"
                originals.mkdir(mode=0o700)
                (originals / original_name).write_bytes(before)
                foreign = originals / "foreign.bin"
                if attack == "symlink":
                    external = self.repo / f"external-original-{attack}"
                    external.write_bytes(b"EXTERNAL\n")
                    foreign.symlink_to(external)
                else:
                    foreign.write_bytes(b"FOREIGN\n")
                    if attack == "hardlink":
                        os.link(foreign, originals / "foreign-peer.bin")
                mutation_lock = base / rf._RECOVERY_MUTATION_LOCK
                mutation_lock.write_bytes(b"")
                mutation_lock.chmod(0o600)
                before_tree = self._tree_snapshot(base)

                with self.assertRaises(rf.MigrationRecoveryFailure):
                    rf._claim_new_journal("target-batch")

                self.assertEqual(before_tree, self._tree_snapshot(base))
                self.assertEqual(planned, target.read_bytes())

    def test_retirement_none_owner_rejects_non_create_error_operation(self):
        root = rf._journal_base() / (".journal-" + "3" * 32)
        root.mkdir(parents=True, mode=0o700)
        root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            with self.assertRaises(rf.MigrationRecoveryFailure):
                rf._retire_claimed_recovery_root(root_fd, None, "verified")
        finally:
            os.close(root_fd)
        self.assertFalse((root / rf._RETIRED_MARKER).exists())

    def test_disposable_cleanup_preserves_foreign_entry_reused_after_identity_check(self):
        directory_fd = os.open(
            rf.repo_root(), os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
        )
        owned = rf.repo_root() / ".owned-disposable"
        foreign = rf.repo_root() / ".foreign-disposable"
        owned.write_bytes(b"OWNED\n")
        foreign.write_bytes(b"FOREIGN\n")
        expected = os.stat(owned, follow_symlinks=False)
        real_stat = os.stat
        raced = False

        def replace_after_stat(path, *args, **kwargs):
            nonlocal raced
            result = real_stat(path, *args, **kwargs)
            if (
                not raced
                and path == owned.name
                and kwargs.get("dir_fd") == directory_fd
            ):
                raced = True
                os.replace(
                    foreign.name,
                    owned.name,
                    src_dir_fd=directory_fd,
                    dst_dir_fd=directory_fd,
                )
            return result

        try:
            with patch.object(os, "stat", side_effect=replace_after_stat):
                with contextlib.suppress(rf.MigrationRecoveryFailure):
                    rf._unlink_disposable_entry(directory_fd, owned.name, expected)
        finally:
            os.close(directory_fd)

        if raced:
            retained = list(rf._disposable_root().glob(".retained-*/entry"))
            self.assertEqual(1, len(retained))
            self.assertEqual(b"FOREIGN\n", retained[0].read_bytes())
        else:
            self.assertEqual(b"FOREIGN\n", foreign.read_bytes())

    def test_disposable_cleanup_never_unlinks_foreign_swapped_after_validation(self):
        directory_fd = os.open(
            rf.repo_root(), os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
        )
        owned = rf.repo_root() / ".owned-after-validation"
        foreign = rf.repo_root() / ".foreign-after-validation"
        owned.write_bytes(b"OWNED\n")
        foreign.write_bytes(b"FOREIGN\n")
        expected = os.stat(owned, follow_symlinks=False)
        real_same_inode = rf._same_inode
        raced_path: Path | None = None

        def replace_after_validation(left, right):
            nonlocal raced_path
            result = real_same_inode(left, right)
            if result and raced_path is None:
                candidates = list(rf._disposable_root().glob(".retained-*/entry"))
                if candidates:
                    raced_path = candidates[0]
                    os.replace(foreign, raced_path)
            return result

        try:
            with patch.object(rf, "_same_inode", side_effect=replace_after_validation):
                rf._unlink_disposable_entry(directory_fd, owned.name, expected)
        finally:
            os.close(directory_fd)

        self.assertIsNotNone(raced_path, "test ต้องสลับ inode หลัง validation จริง")
        assert raced_path is not None
        self.assertTrue(raced_path.exists(), "foreign inode ต้องไม่ถูก pathname unlink")
        self.assertEqual(b"FOREIGN\n", raced_path.read_bytes())
        self.assertTrue((raced_path.parent / rf._RETIRED_MARKER).is_file())

    def test_planned_swap_is_never_durable_before_its_intent(self):
        target = rf.repo_root() / "orphan-before-intent.md"
        target.write_bytes(b"BEFORE\n")
        ready_read, ready_write = os.pipe()
        gate_read, gate_write = os.pipe()
        env = dict(
            os.environ,
            SDD_RETROFIT_REPO=str(self.repo),
            PYTHONDONTWRITEBYTECODE="1",
        )
        code = (
            "import importlib.util, os, pathlib, sys\n"
            f"sys.path.insert(0, {str(SCRIPTS)!r})\n"
            f"p = pathlib.Path({str(SCRIPTS / 'spec-retrofit.py')!r})\n"
            "s = importlib.util.spec_from_file_location('orphan_child', p)\n"
            "m = importlib.util.module_from_spec(s)\n"
            "sys.modules['orphan_child'] = m\n"
            "s.loader.exec_module(m)\n"
            "real = m._write_new_regular_at\n"
            "def stop_after_swap(*args, **kwargs):\n"
            "    result = real(*args, **kwargs)\n"
            "    if str(args[1]).startswith('.sdd-retrofit-swap-'):\n"
            f"        os.write({ready_write}, b'R')\n"
            f"        os.read({gate_read}, 1)\n"
            "    return result\n"
            "m._write_new_regular_at = stop_after_swap\n"
            f"m._atomic_write_repo_file(pathlib.Path({str(target)!r}), b'PLANNED\\n')\n"
        )
        process = subprocess.Popen(
            [sys.executable, "-c", code],
            env=env,
            pass_fds=(ready_write, gate_read),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        os.close(ready_write)
        os.close(gate_read)
        try:
            self.assertEqual(b"R", os.read(ready_read, 1))
            os.kill(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
            self.assertEqual(-signal.SIGKILL, process.returncode)
        finally:
            os.close(ready_read)
            os.close(gate_write)
            if process.poll() is None:
                process.kill()
                process.wait(timeout=5)
            if process.stdout is not None:
                process.stdout.close()
            if process.stderr is not None:
                process.stderr.close()

        swaps = list(target.parent.glob(".sdd-retrofit-swap-*"))
        intent_root = rf._write_intent_root()
        roots = [] if not intent_root.exists() else [
            path for path in intent_root.iterdir() if path.is_dir()
        ]
        self.assertEqual(1, len(swaps))
        self.assertEqual(1, len(roots), "durable swap ต้องมี recovery owner ก่อนเสมอ")
        self.assertTrue((roots[0] / rf._OWNER_LOCK).is_file())
        self.assertTrue((roots[0] / rf._WRITE_INTENT_FILE).is_file())
        rf._reconcile_write_intents()
        self.assertEqual([], list(target.parent.glob(".sdd-retrofit-swap-*")))
        self.assertEqual(b"BEFORE\n", target.read_bytes())

    def test_write_intent_rejects_non_exact_direct_children_before_read_or_mutation(self):
        for attack in ("regular", "symlink", "directory", "hardlink"):
            with self.subTest(attack=attack):
                target = rf.repo_root() / f"intent-inventory-{attack}.md"
                target.write_bytes(b"BEFORE\n")
                self._kill_at_phase(
                    "intent-fsync",
                    f"m._atomic_write_repo_file(pathlib.Path({str(target)!r}), b'PLANNED\\n')",
                )
                intent_root = rf._write_intent_root()
                root = next(path for path in intent_root.iterdir() if path.is_dir())
                foreign = root / "foreign.bin"
                external = rf.repo_root() / f"intent-inventory-external-{attack}.bin"
                try:
                    if attack == "symlink":
                        external.write_bytes(b"FOREIGN\n")
                        foreign.symlink_to(external)
                    elif attack == "directory":
                        foreign.mkdir()
                    elif attack == "hardlink":
                        external.write_bytes(b"FOREIGN\n")
                        os.link(external, foreign)
                    else:
                        foreign.write_bytes(b"FOREIGN\n")
                    before_tree = self._tree_snapshot(intent_root)
                    before_target = target.read_bytes()

                    with self.assertRaises(rf.MigrationRecoveryFailure):
                        rf.write_intents_pending()
                    self.assertEqual(before_tree, self._tree_snapshot(intent_root))
                    self.assertEqual(before_target, target.read_bytes())

                    proc = run_cli(
                        ["--apply-safe", "--batch", "approved-aliases"], self.repo
                    )
                    self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
                    payload = json.loads(proc.stdout)
                    self.assertEqual(
                        "MIGRATION_RECOVERY_FAILED",
                        payload["diagnostics"][0]["code"],
                    )
                    self.assertEqual(before_tree, self._tree_snapshot(intent_root))
                    self.assertEqual(before_target, target.read_bytes())
                finally:
                    shutil.rmtree(intent_root, ignore_errors=True)
                    for swap in target.parent.glob(".sdd-retrofit-swap-*"):
                        swap.unlink(missing_ok=True)

    def test_retirement_none_owner_cannot_race_active_owner_before_marker(self):
        root = rf._journal_base() / (".journal-" + "4" * 32)
        root.mkdir(parents=True, mode=0o700)
        root_fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        real_open = os.open
        real_stat = os.stat
        active_fd: int | None = None
        injected_tree = None

        def inject_active_owner() -> None:
            nonlocal active_fd, injected_tree
            if active_fd is not None:
                return
            active_fd = real_open(
                rf._OWNER_LOCK,
                os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                0o600,
                dir_fd=root_fd,
            )
            rf.fcntl.flock(active_fd, rf.fcntl.LOCK_EX | rf.fcntl.LOCK_NB)
            os.fsync(active_fd)
            injected_tree = self._tree_snapshot(root)

        def race_stat(path, *args, **kwargs):
            if (
                active_fd is None
                and path == rf._OWNER_LOCK
                and kwargs.get("dir_fd") == root_fd
            ):
                inject_active_owner()
                raise FileNotFoundError(errno.ENOENT, os.strerror(errno.ENOENT), path)
            return real_stat(path, *args, **kwargs)

        def race_open(path, flags, mode=0o777, *, dir_fd=None):
            if (
                active_fd is None
                and path == rf._OWNER_LOCK
                and dir_fd == root_fd
                and flags & os.O_EXCL
            ):
                inject_active_owner()
                raise FileExistsError(errno.EEXIST, os.strerror(errno.EEXIST), path)
            return real_open(path, flags, mode, dir_fd=dir_fd)

        try:
            with patch.object(os, "stat", side_effect=race_stat), \
                    patch.object(os, "open", side_effect=race_open), \
                    self.assertRaises(rf.MigrationRecoveryFailure):
                rf._retire_claimed_recovery_root(root_fd, None, "create-error")
        finally:
            if active_fd is not None:
                os.close(active_fd)
            os.close(root_fd)

        self.assertIsNotNone(injected_tree)
        self.assertEqual(injected_tree, self._tree_snapshot(root))
        self.assertFalse((root / rf._RETIRED_MARKER).exists())

    def test_swap_cleanup_class_has_no_unbound_unlink_calls(self):
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        self.assertIn("def _unlink_disposable_entry(", source)
        self.assertEqual(0, source.count("os.unlink("))
        for caller in (
            "_create_write_intent",
            "_reconcile_one_write_intent",
            "_atomic_write_at",
        ):
            start = source.index(f"def {caller}(")
            end = source.index("\ndef ", start + 4)
            self.assertIn("_unlink_disposable_entry(", source[start:end])
        probe_start = source.index("def _probe_atomic_exchange(")
        probe_end = source.index("\ndef ", probe_start + 4)
        probe = source[probe_start:probe_end]
        self.assertIn("_claim_disposable_generation(", probe)
        self.assertNotIn("_unlink_disposable_entry(", probe)

    def test_cross_device_retention_fails_before_probe_or_disposable_artifacts(self):
        target = rf.repo_root() / "cross-device.md"
        target.write_bytes(b"BEFORE\n")
        target_directory = os.stat(target.parent, follow_symlinks=False)

        def cross_mount_identity(fd):
            result = os.fstat(fd)
            if (result.st_dev, result.st_ino) == (
                target_directory.st_dev,
                target_directory.st_ino,
            ):
                return ("mount", 1)
            return ("mount", 2)

        with patch.object(rf, "_mount_identity", side_effect=cross_mount_identity), \
                self.assertRaisesRegex(
                    rf.MigrationRecoveryFailure,
                    "DISPOSABLE_RETENTION_CROSS_DEVICE",
                ):
            rf._atomic_write_repo_file(target, b"AFTER\n")

        directory_fd = os.open(
            target.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
        )
        try:
            with patch.object(rf, "_mount_identity", side_effect=cross_mount_identity), \
                    self.assertRaisesRegex(
                        rf.MigrationRecoveryFailure,
                        "DISPOSABLE_RETENTION_CROSS_DEVICE",
                    ):
                rf._probe_atomic_exchange(directory_fd)
        finally:
            os.close(directory_fd)

        missing = rf.repo_root() / "cross-device-missing.md"
        with patch.object(rf, "_mount_identity", side_effect=cross_mount_identity), \
                self.assertRaisesRegex(
                    rf.MigrationRecoveryFailure,
                    "DISPOSABLE_RETENTION_CROSS_DEVICE",
                ):
            rf._atomic_write_repo_file(missing, b"AFTER\n")

        self.assertEqual(b"BEFORE\n", target.read_bytes())
        self.assertFalse(missing.exists())
        self.assertEqual([], list(target.parent.glob(".sdd-retrofit-exchange-*")))
        self.assertEqual([], list(target.parent.glob(".sdd-retrofit-swap-*")))
        self.assertFalse(rf._disposable_root().exists())
        self.assertFalse(rf._write_intent_root().exists())

    def test_exchange_probe_never_uses_cross_directory_disposable_move(self):
        directory_fd = os.open(
            rf.repo_root(), os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
        )
        try:
            with patch.object(
                rf,
                "_atomic_rename_noreplace",
                side_effect=OSError(errno.EXDEV, os.strerror(errno.EXDEV)),
            ) as cross_move:
                rf._probe_atomic_exchange(directory_fd)
        finally:
            os.close(directory_fd)

        self.assertEqual(0, cross_move.call_count)
        self.assertEqual(
            [], list(rf.repo_root().glob(".sdd-retrofit-exchange-*"))
        )
        roots = list(rf._disposable_root().glob(".retained-*"))
        self.assertEqual(1, len(roots))
        self.assertTrue((roots[0] / rf._RETIRED_MARKER).is_file())

    def test_mount_identity_guard_mutation_is_killed(self):
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        guard = (
            "        if _mount_identity(directory_fd) != "
            "_mount_identity(retention_fd):\n"
        )
        self.assertEqual(1, source.count(guard))
        mutated = source.replace(guard, "        if False:\n", 1)
        module_path = self.repo / "mount-identity-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_mount_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(
            module_name, module_path
        )
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)

        target = module.repo_root() / "mount-mutation.md"
        target.write_bytes(b"BEFORE\n")
        target_directory = os.stat(target.parent, follow_symlinks=False)

        def cross_mount_identity(fd):
            observed = os.fstat(fd)
            if (observed.st_dev, observed.st_ino) == (
                target_directory.st_dev,
                target_directory.st_ino,
            ):
                return ("mount", 1)
            return ("mount", 2)

        with patch.object(
            module, "_mount_identity", side_effect=cross_mount_identity
        ):
            module._atomic_write_repo_file(target, b"AFTER\n")
        self.assertEqual(
            b"AFTER\n", target.read_bytes(),
            "sanity: mutation ต้องข้าม cross-mount preflightจริง",
        )

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


class ResolutionLedgerTest(RetrofitSandbox):
    """Human-decision ledger drives safe actions where history has no proof."""

    def ledger(self, *decisions: dict) -> Path:
        target = self.repo / ".ai" / "specs" / "sdd-operating-layer-parity" / \
            "migration-resolutions.json"
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(
            {"_meta": {"authority": "human checkpoint"}, "decisions": list(decisions)},
            ensure_ascii=False), encoding="utf-8")
        return target

    def _entries(self, payload: str) -> list[dict]:
        return json.loads(payload).get("actions", [])

    def test_rename_canonical_id_rewrites_hyphenless_bugfix_ids(self):
        directory = self.feature("bugfix-hyphen-demo")
        (directory / "bugfix.md").write_text(
            "# Bugfix: demo\n\n- F1 WHEN the SDD task runs THE SYSTEM SHALL pass.\n"
            "- B1 WHILE cache is warm THE SYSTEM SHALL reuse results.\n",
            encoding="utf-8")
        self.commit_all("seed alias-ish bugfix doc")
        self.ledger({"path": ".ai/specs/bugfix-hyphen-demo/bugfix.md",
                     "field": "bugfix.criterion", "disposition": "rename-canonical-id",
                     "rationale": "mechanical"})
        self.commit_all("record human resolution")
        proc = run_cli(["--apply-safe", "--batch", "bugfix"], self.repo)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        text = (directory / "bugfix.md").read_text(encoding="utf-8")
        self.assertIn("- F-1 WHEN the SDD task runs", text)
        self.assertIn("- B-1 WHILE cache is warm", text)
        self.assertNotIn("F1 WHEN", text)

    def test_status_unknown_replaces_alias_without_history(self):
        directory = self.feature("status-unknown-demo")
        (directory / "tasks.md").write_text(
            "# Legacy\n\n> Status: shipped-and-done 2024-01-01\n\n"
            "- [x] A1. ship.\n\n     Evidence:\n     - test: `demo` -> ok\n",
            encoding="utf-8")
        self.commit_all("alias status without explicit approved line")
        self.ledger({"path": ".ai/specs/status-unknown-demo/tasks.md",
                     "field": "status.line", "disposition": "status-unknown",
                     "rationale": "no owner recorded"})
        self.commit_all("record human resolution")
        proc = run_cli(["--apply-safe", "--batch", "approved-aliases"], self.repo)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        text = (directory / "tasks.md").read_text(encoding="utf-8")
        self.assertIn("> Status: unknown", text)
        self.assertNotIn("shipped-and-done", text)

    def test_evidence_waiver_inserts_na_viewports_and_deviations(self):
        directory = self.feature("evidence-waive-demo")
        (directory / "tasks.md").write_text(
            "# Feature\n\n> Status: approved 2024-02-02\n\n"
            "- [x] V1. visible work.\n\n"
            "     Evidence:\n"
            "     - test: `pytest -q` -> 5 passed\n",
            encoding="utf-8")
        self.commit_all("seed evidence task missing vp/dev")
        tasks_rel = ".ai/specs/evidence-waive-demo/tasks.md"
        self.ledger(
            {"path": tasks_rel, "field": "evidence.viewports",
             "disposition": "waive-protocol-history", "rationale": "predates protocol"},
            {"path": tasks_rel, "field": "evidence.deviations",
             "disposition": "waive-protocol-history", "rationale": "predates protocol"},
        )
        self.commit_all("record human resolution")
        dry = run_cli(["--dry-run", "--batch", "evidence", "--format", "json"], self.repo)
        payload = json.loads(dry.stdout)
        fields = {action["targetField"] for action in payload["actions"]}
        self.assertIn("evidence.viewports", fields)
        self.assertIn("evidence.deviations", fields)
        proc = run_cli(["--apply-safe", "--batch", "evidence"], self.repo)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        import importlib.util as _ilu
        sc_spec = _ilu.spec_from_file_location("sc_probe", SCRIPTS / "spec_contract.py")
        sc = importlib.util.module_from_spec(sc_spec); sys.modules["sc_probe"] = sc
        sc_spec.loader.exec_module(sc)
        tasks, diags = sc.parse_task_blocks((directory / "tasks.md").read_bytes(),
                                            Path(tasks_rel))
        self.assertFalse(diags)
        problems = {p.code for p in sc._task_evidence_problems(tasks[0])}
        self.assertNotIn("EVIDENCE_VIEWPORTS_INVALID", problems)
        self.assertNotIn("EVIDENCE_DEVIATIONS_MISSING", problems)
        self.assertIn("n/a \u2014 legacy corpus predates viewport protocol",
                      "\n".join(tasks[0].evidence))

    def test_active_authoring_exempt_removes_chain_blocker(self):
        directory = self.feature("active-chain-demo")
        (directory / "requirements.md").write_text(
            "# Req\n\n## REQ-1: Work\n\n- 1.1 WHEN x occurs THE SYSTEM SHALL y.\n",
            encoding="utf-8")
        (directory / "design.md").write_text("# Design\n\ndraft\n", encoding="utf-8")
        (directory / "tasks.md").write_text(
            "# Tasks\n\n> Status: draft\n\n- [ ] 1. pending work.\n", encoding="utf-8")
        self.commit_all("seed incomplete authoring chain")
        base = {"path": ".ai/specs/active-chain-demo/tasks.md",
                "field": "authoring.chain"}
        plain = run_cli(["--check", "--batch", "canonical-complete", "--format", "json"],
                        self.repo)
        self.assertNotEqual(plain.returncode, 0)
        self.ledger({**base, "disposition": "active-authoring-exempt",
                     "rationale": "incomplete by design"})
        exempt = run_cli(["--check", "--batch", "canonical-complete", "--format", "json"],
                         self.repo)
        self.assertEqual(exempt.returncode, 0, exempt.stdout)
        self.assertEqual(json.loads(exempt.stdout)["verdict"], "allow")

    def test_emit_resolution_template_is_deterministic_and_prefilled(self):
        directory = self.feature("template-demo")
        (directory / "bugfix.md").write_text(
            "# Bugfix: t\n\n- F9 WHEN k happens THEN finish.\n", encoding="utf-8")
        self.commit_all("seed near-miss bugfix")
        out_rel = ".pipeline/template-out.json"
        first = run_cli(["--check", "--batch", "bugfix",
                         "--emit-resolution-template", out_rel], self.repo)
        self.assertEqual(first.returncode, 0, first.stdout + first.stderr)
        emitted = self.repo / out_rel
        payload = json.loads(emitted.read_text(encoding="utf-8"))
        kinds = {(d["path"].endswith("bugfix.md"), d["disposition"])
                 for d in payload["decisions"]}
        self.assertIn((True, ""), kinds)  # FILL entry: statement is not full EARS
        snapshot = emitted.read_bytes()
        second = run_cli(["--check", "--batch", "bugfix",
                          "--emit-resolution-template", out_rel], self.repo)
        self.assertEqual(second.returncode, 0)
        self.assertEqual(emitted.read_bytes(), snapshot)


class StrictHistoricalInventoryTest(RetrofitSandbox):
    """F-4/F-5/B-6: named historical set และ observed strict report เท่านั้น."""

    def setUp(self):
        super().setUp()
        self._previous_repo = os.environ.get("SDD_RETROFIT_REPO")
        os.environ["SDD_RETROFIT_REPO"] = str(self.repo)
        rf._LEDGER_CACHE.clear()

    def tearDown(self):
        rf._LEDGER_CACHE.clear()
        if self._previous_repo is None:
            os.environ.pop("SDD_RETROFIT_REPO", None)
        else:
            os.environ["SDD_RETROFIT_REPO"] = self._previous_repo
        super().tearDown()

    def _write_feature(self, name: str, *, valid: bool = True) -> Path:
        directory = self.feature(name)
        if not valid:
            (directory / "requirements.md").write_text(
                "# malformed historical directory\n", encoding="utf-8"
            )
            return directory
        approved = "> Status: approved 2026-08-27\n"
        (directory / "requirements.md").write_text(
            approved + "## REQ-1: Capability\n- 1.1 WHEN input arrives THE SYSTEM SHALL accept it.\n",
            encoding="utf-8",
        )
        (directory / "design.md").write_text(
            approved
            + "## Build\nBody\n\n"
            + "## Requirement Traceability\n"
            + "| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
            encoding="utf-8",
        )
        (directory / "tasks.md").write_text(
            approved + "- [ ] 1. implement.\n     Satisfies: REQ-1.1\n",
            encoding="utf-8",
        )
        return directory

    def _ledger(self, *residual_features: str) -> None:
        owner = self.features / rf.CURRENT_FEATURE
        owner.mkdir(parents=True, exist_ok=True)
        (owner / "migration-resolutions.json").write_text(
            json.dumps({
                "decisions": [{
                    "path": f".ai/specs/{feature}/design.md",
                    "field": "trace.table",
                    "taskId": "",
                    "disposition": "trace-header-canonical",
                } for feature in residual_features]
            }),
            encoding="utf-8",
        )

    def test_canonical_named_set_is_61_unique_sorted_historical_features(self):
        self.assertEqual(61, len(rf.CANONICAL_HISTORICAL_FEATURES))
        self.assertEqual(61, len(set(rf.CANONICAL_HISTORICAL_FEATURES)))
        self.assertEqual(tuple(sorted(rf.CANONICAL_HISTORICAL_FEATURES)),
                         rf.CANONICAL_HISTORICAL_FEATURES)
        self.assertNotIn(rf.CURRENT_FEATURE, rf.CANONICAL_HISTORICAL_FEATURES)
        self.assertNotIn("bugfix-sdd-operating-layer-parity-review",
                         rf.CANONICAL_HISTORICAL_FEATURES)

    def test_named_membership_ignores_future_feature_and_detects_substitution(self):
        self._write_feature("legacy-a")
        self._write_feature("future-feature")
        with patch.object(rf, "HISTORICAL_FEATURES", ("legacy-a", "legacy-b")):
            membership = rf.historical_membership()
        self.assertEqual(membership.expected, ("legacy-a", "legacy-b"))
        self.assertEqual(membership.present, ("legacy-a",))
        self.assertEqual(membership.missing, ("legacy-b",))
        self.assertIn("future-feature", membership.outside_scope)

        with patch.object(rf, "HISTORICAL_FEATURES", ("legacy-a", "legacy-b")):
            blockers = rf.scope_check()
        self.assertEqual(["MIGRATION_SCOPE_MISMATCH"], [blocker.code for blocker in blockers])
        self.assertIn("missing=legacy-b", blockers[0].message)
        self.assertIn("outsideScope=future-feature", blockers[0].message)

    def test_outside_scope_symlink_directory_file_and_broken_target_block_final_allow(self):
        self._write_feature("legacy-pass")
        self._write_feature(rf.CURRENT_FEATURE)
        external_directory = self.repo / "external-directory"
        external_directory.mkdir()
        external_file = self.repo / "external-file"
        external_file.write_text("not a spec directory\n", encoding="utf-8")
        (self.features / "future-dir-link").symlink_to(external_directory, target_is_directory=True)
        (self.features / "future-file-link").symlink_to(external_file)
        (self.features / "future-broken-link").symlink_to(self.repo / "missing-target")

        with patch.object(rf, "HISTORICAL_FEATURES", ("legacy-pass",)), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            rc = rf.run_check("final-all-spec")
        payload = json.loads(output.getvalue())

        outside = {entry["feature"]: entry for entry in payload["outsideHistoricalScope"]}
        self.assertEqual(1, rc)
        self.assertEqual("policy-fail", payload["verdict"])
        self.assertFalse(payload["strictOk"])
        for feature in ("future-dir-link", "future-file-link", "future-broken-link"):
            self.assertIn(feature, outside)
            self.assertFalse(outside[feature]["strictOk"])

    def test_final_report_uses_observed_outcomes_and_separates_scope_groups(self):
        self._write_feature("legacy-pass")
        self._write_feature("legacy-broken", valid=False)
        self._write_feature(rf.CURRENT_FEATURE)
        self._write_feature("future-feature")
        self._ledger("legacy-broken", rf.CURRENT_FEATURE, "future-feature")
        with patch.object(rf, "HISTORICAL_FEATURES", ("legacy-pass", "legacy-broken")), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            rc = rf.run_check("final-all-spec")
        payload = json.loads(output.getvalue())

        self.assertEqual(1, rc)
        historical = payload["historicalInventory"]
        self.assertEqual(2, historical["expectedCount"])
        self.assertEqual(2, historical["checkedCount"])
        self.assertEqual([], historical["uncheckedFeatures"])
        self.assertFalse(historical["strictOk"])
        results = {entry["feature"]: entry for entry in historical["results"]}
        self.assertTrue(results["legacy-pass"]["checked"])
        self.assertTrue(results["legacy-pass"]["strictOk"])
        self.assertTrue(results["legacy-broken"]["legacyResidual"])
        self.assertTrue(results["legacy-broken"]["checked"])
        self.assertFalse(results["legacy-broken"]["strictOk"])
        self.assertEqual("sdd-operating-layer-parity", payload["currentFeature"]["feature"])
        self.assertTrue(payload["currentFeature"]["checked"])
        self.assertFalse(payload["currentFeature"]["legacyResidual"])
        outside = {entry["feature"]: entry for entry in payload["outsideHistoricalScope"]}
        self.assertIn("future-feature", outside)
        self.assertTrue(outside["future-feature"]["checked"])
        self.assertFalse(outside["future-feature"]["legacyResidual"])
        self.assertEqual("policy-fail", payload["verdict"])

    def test_unchecked_historical_result_blocks_group_and_aggregate(self):
        self._write_feature("legacy-pass")
        self._write_feature("legacy-unreadable")
        self._write_feature(rf.CURRENT_FEATURE)

        def probe(feature: str, _root: Path) -> int:
            if feature == "legacy-unreadable":
                raise OSError("unreadable")
            return 0

        with patch.object(rf, "HISTORICAL_FEATURES", ("legacy-pass", "legacy-unreadable")), \
                patch.object(rf.sc, "trace_run", side_effect=probe), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            rc = rf.run_check("final-all-spec")
        payload = json.loads(output.getvalue())
        historical = payload["historicalInventory"]
        self.assertEqual(2, rc)
        self.assertEqual(1, historical["checkedCount"])
        self.assertEqual(["legacy-unreadable"], historical["uncheckedFeatures"])
        self.assertFalse(historical["strictOk"])
        self.assertFalse(payload["strictOk"])
        self.assertEqual("engine-fail", payload["verdict"])

    def test_invalid_utf8_is_unchecked_engine_failure_in_final_report(self):
        invalid = self._write_feature("legacy-invalid")
        (invalid / "requirements.md").write_bytes(b"\xff\xfe")
        self._write_feature(rf.CURRENT_FEATURE)

        with patch.object(rf, "HISTORICAL_FEATURES", ("legacy-invalid",)), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            rc = rf.run_check("final-all-spec")
        payload = json.loads(output.getvalue())
        result = payload["historicalInventory"]["results"][0]

        self.assertEqual(2, rc)
        self.assertFalse(result["checked"])
        self.assertTrue(result["engineFailure"])
        self.assertFalse(result["strictOk"])
        self.assertEqual("engine-fail", payload["verdict"])

    def test_invalid_utf8_is_engine_failure_in_normal_check_report(self):
        invalid = self._write_feature("legacy-invalid")
        (invalid / "requirements.md").write_bytes(b"\xff\xfe")

        with patch.object(rf, "HISTORICAL_FEATURES", ("legacy-invalid",)), \
                contextlib.redirect_stdout(io.StringIO()) as output:
            rc = rf.run_check("canonical-complete")
        payload = json.loads(output.getvalue())

        self.assertEqual(2, rc)
        self.assertEqual("engine-fail", payload["verdict"])
        self.assertEqual(["legacy-invalid"], payload["engineFailureFeatures"])
        result = payload["strictResults"][0]
        self.assertFalse(result["checked"])
        self.assertTrue(result["engineFailure"])
        self.assertFalse(result["strictOk"])

    def test_observed_engine_tri_state_mutation_is_killed(self):
        invalid = self._write_feature("legacy-invalid")
        (invalid / "requirements.md").write_bytes(b"\xff\xfe")
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        before = "    if code == 2:\n        return {\n"
        self.assertEqual(1, source.count(before))
        mutated = source.replace(before, "    if False:\n        return {\n")
        module_path = self.repo / "tri-state-mutant.py"
        module_path.write_text(mutated, encoding="utf-8")
        module_name = f"spec_retrofit_tri_mutant_{len(sys.modules)}"
        module_spec = importlib.util.spec_from_file_location(module_name, module_path)
        module = importlib.util.module_from_spec(module_spec)
        sys.modules[module_name] = module
        module_spec.loader.exec_module(module)

        result = module._observed_strict_result("legacy-invalid", set())

        self.assertTrue(result["checked"], "sanity: mutation ต้อง flatten engine failure")
        self.assertFalse(result["engineFailure"])
        self.assertEqual(2, result["exitCode"])

    def test_report_truthfulness_mutations_are_killed(self):
        source = (SCRIPTS / "spec-retrofit.py").read_text(encoding="utf-8")
        mutations = (
            (
                "code = sc.all_tree_trace_run(feature, specs_root())",
                "code = 0 if feature in legacy_dirs else sc.all_tree_trace_run(feature, specs_root())",
            ),
            (
                '"strictOk": aggregate_strict_ok,',
                '"strictOk": True,',
            ),
            (
                '"strictOk": aggregate_strict_ok,\n            "verdict": verdict,',
                '"strictOk": aggregate_strict_ok,\n            "verdict": "allow",',
            ),
            (
                "return tuple(specs_root() / feature for feature in HISTORICAL_FEATURES\n                 if feature in membership.present)",
                "return tuple(path for path in sorted(specs_root().iterdir())\n                 if path.is_dir() and path.name not in {CURRENT_FEATURE, ARCHIVE_CONTAINER})",
            ),
            (
                "_observed_strict_result(CURRENT_FEATURE, set())",
                "_observed_strict_result(CURRENT_FEATURE, _legacy_residual_features())",
            ),
            (
                "_observed_strict_result(feature, set())\n            for feature in membership.outside_scope",
                "_observed_strict_result(feature, _legacy_residual_features())\n            for feature in membership.outside_scope",
            ),
        )
        for index, (before, after) in enumerate(mutations):
            with self.subTest(mutation=index):
                self.assertEqual(1, source.count(before))
                mutated = source.replace(before, after)
                path = self.repo / f"mutant-{index}.py"
                path.write_text(mutated, encoding="utf-8")
                name = f"spec_retrofit_truth_mutant_{index}_{len(sys.modules)}"
                module_spec = importlib.util.spec_from_file_location(name, path)
                module = importlib.util.module_from_spec(module_spec)
                sys.modules[name] = module
                module_spec.loader.exec_module(module)
                module._LEDGER_CACHE.clear()
                self._write_feature("legacy-pass")
                self._write_feature("legacy-broken", valid=False)
                self._write_feature(module.CURRENT_FEATURE)
                self._write_feature("future-feature")
                self._ledger("legacy-broken", module.CURRENT_FEATURE, "future-feature")
                module.HISTORICAL_FEATURES = ("legacy-pass", "legacy-broken")
                with contextlib.redirect_stdout(io.StringIO()) as output:
                    rc = module.run_check("final-all-spec")
                payload = json.loads(output.getvalue())
                results = {
                    entry["feature"]: entry
                    for entry in payload["historicalInventory"]["results"]
                }
                truth = (
                    rc == 1
                    and payload["verdict"] == "policy-fail"
                    and payload["strictOk"] is False
                    and payload["historicalInventory"]["strictOk"] is False
                    and results["legacy-broken"]["checked"] is True
                    and results["legacy-broken"]["strictOk"] is False
                    and payload["currentFeature"]["legacyResidual"] is False
                    and all(
                        entry["legacyResidual"] is False
                        for entry in payload["outsideHistoricalScope"]
                    )
                    and set(module.historical_directories())
                    == {module.specs_root() / "legacy-pass", module.specs_root() / "legacy-broken"}
                )
                self.assertFalse(truth, "sanity: mutation ต้องทำ report contract ผิด")


if __name__ == "__main__":
    unittest.main()
