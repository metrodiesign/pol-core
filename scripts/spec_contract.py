#!/usr/bin/env python3
"""Canonical read-only contract สำหรับ SDD Markdown artifacts."""
from __future__ import annotations

import argparse
import json
import re
import stat
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


def resolve_feature_directory(specs_root: Path, feature: str) -> tuple[Path | None, tuple[Diagnostic, ...]]:
    """Resolve one direct, real spec child without traversal or symlink escape."""
    if feature == "archive" or not re.fullmatch(TASK_ID_PATTERN, feature):
        return None, (_diag("SLICE_FEATURE_UNKNOWN", specs_root / feature, 1, "feature ต้องเป็น canonical direct child"),)
    if specs_root.is_symlink() or not specs_root.is_dir():
        return None, (_diag("ENGINE_INTERNAL", specs_root, 1, "canonical specs root ใช้งานไม่ได้", "engine-fail"),)
    directory = specs_root / feature
    if not directory.is_dir() or directory.is_symlink():
        return None, (_diag("SLICE_FEATURE_UNKNOWN", directory, 1, "ไม่พบ canonical spec directory"),)
    return directory, ()


def _canonical_location_diagnostics(feature_dir: Path, specs_root: Path) -> tuple[Diagnostic, ...]:
    try:
        relative = feature_dir.relative_to(specs_root)
    except ValueError:
        return (_diag("STATE_ARTIFACT_BLOCKED", feature_dir, 1, "spec directory อยู่นอก canonical specs root"),)
    valid = len(relative.parts) == 1 or (len(relative.parts) == 2 and relative.parts[0] == "archive")
    if not valid or feature_dir.is_symlink():
        return (_diag("STATE_ARTIFACT_BLOCKED", feature_dir, 1, "spec directory ไม่ใช่ canonical direct child"),)
    current = specs_root
    for part in relative.parts:
        current = current / part
        if current.is_symlink():
            return (_diag("STATE_ARTIFACT_BLOCKED", current, 1, "spec path มี symlink"),)
    return ()


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
    # CommonMark: ATX heading รับ indent ได้ไม่เกิน 3 space; 4 space ขึ้นไปหรือ tab คือ indented code block
    return bool(re.match(r"^ {0,3}#{1,2}(?:[ \t]|$)", line))


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


def _read_canonical_artifact(
    feature_dir: Path, name: str, canonical_specs_root: Path | None = None
) -> tuple[bytes | None, tuple[Diagnostic, ...]]:
    """Read one regular direct child without following an artifact symlink."""
    specs_root = canonical_specs_root or feature_dir.parent
    path = feature_dir / name
    if (
        Path(name).name != name
        or specs_root.is_symlink()
        or not specs_root.is_dir()
        or feature_dir.parent != specs_root
        or feature_dir.is_symlink()
        or not feature_dir.is_dir()
        or feature_dir.resolve().parent != specs_root.resolve()
    ):
        return None, (_diag("STATE_ARTIFACT_BLOCKED", path, 1, "artifact ไม่อยู่ใต้ canonical direct child"),)
    if path.is_symlink() or not path.is_file() or not stat.S_ISREG(path.stat().st_mode):
        return None, (_diag("STATE_ARTIFACT_BLOCKED", path, 1, "artifact file ต้องเป็น regular file และห้ามเป็น symlink"),)
    return path.read_bytes(), ()


def _read_status(
    feature_dir: Path, name: str, canonical_specs_root: Path | None = None
) -> tuple[ArtifactStatus | None, tuple[Diagnostic, ...]]:
    data, diagnostics = _read_canonical_artifact(feature_dir, name, canonical_specs_root)
    path = feature_dir / name
    if data is None:
        return None, diagnostics
    status, status_diagnostics = parse_status(data, path)
    return status, diagnostics + status_diagnostics


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


def _feature_trace(feature_dir: Path, canonical_specs_root: Path | None = None) -> tuple[Diagnostic, ...]:
    names = ("requirements.md", "design.md", "tasks.md")
    artifacts = {
        name: _read_canonical_artifact(feature_dir, name, canonical_specs_root)
        for name in names
    }
    read_diagnostics = tuple(
        diagnostic for _, diagnostics in artifacts.values() for diagnostic in diagnostics
    )
    if read_diagnostics:
        return read_diagnostics
    requirements_path, design_path, tasks_path = (feature_dir / name for name in names)
    requirements_data, design_data, tasks_data = (artifacts[name][0] for name in names)
    assert requirements_data is not None and design_data is not None and tasks_data is not None
    criteria, problems = parse_requirement_criteria(requirements_data, requirements_path)
    diagnostics: list[Diagnostic] = list(problems)
    known = {criterion.ref for criterion in criteria}
    rows, _, trace_problems = parse_traceability_table(design_data, design_path, known)
    diagnostics.extend(trace_problems)
    trace_covered = {ref for row in rows for ref in row.refs}
    tasks, task_problems = parse_task_blocks(tasks_data, tasks_path)
    diagnostics.extend(task_problems)
    diagnostics.extend(validate_task_graph(tasks))
    task_covered = _task_coverage(tasks, known)
    for ref in sorted(known - trace_covered):
        diagnostics.append(_diag("TRACE_REF_UNKNOWN", design_path, 1, f"design trace ไม่ครอบ {ref}"))
    for ref in sorted(known - task_covered):
        diagnostics.append(_diag("TRACE_REF_UNKNOWN", tasks_path, 1, f"tasks trace ไม่ครอบ {ref}"))
    return tuple(diagnostics)


