#!/usr/bin/env python3
"""ฉีด cost จริง (จาก ledger) เข้าท้ายไฟล์ retrospective ของแต่ละ session. idempotent
(มี marker แล้วข้าม). ค่าคงที่/regex ที่ผูกกับ workflow อยู่ใน cost_lib.py.

map retro->cost: parse `Session ID` ที่ retro template ฝังไว้ในไฟล์ แล้ว lookup ledger ตรง
ตาม sid นั้น (robust: ไม่พึ่ง mtime-index แบบเดิม, รองรับ all-in-one ที่ 1 retro ครอบหลาย
task — cost ของทั้ง session ถูกฉีดเข้า retro ใบเดียวนั้น).
"""
import glob, os, re
from cost_lib import ledger_for, render_breakdown

RETRO_GLOB = "retrospectives/*/*/*.md"
MARK = "## Cost (จริง — Claude Code ledger)"
# retro template ฝัง "Session ID: `<uuid>`" -> ดึง uuid มา lookup ledger ตรง ๆ
SID_RE = re.compile(r"Session ID\D*([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-"
                    r"[0-9a-f]{4}-[0-9a-f]{12})", re.I)

for rf in sorted(glob.glob(RETRO_GLOB)):
    body = open(rf, encoding="utf-8").read()
    if MARK in body:
        continue  # idempotent
    m = SID_RE.search(body)
    led = ledger_for(m.group(1)) if m else None
    if led:
        c, dur, la, lr, full = led
        mm = dur // 60000
        ss = (dur % 60000) // 1000
        bd = "\n".join(render_breakdown(full, c, heading="Breakdown ต่อ model"))
        sec = (f"\n{MARK}\n\n"
               f"- cost: **${c:.2f}** (ค่าประมาณ Claude Code; subscription ไม่คิดเงินจริง)\n"
               f"- duration: {mm}m{ss:02d}s\n"
               f"- lines: +{la:,} / -{lr:,}\n"
               f"- แหล่ง: `Session ID` ในไฟล์ -> `.cost.total_cost_usd` ledger\n\n"
               f"{bd}\n")
    else:
        why = "ไม่พบ Session ID ในไฟล์" if not m else "session ปิดก่อนติดตั้ง ledger (resume reset=0)"
        sec = f"\n{MARK}\n\n- cost: ไม่บันทึก ({why})\n"
    open(rf, "a", encoding="utf-8").write(sec)
    print(f"{os.path.basename(rf)}: {('$%.2f' % led[0]) if led else 'n/a'}")
