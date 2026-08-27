#!/usr/bin/env bash
# cross-harness-conformance.test.sh — Task 8 fixture (REQ-6.1–6.4).
# One normalized fixture set must yield the same VERDICT CLASS through every
# harness surface, honoring each runtime's documented capability:
#   F0 floor-engine parity  : identical bytes -> identical envelope verdict for
#                             every source label (claude-edit|codex-edit|
#                             opencode|pre-commit|ci)
#   F1/F2 Claude adapter    : snapshot->no-op = allow(exit0); red flip = block(2)
#   F3 Codex adapter        : fail-closed GATE_SNAPSHOT_MISSING on ANY tasks.md
#                             write — never guesses, never exits 0
#   F4/F5 OpenCode plugin   : driver-injected events behave like Claude; green
#                             flip reaches allow through shimmed commands
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
pass=0 fail=0
ok() { pass=$((pass+1)); }
bad() { fail=$((fail+1)); echo "FAIL: $1"; }

SANDBOX="$(mktemp -d "${TMPDIR:-/tmp}/conformance.XXXXXX")"
trap 'rm -rf "$SANDBOX"' EXIT
SPEC_DIR="$SANDBOX/.ai/specs/demo"
mkdir -p "$SPEC_DIR"
cp -R "$ROOT/scripts" "$SANDBOX/scripts"
mkdir -p "$SANDBOX/.ai/bin"
cp "$ROOT/.ai/bin/check-evidence.sh" "$ROOT/.ai/bin/gate-task.sh" \
   "$ROOT/.ai/bin/check-destructive.sh" "$SANDBOX/.ai/bin/" 2>/dev/null || true
: >"$SANDBOX/pol-core.slnx"
# gate seam: every delegated execution resolves REPO to the sandbox, and build/
# test commands are shims -> fixtures never touch real dotnet or the corpus.
export SDD_GATE_REPO="$SANDBOX" SDD_TYPECHECK_CMD=true SDD_TEST_CMD=true

BEFORE_FILE="$SPEC_DIR/before.md"; AFTER_FILE="$SPEC_DIR/tasks.md"

red_body='> Status: approved 2026-08-01

- [ ] T1. work without evidence.
'
red_after_body='> Status: approved 2026-08-01

- [x] T1. DONE. work without evidence.
'
green_body='> Status: approved 2026-08-01

- [x] T1. work with proof.

     Evidence:
     - test: `demo` -> observed ok

     viewports: n/a - tooling only
     deviations: none
'

# ---------------------------------------------------------------------------
# helpers
verdict_of() { # before after source -> prints envelope verdict via shared engine
  local bf="$1" af="$2" src="$3" work rfile
  work="$(mktemp -d)"; rfile="$work/r.json"
  python3 "$ROOT/scripts/spec_contract.py" diff-ranges \
    --before-file "$bf" --after-file "$af" >"$rfile" || return 9
  bash "$ROOT/.ai/bin/check-evidence.sh" ".ai/specs/demo/tasks.md" \
    "$bf" "$af" "$rfile" "$src" 2>/dev/null |
    python3 -c 'import json,sys; print(json.load(sys.stdin)["verdict"])'
  rm -rf "$work"
}

printf '%s' "$red_body" >"$BEFORE_FILE"
# ---------------------------------------------------------------------- F0 --
# no-flip: every transport label -> allow
for src in claude-edit codex-edit opencode pre-commit ci; do
  v="$(verdict_of "$BEFORE_FILE" "$BEFORE_FILE" "$src")"
  [ "$v" = "allow" ] && ok || bad "F0 allow parity broke for $src ($v)"
done
AFTER_RED="$SPEC_DIR/after-red.md"
printf '%s' "$red_after_body" >"$AFTER_RED"
for src in claude-edit codex-edit opencode pre-commit ci; do
  v="$(verdict_of "$BEFORE_FILE" "$AFTER_RED" "$src")"
  [ "$v" = "policy-fail" ] && ok || bad "F0 policy-fail parity broke for $src ($v)"
done

# ------------------------------------------------------- F1/F2 Claude ------
CLAUDE_SNAP="$ROOT/.claude/hooks/task-snapshot.sh"
CLAUDE_GATE="$ROOT/.claude/hooks/task-gate.sh"
capture_claude() {
  printf '{"tool_input":{"file_path":"%s"}}\n' "$AFTER_FILE" | bash "$CLAUDE_SNAP"
}
cleanup_claude_key() {
  python3 - "$AFTER_FILE" <<'PY'
import hashlib, json, os, sys, pathlib
key = hashlib.sha256(os.path.abspath(sys.argv[1]).encode()).hexdigest()
store = pathlib.Path(os.environ.get("HOME","/")) # placeholder replaced below
PY
}

# F1: byte-identical rewrite after snapshot => exit 0 quiet allow
rm -f "$AFTER_FILE"; printf '%s' "$red_body" >"$AFTER_FILE"
capture_claude
OUT="$(printf '{"tool_input":{"file_path":"%s"}}\n' "$AFTER_FILE" | bash "$CLAUDE_GATE" 2>&1)"
RC=$?
if [ "$RC" -eq 0 ]; then ok; else bad "F1 claude no-op expected allow, got rc=$RC ($OUT)"; fi

# F2: capture pre-flip bytes, then flip without Evidence => exit 2 block
capture_claude
printf '%s' "$red_after_body" >"$AFTER_FILE"
OUT="$(printf '{"tool_input":{"file_path":"%s"}}\n' "$AFTER_FILE" | bash "$CLAUDE_GATE" 2>&1)"
RC=$?
if [ "$RC" -eq 2 ] && grep -q 'Evidence/selection red' <<<"$OUT"; then ok
else bad "F2 claude red flip expected block(2), got rc=$RC ($OUT)"; fi