def _bugfix_trace(feature_dir: Path, canonical_specs_root: Path | None = None) -> tuple[Diagnostic, ...]:
    names = ("bugfix.md", "tasks.md")
    artifacts = {
        name: _read_canonical_artifact(feature_dir, name, canonical_specs_root)
        for name in names
    }
    read_diagnostics = tuple(
        diagnostic for _, diagnostics in artifacts.values() for diagnostic in diagnostics
    )
    if read_diagnostics:
        return read_diagnostics
    bugfix_path, tasks_path = (feature_dir / name for name in names)
    bugfix_data, tasks_data = (artifacts[name][0] for name in names)
    assert bugfix_data is not None and tasks_data is not None
    criteria, diagnostics = parse_bugfix_criteria(bugfix_data, bugfix_path)
    tasks, task_diagnostics = parse_task_blocks(tasks_data, tasks_path)
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

KNOWN_ARTIFACTS = frozenset({"requirements.md", "design.md", "tasks.md", "bugfix.md"})
WORKFLOW_ARTIFACTS = {
    "requirements-first": frozenset({"requirements.md", "design.md", "tasks.md"}),
    "design-first": frozenset({"requirements.md", "design.md", "tasks.md"}),
    "bugfix": frozenset({"bugfix.md", "tasks.md"}),
}


def _known_artifact_names(feature_dir: Path) -> frozenset[str]:
    return frozenset(
        name
        for name in KNOWN_ARTIFACTS
        if (feature_dir / name).exists() or (feature_dir / name).is_symlink()
    )


def _workflow_shape_diagnostics(feature_dir: Path, workflow: str, phase: str) -> tuple[Diagnostic, ...]:
    known = _known_artifact_names(feature_dir)
    if workflow == "design-first" and phase == "design":
        return tuple(
            _diag("PHASE_WORKFLOW_UNSUPPORTED", feature_dir / name, 1, "Design-first design ต้องเริ่มโดยไม่มี artifact อื่น")
            for name in sorted(known - {"design.md"})
        )
    return tuple(
        _diag("PHASE_WORKFLOW_AMBIGUOUS", feature_dir / name, 1, "artifact ขัดกับ workflow ที่ caller ระบุ")
        for name in sorted(known - WORKFLOW_ARTIFACTS[workflow])
    )


def check_phase_gate(
    feature_dir: Path, phase: str, workflow: str, canonical_specs_root: Path | None = None
) -> tuple[SpecSnapshot, tuple[Diagnostic, ...]]:
    snapshot = SpecSnapshot(feature_dir, workflow)
    if canonical_specs_root is not None:
        resolved, resolver_diagnostics = resolve_feature_directory(canonical_specs_root, feature_dir.name)
        if resolver_diagnostics or resolved != feature_dir:
            return snapshot, resolver_diagnostics or (_diag("SLICE_FEATURE_UNKNOWN", feature_dir, 1, "feature ไม่ใช่ canonical direct child"),)
    if workflow not in {"requirements-first", "design-first", "bugfix"}:
        return snapshot, (_diag("PHASE_WORKFLOW_AMBIGUOUS", feature_dir, 1, "workflow ไม่รองรับ"),)
    required = PHASE_REQUIREMENTS.get((workflow, phase))
    if required is None:
        return snapshot, (_diag("PHASE_WORKFLOW_UNSUPPORTED", feature_dir, 1, "workflow ไม่รองรับ phase นี้"),)
    diagnostics: list[Diagnostic] = list(_workflow_shape_diagnostics(feature_dir, workflow, phase))
    for name in required:
        status, status_diagnostics = _read_status(feature_dir, name, canonical_specs_root)
        if status is None or status.kind != "approved" or status_diagnostics:
            diagnostics.append(_diag("PHASE_UPSTREAM_NOT_APPROVED", feature_dir / name, 1, "artifact upstream ไม่ approved"))
            diagnostics.extend(status_diagnostics)
    if not diagnostics and phase == "implement":
        trace = (
            _bugfix_trace(feature_dir, canonical_specs_root)
            if workflow == "bugfix"
            else _feature_trace(feature_dir, canonical_specs_root)
        )
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
    feature_dir, resolver_diagnostics = resolve_feature_directory(specs_dir, feature)
    if resolver_diagnostics or feature_dir is None:
        _print_diagnostics(resolver_diagnostics)
        return 1
    workflow = "bugfix" if (feature_dir / "bugfix.md").is_file() and not (feature_dir / "requirements.md").is_file() else "requirements-first"
    _, diagnostics = check_phase_gate(feature_dir, "implement", workflow, specs_dir)
    if diagnostics:
        return _print_contract_diagnostics(feature, diagnostics)
    artifact_name = "bugfix.md" if workflow == "bugfix" else "requirements.md"
    data, read_diagnostics = _read_canonical_artifact(feature_dir, artifact_name, specs_dir)
    if data is None:
        return _print_contract_diagnostics(feature, read_diagnostics)
    path = feature_dir / artifact_name
    criteria, _ = (
        parse_bugfix_criteria(data, path)
        if workflow == "bugfix"
        else parse_requirement_criteria(data, path)
    )
    if workflow == "bugfix":
        print(f"OK: '{feature}' เกณฑ์ F/B {len(criteria)} ข้อ ถูกอ้างครบใน tasks.md, EARS lint ผ่านทุกข้อ")
    else:
        print(f"OK: '{feature}' เกณฑ์ {len(criteria)} ข้อ ถูกอ้างครบใน design.md และ tasks.md, EARS lint ผ่านทุกข้อ")
    return 0


