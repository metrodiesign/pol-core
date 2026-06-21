#!/usr/bin/env bash
#
# backfill-cost.sh — populate the cost ledger for CLOSED task sessions by briefly
# resuming each in an iTerm pane. Resuming renders the statusline, which writes the
# authoritative .cost.total_cost_usd to ~/.claude/cost-sessions/<id>.json. No /cost
# typing, no scraping — just trigger one render, then close.
#
# Usage: scripts/backfill-cost.sh <session-id> [<session-id> ...]
#
set -u
REPO="$(cd "$(dirname "$0")/.." && pwd)"

for SID in "$@"; do
  echo "::: resume $SID (render statusline -> ledger)"
  PANE=$(osascript <<AS
tell application "iTerm2"
  tell current session of current window
    set s to (split vertically with default profile command "/bin/zsh -lc 'cd $REPO && claude --resume $SID'")
  end tell
  return id of s
end tell
AS
)
  PANE=$(echo "$PANE" | tr -d '[:space:]')
  # wait until ledger file for this session appears/updates (statusline rendered)
  for i in $(seq 1 20); do
    [ -f "$HOME/.claude/cost-sessions/$SID.json" ] && break
    sleep 1
  done
  sleep 2
  osascript -e "tell application \"iTerm2\"
    repeat with w in windows
      repeat with t in tabs of w
        repeat with se in sessions of t
          if (id of se) is \"$PANE\" then tell se to close
        end repeat
      end repeat
    end repeat
  end tell" 2>/dev/null
  if [ -f "$HOME/.claude/cost-sessions/$SID.json" ]; then
    echo "    OK -> $(cat "$HOME/.claude/cost-sessions/$SID.json")"
  else
    echo "    MISS (ledger ไม่เกิด)"
  fi
done
