#!/usr/bin/env python3
import re
import contextlib
import importlib.util
import io
import json
import os
import subprocess
import sys
import tempfile
import unicodedata
import unittest
from pathlib import Path
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))

import spec_contract
from spec_trace import run as trace_run, run_compatible_all
from spec_contract import (
    check_phase_gate,
    parse_bugfix_criteria,
    parse_requirement_criteria,
    parse_status,
    parse_task_blocks,
    parse_traceability_table,
    resolve_task_selector,
    trace_run as strict_trace_run,
    ledger_legacy_features,
    validate_task_graph,
)


class SpecContractTest(unittest.TestCase):
    def path(self, name="artifact.md"):
        return Path(".ai/specs/fixture") / name

    def codes(self, diagnostics):
        return {diagnostic.code for diagnostic in diagnostics}

    def write(self, directory, name, content):
        path = directory / name
        path.write_text(content, encoding="utf-8")
        return path

    def feature_files(self, directory, *, requirements, design, tasks, bugfix=None):
        self.write(directory, "requirements.md", requirements)
        self.write(directory, "design.md", design)
        self.write(directory, "tasks.md", tasks)
        if bugfix is not None:
            self.write(directory, "bugfix.md", bugfix)

    def test_status_accepts_only_canonical_line_outside_fences(self):
        cases = {
            "> Status: draft\n": "draft",
            "> Status: approved 2026-08-25\n": "approved",
            "> Status: superseded 2026-08-25 by old_spec\n": "superseded",
            "> Status: unknown\n": "unknown",
        }
        for text, expected in cases.items():
            with self.subTest(text=text):
                status, diagnostics = parse_status(text.encode(), self.path())
                self.assertEqual(expected, status.kind)
                self.assertEqual(set(), self.codes(diagnostics))
        for text, code in {
            "```\n> Status: approved 2026-08-25\n```\n": "STATUS_MISSING",
            "> Status: approved 2026-08-25, amended\n": "STATUS_MALFORMED",
            "> Status: approved 2026-08-25\n> Status: draft\n": "STATUS_CONFLICT",
            "> Status: draft\n> Status: draft\n": "STATUS_MULTIPLE",
            "> Status: approved 2026-08-25\n```\n": "TRACE_FENCE_UNCLOSED",
        }.items():
            with self.subTest(text=text):
                _, diagnostics = parse_status(text.encode(), self.path())
                self.assertIn(code, self.codes(diagnostics))

    def test_phase_gate_requires_exact_upstream_artifacts_and_trace(self):
        with tempfile.TemporaryDirectory() as raw:
            directory = Path(raw)
            approved = "> Status: approved 2026-08-25\n"
            self.feature_files(
                directory,
                requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                design=approved + "## Implementation\ntext\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Implementation |\n",
                tasks=approved + "- [ ] A1. work\n  Satisfies: REQ-1.1\n",
            )
            _, diagnostics = check_phase_gate(directory, "implement", "requirements-first")
            self.assertEqual(set(), self.codes(diagnostics))
            for status in (
                "",
                "> Status: draft\n",
                "> Status: unknown\n",
                "> Status: approved 2026-08-25, amended\n",
                "> Status: approved 2026-08-25\n> Status: draft\n",
            ):
                with self.subTest(status=status):
                    self.write(directory, "requirements.md", status)
                    _, diagnostics = check_phase_gate(directory, "implement", "requirements-first")
                    self.assertIn("PHASE_UPSTREAM_NOT_APPROVED", self.codes(diagnostics))

    def test_strict_check_validates_every_required_artifact_before_trace(self):
        status_variants = {
            "missing": "",
            "draft": "> Status: draft\n",
            "unknown": "> Status: unknown\n",
            "malformed": "> Status: approved 2026-08-25, amended\n",
            "multiple": "> Status: approved 2026-08-25\n> Status: approved 2026-08-25\n",
            "conflict": "> Status: approved 2026-08-25\n> Status: draft\n",
        }
        approved = "> Status: approved 2026-08-25\n"
        with tempfile.TemporaryDirectory() as raw:
            specs = Path(raw)
            feature = specs / "feature"; feature.mkdir()
            self.feature_files(
                feature,
                requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                design=approved + "## Implementation\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Implementation |\n",
                tasks=approved + "- [ ] A. work\n  Satisfies: REQ-1.1\n",
            )
            for artifact in ("requirements.md", "design.md", "tasks.md"):
                for variant, status in status_variants.items():
                    with self.subTest(workflow="feature", artifact=artifact, variant=variant):
                        original = (feature / artifact).read_text(encoding="utf-8")
                        body = original.split("\n", 1)[1]
                        self.write(feature, artifact, status + body)
                        with contextlib.redirect_stdout(io.StringIO()):
                            self.assertEqual(1, strict_trace_run("feature", specs))
                        self.write(feature, artifact, original)

            bugfix = specs / "bugfix"; bugfix.mkdir()
            self.write(bugfix, "bugfix.md", approved + "- F-1 THE SYSTEM SHALL fix\n- B-1 THE SYSTEM SHALL preserve\n")
            self.write(bugfix, "tasks.md", approved + "- [ ] A. work\n  Satisfies: F-1, B-1\n")
            for artifact in ("bugfix.md", "tasks.md"):
                for variant, status in status_variants.items():
                    with self.subTest(workflow="bugfix", artifact=artifact, variant=variant):
                        original = (bugfix / artifact).read_text(encoding="utf-8")
                        body = original.split("\n", 1)[1]
                        self.write(bugfix, artifact, status + body)
                        with contextlib.redirect_stdout(io.StringIO()):
                            self.assertEqual(1, strict_trace_run("bugfix", specs))
                        self.write(bugfix, artifact, original)

    def test_strict_cli_routes_through_status_gate_before_trace(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            specs = root / ".ai" / "specs"
            feature = specs / "feature"; feature.mkdir(parents=True)
            approved = "> Status: approved 2026-08-25\n"
            self.feature_files(
                feature,
                requirements="## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                design=approved + "## Implementation\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Implementation |\n",
                tasks=approved + "- [ ] A. work\n  Satisfies: REQ-1.1\n",
            )
            with patch.object(spec_contract, "__file__", str(root / "scripts" / "spec_contract.py")), contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(1, spec_contract._cli(("check", "--feature", "feature", "--strict")))
            self.assertIn("STATUS_MISSING", output.getvalue())
            self.write(feature, "requirements.md", approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n")
            with patch.object(spec_contract, "__file__", str(root / "scripts" / "spec_contract.py")), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(0, spec_contract._cli(("check", "--feature", "feature", "--strict")))

    def test_blockquote_fences_hide_status_and_keep_source_location(self):
        for marker in ("```", "~~~~"):
            with self.subTest(marker=marker):
                text = f"> {marker}md\n> Status: approved 2026-08-25\n> {marker}\n"
                status, diagnostics = parse_status(text.encode(), self.path())
                self.assertIsNone(status)
                self.assertIn("STATUS_MISSING", self.codes(diagnostics))
                self.assertTrue(all(diagnostic.location.line == 1 for diagnostic in diagnostics))

    def test_shared_fence_scanner_covers_plain_list_and_blockquote_variants(self):
        approved = "> Status: approved 2026-08-25\n"
        cases = (
            ("```md", "> Status: approved 2026-08-25", "```"),
            ("~~~~md", "> Status: approved 2026-08-25", "~~~~"),
            ("  ```md", "> Status: approved 2026-08-25", "  ```"),
            ("- ```md", "> Status: approved 2026-08-25", "- ```"),
            ("> ````md", "> Status: approved 2026-08-25", "> ````"),
            ("> ~~~~md", "> Status: approved 2026-08-25", "> ~~~~"),
        )
        for opener, hidden_status, closer in cases:
            with self.subTest(opener=opener):
                status, diagnostics = parse_status(
                    f"{opener}\n{hidden_status}\n{closer}\n{approved}".encode(), self.path()
                )
                self.assertEqual("approved", status.kind)
                self.assertEqual(set(), self.codes(diagnostics))
        _, diagnostics = parse_status(b"> ````\n> Status: approved 2026-08-25\n> ```\n", self.path())
        self.assertIn("TRACE_FENCE_UNCLOSED", self.codes(diagnostics))

    def test_blockquote_fences_block_full_phase_gate(self):
        approved = "> Status: approved 2026-08-25\n"
        with tempfile.TemporaryDirectory() as raw:
            directory = Path(raw)
            for marker in ("```", "~~~"):
                with self.subTest(marker=marker):
                    hidden = f"> {marker}md\n> Status: approved 2026-08-25\n> {marker}\n"
                    self.feature_files(
                        directory,
                        requirements=hidden + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                        design=hidden + "## Implementation\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Implementation |\n",
                        tasks=hidden + "- [ ] A. work\n  Satisfies: REQ-1.1\n",
                    )
                    _, diagnostics = check_phase_gate(directory, "implement", "requirements-first")
                    self.assertIn("STATUS_MISSING", self.codes(diagnostics))
                    self.assertIn("PHASE_UPSTREAM_NOT_APPROVED", self.codes(diagnostics))

    def test_criterion_classifier_blocks_obvious_near_misses_and_ignores_prose(self):
        feature_variants = ("**1.2**", "*1.2*", "REQ-1.2", "(1.2)", "[1.2]", "1.2.", "1.2:", "1-2", "1 : 2")
        bugfix_variants = ("**F-2**", "[B-2]", "F 2", "B - 2", "F--2", "B:2", "F2")
        for token in feature_variants:
            with self.subTest(kind="feature", token=token):
                _, diagnostics = parse_requirement_criteria(
                    f"## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n- {token} THE SYSTEM SHALL hidden\n".encode(),
                    self.path("requirements.md"),
                )
                self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))
        for token in bugfix_variants:
            with self.subTest(kind="bugfix", token=token):
                _, diagnostics = parse_bugfix_criteria(
                    f"- F-1 THE SYSTEM SHALL fix\n- B-1 THE SYSTEM SHALL preserve\n- {token} THE SYSTEM SHALL hidden\n".encode(),
                    self.path("bugfix.md"),
                )
                self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))
        for parser, text, path in (
            (parse_requirement_criteria, b"## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n- ordinary prose\n", self.path("requirements.md")),
            (parse_bugfix_criteria, b"- F-1 THE SYSTEM SHALL fix\n- ordinary prose\n", self.path("bugfix.md")),
        ):
            with self.subTest(kind="ordinary", parser=parser.__name__):
                _, diagnostics = parser(text, path)
                self.assertNotIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

    def test_design_first_and_bugfix_phase_matrix_fail_closed(self):
        approved = "> Status: approved 2026-08-25\n"
        with tempfile.TemporaryDirectory() as raw:
            directory = Path(raw)
            self.write(directory, "design.md", approved)
            _, diagnostics = check_phase_gate(directory, "requirements", "design-first")
            self.assertEqual(set(), self.codes(diagnostics))
        with tempfile.TemporaryDirectory() as raw:
            directory = Path(raw)
            self.write(directory, "bugfix.md", approved + "## Fix\n- F-1 WHEN x THE SYSTEM SHALL y\n")
            self.write(directory, "tasks.md", approved + "- [ ] B. work\n  Satisfies: F-1\n")
            _, diagnostics = check_phase_gate(directory, "implement", "bugfix")
            self.assertEqual(set(), self.codes(diagnostics))
            self.write(directory, "tasks.md", "> Status: draft\n")
            _, diagnostics = check_phase_gate(directory, "implement", "bugfix")
            self.assertIn("PHASE_UPSTREAM_NOT_APPROVED", self.codes(diagnostics))

    def test_task_ids_preserve_case_bytes_and_file_order(self):
        text = (
            "- [ ] 1. numeric\n"
            "- [ ] A1. upper\n"
            "- [ ] a1. lower\n"
            "- [ ] migration-2. dash\n"
            "- [ ] api_v2. underscore\n"
            "- [ ] 1-3. literal\n"
        )
        tasks, diagnostics = parse_task_blocks(text.encode(), self.path("tasks.md"))
        self.assertEqual(["1", "A1", "a1", "migration-2", "api_v2", "1-3"], [task.task_id for task in tasks])
        self.assertEqual(set(), self.codes(diagnostics))
        selected, diagnostics = resolve_task_selector(tasks, "1-3")
        self.assertEqual(("1-3",), selected)
        self.assertEqual(set(), self.codes(diagnostics))
        range_tasks, diagnostics = parse_task_blocks(
            ("- [ ] 1. first\n- [ ] A1. middle\n- [ ] 3. last\n").encode(), self.path("tasks.md")
        )
        self.assertEqual(set(), self.codes(diagnostics))
        selected, diagnostics = resolve_task_selector(range_tasks, "1-3")
        self.assertEqual(("1", "A1", "3"), selected)
        self.assertEqual(set(), self.codes(diagnostics))

    def test_task_parser_rejects_invalid_duplicate_dependencies_cycles_and_ambiguous_ranges(self):
        cases = {
            "- [ ] A. one\n- [ ] A. two\n": "TASK_ID_DUPLICATE",
            "- [ ] A. one\n  Depends on: Missing\n": "TASK_DEPENDENCY_UNKNOWN",
            "- [ ] A. one\n  Depends on: B\n- [ ] B. two\n  Depends on: A\n": "TASK_DEPENDENCY_CYCLE",
            "- [ ] A. one\n": "TASK_SELECTOR_AMBIGUOUS",
        }
        for text, expected in cases.items():
            with self.subTest(expected=expected):
                tasks, diagnostics = parse_task_blocks(text.encode(), self.path("tasks.md"))
                diagnostics += validate_task_graph(tasks)
                if expected == "TASK_SELECTOR_AMBIGUOUS":
                    _, diagnostics = resolve_task_selector(tasks, "1-3")
                self.assertIn(expected, self.codes(diagnostics))
                self.assertTrue(all(diagnostic.location.line > 0 for diagnostic in diagnostics))

    def test_metadata_is_read_only_before_evidence(self):
        text = (
            "- [ ] A1. work\n"
            "  Satisfies: REQ-1.1\n"
            "  Depends on: B2\n"
            "  Verify: `python3 check.py`\n"
            "  Batch: alpha\n"
            "  Evidence:\n"
            "    - test: `python3 other.py` -> pass\n"
            "    - Satisfies: REQ-9.9\n"
            "    Satisfies: REQ-9.8\n"
            "    - Depends on: hidden\n"
            "    - Verify: hidden\n"
            "    - Batch: hidden\n"
        )
        tasks, diagnostics = parse_task_blocks(text.encode(), self.path("tasks.md"))
        self.assertEqual(set(), self.codes(diagnostics))
        task = tasks[0]
        self.assertEqual(("REQ-1.1",), task.satisfies)
        self.assertEqual(("B2",), task.depends_on)
        self.assertEqual(("`python3 check.py`",), task.verify)
        self.assertEqual(("alpha",), task.batch)

    def test_feature_criteria_require_full_ears_major_match_and_unique_ids(self):
        valid = (
            "## REQ-1: Capability\n"
            "- 1.1 THE SYSTEM SHALL always work\n"
            "- 1.2 WHEN event happens THE SYSTEM SHALL react\n"
            "- 1.3 WHILE active THE SYSTEM SHALL persist\n"
            "- 1.4 WHERE enabled THE SYSTEM SHALL expose\n"
            "- 1.5 IF broken THEN THE SYSTEM SHALL fail\n"
        )
        criteria, diagnostics = parse_requirement_criteria(valid.encode(), self.path("requirements.md"))
        self.assertEqual(5, len(criteria))
        self.assertEqual(set(), self.codes(diagnostics))
        invalid = (
            "## REQ-1: Capability\n"
            "- 2.1 WHEN only\n"
            "- 1.1 IF broken THEN recover\n"
            "- 1.1 THE SYSTEM SHALL duplicate\n"
        )
        _, diagnostics = parse_requirement_criteria(invalid.encode(), self.path("requirements.md"))
        self.assertTrue({"EARS_FORM_INVALID", "EARS_MAJOR_MISMATCH", "EARS_ID_DUPLICATE"} <= self.codes(diagnostics))

    def test_bugfix_lints_f_and_b_without_requirements_and_requires_task_coverage(self):
        bugfix = (
            "## Fix\n"
            "- F-1 WHEN event THE SYSTEM SHALL fix\n"
            "- B-1 WHILE valid THE SYSTEM SHALL preserve\n"
        )
        criteria, diagnostics = parse_bugfix_criteria(bugfix.encode(), self.path("bugfix.md"))
        self.assertEqual(["F-1", "B-1"], [criterion.ref for criterion in criteria])
        self.assertEqual(set(), self.codes(diagnostics))
        with tempfile.TemporaryDirectory() as raw:
            directory = Path(raw)
            self.write(directory, "bugfix.md", "> Status: approved 2026-08-25\n" + bugfix)
            self.write(directory, "tasks.md", "> Status: approved 2026-08-25\n- [ ] A. work\n  Satisfies: F-1\n")
            _, diagnostics = check_phase_gate(directory, "implement", "bugfix")
            self.assertIn("PHASE_TRACE_INVALID", self.codes(diagnostics))

    def test_bugfix_parser_accepts_canonical_ids_and_rejects_legacy_ids(self):
        canonical = (
            "- F-1 WHEN event happens THE SYSTEM SHALL fix\n"
            "- B-1 WHILE valid THE SYSTEM SHALL preserve\n"
        )
        criteria, diagnostics = parse_bugfix_criteria(canonical.encode(), self.path("bugfix.md"))
        self.assertEqual(("F-1", "B-1"), tuple(criterion.ref for criterion in criteria))
        self.assertEqual(set(), self.codes(diagnostics))

        legacy = (
            "- F1 WHEN event happens THE SYSTEM SHALL fix\n"
            "- B1 WHILE valid THE SYSTEM SHALL preserve\n"
        )
        criteria, diagnostics = parse_bugfix_criteria(legacy.encode(), self.path("bugfix.md"))
        self.assertEqual((), criteria)
        self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

    def test_near_miss_criteria_are_diagnostic_and_block_phase_gates(self):
        with tempfile.TemporaryDirectory() as raw:
            directory = Path(raw)
            approved = "> Status: approved 2026-08-25\n"
            self.feature_files(
                directory,
                requirements=approved + "## REQ-1: Capability\n- 1.1. THE SYSTEM SHALL work\n",
                design=approved + "## Implementation\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n",
                tasks=approved + "- [ ] A. work\n",
            )
            criteria, diagnostics = parse_requirement_criteria(
                (directory / "requirements.md").read_bytes(), directory / "requirements.md"
            )
            self.assertEqual((), criteria)
            self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))
            _, diagnostics = check_phase_gate(directory, "implement", "requirements-first")
            self.assertIn("PHASE_TRACE_INVALID", self.codes(diagnostics))
            self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

        with tempfile.TemporaryDirectory() as raw:
            directory = Path(raw)
            self.write(directory, "bugfix.md", approved + "- F1 THE SYSTEM SHALL fix\n")
            self.write(directory, "tasks.md", approved + "- [ ] B. work\n  Satisfies: F1\n")
            _, diagnostics = check_phase_gate(directory, "implement", "bugfix")
            self.assertIn("PHASE_TRACE_INVALID", self.codes(diagnostics))
            self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

    def test_fence_scanner_requires_matching_marker_length_and_fence_only_closer(self):
        status, diagnostics = parse_status(
            b"````md\n``` trailing\n> Status: approved 2026-08-25\n",
            self.path(),
        )
        self.assertIsNone(status)
        self.assertIn("TRACE_FENCE_UNCLOSED", self.codes(diagnostics))
        for parser, payload in (
            (parse_requirement_criteria, b"~~~~\n~~~\n## REQ-1: Hidden\n- 1.1 THE SYSTEM SHALL hide\n"),
            (parse_bugfix_criteria, b"~~~~\n~~~\n- F-1 THE SYSTEM SHALL hide\n"),
        ):
            with self.subTest(parser=parser.__name__):
                criteria, diagnostics = parser(payload, self.path())
                self.assertEqual((), criteria)
                self.assertIn("TRACE_FENCE_UNCLOSED", self.codes(diagnostics))
        rows, _, diagnostics = parse_traceability_table(
            b"````\n```\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Hidden |\n",
            self.path("design.md"),
            {"REQ-1.1"},
        )
        self.assertEqual((), rows)
        self.assertIn("TRACE_FENCE_UNCLOSED", self.codes(diagnostics))

    def test_malformed_fences_block_full_phase_gate(self):
        with tempfile.TemporaryDirectory() as raw:
            directory = Path(raw)
            approved = "> Status: approved 2026-08-25\n"
            self.feature_files(
                directory,
                requirements=(
                    "````md\n``` trailing\n" + approved
                    + "## REQ-1: Hidden\n- 1.1 THE SYSTEM SHALL hide\n"
                ),
                design=approved + "## Implementation\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Implementation |\n",
                tasks=approved + "- [ ] A. work\n  Satisfies: REQ-1.1\n",
            )
            _, diagnostics = check_phase_gate(directory, "implement", "requirements-first")
            self.assertIn("PHASE_UPSTREAM_NOT_APPROVED", self.codes(diagnostics))
            self.assertIn("TRACE_FENCE_UNCLOSED", self.codes(diagnostics))

    def test_task_blocks_and_all_metadata_fields_ignore_fenced_content(self):
        text = (
            "```md\n"
            "- [x] forged. example\n"
            "  Satisfies: REQ-1.1\n"
            "  Depends on: forged\n"
            "  Verify: forged\n"
            "  Batch: forged\n"
            "```\n"
            "- [ ] actual. work\n"
            "  Satisfies: REQ-2.1\n"
            "  Depends on: real\n"
            "  Verify: `python3 check.py`\n"
            "  Batch: real\n"
        )
        tasks, diagnostics = parse_task_blocks(text.encode(), self.path("tasks.md"))
        self.assertEqual(("actual",), tuple(task.task_id for task in tasks))
        self.assertEqual(set(), self.codes(diagnostics))
        task = tasks[0]
        self.assertEqual(("REQ-2.1",), task.satisfies)
        self.assertEqual(("real",), task.depends_on)
        self.assertEqual(("`python3 check.py`",), task.verify)
        self.assertEqual(("real",), task.batch)

        tasks, diagnostics = parse_task_blocks(b"````\n```\n- [ ] hidden. task\n", self.path("tasks.md"))
        self.assertEqual((), tasks)
        self.assertIn("TRACE_FENCE_UNCLOSED", self.codes(diagnostics))

    def test_compatibility_trace_keeps_historical_requirements_corpus_green(self):
        specs = SCRIPTS.parent / ".ai" / "specs"
        features = sorted(path.name for path in specs.iterdir() if path.is_dir() and (path / "requirements.md").is_file())
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            failures = [feature for feature in features if trace_run(feature, specs)]
        self.assertEqual(54, len(features))
        self.assertEqual([], failures)

    def test_compatibility_trace_validates_migrated_task_refs_without_weakening_strict(self):
        with tempfile.TemporaryDirectory(prefix="compat-migrated-") as raw:
            specs = Path(raw) / ".ai" / "specs"
            legacy = specs / "legacy"
            legacy.mkdir(parents=True)
            (legacy / "requirements.md").write_text(
                "## REQ-1: Capability\n- 1.1 WHEN input arrives THE SYSTEM SHALL accept it.\n",
                encoding="utf-8",
            )
            (legacy / "design.md").write_text(
                "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                encoding="utf-8",
            )
            tasks = legacy / "tasks.md"
            tasks.write_text(
                "- [x] 1. migrated task\n"
                "     REQ-1.1. Depends on: none.\n",
                encoding="utf-8",
            )

            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(0, trace_run("legacy", specs))
            with contextlib.redirect_stdout(io.StringIO()) as compatibility_output:
                self.assertEqual(0, run_compatible_all(specs))
            self.assertIn("checked 1 / failures 0", compatibility_output.getvalue())
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(1, strict_trace_run("legacy", specs))

            tasks.write_text("- [x] 1. missing trace\n", encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(1, trace_run("legacy", specs))
                self.assertEqual(1, run_compatible_all(specs))

    def test_compatibility_trace_requires_visible_task_owned_refs(self):
        requirements = (
            "## REQ-1: Capability\n"
            "- 1.1 WHEN input arrives THE SYSTEM SHALL accept it.\n"
        )
        design = (
            "## Build\n\n"
            "## Requirement Traceability\n"
            "| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n"
        )
        cases = {
            "orphan bare ref": "     REQ-1.1\n- [ ] 1. missing trace\n",
            "fenced bare ref": "- [ ] 1. missing trace\n```\n     REQ-1.1\n```\n",
            "commented bare ref": "- [ ] 1. missing trace\n<!--\n     REQ-1.1\n-->\n",
            "fenced satisfies": "- [ ] 1. missing trace\n```\n  Satisfies: REQ-1.1\n```\n",
            "commented task": "<!--\n- [ ] 1. hidden task\n     REQ-1.1\n-->\n- [ ] 2. missing trace\n",
        }
        for label, tasks in cases.items():
            with self.subTest(label=label), tempfile.TemporaryDirectory(
                prefix="compat-hidden-"
            ) as raw:
                specs = Path(raw) / ".ai" / "specs"
                feature = specs / "legacy"
                feature.mkdir(parents=True)
                self.feature_files(
                    feature,
                    requirements=requirements,
                    design=design,
                    tasks=tasks,
                )
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(1, trace_run("legacy", specs))

        with tempfile.TemporaryDirectory(prefix="compat-visible-") as raw:
            specs = Path(raw) / ".ai" / "specs"
            feature = specs / "legacy"
            feature.mkdir(parents=True)
            self.feature_files(
                feature,
                requirements=requirements,
                design=design,
                tasks="- [ ] 1. visible task\n     REQ-1.1\n",
            )
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(0, trace_run("legacy", specs))

    def test_trace_accepts_only_named_req_column_and_exact_unfenced_headings(self):
        requirements = (
            "## REQ-1: Capability\n"
            "- 1.1 THE SYSTEM SHALL one\n"
            "- 1.2 THE SYSTEM SHALL two\n"
        )
        design = (
            "## Alpha\ntext\n"
            "```\n## Forged\n```\n"
            "## Requirement Traceability\n"
            "| Section | REQ | Other |\n| --- | --- | --- |\n"
            "| Alpha | REQ-1.1 | REQ-1.2 |\n"
        )
        rows, sections, diagnostics = parse_traceability_table(
            design.encode(), self.path("design.md"), {"REQ-1.1", "REQ-1.2"}
        )
        self.assertEqual(("REQ-1.1",), rows[0].refs)
        self.assertEqual(["Alpha", "Requirement Traceability"], [section.heading for section in sections])
        self.assertEqual(set(), self.codes(diagnostics))
        broken = design.replace("Alpha | REQ-1.1", "alpha | REQ-1.1")
        _, _, diagnostics = parse_traceability_table(broken.encode(), self.path("design.md"), {"REQ-1.1"})
        self.assertIn("TRACE_SECTION_UNKNOWN", self.codes(diagnostics))

    def test_trace_rejects_bare_dotted_cross_major_and_unclosed_fence(self):
        base = "## Alpha\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| {ref} | Alpha |\n"
        for ref in ("1.1", "REQ-1.1-REQ-2.1"):
            with self.subTest(ref=ref):
                rows, _, diagnostics = parse_traceability_table(
                    base.format(ref=ref).encode(), self.path("design.md"), {"REQ-1.1", "REQ-2.1"}
                )
                self.assertEqual((), rows[0].refs)
                self.assertIn("TRACE_REF_UNKNOWN", self.codes(diagnostics))
        _, _, diagnostics = parse_traceability_table(
            b"```\n## Requirement Traceability\n", self.path("design.md"), set()
        )
        self.assertIn("TRACE_FENCE_UNCLOSED", self.codes(diagnostics))

    def test_required_mutations_are_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")

        def load(mutant_source):
            with tempfile.TemporaryDirectory() as raw:
                path = Path(raw) / "spec_contract.py"
                path.write_text(mutant_source, encoding="utf-8")
                name = f"spec_contract_mutant_{len(sys.modules)}"
                spec = importlib.util.spec_from_file_location(name, path)
                module = importlib.util.module_from_spec(spec)
                sys.modules[name] = module
                spec.loader.exec_module(module)
                return module

        def numeric_only(module):
            tasks, diagnostics = module.parse_task_blocks(
                b"- [ ] A1. alpha\n- [ ] 2. numeric\n", Path("tasks.md")
            )
            self.assertEqual((), diagnostics)
            self.assertEqual(("A1", "2"), tuple(task.task_id for task in tasks))

        def loose_ears(module):
            _, diagnostics = module.parse_requirement_criteria(
                b"## REQ-1: Capability\n- 1.1 WHEN only\n", Path("requirements.md")
            )
            self.assertIn("EARS_FORM_INVALID", self.codes(diagnostics))

        def post_evidence(module):
            tasks, diagnostics = module.parse_task_blocks(
                b"- [ ] A. task\n  Evidence:\n    Satisfies: REQ-1.1\n", Path("tasks.md")
            )
            self.assertEqual((), diagnostics)
            self.assertEqual((), tasks[0].satisfies)

        mutations = (
            (
                'TASK_ID_PATTERN = r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}"',
                'TASK_ID_PATTERN = r"[0-9]+"',
                numeric_only,
            ),
            (
                'normalized = " ".join(statement.split())\n    if re.fullmatch',
                'normalized = " ".join(statement.split())\n    if "WHEN" in normalized:\n        return True\n    if re.fullmatch',
                loose_ears,
            ),
            (
                'if stripped == "Evidence:":\n            break',
                'if stripped == "Never:":\n            break',
                post_evidence,
            ),
        )
        for before, after, assertion in mutations:
            self.assertEqual(1, source.count(before))
            with self.assertRaises(AssertionError):
                assertion(load(source.replace(before, after)))

    def test_blocking_parser_mutations_are_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")

        def load(mutant_source):
            with tempfile.TemporaryDirectory() as raw:
                path = Path(raw) / "spec_contract.py"
                path.write_text(mutant_source, encoding="utf-8")
                name = f"spec_contract_blocking_mutant_{len(sys.modules)}"
                spec = importlib.util.spec_from_file_location(name, path)
                module = importlib.util.module_from_spec(spec)
                sys.modules[name] = module
                spec.loader.exec_module(module)
                return module

        def rejects_legacy_bugfix(module):
            _, diagnostics = module.parse_bugfix_criteria(
                b"- F1 THE SYSTEM SHALL fix\n", Path("bugfix.md")
            )
            self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

        def rejects_short_fence(module):
            status, diagnostics = module.parse_status(
                b"````\n```\n> Status: approved 2026-08-25\n", Path("requirements.md")
            )
            self.assertIsNone(status)
            self.assertIn("TRACE_FENCE_UNCLOSED", self.codes(diagnostics))

        def rejects_near_miss(module):
            _, diagnostics = module.parse_requirement_criteria(
                b"## REQ-1: Capability\n- 1.1. THE SYSTEM SHALL work\n", Path("requirements.md")
            )
            self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))
            self.assertIn(2, {diagnostic.location.line for diagnostic in diagnostics})

        def ignores_fenced_task(module):
            tasks, _ = module.parse_task_blocks(
                b"```\n- [ ] forged. task\n```\n- [ ] actual. task\n", Path("tasks.md")
            )
            self.assertEqual(("actual",), tuple(task.task_id for task in tasks))

        mutations = (
            (
                'BUGFIX_CRITERION_RE = re.compile(r"^- ([FB]-[0-9]+)\\s+(.+?)\\s*$")',
                'BUGFIX_CRITERION_RE = re.compile(r"^- ([FB]-?[0-9]+)\\s+(.+?)\\s*$")',
                rejects_legacy_bugfix,
            ),
            (
                'len(closing.group(1)) >= opening_length',
                'True',
                rejects_short_fence,
            ),
            (
                'if item and _looks_like_malformed_feature_criterion(item.group(1)):\n        return "near-miss", None',
                'if False:\n        return "near-miss", None',
                rejects_near_miss,
            ),
            (
                'for number, line in visible:\n        kind, match = _classify_task_line(line)',
                'for number, line in enumerate(lines, 1):\n        kind, match = _classify_task_line(line)',
                ignores_fenced_task,
            ),
        )
        for before, after, assertion in mutations:
            self.assertEqual(1, source.count(before))
            with self.assertRaises(AssertionError):
                assertion(load(source.replace(before, after)))

    def test_rework_two_mutations_are_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")

        def load(mutant_source):
            with tempfile.TemporaryDirectory() as raw:
                path = Path(raw) / "spec_contract.py"
                path.write_text(mutant_source, encoding="utf-8")
                name = f"spec_contract_rework_two_mutant_{len(sys.modules)}"
                spec = importlib.util.spec_from_file_location(name, path)
                module = importlib.util.module_from_spec(spec)
                sys.modules[name] = module
                spec.loader.exec_module(module)
                return module

        def blocks_unapproved_status(module):
            with tempfile.TemporaryDirectory() as raw:
                specs = Path(raw); feature = specs / "feature"; feature.mkdir()
                self.feature_files(
                    feature,
                    requirements="## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                    design="> Status: approved 2026-08-25\n## Implementation\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Implementation |\n",
                    tasks="> Status: approved 2026-08-25\n- [ ] A. work\n  Satisfies: REQ-1.1\n",
                )
                with contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(1, module.trace_run("feature", specs))

        def hides_blockquote_fence(module):
            status, diagnostics = module.parse_status(
                b"> ```md\n> Status: approved 2026-08-25\n> ```\n", Path("requirements.md")
            )
            self.assertIsNone(status)
            self.assertIn("STATUS_MISSING", self.codes(diagnostics))

        def blocks_feature_near_miss(module):
            _, diagnostics = module.parse_requirement_criteria(
                b"## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n- **1.2** THE SYSTEM SHALL hidden\n",
                Path("requirements.md"),
            )
            self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

        def blocks_bugfix_near_miss(module):
            _, diagnostics = module.parse_bugfix_criteria(
                b"- F-1 THE SYSTEM SHALL fix\n- **B-2** THE SYSTEM SHALL hidden\n", Path("bugfix.md")
            )
            self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

        mutations = (
            (
                "if diagnostics:\n        return _print_contract_diagnostics(feature, diagnostics)",
                "if False:\n        return _print_contract_diagnostics(feature, diagnostics)",
                blocks_unapproved_status,
            ),
            (
                "subject = _fence_subject(line)",
                "subject = line",
                hides_blockquote_fence,
            ),
            (
                'if item and _looks_like_malformed_feature_criterion(item.group(1)):\n        return "near-miss", None',
                'if False:\n        return "near-miss", None',
                blocks_feature_near_miss,
            ),
            (
                'if item and _looks_like_malformed_bugfix_criterion(item.group(1)):\n        return "near-miss", None',
                'if False:\n        return "near-miss", None',
                blocks_bugfix_near_miss,
            ),
        )
        for before, after, assertion in mutations:
            with self.subTest(before=before):
                self.assertEqual(1, source.count(before))
                with self.assertRaises(AssertionError):
                    assertion(load(source.replace(before, after)))

    def test_spec_trace_rejects_cross_requirement_range(self):
        with tempfile.TemporaryDirectory() as raw:
            specs = Path(raw)
            directory = specs / "fixture"
            directory.mkdir()
            self.feature_files(
                directory,
                requirements=(
                    "> Status: approved 2026-08-25\n"
                    "## REQ-1: One\n- 1.1 THE SYSTEM SHALL one\n"
                    "## REQ-2: Two\n- 2.1 THE SYSTEM SHALL two\n"
                ),
                design=(
                    "> Status: approved 2026-08-25\n## Alpha\n"
                    "## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n"
                    "| REQ-1.1-REQ-2.1 | Alpha |\n"
                ),
                tasks=(
                    "> Status: approved 2026-08-25\n- [ ] A. work\n"
                    "  Satisfies: REQ-1.1-REQ-2.1\n"
                ),
            )
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(1, strict_trace_run("fixture", specs))

    def test_spec_trace_cli_checks_all_43_task_one_criteria(self):
        completed = subprocess.run(
            [str(SCRIPTS / "spec-trace.sh"), "sdd-operating-layer-parity"],
            cwd=SCRIPTS.parent,
            text=True,
            capture_output=True,
            check=False,
        )
        self.assertEqual(0, completed.returncode, completed.stderr + completed.stdout)
        self.assertIn("เกณฑ์ 178 ข้อ", completed.stdout)

    def test_required_mutations_are_detected_by_observable_contract(self):
        task_text = "- [ ] A1. work\n- [ ] 2. work\n"
        tasks, diagnostics = parse_task_blocks(task_text.encode(), self.path("tasks.md"))
        self.assertEqual(set(), self.codes(diagnostics))
        self.assertEqual(("A1", "2"), tuple(task.task_id for task in tasks))
        criteria, diagnostics = parse_requirement_criteria(
            b"## REQ-1: Capability\n- 1.1 WHEN only\n", self.path("requirements.md")
        )
        self.assertIn("EARS_FORM_INVALID", self.codes(diagnostics))
        tasks, diagnostics = parse_task_blocks(
            b"- [ ] A. work\n  Evidence:\n    - Satisfies: REQ-1.1\n", self.path("tasks.md")
        )
        self.assertEqual((), tasks[0].satisfies)
        self.assertEqual(set(), self.codes(diagnostics))


    def test_rework_three_criterion_classifier_is_ascii_strict_and_reject_only(self):
        feature_variants = (
            "- req-1.2 THE SYSTEM SHALL hidden",
            "- re​q-1.2 THE SYSTEM SHALL hidden",
            "- `1.2` THE SYSTEM SHALL hidden",
            "* 1.2 THE SYSTEM SHALL hidden",
            "- １.２ THE SYSTEM SHALL hidden",
            "- ١.٢ THE SYSTEM SHALL hidden",
            "- 1​.2 THE SYSTEM SHALL hidden",
            "- 1-2 THE SYSTEM SHALL hidden",
        )
        bugfix_variants = (
            "- f-2 THE SYSTEM SHALL hidden",
            "- `B-2` THE SYSTEM SHALL hidden",
            "* F-2 THE SYSTEM SHALL hidden",
            "- F-２ THE SYSTEM SHALL hidden",
            "- B-٢ THE SYSTEM SHALL hidden",
            "- F​-2 THE SYSTEM SHALL hidden",
            "- B:2 THE SYSTEM SHALL hidden",
        )
        for variant in feature_variants:
            with self.subTest(kind="feature", variant=variant):
                criteria, diagnostics = parse_requirement_criteria(
                    (
                        "## REQ-1: Capability\n"
                        "- 1.1 THE SYSTEM SHALL work\n"
                        f"{variant}\n"
                    ).encode(),
                    self.path("requirements.md"),
                )
                self.assertEqual(("REQ-1.1",), tuple(criterion.ref for criterion in criteria))
                self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))
        for variant in bugfix_variants:
            with self.subTest(kind="bugfix", variant=variant):
                criteria, diagnostics = parse_bugfix_criteria(
                    (
                        "- F-1 THE SYSTEM SHALL fix\n"
                        "- B-1 THE SYSTEM SHALL preserve\n"
                        f"{variant}\n"
                    ).encode(),
                    self.path("bugfix.md"),
                )
                self.assertEqual(("F-1", "B-1"), tuple(criterion.ref for criterion in criteria))
                self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

    def test_rework_three_task_classifier_blocks_invalid_openings_and_metadata_leakage(self):
        openings = (
            "- [X] B. uppercase",
            "- [✓] B. checkmark",
            "- [] B. empty",
            "* [x] B. star",
            "+ [x] B. plus",
            "1. [x] B. ordered",
            "- [x] B . spaced",
            "- [x] B missing-dot",
            "- [x] bad/id. invalid-id",
        )
        for opening in openings:
            with self.subTest(opening=opening):
                tasks, diagnostics = parse_task_blocks(
                    (
                        "- [ ] A. canonical\n"
                        "  Satisfies: REQ-1.1\n"
                        f"{opening}\n"
                        "  Satisfies: REQ-1.2\n"
                    ).encode(),
                    self.path("tasks.md"),
                )
                self.assertEqual(("A",), tuple(task.task_id for task in tasks))
                self.assertEqual(("REQ-1.1",), tasks[0].satisfies)
                self.assertIn("TASK_ID_INVALID", self.codes(diagnostics))

    def test_rework_three_feature_structure_requires_canonical_sections(self):
        no_heading, diagnostics = parse_requirement_criteria(
            b"- 1.1 THE SYSTEM SHALL not count\n", self.path("requirements.md")
        )
        self.assertEqual((), no_heading)
        self.assertIn("EARS_HEADING_MALFORMED", self.codes(diagnostics))
        for heading in (
            "## REQ-1 — Capability",
            "## req-1: Capability",
            "### REQ-1: Capability",
            "  ## REQ-1: Capability",
        ):
            with self.subTest(heading=heading):
                criteria, diagnostics = parse_requirement_criteria(
                    f"{heading}\n- 1.1 THE SYSTEM SHALL hidden\n".encode(),
                    self.path("requirements.md"),
                )
                self.assertEqual((), criteria)
                self.assertIn("EARS_HEADING_MALFORMED", self.codes(diagnostics))
        criteria, diagnostics = parse_requirement_criteria(
            (
                "## REQ-1: Capability\n"
                "- 1.1 THE SYSTEM SHALL work\n"
                "## Notes\n"
                "- 1.2 THE SYSTEM SHALL not inherit REQ-1\n"
            ).encode(),
            self.path("requirements.md"),
        )
        self.assertEqual(("REQ-1.1",), tuple(criterion.ref for criterion in criteria))
        self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))
        _, diagnostics = parse_requirement_criteria(
            b"## REQ-1: Empty\n", self.path("requirements.md")
        )
        self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

    def test_rework_three_public_phase_cli_and_complete_matrix(self):
        approved = "> Status: approved 2026-08-25\n"
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            script = root / "scripts" / "spec_contract.py"
            script.parent.mkdir()
            script.write_text((SCRIPTS / "spec_contract.py").read_text(encoding="utf-8"), encoding="utf-8")
            specs = root / ".ai" / "specs"

            def write_feature(name, *, requirements=True, design=True, tasks=True, bugfix=False):
                directory = specs / name
                directory.mkdir(parents=True)
                if requirements:
                    self.write(directory, "requirements.md", approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n")
                if design:
                    self.write(directory, "design.md", approved + "## Implementation\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Implementation |\n")
                if tasks:
                    self.write(directory, "tasks.md", approved + "- [ ] A. work\n  Satisfies: REQ-1.1\n")
                if bugfix:
                    self.write(directory, "bugfix.md", approved + "- F-1 THE SYSTEM SHALL fix\n")
                return directory

            requirements_first = write_feature("requirements-first")
            design_first_design = write_feature("design-first-design", requirements=False, design=False, tasks=False)
            self.write(design_first_design, "design.md", approved)
            design_first_requirements = write_feature("design-first-requirements", requirements=False, tasks=False)
            design_first_full = write_feature("design-first-full")
            bugfix = specs / "bugfix"; bugfix.mkdir()
            self.write(bugfix, "bugfix.md", approved + "- F-1 THE SYSTEM SHALL fix\n")
            self.write(bugfix, "tasks.md", approved + "- [ ] A. work\n  Satisfies: F-1\n")

            valid_cases = (
                ("requirements-first", "design", "requirements-first"),
                ("requirements-first", "tasks", "requirements-first"),
                ("requirements-first", "implement", "requirements-first"),
                ("design-first-design", "design", "design-first"),
                ("design-first-requirements", "requirements", "design-first"),
                ("design-first-full", "tasks", "design-first"),
                ("design-first-full", "implement", "design-first"),
                ("bugfix", "tasks", "bugfix"),
                ("bugfix", "implement", "bugfix"),
            )
            for feature, phase, workflow in valid_cases:
                with self.subTest(feature=feature, phase=phase, workflow=workflow):
                    result = subprocess.run(
                        [sys.executable, str(script), "gate", "phase", "--feature", feature, "--phase", phase, "--workflow", workflow],
                        cwd=root, text=True, capture_output=True, check=False,
                    )
                    self.assertEqual(0, result.returncode, result.stderr + result.stdout)

            for file_name in ("requirements.md", "tasks.md"):
                directory = write_feature(f"design-first-{file_name}", requirements=False, design=False, tasks=False)
                self.write(directory, "design.md", approved)
                self.write(directory, file_name, approved)
                _, diagnostics = check_phase_gate(directory, "design", "design-first")
                self.assertIn("PHASE_WORKFLOW_UNSUPPORTED", self.codes(diagnostics))
            invalid_cases = (
                ("requirements-first", "requirements", "requirements-first"),
                ("bugfix", "design", "bugfix"),
                ("bugfix", "requirements", "bugfix"),
            )
            for feature, phase, workflow in invalid_cases:
                with self.subTest(feature=feature, phase=phase, workflow=workflow):
                    result = subprocess.run(
                        [sys.executable, str(script), "gate", "phase", "--feature", feature, "--phase", phase, "--workflow", workflow],
                        cwd=root, text=True, capture_output=True, check=False,
                    )
                    self.assertEqual(1, result.returncode, result.stderr + result.stdout)
                    self.assertIn("PHASE_WORKFLOW_UNSUPPORTED", result.stdout)


    def test_rework_three_mutations_are_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")

        def load(mutant_source):
            with tempfile.TemporaryDirectory() as raw:
                path = Path(raw) / "spec_contract.py"
                path.write_text(mutant_source, encoding="utf-8")
                name = f"spec_contract_rework_three_mutant_{len(sys.modules)}"
                spec = importlib.util.spec_from_file_location(name, path)
                module = importlib.util.module_from_spec(spec)
                sys.modules[name] = module
                spec.loader.exec_module(module)
                return module

        def rejects_unicode_feature(module):
            _, diagnostics = module.parse_requirement_criteria(
                "## REQ-1: Capability\n- １.２ THE SYSTEM SHALL hidden\n".encode(), Path("requirements.md")
            )
            self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

        def rejects_feature_wrapper(module):
            _, diagnostics = module.parse_requirement_criteria(
                b"## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n- `1.2` THE SYSTEM SHALL hidden\n",
                Path("requirements.md"),
            )
            self.assertIn(3, {diagnostic.location.line for diagnostic in diagnostics if diagnostic.code == "EARS_CRITERION_MALFORMED"})

        def rejects_bugfix_wrapper(module):
            _, diagnostics = module.parse_bugfix_criteria(
                b"- F-1 THE SYSTEM SHALL work\n- `F-2` THE SYSTEM SHALL hidden\n", Path("bugfix.md")
            )
            self.assertIn(2, {diagnostic.location.line for diagnostic in diagnostics if diagnostic.code == "EARS_CRITERION_MALFORMED"})

        def rejects_malformed_task_boundary(module):
            tasks, diagnostics = module.parse_task_blocks(
                b"- [ ] A. canonical\n  Satisfies: REQ-1.1\n* [x] B. invalid\n  Satisfies: REQ-1.2\n",
                Path("tasks.md"),
            )
            self.assertEqual(("REQ-1.1",), tasks[0].satisfies)
            self.assertIn("TASK_ID_INVALID", self.codes(diagnostics))

        def requires_req_heading(module):
            _, diagnostics = module.parse_requirement_criteria(
                b"- 1.1 THE SYSTEM SHALL hidden\n", Path("requirements.md")
            )
            self.assertIn("EARS_HEADING_MALFORMED", self.codes(diagnostics))

        def resets_at_sibling_heading(module):
            criteria, diagnostics = module.parse_requirement_criteria(
                b"## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n## Notes\n- 1.2 THE SYSTEM SHALL hidden\n",
                Path("requirements.md"),
            )
            self.assertEqual(("REQ-1.1",), tuple(criterion.ref for criterion in criteria))
            self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

        def rejects_design_first_downstream(module):
            with tempfile.TemporaryDirectory() as raw:
                directory = Path(raw)
                self.write(directory, "requirements.md", "> Status: approved 2026-08-25\n")
                _, diagnostics = module.check_phase_gate(directory, "design", "design-first")
                self.assertIn("PHASE_WORKFLOW_UNSUPPORTED", self.codes(diagnostics))

        mutations = (
            (
                'REQ_CRITERION_RE = re.compile(r"^- ([0-9]+)\\.([0-9]+)\\s+(.+?)\\s*$")',
                'REQ_CRITERION_RE = re.compile(r"^- (\\d+)\\.(\\d+)\\s+(.+?)\\s*$")',
                rejects_unicode_feature,
            ),
            (
                'if item and _looks_like_malformed_feature_criterion(item.group(1)):\n        return "near-miss", None',
                'if False:\n        return "near-miss", None',
                rejects_feature_wrapper,
            ),
            (
                'if item and _looks_like_malformed_bugfix_criterion(item.group(1)):\n        return "near-miss", None',
                'if False:\n        return "near-miss", None',
                rejects_bugfix_wrapper,
            ),
            (
                'if _looks_like_task_opening(line):',
                'if False:',
                rejects_malformed_task_boundary,
            ),
            (
                'if not headings:\n        problems.append(_diag("EARS_HEADING_MALFORMED", path, 1, "ไม่พบ canonical REQ heading"))',
                'if not headings:\n        pass',
                requires_req_heading,
            ),
            (
                'major = None\n            if _looks_like_req_heading(line):',
                'major = 1\n            if _looks_like_req_heading(line):',
                resets_at_sibling_heading,
            ),
            (
                'if workflow == "design-first" and phase == "design":',
                'if False:',
                rejects_design_first_downstream,
            ),
        )
        for before, after, assertion in mutations:
            with self.subTest(before=before):
                self.assertEqual(1, source.count(before))
                with self.assertRaises(AssertionError):
                    assertion(load(source.replace(before, after)))


    def test_rework_four_finite_unicode_criterion_sweeps(self):
        feature_base = "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n"
        bugfix_base = "- F-1 THE SYSTEM SHALL fix\n- B-1 THE SYSTEM SHALL preserve\n"
        decimal_characters = [
            chr(codepoint)
            for codepoint in range(sys.maxunicode + 1)
            if unicodedata.category(chr(codepoint)) == "Nd" and not chr(codepoint).isascii()
        ]
        format_characters = [
            chr(codepoint)
            for codepoint in range(sys.maxunicode + 1)
            if unicodedata.category(chr(codepoint)) == "Cf"
        ]
        # Count grows with Python's bundled Unicode database (670 on Python 3.12,
        # higher on newer runtimes). Lower bound still catches a BMP-only sweep.
        self.assertEqual(sys.maxunicode, 0x10FFFF)
        self.assertGreaterEqual(len(decimal_characters), 600)
        self.assertGreaterEqual(len(format_characters), 100)

        for character in decimal_characters:
            with self.subTest(category="Nd", codepoint=f"U+{ord(character):04X}"):
                _, diagnostics = parse_requirement_criteria(
                    (feature_base + f"- {character}.{character} THE SYSTEM SHALL hidden\n").encode(),
                    self.path("requirements.md"),
                )
                self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))
                _, diagnostics = parse_bugfix_criteria(
                    (bugfix_base + f"- F-{character} THE SYSTEM SHALL hidden\n").encode(),
                    self.path("bugfix.md"),
                )
                self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

        for character in format_characters:
            with self.subTest(category="Cf", codepoint=f"U+{ord(character):04X}"):
                _, diagnostics = parse_requirement_criteria(
                    (feature_base + f"- 1{character}.2 THE SYSTEM SHALL hidden\n").encode(),
                    self.path("requirements.md"),
                )
                self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))
                _, diagnostics = parse_bugfix_criteria(
                    (bugfix_base + f"- F{character}-2 THE SYSTEM SHALL hidden\n").encode(),
                    self.path("bugfix.md"),
                )
                self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

        nfkc_confusables = {
            target: [
                chr(codepoint)
                for codepoint in range(sys.maxunicode + 1)
                if not chr(codepoint).isascii()
                and unicodedata.normalize("NFKC", chr(codepoint)) == target
            ]
            for target in "REQFB.-"
        }
        for target, characters in nfkc_confusables.items():
            self.assertTrue(characters, target)
            for character in characters:
                with self.subTest(category="NFKC", target=target, codepoint=f"U+{ord(character):04X}"):
                    if target in "REQ":
                        token = f"{character}EQ-1.2" if target == "R" else (
                            f"R{character}Q-1.2" if target == "E" else f"RE{character}-1.2"
                        )
                        parser, text, path = parse_requirement_criteria, feature_base + f"- {token} THE SYSTEM SHALL hidden\n", self.path("requirements.md")
                    elif target == ".":
                        parser, text, path = parse_requirement_criteria, feature_base + f"- 1{character}2 THE SYSTEM SHALL hidden\n", self.path("requirements.md")
                    elif target in "FB":
                        parser, text, path = parse_bugfix_criteria, bugfix_base + f"- {character}-2 THE SYSTEM SHALL hidden\n", self.path("bugfix.md")
                    else:
                        parser, text, path = parse_bugfix_criteria, bugfix_base + f"- F{character}2 THE SYSTEM SHALL hidden\n", self.path("bugfix.md")
                    _, diagnostics = parser(text.encode(), path)
                    self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

    def test_rework_four_wrapper_bullet_task_heading_and_public_cli_sweeps(self):
        feature_base = "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n"
        bugfix_base = "- F-1 THE SYSTEM SHALL fix\n- B-1 THE SYSTEM SHALL preserve\n"
        for bullet in ("-", "*", "+", "1.", "1)"):
            for wrapper in ("**{}**", "*{}*", "_{}_", "`{}`", "~~{}~~", "[{}]", "({})"):
                with self.subTest(kind="feature", bullet=bullet, wrapper=wrapper):
                    _, diagnostics = parse_requirement_criteria(
                        (feature_base + f"{bullet} {wrapper.format('1.2')} THE SYSTEM SHALL hidden\n").encode(),
                        self.path("requirements.md"),
                    )
                    self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))
                with self.subTest(kind="bugfix", bullet=bullet, wrapper=wrapper):
                    _, diagnostics = parse_bugfix_criteria(
                        (bugfix_base + f"{bullet} {wrapper.format('F-2')} THE SYSTEM SHALL hidden\n").encode(),
                        self.path("bugfix.md"),
                    )
                    self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

        openings = (
            "-[x] B. no-bullet-space",
            "- [x]B. no-checkbox-space",
            "- ［x］ B. fullwidth-brackets",
            "- [x B. missing-closing-bracket",
            "- x] B. missing-opening-bracket",
            "- [] B. empty-checkbox",
            "- [X] B. uppercase-checkbox",
            "- [✓] B. checkmark-checkbox",
            "- [x] B missing-delimiter",
            "- [x] B . spaced-delimiter",
            "* [x] B. alternate-bullet",
            "1) [x] B. ordered-bullet",
            "- [x] bad/id. invalid-id",
        )
        for opening in openings:
            with self.subTest(opening=opening):
                tasks, diagnostics = parse_task_blocks(
                    (
                        "- [ ] A. canonical\n"
                        "  Satisfies: REQ-1.1\n"
                        f"{opening}\n"
                        "  Satisfies: REQ-1.2\n"
                    ).encode(),
                    self.path("tasks.md"),
                )
                self.assertEqual(("A",), tuple(task.task_id for task in tasks))
                self.assertEqual(("REQ-1.1",), tasks[0].satisfies)
                self.assertIn("TASK_ID_INVALID", self.codes(diagnostics))

        malformed_headings = (
            "## REQ-1:",
            "## REQ-1: ",
            "## REQ-1:Capability",
            "## REQ-1:: Capability",
            "## REQ-1:  Capability",
            "## REQ-1:\tCapability",
            "## REQ-1 : Capability",
            "## req-1: Capability",
        )
        for heading in malformed_headings:
            with self.subTest(heading=heading):
                criteria, diagnostics = parse_requirement_criteria(
                    f"{heading}\n- 1.1 THE SYSTEM SHALL hidden\n".encode(), self.path("requirements.md")
                )
                self.assertEqual((), criteria)
                self.assertIn("EARS_HEADING_MALFORMED", self.codes(diagnostics))

        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            script = root / "scripts" / "spec_contract.py"
            script.parent.mkdir()
            script.write_text((SCRIPTS / "spec_contract.py").read_text(encoding="utf-8"), encoding="utf-8")
            specs = root / ".ai" / "specs"
            approved = "> Status: approved 2026-08-25\n"
            feature = specs / "feature"
            feature.mkdir(parents=True)
            self.feature_files(
                feature,
                requirements=approved + feature_base + "- ~~1.2~~ THE SYSTEM SHALL hidden\n",
                design=approved + "## Implementation\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Implementation |\n",
                tasks=approved + "- [ ] A. work\n  Satisfies: REQ-1.1\n",
            )
            bugfix = specs / "bugfix"
            bugfix.mkdir()
            self.write(bugfix, "bugfix.md", approved + bugfix_base + "- Ｆ-2 THE SYSTEM SHALL hidden\n")
            self.write(bugfix, "tasks.md", approved + "- [ ] A. work\n  Satisfies: F-1, B-1\n")
            for name in ("feature", "bugfix"):
                with self.subTest(public_cli=name):
                    completed = subprocess.run(
                        [sys.executable, str(script), "check", "--feature", name, "--strict"],
                        cwd=root, text=True, capture_output=True, check=False,
                    )
                    self.assertEqual(1, completed.returncode, completed.stderr + completed.stdout)
                    self.assertIn("EARS_CRITERION_MALFORMED", completed.stdout)

    def test_rework_four_property_mutations_are_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")

        def load(mutant_source):
            with tempfile.TemporaryDirectory() as raw:
                path = Path(raw) / "spec_contract.py"
                path.write_text(mutant_source, encoding="utf-8")
                name = f"spec_contract_rework_four_mutant_{len(sys.modules)}"
                spec = importlib.util.spec_from_file_location(name, path)
                module = importlib.util.module_from_spec(spec)
                sys.modules[name] = module
                spec.loader.exec_module(module)
                return module

        def rejects_unicode_decimal(module):
            _, diagnostics = module.parse_requirement_criteria(
                "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n- ١.٢ THE SYSTEM SHALL hidden\n".encode(), Path("requirements.md")
            )
            self.assertIn(3, {diagnostic.location.line for diagnostic in diagnostics if diagnostic.code == "EARS_CRITERION_MALFORMED"})

        def rejects_strikethrough_wrapper(module):
            _, diagnostics = module.parse_requirement_criteria(
                b"## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n- ~~1.2~~ THE SYSTEM SHALL hidden\n", Path("requirements.md")
            )
            self.assertIn(3, {diagnostic.location.line for diagnostic in diagnostics if diagnostic.code == "EARS_CRITERION_MALFORMED"})

        def blocks_fullwidth_task_boundary(module):
            tasks, diagnostics = module.parse_task_blocks(
                "- [ ] A. canonical\n  Satisfies: REQ-1.1\n- ［x］ B. invalid\n  Satisfies: REQ-1.2\n".encode(), Path("tasks.md")
            )
            self.assertEqual(("REQ-1.1",), tasks[0].satisfies)
            self.assertIn("TASK_ID_INVALID", self.codes(diagnostics))

        def requires_exact_heading_suffix(module):
            criteria, diagnostics = module.parse_requirement_criteria(
                b"## REQ-1:Capability\n- 1.1 THE SYSTEM SHALL hidden\n", Path("requirements.md")
            )
            self.assertEqual((), criteria)
            self.assertIn("EARS_HEADING_MALFORMED", self.codes(diagnostics))

        mutations = (
            (
                'if item and _looks_like_malformed_feature_criterion(item.group(1)):',
                'if False:',
                rejects_unicode_decimal,
            ),
            (
                'if token.startswith("~~"):',
                'while False:',
                rejects_strikethrough_wrapper,
            ),
            (
                'if _looks_like_task_opening(line):',
                'if False:',
                blocks_fullwidth_task_boundary,
            ),
            (
                'REQ_HEADING_RE = re.compile(r"^## REQ-([0-9]+): (?=\\S)(?:.*\\S)?\\s*$")',
                'REQ_HEADING_RE = re.compile(r"^## REQ-([0-9]+):")',
                requires_exact_heading_suffix,
            ),
        )
        for before, after, assertion in mutations:
            with self.subTest(before=before):
                self.assertEqual(1, source.count(before))
                with self.assertRaises(AssertionError):
                    assertion(load(source.replace(before, after)))


    def test_rework_five_composition_fixed_point_property_and_public_cli(self):
        approved = "> Status: approved 2026-08-25\n"
        feature_base = "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n"
        bugfix_base = "- F-1 THE SYSTEM SHALL fix\n- B-1 THE SYSTEM SHALL preserve\n"
        criteria_cases = (
            ("feature-nested", parse_requirement_criteria, self.path("requirements.md"), feature_base + "- _~~1.2~~_ THE SYSTEM SHALL hidden\n"),
            ("bugfix-nested", parse_bugfix_criteria, self.path("bugfix.md"), bugfix_base + "- _~~F-2~~_ THE SYSTEM SHALL hidden\n"),
            ("feature-cf", parse_requirement_criteria, self.path("requirements.md"), feature_base + "- ​~~1.2~~ THE SYSTEM SHALL hidden\n"),
            ("bugfix-cf", parse_bugfix_criteria, self.path("bugfix.md"), bugfix_base + "- ​~~F-2~~ THE SYSTEM SHALL hidden\n"),
            ("feature-nfkc", parse_requirement_criteria, self.path("requirements.md"), feature_base + "- ＿1.2＿ THE SYSTEM SHALL hidden\n"),
            ("bugfix-nfkc", parse_bugfix_criteria, self.path("bugfix.md"), bugfix_base + "- ＿F-2＿ THE SYSTEM SHALL hidden\n"),
        )
        for name, parser, path, payload in criteria_cases:
            with self.subTest(composition=name):
                _, diagnostics = parser(payload.encode(), path)
                self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

        for prefix in ("​", "＿"):
            with self.subTest(task_prefix=repr(prefix)):
                tasks, diagnostics = parse_task_blocks(
                    (
                        "- [ ] A. canonical\n"
                        "  Satisfies: REQ-1.1\n"
                        f"- {prefix}[x] B. invalid\n"
                        "  Satisfies: REQ-1.2\n"
                    ).encode(),
                    self.path("tasks.md"),
                )
                self.assertEqual(("A",), tuple(task.task_id for task in tasks))
                self.assertEqual(("REQ-1.1",), tasks[0].satisfies)
                self.assertIn("TASK_ID_INVALID", self.codes(diagnostics))

        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            script = root / "scripts" / "spec_contract.py"
            script.parent.mkdir()
            script.write_text((SCRIPTS / "spec_contract.py").read_text(encoding="utf-8"), encoding="utf-8")
            specs = root / ".ai" / "specs"

            def write_feature(name, criterion):
                directory = specs / name
                directory.mkdir(parents=True)
                self.feature_files(
                    directory,
                    requirements=approved + feature_base + criterion,
                    design=approved + "## Implementation\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Implementation |\n",
                    tasks=approved + "- [ ] A. work\n  Satisfies: REQ-1.1\n",
                )

            def write_bugfix(name, criterion):
                directory = specs / name
                directory.mkdir(parents=True)
                self.write(directory, "bugfix.md", approved + bugfix_base + criterion)
                self.write(directory, "tasks.md", approved + "- [ ] A. work\n  Satisfies: F-1, B-1\n")

            write_feature("feature-nested", "- _~~1.2~~_ THE SYSTEM SHALL hidden\n")
            write_bugfix("bugfix-nested", "- _~~F-2~~_ THE SYSTEM SHALL hidden\n")
            write_feature("feature-cf", "- ​~~1.2~~ THE SYSTEM SHALL hidden\n")
            write_bugfix("bugfix-cf", "- ​~~F-2~~ THE SYSTEM SHALL hidden\n")
            write_feature("feature-nfkc", "- ＿1.2＿ THE SYSTEM SHALL hidden\n")
            write_bugfix("bugfix-nfkc", "- ＿F-2＿ THE SYSTEM SHALL hidden\n")
            for name, prefix in (("task-cf", "​"), ("task-nfkc", "＿")):
                directory = specs / name
                directory.mkdir(parents=True)
                self.feature_files(
                    directory,
                    requirements=approved + feature_base,
                    design=approved + "## Implementation\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Implementation |\n",
                    tasks=(
                        approved
                        + "- [ ] A. canonical\n  Satisfies: REQ-1.1\n"
                        + f"- {prefix}[x] B. invalid\n  Satisfies: REQ-1.2\n"
                    ),
                )
            for name in ("feature-nested", "bugfix-nested", "feature-cf", "bugfix-cf", "feature-nfkc", "bugfix-nfkc", "task-cf", "task-nfkc"):
                with self.subTest(public_cli=name):
                    completed = subprocess.run(
                        [sys.executable, str(script), "check", "--feature", name, "--strict"],
                        cwd=root, text=True, capture_output=True, check=False,
                    )
                    self.assertEqual(1, completed.returncode, completed.stderr + completed.stdout)
                    self.assertRegex(completed.stdout, r"EARS_CRITERION_MALFORMED|TASK_ID_INVALID")

    def test_rework_five_fixed_point_and_task_seam_mutations_are_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")

        def load(mutant_source):
            with tempfile.TemporaryDirectory() as raw:
                path = Path(raw) / "spec_contract.py"
                path.write_text(mutant_source, encoding="utf-8")
                name = f"spec_contract_rework_five_mutant_{len(sys.modules)}"
                spec = importlib.util.spec_from_file_location(name, path)
                module = importlib.util.module_from_spec(spec)
                sys.modules[name] = module
                spec.loader.exec_module(module)
                return module

        def requires_fixed_point(module):
            _, diagnostics = module.parse_requirement_criteria(
                b"## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n- _~~1.2~~_ THE SYSTEM SHALL hidden\n",
                Path("requirements.md"),
            )
            self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

        def shares_task_detection_seam(module):
            tasks, diagnostics = module.parse_task_blocks(
                "- [ ] A. canonical\n  Satisfies: REQ-1.1\n- ＿[x] B. invalid\n  Satisfies: REQ-1.2\n".encode(),
                Path("tasks.md"),
            )
            self.assertEqual(("REQ-1.1",), tasks[0].satisfies)
            self.assertIn("TASK_ID_INVALID", self.codes(diagnostics))

        mutations = (
            (
                'next_token = _strip_wrapper(normalized, stop_before_checkbox=task_prefix)',
                'next_token = normalized',
                requires_fixed_point,
            ),
            (
                "normalized, limit_hit = _detection_fixed_point(suffix, task_prefix=True)",
                "normalized, limit_hit = _detection_token(suffix), False",
                shares_task_detection_seam,
            ),
        )
        for before, after, assertion in mutations:
            with self.subTest(before=before):
                self.assertEqual(1, source.count(before))
                with self.assertRaises(AssertionError):
                    assertion(load(source.replace(before, after)))

    def test_rework_six_fixed_point_limit_fails_closed_at_boundary(self):
        feature_base = "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n"
        bugfix_base = "- F-1 THE SYSTEM SHALL fix\n- B-1 THE SYSTEM SHALL preserve\n"
        for depth in (15, 16, 17, 32):
            with self.subTest(kind="feature", depth=depth):
                wrapped = "_~~" * depth + "1.2" + "~~_" * depth
                _, diagnostics = parse_requirement_criteria(
                    (feature_base + f"- {wrapped} THE SYSTEM SHALL hidden\n").encode(),
                    self.path("requirements.md"),
                )
                self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))
            with self.subTest(kind="bugfix", depth=depth):
                wrapped = "_~~" * depth + "F-2" + "~~_" * depth
                _, diagnostics = parse_bugfix_criteria(
                    (bugfix_base + f"- {wrapped} THE SYSTEM SHALL hidden\n").encode(),
                    self.path("bugfix.md"),
                )
                self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))

        for depth in (32, 64):
            with self.subTest(kind="task", depth=depth):
                tasks, diagnostics = parse_task_blocks(
                    (
                        "- [ ] A. canonical\n"
                        "  Satisfies: REQ-1.1\n"
                        + f"- {'_' * depth}[x] B. invalid\n"
                        + "  Satisfies: REQ-1.2\n"
                    ).encode(),
                    self.path("tasks.md"),
                )
                self.assertEqual(("A",), tuple(task.task_id for task in tasks))
                self.assertEqual(("REQ-1.1",), tasks[0].satisfies)
                self.assertIn("TASK_ID_INVALID", self.codes(diagnostics))

        approved = "> Status: approved 2026-08-25\n"
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            script = root / "scripts" / "spec_contract.py"
            script.parent.mkdir()
            script.write_text((SCRIPTS / "spec_contract.py").read_text(encoding="utf-8"), encoding="utf-8")
            feature = root / ".ai" / "specs" / "fixture"
            feature.mkdir(parents=True)
            wrapped = "_~~" * 16 + "1.2" + "~~_" * 16
            self.feature_files(
                feature,
                requirements=approved + feature_base + f"- {wrapped} THE SYSTEM SHALL hidden\n",
                design=approved + "## Implementation\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Implementation |\n",
                tasks=approved + "- [ ] A. work\n  Satisfies: REQ-1.1\n",
            )
            completed = subprocess.run(
                [sys.executable, str(script), "check", "--feature", "fixture", "--strict"],
                cwd=root, text=True, capture_output=True, check=False,
            )
            self.assertEqual(1, completed.returncode, completed.stderr + completed.stdout)
            self.assertIn("EARS_CRITERION_MALFORMED", completed.stdout)

    def test_rework_six_limit_hit_mutation_is_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")
        self.assertNotIn("_DETECTION_MAX_ITERATIONS", source)
        before = "next_token = _strip_wrapper(normalized, stop_before_checkbox=task_prefix)"
        after = "next_token = token"
        self.assertEqual(1, source.count(before))
        with tempfile.TemporaryDirectory() as raw:
            path = Path(raw) / "spec_contract.py"
            path.write_text(source.replace(before, after), encoding="utf-8")
            name = f"spec_contract_rework_six_mutant_{len(sys.modules)}"
            spec = importlib.util.spec_from_file_location(name, path)
            module = importlib.util.module_from_spec(spec)
            sys.modules[name] = module
            spec.loader.exec_module(module)
            wrapped = "_~~" * 16 + "1.2" + "~~_" * 16
            with self.assertRaises(AssertionError):
                _, diagnostics = module.parse_requirement_criteria(
                    ("## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n- " + wrapped + " THE SYSTEM SHALL hidden\n").encode(),
                    Path("requirements.md"),
                )
                self.assertIn("EARS_CRITERION_MALFORMED", self.codes(diagnostics))


    def test_rework_seven_task_wrapper_normalization_is_shared_and_preserves_checkbox_marker(self):
        feature_base = "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n"
        bugfix_base = "- F-1 THE SYSTEM SHALL fix\n- B-1 THE SYSTEM SHALL preserve\n"
        depths = (0, 1, 15, 16, 17, 32, 64)
        for depth in depths:
            with self.subTest(kind="direct", depth=depth):
                wrapped = "_~~" * depth + "[x] bad/id. invalid" + "~~_" * depth
                token, limit_hit = spec_contract._detection_fixed_point(
                    wrapped, task_prefix=True
                )
                self.assertFalse(limit_hit)
                self.assertTrue(token.startswith("[x]"))
                self.assertEqual("malformed", spec_contract._classify_task_line(f"- {wrapped}")[0])
                tasks, diagnostics = parse_task_blocks(
                    (
                        "- [ ] A. canonical\n"
                        "  Satisfies: REQ-1.1\n"
                        f"- {wrapped}\n"
                        "  Satisfies: REQ-1.2\n"
                    ).encode(),
                    self.path("tasks.md"),
                )
                self.assertEqual(("A",), tuple(task.task_id for task in tasks))
                self.assertEqual(("REQ-1.1",), tasks[0].satisfies)
                self.assertIn("TASK_ID_INVALID", self.codes(diagnostics))
                tasks, diagnostics = parse_task_blocks(
                    ("- [ ] A. canonical\n" f"- {wrapped}\n").encode(),
                    self.path("tasks.md"),
                )
                self.assertEqual(("A",), tuple(task.task_id for task in tasks))
                self.assertIn("TASK_ID_INVALID", self.codes(diagnostics))
        for depth in (15, 16, 17, 32):
            for parser, payload, path in (
                (
                    parse_requirement_criteria,
                    feature_base + f"- {'_~~' * depth}1.2{'~~_' * depth} THE SYSTEM SHALL hidden\n",
                    self.path("requirements.md"),
                ),
                (
                    parse_bugfix_criteria,
                    bugfix_base + f"- {'_~~' * depth}F-2{'~~_' * depth} THE SYSTEM SHALL hidden\n",
                    self.path("bugfix.md"),
                ),
            ):
                _, criterion_diagnostics = parser(payload.encode(), path)
                self.assertIn("EARS_CRITERION_MALFORMED", self.codes(criterion_diagnostics))

        tasks, diagnostics = parse_task_blocks(
            b"- [x] valid. canonical\n", self.path("tasks.md")
        )
        self.assertEqual(("valid",), tuple(task.task_id for task in tasks))
        self.assertEqual(set(), self.codes(diagnostics))
        for prefix in ("*", "_", "`", "(", "~~", "_~~", "​", "＿"):
            with self.subTest(kind="wrapper", prefix=repr(prefix)):
                kind, _ = spec_contract._classify_task_line(f"- {prefix}[x] bad/id. invalid")
                self.assertEqual("malformed", kind)
        for depth in (1, 16, 32, 64):
            with self.subTest(kind="ordinary", depth=depth):
                ordinary = "_~~" * depth + "ordinary prose" + "~~_" * depth
                self.assertEqual("ordinary", spec_contract._classify_task_line(f"- {ordinary}")[0])

        approved = "> Status: approved 2026-08-25\n"
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            script = root / "scripts" / "spec_contract.py"
            script.parent.mkdir()
            script.write_text((SCRIPTS / "spec_contract.py").read_text(encoding="utf-8"), encoding="utf-8")
            specs = root / ".ai" / "specs"
            for depth in (1, 16, 32, 64):
                wrapped = "_~~" * depth + "[x] bad/id. invalid" + "~~_" * depth
                feature = specs / f"feature-{depth}"
                feature.mkdir(parents=True)
                self.feature_files(
                    feature,
                    requirements=approved + feature_base + "- 1.2 THE SYSTEM SHALL stay safe\n",
                    design=approved + "## Implementation\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1, REQ-1.2 | Implementation |\n",
                    tasks=approved + "- [ ] A. canonical\n  Satisfies: REQ-1.1\n" + f"- {wrapped}\n  Satisfies: REQ-1.2\n",
                )
                bugfix = specs / f"bugfix-{depth}"
                bugfix.mkdir()
                self.write(bugfix, "bugfix.md", approved + bugfix_base)
                self.write(bugfix, "tasks.md", approved + "- [ ] A. canonical\n  Satisfies: F-1\n" + f"- {wrapped}\n  Satisfies: B-1\n")
                for name in (feature.name, bugfix.name):
                    completed = subprocess.run(
                        [sys.executable, str(script), "check", "--feature", name, "--strict"],
                        cwd=root,
                        text=True,
                        capture_output=True,
                        check=False,
                    )
                    self.assertEqual(1, completed.returncode, completed.stderr + completed.stdout)
                    self.assertIn("TASK_ID_INVALID", completed.stdout)

    def test_rework_seven_finite_task_wrapper_sweep_and_mutations_are_killed(self):
        for depth in range(129):
            wrapped = "_~~" * depth + "[x] bad/id. invalid" + "~~_" * depth
            self.assertEqual("malformed", spec_contract._classify_task_line(f"- {wrapped}")[0])

        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")

        def load(mutant_source):
            with tempfile.TemporaryDirectory() as raw:
                path = Path(raw) / "spec_contract.py"
                path.write_text(mutant_source, encoding="utf-8")
                name = f"spec_contract_rework_seven_mutant_{len(sys.modules)}"
                module_spec = importlib.util.spec_from_file_location(name, path)
                module = importlib.util.module_from_spec(module_spec)
                sys.modules[name] = module
                module_spec.loader.exec_module(module)
                return module

        def blocks_malformed_boundary(module):
            tasks, diagnostics = module.parse_task_blocks(
                b"- [ ] A. canonical\n  Satisfies: REQ-1.1\n- _~~[x] bad/id. invalid~~_\n  Satisfies: REQ-1.2\n",
                Path("tasks.md"),
            )
            self.assertEqual(("REQ-1.1",), tasks[0].satisfies)
            self.assertIn("TASK_ID_INVALID", self.codes(diagnostics))

        def preserves_checkbox_marker(module):
            token, limit_hit = module._detection_fixed_point(
                "_~~[x] bad/id. invalid~~_", task_prefix=True
            )
            self.assertFalse(limit_hit)
            self.assertTrue(token.startswith("[x]"))

        mutations = (
            (
                "next_token = _strip_wrapper(normalized, stop_before_checkbox=task_prefix)",
                'next_token = normalized.lstrip(" \\t_")',
                blocks_malformed_boundary,
            ),
            (
                'if token.startswith("~~"):',
                "if False:",
                blocks_malformed_boundary,
            ),
            (
                'if stop_before_checkbox and token.startswith("["):',
                "if False:",
                preserves_checkbox_marker,
            ),
            (
                "boundaries.append((number - 1, kind, None))",
                "pass",
                blocks_malformed_boundary,
            ),
        )
        for before, after, assertion in mutations:
            with self.subTest(before=before):
                self.assertEqual(1, source.count(before))
                with self.assertRaises(AssertionError):
                    assertion(load(source.replace(before, after)))


    def test_task_two_slice_golden_feature_bugfix_missing_and_unknown(self):
        approved = "> Status: approved 2026-08-25\n"
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            feature = root / "feature"; feature.mkdir()
            self.feature_files(
                feature,
                requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                design=approved + "## Build\nBody\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                tasks=approved + "- [ ] A1. exact task\n  Satisfies: REQ-1.1\n",
            )
            text, diagnostics = spec_contract.build_spec_slice(feature, "A1")
            self.assertEqual((), diagnostics)
            self.assertEqual(
                "requirements.md: approved\ndesign.md: approved\ntasks.md: approved\n\n"
                "- [ ] A1. exact task\n  Satisfies: REQ-1.1\n\n"
                "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n\n"
                "## Build\nBody\n\n",
                text,
            )
            self.write(feature, "design.md", approved + "## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Missing |\n")
            text, diagnostics = spec_contract.build_spec_slice(feature, "A1")
            self.assertIn("MISSING: TRACE_SECTION_UNKNOWN:", text)
            self.assertIn("SLICE_MAPPING_MISSING", self.codes(diagnostics))
            _, diagnostics = spec_contract.build_spec_slice(feature, "unknown")
            self.assertIn("SLICE_TASK_UNKNOWN", self.codes(diagnostics))
            self.assertIn("A1", diagnostics[0].message)

            bugfix = root / "bugfix"; bugfix.mkdir()
            self.write(bugfix, "bugfix.md", approved + "- F-1 THE SYSTEM SHALL fix\n- B-1 THE SYSTEM SHALL preserve\n")
            self.write(bugfix, "tasks.md", approved + "- [ ] B1. exact bugfix task\n  Satisfies: F-1, B-1\n")
            text, diagnostics = spec_contract.build_spec_slice(bugfix, "B1")
            self.assertEqual((), diagnostics)
            self.assertIn("bugfix.md: approved", text)
            self.assertIn("- F-1 THE SYSTEM SHALL fix", text)
            self.assertIn("- B-1 THE SYSTEM SHALL preserve", text)
            self.assertNotIn("Requirement Traceability", text)

    def test_task_two_state_precedence_archive_and_authoring_chain(self):
        approved = "> Status: approved 2026-08-25\n"
        evidence = "  Evidence:\n    - test: `python3 test.py` -> OK\n    - viewports: n/a — tooling only\n    - deviations: none\n"
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw) / ".ai" / "specs"; root.mkdir(parents=True)
            active = root / "active"; active.mkdir()
            self.write(active, "requirements.md", approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n")
            self.assertEqual("active", spec_contract.derive_spec_state(active, root)[0])
            invalid_evidence = root / "invalid-evidence"; invalid_evidence.mkdir()
            self.feature_files(
                invalid_evidence,
                requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                design=approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                tasks=approved + "- [x] A. done\n  Satisfies: REQ-1.1\n",
            )
            self.assertEqual("blocked", spec_contract.derive_spec_state(invalid_evidence, root)[0])
            invalid_graph = root / "invalid-graph"; invalid_graph.mkdir()
            self.feature_files(
                invalid_graph,
                requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                design=approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                tasks=approved + "- [ ] A. pending\n  Satisfies: REQ-1.1\n  Depends on: missing\n",
            )
            self.assertEqual("blocked", spec_contract.derive_spec_state(invalid_graph, root)[0])

            complete = root / "complete"; complete.mkdir()
            self.feature_files(
                complete,
                requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                design=approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                tasks=approved + "- [x] A. done\n  Satisfies: REQ-1.1\n" + evidence,
            )
            self.assertEqual("complete", spec_contract.derive_spec_state(complete, root)[0])
            self.write(
                complete,
                "tasks.md",
                approved
                + "- [x] A. done\n  Satisfies: REQ-1.1\n  Evidence:\n    - test:\n"
                + "      ```bash\n      python3 test.py\n      ```\n      -> OK\n"
                + "    - viewports: n/a — tooling only\n    - deviations: none\n",
            )
            self.assertEqual("complete", spec_contract.derive_spec_state(complete, root)[0])

            superseded = root / "superseded"; superseded.mkdir()
            target = root / "target"; target.mkdir()
            superseded_status = "> Status: superseded 2026-08-25 by target\n"
            self.feature_files(
                superseded,
                requirements=superseded_status + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                design=superseded_status + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                tasks=superseded_status + "- [x] A. done\n  Satisfies: REQ-1.1\n" + evidence,
            )
            self.assertEqual("superseded", spec_contract.derive_spec_state(superseded, root)[0])
            self.write(
                superseded,
                "requirements.md",
                "> Status: superseded 2026-08-25 by missing-target\n## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
            )
            self.assertEqual("blocked", spec_contract.derive_spec_state(superseded, root)[0])

            archived = root / "archive" / "same-bytes"; archived.mkdir(parents=True)
            self.feature_files(
                archived,
                requirements=(complete / "requirements.md").read_text(encoding="utf-8"),
                design=(complete / "design.md").read_text(encoding="utf-8"),
                tasks=(complete / "tasks.md").read_text(encoding="utf-8"),
            )
            self.assertEqual("archived", spec_contract.derive_spec_state(archived, root)[0])
            named_archive = root / "archive-like"; named_archive.mkdir()
            self.feature_files(
                named_archive,
                requirements=(complete / "requirements.md").read_text(encoding="utf-8"),
                design=(complete / "design.md").read_text(encoding="utf-8"),
                tasks=(complete / "tasks.md").read_text(encoding="utf-8"),
            )
            self.assertEqual("complete", spec_contract.derive_spec_state(named_archive, root)[0])

            empty = root / "empty"; empty.mkdir()
            self.assertEqual("blocked", spec_contract.derive_spec_state(empty, root)[0])
            ambiguous = root / "ambiguous"; ambiguous.mkdir()
            self.write(ambiguous, "requirements.md", approved)
            self.write(ambiguous, "bugfix.md", approved)
            self.assertEqual("blocked", spec_contract.derive_spec_state(ambiguous, root)[0])

    def test_task_two_active_summary_lexical_and_inactive_suppression(self):
        approved = "> Status: approved 2026-08-25\n"
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw) / ".ai" / "specs"; root.mkdir(parents=True)
            for name in ("zeta", "alpha"):
                directory = root / name; directory.mkdir()
                self.write(directory, "requirements.md", approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n")
            blocked = root / "broken"; blocked.mkdir()
            complete = root / "complete"; complete.mkdir()
            self.feature_files(
                complete,
                requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                design=approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                tasks=(
                    approved
                    + "- [x] A. done\n  Satisfies: REQ-1.1\n"
                    + "  Evidence:\n    - test: `python3 test.py` -> OK\n"
                    + "    - viewports: n/a — tooling only\n    - deviations: none\n"
                ),
            )
            summary, diagnostics = spec_contract.active_summary(root)
            self.assertIn("STATE_EMPTY_DIRECTORY", self.codes(diagnostics))
            self.assertEqual("Active specs: alpha zeta. Blocked specs: 1.\n", summary)
            self.assertNotIn("complete", summary)
            self.assertNotIn("archive", summary)

    def test_task_two_mutations_are_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")
        approved = "> Status: approved 2026-08-25\n"

        def load(mutant_source):
            with tempfile.TemporaryDirectory() as raw:
                path = Path(raw) / "spec_contract.py"
                path.write_text(mutant_source, encoding="utf-8")
                name = f"spec_contract_task_two_mutant_{len(sys.modules)}"
                module_spec = importlib.util.spec_from_file_location(name, path)
                module = importlib.util.module_from_spec(module_spec)
                sys.modules[name] = module
                module_spec.loader.exec_module(module)
                return module

        def rejects_unknown_task(module):
            with tempfile.TemporaryDirectory() as raw:
                directory = Path(raw); directory.mkdir(exist_ok=True)
                self.write(directory, "tasks.md", approved + "- [ ] A1. task\n")
                try:
                    text, diagnostics = module.build_spec_slice(directory, "unknown")
                    self.assertEqual("", text)
                    self.assertIn("SLICE_TASK_UNKNOWN", self.codes(diagnostics))
                except Exception as error:
                    raise AssertionError("unknown task must stay policy-fail") from error

        def emits_missing_marker(module):
            with tempfile.TemporaryDirectory() as raw:
                directory = Path(raw)
                self.feature_files(
                    directory,
                    requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                    design=approved + "## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Missing |\n",
                    tasks=approved + "- [ ] A. task\n  Satisfies: REQ-1.1\n",
                )
                text, _ = module.build_spec_slice(directory, "A")
                self.assertIn("MISSING:", text)

        def archives_by_location_only(module):
            with tempfile.TemporaryDirectory() as raw:
                root = Path(raw) / ".ai" / "specs"; root.mkdir(parents=True)
                directory = root / "archive-like"; directory.mkdir()
                self.feature_files(
                    directory,
                    requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                    design=approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                    tasks=approved + "- [x] A. done\n  Satisfies: REQ-1.1\n  Evidence:\n    - test: `python3 test.py` -> OK\n    - viewports: n/a — tooling only\n    - deviations: none\n",
                )
                self.assertEqual("complete", module.derive_spec_state(directory, root)[0])

        def suppresses_inactive(module):
            with tempfile.TemporaryDirectory() as raw:
                root = Path(raw) / ".ai" / "specs"; root.mkdir(parents=True)
                directory = root / "complete"; directory.mkdir()
                self.feature_files(
                    directory,
                    requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                    design=approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                    tasks=approved + "- [x] A. done\n  Satisfies: REQ-1.1\n  Evidence:\n    - test: `python3 test.py` -> OK\n    - viewports: n/a — tooling only\n    - deviations: none\n",
                )
                summary, _ = module.active_summary(root)
                self.assertNotIn("complete", summary)

        mutations = (
            ("if task is None:", "if False:", rejects_unknown_task),
            ("if mapping_diagnostics:", "if False:", emits_missing_marker),
            ("if _is_canonical_archive_location(feature_dir, canonical_specs_root):", "if feature_dir.name.startswith(\"archive\"):", archives_by_location_only),
            ('if state == "active":\n            active.append(directory.name)', 'if state in {"active", "complete"}:\n            active.append(directory.name)', suppresses_inactive),
        )
        for before, after, assertion in mutations:
            with self.subTest(before=before):
                self.assertEqual(1, source.count(before))
                with self.assertRaises(AssertionError):
                    assertion(load(source.replace(before, after)))

    def test_task_two_rework_one_slice_snapshot_and_mapping_fail_closed(self):
        approved = "> Status: approved 2026-08-25\n"
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            script = root / "scripts" / "spec_contract.py"
            script.parent.mkdir()
            script.write_text((SCRIPTS / "spec_contract.py").read_text(encoding="utf-8"), encoding="utf-8")
            specs = root / ".ai" / "specs"

            def feature(name, *, requirements=None, design=None, tasks=None):
                directory = specs / name
                directory.mkdir(parents=True)
                self.feature_files(
                    directory,
                    requirements=requirements or approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                    design=design or approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                    tasks=tasks or approved + "- [ ] A1. task\n  Satisfies: REQ-1.1\n",
                )
                return directory

            invalid_cases = {
                "bad-status": (
                    approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                    approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                    "> Status: approved 2026-08-25, amended\n- [ ] A1. task\n  Satisfies: REQ-1.1\n",
                    "STATUS_MALFORMED",
                ),
                "duplicate": (
                    approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                    approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                    approved + "- [ ] A1. one\n  Satisfies: REQ-1.1\n- [ ] A1. two\n  Satisfies: REQ-1.1\n",
                    "TASK_ID_DUPLICATE",
                ),
                "bad-graph": (
                    approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                    approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                    approved + "- [ ] A1. task\n  Satisfies: REQ-1.1\n  Depends on: missing\n",
                    "TASK_DEPENDENCY_UNKNOWN",
                ),
                "unclosed": (
                    approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n```\n",
                    approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                    approved + "- [ ] A1. task\n  Satisfies: REQ-1.1\n",
                    "TRACE_FENCE_UNCLOSED",
                ),
            }
            for name, (requirements, design, tasks, expected) in invalid_cases.items():
                with self.subTest(name=name):
                    feature(name, requirements=requirements, design=design, tasks=tasks)
                    completed = subprocess.run(
                        [sys.executable, str(script), "slice", "--feature", name, "--task", "A1"],
                        cwd=root, text=True, capture_output=True, check=False,
                    )
                    self.assertNotEqual(0, completed.returncode, completed.stdout + completed.stderr)
                    self.assertIn(expected, completed.stdout + completed.stderr)

            no_satisfies = feature("no-satisfies", tasks=approved + "- [ ] A1. task\n")
            text, diagnostics = spec_contract.build_spec_slice(no_satisfies, "A1")
            self.assertIn("SLICE_MAPPING_MISSING", self.codes(diagnostics))
            self.assertIn("MISSING: SLICE_MAPPING_MISSING:", text)

            no_trace = feature("no-trace", design=approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n")
            text, diagnostics = spec_contract.build_spec_slice(no_trace, "A1")
            self.assertIn("SLICE_MAPPING_MISSING", self.codes(diagnostics))
            self.assertIn("MISSING: SLICE_MAPPING_MISSING:", text)

            bugfix = specs / "bug-no-satisfies"; bugfix.mkdir()
            self.write(bugfix, "bugfix.md", approved + "- F-1 THE SYSTEM SHALL fix\n")
            self.write(bugfix, "tasks.md", approved + "- [ ] A1. task\n")
            text, diagnostics = spec_contract.build_spec_slice(bugfix, "A1")
            self.assertIn("SLICE_MAPPING_MISSING", self.codes(diagnostics))
            self.assertIn("MISSING: SLICE_MAPPING_MISSING:", text)

    def test_task_two_rework_one_resolver_authoring_and_evidence_classes(self):
        approved = "> Status: approved 2026-08-25\n"
        valid_requirement = approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n"
        valid_design = approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n"
        valid_task = approved + "- [ ] A1. task\n  Satisfies: REQ-1.1\n"
        evidence = "  Evidence:\n    - test: `python3 test.py` -> OK\n    - viewports: n/a — tooling only\n    - deviations: none\n"
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            specs = root / ".ai" / "specs"; specs.mkdir(parents=True)
            normal = specs / "normal"; normal.mkdir()
            self.feature_files(normal, requirements=valid_requirement, design=valid_design, tasks=valid_task)
            external = root / "outside"; external.mkdir()
            self.feature_files(external, requirements=valid_requirement, design=valid_design, tasks=valid_task)
            (specs / "link-out").symlink_to(external, target_is_directory=True)
            for invalid_feature in ("../normal", "normal/child", "bad.slug", ""):
                with self.subTest(invalid_feature=invalid_feature):
                    resolved, diagnostics = spec_contract.resolve_feature_directory(specs, invalid_feature)
                    self.assertIsNone(resolved)
                    self.assertIn("SLICE_FEATURE_UNKNOWN", self.codes(diagnostics))
            (normal / "requirements.md").unlink()
            (normal / "requirements.md").symlink_to(external / "requirements.md")
            self.assertEqual("blocked", spec_contract.derive_spec_state(normal, specs)[0])
            self.assertNotIn(specs / "link-out", spec_contract._state_directories(specs))

            archive = specs / "archive"; archive.mkdir()
            archived = archive / "real"; archived.mkdir()
            self.feature_files(archived, requirements=valid_requirement, design=valid_design, tasks=valid_task)
            self.assertEqual("archived", spec_contract.derive_spec_state(archived, specs)[0])
            archive_like = specs / "archive-like"; archive_like.mkdir()
            self.feature_files(archive_like, requirements=valid_requirement, design=valid_design, tasks=valid_task)
            self.assertEqual("active", spec_contract.derive_spec_state(archive_like, specs)[0])

            design_first = specs / "design-first"; design_first.mkdir()
            self.write(design_first, "design.md", approved + "## Design\n")
            self.assertEqual("active", spec_contract.derive_spec_state(design_first, specs)[0])
            bugfix = specs / "bugfix"; bugfix.mkdir()
            self.write(bugfix, "bugfix.md", approved + "- F-1 THE SYSTEM SHALL fix\n")
            self.assertEqual("active", spec_contract.derive_spec_state(bugfix, specs)[0])
            skipped = specs / "skipped"; skipped.mkdir()
            self.write(skipped, "requirements.md", valid_requirement)
            self.write(skipped, "tasks.md", valid_task)
            self.assertEqual("blocked", spec_contract.derive_spec_state(skipped, specs)[0])

            for marker in ("TODO", "TBD", "pending", "???"):
                with self.subTest(marker=marker):
                    directory = specs / f"evidence-{marker.replace('?', 'question')}"; directory.mkdir()
                    self.feature_files(
                        directory,
                        requirements=valid_requirement,
                        design=valid_design,
                        tasks=approved + "- [x] A1. done\n  Satisfies: REQ-1.1\n" + evidence.replace("OK", marker),
                    )
                    self.assertEqual("blocked", spec_contract.derive_spec_state(directory, specs)[0])
            mixed = specs / "mixed"; mixed.mkdir()
            self.feature_files(
                mixed,
                requirements=valid_requirement,
                design=valid_design,
                tasks=approved + "- [x] A1. done\n  Satisfies: REQ-1.1\n" + evidence.replace("    - viewports", "    - test: `python3 broken.py` -> pending\n    - viewports"),
            )
            self.assertEqual("blocked", spec_contract.derive_spec_state(mixed, specs)[0])

            script = root / "scripts" / "spec_contract.py"
            script.parent.mkdir()
            script.write_text((SCRIPTS / "spec_contract.py").read_text(encoding="utf-8"), encoding="utf-8")
            for command in (
                ("slice", "--feature", str(external), "--task", "A1"),
                ("state", "--feature", str(external)),
                ("gate", "phase", "--feature", str(external), "--phase", "implement", "--workflow", "requirements-first"),
            ):
                with self.subTest(command=command):
                    completed = subprocess.run([sys.executable, str(script), *command], cwd=root, text=True, capture_output=True, check=False)
                    self.assertNotEqual(0, completed.returncode, completed.stdout + completed.stderr)
                    self.assertNotIn("outside", completed.stdout)

    def test_task_two_rework_one_rejects_archive_container_through_every_public_feature_entry(self):
        approved = "> Status: approved 2026-08-25\n"
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            script = root / "scripts" / "spec_contract.py"
            script.parent.mkdir()
            script.write_text((SCRIPTS / "spec_contract.py").read_text(encoding="utf-8"), encoding="utf-8")
            archive = root / ".ai" / "specs" / "archive"
            archive.mkdir(parents=True)
            self.write(archive, "requirements.md", approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n")
            for command in (
                ("slice", "--feature", "archive", "--task", "A1"),
                ("state", "--feature", "archive"),
                ("gate", "phase", "--feature", "archive", "--phase", "design", "--workflow", "requirements-first"),
            ):
                with self.subTest(command=command):
                    completed = subprocess.run(
                        [sys.executable, str(script), *command],
                        cwd=root,
                        text=True,
                        capture_output=True,
                        check=False,
                    )
                    self.assertNotEqual(0, completed.returncode, completed.stdout + completed.stderr)
                    self.assertIn("SLICE_FEATURE_UNKNOWN", completed.stdout + completed.stderr)

    def test_task_two_rework_two_artifact_symlink_phase_gate_parity(self):
        approved = "> Status: approved 2026-08-25\n"
        requirement = approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n"
        design = approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n"
        tasks = approved + "- [ ] A1. task\n  Satisfies: REQ-1.1\n"
        bugfix = approved + "- F-1 THE SYSTEM SHALL fix\n- B-1 THE SYSTEM SHALL preserve\n"
        bugfix_tasks = approved + "- [ ] B1. task\n  Satisfies: F-1, B-1\n"
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            script = root / "scripts" / "spec_contract.py"
            script.parent.mkdir()
            script.write_text((SCRIPTS / "spec_contract.py").read_text(encoding="utf-8"), encoding="utf-8")
            specs = root / ".ai" / "specs"; specs.mkdir(parents=True)
            external = root / "external"; external.mkdir()
            self.write(external, "requirements.md", requirement)
            self.write(external, "design.md", design)
            self.write(external, "tasks.md", tasks)
            self.write(external, "bugfix.md", bugfix)
            self.write(external, "bugtasks.md", bugfix_tasks)
            variants = (
                ("rf-requirements", "requirements.md", "requirements.md", "design", "requirements-first", "A1"),
                ("rf-design", "design.md", "design.md", "tasks", "requirements-first", "A1"),
                ("rf-tasks", "tasks.md", "tasks.md", "implement", "requirements-first", "A1"),
                ("df-design", "design.md", "design.md", "requirements", "design-first", "A1"),
                ("bugfix-root", "bugfix.md", "bugfix.md", "tasks", "bugfix", "B1"),
                ("bugfix-tasks", "tasks.md", "bugtasks.md", "implement", "bugfix", "B1"),
            )
            for feature, link_name, target_name, phase, workflow, task_id in variants:
                with self.subTest(feature=feature):
                    directory = specs / feature; directory.mkdir()
                    if workflow == "bugfix":
                        if link_name != "bugfix.md":
                            self.write(directory, "bugfix.md", bugfix)
                        if link_name != "tasks.md":
                            self.write(directory, "tasks.md", bugfix_tasks)
                    else:
                        if link_name != "requirements.md":
                            self.write(directory, "requirements.md", requirement)
                        if link_name != "design.md":
                            self.write(directory, "design.md", design)
                        if link_name != "tasks.md":
                            self.write(directory, "tasks.md", tasks)
                    (directory / link_name).symlink_to(external / target_name)
                    commands = (
                        ("slice", "--feature", feature, "--task", task_id),
                        ("state", "--feature", feature),
                        ("gate", "phase", "--feature", feature, "--phase", phase, "--workflow", workflow),
                    )
                    for command in commands:
                        completed = subprocess.run(
                            [sys.executable, str(script), *command],
                            cwd=root,
                            text=True,
                            capture_output=True,
                            check=False,
                        )
                        self.assertNotEqual(0, completed.returncode, completed.stdout + completed.stderr)
                        self.assertIn("STATE_ARTIFACT_BLOCKED", completed.stdout + completed.stderr)

    def test_task_two_rework_three_phase_gate_rejects_conflicting_workflow_shapes(self):
        approved = "> Status: approved 2026-08-25\n"
        requirement = approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n"
        design = approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n"
        tasks = approved + "- [ ] A1. task\n  Satisfies: REQ-1.1\n"
        bugfix = approved + "- F-1 THE SYSTEM SHALL fix\n- B-1 THE SYSTEM SHALL preserve\n"
        bugfix_tasks = approved + "- [ ] B1. task\n  Satisfies: F-1, B-1\n"
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            script = root / "scripts" / "spec_contract.py"
            script.parent.mkdir()
            script.write_text((SCRIPTS / "spec_contract.py").read_text(encoding="utf-8"), encoding="utf-8")
            specs = root / ".ai" / "specs"; specs.mkdir(parents=True)
            external = root / "external"; external.mkdir()
            self.write(external, "bugfix.md", bugfix)
            self.write(external, "requirements.md", requirement)
            variants = (
                ("requirements-first-design", "requirements-first", "design", "bugfix.md", bugfix),
                ("requirements-first-tasks", "requirements-first", "tasks", "bugfix.md", bugfix),
                ("requirements-first-implement", "requirements-first", "implement", "bugfix.md", bugfix),
                ("design-first-requirements", "design-first", "requirements", "bugfix.md", bugfix),
                ("design-first-tasks", "design-first", "tasks", "bugfix.md", bugfix),
                ("design-first-implement", "design-first", "implement", "bugfix.md", bugfix),
                ("bugfix-tasks", "bugfix", "tasks", "requirements.md", requirement),
                ("bugfix-implement", "bugfix", "implement", "requirements.md", requirement),
            )
            for use_symlink in (False, True):
                for feature, workflow, phase, conflict_name, conflict_data in variants:
                    with self.subTest(feature=feature, symlink=use_symlink):
                        directory = specs / f"{'symlink' if use_symlink else 'regular'}-{feature}"
                        directory.mkdir()
                        if workflow == "bugfix":
                            self.write(directory, "bugfix.md", bugfix)
                            if phase == "implement":
                                self.write(directory, "tasks.md", bugfix_tasks)
                        else:
                            self.write(directory, "design.md", design)
                            if workflow == "requirements-first" or phase != "requirements":
                                self.write(directory, "requirements.md", requirement)
                            if phase == "implement":
                                self.write(directory, "tasks.md", tasks)
                        if use_symlink:
                            (directory / conflict_name).symlink_to(external / conflict_name)
                        else:
                            self.write(directory, conflict_name, conflict_data)
                        completed = subprocess.run(
                            [
                                sys.executable,
                                str(script),
                                "gate",
                                "phase",
                                "--feature",
                                directory.name,
                                "--phase",
                                phase,
                                "--workflow",
                                workflow,
                            ],
                            cwd=root,
                            text=True,
                            capture_output=True,
                            check=False,
                        )
                        self.assertNotEqual(0, completed.returncode, completed.stdout + completed.stderr)
                        self.assertIn("PHASE_WORKFLOW_AMBIGUOUS", completed.stdout + completed.stderr)

    def test_task_two_rework_three_workflow_shape_mutation_is_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")
        before = "diagnostics: list[Diagnostic] = list(_workflow_shape_diagnostics(feature_dir, workflow, phase))"
        after = "diagnostics: list[Diagnostic] = []"
        self.assertEqual(1, source.count(before))
        with tempfile.TemporaryDirectory() as raw:
            path = Path(raw) / "spec_contract.py"
            path.write_text(source.replace(before, after), encoding="utf-8")
            name = f"spec_contract_task_two_rework_three_mutant_{len(sys.modules)}"
            module_spec = importlib.util.spec_from_file_location(name, path)
            module = importlib.util.module_from_spec(module_spec)
            sys.modules[name] = module
            module_spec.loader.exec_module(module)
            specs = Path(raw) / "specs"; specs.mkdir()
            feature = specs / "feature"; feature.mkdir()
            approved = "> Status: approved 2026-08-25\n"
            self.write(feature, "requirements.md", approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n")
            self.write(feature, "bugfix.md", approved + "- F-1 THE SYSTEM SHALL fix\n")
            with self.assertRaises(AssertionError):
                _, diagnostics = module.check_phase_gate(feature, "design", "requirements-first", specs)
                self.assertIn("PHASE_WORKFLOW_AMBIGUOUS", self.codes(diagnostics))

    def test_task_two_rework_one_mutations_are_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")

        def load(mutant_source):
            with tempfile.TemporaryDirectory() as raw:
                path = Path(raw) / "spec_contract.py"
                path.write_text(mutant_source, encoding="utf-8")
                name = f"spec_contract_task_two_rework_one_mutant_{len(sys.modules)}"
                module_spec = importlib.util.spec_from_file_location(name, path)
                module = importlib.util.module_from_spec(module_spec)
                sys.modules[name] = module
                module_spec.loader.exec_module(module)
                return module

        def blocks_evidence_marker(module):
            approved = "> Status: approved 2026-08-25\n"
            with tempfile.TemporaryDirectory() as raw:
                root = Path(raw); directory = root / "spec"; directory.mkdir()
                self.feature_files(
                    directory,
                    requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                    design=approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                    tasks=approved + "- [x] A1. done\n  Satisfies: REQ-1.1\n  Evidence:\n    - test: `python3 test.py` -> TODO\n    - viewports: n/a — tooling only\n    - deviations: none\n",
                )
                self.assertEqual("blocked", module.derive_spec_state(directory, root)[0])

        mutation = ("if _marker_leads(_segments()):", "if False:")
        self.assertEqual(1, source.count(mutation[0]))
        with self.assertRaises(AssertionError):
            blocks_evidence_marker(load(source.replace(*mutation)))


    def test_task_two_rework_one_failure_class_mutations_are_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")
        approved = "> Status: approved 2026-08-25\n"

        def load(mutant_source):
            with tempfile.TemporaryDirectory() as raw:
                path = Path(raw) / "spec_contract.py"
                path.write_text(mutant_source, encoding="utf-8")
                name = f"spec_contract_task_two_class_mutant_{len(sys.modules)}"
                module_spec = importlib.util.spec_from_file_location(name, path)
                module = importlib.util.module_from_spec(module_spec)
                sys.modules[name] = module
                module_spec.loader.exec_module(module)
                return module

        def rejects_invalid_snapshot(module):
            with tempfile.TemporaryDirectory() as raw:
                directory = Path(raw); directory.mkdir(exist_ok=True)
                self.feature_files(
                    directory,
                    requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                    design=approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                    tasks="> Status: approved 2026-08-25, amended\n- [ ] A1. task\n  Satisfies: REQ-1.1\n",
                )
                text, diagnostics = module.build_spec_slice(directory, "A1")
                self.assertEqual("", text)
                self.assertIn("STATUS_MALFORMED", self.codes(diagnostics))

        def rejects_resolver_bypass(module):
            with tempfile.TemporaryDirectory() as raw:
                root = Path(raw) / "specs"; root.mkdir()
                external = Path(raw) / "outside"; external.mkdir()
                resolved, diagnostics = module.resolve_feature_directory(root, str(external))
                self.assertIsNone(resolved)
                self.assertIn("SLICE_FEATURE_UNKNOWN", self.codes(diagnostics))

        def rejects_archive_container(module):
            with tempfile.TemporaryDirectory() as raw:
                root = Path(raw) / "specs"; root.mkdir()
                (root / "archive").mkdir()
                resolved, diagnostics = module.resolve_feature_directory(root, "archive")
                self.assertIsNone(resolved)
                self.assertIn("SLICE_FEATURE_UNKNOWN", self.codes(diagnostics))

        def keeps_authoring_root_active(module):
            with tempfile.TemporaryDirectory() as raw:
                root = Path(raw) / "specs"; root.mkdir()
                directory = root / "bugfix"; directory.mkdir()
                self.write(directory, "bugfix.md", approved + "- F-1 THE SYSTEM SHALL fix\n")
                self.assertEqual("active", module.derive_spec_state(directory, root)[0])

        def rejects_mixed_evidence(module):
            with tempfile.TemporaryDirectory() as raw:
                root = Path(raw) / "specs"; root.mkdir()
                directory = root / "mixed"; directory.mkdir()
                self.feature_files(
                    directory,
                    requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                    design=approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                    tasks=approved + "- [x] A1. done\n  Satisfies: REQ-1.1\n  Evidence:\n    - test: `python3 good.py` -> OK\n    - test: `python3 bad.py` -> pending\n    - viewports: n/a — tooling only\n    - deviations: none\n",
                )
                self.assertEqual("blocked", module.derive_spec_state(directory, root)[0])

        mutations = (
            ("if snapshot_diagnostics:", "if False:", rejects_invalid_snapshot),
            ("if feature == \"archive\" or not re.fullmatch(TASK_ID_PATTERN, feature):", "if False:", rejects_resolver_bypass),
            ("if feature == \"archive\" or not re.fullmatch(TASK_ID_PATTERN, feature):", "if not re.fullmatch(TASK_ID_PATTERN, feature):", rejects_archive_container),
            ("if highest_existing > earliest_missing:", "if True:", keeps_authoring_root_active),
            ("if _marker_leads(_segments()):", "if False:", rejects_mixed_evidence),
        )
        for before, after, assertion in mutations:
            with self.subTest(before=before):
                self.assertEqual(1, source.count(before))
                with self.assertRaises(AssertionError):
                    assertion(load(source.replace(before, after)))

        state_before = '''        if args.feature:
            feature_dir, resolver_diagnostics = resolve_feature_directory(specs_dir, args.feature)
            if resolver_diagnostics or feature_dir is None:
                _print_diagnostics(resolver_diagnostics)
                return _diagnostic_exit_code(resolver_diagnostics)
            state, diagnostics = derive_spec_state(feature_dir, specs_dir)
'''
        state_after = '''        if args.feature:
            feature_dir, resolver_diagnostics = resolve_feature_directory(specs_dir, args.feature)
            if resolver_diagnostics or feature_dir is None:
                _print_diagnostics(resolver_diagnostics)
                return 0
            state, diagnostics = derive_spec_state(feature_dir, specs_dir)
'''
        self.assertEqual(1, source.count(state_before))
        with self.assertRaises(AssertionError):
            module = load(source.replace(state_before, state_after))
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                self.assertNotEqual(0, module._cli(("state", "--feature", "missing")))


    def test_task_two_rework_one_spec_state_wrapper_mutations_are_killed(self):
        wrapper_source = (SCRIPTS / "spec-state.sh").read_text(encoding="utf-8")
        engine_source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")
        approved = "> Status: approved 2026-08-25\n"

        def assert_wrapper_contract(source):
            with tempfile.TemporaryDirectory() as raw:
                root = Path(raw)
                scripts = root / "scripts"; scripts.mkdir()
                wrapper = scripts / "spec-state.sh"
                wrapper.write_text(source, encoding="utf-8")
                wrapper.chmod(0o755)
                (scripts / "spec_contract.py").write_text(engine_source, encoding="utf-8")
                feature = root / ".ai" / "specs" / "feature"; feature.mkdir(parents=True)
                self.feature_files(
                    feature,
                    requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n",
                    design=approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n",
                    tasks=approved + "- [ ] A1. task\n  Satisfies: REQ-1.1\n",
                )
                for command in (("git", "init", "-q"), ("git", "config", "user.email", "test@example.invalid"), ("git", "config", "user.name", "test"), ("git", "add", "."), ("git", "commit", "-qm", "fixture")):
                    completed = subprocess.run(command, cwd=root, text=True, capture_output=True, check=False)
                    self.assertEqual(0, completed.returncode, completed.stderr)
                headings = ("== [a] artifacts:", "== [b] checkboxes:", "== [c] git:", "== [d] disk artifacts ==", "== [e] derived state ==")
                completed = subprocess.run([str(wrapper), "feature"], cwd=root, text=True, capture_output=True, check=False)
                self.assertEqual(0, completed.returncode, completed.stderr)
                for heading in headings:
                    self.assertIn(heading, completed.stdout)
                self.assertIn("pol-core.slnx: MISSING", completed.stdout)
                missing = subprocess.run([str(wrapper), "missing"], cwd=root, text=True, capture_output=True, check=False)
                self.assertNotEqual(0, missing.returncode)
                blocked_dir = root / ".ai" / "specs" / "blocked"; blocked_dir.mkdir()
                self.write(blocked_dir, "requirements.md", approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n")
                self.write(blocked_dir, "bugfix.md", approved + "- F-1 THE SYSTEM SHALL fix\n")
                blocked = subprocess.run([str(wrapper), "blocked"], cwd=root, text=True, capture_output=True, check=False)
                self.assertNotEqual(0, blocked.returncode)
                for heading in headings:
                    self.assertIn(heading, blocked.stdout)

        assert_wrapper_contract(wrapper_source)
        mutations = (
            ("== [a] artifacts:", "== [z] artifacts:"),
            ('")" || STATE_RC=$?', '")"'),
        )
        for mutation in mutations:
            with self.subTest(mutation=mutation[0]):
                self.assertEqual(1, wrapper_source.count(mutation[0]))
                with self.assertRaises(AssertionError):
                    assert_wrapper_contract(wrapper_source.replace(*mutation))


    def test_task_two_rework_four_finite_boundary_grammar_sweep(self):
        approved = "> Status: approved 2026-08-25\n"
        design = approved + "## Build\nBody\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n"
        tasks = approved + "- [ ] A1. first\n  Satisfies: REQ-1.1\n"
        marker = "TAIL-MARKER"
        cases = (
            ("h2-col0", "## Notes", True),
            ("h1-col0", "# Notes", True),
            ("h2-indent1", " ## Notes", False),
            ("h2-indent3", "   ## Notes", False),
            ("h1-indent1", " # Notes", False),
            ("html-comment", "<!--\n## Hidden inside comment\n-->", False),
            ("html-comment-oneline", "<!-- hidden -->\n", False),
            ("list-nested-h2", "- item\n  ## Notes", False),
            ("list-nested-h1", "1. item\n   # Notes", False),
            ("setext-h2", "Notes\n---", False),
            ("setext-h1", "Notes\n===", False),
            ("h2-indent4", "    ## Notes", False),
            ("h2-indent8", "        ## Notes", False),
            ("h1-indent4", "    # Notes", False),
            ("h1-tab", "\t# Notes", False),
            ("h2-tab-indent", "\t## Notes", False),
            ("h3", "### Notes", False),
            ("h6", "###### Notes", False),
            ("no-space", "##Notes", False),
            ("hash-only-h1", "#", True),
            ("hash-only-h2", "##", True),
            ("h2-tab-after", "##\tNotes", True),
            ("h2-trailing-space", "##   ", True),
            ("blockquote-h2", "> ## Notes", False),
            ("bullet-h2", "- ## Notes", False),
            ("text-hash", "value # Notes", False),
        )
        for label, line, should_close in cases:
            with self.subTest(case=label):
                with tempfile.TemporaryDirectory() as raw:
                    feature = Path(raw) / "feature"; feature.mkdir()
                    self.feature_files(
                        feature,
                        requirements=approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n" + line + "\n" + marker + "\n",
                        design=design,
                        tasks=tasks,
                    )
                    text, diagnostics = spec_contract.build_spec_slice(feature, "A1")
                    self.assertEqual((), diagnostics)
                    self.assertEqual(should_close, marker not in text)

    def test_task_two_rework_four_section_blocks_end_at_next_major_heading(self):
        approved = "> Status: approved 2026-08-25\n"
        requirements = (
            approved
            + "## REQ-1: Capability\n"
            + "- 1.1 THE SYSTEM SHALL work\n"
            + "### Detail of REQ-1\n"
            + "KEEP-SUBHEADING\n"
            + "```text\n"
            + "## Fenced heading\n"
            + "KEEP-FENCED\n"
            + "```\n"
            + "    # indented four spaces is code not heading\n"
            + "KEEP-AFTER-INDENTED-CODE\n"
            + "<!--\n"
            + "## commented out section\n"
            + "-->\n"
            + "KEEP-AFTER-COMMENT\n"
            + "  ## indented heading is content by contract\n"
            + "KEEP-AFTER-INDENTED-HEADING\n"
            + "# Appendix H1\n"
            + "DROP-H1-REQ\n"
            + "## Notes\n"
            + "DROP-INTERVENING\n"
            + "## REQ-2: Second\n"
            + "- 2.1 THE SYSTEM SHALL also work\n"
            + "## Appendix\n"
            + "DROP-TRAILING\n"
        )
        design = (
            approved
            + "## Build\nKEEP-DESIGN-BODY\n"
            + "\t# tab indented is code not heading\n"
            + "KEEP-DESIGN-AFTER-CODE\n"
            + "# Design appendix\nDROP-H1-DESIGN\n"
            + "\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n"
            + "| REQ-1.1 | Build |\n| REQ-2.1 | Build |\n"
        )
        tasks = approved + "- [ ] A1. first\n  Satisfies: REQ-1.1\n- [ ] A2. second\n  Satisfies: REQ-2.1\n"
        dropped = ("DROP-H1-REQ", "DROP-INTERVENING", "DROP-TRAILING", "DROP-H1-DESIGN")

        def assert_block_altitude(module):
            with tempfile.TemporaryDirectory() as raw:
                feature = Path(raw) / "feature"; feature.mkdir()
                self.feature_files(feature, requirements=requirements, design=design, tasks=tasks)
                first, first_diagnostics = module.build_spec_slice(feature, "A1")
                self.assertEqual((), first_diagnostics)
                self.assertIn("KEEP-SUBHEADING", first)
                self.assertIn("KEEP-FENCED", first)
                self.assertIn("KEEP-DESIGN-BODY", first)
                self.assertIn("KEEP-AFTER-INDENTED-CODE", first)
                self.assertIn("KEEP-AFTER-COMMENT", first)
                self.assertIn("KEEP-AFTER-INDENTED-HEADING", first)
                self.assertIn("KEEP-DESIGN-AFTER-CODE", first)
                for marker in dropped:
                    self.assertNotIn(marker, first)
                second, second_diagnostics = module.build_spec_slice(feature, "A2")
                self.assertEqual((), second_diagnostics)
                self.assertIn("- 2.1 THE SYSTEM SHALL also work", second)
                for marker in dropped:
                    self.assertNotIn(marker, second)

        assert_block_altitude(spec_contract)

        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            script = root / "scripts" / "spec_contract.py"; script.parent.mkdir()
            script.write_text((SCRIPTS / "spec_contract.py").read_text(encoding="utf-8"), encoding="utf-8")
            specs = root / ".ai" / "specs"; specs.mkdir(parents=True)
            feature = specs / "feature"; feature.mkdir()
            self.feature_files(feature, requirements=requirements, design=design, tasks=tasks)
            completed = subprocess.run(
                [sys.executable, str(script), "slice", "--feature", "feature", "--task", "A1"],
                cwd=root, text=True, capture_output=True, check=False,
            )
            self.assertEqual(0, completed.returncode, completed.stderr)
            self.assertIn("KEEP-FENCED", completed.stdout)
            for marker in dropped:
                self.assertNotIn(marker, completed.stdout)

        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")
        mutations = (
            (
                "        if _is_sibling_major_heading(line):\n            boundaries.append(number)",
                "        if REQ_HEADING_RE.match(line):\n            boundaries.append(number)",
            ),
            (
                "    boundaries = [number for number, line in visible if _is_sibling_major_heading(line)]",
                '    boundaries = [number for number, line in visible if line.startswith("## ")]',
            ),
            (
                'return bool(re.match(r"^#{1,2}(?:[ \\t]|$)", line))',
                'return bool(line.startswith("#"))',
            ),
            (
                '            if HTML_COMMENT_OPEN_RE.match(line):',
                '            if False:',
            ),
        )
        for index, (before, after) in enumerate(mutations):
            with self.subTest(mutation=index):
                self.assertEqual(1, source.count(before))
            with tempfile.TemporaryDirectory() as raw:
                path = Path(raw) / "spec_contract.py"
                path.write_text(source.replace(before, after), encoding="utf-8")
                name = f"spec_contract_rework_four_mutant_{index}_{len(sys.modules)}"
                module_spec = importlib.util.spec_from_file_location(name, path)
                module = importlib.util.module_from_spec(module_spec)
                sys.modules[name] = module
                module_spec.loader.exec_module(module)
                with self.assertRaises(AssertionError):
                    assert_block_altitude(module)

    def test_task_two_rework_three_non_regular_artifacts_block_state_and_slice(self):
        approved = "> Status: approved 2026-08-25\n"
        requirement = approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n"
        design = approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n"
        tasks = approved + "- [ ] A1. task\n  Satisfies: REQ-1.1\n"

        def assert_every_consumer_blocks(module_path, root, specs):
            for representation in ("directory", "fifo", "broken-symlink"):
                directory = specs / f"rf-{representation}"; directory.mkdir()
                self.write(directory, "requirements.md", requirement)
                self.write(directory, "design.md", design)
                self.write(directory, "tasks.md", tasks)
                conflict = directory / "bugfix.md"
                if representation == "directory":
                    conflict.mkdir()
                elif representation == "fifo":
                    os.mkfifo(conflict)
                else:
                    conflict.symlink_to(root / "no-such-target.md")
                for command in (
                    ["state", "--feature", directory.name],
                    ["slice", "--feature", directory.name, "--task", "A1"],
                    ["gate", "phase", "--feature", directory.name, "--phase", "implement", "--workflow", "requirements-first"],
                ):
                    completed = subprocess.run(
                        [sys.executable, str(module_path)] + command,
                        cwd=root, text=True, capture_output=True, check=False,
                    )
                    self.assertNotEqual(0, completed.returncode, [representation] + command + [completed.stdout])

        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            script = root / "scripts" / "spec_contract.py"; script.parent.mkdir()
            script.write_text(source, encoding="utf-8")
            specs = root / ".ai" / "specs"; specs.mkdir(parents=True)
            assert_every_consumer_blocks(script, root, specs)

        before = "if not entries[name].is_file()"
        after = "if False"
        self.assertEqual(1, source.count(before))
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            script = root / "scripts" / "spec_contract.py"; script.parent.mkdir()
            script.write_text(source.replace(before, after), encoding="utf-8")
            specs = root / ".ai" / "specs"; specs.mkdir(parents=True)
            with self.assertRaises(AssertionError):
                assert_every_consumer_blocks(script, root, specs)

    def test_task_two_rework_two_canonical_reader_mutation_is_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")
        before = "if path.is_symlink() or not path.is_file() or not stat.S_ISREG(path.stat().st_mode):"
        after = "if not path.is_file() or not stat.S_ISREG(path.stat().st_mode):"
        self.assertEqual(1, source.count(before))
        with tempfile.TemporaryDirectory() as raw:
            path = Path(raw) / "spec_contract.py"
            path.write_text(source.replace(before, after), encoding="utf-8")
            name = f"spec_contract_task_two_rework_two_mutant_{len(sys.modules)}"
            module_spec = importlib.util.spec_from_file_location(name, path)
            module = importlib.util.module_from_spec(module_spec)
            sys.modules[name] = module
            module_spec.loader.exec_module(module)
            specs = Path(raw) / "specs"; specs.mkdir()
            feature = specs / "feature"; feature.mkdir()
            external = Path(raw) / "external"; external.mkdir()
            approved = "> Status: approved 2026-08-25\n"
            self.write(external, "requirements.md", approved + "## REQ-1: Capability\n- 1.1 THE SYSTEM SHALL work\n")
            self.write(feature, "design.md", approved + "## Build\n\n## Requirement Traceability\n| REQ | Section |\n| --- | --- |\n| REQ-1.1 | Build |\n")
            self.write(feature, "tasks.md", approved + "- [ ] A1. task\n  Satisfies: REQ-1.1\n")
            (feature / "requirements.md").symlink_to(external / "requirements.md")
            with self.assertRaises(AssertionError):
                _, diagnostics = module.check_phase_gate(feature, "design", "requirements-first", specs)
                self.assertIn("STATE_ARTIFACT_BLOCKED", self.codes(diagnostics))



class CheckAllStrictTest(unittest.TestCase):
    """`check --all --strict`: direct directory ทุกตัวถูกตรวจโดยไม่มี ledger skip."""

    def _build(self, root: Path) -> None:
        import json as _json
        req_ok = ("# Req\n\n## REQ-1: Cap\n\n"
                  "- 1.1 WHEN a THEN THE SYSTEM SHALL b.\n")
        req_bad = "# Req\nnothing canonical\n"
        specs = {
            "alpha-active": req_ok,
            "beta-bugfix": None,           # bugfix shape built below
            "gamma-legacy": req_bad,       # ledger-dispositioned -> skipped
        }
        for name, body in specs.items():
            d = root / name
            d.mkdir(parents=True, exist_ok=True)
            if name == "beta-bugfix":
                (d / "bugfix.md").write_text(
                    "# Bugfix: beta\n\n- F-1 WHEN k THE SYSTEM SHALL pass.\n",
                    encoding="utf-8")
                continue
            (d / "requirements.md").write_text(body or "", encoding="utf-8")
        resolutions = {"decisions": [
            {"path": ".ai/specs/gamma-legacy/design.md", "field": "trace.table",
             "taskId": "", "disposition": "trace-header-canonical"}]}
        ledger_dir = root / "sdd-operating-layer-parity"
        ledger_dir.mkdir(parents=True, exist_ok=True)
        (ledger_dir / "migration-resolutions.json").write_text(
            _json.dumps(resolutions), encoding="utf-8")

    def test_active_first_scope_and_exit_codes(self):
        with tempfile.TemporaryDirectory(prefix="checkall-") as td:
            root = Path(td) / ".ai" / "specs"
            root.mkdir(parents=True)
            self._build(root)
            env = dict(os.environ, PYTHONDONTWRITEBYTECODE="1")
            proc = subprocess.run(
                [sys.executable, str(SCRIPTS / "spec_contract.py"),
                 "check", "--all", "--strict", "--specs-root", str(root)],
                capture_output=True, text=True, env=env)
            self.assertEqual(1, proc.returncode)
            self.assertIn("check --all --strict: 4 checked / 4 failing / 0 unchecked", proc.stdout)
            self.assertIn("::group::strict-trace gamma-legacy", proc.stdout)

    def test_ledger_skip_semantics_via_helper(self):
        with tempfile.TemporaryDirectory(prefix="ledgerlegacy-") as td:
            root = Path(td)
            self._build(root)
            skipped = spec_contract.ledger_legacy_features(root)
            self.assertEqual(skipped, {"gamma-legacy"})

    def test_ledger_legacy_feature_is_checked_with_compatibility_trace(self):
        with tempfile.TemporaryDirectory(prefix="ledgercompat-") as td:
            root = Path(td)
            feature = root / "legacy-feature"
            feature.mkdir(parents=True)
            unknown = "> Status: unknown\n"
            (feature / "requirements.md").write_text(
                unknown + "## REQ-1: Capability\n- 1.1 WHEN input arrives THE SYSTEM SHALL accept it.\n",
                encoding="utf-8",
            )
            (feature / "design.md").write_text(
                unknown
                + "## Build\n\n"
                + "## Requirement Traceability\n"
                + "| Section | REQ |\n| --- | --- |\n| Legacy component | REQ-1.1 |\n",
                encoding="utf-8",
            )
            (feature / "tasks.md").write_text(
                unknown + "- [x] 1. implement.\n     Satisfies: REQ-1.1\n",
                encoding="utf-8",
            )
            owner = root / "sdd-operating-layer-parity"
            owner.mkdir()
            (owner / "migration-resolutions.json").write_text(
                json.dumps({"decisions": [{
                    "path": ".ai/specs/legacy-feature/design.md",
                    "field": "trace.table",
                    "taskId": "",
                    "disposition": "trace-header-canonical",
                }]}),
                encoding="utf-8",
            )

            with contextlib.redirect_stdout(io.StringIO()):
                rc = spec_contract.all_tree_trace_run("legacy-feature", root)

            self.assertEqual(0, rc)

    def test_unledgered_legacy_feature_still_fails_canonical_strict_trace(self):
        with tempfile.TemporaryDirectory(prefix="unledgeredcompat-") as td:
            root = Path(td)
            feature = root / "legacy-feature"
            feature.mkdir(parents=True)
            unknown = "> Status: unknown\n"
            (feature / "requirements.md").write_text(
                unknown + "## REQ-1: Capability\n- 1.1 WHEN input arrives THE SYSTEM SHALL accept it.\n",
                encoding="utf-8",
            )
            (feature / "design.md").write_text(
                unknown
                + "## Requirement Traceability\n"
                + "| Section | REQ |\n| --- | --- |\n| Legacy component | REQ-1.1 |\n",
                encoding="utf-8",
            )
            (feature / "tasks.md").write_text(
                unknown + "- [x] 1. implement.\n     Satisfies: REQ-1.1\n",
                encoding="utf-8",
            )

            with contextlib.redirect_stdout(io.StringIO()):
                rc = spec_contract.all_tree_trace_run("legacy-feature", root)

            self.assertEqual(1, rc)

    def test_ledger_active_authoring_chain_is_checked_without_fabricated_requirements(self):
        with tempfile.TemporaryDirectory(prefix="ledgerauthoring-") as td:
            root = Path(td)
            feature = root / "design-first-tracker"
            feature.mkdir(parents=True)
            (feature / "design.md").write_text(
                "> Status: unknown\n# Design\n", encoding="utf-8"
            )
            (feature / "tasks.md").write_text(
                "> Status: unknown\n# Tasks\n", encoding="utf-8"
            )
            owner = root / "sdd-operating-layer-parity"
            owner.mkdir()
            (owner / "migration-resolutions.json").write_text(
                json.dumps({"decisions": [{
                    "path": ".ai/specs/design-first-tracker/tasks.md",
                    "field": "authoring.chain",
                    "taskId": "",
                    "disposition": "active-authoring-exempt",
                    "rationale": "design-first tracker without requirements by documented intent",
                }]}),
                encoding="utf-8",
            )

            with contextlib.redirect_stdout(io.StringIO()):
                rc = spec_contract.all_tree_trace_run("design-first-tracker", root)

            self.assertEqual(0, rc)

    def test_ledger_superseded_bugfix_checks_status_and_criteria_without_task_trace(self):
        with tempfile.TemporaryDirectory(prefix="ledgersuperseded-") as td:
            root = Path(td)
            feature = root / "historical-bugfix"
            feature.mkdir(parents=True)
            status = "> Status: superseded 2026-07-01 by replacement\n"
            (feature / "bugfix.md").write_text(
                status + "- F-1 WHEN input arrives THE SYSTEM SHALL accept it.\n",
                encoding="utf-8",
            )
            (feature / "tasks.md").write_text(
                status + "- [x] T1. historical implementation.\n",
                encoding="utf-8",
            )
            (root / "replacement").mkdir()
            owner = root / "sdd-operating-layer-parity"
            owner.mkdir()
            (owner / "migration-resolutions.json").write_text(
                json.dumps({"decisions": [{
                    "path": ".ai/specs/historical-bugfix/bugfix.md",
                    "field": "authoring.chain",
                    "taskId": "",
                    "disposition": "legacy-baseline-exempt",
                    "rationale": "superseded history-only spec",
                }]}),
                encoding="utf-8",
            )

            with contextlib.redirect_stdout(io.StringIO()):
                rc = spec_contract.all_tree_trace_run("historical-bugfix", root)

            self.assertEqual(0, rc)


if __name__ == "__main__":
    unittest.main()


class GateSelectionTest(unittest.TestCase):
    """Task 3: raw snapshot selection, canonical ranges และ Evidence v2 gate."""

    GOLDEN_TASK = (
        "\n- [x] {id}. title.\n"
        "     Satisfies: REQ-1.1\n"
        "     Verify:\n"
        "       - `cmd`\n"
        "     Evidence:\n"
        "       - test: `true` -> ran 1 tests; OK\n"
        "       - viewports: n/a \u2014 tooling-only\n"
        "       - deviations: none\n"
    )
    BARE_TASK = "\n- [ ] {id}. pending title.\n"

    def _make(self, *tasks: str) -> bytes:
        head = "# tasks\n\n## Implementation tasks\n"
        return head.encode("utf-8") + "".join(tasks).encode("utf-8")

    def _selection(self, before: bytes | None, after: bytes, *, source: str = "pre-commit",
                   changed=None) -> object:
        from spec_contract import GateSelection, canonical_changed_ranges

        exists = before is not None
        before_bytes = before or b""
        if changed is None:
            changed = canonical_changed_ranges(before_bytes, after) if exists else canonical_changed_ranges(b"", after)
        return GateSelection(
            path=".ai/specs/demo/tasks.md",
            before_exists=exists,
            before_bytes=before_bytes,
            after_bytes=after,
            changed_ranges=tuple(changed),
            source=source,
        )

    def _splice(self, before: bytes, after: bytes, ranges) -> bytes:
        out = bytearray()
        cursor = 0
        for item in ranges:
            out.extend(before[cursor:item.before_start])
            out.extend(after[item.after_start:item.after_end])
            cursor = item.before_end
        out.extend(before[cursor:])
        return bytes(out)

    def test_canonical_ranges_are_exact_sorted_and_non_overlapping(self):
        from spec_contract import canonical_changed_ranges

        self.assertEqual(canonical_changed_ranges(b"same", b"same"), ())
        pairs = [
            (b"aaa\nbbb\nccc\n", b"aaa\nXXX\nYYY\nccc\n"),
            (b"a\nb\nc\nd\n", b"a\nX\nc\nd\nY\nZ\n"),
            (b"", b"brand-new-file-content\n"),
            (b"delete-me\nkeep\n", b"keep\n"),
            ("แล้วก็ emoji 🎯 ด้วย\n".encode(), "emoji 🎯\nchanged\n".encode()),
        ]
        for before, after in pairs:
            ranges = canonical_changed_ranges(before, after)
            self.assertEqual(
                [item.after_start for item in ranges],
                sorted(item.after_start for item in ranges),
            )
            for low, item in enumerate(ranges):
                self.assertTrue(0 <= item.before_start <= item.before_end <= len(before))
                self.assertTrue(0 <= item.after_start <= item.after_end <= len(after))
                for other in ranges[low + 1:]:
                    self.assertTrue(item.after_end <= other.after_start)
                    self.assertTrue(item.before_end <= other.before_start)
            self.assertEqual(self._splice(before, after, ranges), after)

    def test_transition_after_only_overlap_and_no_reselection(self):
        from spec_contract import discover_completed_tasks

        before = self._make(self.BARE_TASK.format(id="1"), self.BARE_TASK.format(id="2"))
        done_before = self._make(self.GOLDEN_TASK.format(id="1"), self.BARE_TASK.format(id="2"))
        both_done = self._make(self.GOLDEN_TASK.format(id="1"), self.GOLDEN_TASK.format(id="2"))
        ids, diags = discover_completed_tasks(self._selection(done_before, both_done))
        self.assertEqual((ids, diags), (("2",), ()))
        fresh = both_done + self.GOLDEN_TASK.format(id="3").encode("utf-8") if False else \
            self._make(self.GOLDEN_TASK.format(id="1"), self.BARE_TASK.format(id="2"), self.GOLDEN_TASK.format(id="3"))
        ids_fresh, _ = discover_completed_tasks(self._selection(done_before, fresh))
        self.assertEqual(ids_fresh, ("3",))
        ids_same, _ = discover_completed_tasks(self._selection(both_done, both_done))
        self.assertEqual(ids_same, ())
        alphanumeric = self._make(self.BARE_TASK.format(id="A1"))
        alphanumeric_done = self._make(self.GOLDEN_TASK.format(id="A1"))
        ids_alpha, _ = discover_completed_tasks(self._selection(alphanumeric, alphanumeric_done))
        self.assertEqual(ids_alpha, ("A1",))

    def test_after_only_task_requires_opening_span_overlap(self):
        from spec_contract import discover_completed_tasks

        base = self._make(self.GOLDEN_TASK.format(id="1"))
        grew = self._make(self.GOLDEN_TASK.format(id="1")) + self.GOLDEN_TASK.format(id="9").encode("utf-8")
        ids_overlap, _ = discover_completed_tasks(self._selection(base, grew))
        self.assertEqual(ids_overlap, ("9",))
        appended_two = grew + self.GOLDEN_TASK.format(id="10").encode("utf-8")
        ids_pair, _ = discover_completed_tasks(self._selection(grew, appended_two))
        self.assertEqual(ids_pair, ("10",))

    def test_existence_state_source_and_range_failures_are_engine_failures(self):
        from spec_contract import discover_completed_tasks

        done = self._make(self.GOLDEN_TASK.format(id="1"))
        selection = self._selection(None, done)
        selection = type(selection)(path=selection.path, before_exists=False,
                                    before_bytes=b"ghost", after_bytes=selection.after_bytes,
                                    changed_ranges=selection.changed_ranges, source="ci")
        ids, diags = discover_completed_tasks(selection)
        self.assertEqual(ids, ())
        self.assertTrue(any(d.code == "GATE_SNAPSHOT_MISSING" and d.verdict == "engine-fail" for d in diags))
        ids_src, diags_src = discover_completed_tasks(self._selection(done, done, source="totally-bogus"))
        self.assertEqual((ids_src, diags_src[0].code), ((), "GATE_SELECTION_SOURCE_INVALID"))
        bad_pair_done = self._make(self.GOLDEN_TASK.format(id="1"))
        bad_pair_after = self._make(self.GOLDEN_TASK.format(id="1"), self.GOLDEN_TASK.format(id="2"))
        bad_ranges, diags_bad = discover_completed_tasks(
            self._selection(bad_pair_done, bad_pair_after, changed=[]))
        self.assertEqual((bad_ranges, diags_bad[0].code), ((), "GATE_RANGE_INVALID"))

    def test_created_file_without_before_bytes_is_valid_selection(self):
        from spec_contract import discover_completed_tasks

        made = self._make(self.GOLDEN_TASK.format(id="1"))
        ids, diags = discover_completed_tasks(self._selection(None, made))
        self.assertEqual((ids, diags), (("1",), ()))


class EvidenceGateTest(unittest.TestCase):
    """Evidence v2 validator: distinct stable codes per REQ-3 failure class."""

    def _task_lines(self, evidence_block: str, *, complete: bool = True, task_id: str = "1") -> tuple[str, ...]:
        checkbox = "- [x]" if complete else "- [ ]"
        text = f"{checkbox} {task_id}. demo.\n{evidence_block}"
        import spec_contract as sc

        data = text.encode("utf-8")
        return tuple(sc.parse_task_blocks(data, __import__("pathlib").Path("tasks.md"))[0])

    def _ids_of(self, problems):
        return [problem.code for problem in problems]

    def test_golden_evidence_passes(self):
        import spec_contract as sc

        block = ("     Evidence:\n"
                 "       - test: `python3 -m unittest` -> Ran 5 tests; OK\n"
                 "       - viewports: n/a \u2014 tooling-only\n"
                 "       - deviations: none\n")
        tasks = self._task_lines(block)
        self.assertEqual(sc.validate_evidence(tasks, ["1"]), ())

    def test_sibling_evidence_cannot_satisfy_selected_task(self):
        import spec_contract as sc
        from pathlib import Path

        text = ("- [x] 1. first.\n"
                "     Evidence:\n"
                "       - test: `a` -> ok\n"
                "       - viewports: n/a \u2014 x\n"
                "       - deviations: none\n"
                "- [x] 2. second.\n")
        tasks, _ = sc.parse_task_blocks(text.encode(), Path("t.md"))
        problems = sc.validate_evidence(tasks, ["2"])
        self.assertEqual(self._ids_of(problems), ["EVIDENCE_MISSING"])
        self.assertEqual(sc.validate_evidence(tasks, []), ())

    def test_each_failure_class_has_distinct_code(self):
        import spec_contract as sc
        from pathlib import Path

        def build(evidence: str) -> str:
            return f"- [x] 1. demo.\n     Verify:\n       - `c`\n{evidence}"

        cases = {
            "EVIDENCE_COMMAND_MISSING": build("     Evidence:\n       - test: just text here\n"),
            "EVIDENCE_RESULT_MISSING": build("     Evidence:\n       - test: `cmd` with no arrow tail ->\n"),
            "EVIDENCE_VIEWPORTS_INVALID": build("     Evidence:\n       - test: `c` -> ok\n       - viewports: n/a\n"),
            "EVIDENCE_DEVIATIONS_MISSING": build("     Evidence:\n       - test: `c` -> ok\n       - viewports: n/a \u2014 x\n"),
            "EVIDENCE_UNFINISHED_MARKER": build("     Evidence:\n       - test: `c` -> ok TODO\n       - viewports: n/a \u2014 x\n       - deviations: none\n"),
            "EVIDENCE_PLANNED_ONLY": build("     Evidence:\n       - test: คาดว่าจะรัน c\n       - viewports: n/a \u2014 x\n       - deviations: none\n"),
            "EVIDENCE_MISSING": build("     Notes: nothing here\n"),
        }
        for expected_code, body in cases.items():
            tasks, parse_problems = sc.parse_task_blocks(body.encode(), Path("t.md"))
            self.assertFalse(parse_problems, body)
            problems = sc.validate_evidence(tasks, ["1"])
            self.assertIn(expected_code, self._ids_of(problems), (expected_code, self._ids_of(problems)))

    def test_ui_viewports_require_all_three_breakpoints(self):
        import spec_contract as sc
        from pathlib import Path

        good = ("Evidence:\n"
                "       - test: `c` -> ok\n"
                "       - viewports: verified 375 / 768 / 1440\n"
                "       - deviations: none\n")
        tasks, _ = sc.parse_task_blocks(f"- [x] 1. d.\n     {good}".encode(), Path("t.md"))
        self.assertEqual(sc.validate_evidence(tasks, ["1"]), ())
        partial = good.replace("verified 375 / 768 / 1440", "verified 375 and 768 only")
        tasks_partial, _ = sc.parse_task_blocks(f"- [x] 1. d.\n     Evidence:\n       - test: `c` -> ok\n       - viewports: {partial.split(chr(10))[0]}\n       - deviations: none".encode(), Path("t.md"))
        # rebuild cleanly: viewports value comes from partial string minus prefix
        line_value = "viewports: verified 375 and 768 only"
        tasks_partial, _ = sc.parse_task_blocks(
            f"- [x] 1. d.\n     Evidence:\n       - test: `c` -> ok\n       - {line_value}\n       - deviations: none".encode(),
            Path("t.md"),
        )
        problems = sc.validate_evidence(tasks_partial, ["1"])
        self.assertIn("EVIDENCE_VIEWPORTS_INVALID", self._ids_of(problems))


class GateEvidenceCliTest(unittest.TestCase):
    """Public CLI contract: gate evidence + diff-ranges envelopes และ exit mapping."""

    def setUp(self):
        self.module_root = Path(__file__).resolve().parents[1]
        self.tmp = Path(tempfile.mkdtemp(prefix="gate-evidence-cli-"))

    def tearDown(self):
        import shutil

        shutil.rmtree(self.tmp, ignore_errors=True)

    def _write(self, name: str, data: bytes) -> Path:
        target = self.tmp / name
        target.write_bytes(data)
        return target

    def _task_doc(self, tasks: tuple[str, ...]) -> bytes:
        head = "# t\n\n## Implementation tasks\n"
        return head.encode("utf-8") + "".join(tasks).encode("utf-8")

    GOLDEN = (
        "\n- [{mark}] 1. demo.\n"
        "     Satisfies: REQ-1.1\n"
        "     Verify:\n"
        "       - `x`\n"
        "     Evidence:\n"
        "       - test: `true` -> ran 1 tests; OK\n"
        "       - viewports: n/a \u2014 tooling-only\n"
        "       - deviations: none\n"
    )

    def _run_cli(self, argv: list[str]) -> tuple[int, dict]:
        env = dict(os.environ, PYTHONDONTWRITEBYTECODE="1")
        proc = subprocess.run(
            [sys.executable, str(self.module_root / "spec_contract.py"), *argv],
            capture_output=True, text=True, cwd=self.tmp, env=env,
        )
        try:
            payload = json.loads(proc.stdout)
        except json.JSONDecodeError:
            payload = {"_stdout": proc.stdout, "_stderr": proc.stderr}
        return proc.returncode, payload

    def _ranges_payload(self, before: bytes, after: bytes) -> Path:
        b_file = self._write("before.bin", before)
        a_file = self._write("after.bin", after)
        code, payload = self._run_cli(["diff-ranges", "--before-file", str(b_file), "--after-file", str(a_file)])
        self.assertEqual(code, 0, payload)
        out = self.tmp / "ranges.json"
        out.write_text(json.dumps({"ranges": payload["ranges"]}), encoding="utf-8")
        return out

    def test_allow_policy_and_engine_paths(self):
        before = self._task_doc((self.GOLDEN.replace("{mark}", " "),))
        after_ok = self._task_doc((self.GOLDEN.replace("{mark}", "x"),))
        ranges_ok = self._ranges_payload(before, after_ok)
        args = ["gate", "evidence", "--path", ".ai/specs/demo/tasks.md",
                "--after-file", self._write("after-ok.bin", after_ok).as_posix(),
                "--ranges-file", ranges_ok.as_posix(),
                "--before-file", self.tmp.joinpath("before.bin").as_posix(),
                "--source", "pre-commit"]
        code, payload = self._run_cli(args)
        self.assertEqual(code, 0, payload)
        self.assertEqual(payload["verdict"], "allow")

        bad_ev = self.GOLDEN.replace("- test: `true` -> ran 1 tests; OK", "- test: planned command only")
        after_bad = self._task_doc((bad_ev.replace("{mark}", "x"),))
        ranges_bad = self._ranges_payload(before, after_bad)
        code_bad, payload_bad = self._run_cli([
            "gate", "evidence", "--path", ".ai/specs/demo/tasks.md",
            "--after-file", self._write("after-bad.bin", after_bad).as_posix(),
            "--ranges-file", ranges_bad.as_posix(),
            "--before-file", self.tmp.joinpath("before.bin").as_posix(),
            "--source", "pre-commit"])
        self.assertEqual(code_bad, 1, payload_bad)
        codes = {diag["code"] for diag in payload_bad["diagnostics"]}
        self.assertTrue(codes & {"EVIDENCE_PLANNED_ONLY", "EVIDENCE_RESULT_MISSING"}, codes)

        stale = self.tmp / "stale-ranges.json"
        stale.write_text(json.dumps({"ranges": []}), encoding="utf-8")
        code_engine, payload_engine = self._run_cli([
            "gate", "evidence", "--path", ".ai/specs/demo/tasks.md",
            "--after-file", self._write("after-engine.bin", after_ok).as_posix(),
            "--ranges-file", stale.as_posix(),
            "--before-file", self.tmp.joinpath("before.bin").as_posix(),
            "--source", "pre-commit"])
        self.assertEqual(code_engine, 2, payload_engine)
        self.assertEqual(payload_engine["verdict"], "engine-fail")
        self.assertEqual(payload_engine["diagnostics"][0]["code"], "GATE_RANGE_INVALID")

    def test_before_missing_flag_rules(self):
        fresh = self._task_doc((self.GOLDEN.replace("{mark}", "x"),))
        ranges_fresh = self._canonical_empty(fresh)
        base = ["gate", "evidence", "--path", "p.md", "--after-file",
                self._write("fresh.bin", fresh).as_posix(),
                "--ranges-file", ranges_fresh.as_posix(), "--source", "ci"]
        code_new, payload_new = self._run_cli([*base, "--before-missing"])
        self.assertEqual(code_new, 0, payload_new)
        code_both, payload_both = self._run_cli([*base, "--before-missing", "--before-file", "/dev/null"])
        self.assertEqual(code_both, 2, payload_both)

    def _canonical_empty(self, data: bytes) -> Path:
        sys.path.insert(0, str(self.module_root))
        from spec_contract import canonical_changed_ranges

        ranges = [{"afterStart": r.after_start, "afterEnd": r.after_end,
                   "beforeStart": r.before_start, "beforeEnd": r.before_end}
                  for r in canonical_changed_ranges(b"", data)]
        out = self.tmp / "empty-base.json"
        out.write_text(json.dumps({"ranges": ranges}), encoding="utf-8")
        return out


class GateMutationTest(unittest.TestCase):
    """Mutation floor: parser shortcuts ต้องตาย ไม่ fail-open."""

    def load_module_with_mutated_source(self, replacements: list[tuple[str, str]]):
        source_path = Path(__file__).resolve().parents[1] / "spec_contract.py"
        mutated = source_path.read_text(encoding="utf-8")
        for old, new in replacements:
            self.assertIn(old, mutated, old)
            mutated = mutated.replace(old, new)
        namespace: dict[str, object] = {}
        exec(compile(mutated, "<mutated>", "exec"), namespace)
        return namespace

    def test_required_gate_mutations_are_killed(self):
        sample_before = b"# t\n\n- [ ] 1. demo.\n"
        sample_after = b"# t\n\n- [x] 1. demo.\n     Evidence:\n       - test: `c` -> ok\n       - viewports: n/a \xe2\x80\x94 x\n       - deviations: none\n"

        # M1: canonical diff ถูก disable -> range validation ต้อง block การเลือก
        from spec_contract import canonical_changed_ranges as real_ranges
        ns = self.load_module_with_mutated_source([
            ("matcher = difflib.SequenceMatcher(None, before_lines, after_lines, autojunk=False)",
             'matcher = type("M", (), {"get_opcodes": (lambda self: [("equal", 0, len(before_lines), 0, len(after_lines))])})()'),
        ])
        selection = ns["GateSelection"](path="t", before_exists=True, before_bytes=sample_before,
                                        after_bytes=sample_after, changed_ranges=real_ranges(sample_before, sample_after),
                                        source="pre-commit")
        ids, diags = ns["discover_completed_tasks"](selection)
        self.assertEqual(ids, (), "mutation must fail closed")
        self.assertTrue(any(d.code == "GATE_RANGE_INVALID" for d in diags))

        # M2: scan-all-completed shortcut -> pre-existing completions ต้องไม่ถูก reselect
        ns2 = self.load_module_with_mutated_source([
            ("if transitioned or after_only_opening_overlaps:",
             "if task.completed:"),
        ])
        sel2 = ns2["GateSelection"](path="t", before_exists=True, before_bytes=sample_after, after_bytes=sample_after,
                                    changed_ranges=(), source="ci")
        ids2, _ = ns2["discover_completed_tasks"](sel2)
        self.assertNotEqual(ids2, (), "sanity: mutation flips behavior visibly")
        from spec_contract import discover_completed_tasks as real_discover
        ids_real, _ = real_discover(sel2)
        self.assertEqual(ids_real, (), "real engine stays closed where the mutant re-selected")

        # M3: Evidence validator กลืน unfinished marker -> ต้องตายด้วย fixture TODO
        ns3 = self.load_module_with_mutated_source([
            ("return bool(_UNFINISHED_MARKER_RE.search(value))",
             "return False"),
        ])
        evidence_text = ("- [x] 1. demo.\n"
                         "     Evidence:\n"
                         "       - test: `c` -> ok\n"
                         "       - viewports: n/a \u2014 x\n"
                         "       - deviations: none TODO\n")
        import spec_contract as real_sc
        tasks_real, _ = real_sc.parse_task_blocks(evidence_text.encode(), Path("t.md"))
        self.assertEqual([p.code for p in real_sc.validate_evidence(tasks_real, ["1"])],
                         ["EVIDENCE_UNFINISHED_MARKER"])
        mutated_tasks, _ = ns3["parse_task_blocks"](evidence_text.encode(), Path("t.md"))
        problems_mutated = ns3["validate_evidence"](mutated_tasks, ["1"])
        self.assertEqual([], [problem.code for problem in problems_mutated],
                         "sanity: mutation flips detection visibly")


class Task5ConsumerTest(unittest.TestCase):
    """REQ-4.1-4.6: shared consumers ใช้ exact string IDs + task graph verdict เดิม."""

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="task5-ids-"))
        root = self.tmp / ".ai" / "specs" / "demo"
        root.mkdir(parents=True)
        (root / "tasks.md").write_text(
            "> Status: approved 2026-08-25\n"
            "\n"
            "- [x] A1. first done.\n"
            "     Satisfies: REQ-1.1\n"
            "- [ ] migration-2. second pending.\n"
            "     Satisfies: REQ-1.1\n"
            "- [ ] zz. last pending.\n",
            encoding="utf-8",
        )

    def tearDown(self):
        import shutil

        shutil.rmtree(self.tmp, ignore_errors=True)

    def _run(self, *argv: str) -> tuple[int, str]:
        proc = subprocess.run(
            [sys.executable, str(SCRIPTS / "spec_contract.py"), *argv],
            capture_output=True, text=True,
        )
        return proc.returncode, proc.stdout

    def test_pending_lines_preserve_case_and_file_order(self):
        rc, out = self._run("task-ids", "--feature", "demo", "--pending",
                            "--specs-root", str(self.tmp / ".ai" / "specs"))
        self.assertEqual(rc, 0)
        self.assertEqual(out.split(), ["migration-2", "zz"])

    def test_all_includes_completed_exact_case(self):
        rc, out = self._run("task-ids", "--feature", "demo",
                            "--specs-root", str(self.tmp / ".ai" / "specs"))
        self.assertEqual(rc, 0)
        self.assertEqual(out.split(), ["A1", "migration-2", "zz"])

    def test_json_envelope_sorted_keys(self):
        rc, out = self._run("task-ids", "--feature", "demo", "--pending", "--format", "json",
                            "--specs-root", str(self.tmp / ".ai" / "specs"))
        self.assertEqual(rc, 0)
        payload = json.loads(out)
        self.assertEqual(payload["schemaVersion"], 1)
        self.assertEqual(payload["taskIds"], ["migration-2", "zz"])

    def test_unknown_dependency_and_cycle_still_reject(self):
        bad_cycle = self.tmp / ".ai" / "specs" / "cycle"
        bad_cycle.mkdir()
        (bad_cycle / "tasks.md").write_text(
            "> Status: approved 2026-08-25\n"
            "- [ ] a. x.\n     Depends on: b\n"
            "- [ ] b. y.\n     Depends on: a\n",
            encoding="utf-8",
        )
        rc, _out = self._run("task-ids", "--feature", "cycle",
                             "--specs-root", str(self.tmp / ".ai" / "specs"))
        self.assertEqual(rc, 1)

    def test_cost_lib_widened_id_regex_is_string_safe(self):
        sys.path.insert(0, str(SCRIPTS))
        import cost_lib

        row = "- [x] migration-2. migrated.\n"
        match = re.match(cost_lib.TASKS_CHECKBOX_RE, row)
        self.assertIsNotNone(match)
        self.assertEqual(match.group(1), "migration-2")
        self.assertFalse(hasattr(cost_lib, "TASK_ID_NUMERIC"))