def _slice_mapping_diagnostic(path: Path, line: int, message: str) -> Diagnostic:
    return _diag("SLICE_MAPPING_MISSING", path, line, message)


def _verbatim_task_block(data: bytes, task: TaskBlock) -> str:
    lines = data.decode("utf-8").splitlines(keepends=True)
    return "".join(lines[task.span[0] - 1:task.span[1]])


def _feature_requirement_blocks(
    data: bytes, path: Path, refs: set[str]
) -> tuple[list[str], tuple[Diagnostic, ...]]:
    lines, diagnostics = _lines(data, path)
    if diagnostics:
        return [], diagnostics
    visible, fence_diagnostics = _outside_fence(lines, path)
    raw_lines = data.decode("utf-8").splitlines(keepends=True)
    headings: list[tuple[int, str]] = []
    boundaries: list[int] = []
    for number, line in visible:
        if _is_sibling_major_heading(line):
            boundaries.append(number)
        match = REQ_HEADING_RE.match(line)
        if match:
            headings.append((number, f"REQ-{match.group(1)}"))
    criteria, criterion_diagnostics = parse_requirement_criteria(data, path)
    refs_by_heading: dict[str, set[str]] = {}
    for criterion in criteria:
        if criterion.heading is not None:
            refs_by_heading.setdefault(criterion.heading, set()).add(criterion.ref)
    blocks: list[str] = []
    found: set[str] = set()
    for start, heading in headings:
        end = next((number - 1 for number in boundaries if number > start), len(raw_lines))
        heading_refs = refs_by_heading.get(heading, set())
        if refs & heading_refs:
            blocks.append("".join(raw_lines[start - 1:end]))
            found.update(refs & heading_refs)
    missing = tuple(
        _slice_mapping_diagnostic(path, 1, f"ไม่พบ linked requirement {ref}")
        for ref in sorted(refs - found)
    )
    return blocks, fence_diagnostics + criterion_diagnostics + missing


def _bugfix_criterion_lines(
    data: bytes, path: Path, refs: set[str]
) -> tuple[list[str], tuple[Diagnostic, ...]]:
    criteria, diagnostics = parse_bugfix_criteria(data, path)
    lines = data.decode("utf-8").splitlines(keepends=True)
    blocks: list[str] = []
    found: set[str] = set()
    for criterion in criteria:
        if criterion.ref in refs:
            blocks.append(lines[criterion.location.line - 1])
            found.add(criterion.ref)
    missing = tuple(
        _slice_mapping_diagnostic(path, 1, f"ไม่พบ linked bugfix criterion {ref}")
        for ref in sorted(refs - found)
    )
    return blocks, diagnostics + missing


def _design_section_blocks(data: bytes, path: Path, headings: Sequence[str]) -> tuple[list[str], tuple[Diagnostic, ...]]:
    lines, diagnostics = _lines(data, path)
    if diagnostics:
        return [], diagnostics
    visible, fence_diagnostics = _outside_fence(lines, path)
    raw_lines = data.decode("utf-8").splitlines(keepends=True)
    boundaries = [number for number, line in visible if _is_sibling_major_heading(line)]
    starts = [(number, line[3:].strip()) for number, line in visible if line.startswith("## ")]
    wanted = set(headings)
    blocks: list[str] = []
    found: set[str] = set()
    for start, heading in starts:
        if heading not in wanted:
            continue
        end = next((number - 1 for number in boundaries if number > start), len(raw_lines))
        blocks.append("".join(raw_lines[start - 1:end]))
        found.add(heading)
    missing = tuple(
        _slice_mapping_diagnostic(path, 1, f"ไม่พบ mapped design section {heading}")
        for heading in sorted(wanted - found)
    )
    return blocks, fence_diagnostics + missing


