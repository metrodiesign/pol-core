#!/usr/bin/env python3
"""Task 8 alignment fixtures: every row must go RED when one invariant is
mutated inside a temporary tree, and stay GREEN on a faithfully copied tree.
Plus REQ-8.13 unverified-record schema fixtures (Docker/SQL/generic)."""
from __future__ import annotations

import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent.parent
_spec = importlib.util.spec_from_file_location(
    "rpa", SCRIPTS / "repo_policy_alignment.py")
rpa = importlib.util.module_from_spec(_spec)
sys.modules["rpa"] = rpa
_spec.loader.exec_module(rpa)

REPO = SCRIPTS.parent

COPY_ROWS = {
    "modules": [".ai/shared/ARCHITECTURE.md"],
    "dbcontexts": [".ai/shared/ARCHITECTURE.md"],
    "ci_jobs": [".ai/shared/ARCHITECTURE.md", ".github/workflows/ci.yml",
                ".gitlab-ci.yml"],
}


class AlignmentFixtureBase(unittest.TestCase):
    """Sandbox copies ONLY the files a row reads; the rest is synthesized."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory(prefix="align-")
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name)
        import os
        self.prev_env = os.environ.get("SDD_ALIGNMENT_REPO")
        os.environ["SDD_ALIGNMENT_REPO"] = str(self.root)

    def tearDown(self):
        import os
        if self.prev_env is None:
            os.environ.pop("SDD_ALIGNMENT_REPO", None)
        else:
            os.environ["SDD_ALIGNMENT_REPO"] = self.prev_env

    def copy_rel(self, rel: str) -> Path:
        target = self.root / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(REPO / rel, target)
        return target


class SandboxBuilder:
    """Compose the minimal real-shaped tree each row needs."""

    def __init__(self, root: Path):
        self.root = root

    def architecture_with_registry(self, modules=(), contexts=(), ci=()):
        arch = self.root / ".ai/shared/ARCHITECTURE.md"
        arch.parent.mkdir(parents=True, exist_ok=True)
        parts = [rpa.REGISTRY_HEADING]
        if modules is not None:
            parts.append("### Modules\n" + "".join(f"| `{m}` | x |\n"
                                                   for m in modules))
        if contexts is not None:
            parts.append("### Runtime DbContexts\n" + "".join(
                f"| `{c}` | x |\n" for c in contexts))
        if ci is not None:
            parts.append("### CI topology\n" + "".join(
                f"| `{j}` | x |\n" for j in ci))
        arch.write_text("\n".join(parts), encoding="utf-8")

    def module_dir(self, name: str, with_csproj: bool = True) -> Path:
        base = self.root / "src/Modules" / name
        base.mkdir(parents=True, exist_ok=True)
        if with_csproj:
            (base / f"{name}.csproj").write_text("<Project />", encoding="utf-8")
        return base

    def persistence_context(self, project: str, cls: str,
                            guard_base: bool = False,
                            query_filter_cfg: bool = False) -> Path:
        ctx = self.root / "src/Persistence" / project / f"{cls}.cs"
        ctx.parent.mkdir(parents=True, exist_ok=True)
        body = f"class {cls} " + (" : GuardedRuntimeDbContext " if guard_base else "") + "{}"
        ctx.write_text(body, encoding="utf-8")
        if query_filter_cfg:
            cfg = ctx.parent / f"{cls[:4]}Cfg.cs"
            cfg.write_text("builder.HasQueryFilter(e => e.Id > 0);",
                           encoding="utf-8")
        return ctx

    def persistence_infra(self, snapshot: bool = True) -> None:
        base = self.root / rpa._PERSISTENCE_BASE
        base.mkdir(parents=True, exist_ok=True)
        (base / "PolDbContext.cs").write_text("class PolDbContext {}",
                                              encoding="utf-8")
        if snapshot:
            migrations = base / "Migrations"
            migrations.mkdir(exist_ok=True)
            (migrations / "PolDbContextModelSnapshot.cs").write_text(
                "// snapshot", encoding="utf-8")

    def isolation_tree(self) -> None:
        self.persistence_context("Persistence.ControlPlane", "ControlPlaneDbContext",
                                 guard_base=True)
        self.persistence_context("Persistence.MerchantUsers", "MerchantUserDbContext",
                                 guard_base=True, query_filter_cfg=True)
        self.persistence_context("Persistence.MerchantRuntime",
                                 "MerchantRuntimeDbContext",
                                 guard_base=True, query_filter_cfg=True)
        tests = self.root / "tests/Architecture.Tests"
        tests.mkdir(parents=True, exist_ok=True)
        (tests / "ModelDisjointnessTests.cs").write_text("class X {}",
                                                         encoding="utf-8")
        guard = self.root / rpa._PERSISTENCE_BASE / "GuardedRuntimeDbContext.cs"
        guard.parent.mkdir(parents=True, exist_ok=True)
        guard.write_text("abstract class GuardedRuntimeDbContext {}",
                         encoding="utf-8")

    def workflows(self, github=("verify", "dotnet", "docker-build",
                                "dotnet-integration"),
                  gitlab=("verify", "dotnet", "integration", "package",
                          "deploy-uat", "deploy-prod")) -> None:
        wf = self.root / ".github/workflows/ci.yml"
        wf.parent.mkdir(parents=True, exist_ok=True)
        lines = ["on:", "jobs:"]
        for job in github:
            lines.append(f"  {job}:")
            lines.append("    runs-on: ubuntu-latest")
        wf.write_text("\n".join(lines) + "\n", encoding="utf-8")
        gl = self.root / ".gitlab-ci.yml"
        gl_lines = ["stages:", "  - build"]
        for job in gitlab:
            gl_lines.append(job + ":")
            gl_lines.append("  script: true")
        gl_lines.append(".hidden-template:")
        gl.write_text("\n".join(gl_lines) + "\n", encoding="utf-8")

    def handoff_pair(self, headings=("Task Summary", "Current Status")) -> None:
        proto = self.root / ".ai/shared/AGENT_HANDOFF_PROTOCOL.md"
        proto.parent.mkdir(parents=True, exist_ok=True)
        block = "\n".join(f"## {h}\nbody" for h in headings)
        proto.write_text("# P\n\n## Schema\n\n```\nintro\n" + block +
                         "\n```\n", encoding="utf-8")
        template = self.root / ".ai/templates/handoff-note-template.md"
        template.parent.mkdir(parents=True, exist_ok=True)
        template.write_text("\n".join(f"## {h}" for h in headings),
                            encoding="utf-8")

    def boundary_docs(self, develop_in_hooks: bool = True) -> None:
        push = self.root / ".githooks/pre-push"
        push.parent.mkdir(parents=True, exist_ok=True)
        refs = ["refs/heads/main"] + (["refs/heads/develop"]
                                      if develop_in_hooks else [])
        push.write_text(f"PROTECTED='{' '.join(refs)}'\n"
                        "# non-fast-forward blocks\n", encoding="utf-8")
        sec = self.root / ".ai/shared/SECURITY_RULES.md"
        sec.parent.mkdir(parents=True, exist_ok=True)
        sec.write_text("Tier rules with Tier 1 floor", encoding="utf-8")
        taskp = self.root / ".ai/shared/TASK_PROTOCOL.md"
        taskp.parent.mkdir(parents=True, exist_ok=True)
        taskp.write_text("Never push to `main` / `develop` directly.",
                         encoding="utf-8")
        dguard = self.root / ".ai/bin/check-destructive.sh"
        dguard.parent.mkdir(parents=True, exist_ok=True)
        dguard.write_text("# blocks destructive commands", encoding="utf-8")


MODULE_SET = ("Admins", "Carts", "Orders")
CONTEXT_SET = ("ControlPlaneDbContext", "MerchantUserDbContext",
               "MerchantRuntimeDbContext")
CI_SET = ("github:verify", "github:dotnet", "github:docker-build",
          "github:dotnet-integration", "gitlab:verify", "gitlab:dotnet",
          "gitlab:integration", "gitlab:package", "gitlab:deploy-uat",
          "gitlab:deploy-prod")


class ModulesRowTest(AlignmentFixtureBase):

    def build_valid(self):
        s = SandboxBuilder(self.root)
        s.architecture_with_registry(modules=MODULE_SET)
        for name in MODULE_SET:
            s.module_dir(name)
        (self.root / "src/Modules/Checkouts").mkdir(parents=True)

    def test_positive_aligned_and_extractor_not_empty(self):
        self.build_valid()
        self.assertEqual(rpa.check_modules(self.root), [])

    def test_fake_module_on_disk_fails(self):
        self.build_valid()
        s = SandboxBuilder(self.root)
        s.module_dir("FakeModule")
        diags = rpa.check_modules(self.root)
        self.assertTrue(any(d.code == "ALIGN_MODULES_MISMATCH"
                            and "FakeModule" in d.message for d in diags))

    def test_csproj_in_retired_container_counts_as_module(self):
        self.build_valid()
        s = SandboxBuilder(self.root)
        s.module_dir("Checkouts")  # retired container gains a csproj
        diags = rpa.check_modules(self.root)
        self.assertTrue(any(d.code == "ALIGN_MODULES_MISMATCH"
                            and "Checkouts" in d.message for d in diags))

    def test_missing_doc_row_fails(self):
        self.build_valid()
        s = SandboxBuilder(self.root)
        s.architecture_with_registry(modules=MODULE_SET[:-1])
        diags = rpa.check_modules(self.root)
        self.assertTrue(all(d.code == "ALIGN_MODULES_MISMATCH" for d in diags)
                        and diags)


class DbContextsRowTest(AlignmentFixtureBase):

    def build_valid(self):
        s = SandboxBuilder(self.root)
        s.persistence_infra(snapshot=False)
        s.architecture_with_registry(contexts=CONTEXT_SET)
        s.persistence_context("Persistence.ControlPlane", "ControlPlaneDbContext")
        s.persistence_context("Persistence.MerchantUsers", "MerchantUserDbContext")
        s.persistence_context("Persistence.MerchantRuntime",
                              "MerchantRuntimeDbContext")

    def test_positive(self):
        self.build_valid()
        self.assertEqual(rpa.check_dbcontexts(self.root), [])

    def test_extra_pol_runtime_context_fails(self):
        self.build_valid()
        s = SandboxBuilder(self.root)
        s.persistence_context("Persistence.ControlPlane", "PolDbContext")
        diags = rpa.check_dbcontexts(self.root)
        self.assertTrue(any(d.code == "ALIGN_DBCONTEXTS_MISMATCH"
                            and "PolDbContext" in d.message for d in diags))

    def test_removed_context_fails_both_ways(self):
        self.build_valid()
        s = SandboxBuilder(self.root)
        s.architecture_with_registry(contexts=CONTEXT_SET[:2])
        diags = rpa.check_dbcontexts(self.root)
        self.assertTrue(any(d.code == "ALIGN_DBCONTEXTS_MISMATCH"
                            and "MerchantRuntimeDbContext" in d.message
                            for d in diags))


class MigrationOwnerRowTest(AlignmentFixtureBase):

    def test_positive(self):
        SandboxBuilder(self.root).persistence_infra()
        self.assertEqual(rpa.check_migration_owner(self.root), [])

    def test_missing_snapshot_fails(self):
        SandboxBuilder(self.root).persistence_infra(snapshot=False)
        diags = rpa.check_migration_owner(self.root)
        codes = {d.code for d in diags}
        self.assertIn("ALIGN_MIGRATION_OWNER_MISMATCH", codes)

    def test_runtime_registration_fails(self):
        s = SandboxBuilder(self.root)
        s.persistence_infra()
        stray = s.root / "src/Hosts/Api/Stray.cs"
        stray.parent.mkdir(parents=True, exist_ok=True)
        stray.write_text("services.AddDbContext<PolDbContext>();",
                         encoding="utf-8")
        diags = rpa.check_migration_owner(self.root)
        self.assertTrue(any("register" in d.message for d in diags))


class IsolationRowTest(AlignmentFixtureBase):

    def test_positive(self):
        SandboxBuilder(self.root).isolation_tree()
        self.assertEqual(rpa.check_isolation(self.root), [])

    def test_unsealed_context_fails(self):
        s = SandboxBuilder(self.root)
        s.isolation_tree()
        victim = (s.root / "src/Persistence/Persistence.MerchantUsers/"
                  "MerchantUserDbContext.cs")
        victim.write_text("class MerchantUserDbContext {}", encoding="utf-8")
        diags = rpa.check_isolation(self.root)
        self.assertTrue(any(d.code == "ALIGN_ISOLATION_MISMATCH"
                            and "MerchantUsers" in d.message for d in diags))

    def test_no_filter_config_fails(self):
        s = SandboxBuilder(self.root)
        s.isolation_tree()
        for cfg in (s.root / "src/Persistence/Persistence.MerchantRuntime").glob("*Cfg.cs"):
            cfg.unlink()
        diags = rpa.check_isolation(self.root)
        self.assertTrue(any(d.code == "ALIGN_ISOLATION_MISMATCH"
                            and "MerchantRuntime" in d.message for d in diags))


class CiJobsRowTest(AlignmentFixtureBase):

    def build_valid(self):
        s = SandboxBuilder(self.root)
        s.workflows()
        s.architecture_with_registry(ci=CI_SET)

    def test_positive_real_shapes(self):
        self.copy_rel(".ai/shared/ARCHITECTURE.md")
        self.copy_rel(".github/workflows/ci.yml")
        self.copy_rel(".gitlab-ci.yml")
        self.assertEqual(rpa.check_ci_jobs(rpa.repo_root()), [])

    def test_positive_synthethic(self):
        self.build_valid()
        self.assertEqual(rpa.check_ci_jobs(self.root), [])

    def test_renamed_job_fails(self):
        self.build_valid()
        s = SandboxBuilder(self.root)
        text = (s.root / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        (s.root / ".github/workflows/ci.yml").write_text(
            text.replace("  dotnet-integration:\n", "  integration:\n"),
            encoding="utf-8")
        diags = rpa.check_ci_jobs(s.root)
        self.assertTrue(any(d.code == "ALIGN_CI_JOBS_MISMATCH" for d in diags))

    def test_docs_drift_fails(self):
        self.build_valid()
        s = SandboxBuilder(self.root)
        s.architecture_with_registry(ci=CI_SET[:4])
        diags = rpa.check_ci_jobs(s.root)
        self.assertTrue(any("gitlab" in d.message for d in diags))


class HandoffSchemaRowTest(AlignmentFixtureBase):

    def test_positive(self):
        headings = ("A", "B")
        # need distinct valid H2 names; reuse builder then patch names
        s = SandboxBuilder(self.root)
        s.handoff_pair(("Task Summary", "Next Steps"))
        self.assertEqual(rpa.check_handoff_schema(self.root), [])

    def test_reordered_headings_fail(self):
        s = SandboxBuilder(self.root)
        s.handoff_pair(("Task Summary", "Next Steps"))
        tpl = s.root / ".ai/templates/handoff-note-template.md"
        tpl.write_text("## Next Steps\n## Task Summary", encoding="utf-8")
        diags = rpa.check_handoff_schema(self.root)
        self.assertTrue(d.code == "ALIGN_HANDOFF_SCHEMA_MISMATCH"
                        for d in diags) and self.assertTrue(diags)

    def test_cardinality_drift_fails(self):
        s = SandboxBuilder(self.root)
        s.handoff_pair(("Task Summary", "Next Steps"))
        tpl = s.root / ".ai/templates/handoff-note-template.md"
        tpl.write_text(tpl.read_text(encoding="utf-8") + "\n## Extra Section",
                       encoding="utf-8")
        diags = rpa.check_handoff_schema(self.root)
        self.assertTrue(any(d.code == "ALIGN_HANDOFF_SCHEMA_MISMATCH"
                            for d in diags))


class GitBoundaryRowTest(AlignmentFixtureBase):

    def test_positive_synthetic(self):
        SandboxBuilder(self.root).boundary_docs()
        self.assertEqual(rpa.check_git_boundary(self.root), [])

    def test_positive_real_repo(self):
        self.copy_rel(".githooks/pre-push")
        self.copy_rel(".ai/shared/TASK_PROTOCOL.md")
        self.copy_rel(".ai/shared/SECURITY_RULES.md")
        self.copy_rel(".ai/bin/check-destructive.sh")
        self.assertEqual(rpa.check_git_boundary(rpa.repo_root()), [])

    def test_removing_develop_ref_fails(self):
        SandboxBuilder(self.root).boundary_docs(develop_in_hooks=False)
        diags = rpa.check_git_boundary(self.root)
        self.assertTrue(any("refs/heads/develop" in d.message for d in diags))


class PhaseSkillsRowTest(AlignmentFixtureBase):
    SKILLS = (
        "spec-requirements",
        "spec-design",
        "spec-tasks",
        "spec-implement",
        "spec-quick",
    )
    IMPLEMENT_GATE = (
        "python3 scripts/spec_contract.py gate phase --feature <feature> "
        "--phase implement --workflow <workflow>")
    TASK_IDS_SELECTOR = (
        "python3 scripts/spec_contract.py task-ids --feature <feature> "
        "--selector \"$ARGUMENTS\" --format lines")
    TASK_IDS_PENDING = (
        "python3 scripts/spec_contract.py task-ids --feature <feature> "
        "--pending --format lines")
    COMMAND_PATHS = (
        ("spec-requirements",
         "python3 scripts/spec_contract.py gate phase --feature <feature> "
         "--phase requirements --workflow design-first"),
        ("spec-design",
         "python3 scripts/spec_contract.py gate phase --feature <feature> "
         "--phase design --workflow requirements-first"),
        ("spec-design",
         "python3 scripts/spec_contract.py gate phase --feature <feature> "
         "--phase design --workflow design-first"),
        ("spec-tasks",
         "python3 scripts/spec_contract.py gate phase --feature <feature> "
         "--phase tasks --workflow <workflow>"),
        ("spec-implement", IMPLEMENT_GATE),
        ("spec-implement", "scripts/spec-slice.sh <feature> <task-id>"),
    )
    EXECUTABLE_OCCURRENCES = (
        (*COMMAND_PATHS[0], 0),
        (*COMMAND_PATHS[1], 0),
        (*COMMAND_PATHS[2], 0),
        (*COMMAND_PATHS[3], 0),
        ("spec-implement", IMPLEMENT_GATE, 0),
        ("spec-implement", IMPLEMENT_GATE, 1),
        ("spec-implement", TASK_IDS_PENDING, 0),
        ("spec-implement", TASK_IDS_SELECTOR, 0),
        (*COMMAND_PATHS[5], 0),
    )
    SEMANTIC_SPANS = (
        ("spec-requirements",
         "`> Status: approved <original date>` พร้อมเพิ่ม annotation แยกบรรทัดเป็น\n"
         "`> Status-Note: amended <YYYY-MM-DD>`"),
        ("spec-design",
         "`> Status: approved <original date>` and add the amendment separately as\n"
         "`> Status-Note: amended <YYYY-MM-DD>`"),
        ("spec-tasks",
         "approved, keep its canonical header as `> Status: approved <original date>` and add\n"
         "`> Status-Note: amended <YYYY-MM-DD>` separately."),
        ("spec-quick",
         "> Status: approved <YYYY-MM-DD>\n"
         "> Status-Note: quick, no approval gates"),
        ("spec-tasks",
         "เลือก workflow จาก canonical artifact shape บน disk เท่านั้น:\n\n"
         "- มี `bugfix.md` และไม่มี `requirements.md`/`design.md` → `bugfix`\n"
         "- มี `requirements.md` กับ `design.md` และไม่มี `bugfix.md` → feature shape ที่\n"
         "  Requirements-First และ Design-First converge แล้ว (`requirements-first` กับ\n"
         "  `design-first` ใช้ phase contract เดียวกันสำหรับ tasks); ใช้\n"
         "  `requirements-first` เป็น canonical label ของ shape นี้โดยไม่เดาประวัติจาก prose\n"
         "- shape อื่น → หยุด เพราะ missing หรือ ambiguous"),
        ("spec-implement",
         "เลือก workflow จาก canonical artifact shape บน disk เท่านั้น:\n\n"
         "- มี `bugfix.md` และไม่มี `requirements.md`/`design.md` → `bugfix`\n"
         "- มี `requirements.md` กับ `design.md` และไม่มี `bugfix.md` → feature shape ที่\n"
         "  Requirements-First และ Design-First converge แล้ว (`requirements-first` กับ\n"
         "  `design-first` ใช้ phase contract เดียวกันสำหรับ implement); ใช้\n"
         "  `requirements-first` เป็น canonical label ของ shape นี้โดยไม่เดาประวัติจาก prose\n"
         "- shape อื่น → หยุด เพราะ missing หรือ ambiguous"),
        ("spec-implement", "หาก `$ARGUMENTS == all` ให้เลือกเฉพาะ pending task IDs:"),
        ("spec-implement",
         "ทั้งสอง branch ต้องคืน exit `0` ก่อนเข้า loop; selector unknown หรือคำสั่งคืน non-zero ให้หยุด\n"
         "ตาม diagnostic ทันที. นำทุก ID ที่ CLI คืนเข้า loop ด้านล่างตาม file order โดยไม่ข้าม ID."),
        ("spec-implement",
         "ถ้า `spec-slice.sh` คืน non-zero ให้หยุดทันที ใช้ output ที่ exit `0` เป็น initial slice\n"
         "   และห้ามแทนด้วย grep หรือ parser ใน skill."),
        ("spec-implement", "หาก output มี `MISSING:` ให้ full-read upstream artifacts ทั้งหมดตาม workflow:"),
    )
    OUTER_WRAPPERS = (
        ("````markdown", "````"),
        ("~~~~markdown", "~~~~"),
        ("<!--", "-->"),
    )
    AMENDED_STATUS_SKILLS = (
        "spec-requirements",
        "spec-design",
        "spec-tasks",
    )

    def copy_skills(self):
        for skill in self.SKILLS:
            self.copy_rel(f".claude/skills/{skill}/SKILL.md")

    def mutate_skill(self, skill: str, needle: str, replacement: str) -> None:
        self.copy_skills()
        path = self.root / f".claude/skills/{skill}/SKILL.md"
        text = path.read_text(encoding="utf-8")
        self.assertIn(needle, text)
        path.write_text(text.replace(needle, replacement, 1), encoding="utf-8")

    def mutate_command_block(self, skill: str, command: str, occurrence: int,
                             prefix: str, suffix: str | None = None) -> None:
        self.copy_skills()
        path = self.root / f".claude/skills/{skill}/SKILL.md"
        lines = path.read_text(encoding="utf-8").splitlines(keepends=True)
        matches = [index for index, line in enumerate(lines)
                   if line.strip() == command]
        self.assertGreater(len(matches), occurrence)
        index = matches[occurrence]
        indent = lines[index][:len(lines[index]) - len(lines[index].lstrip())]
        inserted = [f"{indent}{prefix}\n", lines[index]]
        if suffix is not None:
            inserted.append(f"{indent}{suffix}\n")
        lines[index:index + 1] = inserted
        path.write_text("".join(lines), encoding="utf-8")

    def wrap_fenced_block(self, skill: str, needle: str, occurrence: int,
                          opening: str, wrapper: tuple[str, str]) -> None:
        self.copy_skills()
        path = self.root / f".claude/skills/{skill}/SKILL.md"
        lines = path.read_text(encoding="utf-8").splitlines(keepends=True)
        matches = [index for index, line in enumerate(lines)
                   if line.strip() == needle]
        self.assertGreater(len(matches), occurrence)
        match = matches[occurrence]
        start = max(index for index in range(match + 1)
                    if lines[index].strip() == opening)
        end = next(index for index in range(match + 1, len(lines))
                   if lines[index].strip() == "```")
        prefix, suffix = wrapper
        lines[start:end + 1] = [prefix + "\n", *lines[start:end + 1], suffix + "\n"]
        path.write_text("".join(lines), encoding="utf-8")

    def wrap_semantic_span(self, skill: str, needle: str,
                           wrapper: tuple[str, str]) -> None:
        self.copy_skills()
        path = self.root / f".claude/skills/{skill}/SKILL.md"
        text = path.read_text(encoding="utf-8")
        self.assertIn(needle, text)
        start = text.rfind("\n", 0, text.index(needle)) + 1
        end = text.find("\n", text.index(needle) + len(needle))
        if end < 0:
            end = len(text)
        prefix, suffix = wrapper
        mutated = text[:start] + prefix + "\n" + text[start:end] + "\n" + suffix + text[end:]
        path.write_text(mutated, encoding="utf-8")

    def assert_alignment_failure(self, label: str) -> None:
        diags = rpa.check_phase_skills(self.root)
        self.assertTrue(any(
            diag.code == "ALIGN_PHASE_SKILLS_MISMATCH" for diag in diags
        ), msg=f"mutation remained green: {label}")

    def test_real_canonical_phase_skills_are_aligned(self):
        self.assertEqual(rpa.check_phase_skills(REPO), [])

    def test_required_phase_behavior_mutations_fail(self):
        mutations = (
            ("spec-implement", "MISSING:", "full-read fallback removed"),
            ("spec-quick", "> Status-Note:", "quick status note removed"),
        )
        for skill, needle, replacement in mutations:
            with self.subTest(skill=skill, needle=needle):
                self.mutate_skill(skill, needle, replacement)
                self.assert_alignment_failure(f"{skill} {needle}")

    def test_noop_command_mutations_fail_across_all_six_paths(self):
        replacements = (
            lambda command: "command removed",
            lambda command: f"true # {command}",
            lambda command: f"printf '%s\\n' '{command}'",
            lambda command: f"{command} || true",
        )
        for skill, command in self.COMMAND_PATHS:
            for replacement in replacements:
                with self.subTest(skill=skill, command=command,
                                  replacement=replacement(command)):
                    self.mutate_skill(skill, command, replacement(command))
                    self.assert_alignment_failure(f"{skill} no-op command")

    def test_control_flow_noop_mutations_fail_across_all_nine_occurrences(self):
        wrappers = (
            ("if false; then", "fi"),
            ("false &&", None),
        )
        self.assertEqual(9, len(self.EXECUTABLE_OCCURRENCES))
        for skill, command, occurrence in self.EXECUTABLE_OCCURRENCES:
            for prefix, suffix in wrappers:
                with self.subTest(skill=skill, command=command,
                                  occurrence=occurrence, prefix=prefix):
                    self.mutate_command_block(
                        skill, command, occurrence, prefix, suffix)
                    self.assert_alignment_failure(
                        f"{skill} control-flow no-op occurrence {occurrence}")

    def test_outer_wrappers_hide_all_command_and_semantic_assertions(self):
        self.assertEqual(9, len(self.EXECUTABLE_OCCURRENCES))
        self.assertEqual(10, len(self.SEMANTIC_SPANS))
        for wrapper in self.OUTER_WRAPPERS:
            for skill, command, occurrence in self.EXECUTABLE_OCCURRENCES:
                with self.subTest(kind="command", wrapper=wrapper[0],
                                  skill=skill, occurrence=occurrence):
                    self.wrap_fenced_block(
                        skill, command, occurrence, "```bash", wrapper)
                    self.assert_alignment_failure(
                        f"{skill} hidden command occurrence {occurrence}")
            for skill, needle in self.SEMANTIC_SPANS:
                with self.subTest(kind="semantic", wrapper=wrapper[0],
                                  skill=skill, needle=needle):
                    if skill == "spec-quick":
                        self.wrap_fenced_block(
                            skill, "> Status: approved <YYYY-MM-DD>", 0,
                            "```text", wrapper)
                    else:
                        self.wrap_semantic_span(skill, needle, wrapper)
                    self.assert_alignment_failure(
                        f"{skill} hidden semantic span")

    def test_unclosed_outer_wrappers_fail_via_shared_scanner_contract(self):
        skill, command, occurrence = self.EXECUTABLE_OCCURRENCES[0]
        for opening in ("````markdown", "~~~~markdown", "<!--"):
            with self.subTest(opening=opening):
                self.copy_skills()
                path = self.root / f".claude/skills/{skill}/SKILL.md"
                text = path.read_text(encoding="utf-8")
                fenced = f"```bash\n{command}\n```"
                self.assertIn(fenced, text)
                path.write_text(text.replace(fenced, opening + "\n" + fenced, 1),
                                encoding="utf-8")
                self.assert_alignment_failure(
                    f"unclosed shared scanner wrapper {opening}")

    def test_selector_family_routes_and_stops_before_each_id_enters_slice_loop(self):
        feature = self.root / ".ai/specs/demo"
        feature.mkdir(parents=True)
        (feature / "tasks.md").write_text(
            "> Status: approved 2026-08-27\n"
            "- [x] 1. done\n"
            "- [ ] 2. pending\n"
            "- [ ] 3. pending\n",
            encoding="utf-8",
        )

        def run(*arguments: str) -> subprocess.CompletedProcess[str]:
            return subprocess.run(
                [sys.executable, str(REPO / "scripts/spec_contract.py"),
                 "task-ids", "--feature", "demo", *arguments, "--format", "lines",
                 "--specs-root", str(self.root / ".ai/specs")],
                text=True, capture_output=True, check=False,
            )

        exact = run("--selector", "2")
        numeric_range = run("--selector", "1-3")
        pending = run("--pending")
        unknown = run("--selector", "unknown")
        self.assertEqual((0, ["2"]), (exact.returncode, exact.stdout.splitlines()))
        self.assertEqual((0, ["1", "2", "3"]),
                         (numeric_range.returncode, numeric_range.stdout.splitlines()))
        self.assertEqual((0, ["2", "3"]),
                         (pending.returncode, pending.stdout.splitlines()))
        self.assertNotEqual(0, unknown.returncode)
        self.assertIn("TASK_SELECTOR_AMBIGUOUS", unknown.stdout + unknown.stderr)

        text = (REPO / ".claude/skills/spec-implement/SKILL.md").read_text(
            encoding="utf-8")
        all_branch = text.find("`$ARGUMENTS == all`")
        pending_command = text.find(self.TASK_IDS_PENDING)
        exact_range_branch = text.find("exact ID หรือ numeric range")
        selector_command = text.find(self.TASK_IDS_SELECTOR)
        loop = text.find("For EACH exact task ID:")
        slice_command = text.find("scripts/spec-slice.sh <feature> <task-id>")
        self.assertTrue(
            0 <= all_branch < pending_command < exact_range_branch
            < selector_command < loop < slice_command)
        self.assertIn("คืน non-zero ให้หยุด", text[pending_command:loop])
        self.assertIn("ทุก ID ที่ CLI คืนเข้า loop", text[pending_command:loop])

    def test_pending_selector_command_mutations_fail(self):
        mutations = (
            ("pending command removed", None),
            ("if false; then", "fi"),
            ("false &&", None),
        )
        for prefix, suffix in mutations:
            with self.subTest(prefix=prefix):
                if prefix == "pending command removed":
                    self.mutate_skill(
                        "spec-implement", self.TASK_IDS_PENDING, prefix)
                else:
                    self.mutate_command_block(
                        "spec-implement", self.TASK_IDS_PENDING, 0, prefix, suffix)
                self.assert_alignment_failure("pending selector command")

    def test_wrong_workflow_mutations_fail_across_all_five_gate_paths(self):
        for skill, command in self.COMMAND_PATHS[:-1]:
            workflow = command.rsplit("--workflow ", 1)[1]
            mutated = command.replace(
                f"--workflow {workflow}", "--workflow wrong-workflow", 1)
            with self.subTest(skill=skill, command=command):
                self.mutate_skill(skill, command, mutated)
                self.assert_alignment_failure(f"{skill} wrong workflow")

    def test_command_after_write_or_work_mutations_fail_across_all_six_paths(self):
        anchors = (
            "Write `.ai/specs/<feature>/requirements.md`",
            "Then write `.ai/specs/<feature>/design.md`",
            "Then write `.ai/specs/<feature>/design.md`",
            "Then write\n`.ai/specs/<feature>/tasks.md`",
            "2. Plan the task",
            "2. Plan the task",
        )
        for (skill, command), anchor in zip(self.COMMAND_PATHS, anchors):
            with self.subTest(skill=skill, command=command):
                self.copy_skills()
                path = self.root / f".claude/skills/{skill}/SKILL.md"
                text = path.read_text(encoding="utf-8")
                self.assertIn(command, text)
                self.assertIn(anchor, text)
                mutated = text.replace(command, "command moved", 1)
                mutated += f"\n{anchor}\n{command}\n"
                path.write_text(mutated, encoding="utf-8")
                self.assert_alignment_failure(f"{skill} command after work")

    def test_spec_implement_slice_precedes_state_and_reconciliation(self):
        text = (REPO / ".claude/skills/spec-implement/SKILL.md").read_text(
            encoding="utf-8")
        self.assertLess(text.find("scripts/spec-slice.sh <feature> <task-id>"),
                        text.find("scripts/spec-state.sh <feature>"))
        self.assertLess(text.find("scripts/spec-state.sh <feature>"),
                        text.find("2. Plan the task"))

    def test_state_before_slice_or_repeated_gate_mutations_fail(self):
        state = "scripts/spec-state.sh <feature>"
        for anchor in ("For EACH exact task ID:",
                       "หลัง full-read ให้รัน gate ซ้ำด้วย workflow เดิม:"):
            with self.subTest(anchor=anchor):
                self.copy_skills()
                path = self.root / ".claude/skills/spec-implement/SKILL.md"
                text = path.read_text(encoding="utf-8")
                self.assertIn(state, text)
                self.assertIn(anchor, text)
                mutated = text.replace(state, "state moved", 1)
                mutated = mutated.replace(anchor, f"`{state}`\n\n{anchor}", 1)
                path.write_text(mutated, encoding="utf-8")
                self.assert_alignment_failure(f"state before {anchor}")

    def test_removing_repeated_implement_gate_fails(self):
        self.copy_skills()
        path = self.root / ".claude/skills/spec-implement/SKILL.md"
        text = path.read_text(encoding="utf-8")
        command = self.COMMAND_PATHS[-2][1]
        before, separator, after = text.rpartition(command)
        self.assertTrue(separator)
        path.write_text(before + "repeated gate removed" + after,
                        encoding="utf-8")
        self.assert_alignment_failure("repeated implement gate removed")

    def test_amended_status_mutations_fail_across_all_three_sync_paths(self):
        canonical = "> Status: approved <original date>"
        note = "> Status-Note: amended <YYYY-MM-DD>"
        malformed = "> Status: approved <original date>, amended <YYYY-MM-DD>"
        for skill in self.AMENDED_STATUS_SKILLS:
            with self.subTest(skill=skill):
                self.copy_skills()
                path = self.root / f".claude/skills/{skill}/SKILL.md"
                text = path.read_text(encoding="utf-8")
                self.assertIn(canonical, text)
                self.assertIn(note, text)
                mutated = text.replace(canonical, malformed, 1).replace(note, "", 1)
                path.write_text(mutated, encoding="utf-8")
                self.assert_alignment_failure(f"{skill} malformed amended status")

    def test_legacy_quick_status_grammar_fails(self):
        self.copy_skills()
        path = self.root / ".claude/skills/spec-quick/SKILL.md"
        text = path.read_text(encoding="utf-8")
        canonical = "> Status: approved <YYYY-MM-DD>"
        self.assertIn(canonical, text)
        path.write_text(
            text.replace(canonical,
                         "> Status: approved <YYYY-MM-DD> (quick, no gates)", 1),
            encoding="utf-8",
        )
        self.assert_alignment_failure("spec-quick legacy status")


class RealRepoCheckTest(unittest.TestCase):
    """The whole engine against the REAL tree — the canonical positive."""

    def test_full_check_green(self):
        results = rpa.run_check()
        self.assertEqual(results, [], msg="\n".join(
            f"{row}:{d.code}:{d.message}" for row, d in results))


class VerificationRecordTest(unittest.TestCase):

    def _valid_docker(self):
        return rpa.build_unverified_record(
            check_id="docker-image-build",
            command="docker build -t pol-core-check .",
            reason="docker daemon unavailable in this environment",
            constraint="no docker socket inside sandbox",
            scope="local-environment-unverified")

    def test_valid_records_accept_scope_variants(self):
        rec = self._valid_docker()
        rec["scope_label"] = "temporary-local-ci-equivalent"
        self.assertEqual([d.code for d in rpa.validate_unverified_record(rec)], [])
        sql = rpa.build_unverified_record("sql-live", "dotnet test --filter Integration",
                                          reason="no SQL server", constraint="sandbox")
        self.assertEqual(rpa.validate_unverified_record(sql), [])
        generic = rpa.build_unverified_record("browser-e2e", "npm run e2e",
                                              reason="no browser",
                                              constraint="headless sandbox")
        self.assertEqual(generic["exit_code"], None)
        self.assertEqual(generic["observed_result"], "not-run")
        self.assertEqual(rpa.validate_unverified_record(generic), [])

    def test_missing_required_field_reports_code(self):
        rec = self._valid_docker()
        del rec["environment_constraint"]
        codes = {d.code for d in rpa.validate_unverified_record(rec)}
        self.assertIn("VERIFY_UNVERIFIED_FIELDS_MISSING", codes)

    def test_outside_closed_scope_rejected(self):
        rec = self._valid_docker()
        rec["scope_label"] = "remote-verified"
        codes = {d.code for d in rpa.validate_unverified_record(rec)}
        self.assertIn("VERIFY_SCOPE_INVALID", codes)

    def test_new_scope_labels_accepted(self):
        for scope in ("remote-github-unverified", "remote-gitlab-unverified",
                      "local-environment-unverified"):
            rec = rpa.build_unverified_record(
                "x", "cmd", reason="r", constraint="c", scope=scope)
            self.assertEqual([], rpa.validate_unverified_record(rec),
                             msg=scope)

    def test_pass_claim_forbidden(self):
        rec = self._valid_docker()
        rec["observed_result"] = "pass"
        rec["exit_code"] = 0
        codes = {d.code for d in rpa.validate_unverified_record(rec)}
        self.assertIn("VERIFY_PASS_CLAIM_FORBIDDEN", codes)
        rec2 = self._valid_docker()
        rec2["message"] = "all good"
        codes2 = {d.code for d in rpa.validate_unverified_record(rec2)}
        self.assertIn("VERIFY_PASS_CLAIM_FORBIDDEN", codes2)

    def test_exit_null_violation_flagged(self):
        rec = self._valid_docker()
        rec["exit_code"] = 0   # a run that exited cannot be an honest not-run record
        codes = [d.code for d in rpa.validate_unverified_record(rec)]
        self.assertIn("VERIFY_PASS_CLAIM_FORBIDDEN", codes)


if __name__ == "__main__":
    unittest.main()
