#!/usr/bin/env python3
"""Identifier gate for the hierarchical-naming rename (spec task 12, REQ-15.3/15.4/15.5).

The eight retired compound identifiers must not appear as live-code TOKENS anywhere in
src/ or tests/:

    MerchantUser, PlatformUser, AdminRole, AdminPermission,
    PaymentSession, CartItem, CheckoutSession, PspConnection

Matching is word-bounded (\\bToken\\b), so longer identifiers that merely CONTAIN a
token (`PaymentSessionId`, `AddPlatformUserSessionScheme`, `FakePlatformUserRepository`,
`MerchantUserRegistrationSubmitted`, `ToTable("PlatformUsers")`) do not match — member
names and retained contracts were out of the rename's scope by design.

Exception list (REQ-15.4) — shipped WITH the check, each excluded mechanically:
  1. `MerchantUserRegistrationSubmitted` — retained integration-event type (design §10,
     L8). Never word-boundary-matches anyway; named here so nobody "fixes" it.
  2. `MerchantUser:*` configuration section keys (design §5b, L8). STRING LITERALS ARE
     STRIPPED before matching, which covers these and every other frozen wire string
     (the `MerchantUser.RegistrationTicket.v1` DataProtection purpose, OpenAPI
     tag/summary display strings, exception-message prose). Interpolation holes
     (`{...}` inside `$"..."`) are the exception to the exception: their content is
     live code and IS scanned. Renamed TABLE names inside raw-SQL strings are guarded
     by the fresh-DB gate (`assert-fresh-db.sql` + EF has-pending-model-changes),
     not by this check.
  3. Comments citing history — COMMENTS ARE STRIPPED before matching (// and /* */).
  4. L6 file-level aliases (design L6): `using PaymentSession = Payments.Domain.Session;`
     deliberately reconstructs a compound as a file-local alias. Alias names declared in
     a file are allowed as tokens within that same file only.

Exit 0 = clean; exit 1 = violations listed on stdout (file:line: token | code).
"""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

TOKENS = re.compile(
    r"\b(MerchantUser|PlatformUser|AdminRole|AdminPermission"
    r"|PaymentSession|CartItem|CheckoutSession|PspConnection)\b"
)
ALIAS_DECL = re.compile(r"^\s*using\s+(\w+)\s*=")


def emit_hole(source: str, i: int, n: int, out: list[str]) -> int:
    """Emit an interpolation hole's content — it is LIVE CODE, not string text.

    `i` points at the opening `{`. Emits everything up to the matching `}` (brace
    depth tracked) and returns the index just past it. Codex P2 on PR #96 caught
    the original version stripping holes with the rest of the string, which made
    the gate fail open on `$"{RetiredType.X}"`.
    ponytail: a nested string literal inside a hole is emitted as-is (a retired
    token in such prose would false-POSITIVE, the safe direction); switch to a
    real C# lexer if that ever bites.
    """
    depth = 1
    i += 1
    while i < n and depth:
        c = source[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        if depth:
            out.append(c)
        i += 1
    return i


def strip_strings_and_comments(source: str) -> str:
    """Blank out string/char literals and comments, preserving line structure.

    Interpolation holes (`{...}` inside `$"..."` / `$@"..."`) are NOT stripped —
    their content is scanned as live code via emit_hole."""
    out: list[str] = []
    i, n = 0, len(source)
    while i < n:
        ch = source[i]
        nxt = source[i + 1] if i + 1 < n else ""
        if ch == "/" and nxt == "/":  # line comment
            while i < n and source[i] != "\n":
                i += 1
        elif ch == "/" and nxt == "*":  # block comment (keep newlines)
            i += 2
            while i < n and not (source[i] == "*" and i + 1 < n and source[i + 1] == "/"):
                if source[i] == "\n":
                    out.append("\n")
                i += 1
            i += 2
        elif ch == "@" and nxt == '"' or (ch == "$" and nxt == "@") or (ch == "@" and nxt == "$"):
            # verbatim (possibly interpolated) string: "" is the only escape.
            interpolated = ch == "$" or nxt == "$"
            i += 2 if nxt == '"' else 3
            while i < n:
                if source[i] == '"' and i + 1 < n and source[i + 1] == '"':
                    i += 2
                    continue
                if source[i] == '"':
                    i += 1
                    break
                if interpolated and source[i] == "{":
                    if i + 1 < n and source[i + 1] == "{":  # {{ escape — literal text
                        i += 2
                        continue
                    i = emit_hole(source, i, n, out)
                    continue
                if source[i] == "\n":
                    out.append("\n")
                i += 1
        elif ch == '"' or (ch == "$" and nxt == '"'):
            # regular / interpolated string: backslash escapes
            interpolated = ch == "$"
            i += 1 if ch == '"' else 2
            while i < n:
                if source[i] == "\\":
                    i += 2
                    continue
                if source[i] == '"':
                    i += 1
                    break
                if interpolated and source[i] == "{":
                    if i + 1 < n and source[i + 1] == "{":  # {{ escape — literal text
                        i += 2
                        continue
                    i = emit_hole(source, i, n, out)
                    continue
                i += 1
        elif ch == "'":
            i += 1
            while i < n:
                if source[i] == "\\":
                    i += 2
                    continue
                if source[i] == "'":
                    i += 1
                    break
                i += 1
        else:
            out.append(ch)
            i += 1
    return "".join(out)


def tracked_cs_files() -> list[Path]:
    res = subprocess.run(
        ["git", "ls-files", "src/**/*.cs", "tests/**/*.cs", "src/*.cs", "tests/*.cs"],
        capture_output=True, text=True, check=True,
    )
    return [Path(p) for p in res.stdout.splitlines() if p]


def main() -> int:
    violations: list[str] = []
    for path in tracked_cs_files():
        text = path.read_text(encoding="utf-8-sig")
        stripped = strip_strings_and_comments(text)
        aliases = {m.group(1) for m in map(ALIAS_DECL.match, stripped.splitlines()) if m}
        for lineno, line in enumerate(stripped.splitlines(), start=1):
            for m in TOKENS.finditer(line):
                if m.group(1) in aliases:
                    continue  # L6 file-local alias (exception 4)
                violations.append(f"{path}:{lineno}: {m.group(1)} | {line.strip()}")
    if violations:
        print("rename-identifier gate: FAILED — retired identifiers in live code:")
        print("\n".join(violations))
        return 1
    print("rename-identifier gate: OK — no retired identifier appears as a live-code token in src/ or tests/.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