def _slice_refs(task: TaskBlock, known: set[str]) -> tuple[set[str], tuple[Diagnostic, ...]]:
    refs: set[str] = set()
    diagnostics: list[Diagnostic] = []
    if not task.satisfies:
        diagnostics.append(_slice_mapping_diagnostic(Path(task.location.path), task.location.line, "task ไม่มี Satisfies:"))
    for value in task.satisfies:
        expanded, invalid = _expand_refs(value, known) if value.startswith("REQ-") else ((value,) if value in known else (), value not in known)
        refs.update(expanded)
        if invalid:
            diagnostics.append(_slice_mapping_diagnostic(Path(task.location.path), task.location.line, f"task อ้าง mapping ที่ไม่ resolve: {value}"))
    return refs, tuple(diagnostics)


def _slice_snapshot_diagnostics(feature_dir: Path) -> tuple[Diagnostic, ...]:
    workflow, required, shape_diagnostics = _state_shape(feature_dir)
    if shape_diagnostics:
        return shape_diagnostics
    assert workflow is not None
    specs_root = feature_dir.parent
    diagnostics: list[Diagnostic] = []
    artifacts: dict[str, bytes] = {}
    for name in required:
        data, artifact_diagnostics = _read_canonical_artifact(feature_dir, name, specs_root)
        diagnostics.extend(artifact_diagnostics)
        if data is None:
            continue
        artifacts[name] = data
        status, status_diagnostics = _state_status(feature_dir, name, specs_root)
        diagnostics.extend(status_diagnostics)
        if status is not None:
            diagnostics.extend(validate_status_reference(status, feature_dir, specs_root))
    tasks_path = feature_dir / "tasks.md"
    if tasks_data := artifacts.get("tasks.md"):
        tasks, task_diagnostics = parse_task_blocks(tasks_data, tasks_path)
        diagnostics.extend(task_diagnostics)
        diagnostics.extend(validate_task_graph(tasks))
    if workflow == "bugfix" and (bugfix_data := artifacts.get("bugfix.md")):
        _, criterion_diagnostics = parse_bugfix_criteria(bugfix_data, feature_dir / "bugfix.md")
        diagnostics.extend(criterion_diagnostics)
    elif workflow != "bugfix" and (requirements_data := artifacts.get("requirements.md")):
        criteria, criterion_diagnostics = parse_requirement_criteria(requirements_data, feature_dir / "requirements.md")
        diagnostics.extend(criterion_diagnostics)
        if design_data := artifacts.get("design.md"):
            _, _, trace_diagnostics = parse_traceability_table(design_data, feature_dir / "design.md", {criterion.ref for criterion in criteria})
            diagnostics.extend(diagnostic for diagnostic in trace_diagnostics if diagnostic.code not in {"TRACE_REF_UNKNOWN", "TRACE_SECTION_UNKNOWN"})
    return tuple(diagnostics)


