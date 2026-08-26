#!/usr/bin/env python3
import contextlib
import importlib.util
import io
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
from spec_trace import run as trace_run
from spec_contract import (
    check_phase_gate,
    parse_bugfix_criteria,
    parse_requirement_criteria,
    parse_status,
    parse_task_blocks,
    parse_traceability_table,
    resolve_task_selector,
    trace_run as strict_trace_run,
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
        with tempfile.TemporaryDirectory() as raw:
            directory = Path(raw)
            approved = "> Status: approved 2026-08-25\n"
            self.write(directory, "design.md", approved)
            _, diagnostics = check_phase_gate(directory, "requirements", "design-first")
            self.assertEqual(set(), self.codes(diagnostics))
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
        self.assertEqual(52, len(features))
        self.assertEqual([], failures)

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
        self.assertGreaterEqual(len(decimal_characters), 700)
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


if __name__ == "__main__":
    unittest.main()
