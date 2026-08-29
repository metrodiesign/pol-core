#!/usr/bin/env bash
# gate-task.sh — command resolution + Evidence + safe cache + build/test, one verdict.
# Task 3 (sdd-operating-layer-parity): replaces the awk Evidence parser and the
# zero-test exception. All task/Evidence semantics live in scripts/spec_contract.py;
# this script owns ONLY: GateSelection call, command resolution, cache I/O,
# process execution and exit mapping (design §Command resolution / §Safe cache).
#
# Interface:
#   $1 = repo-relative tasks.md path        $4 = changed-ranges JSON file
#   $2 = before snapshot file ("-" = absent)$5 = source enum (claude-edit|codex-edit|opencode|pre-commit|ci)
#   $3 = after snapshot file
# Exit: 0 green (Evidence+build+test) · 2 block with deterministic stderr diagnostics.
set -uo pipefail

REPO="$(cd "$(dirname "$0")/../.." && pwd)"
if ROOT_CANDIDATE="$(git -C "$REPO" rev-parse --show-toplevel 2>/dev/null)"; then
  REPO="$ROOT_CANDIDATE"
fi
# Test seam: fixtures redirect the repo root to a sandbox (production leaves this unset).
if [[ -n "${SDD_GATE_REPO:-}" ]]; then
  REPO="${SDD_GATE_REPO}"
fi
# Engine binaries bind to the installation floor, not the target repo (so the
# SDD_GATE_REPO sandbox seam keeps working); REPO is only where work executes.
INSTALL="$(cd "$(dirname "$0")/../.." && pwd)"
ENGINE="$INSTALL/scripts/spec_contract.py"
CHECK_EVIDENCE="$INSTALL/.ai/bin/check-evidence.sh"

if [[ $# -ne 5 ]]; then
  printf 'Usage: %s <path> <before-file|-> <after-file> <ranges-json> <source>\n' "$0" >&2
  exit 2
fi
PATH_ARG="$1"; BEFORE="$2"; AFTER="$3"; RANGES="$4"; SOURCE="$5"

fail() { printf '%s\n' "$1" >&2; exit 2; }

# --- Evidence first: always validated, never cached -------------------------
ENVELOPE="$(bash "$CHECK_EVIDENCE" "$PATH_ARG" "$BEFORE" "$AFTER" "$RANGES" "$SOURCE")"
EV_RC=$?
printf '%s\n' "$ENVELOPE" >&2
[[ "$EV_RC" -eq 0 ]] || fail "Task gate: Evidence/selection red — mark [x] ถูก block"

# --- Command resolution ------------------------------------------------------
TYPECHECK_CMD="${SDD_TYPECHECK_CMD:-}"
TEST_CMD="${SDD_TEST_CMD:-}"
if [[ -z "$TYPECHECK_CMD" && -f "$REPO/pol-core.slnx" ]]; then
  TYPECHECK_CMD='dotnet build pol-core.slnx -warnaserror'
fi
if [[ -z "$TEST_CMD" && -f "$REPO/pol-core.slnx" ]]; then
  TEST_CMD='dotnet test pol-core.slnx --no-build --filter "Category!=Integration"'
fi
if [[ -z "$TYPECHECK_CMD" || -z "$TEST_CMD" ]]; then
  fail 'COMMAND_UNRESOLVED: build/test command resolve ไม่ได้ — block'
fi

NO_CACHE="${SDD_GATE_NO_CACHE:-}"
CACHE_DIR=""
cache_key=""

if [[ "$NO_CACHE" != "1" ]]; then
  GIT_DIR="$(git -C "$REPO" rev-parse --absolute-git-dir 2>/dev/null)" || GIT_DIR=""
  if [[ -z "$GIT_DIR" ]]; then NO_CACHE=1; fi
fi

if [[ "$NO_CACHE" != "1" ]]; then
  CACHE_DIR="$GIT_DIR/sdd-gate-cache/v1"
  KEY_JSON="$(python3 - "$REPO" "$CACHE_DIR" "$TYPECHECK_CMD" "$TEST_CMD" <<'PY'
import hashlib, json, os, stat as _stat, subprocess, sys
repo, cache_dir, build, test = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]

def inventory():
    tracked = subprocess.run(["git", "-C", repo, "ls-files", "-s", "-z"], capture_output=True).stdout.split(b"\0")
    others = subprocess.run(["git", "-C", repo, "ls-files", "--others", "--exclude-standard", "-z"], capture_output=True).stdout.split(b"\0")
    entries = []
    for raw in tracked:
        if not raw:
            continue
        meta, rel = raw.split(b"\t", 1)
        mode = meta.split()[0].decode()
        path = os.path.join(repo, os.fsdecode(rel))
        try:
            data = open(path, "rb").read()
        except OSError:
            return None
        entries.append((os.fsdecode(rel), mode, hashlib.sha256(data).hexdigest()))
    for raw in others:
        if not raw:
            continue
        rel = os.fsdecode(raw)
        path = os.path.join(repo, rel)
        try:
            st = os.lstat(path)
            data = open(path, "rb").read()
        except OSError:
            continue
        if _stat.S_ISLNK(st.st_mode):
            return None          # submodule/symlink inventory undigestible -> disable cache
        if rel.startswith((".ai/specs/", ".git/")) or "/.ai/specs/" in "/" + rel or cache_dir in rel:
            continue
        entries.append((rel, format(st.st_mode & 0o777), hashlib.sha256(data).hexdigest()))
    entries.sort(key=lambda e: e[0])
    return entries

def toolchain(cmd):
    binary = cmd.split()[0]
    resolved = subprocess.run(["bash", "-lc", f"command -v {binary}"], capture_output=True, text=True).stdout.strip()
    version = subprocess.run(["bash", "-lc", f"{binary} --version"], capture_output=True, text=True).stdout.strip()
    return [resolved, hashlib.sha256(version.encode()).hexdigest()]

entries = inventory()
if entries is None:
    print(json.dumps({"disabled": True}))
    raise SystemExit(0)
filtered = [e for e in entries if not (e[0] == ".ai/specs" or e[0].startswith(".ai/specs/") or e[0].startswith(".git/"))]
material = json.dumps({"entries": filtered, "build": build, "test": test,
                       "toolchain": toolchain(build) + toolchain(test)}, sort_keys=True).encode()
print(json.dumps({"key": hashlib.sha256(material).hexdigest()}))
PY
)" || KEY_JSON=""
  cache_key="$(printf '%s' "$KEY_JSON" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("key",""))' 2>/dev/null || true)"