def build_spec_slice(
    feature_dir: Path, task_id: str, canonical_specs_root: Path | None = None
) -> tuple[str, tuple[Diagnostic, ...]]:
    """สร้าง slice ตาม source order; mapping ที่ขาดเป็น successful fallback signal."""
    if canonical_specs_root is not None:
        resolved, resolver_diagnostics = resolve_feature_directory(canonical_specs_root, feature_dir.name)
        if resolver_diagnostics or resolved != feature_dir:
            return "", resolver_diagnostics or (_diag("SLICE_FEATURE_UNKNOWN", feature_dir, 1, "feature ไม่ใช่ canonical direct child"),)
    if not feature_dir.is_dir():
        return "", (_diag("SLICE_FEATURE_UNKNOWN", feature_dir, 1, f"ไม่พบ feature '{feature_dir.name}'"),)
    specs_root = canonical_specs_root or feature_dir.parent
    requirements_path = feature_dir / "requirements.md"
    bugfix_path = feature_dir / "bugfix.md"
    tasks_path = feature_dir / "tasks.md"
    if not tasks_path.is_file() and not tasks_path.is_symlink():
        return "", (_diag("SLICE_TASK_UNKNOWN", tasks_path, 1, f"ไม่พบ task '{task_id}'; available IDs: none"),)
    task_bytes, task_read_diagnostics = _read_canonical_artifact(feature_dir, "tasks.md", specs_root)
    if task_bytes is None:
        return "", task_read_diagnostics
    tasks, task_diagnostics = parse_task_blocks(task_bytes, tasks_path)
    task = next((candidate for candidate in tasks if candidate.task_id == task_id), None)
    if task is None:
        available = " ".join(candidate.task_id for candidate in tasks) or "none"
        return "", task_diagnostics + (_diag("SLICE_TASK_UNKNOWN", tasks_path, 1, f"ไม่พบ task '{task_id}'; available IDs: {available}"),)
    snapshot_diagnostics = _slice_snapshot_diagnostics(feature_dir)
    if snapshot_diagnostics:
        return "", snapshot_diagnostics

    artifact_names = ("bugfix.md", "tasks.md") if bugfix_path.is_file() and not requirements_path.is_file() else ("requirements.md", "design.md", "tasks.md")
    artifact_data: dict[str, bytes] = {"tasks.md": task_bytes}
    for name in artifact_names:
        if name == "tasks.md":
            continue
        data, read_diagnostics = _read_canonical_artifact(feature_dir, name, specs_root)
        if data is None:
            return "", read_diagnostics
        artifact_data[name] = data
    output: list[str] = []
    diagnostics: list[Diagnostic] = list(task_diagnostics)
    for name in artifact_names:
        path = feature_dir / name
        status, status_diagnostics = parse_status(artifact_data[name], path)
        diagnostics.extend(status_diagnostics)
        output.append(f"{name}: {status.kind if status is not None else 'invalid'}\n")
    output.append("\n")
    output.append(_verbatim_task_block(task_bytes, task))
    output.append("\n")

    if "bugfix.md" in artifact_data:
        bugfix_data = artifact_data["bugfix.md"]
        criteria, criterion_diagnostics = parse_bugfix_criteria(bugfix_data, bugfix_path)
        diagnostics.extend(criterion_diagnostics)
        refs, ref_diagnostics = _slice_refs(task, {criterion.ref for criterion in criteria})
        diagnostics.extend(ref_diagnostics)
        blocks, block_diagnostics = _bugfix_criterion_lines(bugfix_data, bugfix_path, refs)
        diagnostics.extend(block_diagnostics)
        output.extend(blocks)
    elif "requirements.md" in artifact_data:
        requirements_data = artifact_data["requirements.md"]
        design_path = feature_dir / "design.md"
        design_data = artifact_data["design.md"]
        criteria, criterion_diagnostics = parse_requirement_criteria(requirements_data, requirements_path)
        diagnostics.extend(criterion_diagnostics)
        known = {criterion.ref for criterion in criteria}
        refs, ref_diagnostics = _slice_refs(task, known)
        diagnostics.extend(ref_diagnostics)
        blocks, block_diagnostics = _feature_requirement_blocks(requirements_data, requirements_path, refs)
        diagnostics.extend(block_diagnostics)
        output.extend(blocks)
        rows, _, trace_diagnostics = parse_traceability_table(design_data, design_path, known)
        diagnostics.extend(trace_diagnostics)
        section_names: list[str] = []
        mapped_refs: set[str] = set()
        for row in rows:
            linked = refs & set(row.refs)
            if linked:
                mapped_refs.update(linked)
                if row.section not in section_names:
                    section_names.append(row.section)
        for ref in sorted(refs - mapped_refs):
            diagnostics.append(_slice_mapping_diagnostic(design_path, 1, f"ไม่พบ trace row สำหรับ {ref}"))
        blocks, block_diagnostics = _design_section_blocks(design_data, design_path, section_names)
        diagnostics.extend(block_diagnostics)
        if blocks and output and not output[-1].endswith("\n\n"):
            output.append("\n")
        output.extend(blocks)

    mapping_diagnostics = sorted(
        (diagnostic for diagnostic in diagnostics if diagnostic.code in {"SLICE_MAPPING_MISSING", "TRACE_SECTION_UNKNOWN", "TRACE_REF_UNKNOWN"}),
        key=lambda diagnostic: (diagnostic.location.path, diagnostic.location.line, diagnostic.code, diagnostic.message),
    )
    if mapping_diagnostics:
        output.append("\n")
        for diagnostic in mapping_diagnostics:
            output.append(f"MISSING: {diagnostic.code}: {diagnostic.message}\n")
    return "".join(output), tuple(diagnostics)


def _state_status(
    feature_dir: Path, name: str, canonical_specs_root: Path | None = None
) -> tuple[ArtifactStatus | None, tuple[Diagnostic, ...]]:
    data, diagnostics = _read_canonical_artifact(feature_dir, name, canonical_specs_root)
    path = feature_dir / name
    if data is None:
        return None, diagnostics
    status, status_diagnostics = parse_status(data, path)
    diagnostics += status_diagnostics
    if status is not None and status.kind == "unknown":
        diagnostics += (_diag("STATUS_UNKNOWN", path, status.location.line, "status เป็น unknown"),)
    return status, diagnostics


