#!/usr/bin/env python3
"""ค่าคงที่ + helper ที่ผูกกับ spec-driven workflow — ใช้ร่วมโดย cost-summary.py และ
inject-cost.py. ถ้า workflow เปลี่ยน (ชื่อ slash command / รูปแบบ task id / สไตล์
checkbox ใน tasks.md) แก้ที่ไฟล์นี้ที่เดียว ทั้งสองสคริปต์ได้ผลตาม.

ไม่ครอบ: การ map retro->task ด้วย mtime ใน inject-cost.py (ผูกสมมติฐาน 1 retro/task
เรียงตามเวลา — ไม่มี literal ให้ย้าย, ดูคอมเมนต์ในไฟล์นั้น).
"""
import json, glob, os, re, sys

# ===== workflow-coupled config (แก้ที่นี่ที่เดียวถ้า workflow เปลี่ยน) =====
IMPLEMENT_CMD = "/spec-implement"   # command ที่สั่ง implement task (มองหาใน transcript)
RETRO_CMD     = "/spec-retro"       # command ที่ยืนยัน session ทำ retro จบ
TASK_ID_RE    = r"\d+"              # รูปแบบ task id; slug/ทศนิยม -> r"[\w.\-]+"
TASK_ID_NUMERIC = True              # id เป็นเลขล้วน? (กำหนดการ cast + sort)
# บรรทัด task ใน tasks.md เช่น "- [ ] 3." / "- [x] 3." (รองรับ -/* และ x/X)
TASKS_CHECKBOX_RE = r"^[-*] \[[ xX]\] (" + TASK_ID_RE + r")"

# ===== derived (ไม่ผูก workflow) =====
HOME   = os.path.expanduser("~")
SLUG   = re.sub(r"[._/]", "-", os.getcwd())   # project dir slug ของ Claude Code
TXDIR  = os.path.join(HOME, ".claude", "projects", SLUG)
LEDGER = os.path.join(HOME, ".claude", "cost-sessions")

# ===== pricing (USD ต่อ token = per-M / 1e6) — ใช้เฉพาะ "ปันส่วน" total authoritative
# ตาม "สัดส่วน" เท่านั้น; ไม่ใช้เป็น cost จริง (lesson: recompute จาก transcript เพี้ยน
# -40%..+20%). cache write แยก tier 5m (1.25x) / 1h (2x). =====
M = 1_000_000
RATES = {
    "claude-opus-4-8":   dict(inp=5/M,  out=25/M, cr=0.50/M, w5=6.25/M, w1=10/M),
    "claude-sonnet-4-6": dict(inp=3/M,  out=15/M, cr=0.30/M, w5=3.75/M, w1=6/M),
    "claude-haiku-4-5":  dict(inp=1/M,  out=5/M,  cr=0.10/M, w5=1.25/M, w1=2/M),
}
_CACHE_READ_DISCOUNT = 10   # cache-read ถูกกว่า input สด ~10x (ทุก model ข้างบน)


def _base_model(name):
    """ชื่อ model เต็ม (เช่น 'claude-opus-4-8[1m]') -> key ใน RATES; ไม่รู้จัก -> None."""
    for k in RATES:
        if name and name.startswith(k):
            return k
    return None


def session_breakdown(sid):
    """อ่าน transcript ของ session -> per-model token counts (จริง) + est cost ต่อชั้น.
    dedup ด้วย message.id (ไม่ใช่ uuid: msg เดียวกันโผล่หลายบรรทัด -> overcount).
    คืน {models: {base: {inp,out,cr,cw,est}}, est_total, found}. found=False ถ้า
    transcript ไม่มี usage (compacted/หาย) — caller ใช้ total จาก ledger แทน."""
    tp = os.path.join(TXDIR, sid + ".jsonl")
    models, seen, est_total = {}, set(), 0.0
    if os.path.exists(tp):
        for line in open(tp, encoding="utf-8", errors="ignore"):
            try:
                d = json.loads(line)
            except Exception:
                continue
            msg = d.get("message") or {}
            u = msg.get("usage")
            if not u:
                continue
            mid = msg.get("id")
            if mid in seen:
                continue
            seen.add(mid)
            bm = _base_model(msg.get("model"))
            if not bm:
                continue
            cc = u.get("cache_creation") or {}
            w5 = cc.get("ephemeral_5m_input_tokens", 0)
            w1 = cc.get("ephemeral_1h_input_tokens", 0)
            inp = u.get("input_tokens", 0)
            out = u.get("output_tokens", 0)
            cr = u.get("cache_read_input_tokens", 0)
            r = RATES[bm]
            est = inp*r["inp"] + out*r["out"] + cr*r["cr"] + w5*r["w5"] + w1*r["w1"]
            est_total += est
            m = models.setdefault(bm, dict(inp=0, out=0, cr=0, cw=0, est=0.0, turns=0))
            m["inp"] += inp; m["out"] += out; m["cr"] += cr
            m["cw"] += w5 + w1; m["est"] += est; m["turns"] += 1   # 1 dedup'd assistant msg = 1 model turn
    return dict(models=models, est_total=est_total, found=est_total > 0)


