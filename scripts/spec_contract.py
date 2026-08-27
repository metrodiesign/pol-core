#!/usr/bin/env python3
"""Canonical read-only contract สำหรับ SDD Markdown artifacts."""
from __future__ import annotations

import argparse
import difflib
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
    evidence: tuple[str, ...] = ()


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


HTML_COMMENT_OPEN_RE = re.compile(r"^ {0,3}<!--")
HTML_BLOCK_RE = re.compile(r"^ {0,3}</?[A-Za-z][A-Za-z0-9-]*(?:[ \t/>]|$)")


def _outside_fence(lines: Iterable[str], path: Path) -> tuple[list[tuple[int, str]], tuple[Diagnostic, ...]]:
    visible: list[tuple[int, str]] = []
    marker: str | None = None
    opening_length = 0
    opening_line = 0
    comment_line = 0
    in_comment = False
    for number, line in enumerate(lines, 1):
        if in_comment:
            if "-->" in line:
                in_comment = False
            continue
        subject = _fence_subject(line)
        if marker is None:
            if HTML_COMMENT_OPEN_RE.match(line):
                if "-->" not in line.split("<!--", 1)[1]:
                    in_comment = True
                    comment_line = number
                continue
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
    if in_comment:
        return visible, (_diag("TRACE_FENCE_UNCLOSED", path, comment_line, "HTML comment ไม่ปิด"),)
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


def _evidence_lines(lines: Sequence[str], start: int, end: int, visible_numbers: set[int]) -> tuple[str, ...]:
    """Collect all visible continuation lines after the Evidence: header of one task."""
    collected: list[str] = []
    open_ = False
    for offset in range(start, end):
        if offset + 1 not in visible_numbers:
            continue
        stripped = lines[offset].strip()
        if open_:
            collected.append(stripped)
            continue
        if stripped == "Evidence:":
            open_ = True
    return tuple(collected)


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
            evidence=_evidence_lines(lines, start + 1, end, visible_numbers),
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
    # boundary grammar ต้องเท่ากับ start grammar ของ engine (REQ_HEADING_RE และ design starts เป็น column 0 ทั้งคู่)
    # บรรทัดที่เริ่ม section ไม่ได้ ต้องจบ section ไม่ได้ด้วย; indented heading จึงเป็น content เสมอ
    return bool(re.match(r"^#{1,2}(?:[ \t]|$)", line))


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


def _raw_html_diagnostics(
    visible: Sequence[tuple[int, str]], path: Path, start: int, end: int
) -> tuple[Diagnostic, ...]:
    """raw HTML block อยู่นอก grammar ที่ engine รองรับ จึงต้องดังแทนที่จะตัดเนื้อหาเงียบ ๆ"""
    return tuple(
        _slice_mapping_diagnostic(path, number, "block มี raw HTML ที่อยู่นอก grammar ให้ full-read artifact แทน")
        for number, line in visible
        if start <= number <= end and HTML_BLOCK_RE.match(line)
    )


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
    html_diagnostics: list[Diagnostic] = []
    for start, heading in headings:
        end = next((number - 1 for number in boundaries if number > start), len(raw_lines))
        heading_refs = refs_by_heading.get(heading, set())
        if refs & heading_refs:
            blocks.append("".join(raw_lines[start - 1:end]))
            found.update(refs & heading_refs)
            html_diagnostics.extend(_raw_html_diagnostics(visible, path, start, end))
    missing = tuple(
        _slice_mapping_diagnostic(path, 1, f"ไม่พบ linked requirement {ref}")
        for ref in sorted(refs - found)
    )
    return blocks, fence_diagnostics + criterion_diagnostics + tuple(html_diagnostics) + missing


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
    html_diagnostics: list[Diagnostic] = []
    for start, heading in starts:
        if heading not in wanted:
            continue
        end = next((number - 1 for number in boundaries if number > start), len(raw_lines))
        blocks.append("".join(raw_lines[start - 1:end]))
        found.add(heading)
        html_diagnostics.extend(_raw_html_diagnostics(visible, path, start, end))
    missing = tuple(
        _slice_mapping_diagnostic(path, 1, f"ไม่พบ mapped design section {heading}")
        for heading in sorted(wanted - found)
    )
    return blocks, fence_diagnostics + tuple(html_diagnostics) + missing


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


_UNFINISHED_MARKER_RE = re.compile(r"(?i)(?<![A-Za-z0-9_-])(?:TODO|TBD|pending|\?\?\?)(?![A-Za-z0-9_-])")


