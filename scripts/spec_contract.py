#!/usr/bin/env python3
"""Canonical read-only contract สำหรับ SDD Markdown artifacts."""
from __future__ import annotations

import argparse
import json
import re
import sys
import unicodedata
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable, Sequence

TASK_ID_PATTERN = r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}"
TASK_OPENING_RE = re.compile(r"^\s*- \[([ x])\]\s+([^\s.]+)\.\s+(.+?)\s*$")
STATUS_RE = re.compile(
    rf"^> Status: (draft|approved (\d{{4}}-\d{{2}}-\d{{2}})|superseded (\d{{4}}-\d{{2}}-\d{{2}}) by ({TASK_ID_PATTERN})|unknown)$"
)
STATUS_PREFIX_RE = re.compile(r"^> Status:")
REQ_HEADING_RE = re.compile(r"^## REQ-([0-9]+): (?=\S)(?:.*\S)?\s*$")
REQ_CRITERION_RE = re.compile(r"^- ([0-9]+)\.([0-9]+)\s+(.+?)\s*$")
BUGFIX_CRITERION_RE = re.compile(r"^- ([FB]-[0-9]+)\s+(.+?)\s*$")
FENCE_OPEN_RE = re.compile(r"^\s*(`{3,}|~{3,})")
FENCE_CLOSE_RE = re.compile(r"^\s*(`+|~+)\s*$")
FENCE_CONTAINER_RE = re.compile(r"^\s{0,3}>[ \t]?")
FENCE_LIST_RE = re.compile(r"^\s*(?:[-+*]|\d+[.)])\s+")
CRITERION_LIST_PREFIX_RE = re.compile(r"^\s*(?:[-+*]|[0-9]+[.)])\s+(.+?)\s*$")
STRICT_REF_RE = re.compile(r"REQ-([0-9]+)\.([0-9]+)")
WHOLE_REF_RE = re.compile(r"^REQ-([0-9]+)$")
RANGE_REF_RE = re.compile(r"^REQ-([0-9]+)\.([0-9]+)\s*[-–]\s*REQ-([0-9]+)\.([0-9]+)$")


@dataclass(frozen=True, slots=True)
class SourceLocation:
    path: str
    line: int
    column: int = 1


@dataclass(frozen=True, slots=True)
class Diagnostic:
    code: str
    verdict: str
    location: SourceLocation
    message: str
    details: dict[str, object] = field(default_factory=dict)


@dataclass(frozen=True, slots=True)
class ArtifactStatus:
    kind: str
    date: str | None
    superseded_by: str | None
    location: SourceLocation


@dataclass(frozen=True, slots=True)
class RequirementCriterion:
    ref: str
    statement: str
    heading: str | None
    location: SourceLocation


@dataclass(frozen=True, slots=True)
class TaskBlock:
    task_id: str
    title: str
    completed: bool
    ordinal: int
    span: tuple[int, int]
    location: SourceLocation
    satisfies: tuple[str, ...]
    depends_on: tuple[str, ...]
    verify: tuple[str, ...]
    batch: tuple[str, ...]


@dataclass(frozen=True, slots=True)
class TraceRow:
    refs: tuple[str, ...]
    section: str
    location: SourceLocation


@dataclass(frozen=True, slots=True)
class DesignSection:
    heading: str
    span: tuple[int, int]


@dataclass(frozen=True, slots=True)
class SpecSnapshot:
    feature_dir: Path
    workflow: str


def _path(path: Path) -> str:
    return path.as_posix()


def _diag(code: str, path: Path, line: int, message: str, verdict: str = "policy-fail") -> Diagnostic:
    return Diagnostic(code, verdict, SourceLocation(_path(path), line), message)


def _lines(data: bytes, path: Path) -> tuple[list[str], tuple[Diagnostic, ...]]:
    try:
        return data.decode("utf-8").splitlines(), ()
    except UnicodeDecodeError:
        return [], (_diag("ENGINE_INTERNAL", path, 1, "artifact ไม่ใช่ UTF-8", "engine-fail"),)