class Task5EvidenceMarkerBoundaryTest(unittest.TestCase):
    """Real-use defect: CLI flags ใน Evidence ต้องไม่ trigger unfinished-marker."""

    def _evidence_problems(self, observation: str):
        import spec_contract as sc
        from pathlib import Path

        body = ("- [x] 1. demo.\n"
                "     Evidence:\n"
                f"       - test: {observation}\n"
                "       - viewports: n/a \u2014 tooling-only\n"
                "       - deviations: none\n")
        tasks, _problems = sc.parse_task_blocks(body.encode(), Path("t.md"))
        return [diag.code for diag in sc.validate_evidence(tasks, ["1"])]

    def test_flags_with_markers_pass(self):
        self.assertEqual(
            self._evidence_problems("`x --pending` -> ran 5 tests; OK"),
            [],
        )

    def test_bare_todos_still_blocked(self):
        codes = self._evidence_problems("`x` -> TODO upstream")
        self.assertIn("EVIDENCE_UNFINISHED_MARKER", codes)
        codes2 = self._evidence_problems("`x` -> PENDING review")
        self.assertIn("EVIDENCE_UNFINISHED_MARKER", codes2)


class Task5EvidenceMarkerScopeTest(unittest.TestCase):
    """Marker scanning covers result/value segments; command text is data."""

    def _problems(self, observation: str):
        import spec_contract as sc
        from pathlib import Path

        body = ("- [x] 1. demo.\n"
                "     Evidence:\n"
                f"       - test: {observation}\n"
                "       - viewports: n/a \u2014 tooling-only\n"
                "       - deviations: none\n")
        tasks, _p = sc.parse_task_blocks(body.encode(), Path("t.md"))
        return [d.code for d in sc.validate_evidence(tasks, ["1"])]

    def test_pending_in_command_part_is_allowed(self):
        self.assertEqual(self._problems("`run --pending x` -> 5 tests; OK"), [])

    def test_marker_later_in_result_prose_is_allowed(self):
        self.assertEqual(
            self._problems("`x` -> pass=8 fail=0; engine error, zero pending tasks"),
            [],
        )

    def test_strong_marker_later_in_result_still_blocks(self):
        for marker in ("TODO", "TBD", "???"):
            with self.subTest(marker=marker):
                codes = self._problems(f"`x` -> done later {marker}")
                self.assertIn("EVIDENCE_UNFINISHED_MARKER", codes)

    def test_first_marker_token_still_blocked(self):
        for result in ("TODO upstream", "(owner) — TBD", "[linux] pending review", "??? unknown"):
            with self.subTest(result=result):
                codes = self._problems(f"`x` -> {result}")
                self.assertIn("EVIDENCE_UNFINISHED_MARKER", codes)