def _contains_unfinished_marker(value: str) -> bool:
    """Unfinished-work marker: bare word only; `--pending`-style flags are data."""
    return bool(_UNFINISHED_MARKER_RE.search(value))


_PLANNED_PHRASE_RE = re.compile(r"(?i)(?:คาดว่าจะ|จะรัน|will run|expected to run|going to run)")


def _task_evidence_problems(task: TaskBlock) -> list[Diagnostic]:
    problems: list[Diagnostic] = []
    line = task.location.line
    path = Path(task.location.path)
    if not task.evidence:
        return [_diag("EVIDENCE_MISSING", path, line, "completed task ไม่มี Evidence")]
    joined = "\n".join(task.evidence)
    # Markers are judged in RESULT/value segments only: a literal marker inside the
    # COMMAND part (flags, paths, test names like `--pending`) is data, not status.
    def _segments():
        for entry in task.evidence:
            if entry.startswith("- test:") and "->" in entry:
                yield entry.split("->", 1)[1]
            elif entry.startswith("- viewports:"):
                yield entry[len("- viewports:"):]
            elif entry.startswith("- deviations:"):
                yield entry[len("- deviations:"):]
            elif entry.startswith("- notes:"):
                yield entry[len("- notes:"):]

    if _marker_leads(_segments()):
        problems.append(_diag("EVIDENCE_UNFINISHED_MARKER", path, line, "Evidence มี marker ที่ยังไม่เสร็จ"))
    test_openings = [index for index, entry in enumerate(task.evidence) if entry.startswith("- test:")]
    if not test_openings:
        problems.append(_diag("EVIDENCE_COMMAND_MISSING", path, line, "Evidence ไม่มี test observation"))
    else:
        for position, index in enumerate(test_openings):
            stop = test_openings[position + 1] if position + 1 < len(test_openings) else len(task.evidence)
            observation = task.evidence[index:stop]
            payload = observation[0][len("- test:"):].strip()
            tail_lines = [tail for tail in observation[1:] if tail and not tail.startswith(("- ",))]
            has_command = "`" in payload or "`" in "\n".join(tail_lines) or bool(tail_lines)
            arrow_tail = ""
            for probe in (payload, *tail_lines):
                if "->" in probe:
                    arrow_tail = probe.split("->", 1)[1].strip()
                    break
            if not has_command:
                problems.append(_diag("EVIDENCE_COMMAND_MISSING", path, line, "test observation ไม่มี command"))
            if not arrow_tail:
                problems.append(_diag("EVIDENCE_RESULT_MISSING", path, line, "test observation ไม่มี observed result"))
        if not any("->" in entry for entry in task.evidence) and _PLANNED_PHRASE_RE.search(joined):
            problems.append(_diag("EVIDENCE_PLANNED_ONLY", path, line, "Evidence ระบุเพียงแผนว่าจะรัน"))
    viewports = next((entry for entry in task.evidence if entry.startswith("- viewports:")), "")
    if not re.fullmatch(r"- viewports: (?:n/a — .+|(?:.*375.*768.*1440.*|.*1440.*768.*375.*))", viewports):
        problems.append(_diag("EVIDENCE_VIEWPORTS_INVALID", path, line, "Evidence ไม่มี viewports ที่ valid"))
    deviations = next((entry for entry in task.evidence if entry.startswith("- deviations:")), "")
    if deviations != "- deviations: none" and not re.fullmatch(r"- deviations: (?!none$).+", deviations):
        problems.append(_diag("EVIDENCE_DEVIATIONS_MISSING", path, line, "Evidence ไม่มี deviations ที่ valid"))
    return problems


def _marker_leads(segments) -> bool:
    """Unfinished verdict iff the FIRST token of an observed-result/value is the
    marker itself (e.g. `-> TODO`, `deviations: TBD`). Words appearing later in
    prose/test-names (`x5: pending/all/json`) are data, never status."""
    strip_spans_re = re.compile(r"`[^`]*`|\([^)]*\)|\[[^\]]*\]")
    for segment in segments:
        scrubbed = strip_spans_re.sub(" ", segment)
        if _contains_unfinished_marker(scrubbed):
            return True
    return False