def _contains_unfinished_marker(value: str) -> bool:
    return bool(re.search(r"(?i)(?:\bTODO\b|\bTBD\b|\bpending\b|\?\?\?)", value))


def validate_evidence(tasks: Sequence[TaskBlock], data: bytes, path: Path) -> tuple[Diagnostic, ...]:
    """Validate every completed task through the shared Evidence v2 seam."""
    lines, diagnostics = _lines(data, path)
    if diagnostics:
        return diagnostics
    visible, fence_diagnostics = _outside_fence(lines, path)
    visible_numbers = {number for number, _ in visible}
    problems: list[Diagnostic] = list(fence_diagnostics)
    for task in tasks:
        if not task.completed:
            continue
        block_start = task.span[0] - 1
        block = lines[block_start:task.span[1]]
        try:
            evidence_start = next(
                index for index, line in enumerate(block)
                if block_start + index + 1 in visible_numbers and line.strip() == "Evidence:"
            )
        except StopIteration:
            problems.append(_diag("EVIDENCE_MISSING", path, task.location.line, "completed task ไม่มี Evidence"))
            continue
        evidence = block[evidence_start + 1:]
        if any(_contains_unfinished_marker(line) for line in evidence):
            problems.append(_diag("EVIDENCE_UNFINISHED_MARKER", path, task.location.line, "Evidence มี marker ที่ยังไม่เสร็จ"))
        test_indexes = [
            index for index, line in enumerate(evidence)
            if block_start + evidence_start + index + 2 in visible_numbers and line.strip().startswith("- test:")
        ]
        if not test_indexes:
            problems.append(_diag("EVIDENCE_MISSING", path, task.location.line, "Evidence ไม่มี test observation"))
        for position, index in enumerate(test_indexes):
            stop = test_indexes[position + 1] if position + 1 < len(test_indexes) else len(evidence)
            observation = evidence[index:stop]
            payload = observation[0].strip()[len("- test:"):].strip()
            joined = "\n".join(observation)
            has_command = "`" in payload or any(line.strip() and not line.strip().startswith(("- ", "->", "```")) for line in observation[1:])
            has_result = "->" in joined and bool(joined.split("->", 1)[1].strip())
            if not has_command or not has_result:
                problems.append(_diag("EVIDENCE_MISSING", path, task.location.line, "Evidence มี test observation ที่ไม่ valid"))
        viewports = next((
            line.strip() for index, line in enumerate(evidence)
            if block_start + evidence_start + index + 2 in visible_numbers and line.strip().startswith("- viewports:")
        ), "")
        if not re.fullmatch(r"- viewports: (?:n/a — .+|.*375.*768.*1440.*)", viewports):
            problems.append(_diag("EVIDENCE_MISSING", path, task.location.line, "Evidence ไม่มี viewports ที่ valid"))
        deviations = next((
            line.strip() for index, line in enumerate(evidence)
            if block_start + evidence_start + index + 2 in visible_numbers and line.strip().startswith("- deviations:")
        ), "")
        if deviations != "- deviations: none" and not re.fullmatch(r"- deviations: .+", deviations):
            problems.append(_diag("EVIDENCE_MISSING", path, task.location.line, "Evidence ไม่มี deviations ที่ valid"))
    return tuple(problems)


def _is_canonical_archive_location(feature_dir: Path, specs_root: Path) -> bool:
    try:
        relative = feature_dir.relative_to(specs_root)
    except ValueError:
        return False
    if len(relative.parts) < 2 or relative.parts[0] != "archive":
        return False
    current = specs_root
    for part in relative.parts:
        current = current / part
        if current.is_symlink():
            return False
    return feature_dir.resolve().is_relative_to((specs_root / "archive").resolve())


def _state_shape(feature_dir: Path) -> tuple[str | None, tuple[str, ...], tuple[Diagnostic, ...]]:
    entries = {path.name: path for path in feature_dir.iterdir()}
    known = {"requirements.md", "design.md", "tasks.md", "bugfix.md"}
    blocked = tuple(
        _diag("STATE_ARTIFACT_BLOCKED", entries[name], 1, "artifact ต้องเป็น regular file")
        for name in sorted(known & entries.keys())
        if not entries[name].is_file()
    )
    if blocked:
        return None, (), blocked
    files = {name for name, path in entries.items() if path.is_file()}
    if not files:
        return None, (), (_diag("STATE_EMPTY_DIRECTORY", feature_dir, 1, "spec directory ว่าง"),)
    if "requirements.md" in files and "bugfix.md" in files:
        return None, (), (_diag("STATE_AMBIGUOUS_SHAPE", feature_dir, 1, "มี requirements.md และ bugfix.md พร้อมกัน"),)
    if "bugfix.md" in files:
        if "requirements.md" in files or "design.md" in files:
            return None, (), (_diag("STATE_AMBIGUOUS_SHAPE", feature_dir, 1, "bugfix shape มี feature artifact ปะปน"),)
        return "bugfix", ("bugfix.md", "tasks.md"), ()
    if "requirements.md" in files:
        return "feature", ("requirements.md", "design.md", "tasks.md"), ()
    if "design.md" in files and "tasks.md" not in files:
        return "design-first", ("design.md", "requirements.md", "tasks.md"), ()
    if files & known:
        return None, (), (_diag("STATE_AMBIGUOUS_SHAPE", feature_dir, 1, "artifact chain ขาด authoritative root หรือมี downstream ข้าม phase"),)
    return None, (), (_diag("STATE_AMBIGUOUS_SHAPE", feature_dir, 1, "spec directory ไม่มี artifact ที่รู้จัก"),)


