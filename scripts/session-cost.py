#!/usr/bin/env python3
"""พิมพ์ Session Cost แบบละเอียด (per-model + cache tiers) ของ session เดียว.

  scripts/session-cost.py [session-id] [--breakdown-only]

ไม่ระบุ id -> ใช้ $CLAUDE_CODE_SESSION_ID (session ปัจจุบัน). cost total ดึงจาก
ledger (authoritative); token breakdown ต่อ model ดึงจาก transcript. ดู cost_lib.py
สำหรับวิธีปันส่วน cost ต่อชั้น (ไม่ recompute เป็น cost จริง).

--breakdown-only: พิมพ์เฉพาะตาราง breakdown (ไม่มี total/duration/lines) — ใช้แปะ
ใต้ section Session Cost ที่ spec-retro skill เขียนไว้แล้ว.
"""
import os
import sys
from cost_lib import ledger_for, render_breakdown

args = [a for a in sys.argv[1:] if not a.startswith("--")]
breakdown_only = "--breakdown-only" in sys.argv[1:]

sid = args[0] if args else os.environ.get("CLAUDE_CODE_SESSION_ID", "")
if not sid:
    sys.exit("ใช้: session-cost.py <session-id>  (หรือ set $CLAUDE_CODE_SESSION_ID)")

led = ledger_for(sid)
if led is None:
    sys.exit(f"ไม่พบ session '{sid}' ใน ledger (~/.claude/cost-sessions/) — "
             "session อาจปิดก่อนติดตั้ง ledger หรือ id ผิด")

cost, dur, la, lr, full = led

if breakdown_only:
    print("\n".join(render_breakdown(full, cost, heading="Breakdown ต่อ model")))
    sys.exit(0)

m, s = dur // 60000, (dur % 60000) // 1000
print(f"# Session Cost — {full[:8]}")
print()
print(f"- total: **${cost:.2f}** (authoritative; subscription ไม่คิดเงินจริง = ค่าประมาณ Claude Code)")
print(f"- duration: {m}m{s:02d}s")
print(f"- lines: +{la:,} / -{lr:,}")
print()
print("\n".join(render_breakdown(full, cost, heading="Breakdown ต่อ model")))