def validate_evidence(tasks: Sequence[TaskBlock], selected_ids: Iterable[str]) -> tuple[Diagnostic, ...]:
    """Validate Evidence v2 only inside the blocks of the selected completed tasks."""
    selected = set(selected_ids)
    problems: list[Diagnostic] = []
    for task in tasks:
        if not task.completed or task.task_id not in selected:
            continue
        problems.extend(_task_evidence_problems(task))
    return tuple(problems)


GATE_SOURCES = frozenset({"claude-edit", "codex-edit", "opencode", "pre-commit", "ci"})


@dataclass(frozen=True, slots=True)
class ChangedByteRange:
    before_start: int
    before_end: int
    after_start: int
    after_end: int


@dataclass(frozen=True, slots=True)
class GateSelection:
    path: str
    before_exists: bool
    before_bytes: bytes
    after_bytes: bytes
    changed_ranges: tuple[ChangedByteRange, ...]
    source: str


def canonical_changed_ranges(before: bytes, after: bytes) -> tuple[ChangedByteRange, ...]:
    """Canonical non-equal opcode list of one snapshot pair as sorted byte ranges."""
    before_lines = before.decode("utf-8", "surrogateescape").splitlines(keepends=True)
    after_lines = after.decode("utf-8", "surrogateescape").splitlines(keepends=True)
    before_offsets: list[int] = [0]
    for line in before_lines:
        before_offsets.append(before_offsets[-1] + len(line.encode("utf-8", "surrogateescape")))
    after_offsets: list[int] = [0]
    for line in after_lines:
        after_offsets.append(after_offsets[-1] + len(line.encode("utf-8", "surrogateescape")))
    matcher = difflib.SequenceMatcher(None, before_lines, after_lines, autojunk=False)
    ranges: list[ChangedByteRange] = []
    for tag, i0, i1, j0, j1 in matcher.get_opcodes():
        if tag == "equal":
            continue
        ranges.append(ChangedByteRange(
            before_offsets[i0],
            before_offsets[i1],
            after_offsets[j0],
            after_offsets[j1],
        ))
    return tuple(ranges)


def _engine_diag(code: str, path: str, message: str) -> Diagnostic:
    return Diagnostic(code=code, verdict="engine-fail", location=SourceLocation(path, 1), message=message)


def _selection_transport_diagnostics(selection: GateSelection) -> list[Diagnostic]:
    problems: list[Diagnostic] = []
    if selection.source not in GATE_SOURCES:
        return [_engine_diag("GATE_SELECTION_SOURCE_INVALID", selection.path, "source enum ไม่รู้จัก")]
    if selection.before_exists and not selection.before_bytes and not selection.after_bytes:
        pass
    if not selection.before_exists and selection.before_bytes:
        problems.append(_engine_diag(
            "GATE_SNAPSHOT_MISSING",
            selection.path,
            "before_exists=false แต่มี before bytes ซึ่งขัดกับ existence state",
        ))
    if not selection.after_bytes:
        problems.append(_engine_diag("GATE_SNAPSHOT_MISSING", selection.path, "after snapshot ว่าง"))
    computed = canonical_changed_ranges(selection.before_bytes, selection.after_bytes)
    if selection.changed_ranges != computed:
        problems.append(_engine_diag(
            "GATE_RANGE_INVALID",
            selection.path,
            "changed ranges ไม่เท่ากับ canonical diff opcodes ของ snapshot pair เดียวกัน",
        ))
    else:
        for low in range(len(computed)):
            current = computed[low]
            bounds_valid = (
                0 <= current.before_start <= current.before_end <= len(selection.before_bytes)
                and 0 <= current.after_start <= current.after_end <= len(selection.after_bytes)
                and (current.before_start < current.before_end or current.after_start < current.after_end)
            )
            if not bounds_valid:
                problems.append(_engine_diag("GATE_RANGE_INVALID", selection.path, "changed range อยู่นอก bounds"))
                break
            if low + 1 < len(computed) and (
                current.after_end > computed[low + 1].after_start or current.before_end > computed[low + 1].before_start
            ):
                problems.append(_engine_diag("GATE_RANGE_INVALID", selection.path, "changed ranges ซ้อนทับกัน"))
                break
    return problems


def _line_boundaries(data: bytes) -> list[int]:
    offsets: list[int] = [0]
    for line in data.decode("utf-8", "surrogateescape").splitlines(keepends=True):
        offsets.append(offsets[-1] + len(line.encode("utf-8", "surrogateescape")))
    return offsets