def _fence_subject(line: str) -> str:
    """คืนข้อความที่ใช้ตรวจ fence โดยไม่เปลี่ยน source line ที่ caller เห็น."""
    subject = line
    while True:
        container = FENCE_CONTAINER_RE.match(subject)
        if container:
            subject = subject[container.end():]
            continue
        list_item = FENCE_LIST_RE.match(subject)
        if list_item:
            subject = subject[list_item.end():]
            continue
        return subject


def _outside_fence(lines: Iterable[str], path: Path) -> tuple[list[tuple[int, str]], tuple[Diagnostic, ...]]:
    visible: list[tuple[int, str]] = []
    marker: str | None = None
    opening_length = 0
    opening_line = 0
    for number, line in enumerate(lines, 1):
        subject = _fence_subject(line)
        if marker is None:
            opening = FENCE_OPEN_RE.match(subject)
            if opening:
                marker = opening.group(1)[0]
                opening_length = len(opening.group(1))
                opening_line = number
            else:
                visible.append((number, line))
            continue
        closing = FENCE_CLOSE_RE.match(subject)
        if closing and closing.group(1)[0] == marker and len(closing.group(1)) >= opening_length:
            marker = None
            opening_length = 0
    if marker is not None:
        return visible, (_diag("TRACE_FENCE_UNCLOSED", path, opening_line, "code fence ไม่ปิด"),)
    return visible, ()


def _valid_date(value: str) -> bool:
    try:
        from datetime import date
        date.fromisoformat(value)
        return True
    except ValueError:
        return False


def parse_status(data: bytes, path: Path) -> tuple[ArtifactStatus | None, tuple[Diagnostic, ...]]:
    lines, diagnostics = _lines(data, path)
    if diagnostics:
        return None, diagnostics
    visible, fence_diagnostics = _outside_fence(lines, path)
    candidates = [(number, line) for number, line in visible if STATUS_PREFIX_RE.match(line)]
    if not candidates:
        return None, fence_diagnostics + (_diag("STATUS_MISSING", path, 1, "ไม่พบ status canonical"),)
    if len(candidates) > 1:
        code = "STATUS_MULTIPLE" if len({line for _, line in candidates}) == 1 else "STATUS_CONFLICT"
        return None, fence_diagnostics + (_diag(code, path, candidates[1][0], "พบ status มากกว่าหนึ่งบรรทัด"),)
    number, line = candidates[0]
    match = STATUS_RE.match(line)
    if not match:
        return None, fence_diagnostics + (_diag("STATUS_MALFORMED", path, number, "status ไม่ตรง canonical grammar"),)
    raw = match.group(1)
    if raw == "draft":
        status = ArtifactStatus("draft", None, None, SourceLocation(_path(path), number))
    elif raw == "unknown":
        status = ArtifactStatus("unknown", None, None, SourceLocation(_path(path), number))
    elif raw.startswith("approved "):
        date = match.group(2)
        if not _valid_date(date):
            return None, fence_diagnostics + (_diag("STATUS_MALFORMED", path, number, "วันที่ status ไม่ถูกต้อง"),)
        status = ArtifactStatus("approved", date, None, SourceLocation(_path(path), number))
    else:
        date, target = match.group(3), match.group(4)
        if not _valid_date(date):
            return None, fence_diagnostics + (_diag("STATUS_MALFORMED", path, number, "วันที่ status ไม่ถูกต้อง"),)
        status = ArtifactStatus("superseded", date, target, SourceLocation(_path(path), number))
    return status, fence_diagnostics


def _metadata_values(
    lines: list[str], start: int, end: int, label: str, visible_numbers: set[int]
) -> tuple[str, ...]:
    values: list[str] = []
    marker = f"{label}:"
    for index in range(start, end):
        if index + 1 not in visible_numbers:
            continue
        stripped = lines[index].strip()
        if stripped == "Evidence:":
            break
        if stripped.startswith(marker):
            value = stripped[len(marker):].strip()
            if label == "Verify":
                values.append(value)
            else:
                values.extend(part.strip() for part in value.split(",") if part.strip())
    return tuple(values)


def _looks_like_task_opening(line: str) -> bool:
    """จำแนก task-like opening เพื่อปิด metadata leakage โดยไม่ normalize task ID."""
    match = re.match(r"^\s*(?:[-+*]|[0-9]+[.)])", line)
    if not match:
        return False
    suffix = line[match.end():]
    normalized, limit_hit = _detection_fixed_point(suffix, task_prefix=True)
    return limit_hit or normalized.startswith(("[", "x]", "X]", " ]"))


