#!/usr/bin/env python3
"""Spec-to-Goal generator (platform-phase5-stage1) — แปลง approved SDD spec เป็น
`goal.draft.yaml` + `task-graph.draft.json` แบบ one-way, human-gated.

อ่านเฉพาะ `.ai/specs/<feature>/requirements.md` + `tasks.md` แล้วเขียน draft —
ไม่ freeze, ไม่ start run, ไม่เรียก platform CLI ใดๆ (REQ-4.6) และไม่ promote เอง
(`goal.yaml` / `task-graph.json` เกิดจากการ rename โดยมนุษย์เท่านั้น —
phase5-stage4 REQ-2.7).
Parsing semantics ทั้งหมด reuse จาก `spec_trace.py` (import เป็น library):
`parse_requirements` / `expand_refs` / `iter_task_blocks` — single source,
ไม่มีวัน drift จากตัวตรวจ traceability (REQ-2.3).

Draft ที่มี verification ค้าง (unresolved) ถูก emit ให้ `acceptance_criteria`
เป็น array ว่าง — `freezeContract` ปฏิเสธเชิงโครงสร้างทันที (REQ-4.4) — โดยเก็บ
เกณฑ์ทั้งหมดไว้ใน `pending_acceptance_criteria` ให้ human activate เอง.
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

import spec_trace

USAGE = "usage: scripts/spec-to-goal.sh <feature> [--force] [--specs-dir <path>]"

# marker ภายใน task block — ลำดับใดก็ได้; segment ของ marker ตัดท้ายที่ marker ถัดไป
MARKER_RE = re.compile(r"Satisfies:|Verify:|Depends on:|Batch:")

# หัว task block: checkbox (indent ได้ — iter_task_blocks รับผ่าน lstrip) + ordinal
# ที่กลายเป็น `T-<n>` ใน task graph (phase5-stage4 REQ-2.1/2.9)
TASK_HEAD_RE = re.compile(r"^\s*- \[[ x]\]\s*(\d+)\.\s*")

# grammar ของ `Depends on:` แคบโดยเจตนา (REQ-2.6): อ่านเฉพาะ run แรกของเลขคั่น
# จุลภาคที่ติดกับ marker แล้วหยุดที่อักขระแรกที่ไม่เข้ารูป — prose/วงเล็บที่ตามหลัง
# เลขจึงไม่ถูกนับเป็น dependency (spec จริงเขียน `Depends on: 1 (เหตุผล...)`)
DEPENDS_RE = re.compile(r"\s*(\d+(?:\s*,\s*\d+)*)")

# §11.2 default — human ปรับได้ตอน promote
MAX_DIFF_BUDGET_PER_TASK = 400

# §11.1 budget scaffold — pin ตาม console/backend/src/issues.ts `goalDraftYaml`
# (ค่าซ้ำโดยเจตนา: tooling ห้าม import runtime code ข้าม layer)
BUDGET_LINE = (
    "budget: { max_iterations_per_task: 8, max_hypotheses_per_failure: 3, "
    "max_total_tasks: 30, max_parallel_agents: 3, max_cost_units_per_task: 500, "
    "max_wallclock_per_task_min: 30 }"
)

BANNER_UNRESOLVED = [
    "# HUMAN: (1) review pending_acceptance_criteria + fill every TODO verification",
    "#        (2) rename pending_acceptance_criteria -> acceptance_criteria",
    "#            and delete the empty acceptance_criteria line below",
    '#            WARNING: freezeContract does NOT reject "TODO" *verification* strings',
    "#            — renaming without doing step (1) produces fake verifications; the",
    "#            structural gates are the empty acceptance_criteria below and the",
    '#            risk "TODO" placeholder (schema + freezeContract both reject it',
    "#            since phase5-stage2)",
    "#        (3) set risk (L0-L4), decide golden flags, set approval_policy",
    "#        (4) write goal.objective (one sentence) + fill scope/forbidden",
    "#        (5) when approved: rename goal.draft.yaml -> goal.yaml (promotion —",
    "#            the Console loop-managed banner and the loop CLI read only goal.yaml)",
    "#        do not hand-edit or reflow the machine-stamped provenance: line below",
]

BANNER_RESOLVED = [
    "# HUMAN: (1) review acceptance_criteria — every verification was prefilled from the spec",
    "#        (2) set risk (L0-L4), decide golden flags, set approval_policy",
    "#        (3) write goal.objective (one sentence) + fill scope/forbidden",
    "#        (4) when approved: rename goal.draft.yaml -> goal.yaml (promotion —",
    "#            the Console loop-managed banner and the loop CLI read only goal.yaml)",
    "#        do not hand-edit or reflow the machine-stamped provenance: line below",
]


class SpecError(Exception):
    """tasks.md ผิดรูปจนสร้าง draft ไม่ได้ — ข้อความเดียวส่งต่อให้ fail()."""


def fail(msg):
    """refusal ทุกจุด: เหตุผลบรรทัดเดียวบน stderr + exit code 1 (REQ-5.3)."""
    print(msg, file=sys.stderr)
    return 1


def title_from_h1(text, fallback):
    """goal.title จาก H1 (REQ-3.2): strip prefix `Requirements` / `ข้อกำหนด`
    และ separator นำหน้า (`:` / `—` / `-`)."""
    for line in text.splitlines():
        if line.startswith("# "):
            t = line[2:].strip()
            if t.startswith("Requirements"):
                t = t[len("Requirements"):]
            elif t.startswith("ข้อกำหนด"):
                t = t[len("ข้อกำหนด"):]
            return t.lstrip(" :—-").strip() or fallback
    return fallback


def task_maps(tasks_text, criteria_by_req):
    """คืน list ของ dict หนึ่งรายการต่อ task block: `ordinal`, `title`, `refs`
    (satisfies), `depends` (ordinal ของ task อื่น), `verify`.

    เดินไฟล์ด้วย `spec_trace.iter_task_blocks` (นิยาม block เดียวกับตัวตรวจ
    coverage — REQ-2.3) ซึ่งตัด block ที่ `Evidence:` header เองแล้ว (line-anchored,
    case-insensitive — transcript ไม่มีวันรั่วเข้า refs/verification ของทั้งสอง
    consumer, PR #102 Codex P2 + fanout review); ภายใน block: segment ของแต่ละ
    marker ตัดท้ายที่ marker ถัดไป, Satisfies ขยายผ่าน `expand_refs` (dash range /
    N.M / REQ-N ทั้งตัว), Verify copy verbatim (strip ปลายเท่านั้น — ไม่ตีความว่า
    เป็น command จริงไหม; คำว่า `Evidence:` กลาง command คงอยู่ครบ).

    ordinal/title/depends เป็นส่วนที่ task graph ใช้ (phase5-stage4 REQ-2.1) และ
    validation ทั้งสี่ทาง (ordinal หาย/ซ้ำ, Satisfies dangling, Depends dangling)
    ยกเลิกการสร้าง draft ทั้งใบผ่าน `SpecError` — draft ที่มี edge/AC ค้างเติ่ง
    ห้ามเกิด (REQ-2.6/2.8/2.9).
    """
    entries = []
    seen_ordinals = set()
    for block in spec_trace.iter_task_blocks(tasks_text):
        head = TASK_HEAD_RE.match(block)
        if head is None:
            raise SpecError(f"task block has no leading ordinal '- [ ] N.': {block[:60]}")
        ordinal = int(head.group(1))
        if ordinal in seen_ordinals:
            raise SpecError(f"duplicate task ordinal {ordinal} in tasks.md: {block[:60]}")
        seen_ordinals.add(ordinal)

        rest = block[head.end():]
        first_marker = MARKER_RE.search(rest)
        # ไม่มี marker เลย -> ทั้งท้าย block เป็น title (D13)
        title = (rest[:first_marker.start()] if first_marker else rest).strip()[:120].rstrip()

        markers = list(MARKER_RE.finditer(block))
        refs = set()
        verify_cmd = ""
        depends = []
        for j, m in enumerate(markers):
            end = markers[j + 1].start() if j + 1 < len(markers) else len(block)
            segment = block[m.end():end]
            if m.group(0) == "Satisfies:":
                refs |= spec_trace.expand_refs(segment, criteria_by_req)
            elif m.group(0) == "Verify:" and not verify_cmd:
                verify_cmd = segment.strip()
            elif m.group(0) == "Depends on:":
                # ทุก occurrence สะสมเหมือน Satisfies: — task ที่ *อธิบาย* marker นี้
                # ในเนื้อความ (backtick) มี `Depends on:` ปลอมมาก่อนตัวจริง; ถ้าเอา
                # occurrence แรกอย่างเดียว dependency จริงจะหายเงียบ
                run = DEPENDS_RE.match(block, m.end())
                if run:
                    depends += [int(n) for n in re.split(r"\s*,\s*", run.group(1))]
        for major, minor in sorted(refs):
            if minor not in criteria_by_req.get(major, ()):
                raise SpecError(f"task {ordinal} Satisfies ref AC-{major}.{minor} has no "
                                "matching criterion in requirements.md")
        entries.append({"ordinal": ordinal, "title": title, "refs": refs,
                        "depends": list(dict.fromkeys(depends)), "verify": verify_cmd})

    for entry in entries:
        for dep in entry["depends"]:
            if dep not in seen_ordinals:
                raise SpecError(f"task {entry['ordinal']} 'Depends on:' references task {dep} "
                                "which has no task block")
    return entries


def build_acs(criteria, maps):
    """AC หนึ่งตัวต่อเกณฑ์ (REQ-2.1/2.2); verification resolved เมื่อมี task cover
    ตัวเดียวพอดีและ task นั้นมี Verify ไม่ว่าง (REQ-2.4) — นอกนั้น None (REQ-2.5)."""
    acs = []
    for major, minor, text in criteria:
        covers = [e["verify"] for e in maps if (major, minor) in e["refs"]]
        verification = covers[0] if len(covers) == 1 and covers[0] else None
        acs.append({"id": f"AC-{major}.{minor}", "description": text,
                    "verification": verification})
    return acs


def ac_line(ac):
    # scalar ทุกตัวผ่าน json.dumps — JSON string เป็น valid YAML double-quoted
    # scalar (precedent: issues.ts ใช้ JSON.stringify ด้วยเหตุผลเดียวกัน; REQ-3.7)
    verification = ac["verification"] if ac["verification"] is not None else "TODO"
    return (f"  - {{ id: {json.dumps(ac['id'])}, "
            f"description: {json.dumps(ac['description'], ensure_ascii=False)}, "
            f"verification: {json.dumps(verification, ensure_ascii=False)} }}")


def goal_id_for(feature):
    """goal.id (REQ-3.1) — task graph ผูกกับ contract ด้วยค่าเดียวกันนี้
    (phase5-stage4 REQ-2.1/3.14)."""
    return re.sub(r"[^A-Za-z0-9-]", "-", feature).upper() + "-001"


def emit_task_graph(goal_id, entries):
    """task-graph draft (phase5-stage4 REQ-2.1/2.2/2.3): ลำดับ key pin ไว้ตรงนี้
    (`goal_id, tasks, checks`; task: `id, title, satisfies, depends_on` — omit
    `depends_on` เมื่อไม่มี), 2-space indent + newline ปิดท้าย. ensure_ascii=False
    ให้ title ภาษาไทยอ่านออกในไฟล์ที่มนุษย์ต้อง review ก่อน promote."""
    tasks = []
    for e in entries:
        task = {"id": f"T-{e['ordinal']}", "title": e["title"],
                "satisfies": [f"AC-{major}.{minor}" for major, minor in sorted(e["refs"])]}
        if e["depends"]:
            task["depends_on"] = [f"T-{n}" for n in e["depends"]]
        tasks.append(task)
    graph = {"goal_id": goal_id, "tasks": tasks,
             "checks": {"max_diff_budget_per_task": MAX_DIFF_BUDGET_PER_TASK}}
    return json.dumps(graph, indent=2, ensure_ascii=False) + "\n"


def emit(feature, title, acs, spec_path, sha, head_commit, generated_at):
    unresolved = sum(1 for a in acs if a["verification"] is None)
    goal_id = goal_id_for(feature)
    # single-line flow mapping, every key + string value JSON-quoted so the
    # `{...}` remainder parses with json.loads (REQ-2.1); top-level key,
    # column 0, ahead of goal: (drift checker anchors on `^provenance:` — task 4)
    provenance_line = (
        f'provenance: {{ "spec_path": {json.dumps(spec_path)}, '
        f'"requirements_commit": {json.dumps(head_commit)}, '
        f'"requirements_sha256": {json.dumps(sha)}, '
        f'"generated_at": {json.dumps(generated_at)} }}'
    )
    lines = ["# spec-to-goal draft — DO NOT run as-is"]
    lines += BANNER_UNRESOLVED if unresolved else BANNER_RESOLVED
    lines.append(provenance_line)
    lines += [
        f"goal: {{ id: {json.dumps(goal_id)}, "
        f"title: {json.dumps(title, ensure_ascii=False)}, objective: \"TODO\" }}",
        'risk: "TODO"',
        "scope:",
        '  include: ["TODO"]',
        '  exclude: ["TODO"]',
        "constraints:",
        '  forbidden: ["TODO"]',
    ]
    if unresolved:
        # REQ-4.4: active array ว่าง = freezeContract ปฏิเสธไฟล์ทั้งใบเชิงโครงสร้าง
        lines.append("acceptance_criteria: []   "
                     "# gate: freezeContract rejects empty — activate per HUMAN step (2)")
        lines.append("pending_acceptance_criteria:")
    else:
        # REQ-4.5 (superseded by stage-2 REQ-3.7): resolved ครบ — เกณฑ์ทั้งหมด active;
        # freeze ยังปฏิเสธที่ risk "TODO" จนกว่า human ตั้งค่าจริง (D5 human gate)
        lines.append("acceptance_criteria:")
    lines += [ac_line(a) for a in acs]
    lines += [
        BUDGET_LINE,
        "approval_policy:",
        '  require_human_approval: ["TODO"]',
        "",
    ]
    return "\n".join(lines)


def write_atomic(path, content):
    """temp ในโฟลเดอร์เดียวกัน + os.replace — ไม่มี partial file ค้างให้ชน
    output gate รอบถัดไป (REQ-4.2/2.5)."""
    tmp_name = None
    try:
        with tempfile.NamedTemporaryFile("w", encoding="utf-8", dir=path.parent,
                                         prefix=f".{path.name}.", suffix=".tmp",
                                         delete=False) as tmp:
            tmp_name = tmp.name
            tmp.write(content)
        os.replace(tmp_name, path)
    except BaseException:
        if tmp_name is not None and os.path.exists(tmp_name):
            os.unlink(tmp_name)
        raise


def main(argv=None):
    parser = argparse.ArgumentParser(
        prog="spec-to-goal", usage=USAGE,
        description="Generate goal.draft.yaml from an approved REQ-form spec.")
    parser.add_argument("feature")
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--specs-dir", default=None)
    args = parser.parse_args(argv)

    # --- gate chain: paths -> header -> parse -> structural (1.5 -> 1.7 -> 1.8) ---
    specs_dir = Path(args.specs_dir) if args.specs_dir \
        else Path(__file__).resolve().parent.parent / ".ai" / "specs"
    feature_dir = specs_dir / args.feature
    req_path = feature_dir / "requirements.md"
    if not req_path.is_file():
        return fail(f"error: {req_path} not found — {USAGE}")

    raw = req_path.read_bytes()
    # hash + decode จาก buffer เดียวกัน — ห้าม read_text().encode() (newline
    # translation ทำ sha256 anchor เพี้ยนบนไฟล์ CRLF/BOM; supersedes stage-1
    # REQ-3.5, now phase5-stage3 REQ-2.6)
    sha = hashlib.sha256(raw).hexdigest()
    text = raw.decode("utf-8")

    status_line = next((ln for ln in text.splitlines() if ln.startswith("> Status:")), None)
    if status_line is None:
        return fail('error: requirements.md has no "> Status:" header line — '
                    "approve the spec first (no bypass)")
    if not status_line.startswith("> Status: approved"):
        return fail(f'error: requirements.md status is "{status_line.strip()}" — '
                    "spec must be approved before generating a goal draft (no bypass)")

    criteria, has_headings = spec_trace.parse_requirements(text)
    if not has_headings:
        return fail("error: no '## REQ-' headings found — only REQ-form feature "
                    "specs are supported (bugfix F-/B- specs are not)")

    seen = set()
    for major, minor, _ in criteria:
        if (major, minor) in seen:
            return fail(f"error: duplicate criterion id {major}.{minor} in requirements.md")
        seen.add((major, minor))

    # rescan heading เอง — parse_requirements ไม่คืนรายการ heading, REQ ที่ว่าง
    # จะหายเงียบจาก criteria ถ้าไม่เทียบกลับ (REQ-1.8)
    heading_majors = [int(m.group(1)) for ln in text.splitlines()
                      if (m := spec_trace.REQ_HEADING_RE.match(ln))]
    criteria_majors = {major for major, _, _ in criteria}
    empty_reqs = [n for n in heading_majors if n not in criteria_majors]
    if empty_reqs:
        return fail(f"error: REQ-{empty_reqs[0]} has no criterion lines "
                    "('- N.M ...') under it")

    # --- tasks.md -> per-task maps (absent = warn + ทุกตัว unresolved; REQ-1.6) ---
    criteria_by_req = {}
    for major, minor, _ in criteria:
        criteria_by_req.setdefault(major, set()).add(minor)
    tasks_path = feature_dir / "tasks.md"
    if tasks_path.is_file():
        try:
            maps = task_maps(tasks_path.read_text(encoding="utf-8"), criteria_by_req)
        except SpecError as e:
            return fail(f"error: {e}")
    else:
        print(f"warning: {tasks_path} not found — every verification left unresolved, "
              "task-graph draft skipped", file=sys.stderr)
        maps = []
    if tasks_path.is_file() and not maps:
        print(f"warning: {tasks_path} has no task blocks — task-graph draft skipped",
              file=sys.stderr)
    acs = build_acs(criteria, maps)

    # --- provenance (phase5-stage3 REQ-2) ---
    head_commit = "unknown"
    try:
        proc = subprocess.run(["git", "-C", str(feature_dir), "rev-parse", "HEAD"],
                              capture_output=True, text=True)
        if proc.returncode == 0:
            head_commit = proc.stdout.strip()
    except OSError:
        pass
    if head_commit == "unknown":
        print('warning: git rev-parse HEAD failed — head_commit recorded as "unknown"',
              file=sys.stderr)
    generated_at = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    # spec_path: repo-root-relative when requirements.md lies inside the repo;
    # outside (e.g. a temp --specs-dir in tests) -> the path as given, as-is.
    # Human-facing metadata only — the drift checker never resolves it (A1).
    repo_root = Path(__file__).resolve().parent.parent
    try:
        spec_path = str(req_path.resolve().relative_to(repo_root))
    except ValueError:
        spec_path = str(req_path)

    out_path = feature_dir / "goal.draft.yaml"
    resolved = sum(1 for a in acs if a["verification"] is not None)
    outputs = [(out_path,
                emit(args.feature, title_from_h1(text, args.feature), acs,
                     spec_path, sha, head_commit, generated_at),
                f"{len(acs)} acceptance criteria, {resolved} resolved / "
                f"{len(acs) - resolved} unresolved verifications")]
    if maps:
        outputs.append((feature_dir / "task-graph.draft.json",
                        emit_task_graph(goal_id_for(args.feature), maps),
                        f"{len(maps)} tasks"))

    # --- output gate หลัง validation ทั้งหมด (user เห็น error ของ spec ก่อนเรื่อง
    # --force) — แยกต่อไฟล์ (REQ-2.5): draft ที่ชนบล็อกแค่ตัวเอง, ที่เหลือเขียนต่อ ---
    exit_code = 0
    for path, content, summary in outputs:
        if path.exists() and not args.force:
            exit_code = fail(f"error: {path} already exists — pass --force to overwrite")
            continue
        write_atomic(path, content)
        print(f"wrote {path} — {summary}")
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
