#!/usr/bin/env bash
# install.sh — wire the Tier-1 enforcement floor into THIS clone, then report.
#
# ทำไม "คน" ต้องรัน script นี้ (agent บน CLI รันเองไม่ได้):
#   คำสั่ง `git config core.hooksPath ...` มี token `core.hooksPath` ซึ่ง
#   .ai/bin/check-bypass.sh (และ Claude hook-bypass-guard.sh) ตั้งใจ block — เพราะการ set
#   core.hooksPath เป็นวิธีปิด/หลบ git hooks ทั้งชุด (รวม secret-guard). ดังนั้น "agent" ที่อยู่
#   หลัง guard จะพิมพ์คำสั่งนี้บน command line เองไม่ได้.
#   ทางออก: "คน" รัน ./.ai/bin/install.sh นี้ครั้งเดียวต่อ clone — ตัว script (ไม่ใช่ agent บน
#   CLI) เป็นผู้รัน git config ให้ จึงไม่ชน guard ของ command line. ปลอดภัยเพราะ idempotent +
#   no-op นอก git repo.
#   หมายเหตุ: framework เป็น stack-agnostic — ไม่มี package.json/npm install ให้แขวน `prepare`
#   hook อีกต่อไป ดังนั้น script นี้คือทางเดียวในการ wire Tier-1 floor เข้า clone.

set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || true)"

if [ -z "$REPO_ROOT" ]; then
  echo "install.sh: not inside a git work tree — nothing to wire (no-op)." >&2
  exit 0
fi

cd "$REPO_ROOT"

# 1. Point this clone's git hooks at the committed, shared .githooks/ directory.
#    (pre-commit -> secret scan + Evidence check, pre-push -> branch/force-push guard.)
#    Idempotent: writing the same value twice is harmless.
HOOKS_KEY="core.hooksPath"
git config "$HOOKS_KEY" .githooks

# 2. Mark the hook scripts and the check engine executable.
chmod +x .githooks/* .ai/bin/* 2>/dev/null || true

echo "=== .ai/bin floor wired into this clone ==="
echo "  $HOOKS_KEY -> $(git config --get "$HOOKS_KEY")"
echo "  .githooks/* and .ai/bin/* marked executable"
echo
echo "Verify:"
echo "  git config --get $HOOKS_KEY        # -> .githooks"
echo "  ls -l .githooks .ai/bin            # -> scripts are executable (rwx)"
echo
echo "=== OPTIONAL: Codex in-session hooks (per-machine, interactive — issue #26) ==="
echo "  The Tier-1 floor above already protects every agent at commit/push/PR."
echo "  Codex's IN-SESSION hooks (.codex/config.toml [hooks]) are an extra in-loop layer."
echo "  Codex discovers them but they fire ONLY in an INTERACTIVE codex session AFTER you"
echo "  review + trust them once (they did NOT fire in headless 'codex exec' on 0.135/0.139,"
echo "  even with --dangerously-bypass-hook-trust). To enable for interactive use:"
echo "    1) open 'codex' in this repo and trust the project when prompted"
echo "    2) run the '/hooks' command, then review + trust: guard.sh, task-gate.sh,"
echo "       spec-edit-guard.sh (trust is recorded per-machine against each hook's hash)"
echo "  This cannot be committed (trust lives in \$CODEX_HOME), like core.hooksPath setup."