def validate_status_reference(status: ArtifactStatus, feature_dir: Path, specs_root: Path) -> tuple[Diagnostic, ...]:
    if status.kind != "superseded":
        return ()
    assert status.superseded_by is not None
    target, diagnostics = resolve_feature_directory(specs_root, status.superseded_by)
    if diagnostics or target is None or status.superseded_by == feature_dir.name:
        return (_diag("STATUS_TARGET_MISSING", Path(status.location.path), status.location.line, "superseded target ไม่อยู่ใต้ canonical specs root"),)
    return ()


def derive_spec_state(feature_dir: Path, canonical_specs_root: Path) -> tuple[str, tuple[Diagnostic, ...]]:
    """คืน state เดียวจาก bytes และ canonical location โดยใช้ fail-closed precedence."""
    if not feature_dir.is_dir():
        return "blocked", (_diag("STATE_EMPTY_DIRECTORY", feature_dir, 1, "ไม่พบ spec directory", "engine-fail"),)
    location_diagnostics = _canonical_location_diagnostics(feature_dir, canonical_specs_root)
    if location_diagnostics:
        return "blocked", location_diagnostics
    if _is_canonical_archive_location(feature_dir, canonical_specs_root):
        return "archived", ()
    for name in ("requirements.md", "design.md", "tasks.md", "bugfix.md"):
        if (feature_dir / name).is_symlink():
            return "blocked", (_diag("STATE_ARTIFACT_BLOCKED", feature_dir / name, 1, "artifact file ต้องไม่เป็น symlink"),)
    workflow, required, diagnostics = _state_shape(feature_dir)
    if diagnostics:
        return "blocked", diagnostics
    assert workflow is not None
    existing = tuple(name for name in required if (feature_dir / name).is_file())
    statuses: dict[str, ArtifactStatus] = {}
    problems: list[Diagnostic] = []
    for name in existing:
        status, status_diagnostics = _state_status(feature_dir, name, canonical_specs_root)
        problems.extend(status_diagnostics)
        if status is not None:
            statuses[name] = status
            problems.extend(validate_status_reference(status, feature_dir, canonical_specs_root))
    if "tasks.md" in existing:
        task_path = feature_dir / "tasks.md"
        task_data, task_read_diagnostics = _read_canonical_artifact(feature_dir, "tasks.md", canonical_specs_root)
        problems.extend(task_read_diagnostics)
        if task_data is None:
            tasks = ()
        else:
            tasks, task_diagnostics = parse_task_blocks(task_data, task_path)
            problems.extend(task_diagnostics)
            problems.extend(validate_task_graph(tasks))
            problems.extend(validate_evidence(tasks, task_data, task_path))
    else:
        tasks = ()
    if problems:
        return "blocked", tuple(problems)

    root_name = "bugfix.md" if workflow == "bugfix" else ("requirements.md" if "requirements.md" in existing else "design.md")
    root_status = statuses.get(root_name)
    if root_status is None:
        return "blocked", (_diag("STATE_ARTIFACT_BLOCKED", feature_dir / root_name, 1, "authoritative artifact ไม่มี status"),)
    if root_status.kind == "superseded":
        target = root_status.superseded_by
        if all(status.kind == "superseded" and status.superseded_by == target for status in statuses.values()) and not any(task.completed is False for task in tasks):
            return "superseded", ()
        return "blocked", (_diag("STATE_ARTIFACT_BLOCKED", feature_dir, 1, "superseded chain ขัดกันหรือยังมี pending task"),)
    if any(status.kind == "superseded" for status in statuses.values()):
        return "blocked", (_diag("STATE_ARTIFACT_BLOCKED", feature_dir, 1, "status superseded ขัดกับ authoring chain"),)

    missing = [name for name in required if name not in existing]
    if missing:
        phase_order = {name: index for index, name in enumerate(required)}
        highest_existing = max((phase_order[name] for name in existing), default=0)
        earliest_missing = min((phase_order[name] for name in missing), default=0)
        if highest_existing > earliest_missing:
            return "blocked", (_diag("STATE_ARTIFACT_BLOCKED", feature_dir, 1, "artifact chain ขาดในตำแหน่งที่ downstream อ้างว่าควรครบ"),)
        return "active", ()

    if workflow == "bugfix":
        trace_diagnostics = _bugfix_trace(feature_dir, canonical_specs_root)
    else:
        trace_diagnostics = _feature_trace(feature_dir, canonical_specs_root)
    if trace_diagnostics:
        return "blocked", trace_diagnostics
    if all(status.kind == "approved" for status in statuses.values()) and all(task.completed for task in tasks):
        return "complete", ()
    return "active", ()


