#!/usr/bin/env python3
"""ตัวตรวจ requirements traceability (deterministic) สำหรับ spec ใต้ .ai/specs/<feature>/.

ตรวจ 2 เรื่อง:
1. coverage — เกณฑ์ (criterion) ทุกข้อใน requirements.md (บรรทัด `- N.M ...` ใต้หัวข้อ
   `## REQ-N:`) ต้องถูกอ้างถึงทั้งใน design.md (เฉพาะ section `## Requirement Traceability`)
   และใน tasks.md (เฉพาะบรรทัด `Satisfies:`). รูปแบบอ้างอิงที่ขยายให้:
     - id เปล่า:        15.1, 15.3
     - ช่วง dash:       17.1-17.4  -> 17.1, 17.2, 17.3, 17.4
     - prefix:          REQ-1.2
     - ทั้ง REQ:        REQ-1 / REQ-1 (all criteria) -> ทุกเกณฑ์ของ REQ-1
   วงเล็บกำกับ เช่น `(partial)` ไม่ทำให้ parse พัง.
2. EARS lint — ทุกเกณฑ์ต้องมี THE SYSTEM SHALL / WHEN / WHILE / WHERE / IF...THEN
   (ดูข้อความเต็มของเกณฑ์รวมบรรทัดต่อเนื่องที่ indent).

requirements.md ที่ไม่มีหัวข้อ `## REQ-N:` เลย (เช่น bugfix spec) -> ข้ามการตรวจ, exit 0.
เกณฑ์ตกหล่น/EARS ไม่ผ่าน -> รายงานเป็นภาษาไทยแล้ว exit 1; ครบหมด -> 1 บรรทัด OK, exit 0.
"""
import re
import sys
from pathlib import Path

REQ_HEADING_RE = re.compile(r"^## REQ-(\d+):")
CRITERION_RE = re.compile(r"^- (\d+)\.(\d+) ")
# บรรทัดที่ "เกือบ" เป็นเกณฑ์ (เช่น `- 2.3. text`) — ใช้เตือนกัน criterion หายเงียบ
NEAR_MISS_RE = re.compile(r"^- \d+\.\d+")
# อ้างอิงเกณฑ์: ลองช่วง dash ก่อน แล้วค่อย id เดี่ยว แล้วค่อยทั้ง REQ (ลำดับสำคัญ)
# guard ขอบ token: ห้ามมีตัวอักษร/ตัวเลข/จุด ติดหน้า และห้ามมีตัวอักษร/ตัวเลข ติดหลัง
# (กัน prose เช่น "v2.2" / "2.2s" นับเป็นการอ้างเกณฑ์ 2.2) — `,` `.` `-` ตามหลังยังผ่าน
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


def parse_requirements(text):
    """คืน (criteria, has_headings).

    criteria = list ของ (major:int, minor:int, full_text:str) ตามลำดับในไฟล์ —
    เฉพาะบรรทัด `- N.M ` ที่อยู่ใต้หัวข้อ `## REQ-N:`; full_text รวมบรรทัดต่อเนื่อง
    ที่ indent (join เป็นช่องว่างเดียว) เพื่อให้ lint เห็นคีย์เวิร์ดที่ถูกตัดขึ้นบรรทัดใหม่.
    """
    criteria = []
    has_headings = False
    in_req_section = False
    current = None  # (major, minor, [lines])

    def flush():
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
                in_req_section = False  # หัวข้อระดับ 1-2 ที่ไม่ใช่ REQ = จบ section
            # หัวข้อระดับ 3+ (###) เป็นหัวข้อย่อยใน REQ — คง section เดิม
            continue
        m = CRITERION_RE.match(line)
        if m and in_req_section:
            flush()
            current = (int(m.group(1)), int(m.group(2)), [line[m.end():]])
            continue
        if in_req_section and NEAR_MISS_RE.match(line):
            print("คำเตือน: บรรทัดคล้ายเกณฑ์แต่ไม่ตรงรูปแบบ '- N.M <ข้อความ>' "
                  f"(จะไม่ถูกนับ): {line.strip()}", file=sys.stderr)
        if current is not None:
            if line.strip() and line.startswith(" "):
                current[2].append(line.strip())
            else:
                flush()  # บรรทัดว่าง / bullet อื่น / ข้อความชิดซ้าย = จบเกณฑ์
    flush()
    return criteria, has_headings