def _classify_task_line(line: str) -> tuple[str, re.Match[str] | None]:
    canonical = TASK_OPENING_RE.match(line)
    if canonical and re.fullmatch(TASK_ID_PATTERN, canonical.group(2)):
        return "canonical", canonical
    if _looks_like_task_opening(line):
        return "malformed", None
    return "ordinary", None


def parse_task_blocks(data: bytes, path: Path) -> tuple[tuple[TaskBlock, ...], tuple[Diagnostic, ...]]:
    lines, diagnostics = _lines(data, path)
    if diagnostics:
        return (), diagnostics
    visible, fence_diagnostics = _outside_fence(lines, path)
    visible_numbers = {number for number, _ in visible}
    tasks: list[TaskBlock] = []
    found: dict[str, TaskBlock] = {}
    problems: list[Diagnostic] = list(fence_diagnostics)
    boundaries: list[tuple[int, str, re.Match[str] | None]] = []
    for number, line in visible:
        kind, match = _classify_task_line(line)
        if kind == "malformed":
            problems.append(_diag("TASK_ID_INVALID", path, number, "task opening ไม่ตรง canonical grammar"))
            boundaries.append((number - 1, kind, None))
        elif kind == "canonical":
            boundaries.append((number - 1, kind, match))
    for ordinal, (start, kind, match) in enumerate(boundaries):
        if kind != "canonical":
            continue
        assert match is not None
        end = boundaries[ordinal + 1][0] if ordinal + 1 < len(boundaries) else len(lines)
        task = TaskBlock(
            task_id=match.group(2),
            title=match.group(3),
            completed=match.group(1) == "x",
            ordinal=len(tasks),
            span=(start + 1, end),
            location=SourceLocation(_path(path), start + 1),
            satisfies=_metadata_values(lines, start + 1, end, "Satisfies", visible_numbers),
            depends_on=_metadata_values(lines, start + 1, end, "Depends on", visible_numbers),
            verify=_metadata_values(lines, start + 1, end, "Verify", visible_numbers),
            batch=_metadata_values(lines, start + 1, end, "Batch", visible_numbers),
        )
        if task.task_id in found:
            problems.append(_diag("TASK_ID_DUPLICATE", path, start + 1, "task ID ซ้ำ"))
        else:
            found[task.task_id] = task
        tasks.append(task)
    return tuple(tasks), tuple(diagnostics) + tuple(problems)


def validate_task_graph(tasks: Sequence[TaskBlock]) -> tuple[Diagnostic, ...]:
    known = {task.task_id: task for task in tasks}
    diagnostics: list[Diagnostic] = []
    graph: dict[str, tuple[str, ...]] = {}
    for task in tasks:
        unknown = [dependency for dependency in task.depends_on if dependency not in known]
        for dependency in unknown:
            diagnostics.append(_diag("TASK_DEPENDENCY_UNKNOWN", Path(task.location.path), task.location.line, f"ไม่พบ dependency '{dependency}'"))
        graph[task.task_id] = tuple(dependency for dependency in task.depends_on if dependency in known)
    visiting: set[str] = set()
    visited: set[str] = set()

    def visit(task_id: str) -> None:
        if task_id in visited:
            return
        if task_id in visiting:
            task = known[task_id]
            diagnostics.append(_diag("TASK_DEPENDENCY_CYCLE", Path(task.location.path), task.location.line, "dependency graph มี cycle"))
            return
        visiting.add(task_id)
        for dependency in graph[task_id]:
            visit(dependency)
        visiting.remove(task_id)
        visited.add(task_id)

    for task in tasks:
        visit(task.task_id)
    return tuple(diagnostics)