def _k(n):
    """token count -> ย่อ (1,749,700 -> '1.75M', 12,800 -> '12.8k', <1000 -> เลขเต็ม)."""
    if n >= 1_000_000:
        return f"{n/1_000_000:.2f}M"
    if n >= 1000:
        return f"{n/1000:.1f}k"
    return str(n)


def render_breakdown(sid, authoritative_total, heading="Breakdown"):
    """list[str] ตาราง markdown: token จริงต่อ model + cost ปันส่วนจาก total
    authoritative (sum ต่อชั้น = total เป๊ะ) + insight cache savings. ไม่มี emoji."""
    b = session_breakdown(sid)
    if not b["found"] or authoritative_total <= 0:
        return [f"### {heading}",
                "",
                "_breakdown ต่อ model ไม่พร้อม (transcript ไม่มี usage / compacted) — "
                f"แสดงแค่ total authoritative: **${authoritative_total:.2f}**_"]
    scale = authoritative_total / b["est_total"]   # ปันส่วน est -> total จริง
    L = [f"### {heading}",
         "",
         "token = จริงจาก transcript; cost = ปันส่วน total authoritative (ledger) "
         "ตามสัดส่วน token x rate (ประมาณ, sum = total เป๊ะ)",
         "",
         "| model | turns | input | output | cache-read | cache-write | cost $ |",
         "|------|-----:|------:|------:|----------:|-----------:|------:|"]
    cr_cost = 0.0
    turns_total = 0
    for bm in sorted(b["models"], key=lambda k: -b["models"][k]["est"]):
        m = b["models"][bm]
        c = m["est"] * scale
        cr_cost += m["cr"] * RATES[bm]["cr"] * scale
        turns_total += m["turns"]
        short = bm.replace("claude-", "")
        L.append(f"| {short} | {m['turns']} | {_k(m['inp'])} | {_k(m['out'])} | "
                 f"{_k(m['cr'])} | {_k(m['cw'])} | {c:.2f} |")
    L += ["", f"- total (authoritative ledger): **${authoritative_total:.2f}**"
          f" | turns (model invocations): {turns_total}"]
    if cr_cost > 0:
        full = cr_cost * _CACHE_READ_DISCOUNT
        L.append(f"- cache-read ปันส่วน ${cr_cost:.2f}; ถ้าไม่มี cache จ่ายเป็น input สด "
                 f"~${full:.2f} (~{_CACHE_READ_DISCOUNT}x) -> ประหยัด ~${full-cr_cost:.2f}")
    return L

_IMPL_RE = re.compile(re.escape(IMPLEMENT_CMD) + r"\s+(" + TASK_ID_RE + r")")

# Real slash-command INVOCATIONS only. Claude Code records a typed command in a user
# message as: <command-name>/spec-implement</command-name><command-args>N</command-args>.
# Match that exact shape so we count actual runs — NOT bare mentions of the command in
# file reads, diffs, skill text, or discussion (those polluted the old full-text scan and
# misattributed planning sessions to tasks).
_INVOKE_RE = re.compile(
    r"<command-name>" + re.escape(IMPLEMENT_CMD) + r"</command-name>\s*"
    r"<command-args>\s*(" + TASK_ID_RE + r")")
_RETRO_INVOKE = "<command-name>" + RETRO_CMD + "</command-name>"


def _cast(x):
    return int(x) if TASK_ID_NUMERIC else x


def session_tasks(sid):
    """session id -> (sorted list of ACTUALLY-INVOKED implement task ids, has_retro).
    Counts only real command invocations (the <command-name> wrapper in a user message),
    not mentions in file reads / diffs / discussion. Handles all-in-one / batched sessions
    (one session, several /spec-implement invocations) -> many ids. [] if none."""
    tp = os.path.join(TXDIR, sid + ".jsonl")
    if not os.path.exists(tp):
        return ([], False)
    ids, retro = set(), False
    for line in open(tp, encoding="utf-8", errors="ignore"):
        try:
            d = json.loads(line)
        except Exception:
            continue
        m = d.get("message") or {}
        if m.get("role") != "user":          # invocations are user messages
            continue
        c = m.get("content")
        if not isinstance(c, str):           # tool_results are lists -> skip mentions
            continue
        for x in _INVOKE_RE.findall(c):
            ids.add(_cast(x))
        if _RETRO_INVOKE in c:
            retro = True
    return (sorted(ids), retro)