def _state_directories(specs_root: Path) -> tuple[Path, ...]:
    directories = [
        path for path in specs_root.iterdir()
        if path.is_dir() and not path.is_symlink() and path.name != "archive"
    ]
    archive = specs_root / "archive"
    if archive.is_dir() and not archive.is_symlink():
        directories.extend(path for path in archive.iterdir() if path.is_dir() and not path.is_symlink())
    return tuple(sorted(directories, key=lambda path: path.name))


def active_summary(specs_root: Path) -> tuple[str, tuple[Diagnostic, ...]]:
    active: list[str] = []
    blocked = 0
    diagnostics: list[Diagnostic] = []
    for directory in _state_directories(specs_root):
        state, state_diagnostics = derive_spec_state(directory, specs_root)
        diagnostics.extend(state_diagnostics)
        if state == "active":
            active.append(directory.name)
        elif state == "blocked":
            blocked += 1
    names = " ".join(sorted(active)) if active else "none"
    return f"Active specs: {names}. Blocked specs: {blocked}.\n", tuple(diagnostics)


def _print_diagnostics(diagnostics: Sequence[Diagnostic]) -> None:
    for diagnostic in sorted(diagnostics, key=lambda value: (value.location.path, value.location.line, value.code, value.message)):
        print(f"{diagnostic.code}: {diagnostic.message}", file=sys.stderr)


def _cli(argv: Sequence[str]) -> int:
    specs_dir = Path(__file__).resolve().parent.parent / ".ai" / "specs"
    if len(argv) >= 2 and argv[0] == "gate" and argv[1] == "phase":
        parser = argparse.ArgumentParser(add_help=False)
        parser.add_argument("--feature", required=True)
        parser.add_argument("--phase", required=True)
        parser.add_argument("--workflow", required=True)
        args = parser.parse_args(argv[2:])
        feature_dir, resolver_diagnostics = resolve_feature_directory(specs_dir, args.feature)
        if resolver_diagnostics or feature_dir is None:
            _print_diagnostics(resolver_diagnostics)
            return 1
        _, diagnostics = check_phase_gate(feature_dir, args.phase, args.workflow, specs_dir)
        if diagnostics:
            return _print_contract_diagnostics(args.feature, diagnostics)
        print(f"OK: '{args.feature}' phase '{args.phase}' ผ่าน workflow '{args.workflow}'")
        return 0
    if argv and argv[0] == "slice":
        parser = argparse.ArgumentParser(add_help=False)
        parser.add_argument("--feature", required=True)
        parser.add_argument("--task", required=True)
        args = parser.parse_args(argv[1:])
        feature_dir, resolver_diagnostics = resolve_feature_directory(specs_dir, args.feature)
        if resolver_diagnostics or feature_dir is None:
            _print_diagnostics(resolver_diagnostics)
            return 1
        text, diagnostics = build_spec_slice(feature_dir, args.task, specs_dir)
        if text:
            print(text, end="")
        if diagnostics and any(diagnostic.code != "SLICE_MAPPING_MISSING" and diagnostic.code not in {"TRACE_SECTION_UNKNOWN", "TRACE_REF_UNKNOWN"} for diagnostic in diagnostics):
            _print_diagnostics(diagnostics)
            return 1
        return 0
    if argv and argv[0] == "state":
        parser = argparse.ArgumentParser(add_help=False)
        parser.add_argument("--all", action="store_true")
        parser.add_argument("--feature")
        parser.add_argument("--format", choices=("summary", "text"), default="text")
        args = parser.parse_args(argv[1:])
        if args.all:
            summary, _ = active_summary(specs_dir)
            print(summary, end="")
            return 0
        if args.feature:
            feature_dir, resolver_diagnostics = resolve_feature_directory(specs_dir, args.feature)
            if resolver_diagnostics or feature_dir is None:
                _print_diagnostics(resolver_diagnostics)
                return 1
            state, diagnostics = derive_spec_state(feature_dir, specs_dir)
            print(state)
            if diagnostics:
                _print_diagnostics(diagnostics)
                return 1
            return 0
        print("ใช้: spec_contract.py state --all --format summary หรือ --feature FEATURE", file=sys.stderr)
        return 2
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