def resolve_task_selector(tasks: Sequence[TaskBlock], selector: str) -> tuple[tuple[str, ...], tuple[Diagnostic, ...]]:
    by_id = {task.task_id: task for task in tasks}
    if selector in by_id:
        return (selector,), ()
    match = re.fullmatch(r"([0-9]+)-([0-9]+)", selector)
    location = tasks[0].location if tasks else SourceLocation("tasks.md", 1)
    if not match or match.group(1) not in by_id or match.group(2) not in by_id:
        return (), (_diag("TASK_SELECTOR_AMBIGUOUS", Path(location.path), location.line, f"selector '{selector}' ไม่ชัดเจน"),)
    first, last = by_id[match.group(1)].ordinal, by_id[match.group(2)].ordinal
    if first > last:
        return (), (_diag("TASK_SELECTOR_AMBIGUOUS", Path(location.path), location.line, f"selector '{selector}' กลับลำดับ"),)
    return tuple(task.task_id for task in tasks[first:last + 1]), ()


def _ears_ok(statement: str) -> bool:
    normalized = " ".join(statement.split())
    if re.fullmatch(r"THE SYSTEM SHALL\s+.+", normalized):
        return True
    for word in ("WHEN", "WHILE", "WHERE"):
        if re.fullmatch(rf"{word}\s+.+\s+THE SYSTEM SHALL\s+.+", normalized):
            return True
    return bool(re.fullmatch(r"IF\s+.+\s+THEN THE SYSTEM SHALL\s+.+", normalized))


def _strip_wrapper(value: str, *, stop_before_checkbox: bool = False) -> str:
    """ลอก prefix wrapper ที่รองรับเพื่อจำแนก malformed โดยไม่สร้าง canonical token."""
    token = value.strip()
    while True:
        if token.startswith("~~"):
            token = token[2:].lstrip()
            continue
        if token[:1] in {"*", "_", "`", "[", "("}:
            if stop_before_checkbox and token.startswith("["):
                return token
            token = token[1:].lstrip()
            continue
        return token


def _detection_token(value: str) -> str:
    """สร้างสำเนา detection-only; output นี้ห้ามใช้สร้าง ref หรือ Task ID."""
    characters: list[str] = []
    for character in unicodedata.normalize("NFKC", value):
        category = unicodedata.category(character)
        if category == "Cf":
            continue
        if category == "Nd":
            characters.append(str(unicodedata.decimal(character)))
        else:
            characters.append(character)
    return "".join(characters)


def _detection_fixed_point(value: str, *, task_prefix: bool = False) -> tuple[str, bool]:
    """คืน detection token; ทุก branch ที่ไม่ stable ต้องลดความยาว token."""
    token = _detection_token(value)
    while True:
        normalized = _detection_token(token)
        next_token = _strip_wrapper(normalized, stop_before_checkbox=task_prefix)
        if next_token == token:
            return token, False
        if len(next_token) >= len(token):
            return token, True
        token = next_token


def _looks_like_malformed_feature_criterion(value: str) -> bool:
    token, limit_hit = _detection_fixed_point(value)
    return limit_hit or bool(re.match(r"^(?i:REQ)\s*[-:]?\s*[0-9]+\s*[.:-]\s*[0-9]+(?=\s|[)\]*_`:.~\-]|$)", token) or re.match(r"^[0-9]+\s*[.:-]\s*[0-9]+(?=\s|[)\]*_`:.~\-]|$)", token))


def _looks_like_malformed_bugfix_criterion(value: str) -> bool:
    token, limit_hit = _detection_fixed_point(value)
    return limit_hit or bool(re.match(r"^(?i:[FB])\s*(?:[-:_]+\s*)?[0-9]+(?=\s|[)\]*_`:.~\-]|$)", token))


def _classify_feature_criterion(line: str) -> tuple[str, re.Match[str] | None]:
    canonical = REQ_CRITERION_RE.match(line)
    if canonical:
        return "canonical", canonical
    item = CRITERION_LIST_PREFIX_RE.match(line)
    if item and _looks_like_malformed_feature_criterion(item.group(1)):
        return "near-miss", None
    return "ordinary", None


def _classify_bugfix_criterion(line: str) -> tuple[str, re.Match[str] | None]:
    canonical = BUGFIX_CRITERION_RE.match(line)
    if canonical:
        return "canonical", canonical
    item = CRITERION_LIST_PREFIX_RE.match(line)
    if item and _looks_like_malformed_bugfix_criterion(item.group(1)):
        return "near-miss", None
    return "ordinary", None