def _spans_overlap_offsets(span: tuple[int, int], boundaries: list[int], ranges: Sequence[ChangedByteRange]) -> bool:
    start_offset = boundaries[span[0] - 1]
    end_offset = boundaries[min(span[1], len(boundaries) - 1)]
    return any(current.after_start < end_offset and start_offset < current.after_end for current in ranges)


def discover_completed_tasks(selection: GateSelection) -> tuple[tuple[str, ...], tuple[Diagnostic, ...]]:
    """Select newly-completed tasks from raw snapshots; callers never send task IDs."""
    transport_problems = _selection_transport_diagnostics(selection)
    _, before_problems = parse_task_blocks(selection.before_bytes, Path(selection.path)) if selection.before_exists else ((), ())
    after_tasks, after_problems = parse_task_blocks(selection.after_bytes, Path(selection.path))
    if transport_problems or after_problems or before_problems:
        diagnostics = list(transport_problems)
        code_by_prefix = {"TASK_"}
        seen: set[tuple[str, int, str]] = set()
        for diagnostic in (*after_problems, *before_problems):
            if diagnostic.code.startswith(tuple(code_by_prefix)) and diagnostic.verdict == "policy-fail":
                key = (diagnostic.location.path, diagnostic.location.line, diagnostic.code)
                if key not in seen:
                    seen.add(key)
                    diagnostics.append(diagnostic)
        return (), tuple(diagnostics)
    before_tasks, _ = parse_task_blocks(selection.before_bytes, Path(selection.path))
    before_state = {task.task_id: task.completed for task in before_tasks}
    after_line_boundaries = _line_boundaries(selection.after_bytes)
    selected: list[str] = []
    for task in after_tasks:
        if not task.completed:
            continue
        existed = task.task_id in before_state
        transitioned = existed and not before_state[task.task_id]
        after_only_opening_overlaps = (
            not existed
            and _spans_overlap_offsets(task.span, after_line_boundaries, selection.changed_ranges)
        )
        if transitioned or after_only_opening_overlaps:
            selected.append(task.task_id)
    return tuple(selected), ()


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
            problems.extend(validate_evidence(tasks, (task.task_id for task in tasks if task.completed)))
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


def _print_gate_evidence_envelope(
    path: str,
    selected_ids: Sequence[str],
    problems: list[Diagnostic],
) -> int:
    """Print the canonical gate evidence verdict envelope; returns the CLI exit code."""
    del path
    diagnostics = sorted(problems, key=lambda item: (item.location.path, item.location.line, item.location.column, item.code, item.message))
    if any(diagnostic.verdict == "engine-fail" for diagnostic in diagnostics):
        verdict = "engine-fail"
        exit_code = 2
    elif diagnostics:
        verdict = "policy-fail"
        exit_code = 1
    else:
        verdict = "allow"
        exit_code = 0
    payload = {
        "schemaVersion": 1,
        "verdict": verdict,
        "diagnostics": [
            {
                "code": diagnostic.code,
                "message": diagnostic.message,
                "path": diagnostic.location.path,
                "line": diagnostic.location.line,
                "column": diagnostic.location.column,
                "verdict": diagnostic.verdict,
                "details": diagnostic.details,
            }
            for diagnostic in diagnostics
        ],
        "completedTaskIds": sorted(selected_ids),
    }
    print(json.dumps(payload, sort_keys=True))
    return exit_code


