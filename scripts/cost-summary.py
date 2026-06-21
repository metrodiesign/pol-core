#!/usr/bin/env python3
"""สรุป cost จริงต่อ session (1 session = 1..N task; all-in-one ครอบหลาย task) จาก ledger
(~/.claude/cost-sessions/) -> ตาราง markdown ที่ retrospectives/cost-<feature>.md.
cost = .cost.total_cost_usd จาก Claude Code (authoritative, per-session). task ที่ปิดก่อนมี
ledger = ไม่มีข้อมูล.

Generic: project/feature auto-detect. ค่าคงที่/regex ที่ผูกกับ workflow อยู่ใน cost_lib.py.
"""
import os
from cost_lib import detect_feature, all_task_ids, session_costs, render_breakdown

MY_SESSION = os.environ.get("MY_SESSION", "")  # orchestrator session id to exclude
FEATURE = detect_feature()
TASKS = f".ai/specs/{FEATURE}/tasks.md"
OUT = f"retrospectives/cost-{FEATURE}.md"


def fmt_ids(ids):
    """[1,2,3,4] -> '1-4' (ช่วงต่อเนื่อง); ไม่ต่อเนื่อง -> '1,3,5'."""
    if len(ids) > 1 and ids == list(range(ids[0], ids[-1] + 1)):
        return f"{ids[0]}-{ids[-1]}"
    return ",".join(str(i) for i in ids)


sessions = session_costs(exclude_session=MY_SESSION)
ALL = all_task_ids(TASKS)

L = [f"# Cost จริงต่อ session — {FEATURE}",
     "",
     "แหล่ง: `.cost.total_cost_usd` จาก payload statusLine ของ Claude Code (เก็บผ่าน ledger",
     "`~/.claude/cost-sessions/`). cost เป็น per-session (1 session = 1..N task; all-in-one",
     "ครอบหลาย task จึงรายงานรวมเป็นแถวเดียว). เป็นเลขเดียวกับที่ `/cost` แสดง.",
     "",
     "| tasks | cost $ | duration | +lines | -lines |",
     "|------|------:|---------|------:|------:|"]
tot = 0.0
covered = set()
for s in sessions:
    tot += s["cost"]
    covered |= set(s["ids"])
    m = s["dur"] // 60000
    sec = (s["dur"] % 60000) // 1000
    tag = " (all-in-one)" if len(s["ids"]) > 1 else ""
    L.append(f"| {fmt_ids(s['ids'])}{tag} | {s['cost']:.2f} | {m}m{sec:02d}s "
             f"| {s['la']:,} | {s['lr']:,} |")
missing = [t for t in ALL if t not in covered]
L += ["", f"**รวมที่บันทึกได้: ${tot:.2f}** ({len(sessions)} sessions)",
      "", f"_task ที่มีข้อมูล: {', '.join(str(t) for t in sorted(covered)) or '—'}_"]
if missing:
    L.append(f"_task ที่ไม่มีข้อมูล: {', '.join(str(t) for t in missing)} "
             "(ปิดก่อนติดตั้ง ledger -> cost จริงหายถาวร; resume reset=0, transcript-sum ไม่แม่น)_")

# breakdown ละเอียดต่อ session (per-model + cache) ใต้ตารางสรุป
L += ["", "## Breakdown ละเอียดต่อ session", ""]
for s in sessions:
    L += render_breakdown(s["sid"], s["cost"],
                          heading=f"tasks {fmt_ids(s['ids'])} — ${s['cost']:.2f}") + [""]

os.makedirs(os.path.dirname(OUT), exist_ok=True)
open(OUT, "w", encoding="utf-8").write("\n".join(L) + "\n")
print("\n".join(L))
print(f"\n-> เขียน {OUT}")
