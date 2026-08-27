#!/usr/bin/env python3
"""repo_policy_alignment.py — source-to-assertion alignment engine (task 8).

Every row reads REAL filesystem/config as source of truth and compares it to a
machine-readable assertion in canonical docs (.ai/shared/ARCHITECTURE.md
"As-built registry", AGENT_HANDOFF_PROTOCOL.md schema, boundary docs). There are
no hardcoded pass flags: the registry lives in docs, drift either way fails
with a stable diagnostic code. Negative fixtures mutate copies in temporary
trees (SDD_ALIGNMENT_REPO test seam) and must fail with the documented codes.

Exit: 0 aligned · 1 misaligned · 2 usage error.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from dataclasses import dataclass
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
if str(SCRIPTS) not in sys.path:
    sys.path.insert(0, str(SCRIPTS))

import spec_contract as sc  # noqa: E402


@dataclass
class Diag:
    code: str
    message: str


def repo_root() -> Path:
    override = os.environ.get("SDD_ALIGNMENT_REPO")
    if override:
        return Path(override).resolve()
    return Path(__file__).resolve().parent.parent


# ---------------------------------------------------------------------------
# Canonical-doc registry parsing (ARCHITECTURE.md "As-built registry")
# ---------------------------------------------------------------------------

REGISTRY_HEADING = "## As-built registry"


def _registry_text(root: Path) -> str:
    arch = root / ".ai/shared/ARCHITECTURE.md"
    if not arch.is_file():
        raise FileNotFoundError(f"missing {arch}")
    text = arch.read_text(encoding="utf-8")
    start = text.find(REGISTRY_HEADING)
    if start < 0:
        raise ValueError("ARCHITECTURE.md missing '## As-built registry'")
    rest = text[start + len(REGISTRY_HEADING):]
    nxt = re.search(r"^## ", rest, re.MULTILINE)
    return rest[: nxt.start()] if nxt else rest


def registry_entries(root: Path, subsection: str) -> list[str]:
    """First-cell backticked tokens of the table under a `### <subsection>`."""
    section = _registry_text(root)
    match = re.search(rf"^### {re.escape(subsection)}\s*$", section, re.MULTILINE)
    if not match:
        raise ValueError(f"registry subsection missing: {subsection}")
    tail = section[match.end():]
    stop = re.search(r"^### ", tail, re.MULTILINE)
    tail = tail[: stop.start()] if stop else tail
    rows = []
    for line in tail.splitlines():
        row = re.match(r"^\|\s*`([^`]+)`", line)
        if row:
            rows.append(row.group(1))
    return rows


# ---------------------------------------------------------------------------
# Filesystem extractors (source of truth)
# ---------------------------------------------------------------------------

def fs_modules(root: Path) -> list[str]:
    """First-level src/Modules/<name> dirs carrying at least one *.csproj."""
    base = root / "src/Modules"
    if not base.is_dir():
        return []
    mods = []
    for entry in sorted(base.iterdir()):
        if entry.is_dir() and any(entry.rglob("*.csproj")):
            mods.append(entry.name)
    return mods


def fs_runtime_dbcontexts(root: Path) -> list[str]:
    """Class declarations *DbContext.cs under src/Persistence/** (runtime only —
    BuildingBlocks' migration-owner PolDbContext lives outside this tree)."""
    base = root / "src/Persistence"
    contexts = []
    if not base.is_dir():
        return contexts
    for path in sorted(base.rglob("*DbContext.cs")):
        name = path.stem
        if re.search(rf"\bclass {name}\b", path.read_text(encoding="utf-8",
                                                          errors="replace")):
            contexts.append(name)
    return contexts


# ---------------------------------------------------------------------------
# Alignment rows
# ---------------------------------------------------------------------------

MISSING = "negative-guard: extractor ศูนย์รายการ (source grammar/path drift)"


def diff_sets(actual: list[str], declared: list[str]) -> tuple[list[str], list[str]]:
    a, d = set(actual), set(declared)
    return sorted(d - a), sorted(a - d)


def check_modules(root: Path) -> list[Diag]:
    # NOTE: an "empty retired container" re-entering module-hood by gaining a
    # *.csproj MUST surface here — that is the documented negative fixture.
    actual = fs_modules(root)
    declared = registry_entries(root, "Modules")
    problems: list[Diag] = []
    if not actual or not declared:
        return [Diag("ALIGN_MODULES_MISMATCH", MISSING)]
    missing, extra = diff_sets(actual, declared)
    for name in missing:
        problems.append(Diag("ALIGN_MODULES_MISMATCH",
                             f"module {name} มีใน docs แต่ไม่มีใน src/Modules"))
    for name in extra:
        problems.append(Diag("ALIGN_MODULES_MISMATCH",
                             f"module {name} มีใน src/Modules แต่ไม่มีใน docs"))
    return problems


def check_dbcontexts(root: Path) -> list[Diag]:
    actual = fs_runtime_dbcontexts(root)
    declared = registry_entries(root, "Runtime DbContexts")
    if not actual or not declared:
        return [Diag("ALIGN_DBCONTEXTS_MISMATCH", MISSING)]
    missing, extra = diff_sets(actual, declared)
    return [
        Diag("ALIGN_DBCONTEXTS_MISMATCH",
             f"context {name} {'อยู่ใน docs แต่ไม่พบใน src/Persistence' if name in missing else 'พบใน runtime แต่ไม่อยู่ใน docs'}")
        for name in sorted(set(missing) | set(extra))
    ]


_PERSISTENCE_BASE = Path("src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence")


def check_migration_owner(root: Path) -> list[Diag]:
    owner = root / _PERSISTENCE_BASE / "PolDbContext.cs"
    snapshot = root / _PERSISTENCE_BASE / "Migrations/PolDbContextModelSnapshot.cs"
    problems: list[Diag] = []
    if not owner.is_file():
        problems.append(Diag("ALIGN_MIGRATION_OWNER_MISMATCH",
                             f"owner class หาย: {owner}"))
    if not snapshot.is_file():
        problems.append(Diag("ALIGN_MIGRATION_OWNER_MISMATCH",
                             f"model snapshot หาย: {snapshot}"))
    src = root / "src"
    if src.is_dir():
        needle = "AddDbContext<PolDbContext>"
        for path in src.rglob("*.cs"):
            try:
                if needle in path.read_text(encoding="utf-8", errors="replace"):
                    problems.append(Diag(
                        "ALIGN_MIGRATION_OWNER_MISMATCH",
                        f"PolDbContext ถูก register เป็น request runtime context ที่ {path}"))
                    break
            except OSError:
                continue
    return problems


def check_isolation(root: Path) -> list[Diag]:
    guard = root / _PERSISTENCE_BASE / "GuardedRuntimeDbContext.cs"
    problems: list[Diag] = []
    if not guard.is_file():
        problems.append(Diag("ALIGN_ISOLATION_MISMATCH",
                             f"sealed write floor หาย: {guard}"))
    contexts = {
        "ControlPlane": root / "src/Persistence/Persistence.ControlPlane/ControlPlaneDbContext.cs",
        "MerchantUsers": root / "src/Persistence/Persistence.MerchantUsers/MerchantUserDbContext.cs",
        "MerchantRuntime": root / "src/Persistence/Persistence.MerchantRuntime/MerchantRuntimeDbContext.cs",
    }
    for cluster, path in contexts.items():
        if not path.is_file():
            problems.append(Diag("ALIGN_ISOLATION_MISMATCH",
                                 f"{cluster} context หาย: {path}"))
            continue
        body = path.read_text(encoding="utf-8", errors="replace")
        if "GuardedRuntimeDbContext" not in body:
            problems.append(Diag(
                "ALIGN_ISOLATION_MISMATCH",
                f"{cluster} ไม่ inherit sealed write floor (GuardedRuntimeDbContext)"))
        if cluster != "ControlPlane":
            has_filter = any("HasQueryFilter" in cfg.read_text(encoding="utf-8",
                                                             errors="replace")
                             for cfg in path.parent.rglob("*.cs")
                             if cfg.name != path.name)
            if not has_filter:
                problems.append(Diag(
                    "ALIGN_ISOLATION_MISMATCH",
                    f"{cluster} ไม่มี deny-default query filter เลย"))
    disjoint = root / "tests/Architecture.Tests/ModelDisjointnessTests.cs"
    if not disjoint.is_file():
        problems.append(Diag("ALIGN_ISOLATION_MISMATCH",
                             f"model ownership test หาย: {disjoint}"))
    return problems


_JOB_LINE = re.compile(r"^  ([a-z][a-z0-9_-]*):\s*$")
_GITLAB_JOB = re.compile(r"^([A-Za-z][A-Za-z0-9_-]*):\s*$")
_GITLAB_META = {"stages", "workflow", "include", "default", "variables", "image",
                "before_script", "after_script", "cache", "retry", "timeout"}


def _github_jobs(root: Path) -> list[str]:
    path = root / ".github/workflows/ci.yml"
    if not path.is_file():
        return []
    jobs, inside = [], False
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip() == "jobs:":
            inside = True
            continue
        if inside:
            if line.startswith(("  ", "\t")) and (match := _JOB_LINE.match(line)):
                jobs.append(match.group(1))
            elif line and not line.startswith((" ", "#")):
                break
    return jobs


def _gitlab_jobs(root: Path) -> list[str]:
    path = root / ".gitlab-ci.yml"
    if not path.is_file():
        return []
    jobs = []
    for line in path.read_text(encoding="utf-8").splitlines():
        match = _GITLAB_JOB.match(line)
        if match and match.group(1) not in _GITLAB_META:
            jobs.append(match.group(1))
    return jobs


def check_ci_jobs(root: Path) -> list[Diag]:
    declared = registry_entries(root, "CI topology")
    github_declared = [j for j in declared if j.startswith("github:")]
    gitlab_declared = [j.split(":", 1)[1] for j in declared
                       if j.startswith("gitlab:")]
    github_actual = _github_jobs(root)
    gitlab_actual = _gitlab_jobs(root)
    if not github_actual or not gitlab_actual or not declared:
        return [Diag("ALIGN_CI_JOBS_MISMATCH", MISSING)]
    problems: list[Diag] = []
    for kind, actual, expect in (
            ("github", github_actual,
             [j.split(":", 1)[1] for j in github_declared]),
            ("gitlab", gitlab_actual, gitlab_declared)):
        missing, extra = diff_sets(actual, expect)
        for name in missing:
            problems.append(Diag("ALIGN_CI_JOBS_MISMATCH",
                                 f"{kind} job {name} ประกาศใน docs แต่ workflow ไม่มี"))
        for name in extra:
            problems.append(Diag("ALIGN_CI_JOBS_MISMATCH",
                                 f"{kind} job {name} มีใน workflow แต่ docs ไม่ประกาศ"))
    return problems


def _h2_sequence(text: str) -> list[str]:
    return re.findall(r"^## (.+?)\s*$", text, re.MULTILINE)


def check_handoff_schema(root: Path) -> list[Diag]:
    protocol = root / ".ai/shared/AGENT_HANDOFF_PROTOCOL.md"
    template = root / ".ai/templates/handoff-note-template.md"
    problems: list[Diag] = []
    if not protocol.is_file() or not template.is_file():
        return [Diag("ALIGN_HANDOFF_SCHEMA_MISMATCH", MISSING)]
    proto_text = protocol.read_text(encoding="utf-8")
    schema_at = proto_text.find("## Schema")
    fence = proto_text.find("```", proto_text.find("```", schema_at) + 3)
    block_start = proto_text.find("```", schema_at) + 3
    if schema_at < 0 or fence <= block_start - 1:
        return [Diag("ALIGN_HANDOFF_SCHEMA_MISMATCH", "protocol ไม่มี schema block")]
    schema_block = proto_text[block_start:fence]
    proto_headings = _h2_sequence(schema_block)
    template_headings = _h2_sequence(template.read_text(encoding="utf-8"))
    if not proto_headings or not template_headings:
        return [Diag("ALIGN_HANDOFF_SCHEMA_MISMATCH", MISSING)]
    if proto_headings != template_headings:
        problems.append(Diag(
            "ALIGN_HANDOFF_SCHEMA_MISMATCH",
            f"H2 order/cardinality ต่างกัน: protocol={proto_headings} vs "
            f"template={template_headings}"))
    return problems


def check_git_boundary(root: Path) -> list[Diag]:
    pre_push = root / ".githooks/pre-push"
    task_protocol = root / ".ai/shared/TASK_PROTOCOL.md"
    security_rules = root / ".ai/shared/SECURITY_RULES.md"
    destructive = root / ".ai/bin/check-destructive.sh"
    problems: list[Diag] = []
    if not all(p.is_file() for p in (pre_push, task_protocol, security_rules,
                                     destructive)):
        return [Diag("ALIGN_GIT_BOUNDARY_MISMATCH", MISSING)]
    hooks = pre_push.read_text(encoding="utf-8")
    for ref in ("refs/heads/main", "refs/heads/develop"):
        if ref not in hooks:
            problems.append(Diag("ALIGN_GIT_BOUNDARY_MISMATCH",
                                 f"pre-push ขาด protected ref {ref}"))
    if "non-fast-forward" not in hooks:
        problems.append(Diag("ALIGN_GIT_BOUNDARY_MISMATCH",
                             "pre-push ไม่ block force/non-fast-forward push"))
    rules = security_rules.read_text(encoding="utf-8")
    if "Tier 1" not in rules:
        problems.append(Diag("ALIGN_GIT_BOUNDARY_MISMATCH",
                             "SECURITY_RULES ไม่ประกาศ Tier 1 floor"))
    protocol = task_protocol.read_text(encoding="utf-8")
    if "`main`" not in protocol or "`develop`" not in protocol:
        problems.append(Diag("ALIGN_GIT_BOUNDARY_MISMATCH",
                             "TASK_PROTOCOL ไม่ระบุ protected branches"))
    if "destructive" not in destructive.read_text(encoding="utf-8").lower():
        problems.append(Diag("ALIGN_GIT_BOUNDARY_MISMATCH",
                             "check-destructive.sh lost its policy vocabulary"))
    return problems


_PHASE_SKILL_EXPECTATIONS = {
    "spec-requirements": (),
    "spec-design": (),
    "spec-tasks": (
        "เลือก workflow จาก canonical artifact shape บน disk เท่านั้น:",
        "requirements-first",
        "design-first",
        "bugfix",
    ),
    "spec-implement": (
        "เลือก workflow จาก canonical artifact shape บน disk เท่านั้น:",
        "หาก output มี `MISSING:` ให้ full-read upstream artifacts ทั้งหมดตาม workflow",
        "ถ้า `spec-slice.sh` คืน non-zero ให้หยุดทันที",
        "คืน non-zero ให้หยุด",
        "ทุก ID ที่ CLI คืนเข้า loop",
        "requirements.md",
        "design.md",
        "bugfix.md",
        "tasks.md",
    ),
    "spec-quick": (
        "> Status: approved <YYYY-MM-DD>",
        "> Status-Note:",
    ),
}

_REQUIREMENTS_GATE = (
    "python3 scripts/spec_contract.py gate phase --feature <feature> "
    "--phase requirements --workflow design-first")
_DESIGN_RF_GATE = (
    "python3 scripts/spec_contract.py gate phase --feature <feature> "
    "--phase design --workflow requirements-first")
_DESIGN_DF_GATE = (
    "python3 scripts/spec_contract.py gate phase --feature <feature> "
    "--phase design --workflow design-first")
_TASKS_GATE = (
    "python3 scripts/spec_contract.py gate phase --feature <feature> "
    "--phase tasks --workflow <workflow>")
_IMPLEMENT_GATE = (
    "python3 scripts/spec_contract.py gate phase --feature <feature> "
    "--phase implement --workflow <workflow>")
_TASK_IDS_COMMAND = (
    "python3 scripts/spec_contract.py task-ids --feature <feature> "
    "--selector \"$ARGUMENTS\" --format lines")
_PENDING_TASK_IDS_COMMAND = (
    "python3 scripts/spec_contract.py task-ids --feature <feature> "
    "--pending --format lines")
_SLICE_COMMAND = "scripts/spec-slice.sh <feature> <task-id>"

_PHASE_SKILL_COMMANDS = (
    ("spec-requirements", _REQUIREMENTS_GATE, 1),
    ("spec-design", _DESIGN_RF_GATE, 1),
    ("spec-design", _DESIGN_DF_GATE, 1),
    ("spec-tasks", _TASKS_GATE, 1),
    ("spec-implement", _IMPLEMENT_GATE, 2),
    ("spec-implement", _PENDING_TASK_IDS_COMMAND, 1),
    ("spec-implement", _TASK_IDS_COMMAND, 1),
    ("spec-implement", _SLICE_COMMAND, 1),
)

_AMENDED_STATUS_SKILLS = (
    "spec-requirements",
    "spec-design",
    "spec-tasks",
)
_AMENDED_STATUS = "> Status: approved <original date>"
_AMENDED_STATUS_NOTE = "> Status-Note: amended <YYYY-MM-DD>"


def _phase_skill_text(root: Path, name: str) -> tuple[Path, str | None]:
    path = root / ".claude/skills" / name / "SKILL.md"
    if not path.is_file():
        return path, None
    return path, path.read_text(encoding="utf-8", errors="replace")


def _top_level_fenced_content_lines(text: str, path: Path,
                                    opening: str) -> set[int]:
    """คืน content lines ของ exact fence ที่ opener อยู่ top-level ตาม shared scanner."""
    raw_lines = text.splitlines(keepends=True)
    plain_lines = text.splitlines()
    content: set[int] = set()
    block_start: int | None = None
    for index, raw_line in enumerate(raw_lines):
        line = raw_line.strip()
        if block_start is None:
            if line != opening:
                continue
            _, prefix_diagnostics = sc._outside_fence(plain_lines[:index], path)
            if not prefix_diagnostics:
                block_start = index
        elif line == "```":
            content.update(range(block_start + 2, index + 1))
            block_start = None
    return content


def _visible_phase_skill_text(text: str, path: Path) -> tuple[str, tuple[object, ...]]:
    """Mask content hidden by shared Markdown visibility while preserving offsets."""
    raw_lines = text.splitlines(keepends=True)
    visible, diagnostics = sc._outside_fence(text.splitlines(), path)
    active_lines = {number for number, _ in visible}
    active_lines.update(_top_level_fenced_content_lines(text, path, "```text"))
    masked = "".join(
        raw_line if number in active_lines else re.sub(r"[^\r\n]", " ", raw_line)
        for number, raw_line in enumerate(raw_lines, 1)
    )
    return masked, diagnostics


def _standalone_command_offsets(text: str, path: Path,
                                command: str) -> tuple[int, ...]:
    """หา top-level fenced bash block ที่มี exact command เพียงคำสั่งเดียว."""
    raw_lines = text.splitlines(keepends=True)
    top_level_content = _top_level_fenced_content_lines(text, path, "```bash")
    offsets: list[int] = []
    block: list[tuple[str, int]] | None = None
    cursor = 0
    for number, raw_line in enumerate(raw_lines, 1):
        line = raw_line.strip()
        if block is None:
            if line == "```bash" and number + 1 in top_level_content:
                block = []
        elif line == "```":
            executable = [(value, offset) for value, offset in block if value]
            if len(executable) == 1 and executable[0][0] == command:
                offsets.append(executable[0][1])
            block = None
        else:
            block.append((line, cursor + raw_line.find(line)))
        cursor += len(raw_line)
    return tuple(offsets)


def _add_ordering_problem(problems: list[Diag], name: str,
                          first_at: int, second_at: int,
                          first: str, second: str) -> None:
    if first_at < 0 or second_at < 0 or first_at >= second_at:
        problems.append(Diag(
            "ALIGN_PHASE_SKILLS_MISMATCH",
            f"{name} ต้องวาง {first!r} ก่อน {second!r}"))


def check_phase_skills(root: Path) -> list[Diag]:
    problems: list[Diag] = []
    texts: dict[str, str] = {}
    raw_texts: dict[str, str] = {}
    paths: dict[str, Path] = {}
    command_offsets: dict[tuple[str, str], tuple[int, ...]] = {}
    for name, tokens in _PHASE_SKILL_EXPECTATIONS.items():
        path, raw_text = _phase_skill_text(root, name)
        if raw_text is None:
            problems.append(Diag("ALIGN_PHASE_SKILLS_MISMATCH",
                                 f"canonical skill หาย: {path}"))
            continue
        text, visibility_diagnostics = _visible_phase_skill_text(raw_text, path)
        texts[name] = text
        raw_texts[name] = raw_text
        paths[name] = path
        for diagnostic in visibility_diagnostics:
            problems.append(Diag(
                "ALIGN_PHASE_SKILLS_MISMATCH",
                f"{name} Markdown visibility ไม่ valid: {diagnostic.code}"))
        for token in tokens:
            if token not in text:
                problems.append(Diag(
                    "ALIGN_PHASE_SKILLS_MISMATCH",
                    f"{name} ขาด required phase token {token!r}"))
    for name, command, expected_count in _PHASE_SKILL_COMMANDS:
        raw_text = raw_texts.get(name)
        path = paths.get(name)
        if raw_text is None or path is None:
            continue
        offsets = _standalone_command_offsets(raw_text, path, command)
        command_offsets[(name, command)] = offsets
        if len(offsets) != expected_count:
            problems.append(Diag(
                "ALIGN_PHASE_SKILLS_MISMATCH",
                f"{name} ต้องมี executable command line {command!r} "
                f"จำนวน {expected_count} จุด แต่พบ {len(offsets)}"))

    authoring_order = (
        ("spec-requirements", _REQUIREMENTS_GATE,
         "Write `.ai/specs/<feature>/requirements.md`"),
        ("spec-design", _DESIGN_RF_GATE,
         "Then write `.ai/specs/<feature>/design.md`"),
        ("spec-design", _DESIGN_DF_GATE,
         "Then write `.ai/specs/<feature>/design.md`"),
        ("spec-tasks", _TASKS_GATE,
         "Then write\n`.ai/specs/<feature>/tasks.md`"),
    )
    for name, command, anchor in authoring_order:
        text = texts.get(name)
        offsets = command_offsets.get((name, command), ())
        if text is not None:
            _add_ordering_problem(
                problems, name, offsets[0] if offsets else -1,
                text.find(anchor), command, anchor)

    implement = texts.get("spec-implement")
    if implement is not None:
        gates = command_offsets.get(("spec-implement", _IMPLEMENT_GATE), ())
        pending_task_id_offsets = command_offsets.get(
            ("spec-implement", _PENDING_TASK_IDS_COMMAND), ())
        task_id_offsets = command_offsets.get(
            ("spec-implement", _TASK_IDS_COMMAND), ())
        slices = command_offsets.get(("spec-implement", _SLICE_COMMAND), ())
        sequence = (
            (gates[0] if len(gates) > 0 else -1, "initial implement gate"),
            (implement.find("`$ARGUMENTS == all`"), "all selector branch"),
            (pending_task_id_offsets[0] if pending_task_id_offsets else -1,
             "pending task-ids resolver"),
            (implement.find("exact ID หรือ numeric range"), "exact/range selector branch"),
            (task_id_offsets[0] if task_id_offsets else -1, "selector task-ids resolver"),
            (implement.find("For EACH exact task ID:"), "per-ID loop"),
            (slices[0] if slices else -1, "spec-slice"),
            (implement.find("หาก output มี `MISSING:`"), "MISSING fallback"),
            (gates[1] if len(gates) > 1 else -1, "repeated implement gate"),
            (implement.find("scripts/spec-state.sh <feature>"), "spec-state/reconciliation"),
            (implement.find("2. Plan the task"), "implementation work"),
        )
        for (first_at, first), (second_at, second) in zip(sequence, sequence[1:]):
            _add_ordering_problem(
                problems, "spec-implement", first_at, second_at, first, second)

    for name in _AMENDED_STATUS_SKILLS:
        text = texts.get(name)
        if text is None:
            continue
        if _AMENDED_STATUS not in text or _AMENDED_STATUS_NOTE not in text:
            problems.append(Diag(
                "ALIGN_PHASE_SKILLS_MISMATCH",
                f"{name} ต้องแยก canonical approved Status กับ amendment Status-Note"))
        if "> Status: approved <original date>, amended" in text:
            problems.append(Diag(
                "ALIGN_PHASE_SKILLS_MISMATCH",
                f"{name} ใส่ amendment ปน canonical Status line"))

    quick = texts.get("spec-quick")
    if quick is not None and re.search(
            r"^\s*> Status: approved <YYYY-MM-DD>\s*\([^\n]*quick",
            quick, re.MULTILINE | re.IGNORECASE):
        problems.append(Diag(
            "ALIGN_PHASE_SKILLS_MISMATCH",
            "spec-quick ใช้ annotation ใน canonical Status line"))
    return problems


_PI_NEEDLES = ("no pre-tool hook", "floor-only", "No built-in subagents")


def check_pi_floor_only(root: Path) -> list[Diag]:
    agent_doc = root / ".ai/agents/pi/AGENT.md"
    problems: list[Diag] = []
    if not agent_doc.is_file():
        return [Diag("ALIGN_PI_DOCS_MISMATCH", MISSING)]
    text = agent_doc.read_text(encoding="utf-8")
    for needle in _PI_NEEDLES:
        if needle.lower() not in text.lower():
            problems.append(Diag(
                "ALIGN_PI_DOCS_MISMATCH",
                f"Pi adapter doc ขาด capability claim '{needle}' (REQ-6.5-6.8)"))
    if (root / ".pi/extensions").is_dir():
        problems.append(Diag("ALIGN_PI_DOCS_MISMATCH",
                             ".pi/extensions/** ต้องไม่เกิดขึ้น (REQ-6.9)"))
    return problems


ALL_ROWS = (
    ("modules", check_modules),
    ("dbcontexts", check_dbcontexts),
    ("migration_owner", check_migration_owner),
    ("isolation", check_isolation),
    ("ci_jobs", check_ci_jobs),
    ("handoff_schema", check_handoff_schema),
    ("git_boundary", check_git_boundary),
    ("phase_skills", check_phase_skills),
    ("pi_floor_only", check_pi_floor_only),
)


# ---------------------------------------------------------------------------
# Verification records (REQ-8.13): unverified-because-environment schema
# ---------------------------------------------------------------------------

# Closed scope labels (design §Verification record): temporary-* prove static
# definition/definition-adjacent checks; the unverified-* classes exist so an
# environment or authorization gap can be recorded HONESTLY without claiming pass.
VERIFY_SCOPES = {
    "temporary-static-workflow",
    "temporary-local-ci-equivalent",
    "remote-github-unverified",
    "remote-gitlab-unverified",
    "local-environment-unverified",
}
UNVERIFIED_SCOPES = {"remote-github-unverified", "remote-gitlab-unverified",
                     "local-environment-unverified"}
UNVERIFIED_MESSAGE = "unverified; must not be claimed as pass"


def build_unverified_record(check_id: str, command: str, reason: str,
                            constraint: str, substitute: str = "none",
                            scope: str = "temporary-static-workflow") -> dict:
    return {
        "check_id": check_id,
        "command": command,
        "exit_code": None,
        "observed_result": "not-run",
        "reason": reason,
        "environment_constraint": constraint,
        "substitute_evidence": substitute,
        "scope_label": scope,
        "message": UNVERIFIED_MESSAGE,
    }


def validate_unverified_record(record: dict) -> list[Diag]:
    required = ("check_id", "command", "exit_code", "observed_result", "reason",
                "environment_constraint", "substitute_evidence", "scope_label",
                "message")
    problems: list[Diag] = []
    for field in required:
        if field not in record:
            problems.append(Diag("VERIFY_UNVERIFIED_FIELDS_MISSING",
                                 f"record ขาด field '{field}'"))
    if problems:
        return problems
    scope = record.get("scope_label")
    if scope not in VERIFY_SCOPES:
        problems.append(Diag("VERIFY_SCOPE_INVALID",
                             f"scope '{scope}' อยู่นอก closed set {sorted(VERIFY_SCOPES)}"))
        # closed-set breach dominates; further field semantics still checked
        # below because a fabricated row must fail loudly either way
    observed = record.get("observed_result")
    exit_code = record.get("exit_code")
    message = str(record.get("message", ""))
    is_unverified_scope = scope in UNVERIFIED_SCOPES
    if is_unverified_scope and UNVERIFIED_MESSAGE not in message:
        problems.append(Diag(
            "VERIFY_PASS_CLAIM_FORBIDDEN",
            "unverified record ต้องมีข้อความ '" + UNVERIFIED_MESSAGE + "'"))
    if is_unverified_scope and (observed == "pass" or
                                record.get("claimed") == "pass" or
                                observed != "not-run" or exit_code is not None):
        problems.append(Diag(
            "VERIFY_PASS_CLAIM_FORBIDDEN",
            "unverified record ห้ามอ้าง pass / ต้อง not-run + exit_code null"))
    if not is_unverified_scope and observed != "not-run":
        # temporary scopes MAY carry an actually-run local result with a real
        # exit code — no not-run constraint applies there.
        pass
    return problems


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def run_check(root: Path | None = None) -> list[tuple[str, Diag]]:
    root = repo_root()
    results: list[tuple[str, Diag]] = []
    for row_name, checker in ALL_ROWS:
        try:
            for problem in checker(root):
                results.append((row_name, problem))
        except (FileNotFoundError, ValueError) as error:
            results.append((row_name, Diag("ALIGN_ENGINE_INTERNAL", str(error))))
    return results


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--json", action="store_true")
    args, extras = parser.parse_known_args(argv)
    if extras or (not args.check):
        parser.print_usage(sys.stderr)
        return 2
    results = run_check()
    if args.json:
        print(json.dumps({
            "schemaVersion": 1,
            "verdict": "allow" if not results else "policy-fail",
            "diagnostics": [{"row": row, "code": diag.code,
                             "message": diag.message}
                            for row, diag in results],
        }, sort_keys=True))
    else:
        for row, diag in results:
            print(f"[{row}] {diag.code}: {diag.message}")
        if not results:
            print("OK: source-to-assertion alignment ตรงทุก row")
    return 0 if not results else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