class StrictAllSpecCoverageTest(unittest.TestCase):
    """F-4/B-6: strict all-spec ต้องตรวจ direct directory ทุกตัวจริง."""

    def _write_feature(self, directory: Path) -> None:
        directory.mkdir(parents=True, exist_ok=True)
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

    def _run(self, root: Path) -> subprocess.CompletedProcess:
        return subprocess.run(
            [
                sys.executable,
                str(SCRIPTS / "spec_contract.py"),
                "check",
                "--all",
                "--strict",
                "--specs-root",
                str(root),
            ],
            capture_output=True,
            text=True,
            check=False,
            env=dict(os.environ, PYTHONDONTWRITEBYTECODE="1"),
        )

    def _parent_symlink_fixture(self, raw: str, source: str | None = None) -> tuple[Path, Path]:
        repo = Path(raw) / "repo"
        scripts = repo / "scripts"
        scripts.mkdir(parents=True)
        (scripts / "spec_contract.py").write_text(
            source or (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8"),
            encoding="utf-8",
        )
        external_ai = Path(raw) / "external-ai"
        self._write_feature(external_ai / "specs" / "external-feature")
        (repo / ".ai").symlink_to(external_ai, target_is_directory=True)
        return repo, scripts / "spec_contract.py"

    def _parent_symlink_callers(self, repo: Path, script: Path) -> tuple[tuple[str, ...], ...]:
        specs = repo / ".ai" / "specs"
        return (
            ("check", "--all", "--strict", "--specs-root", str(specs)),
            ("check", "--feature", "external-feature", "--strict"),
            ("gate", "phase", "--feature", "external-feature", "--phase", "implement", "--workflow", "requirements-first"),
            ("slice", "--feature", "external-feature", "--task", "1"),
            ("state", "--all", "--format", "summary"),
            ("state", "--feature", "external-feature"),
            ("task-ids", "--feature", "external-feature", "--all", "--specs-root", str(specs)),
        )

    def test_parent_ai_symlink_is_engine_failure_for_every_public_reader(self):
        with tempfile.TemporaryDirectory(prefix="strict-parent-link-") as raw:
            repo, script = self._parent_symlink_fixture(raw)
            for argv in self._parent_symlink_callers(repo, script):
                with self.subTest(argv=argv):
                    proc = subprocess.run(
                        [sys.executable, str(script), *argv],
                        cwd=repo,
                        capture_output=True,
                        text=True,
                        check=False,
                        env=dict(os.environ, PYTHONDONTWRITEBYTECODE="1"),
                    )
                    self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
                    self.assertIn("ENGINE_INTERNAL", proc.stdout + proc.stderr)
                    self.assertNotIn("external-feature' เกณฑ์", proc.stdout)

    def test_parent_ai_component_mutation_is_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")
        before = '    for current in (repo_root, repo_root / ".ai", candidate):\n'
        self.assertEqual(1, source.count(before))
        mutated = source.replace(
            before,
            '    for current in (repo_root, candidate):\n',
        )
        with tempfile.TemporaryDirectory(prefix="strict-parent-mutant-") as raw:
            repo, script = self._parent_symlink_fixture(raw, mutated)
            proc = subprocess.run(
                [sys.executable, str(script), "check", "--feature", "external-feature", "--strict"],
                cwd=repo,
                capture_output=True,
                text=True,
                check=False,
                env=dict(os.environ, PYTHONDONTWRITEBYTECODE="1"),
            )
            self.assertEqual(0, proc.returncode, "sanity: mutation ต้องเปิด external canonical tree")

    def test_broken_ledger_residual_and_shape_less_directory_are_checked(self):
        with tempfile.TemporaryDirectory(prefix="strict-all-") as raw:
            root = Path(raw) / ".ai" / "specs"
            self._write_feature(root / "alpha-valid")
            self._write_feature(root / "sdd-operating-layer-parity")
            broken = root / "gamma-legacy"
            broken.mkdir(parents=True)
            (broken / "requirements.md").write_text(
                "# broken legacy without canonical chain\n", encoding="utf-8"
            )
            shape_less = root / "shape-less"
            shape_less.mkdir()
            (shape_less / "notes.md").write_text("not a canonical artifact\n", encoding="utf-8")
            ledger = root / "sdd-operating-layer-parity" / "migration-resolutions.json"
            ledger.write_text(
                json.dumps({
                    "decisions": [{
                        "path": ".ai/specs/gamma-legacy/design.md",
                        "field": "trace.table",
                        "taskId": "",
                        "disposition": "trace-header-canonical",
                    }]
                }),
                encoding="utf-8",
            )

            proc = self._run(root)

            self.assertEqual(1, proc.returncode, proc.stdout + proc.stderr)
            for feature in ("alpha-valid", "gamma-legacy", "shape-less", "sdd-operating-layer-parity"):
                self.assertIn(f"::group::strict-trace {feature}", proc.stdout)
            self.assertIn("check --all --strict: 4 checked / 2 failing / 0 unchecked", proc.stdout)
            self.assertNotIn("legacy-residual", proc.stdout)

    def test_directory_file_and_broken_symlinks_are_all_spec_inventory(self):
        with tempfile.TemporaryDirectory(prefix="strict-symlinks-") as raw:
            sandbox = Path(raw)
            root = sandbox / ".ai" / "specs"
            root.mkdir(parents=True)
            external_directory = sandbox / "external-directory"
            external_directory.mkdir()
            external_file = sandbox / "external-file"
            external_file.write_text("not a spec directory\n", encoding="utf-8")
            (root / "future-dir-link").symlink_to(external_directory, target_is_directory=True)
            (root / "future-file-link").symlink_to(external_file)
            (root / "future-broken-link").symlink_to(sandbox / "missing-target")

            proc = self._run(root)

            self.assertEqual(1, proc.returncode, proc.stdout + proc.stderr)
            for feature in ("future-dir-link", "future-file-link", "future-broken-link"):
                self.assertIn(f"::group::strict-trace {feature}", proc.stdout)
            self.assertIn("check --all --strict: 3 checked / 3 failing / 0 unchecked", proc.stdout)

    def test_invalid_utf8_diagnostic_is_unchecked_engine_failure(self):
        with tempfile.TemporaryDirectory(prefix="strict-invalid-utf8-") as raw:
            root = Path(raw) / ".ai" / "specs"
            directory = root / "invalid-utf8"
            directory.mkdir(parents=True)
            (directory / "requirements.md").write_bytes(b"\xff\xfe")
            (directory / "design.md").write_text(
                "> Status: approved 2026-08-28\n", encoding="utf-8"
            )
            (directory / "tasks.md").write_text(
                "> Status: approved 2026-08-28\n", encoding="utf-8"
            )

            proc = self._run(root)

            self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
            self.assertIn("ENGINE_INTERNAL", proc.stdout + proc.stderr)
            self.assertIn(
                "check --all --strict: 0 checked / 0 failing / 1 unchecked",
                proc.stdout,
            )
            self.assertNotIn("Traceback", proc.stdout + proc.stderr)

    def test_symlinked_specs_root_is_unchecked_engine_failure(self):
        with tempfile.TemporaryDirectory(prefix="strict-root-link-") as raw:
            sandbox = Path(raw)
            external = sandbox / "external-specs"
            self._write_feature(external / "outside")
            linked_root = sandbox / ".ai" / "specs"
            linked_root.parent.mkdir(parents=True)
            linked_root.symlink_to(external, target_is_directory=True)

            proc = self._run(linked_root)

            self.assertEqual(2, proc.returncode, proc.stdout + proc.stderr)
            self.assertIn("ENGINE_INTERNAL", proc.stdout + proc.stderr)
            self.assertNotIn("Traceback", proc.stdout + proc.stderr)

    def test_validator_exception_is_unchecked_and_keeps_nonzero(self):
        with tempfile.TemporaryDirectory(prefix="strict-unchecked-") as raw:
            root = Path(raw) / ".ai" / "specs"
            (root / "alpha").mkdir(parents=True)
            (root / "beta").mkdir()
            outcomes = iter((0, OSError("unreadable")))

            def probe(_feature, _root):
                outcome = next(outcomes)
                if isinstance(outcome, Exception):
                    raise outcome
                return outcome

            with patch.object(spec_contract, "trace_run", side_effect=probe), \
                    contextlib.redirect_stdout(io.StringIO()) as output:
                rc = spec_contract._check_all_strict(root)
            self.assertEqual(2, rc)
            self.assertIn("check --all --strict: 1 checked / 0 failing / 1 unchecked", output.getvalue())

    def test_engine_diagnostic_tri_state_mutation_is_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")
        before = (
            '    return 2 if any(diagnostic.verdict == "engine-fail" '
            'for diagnostic in diagnostics) else 1\n'
        )
        self.assertEqual(2, source.count(before))
        namespace: dict[str, object] = {}
        exec(
            compile(source.replace(before, "    return 1\n"), "<tri-state-mutant>", "exec"),
            namespace,
        )
        with tempfile.TemporaryDirectory(prefix="strict-tri-mutant-") as raw:
            root = Path(raw) / ".ai" / "specs"
            directory = root / "invalid-utf8"
            directory.mkdir(parents=True)
            (directory / "requirements.md").write_bytes(b"\xff\xfe")
            (directory / "design.md").write_text(
                "> Status: approved 2026-08-28\n", encoding="utf-8"
            )
            (directory / "tasks.md").write_text(
                "> Status: approved 2026-08-28\n", encoding="utf-8"
            )
            with contextlib.redirect_stdout(io.StringIO()):
                rc = namespace["_check_all_strict"](root)
        self.assertEqual(1, rc, "sanity: tri-state mutation ต้องทำ engine failure เป็น policy failure")

    def test_shape_filter_and_ledger_skip_mutations_are_killed(self):
        source = (SCRIPTS / "spec_contract.py").read_text(encoding="utf-8")
        mutations = (
            (
                "directories = _all_spec_directories(specs_dir)",
                "directories = tuple(path for path in _all_spec_directories(specs_dir) if (path / 'requirements.md').is_file() or (path / 'bugfix.md').is_file())",
            ),
            (
                "for directory in directories:",
                "for directory in tuple(directory for directory in directories if directory.name not in ledger_legacy_features(specs_dir)):",
            ),
        )
        for index, (before, after) in enumerate(mutations):
            with self.subTest(mutation=index):
                self.assertEqual(1, source.count(before))
                namespace: dict[str, object] = {}
                exec(compile(source.replace(before, after), f"<strict-mutant-{index}>", "exec"), namespace)
                with tempfile.TemporaryDirectory(prefix="strict-mutant-") as raw:
                    root = Path(raw)
                    (root / "requirements-shape").mkdir()
                    (root / "requirements-shape" / "requirements.md").write_text("x", encoding="utf-8")
                    (root / "shape-less").mkdir()
                    owner = root / "sdd-operating-layer-parity"
                    owner.mkdir()
                    (owner / "migration-resolutions.json").write_text(
                        json.dumps({"decisions": [{
                            "path": ".ai/specs/requirements-shape/design.md",
                            "field": "trace.table",
                            "disposition": "trace-header-canonical",
                        }]}),
                        encoding="utf-8",
                    )
                    called: list[str] = []
                    namespace["trace_run"] = lambda feature, _root: called.append(feature) or 1
                    with contextlib.redirect_stdout(io.StringIO()):
                        namespace["_check_all_strict"](root)
                    self.assertNotEqual(
                        called,
                        ["requirements-shape", "sdd-operating-layer-parity", "shape-less"],
                        "sanity: mutation ต้องเปลี่ยน invocation inventory",
                    )