def _cli(argv: Sequence[str]) -> int:
    specs_dir = Path(__file__).resolve().parent.parent / ".ai" / "specs"
    if len(argv) >= 1 and argv[0] == "task-ids":
        parser = argparse.ArgumentParser(add_help=False)
        parser.add_argument("--feature", required=True)
        parser.add_argument("--pending", action="store_true")
        parser.add_argument("--all", action="store_true")
        parser.add_argument("--selector")
        parser.add_argument("--format", choices=("lines", "json"), default="lines")
        parser.add_argument("--specs-root", default=None, help="test seam: alternate .ai/specs root")
        args = parser.parse_args(argv[1:])
        root_dir = Path(args.specs_root) if args.specs_root else specs_dir
        feature_dir, resolver_diagnostics = resolve_feature_directory(root_dir, args.feature)
        if resolver_diagnostics or feature_dir is None:
            _print_diagnostics(resolver_diagnostics)
            return 1
        tasks_data = _read_canonical_artifact(feature_dir, "tasks.md", root_dir)[0]
        if tasks_data is None:
            print("TASK_ARTIFACT_MISSING: no tasks.md", file=sys.stderr)
            return 1
        tasks, parse_problems = parse_task_blocks(tasks_data, feature_dir / "tasks.md")
        if parse_problems or validate_task_graph(tasks):
            _print_diagnostics(tuple(parse_problems))
            return 1
        if args.selector:
            resolved, selector_problems = resolve_task_selector(tasks, args.selector)
            if selector_problems:
                _print_diagnostics(selector_problems)
                return 1
            selected = set(resolved)
        elif args.pending:
            selected = {task.task_id for task in tasks if not task.completed}
        else:
            selected = {task.task_id for task in tasks}
        ordered = [task.task_id for task in tasks if task.task_id in selected]
        if args.format == "json":
            print(json.dumps({"schemaVersion": 1, "taskIds": ordered}, sort_keys=True))
        else:
            for task_id in ordered:
                print(task_id)
        return 0
    if len(argv) >= 1 and argv[0] == "diff-ranges":
        parser = argparse.ArgumentParser(add_help=False)
        parser.add_argument("--before-file", required=True)
        parser.add_argument("--after-file", required=True)
        parser.add_argument("--format", choices=("json",), default="json")
        args = parser.parse_args(argv[1:])
        try:
            before_bytes = Path(args.before_file).read_bytes()
            after_bytes = Path(args.after_file).read_bytes()
        except OSError as error:
            print(f"ENGINE_INTERNAL: {error}", file=sys.stderr)
            return 2
        ranges = canonical_changed_ranges(before_bytes, after_bytes)
        payload = {
            "schemaVersion": 1,
            "ranges": [
                {
                    "beforeStart": current.before_start,
                    "beforeEnd": current.before_end,
                    "afterStart": current.after_start,
                    "afterEnd": current.after_end,
                }
                for current in ranges
            ],
        }
        print(json.dumps(payload, sort_keys=True))
        return 0
    if len(argv) >= 1 and argv[0] == "gate" and len(argv) >= 2 and argv[1] == "evidence":
        parser = argparse.ArgumentParser(add_help=False)
        parser.add_argument("--path", required=True)
        parser.add_argument("--after-file", required=True)
        parser.add_argument("--ranges-file", required=True)
        parser.add_argument("--before-file")
        parser.add_argument("--before-missing", action="store_true")
        parser.add_argument("--source", required=True, choices=sorted(GATE_SOURCES))
        args = parser.parse_args(argv[2:])
        problems: list[Diagnostic] = []
        if bool(args.before_file) == args.before_missing:
            problems.append(_engine_diag("GATE_SNAPSHOT_MISSING", args.path, "ต้องระบุ --before-file หรือ --before-missing อย่างใดอย่างหนึ่ง"))
            return _print_gate_evidence_envelope(args.path, [], problems)
        try:
            after_bytes = Path(args.after_file).read_bytes()
            ranges_payload = json.loads(Path(args.ranges_file).read_text(encoding="utf-8"))
            before_bytes = b"" if args.before_missing else Path(args.before_file or "").read_bytes()
        except (OSError, ValueError) as error:
            print(f"ENGINE_INTERNAL: {error}", file=sys.stderr)
            return 2
        raw_ranges = ranges_payload.get("ranges") if isinstance(ranges_payload, dict) else ranges_payload
        if not isinstance(raw_ranges, list):
            print("ENGINE_INTERNAL: ranges file invalid", file=sys.stderr)
            return 2
        parsed_ranges: list[ChangedByteRange] = []
        malformed_ranges = False
        for entry in raw_ranges:
            try:
                if isinstance(entry, dict):
                    values = (entry["beforeStart"], entry["beforeEnd"], entry["afterStart"], entry["afterEnd"])
                else:
                    values = tuple(entry)
                first, second, third, fourth = (int(value) for value in values)
            except (KeyError, TypeError, ValueError):
                malformed_ranges = True
                break
            parsed_ranges.append(ChangedByteRange(first, second, third, fourth))
        if malformed_ranges:
            print("ENGINE_INTERNAL: ranges file invalid", file=sys.stderr)
            return 2
        selection = GateSelection(
            path=args.path,
            before_exists=not args.before_missing,
            before_bytes=before_bytes,
            after_bytes=after_bytes,
            changed_ranges=tuple(parsed_ranges),
            source=args.source,
        )
        selected_ids, discovery_problems = discover_completed_tasks(selection)
        tasks, parse_problems = parse_task_blocks(after_bytes, Path(args.path))
        diagnostics = (*discovery_problems, *parse_problems)
        if any(diagnostic.verdict == "engine-fail" for diagnostic in diagnostics):
            return _print_gate_evidence_envelope(args.path, selected_ids, list(diagnostics))
        # only task-grammar REGRESSIONS introduced by this change block — a
        # pre-existing TASK_* diagnostic in untouched regions is legacy debt,
        # not something the staged diff is allowed to hide or worsen.
        import collections as _collections
        before_task_counts = _collections.Counter(
            diagnostic.code for diagnostic in parse_task_blocks(before_bytes, Path(args.path))[1]
            if diagnostic.code.startswith("TASK_"))
        after_task_counts = _collections.Counter(
            diagnostic.code for diagnostic in parse_problems
            if diagnostic.code.startswith("TASK_"))
        policy_problems = [
            diagnostic for diagnostic in parse_problems
            if diagnostic.code.startswith("TASK_")
            and after_task_counts[diagnostic.code] > before_task_counts.get(diagnostic.code, 0)
        ]
        if policy_problems:
            return _print_gate_evidence_envelope(args.path, selected_ids, list(policy_problems))
        problems.extend(validate_evidence(tasks, selected_ids))
        return _print_gate_evidence_envelope(args.path, selected_ids, list(problems))
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
    parser.add_argument("--all", action="store_true",
                        help="strict trace over the active spec corpus "
                             "(ledger-dispositioned legacy chains skipped)")
    parser.add_argument("--specs-root", default=None,
                        help="test seam: alternate .ai/specs root")
    args = parser.parse_args(argv)
    if args.command == "check" and (args.named_feature or args.feature):
        return trace_run(args.named_feature or args.feature, specs_dir)
    if args.command == "check" and args.all:
        root_dir = Path(args.specs_root) if args.specs_root else specs_dir
        return _check_all_strict(root_dir)
    if args.command and not args.feature and not args.named_feature:
        return trace_run(args.command, specs_dir)
    print("ใช้: spec_contract.py check --feature FEATURE --strict | check --all --strict", file=sys.stderr)
    return 2