def _looks_like_req_heading(line: str) -> bool:
    return bool(re.match(r"^\s*#{1,6}\s+(?i:REQ)\s*[-:]?\s*[0-9]+", _detection_token(line)))


def _is_sibling_major_heading(line: str) -> bool:
    return bool(re.match(r"^\s*#{1,2}(?:\s|$)", line))


def parse_requirement_criteria(data: bytes, path: Path) -> tuple[tuple[RequirementCriterion, ...], tuple[Diagnostic, ...]]:
    lines, diagnostics = _lines(data, path)
    if diagnostics:
        return (), diagnostics
    visible, fence_diagnostics = _outside_fence(lines, path)
    criteria: list[RequirementCriterion] = []
    problems: list[Diagnostic] = list(fence_diagnostics)
    major: int | None = None
    headings: list[int] = []
    seen: set[str] = set()
    for number, line in visible:
        heading = REQ_HEADING_RE.match(line)
        if heading:
            major = int(heading.group(1))
            headings.append(number)
            continue
        if _is_sibling_major_heading(line):
            major = None
            if _looks_like_req_heading(line):
                problems.append(_diag("EARS_HEADING_MALFORMED", path, number, "REQ heading ไม่ตรง canonical grammar"))
            continue
        if _looks_like_req_heading(line):
            problems.append(_diag("EARS_HEADING_MALFORMED", path, number, "REQ heading ไม่ตรง canonical grammar"))
            continue
        classification, match = _classify_feature_criterion(line)
        if classification == "canonical":
            if major is None:
                problems.append(_diag("EARS_CRITERION_MALFORMED", path, number, "criterion อยู่นอก canonical REQ section"))
                continue
            assert match is not None
            criterion_major, minor, statement = int(match.group(1)), match.group(2), match.group(3)
            ref = f"REQ-{criterion_major}.{minor}"
            criterion = RequirementCriterion(ref, statement, f"REQ-{major}", SourceLocation(_path(path), number))
            criteria.append(criterion)
            if criterion_major != major:
                problems.append(_diag("EARS_MAJOR_MISMATCH", path, number, "criterion major ไม่ตรง REQ heading"))
            if ref in seen:
                problems.append(_diag("EARS_ID_DUPLICATE", path, number, "criterion ID ซ้ำ"))
            seen.add(ref)
            if not _ears_ok(statement):
                problems.append(_diag("EARS_FORM_INVALID", path, number, "criterion ไม่ใช่ EARS รูปเต็ม"))
            continue
        if classification == "near-miss":
            problems.append(_diag("EARS_CRITERION_MALFORMED", path, number, "criterion คล้าย canonical grammar แต่ไม่ตรงรูปแบบ"))
    if not headings:
        problems.append(_diag("EARS_HEADING_MALFORMED", path, 1, "ไม่พบ canonical REQ heading"))
    elif not criteria:
        problems.append(_diag("EARS_CRITERION_MALFORMED", path, headings[0], "REQ heading ไม่มี criterion canonical"))
    return tuple(criteria), tuple(problems)


def parse_bugfix_criteria(data: bytes, path: Path) -> tuple[tuple[RequirementCriterion, ...], tuple[Diagnostic, ...]]:
    lines, diagnostics = _lines(data, path)
    if diagnostics:
        return (), diagnostics
    visible, fence_diagnostics = _outside_fence(lines, path)
    criteria: list[RequirementCriterion] = []
    problems: list[Diagnostic] = list(fence_diagnostics)
    seen: set[str] = set()
    first_visible_line = visible[0][0] if visible else 1
    for number, line in visible:
        classification, match = _classify_bugfix_criterion(line)
        if classification == "canonical":
            assert match is not None
            ref, statement = match.groups()
            criteria.append(RequirementCriterion(ref, statement, None, SourceLocation(_path(path), number)))
            if ref in seen:
                problems.append(_diag("EARS_ID_DUPLICATE", path, number, "criterion ID ซ้ำ"))
            seen.add(ref)
            if not _ears_ok(statement):
                problems.append(_diag("EARS_FORM_INVALID", path, number, "criterion ไม่ใช่ EARS รูปเต็ม"))
            continue
        if classification == "near-miss":
            problems.append(_diag("EARS_CRITERION_MALFORMED", path, number, "criterion F/B คล้าย canonical grammar แต่ไม่ตรงรูปแบบ"))
    if not criteria:
        problems.append(_diag("EARS_CRITERION_MALFORMED", path, first_visible_line, "bugfix ไม่มี criterion F/B canonical"))
    return tuple(criteria), tuple(problems)