def expand_refs(segment, criteria_by_req):
    """ขยายข้อความอ้างอิงเป็น set ของ (major, minor).

    criteria_by_req = dict major -> set(minor) ของเกณฑ์จริง (ใช้ขยายรูปทั้ง REQ).
    """
    covered = set()
    for m in REF_RE.finditer(segment):
        if m.group("a1"):  # ช่วง dash เช่น 17.1-17.4
            a1, b1, a2, b2 = (int(m.group(g)) for g in ("a1", "b1", "a2", "b2"))
            if a1 == a2:
                covered.update((a1, minor) for minor in range(min(b1, b2), max(b1, b2) + 1))
            else:  # ช่วงข้าม REQ ไม่นิยาม — นับเฉพาะปลายทั้งสอง
                covered.update({(a1, b1), (a2, b2)})
        elif m.group("a"):  # id เดี่ยว (มี/ไม่มี prefix REQ-)
            covered.add((int(m.group("a")), int(m.group("b"))))
        else:  # ทั้ง REQ เช่น REQ-1
            major = int(m.group("whole"))
            covered.update((major, minor) for minor in criteria_by_req.get(major, ()))
    return covered


def design_traceability_text(design_text):
    """คืนเนื้อหา section `## Requirement Traceability` (ถึงหัวข้อ ## ถัดไป) หรือ None."""
    m = re.search(r"^##\s+Requirement Traceability\s*$", design_text, re.MULTILINE)
    if not m:
        return None
    rest = design_text[m.end():]
    nxt = re.search(r"^## ", rest, re.MULTILINE)
    return rest[: nxt.start()] if nxt else rest


def satisfies_text(tasks_text):
    """รวมเฉพาะส่วนอ้างอิงบนบรรทัด `Satisfies:` (ตัดท้ายที่ Depends on:/Verify:/Batch:).

    บรรทัดต่อเนื่องที่ indent (ไม่ว่าง, ไม่ใช่ checkbox `- [ ]`/`- [x]` ใหม่)
    ถูก join เข้ากับบรรทัด Satisfies: ก่อนตัดท้าย — กัน reference ที่ถูก wrap หล่นหาย.
    """
    segments = []
    lines = tasks_text.splitlines()
    i = 0
    while i < len(lines):
        line = lines[i]
        i += 1
        if "Satisfies:" not in line:
            continue
        parts = [line.split("Satisfies:", 1)[1]]
        while i < len(lines):
            nxt = lines[i]
            if not nxt.strip() or not nxt[0].isspace():
                break
            if nxt.lstrip().startswith(("- [ ]", "- [x]")):
                break
            parts.append(nxt.strip())
            i += 1
        segments.append(re.split(r"Depends on:|Verify:|Batch:", " ".join(parts))[0])
    return "\n".join(segments)


def ears_ok(text):
    if "THE SYSTEM SHALL" in text:
        return True
    if EARS_KEYWORD_RE.search(text):
        return True
    return bool(EARS_IF_RE.search(text) and EARS_THEN_RE.search(text))