def task_of(sid):
    """back-compat: single task id iff session did exactly one implement + retro, else None."""
    ids, retro = session_tasks(sid)
    return ids[0] if (len(ids) == 1 and retro) else None


def detect_feature():
    """env FEATURE; ไม่งั้น auto ถ้ามี spec เดียวใน .ai/specs/ (หลายอัน -> error)."""
    f = os.environ.get("FEATURE", "")
    if f:
        return f
    dirs = [d for d in glob.glob(".ai/specs/*/") if os.path.isdir(d)]
    if len(dirs) == 1:
        return os.path.basename(dirs[0].rstrip("/"))
    sys.exit(f"ระบุ feature ผ่าน env FEATURE= (พบ {len(dirs)} specs)")


def all_task_ids(tasks_path):
    """รายการ task id (เรียง) จาก tasks.md."""
    if not os.path.exists(tasks_path):
        sys.exit(f"ไม่พบ {tasks_path}")
    txt = open(tasks_path, encoding="utf-8").read()
    ids = {_cast(x) for x in re.findall(TASKS_CHECKBOX_RE, txt, re.M)}
    if not ids:
        sys.exit(f"ไม่มี task ใน {tasks_path}")
    return sorted(ids)


def ledger_for(sid):
    """session id (เต็มหรือ prefix) -> (cost, dur_ms, +lines, -lines, full_sid) จาก ledger;
    ไม่พบ -> None. หลาย match (resume double-record) -> เก็บ cost สูงสุด."""
    best = None
    for f in glob.glob(os.path.join(LEDGER, "*.json")):
        try:
            d = json.load(open(f))
        except Exception:
            continue
        s = d.get("session_id", "")
        if not s or sid not in s:
            continue
        c = d.get("cost") or 0
        if best is None or c > best[0]:
            best = (c, d.get("duration_ms") or 0,
                    d.get("lines_added") or 0, d.get("lines_removed") or 0, s)
    return best


def task_costs(exclude_session=""):
    """อ่าน ledger ทั้งหมด -> {task: (cost, dur_ms, +lines, -lines, sid)}; เก็บ max cost/task."""
    out = {}
    for f in glob.glob(os.path.join(LEDGER, "*.json")):
        try:
            d = json.load(open(f))
        except Exception:
            continue
        sid = d.get("session_id", "")
        if exclude_session and exclude_session in sid:
            continue
        cost = d.get("cost") or 0
        if cost <= 0:
            continue
        t = task_of(sid)
        if t is None:
            continue
        if t not in out or cost > out[t][0]:
            out[t] = (cost, d.get("duration_ms") or 0,
                      d.get("lines_added") or 0, d.get("lines_removed") or 0, sid)
    return out


def session_costs(exclude_session=""):
    """list of session records (one per session) covering >=1 implemented task, with a
    retro and cost>0. Cost is per-session (the ledger gives one total/session), so
    all-in-one / batched sessions report as ONE row spanning their task-set — they can't
    be split per task. dedup by session_id (max cost on resume double-record). Each
    record: dict(ids=[...], cost, dur, la, lr, sid). Sorted by task range."""
    by_sid = {}
    for f in glob.glob(os.path.join(LEDGER, "*.json")):
        try:
            d = json.load(open(f))
        except Exception:
            continue
        sid = d.get("session_id", "")
        if not sid or (exclude_session and exclude_session in sid):
            continue
        cost = d.get("cost") or 0
        if cost <= 0:
            continue
        # any session with >=1 REAL implement invocation incurred build cost — count it
        # (retro is no longer a gate: invocation-matching already excludes planning/mention
        # sessions, and #7 lets a session legitimately skip the retro commit).
        ids, _retro = session_tasks(sid)
        if not ids:
            continue
        if sid not in by_sid or cost > by_sid[sid]["cost"]:
            by_sid[sid] = dict(ids=ids, cost=cost, dur=d.get("duration_ms") or 0,
                               la=d.get("lines_added") or 0,
                               lr=d.get("lines_removed") or 0, sid=sid)
    return sorted(by_sid.values(), key=lambda r: (r["ids"][0], r["ids"][-1]))