def _table_cells(line: str) -> list[str]:
    cells = line.split("|")
    if line.lstrip().startswith("|"):
        cells = cells[1:]
    if line.rstrip().endswith("|"):
        cells = cells[:-1]
    return [cell.strip() for cell in cells]


def _expand_refs(value: str, known: set[str]) -> tuple[tuple[str, ...], bool]:
    value = value.strip()
    if not value:
        return (), False
    result: list[str] = []
    invalid = False
    for token in (part.strip() for part in value.split(",") if part.strip()):
        range_match = RANGE_REF_RE.fullmatch(token)
        if range_match:
            a1, b1, a2, b2 = range_match.groups()
            if a1 != a2:
                invalid = True
                continue
            for minor in range(int(b1), int(b2) + 1):
                ref = f"REQ-{a1}.{minor}"
                if ref not in known:
                    invalid = True
                else:
                    result.append(ref)
            continue
        if WHOLE_REF_RE.fullmatch(token):
            result.extend(sorted(ref for ref in known if ref.startswith(f"{token}.")))
            continue
        if STRICT_REF_RE.fullmatch(token):
            if token not in known:
                invalid = True
            else:
                result.append(token)
            continue
        invalid = True
    return tuple(dict.fromkeys(result)), invalid


def parse_traceability_table(data: bytes, path: Path, known_refs: set[str]) -> tuple[tuple[TraceRow, ...], tuple[DesignSection, ...], tuple[Diagnostic, ...]]:
    lines, decode_diagnostics = _lines(data, path)
    if decode_diagnostics:
        return (), (), decode_diagnostics
    visible, fence_diagnostics = _outside_fence(lines, path)
    sections = tuple(DesignSection(line[3:].strip(), (number, number)) for number, line in visible if line.startswith("## "))
    start = next((index for index, (_, line) in enumerate(visible) if line == "## Requirement Traceability"), None)
    if start is None:
        return (), sections, fence_diagnostics + (_diag("TRACE_COLUMNS_MISSING", path, 1, "ไม่พบ Requirement Traceability"),)
    table: list[tuple[int, str]] = []
    for number, line in visible[start + 1:]:
        if line.startswith("## "):
            break
        if "|" in line:
            table.append((number, line))
    if not table:
        return (), sections, fence_diagnostics + (_diag("TRACE_COLUMNS_MISSING", path, visible[start][0], "ไม่พบ trace table"),)
    headers = _table_cells(table[0][1])
    if "REQ" not in headers or "Section" not in headers:
        return (), sections, fence_diagnostics + (_diag("TRACE_COLUMNS_MISSING", path, table[0][0], "trace table ต้องมี REQ และ Section"),)
    req_index, section_index = headers.index("REQ"), headers.index("Section")
    rows: list[TraceRow] = []
    problems: list[Diagnostic] = list(fence_diagnostics)
    headings = {section.heading for section in sections}
    for number, line in table[1:]:
        cells = _table_cells(line)
        if all(re.fullmatch(r":?-{3,}:?", cell) for cell in cells if cell):
            continue
        req_value = cells[req_index] if req_index < len(cells) else ""
        section = cells[section_index] if section_index < len(cells) else ""
        refs, invalid = _expand_refs(req_value, known_refs)
        if invalid:
            problems.append(_diag("TRACE_REF_UNKNOWN", path, number, "trace reference ไม่ตรง canonical grammar"))
        if section not in headings:
            problems.append(_diag("TRACE_SECTION_UNKNOWN", path, number, "Section ไม่ตรง ## heading แบบ exact"))
        rows.append(TraceRow(refs, section, SourceLocation(_path(path), number)))
    return tuple(rows), sections, tuple(problems)


def _read_status(path: Path) -> tuple[ArtifactStatus | None, tuple[Diagnostic, ...]]:
    if not path.is_file():
        return None, (_diag("PHASE_UPSTREAM_NOT_APPROVED", path, 1, "artifact upstream หาย"),)
    return parse_status(path.read_bytes(), path)


