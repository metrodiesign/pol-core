#!/usr/bin/env python3
"""Compatibility trace for the historical SDD corpus.

This entry point deliberately preserves the pre-cutover `scripts/spec-trace.sh`
contract. Strict canonical parsing remains available through `spec_contract.py`
and becomes the default only after the approved retrofit and CI cutover tasks.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

import spec_contract as sc

REQ_HEADING_RE = re.compile(r"^## REQ-(\d+):")
CRITERION_RE = re.compile(r"^- (\d+)\.(\d+) ")
NEAR_MISS_RE = re.compile(r"^- \d+\.\d+")
REF_RE = re.compile(
    r"(?<![A-Za-z0-9_.])"
    r"(?:"
    r"(?:REQ-)?(?P<a1>\d+)\.(?P<b1>\d+)\s*[-–]\s*(?:REQ-)?(?P<a2>\d+)\.(?P<b2>\d+)"
    r"|(?:REQ-)?(?P<a>\d+)\.(?P<b>\d+)"
    r"|REQ-(?P<whole>\d+)(?!\.\d)"
    r")"
    r"(?![A-Za-z0-9])"
)
EARS_KEYWORD_RE = re.compile(r"(?<![A-Za-z])(?:WHEN|WHILE|WHERE)(?![A-Za-z])")
EARS_IF_RE = re.compile(r"(?<![A-Za-z])IF(?![A-Za-z])")
EARS_THEN_RE = re.compile(r"(?<![A-Za-z])THEN(?![A-Za-z])")


def parse_requirements(text: str) -> tuple[list[tuple[int, int, str]], bool]:
    """Return historical criteria and whether the artifact has REQ headings."""
    criteria: list[tuple[int, int, str]] = []
    has_headings = False
    in_req_section = False
    current: tuple[int, int, list[str]] | None = None

    def flush() -> None:
        nonlocal current
        if current is not None:
            major, minor, lines = current
            criteria.append((major, minor, re.sub(r"\s+", " ", " ".join(lines)).strip()))
            current = None

    for line in text.splitlines():
        if line.startswith("#"):
            flush()
            if REQ_HEADING_RE.match(line):
                in_req_section = True
                has_headings = True
            elif not line.startswith("###"):
                in_req_section = False
            continue
        match = CRITERION_RE.match(line)
        if match and in_req_section:
            flush()
            current = (int(match.group(1)), int(match.group(2)), [line[match.end():]])
            continue
        if in_req_section and NEAR_MISS_RE.match(line):
            print(
                "คำเตือน: บรรทัดคล้ายเกณฑ์แต่ไม่ตรงรูปแบบ '- N.M <ข้อความ>' "
                f"(จะไม่ถูกนับ): {line.strip()}",
                file=sys.stderr,
            )
        if current is not None:
            if line.strip() and line.startswith(" "):
                current[2].append(line.strip())
            else:
                flush()
    flush()
    return criteria, has_headings


def expand_refs(segment: str, criteria_by_req: dict[int, set[int]]) -> set[tuple[int, int]]:
    """Expand the pre-cutover trace reference grammar."""
    covered: set[tuple[int, int]] = set()
    for match in REF_RE.finditer(segment):
        if match.group("a1"):
            a1, b1, a2, b2 = (int(match.group(group)) for group in ("a1", "b1", "a2", "b2"))
            if a1 == a2:
                covered.update((a1, minor) for minor in range(min(b1, b2), max(b1, b2) + 1))
            else:
                covered.update({(a1, b1), (a2, b2)})
        elif match.group("a"):
            covered.add((int(match.group("a")), int(match.group("b"))))
        else:
            major = int(match.group("whole"))
            covered.update((major, minor) for minor in criteria_by_req.get(major, ()))
    return covered


def design_traceability_text(design_text: str) -> str | None:
    match = re.search(r"^##\s+Requirement Traceability\s*$", design_text, re.MULTILINE)
    if not match:
        return None
    rest = design_text[match.end():]
    next_heading = re.search(r"^## ", rest, re.MULTILINE)
    return rest[: next_heading.start()] if next_heading else rest


def satisfies_text(tasks_text: str) -> str:
    """Return explicit Satisfies refs plus migrated bare REQ lines.

    Historical migration removed task metadata labels while preserving each
    trace line as an indented line beginning with ``REQ-``. Keep this reader
    compatible with both shapes; canonical validation stays in spec_contract.
    """
    segments: list[str] = []
    visible, _diagnostics = sc._outside_fence(
        tasks_text.splitlines(), Path("tasks.md")
    )
    in_comment = False
    current_task = False
    collecting_satisfies = False

    for _number, raw in visible:
        line = ""
        rest = raw
        while rest:
            if in_comment:
                marker = rest.find("-->")
                if marker < 0:
                    rest = ""
                    continue
                in_comment = False
                rest = rest[marker + 3:]
                continue
            marker = rest.find("<!--")
            if marker < 0:
                line += rest
                break
            line += rest[:marker]
            rest = rest[marker + 4:]
            in_comment = True

        if (
            sc.TASK_OPENING_RE.match(line)
            or re.match(r"^##\s+- \[[ x]\]\s+\S+", line)
            or line.startswith("- Historical task (")
        ):
            current_task = True
            collecting_satisfies = False
            continue
        if re.match(r"^\s*(?:[-+*]|\d+[.)])\s*\[", line):
            current_task = False
            collecting_satisfies = False
            continue
        if re.match(r"^\s{0,3}#", line):
            current_task = False
            collecting_satisfies = False
            continue
        if not current_task:
            continue
        if "Satisfies:" in line:
            segment = line.split("Satisfies:", 1)[1]
            collecting_satisfies = True
        elif collecting_satisfies and line[:1].isspace() and line.strip():
            segment = line.strip()
        elif re.match(r"^ {5}(?:REQ-\d+|\d+\.\d+)", line):
            segment = line.strip()
        else:
            if not line.strip() or not line[:1].isspace():
                collecting_satisfies = False
            continue
        segments.append(re.split(r"Depends on:|Verify:|Batch:", segment)[0])
    return "\n".join(segments)


def ears_ok(text: str) -> bool:
    return (
        "THE SYSTEM SHALL" in text
        or bool(EARS_KEYWORD_RE.search(text))
        or bool(EARS_IF_RE.search(text) and EARS_THEN_RE.search(text))
    )


def run(feature: str, specs_dir: Path) -> int:
    """Run the historical trace contract for one feature."""
    feature_dir, resolver_diagnostics = sc.resolve_feature_directory(specs_dir, feature)
    if resolver_diagnostics or feature_dir is None:
        if resolver_diagnostics:
            sc._print_diagnostics(resolver_diagnostics)
            return 2 if any(
                diagnostic.verdict == "engine-fail"
                for diagnostic in resolver_diagnostics
            ) else 1
        existing = ", ".join(sorted(path.name for path in specs_dir.iterdir() if path.is_dir())) if specs_dir.is_dir() else "-"
        print(f"ไม่พบ feature '{feature}' ใต้ {specs_dir} (ที่มี: {existing})", file=sys.stderr)
        print("ใช้: scripts/spec-trace.sh <feature>", file=sys.stderr)
        return 1

    requirements_data, requirements_diagnostics = sc._read_canonical_artifact(
        feature_dir, "requirements.md", specs_dir
    )
    if requirements_data is None:
        if (feature_dir / "bugfix.md").is_file():
            print(f"'{feature}' เป็น bugfix spec (มี bugfix.md ไม่มี requirements.md) — ข้ามการตรวจ traceability")
            return 0
        sc._print_diagnostics(requirements_diagnostics)
        return 2 if any(
            diagnostic.verdict == "engine-fail"
            for diagnostic in requirements_diagnostics
        ) else 1

    criteria, has_headings = parse_requirements(requirements_data.decode("utf-8"))
    if not has_headings:
        print(f"requirements.md ของ '{feature}' ไม่ใช่รูปแบบ REQ-based (ไม่มีหัวข้อ '## REQ-N:') — ข้ามการตรวจ traceability")
        return 0
    if not criteria:
        print(f"requirements.md ของ '{feature}' มีหัวข้อ REQ แต่ไม่พบเกณฑ์รูปแบบ '- N.M ...' เลย", file=sys.stderr)
        return 1

    problems: list[tuple[str, list[str]]] = []
    ears_bad = [f"{major}.{minor}: {text[:80]}" for major, minor, text in criteria if not ears_ok(text)]
    if ears_bad:
        problems.append(("EARS lint ไม่ผ่าน (ต้องมี THE SYSTEM SHALL / WHEN / WHILE / WHERE / IF...THEN):", ears_bad))

    criteria_by_req: dict[int, set[int]] = {}
    for major, minor, _ in criteria:
        criteria_by_req.setdefault(major, set()).add(minor)
    all_ids = [(major, minor) for major, minor, _ in criteria]

    design_data, design_diagnostics = sc._read_canonical_artifact(
        feature_dir, "design.md", specs_dir
    )
    if design_data is None:
        sc._print_diagnostics(design_diagnostics)
        return 2 if any(
            diagnostic.verdict == "engine-fail" for diagnostic in design_diagnostics
        ) else 1
    trace = design_traceability_text(design_data.decode("utf-8"))
    if trace is None:
        problems.append(("design.md ไม่มี section '## Requirement Traceability' — ถือว่าทุกเกณฑ์ยังไม่ถูกอ้าง:", [f"{major}.{minor}" for major, minor in all_ids]))
    else:
        design_covered = expand_refs(trace, criteria_by_req)
        missing = [f"{major}.{minor}" for major, minor in all_ids if (major, minor) not in design_covered]
        if missing:
            problems.append(("เกณฑ์ที่ไม่ถูกอ้างใน design.md (section Requirement Traceability):", missing))

    tasks_data, tasks_diagnostics = sc._read_canonical_artifact(
        feature_dir, "tasks.md", specs_dir
    )
    if tasks_data is None:
        sc._print_diagnostics(tasks_diagnostics)
        return 2 if any(
            diagnostic.verdict == "engine-fail" for diagnostic in tasks_diagnostics
        ) else 1
    tasks_covered = expand_refs(satisfies_text(tasks_data.decode("utf-8")), criteria_by_req)
    missing = [f"{major}.{minor}" for major, minor in all_ids if (major, minor) not in tasks_covered]
    if missing:
        problems.append(("เกณฑ์ที่ไม่ถูกอ้างใน tasks.md (บรรทัด Satisfies:):", missing))

    if problems:
        print(f"[{feature}] traceability ไม่ครบ (เกณฑ์ทั้งหมด {len(criteria)} ข้อ):")
        for header, items in problems:
            print(f"\n{header}")
            for item in items:
                print(f"  - {item}")
        return 1

    print(f"OK: '{feature}' เกณฑ์ {len(criteria)} ข้อ ถูกอ้างครบใน design.md และ tasks.md, EARS lint ผ่านทุกข้อ")
    return 0


def run_compatible_all(specs_dir: Path) -> int:
    """Run compatibility trace across every requirements directory."""
    features = sorted(
        path.name
        for path in specs_dir.iterdir()
        if path.is_dir() and (path / "requirements.md").is_file()
    )
    failures = [feature for feature in features if run(feature, specs_dir)]
    print(f"compatibility trace: checked {len(features)} / failures {len(failures)}")
    return 1 if failures else 0


def main(argv: list[str]) -> int:
    if len(argv) not in (2, 3):
        print("ใช้: scripts/spec-trace.sh <feature|--all-compatible> [<specs-dir>]", file=sys.stderr)
        return 1
    specs_dir = Path(argv[2]) if len(argv) == 3 else Path(__file__).resolve().parent.parent / ".ai" / "specs"
    if argv[1] == "--all-compatible":
        return run_compatible_all(specs_dir)
    return run(argv[1], specs_dir)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