# ------------------------------------------------------------ F3 Codex -----
CODEX_GATE="$ROOT/.codex/hooks/task-gate.sh"
OUT="$(printf '{"tool_input":{"file_path":"%s"}}\n' "$AFTER_FILE" | bash "$CODEX_GATE" 2>&1)"
RC=$?
if [ "$RC" -eq 2 ] && grep -q 'GATE_SNAPSHOT_MISSING' <<<"$OUT"; then ok
else bad "F3 codex expected fail-closed(2), got rc=$RC ($OUT)"; fi
# even a fully-green candidate content must NOT turn codex hook green:
printf '%s' "$green_body" >"$AFTER_FILE"
OUT="$(printf '{"tool_input":{"file_path":"%s"}}\n' "$AFTER_FILE" | bash "$CODEX_GATE" 2>&1)"
[ $? -eq 2 ] && ok || bad "F3b codex turned green by content (must stay fail-closed)"
# mutation guard: the adapter must not carry its own verdict heuristics
if grep -Eq 'checkbox|\[x\]|validate_evidence|parse_task_blocks' "$CODEX_GATE"; then
  bad "F3c codex adapter grew private verdict logic (raw-selection contract)"
else ok; fi

# --------------------------------------------------------- F4/F5 OpenCode --
NODE_DRIVER="$SANDBOX/driver.mjs"
cat >"$NODE_DRIVER" <<'EOF'
import { spawnSync } from "node:child_process";
import { copyFileSync, writeFileSync } from "node:fs";
const pluginUrl = new URL(process.argv[2], `file://${process.cwd()}/`).href;
const target = process.argv[3];
const mode = process.argv[4]; // missing | noop | redflip | greenflip
const $ = (strings, ...vals) => {
  let cmd = "";
  strings.forEach((s, i) => { cmd += s + (vals[i] !== undefined ? String(vals[i]) : ""); });
  const p = spawnSync("bash", ["-c", cmd], { encoding: "buffer" });
  return { exitCode: p.status ?? 1, stdout: p.stdout ?? Buffer.alloc(0),
           stderr: p.stderr ?? Buffer.alloc(0), nothrow() { return this; },
           quiet() { return this; } };
};
process.chdir(process.argv[5]);
let hooks;
try { hooks = await import(pluginUrl).then(m => m.TaskGate({ $ })); }
catch (e) { console.log("PLUGIN_LOAD_FAIL:" + e); process.exit(3); }
const feed = async () => {
  if (mode === "missing") {                       // edited() with no capture
    await hooks["file.edited"]({ file: target }, {});
  } else {
    await hooks["tool.execute.before"]({}, { args: { file_path: target } });
    if (mode === "redflip")
      copyFileSync(target + "-variant", target);
    else if (mode === "greenflip")
      writeFileSync(target, process.env.GREEN_BODY);
    await hooks["file.edited"]({ file: target }, {});
  }
};
feed().then(() => console.log("ALLOW"))
      .catch(e => { console.log("BLOCK:" + (e.message ?? e)); process.exit(2); });
EOF
GREEN_BODY="$green_body" node -e '' 2>/dev/null || true
cp "$AFTER_RED" "$AFTER_FILE-variant" 2>/dev/null

run_plugin() { # mode expected(green) etc.
  GREEN_BODY="$green_body" SDD_TYPECHECK_CMD="${SHIM_TRUE}" \
  SDD_TEST_CMD="${SHIM_TRUE}" \
    node "$NODE_DRIVER" "$ROOT/.opencode/plugins/task-gate.js" \
         "$AFTER_FILE" "$1" "$SANDBOX" 2>&1
}
SHIM_TRUE="true"
SHIM_CMD_FILE="$SANDBOX/shimcmd"; printf '#!/usr/bin/env bash\nexit 0\n' >"$SHIM_CMD_FILE"
chmod +x "$SHIM_CMD_FILE"

v="$(run_plugin missing)"; RC=$?
v="${v//$'\n'/ }"
case "$v" in *BLOCK:*GATE_SNAPSHOT_MISSING*) ok ;; *) bad "F4a opencode snapshot-missing got rc=$RC: $v" ;; esac
printf '%s' "$red_body" >"$AFTER_FILE"   # pre-flip state on disk before capture
v="$(run_plugin redflip)"; RC=$?
v="${v//$'\n'/ }"
case "$v" in *BLOCK:*Evidence/selection\ red*|*BLOCK:*Task\ gate*) ok ;; *) bad "F4b opencode red flip expected block, got rc=$RC: $v" ;; esac

# green path needs shim commands INSIDE plugin's shell env -> wrapper sets them;
# sandbox ships a fake slnx so command resolution succeeds and 'true' runs green
ln -sf "$ROOT/.opencode/plugins" "$SANDBOX/.opencode-plugins" 2>/dev/null || true
: >"$SANDBOX/pol-core.slnx"
v="$(SDD_TYPECHECK_CMD=true SDD_TEST_CMD=true run_plugin greenflip)"; RC=$?
case "$v" in *ALLOW*) [ "$RC" -eq 0 ] && ok || bad "F5 opencode green rc=$RC: $v" ;;
  *) bad "F5 opencode green expected ALLOW, got rc=$RC: $v" ;; esac

echo "passed=$pass failed=$fail"
[ "$fail" -eq 0 ]