def _task_coverage(tasks: Sequence[TaskBlock], criteria: set[str]) -> set[str]:
    covered: set[str] = set()
    for task in tasks:
        for value in task.satisfies:
            if value in criteria:
                covered.add(value)
            elif value.startswith("REQ-"):
                refs, _ = _expand_refs(value, criteria)
                covered.update(refs)
            elif value in criteria:
                covered.add(value)
    return covered


def _feature_trace(feature_dir: Path) -> tuple[Diagnostic, ...]:
    requirements_path, design_path, tasks_path = (feature_dir / name for name in ("requirements.md", "design.md", "tasks.md"))
    criteria, problems = parse_requirement_criteria(requirements_path.read_bytes(), requirements_path)
    diagnostics: list[Diagnostic] = list(problems)
    known = {criterion.ref for criterion in criteria}
    rows, _, trace_problems = parse_traceability_table(design_path.read_bytes(), design_path, known)
    diagnostics.extend(trace_problems)
    trace_covered = {ref for row in rows for ref in row.refs}
    tasks, task_problems = parse_task_blocks(tasks_path.read_bytes(), tasks_path)
    diagnostics.extend(task_problems)
    diagnostics.extend(validate_task_graph(tasks))
    task_covered = _task_coverage(tasks, known)
    for ref in sorted(known - trace_covered):
        diagnostics.append(_diag("TRACE_REF_UNKNOWN", design_path, 1, f"design trace ไม่ครอบ {ref}"))
    for ref in sorted(known - task_covered):
        diagnostics.append(_diag("TRACE_REF_UNKNOWN", tasks_path, 1, f"tasks trace ไม่ครอบ {ref}"))
    return tuple(diagnostics)


def _bugfix_trace(feature_dir: Path) -> tuple[Diagnostic, ...]:
    bugfix_path, tasks_path = feature_dir / "bugfix.md", feature_dir / "tasks.md"
    criteria, diagnostics = parse_bugfix_criteria(bugfix_path.read_bytes(), bugfix_path)
    tasks, task_diagnostics = parse_task_blocks(tasks_path.read_bytes(), tasks_path)
    values = {value for task in tasks for value in task.satisfies}
    problems = list(diagnostics) + list(task_diagnostics) + list(validate_task_graph(tasks))
    for criterion in criteria:
        if criterion.ref not in values:
            problems.append(_diag("PHASE_TRACE_INVALID", tasks_path, 1, f"tasks trace ไม่ครอบ {criterion.ref}"))
    return tuple(problems)


PHASE_REQUIREMENTS: dict[tuple[str, str], tuple[str, ...]] = {
    ("requirements-first", "design"): ("requirements.md",),
    ("requirements-first", "tasks"): ("requirements.md", "design.md"),
    ("requirements-first", "implement"): ("requirements.md", "design.md", "tasks.md"),
    ("design-first", "design"): (),
    ("design-first", "requirements"): ("design.md",),
    ("design-first", "tasks"): ("requirements.md", "design.md"),
    ("design-first", "implement"): ("requirements.md", "design.md", "tasks.md"),
    ("bugfix", "tasks"): ("bugfix.md",),
    ("bugfix", "implement"): ("bugfix.md", "tasks.md"),
}