LEDGER_DISPOSITION_SKIP = {"trace-header-canonical", "active-authoring-exempt",
                           "legacy-baseline-exempt"}
LEDGER_REL = Path("sdd-operating-layer-parity") / "migration-resolutions.json"


def ledger_legacy_features(specs_dir: Path) -> set[str]:
    """Feature dirs whose trace tables / authoring chains carry a committed
    human-checkpoint decision (option-K recorded residual). Strict all-spec
    scope is active-first; these re-enter via the verify scope when tasks say
    so. Reading the JSON directly keeps this module free of retrofit imports."""
    path = specs_dir / LEDGER_REL
    if not path.is_file():
        return set()
    import json as _json
    try:
        payload = _json.loads(path.read_text(encoding="utf-8"))
    except ValueError:
        return set()
    legacy: set[str] = set()
    for entry in payload.get("decisions", []):
        if entry.get("field") in ("trace.table", "authoring.chain") and \
                entry.get("disposition") in LEDGER_DISPOSITION_SKIP:
            parent = Path(entry.get("path", "")).parent
            if parent.parent.name == ".ai" or len(parent.parts) >= 2:
                name = parent.name if parent.name != ".ai" else ""
                if name:
                    legacy.add(name)
    return legacy


def _check_all_strict(specs_dir: Path) -> int:
    """Strict trace across the ACTIVE corpus (option-K scoping)."""
    features = sorted(path.name for path in specs_dir.iterdir()
                      if path.is_dir() and (path / "requirements.md").is_file())
    features += sorted(path.name for path in specs_dir.iterdir()
                       if path.is_dir() and (path / "bugfix.md").is_file()
                       and not (path / "requirements.md").is_file())
    skipped = ledger_legacy_features(specs_dir)
    active = [f for f in features if f not in skipped]
    failures = 0
    for feature in active:
        print(f"::group::strict-trace {feature}")
        if trace_run(feature, specs_dir) != 0:
            failures += 1
        print("::endgroup::")
    print(f"check --all --strict: {len(active)} active / "
          f"{len(skipped)} legacy-residual dirs / {failures} failing")
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    try:
        raise SystemExit(_cli(sys.argv[1:]))
    except (OSError, ValueError) as error:
        print(f"ENGINE_INTERNAL: {error}", file=sys.stderr)
        raise SystemExit(2)
