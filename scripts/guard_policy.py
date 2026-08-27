#!/usr/bin/env python3
"""Guard policy verdicts on top of the single span normalizer (Task 4).

Two policies, verdict-compatible with the legacy bash corpora:
  destructive  — rm recursive+force / git reset --hard / git clean -f /
                 find -delete / whole-tree restore/checkout / SQL DROP,
                 TRUNCATE, dropdb, DELETE-without-WHERE / force push family /
                 commit+push on protected branches
  bypass       — floor tamper (.githooks|.ai/bin engines) chmod|chown|rm|
                 truncate|tee|mv target; cp|ln|install destination;
                 >redirect into guards/.git/config; core.hooksPath writes;
                 --no-verify / -n; SECRET_GUARD_SKIP

Detection-only. Unknown token shapes fail CLOSED via engine diagnostics —
never a flat-regex fallback (REQ-9.2/9.3).
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import guard_contract as gc


class Verdict:
    def __init__(self, blocked: bool = False, reason: str = "",
                 engine_fail: bool = False):
        self.blocked = blocked
        self.reason = reason
        self.engine_fail = engine_fail


def _flat_tokens(span: gc.Span) -> list[str]:
    return [tok.value for tok in span.tokens]


def _walk(spans: list[gc.Span]):
    for span in spans:
        yield span
        yield from _walk(span.children)


# --- destructive policy ------------------------------------------------------

def _flags_of(vals: list[str]) -> list[str]:
    out = []
    for v in vals:
        if v.startswith("-") and len(v) > 1 and not v.startswith("--"):
            for ch in v[1:]:
                out.append(f"-{ch}")
        elif v.startswith("--"):
            out.append(v)
    return out


def _check_rm(spans: list[gc.Span]) -> str | None:
    for span in _walk(spans):
        if span.executable != "rm":
            continue
        flags = set(_flags_of(_flat_tokens(span)[1:]))
        has_r = bool(flags & {"-r", "-R", "--recursive"})
        has_f = bool(flags & {"-f", "--force"})
        if has_r and has_f:
            return ("rm แบบ recursive+force — ยืนยันเป้าหมายกับ user ก่อน "
                    "(Destructive Ops rules)")
    return None


def _check_simple_destructive(spans: list[gc.Span]) -> str | None:
    for span in _walk(spans):
        vals = _flat_tokens(span)
        if len(vals) >= 2 and vals[0] == "git" and vals[1] == "reset" \
                and "--hard" in vals[2:]:
            return ("git reset --hard — ยืนยันเป้าหมายก่อน "
                    "(Destructive Ops rules)")
        if span.executable == "git" and len(vals) > 1 and vals[1] == "clean" \
                and any(set(f.replace("-", "")) & {"f"} for f in _flags_of(vals)):
            return "git clean -f — ยืนยันเป้าหมายก่อน (Destructive Ops rules)"
        if span.executable == "find" and any(v == "-delete" for v in vals):
            return "find -delete — ยืนยันเป้าหมายก่อน (Destructive Ops rules)"
    return None


_FORCE_LONG = re.compile(r"^--force(-with-lease)?$|^--mirror$")


def _push_force_reason(vals: list[str]) -> str | None:
    body = vals[2:] if vals[:2] == ["git", "push"] else vals
    force_flag = False
    for v in body:
        if v.startswith("+") and len(v) > 1:
            return ("force push (+refspec rewrite remote history; "
                    "Workflow rules: ห้าม force push)")
        if _FORCE_LONG.match(v):
            if v == "--mirror":
                return ("git push --mirror — overwrite ทุก ref ปลายทาง "
                        "(Workflow rules: ห้าม force push)")
            force_flag = True
        if v.startswith("-") and not v.startswith("--") and \
                set(v[1:]) & {"f"}:
            force_flag = True
    if "--all" in body and force_flag:
        return ("git push --all --force — force-overwrite ทุก branch "
                "(Workflow rules: ห้าม force push)")
    if force_flag:
        return "force push (Workflow rules: ห้าม force push)"
    return None


def _protected_ref_target(vals: list[str]) -> bool:
    for v in vals:
        for piece in re.split(r"[:/\s]+", v):
            if piece.lstrip("+") in ("main", "develop"):
                return True
    return False


def check_destructive(command: str, current_branch: str | None = None) -> Verdict:
    spans, diagnostics = gc.normalize(command)
    if diagnostics:
        return Verdict(engine_fail=True)

    reasons: list[str] = []

    def add(reason: str | None) -> None:
        if reason and reason not in reasons:
            reasons.append(reason)

    add(_check_rm(spans))
    add(_check_simple_destructive(spans))

    # SQL families — judge per command span so separators scope the clause
    sql_clients = {"sqlcmd", "psql", "mysql", "sqlite3", "sqlplus", "dropdb"}
    for span in _walk(spans):
        vals = [v.lower() for v in _flat_tokens(span)]
        joined = " ".join(vals)
        is_sql_context = span.executable in sql_clients or (
            vals and vals[0].lower() in ("drop", "truncate")) or \
            bool(re.search(r"\bdelete\s+from\b", joined))
        if not is_sql_context:
            continue
        if re.search(r"\bdrop\s+table\b|\bdrop\s+database\b", joined):
            add("SQL DROP TABLE/DATABASE — ยืนยันกับ user + ต้องมี backup "
                "(Destructive Ops rules)")
        m_trunc = re.search(r"\btruncate\s+(\x00*)(\S+)", joined)
        if m_trunc and not m_trunc.group(2).startswith("-"):
            add("SQL TRUNCATE — ยืนยันกับ user + ต้องมี backup "
                "(Destructive Ops rules)")
        m_del = re.search(r"\bdelete\s+from\s+(.*)", joined, re.DOTALL)
        tail = (m_del.group(1) if m_del else "").replace("\x00", " ")
        has_where = bool(re.search(r"\bwhere\b|=", tail, re.IGNORECASE))
        if m_del and not has_where:
            add("SQL DELETE FROM ไม่มี WHERE — ลบทั้งตาราง ยืนยันกับ user ก่อน "
                "(Destructive Ops rules)")
        if span.executable == "dropdb":
            add("dropdb — ยืนยันกับ user + ต้องมี backup (Destructive Ops rules)")

    # restore/checkout whole-tree
    for span in _walk(spans):
        vals = _flat_tokens(span)
        if vals[:2] != ["git", "restore"] and vals[:2] != ["git", "checkout"]:
            continue
        rest = vals[2:]
        if vals[1] == "restore" and (
                "--staged" in rest or any(
                    re.fullmatch(r"-[A-Za-z]*S[A-Za-z]*", v) for v in rest)) and \
                not ("--worktree" in rest or any(
                    re.fullmatch(r"-[A-Za-z]*W[A-Za-z]*", v) for v in rest)):
            continue  # unstage-only
        if any(v == "." for v in rest) or (
                "--" in rest and "." in rest[rest.index("--") + 1:]):
            return Verdict(True, "git restore/checkout ทั้ง working tree (.) — "
                           "ทิ้ง uncommitted ทั้งหมด ไม่ผ่าน git history; "
                           "stash/ยืนยันก่อน (Destructive Ops rules)")

    # push family
    push_invocations: list[list[str]] = []
    has_commit = False
    all_branch_delete = True
    saw_push = False
    for span in _walk(spans):
        vals = _flat_tokens(span)
        if vals[:2] == ["git", "commit"]:
            has_commit = True
        if vals[:2] == ["git", "push"]:
            saw_push = True
            push_invocations.append(vals)
            add(_push_force_reason(vals))
            rest = vals[2:]
            # branch-delete span: has --delete/-d, its target operands are all
            # variable refs (never literal refs), and no other option flags.
            # `--push-option VALUE`-style pairs swallow the following token,
            # so a bare `-d` right after such an option is its value, not the
            # delete flag (legacy corpus: push-option -d HEAD -> block).
            swallow_next = False
            effective_flags: list[str] = []
            for v in rest:
                if swallow_next:
                    swallow_next = False
                    continue
                if v == "--push-option":
                    swallow_next = True
                    continue
                if v.startswith("-"):
                    effective_flags.append(v)
            is_delete = "--delete" in effective_flags or "-d" in effective_flags
            if is_delete:
                di = next(idx for idx, v in enumerate(rest)
                          if v in ("--delete", "-d"))
                targets = [v for v in rest[di + 1:] if not v.startswith("-")]
                protected_targets = any(_protected_ref_target([tgt])
                                        for tgt in targets)
                if not protected_targets:
                    continue  # non-protected ref deletion is routine cleanup
                add("git push --delete เข้า main/develop — ต้องผ่าน "
                    "PR (Workflow rules)")
                continue
            all_branch_delete = False
    if saw_push:
        branch_delete_only = all_branch_delete and not has_commit
        if not branch_delete_only:
            if current_branch in ("main", "develop"):
                reasons.append(f"git commit/push บน branch {current_branch} — "
                               "ต้อง branch แยกแล้วผ่าน PR (Workflow rules)")
            else:
                for vals in push_invocations:
                    if _protected_ref_target(vals[2:]) or len(vals) <= 2:
                        reasons.append("git push ตรงเข้า main/develop — ต้องผ่าน "
                                       "PR (Workflow rules)")
                        break

    if reasons:
        return Verdict(True, reasons[0])
    return Verdict(False)


# --- bypass policy -----------------------------------------------------------

GUARD_PATH = re.compile(
    r"(\.githooks(/[\w./-]*)?|\.ai/bin(/check-[\w.-]*\.sh|/gate-task\.sh|/?))"
    r"(\s|$)")
_COPY_VERBS = {"cp", "ln", "install"}
_TAMPER_VERBS = {"chmod", "chown", "rm", "truncate", "tee", "mv"}


def _raw_snippet(command: str, span: gc.Span) -> str:
    """Raw source text of the span — peeled tokens are invisible in token
    values but the bypass policy must still see them (e.g. an inline
    `git -c core.hooksPath=…` global option is itself the violation)."""
    return command[span.raw_start:span.raw_end]


def check_bypass(command: str, current_branch: str | None = None) -> Verdict:
    del current_branch
    spans, diagnostics = gc.normalize(command)
    if diagnostics:
        return Verdict(engine_fail=True)

    def block(r: str) -> Verdict:
        return Verdict(True, r)

    # inline `-c core.hooksPath=...` (any case): the peel step removes global
    # options from effective_tokens, so inspect raw text of git spans too.
    if re.search(r"-c\s+core\.hookspath\s*=", command, re.IGNORECASE):
        return block("set core.hooksPath (inline -c / =value) ปิด git hooks "
                     "floor — ห้ามใช้")
    for span in _walk(spans):
        vals = _flat_tokens(span)
        verb_l = (span.executable or "").lower()
        if not vals:
            continue
        if verb_l in _TAMPER_VERBS and any(
                bool(GUARD_PATH.search(v)) for v in vals[1:]):
            return block("disable/move/overwrite guard or floor (.githooks | "
                         ".ai/bin/check-*.sh | gate-task.sh) — ห้ามปิด ย้าย "
                         "หรือทับ enforcement floor")
        if verb_l in _COPY_VERBS:
            seen_operand = False
            for v in vals[1:]:
                if GUARD_PATH.search(v):
                    if seen_operand:
                        return block("overwrite into guard or floor (.githooks | "
                                     ".ai/bin/check-*.sh | gate-task.sh) — "
                                     "ห้ามทับ enforcement floor")
                    break
                seen_operand = True
        for i, v in enumerate(vals[:-1]):
            target = vals[i + 1]
            if v in (">", ">>", "&>") and (GUARD_PATH.search(target) or
                                           ".git/config" in target):
                return block("redirect/overwrite into guard, floor, or "
                             ".git/config — ห้ามปิดหรือทับ enforcement floor")
        lowered = [v.lower() for v in vals]
        if any("core.hookspath=" in v for v in lowered):
            return block("set core.hooksPath (inline -c / =value) ปิด git hooks "
                         "floor — ห้ามใช้")
        if "config" in lowered and "core.hookspath" in lowered:
            write_flags = {"--unset", "--unset-all", "--replace-all", "--add"}
            key_positions = [i for i, v in enumerate(lowered)
                             if v == "core.hookspath"]
            if any(v in write_flags for v in lowered):
                return block("git config --unset/--replace-all/--add "
                             "core.hooksPath แก้ hooks floor — ห้ามใช้")
            if any(after for kp in key_positions
                   for after in [[v for v in lowered[kp + 1:]
                                  if not v.startswith("-")]]):
                return block("git config core.hooksPath <value> เขียนทับ hooks "
                             "floor — ห้ามใช้ (read-only query ผ่านได้)")
        if "--no-verify" in vals:
            return block("--no-verify ข้าม secret-guard pre-commit hook — commit "
                         "ตามปกติเพื่อให้ scan ทำงาน")
        if any(k.startswith("SECRET_GUARD_SKIP=") for k in vals):
            return block("SECRET_GUARD_SKIP ข้าม secret scan — ถ้าจำเป็นจริงให้ "
                         "user รันเองนอก session")
        if vals[:2] == ["git", "commit"] and any(
                v.startswith("-") and not v.startswith("--") and
                set(v[1:]) & {"n"} for v in vals[2:]):
            return block("git commit -n (--no-verify) ข้าม secret-guard — "
                         "commit ตามปกติ")
    return Verdict(False)


POLICIES = {"destructive": check_destructive, "bypass": check_bypass}


def main(argv: list[str]) -> int:
    import argparse
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("policy", choices=sorted(POLICIES))
    parser.add_argument("--branch", default=None)
    parser.add_argument("command", nargs="+")
    args = parser.parse_args(argv)
    try:
        verdict = POLICIES[args.policy](" ".join(args.command), args.branch)
    except gc.LexError as err:
        print(f"ENGINE_INTERNAL: {err.code}", file=sys.stderr)
        return 2
    if verdict.engine_fail:
        print("ENGINE_INTERNAL: malformed guard input (unclosed quote/substitution)",
              file=sys.stderr)
        return 2
    if verdict.blocked:
        print(f"Blocked: {verdict.reason}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