def check_phase_gate(feature_dir: Path, phase: str, workflow: str) -> tuple[SpecSnapshot, tuple[Diagnostic, ...]]:
    snapshot = SpecSnapshot(feature_dir, workflow)
    if workflow not in {"requirements-first", "design-first", "bugfix"}:
        return snapshot, (_diag("PHASE_WORKFLOW_AMBIGUOUS", feature_dir, 1, "workflow ไม่รองรับ"),)
    required = PHASE_REQUIREMENTS.get((workflow, phase))
    if required is None:
        return snapshot, (_diag("PHASE_WORKFLOW_UNSUPPORTED", feature_dir, 1, "workflow ไม่รองรับ phase นี้"),)
    diagnostics: list[Diagnostic] = []
    if workflow == "design-first" and phase == "design":
        for name in ("requirements.md", "tasks.md", "bugfix.md"):
            if (feature_dir / name).exists():
                diagnostics.append(_diag("PHASE_WORKFLOW_UNSUPPORTED", feature_dir / name, 1, "Design-first design ต้องเริ่มโดยไม่มี artifact อื่น"))
    for name in required:
        status, status_diagnostics = _read_status(feature_dir / name)
        if status is None or status.kind != "approved" or status_diagnostics:
            diagnostics.append(_diag("PHASE_UPSTREAM_NOT_APPROVED", feature_dir / name, 1, "artifact upstream ไม่ approved"))
            diagnostics.extend(status_diagnostics)
    if not diagnostics and phase == "implement":
        trace = _bugfix_trace(feature_dir) if workflow == "bugfix" else _feature_trace(feature_dir)
        if trace:
            diagnostics.append(_diag("PHASE_TRACE_INVALID", feature_dir, 1, "trace contract ไม่ผ่าน"))
            diagnostics.extend(trace)
    return snapshot, tuple(diagnostics)


def _print_contract_diagnostics(feature: str, diagnostics: Sequence[Diagnostic]) -> int:
    print(f"[{feature}] strict contract ไม่ผ่าน:")
    for diagnostic in sorted(diagnostics, key=lambda value: (value.location.path, value.location.line, value.code, value.message)):
        print(f"  - {diagnostic.code}: {diagnostic.message}")
    return 1


def trace_run(feature: str, specs_dir: Path) -> int:
    feature_dir = specs_dir / feature
    if not feature_dir.is_dir():
        print(f"ไม่พบ feature '{feature}' ใต้ {specs_dir}", file=sys.stderr)
        return 1
    workflow = "bugfix" if (feature_dir / "bugfix.md").is_file() and not (feature_dir / "requirements.md").is_file() else "requirements-first"
    _, diagnostics = check_phase_gate(feature_dir, "implement", workflow)
    if diagnostics:
        return _print_contract_diagnostics(feature, diagnostics)
    if workflow == "bugfix":
        criteria, _ = parse_bugfix_criteria((feature_dir / "bugfix.md").read_bytes(), feature_dir / "bugfix.md")
        print(f"OK: '{feature}' เกณฑ์ F/B {len(criteria)} ข้อ ถูกอ้างครบใน tasks.md, EARS lint ผ่านทุกข้อ")
        return 0
    criteria, _ = parse_requirement_criteria((feature_dir / "requirements.md").read_bytes(), feature_dir / "requirements.md")
    print(f"OK: '{feature}' เกณฑ์ {len(criteria)} ข้อ ถูกอ้างครบใน design.md และ tasks.md, EARS lint ผ่านทุกข้อ")
    return 0


def _cli(argv: Sequence[str]) -> int:
    specs_dir = Path(__file__).resolve().parent.parent / ".ai" / "specs"
    if len(argv) >= 2 and argv[0] == "gate" and argv[1] == "phase":
        parser = argparse.ArgumentParser(add_help=False)
        parser.add_argument("--feature", required=True)
        parser.add_argument("--phase", required=True)
        parser.add_argument("--workflow", required=True)
        args = parser.parse_args(argv[2:])
        _, diagnostics = check_phase_gate(specs_dir / args.feature, args.phase, args.workflow)
        if diagnostics:
            return _print_contract_diagnostics(args.feature, diagnostics)
        print(f"OK: '{args.feature}' phase '{args.phase}' ผ่าน workflow '{args.workflow}'")
        return 0
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("command", nargs="?")
    parser.add_argument("feature", nargs="?")
    parser.add_argument("--feature", dest="named_feature")
    parser.add_argument("--strict", action="store_true")
    args = parser.parse_args(argv)
    if args.command == "check" and (args.named_feature or args.feature):
        return trace_run(args.named_feature or args.feature, specs_dir)
    if args.command and not args.feature and not args.named_feature:
        return trace_run(args.command, specs_dir)
    print("ใช้: spec_contract.py check --feature FEATURE --strict", file=sys.stderr)
    return 2


if __name__ == "__main__":
    try:
        raise SystemExit(_cli(sys.argv[1:]))
    except (OSError, ValueError) as error:
        print(f"ENGINE_INTERNAL: {error}", file=sys.stderr)
        raise SystemExit(2)