fi

if [[ "$NO_CACHE" != "1" && -n "$cache_key" && -f "$CACHE_DIR/$cache_key" ]]; then
  exit 0   # reuse of an observed green build/test pair only; Evidence was re-validated above
fi

# --- Execute (real exit status; no zero-tests special pass) ------------------
GATE_SHELL="${SDD_GATE_SHELL:-bash -lc}"   # fixture seam: production default is bash -lc
OUT_B="$(mktemp)" || fail 'engine-fail: temp สร้างไม่ได้'
if ! (cd "$REPO" && $GATE_SHELL "$TYPECHECK_CMD" >"$OUT_B" 2>&1); then
  { echo 'Task gate: typecheck/build ไม่ผ่าน — ห้าม mark [x] จนกว่าเขียว'; head -c 16384 "$OUT_B" | tail -40; } >&2
  rm -f "$OUT_B"; exit 2
fi
if ! (cd "$REPO" && $GATE_SHELL "$TEST_CMD" >>"$OUT_B" 2>&1); then
  { echo 'Task gate: test ไม่ผ่าน — ห้าม mark [x] จนกว่าเขียว'; head -c 16384 "$OUT_B" | tail -40; } >&2
  rm -f "$OUT_B"; exit 2
fi
rm -f "$OUT_B"

# --- Persist green cache (atomic; prune best-effort) -------------------------
if [[ "$NO_CACHE" != "1" && -n "$cache_key" ]]; then
  TMP_KEY="$CACHE_DIR/.tmp-$cache_key.$$"
  python3 - "$CACHE_DIR" "$cache_key" <<'PY' >/dev/null 2>&1 || true
import json, os, sys
cache_dir, key = sys.argv[1], sys.argv[2]
try:
    os.makedirs(cache_dir, exist_ok=True)
    tmp = os.path.join(cache_dir, f".tmp-{key}")
    record = {"schema_version": 1, "key": key, "result": "green"}
    with open(tmp, "w", encoding="utf-8") as fh:
        json.dump(record, fh, sort_keys=True)
    os.replace(tmp, os.path.join(cache_dir, key))
    keys = sorted(
        (os.path.getmtime(os.path.join(cache_dir, name)), name)
        for name in os.listdir(cache_dir)
        if not name.startswith(".tmp-")
    )
    for _, stale in keys[:-8]:
        os.unlink(os.path.join(cache_dir, stale))
except Exception as prune_error:
    print(f"sdd-gate-cache: prune failed ({prune_error})", file=sys.stderr)
PY
fi

exit 0