def run(feature, specs_dir):
    """ตรวจ feature เดียว; print ผลแล้วคืน exit code."""
    feature_dir = specs_dir / feature
    if not feature_dir.is_dir():
        existing = ", ".join(sorted(p.name for p in specs_dir.iterdir() if p.is_dir())) \
            if specs_dir.is_dir() else "-"
        print(f"ไม่พบ feature '{feature}' ใต้ {specs_dir} (ที่มี: {existing})", file=sys.stderr)
        print("ใช้: scripts/spec-trace.sh <feature>", file=sys.stderr)
        return 1

    req_path = feature_dir / "requirements.md"
    if not req_path.is_file():
        if (feature_dir / "bugfix.md").is_file():
            print(f"'{feature}' เป็น bugfix spec (มี bugfix.md ไม่มี requirements.md) — "
                  "ข้ามการตรวจ traceability")
            return 0
        print(f"ไม่พบไฟล์ {req_path}", file=sys.stderr)
        print("ใช้: scripts/spec-trace.sh <feature>", file=sys.stderr)
        return 1

    criteria, has_headings = parse_requirements(req_path.read_text(encoding="utf-8"))
    if not has_headings:
        print(f"requirements.md ของ '{feature}' ไม่ใช่รูปแบบ REQ-based "
              "(ไม่มีหัวข้อ '## REQ-N:') — ข้ามการตรวจ traceability")
        return 0
    if not criteria:
        print(f"requirements.md ของ '{feature}' มีหัวข้อ REQ แต่ไม่พบเกณฑ์รูปแบบ '- N.M ...' เลย",
              file=sys.stderr)
        return 1

    problems = []  # list ของ (หัวข้อกลุ่ม, [บรรทัดรายการ])

    # --- EARS lint ---
    ears_bad = [f"{maj}.{mnr}: {text[:80]}" for maj, mnr, text in criteria if not ears_ok(text)]
    if ears_bad:
        problems.append(("EARS lint ไม่ผ่าน (ต้องมี THE SYSTEM SHALL / WHEN / WHILE / WHERE / "
                         "IF...THEN):", ears_bad))

    criteria_by_req = {}
    for maj, mnr, _ in criteria:
        criteria_by_req.setdefault(maj, set()).add(mnr)
    all_ids = [(maj, mnr) for maj, mnr, _ in criteria]

    # --- coverage: design.md ---
    design_path = feature_dir / "design.md"
    if not design_path.is_file():
        print(f"ไม่พบไฟล์ {design_path} (spec แบบ REQ-based ต้องมี design.md)", file=sys.stderr)
        return 1
    trace = design_traceability_text(design_path.read_text(encoding="utf-8"))
    if trace is None:
        problems.append(("design.md ไม่มี section '## Requirement Traceability' — "
                         "ถือว่าทุกเกณฑ์ยังไม่ถูกอ้าง:", [f"{a}.{b}" for a, b in all_ids]))
    else:
        design_covered = expand_refs(trace, criteria_by_req)
        missing = [f"{a}.{b}" for a, b in all_ids if (a, b) not in design_covered]
        if missing:
            problems.append(("เกณฑ์ที่ไม่ถูกอ้างใน design.md (section Requirement Traceability):",
                             missing))

    # --- coverage: tasks.md ---
    tasks_path = feature_dir / "tasks.md"
    if not tasks_path.is_file():
        print(f"ไม่พบไฟล์ {tasks_path} (spec แบบ REQ-based ต้องมี tasks.md)", file=sys.stderr)
        return 1
    tasks_covered = expand_refs(satisfies_text(tasks_path.read_text(encoding="utf-8")),
                                criteria_by_req)
    missing = [f"{a}.{b}" for a, b in all_ids if (a, b) not in tasks_covered]
    if missing:
        problems.append(("เกณฑ์ที่ไม่ถูกอ้างใน tasks.md (บรรทัด Satisfies:):", missing))

    if problems:
        print(f"[{feature}] traceability ไม่ครบ (เกณฑ์ทั้งหมด {len(criteria)} ข้อ):")
        for header, items in problems:
            print(f"\n{header}")
            for item in items:
                print(f"  - {item}")
        return 1

    print(f"OK: '{feature}' เกณฑ์ {len(criteria)} ข้อ ถูกอ้างครบใน design.md และ tasks.md, "
          "EARS lint ผ่านทุกข้อ")
    return 0


def main(argv):
    if len(argv) != 2:
        print("ใช้: scripts/spec-trace.sh <feature>   (feature = โฟลเดอร์ใต้ .ai/specs/)",
              file=sys.stderr)
        return 1
    specs_dir = Path(__file__).resolve().parent.parent / ".ai" / "specs"
    return run(argv[1], specs_dir)


if __name__ == "__main__":
    sys.exit(main(sys.argv))
