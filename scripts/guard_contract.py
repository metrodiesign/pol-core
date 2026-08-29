#!/usr/bin/env python3
"""Quote-aware shell command normalizer (Task 4 / REQ-9.7-9.8).

Detection-only: parses a raw command string into NormalizedCommandSpan trees so
policies judge semantic structure instead of de-quoted haystack strings. Never
executes anything. Fails closed on malformed input (design §Guard normalization).
"""
from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass, field

MAX_DEPTH = 8
MAX_SPANS = 512
WORD_SEPARATORS = ("&&", "||", "|", ";", "&")


@dataclass
class Token:
    value: str
    raw_start: int
    raw_end: int
    quote_context: str  # none | single | double | substitution
    had_escape: bool


@dataclass
class Span:
    raw_start: int
    raw_end: int
    executable: str | None
    tokens: list[Token] = field(default_factory=list)
    children: list["Span"] = field(default_factory=list)
    depth: int = 0
    region_stop: int = 0


class LexError(Exception):
    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


def _skip_quoted(text: str, i: int) -> tuple[str, str, bool, int]:
    """Consume one quoting construct starting at text[i]; returns (value, ctx, esc, next_i)."""
    ch = text[i]
    n = len(text)
    if ch == "'":
        close = text.find("'", i + 1)
        if close == -1:
            raise LexError("GUARD_UNCLOSED_QUOTE")
        return text[i + 1:close], "single", False, close + 1
    if ch == '"':
        buf: list[str] = []
        j = i + 1
        esc = False
        while True:
            if j >= n:
                raise LexError("GUARD_UNCLOSED_QUOTE")
            c = text[j]
            if c == "\\" and j + 1 < n and text[j + 1] in '"\\$`':
                buf.append(text[j + 1])
                esc = True
                j += 2
                continue
            if c == '"':
                return "".join(buf), "double", esc, j + 1
            buf.append(c)
            j += 1
    if ch == "`":
        close = text.find("`", i + 1)
        if close == -1:
            raise LexError("GUARD_UNCLOSED_SUBSTITUTION")
        return text[i + 1:close], "substitution", False, close + 1
    raise LexError("GUARD_UNCLOSED_QUOTE")


def _read_paren_substitution(text: str, open_end: int) -> tuple[str, int]:
    """text[open_end] == '(' e.g. after `$` or `<` or `>`; read until matching ')'."""
    depth = 0
    j = open_end
    n = len(text)
    while j < n:
        c = text[j]
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return text[open_end + 1:j], j + 1
        elif c in "\"'":
            value, _ctx, _esc, nxt = _skip_quoted(text, j)
            del value
            j = nxt
            continue
        j += 1
    raise LexError("GUARD_UNCLOSED_SUBSTITUTION")


def _is_funsub(text: str, i: int) -> bool:
    return text.startswith("${ ", i) or text.startswith("${\t", i) or \
        text.startswith("${\n", i) or text.startswith("${|", i)


def _read_funsub(text: str, i: int) -> tuple[str, int]:
    body_open = i + 2
    j = body_open
    n = len(text)
    while j < n:
        if text[j] == "}":
            return text[body_open:j], j + 1
        j += 1
    raise LexError("GUARD_UNCLOSED_SUBSTITUTION")


def _sep_len(text: str, i: int) -> int:
    two = text[i:i + 2]
    if two in ("&&", "||"):
        return 2
    if text[i] in ("|", ";", "&", "\n"):
        return 1
    return 0


def _strip_leading(values: list[str]) -> tuple[int, str | None]:
    idx = 0
    progressed = True
    while progressed and idx < len(values):
        cur = values[idx]
        if "=" in cur and not cur.startswith("=") and cur.split("=", 1)[0].replace("_", "").isalnum():
            idx += 1
            continue
        if cur == "rtk":
            nxt = values[idx + 1] if idx + 1 < len(values) else ""
            idx += 2 if nxt == "proxy" else 1
            continue
        if cur in ("env", "sudo", "xargs"):
            idx += 1
            continue
        if "/" in cur and cur != "/":
            base = cur.rsplit("/", 1)[-1]
            if base:
                return idx, base
        progressed = False
    values_after = values[idx:]
    exe = values_after[0] if values_after else None
    if exe is not None:
        stripped = exe.rsplit("/", 1)[-1] if "/" in exe else exe
        return idx, stripped
    return idx, None


GIT_GLOBAL_VALUE_FLAGS = {"-c", "-C"}


def peel_git_globals(args: list[Token]) -> list[Token]:
    kept: list[Token] = []
    i = 0
    while i < len(args):
        tok = args[i]
        if tok.value in GIT_GLOBAL_VALUE_FLAGS:
            i += 2
            continue
        if tok.value.startswith("-"):
            i += 1
            continue
        kept.extend(args[i:])
        break
    return kept


WRAPPER_EXES = {"sh", "bash", "eval"}


def parse_command(text: str, start: int, end: int, base: int, depth: int, counter: list[int]) -> Span:
    tokens: list[Token] = []
    children: list[Span] = []
    i = start
    chunks: list[tuple[str, str, bool]] = []
    token_raw_start = None

    def flush(now: int) -> None:
        nonlocal chunks, token_raw_start
        if token_raw_start is None:
            return
        value_parts: list[str] = []
        escaped = False
        context_seen = "none"
        for chunk_value, ctx, esc in chunks:
            value_parts.append(chunk_value)
            escaped = escaped or esc
            if ctx != "none":
                context_seen = ctx
        tokens.append(Token("".join(value_parts), base + token_raw_start, base + now, context_seen, escaped))
        chunks = []
        token_raw_start = None

    stop = end
    while i < end:
        ch = text[i]
        sep = _sep_len(text, i)
        if sep:
            flush(i)
            stop = i
            break
        if ch in (" ", "\t"):
            flush(i)
            i += 1
            continue
        if ch == "#":
            if token_raw_start is None:
                while i < end and text[i] != "\n":
                    i += 1
                continue
            chunks.append((ch, "none", False))
            i += 1
            continue
        if ch == "\\":
            if token_raw_start is None:
                token_raw_start = i
            if i + 1 < end:
                chunks.append((text[i + 1], "none", True))
                i += 2
            else:
                chunks.append((ch, "none", True))
                i += 1
            continue
        if ch in ("'", '"'):
            if token_raw_start is None:
                token_raw_start = i
            value, ctx, esc, nxt = _skip_quoted(text, i)
            chunks.append((value, ctx, esc))
            i = nxt
            continue
        if ch == "`":
            if token_raw_start is None:
                token_raw_start = i
            close = text.find("`", i + 1)
            if close == -1:
                raise LexError("GUARD_UNCLOSED_SUBSTITUTION")
            inner = text[i + 1:close]
            chunks.append((inner, "substitution", False))
            sub_spans, sub_children = _parse_region(text, i + 1, close, base, depth + 1, counter)
            children.extend(sub_spans)
            children.extend(sub_children)
            i = close + 1
            continue
        if text.startswith("$(", i) or text.startswith("<(", i) or text.startswith(">(", i):
            if token_raw_start is None:
                token_raw_start = i
            inner, nxt = _read_paren_substitution(text, i + 1)
            chunks.append((inner, "substitution", False))
            sub_spans, sub_children = _parse_region(
                text, i + 2, i + 2 + len(inner), base, depth + 1, counter
            )
            children.extend(sub_spans)
            children.extend(sub_children)
            i = nxt
            continue
        if _is_funsub(text, i):
            if token_raw_start is None:
                token_raw_start = i
            inner, nxt = _read_funsub(text, i)
            chunks.append((inner.strip(), "substitution", False))
            sub_spans, sub_children = _parse_region(
                text, i + 2, i + 2 + len(inner), base, depth + 1, counter
            )
            children.extend(sub_spans)
            children.extend(sub_children)
            i = nxt
            continue
        if token_raw_start is None:
            token_raw_start = i
        chunks.append((ch, "none", False))
        i += 1
    flush(end)

    counter[0] += 1
    if counter[0] > MAX_SPANS:
        raise LexError("GUARD_SPAN_LIMIT")

    values = [tok.value for tok in tokens]
    exe_idx, executable = _strip_leading(values)

    effective_tokens = tokens
    if executable == "git":
        git_index = next(
            (position for position, value in enumerate(values) if value == "git"),
            None,
        )
        if git_index is not None:
            peeled_args = peel_git_globals(tokens[git_index + 1:])
            effective_tokens = tokens[:git_index + 1] + peeled_args
    elif executable in WRAPPER_EXES:
        arg_values = [tok.value for tok in tokens]
        try:
            dash_c = arg_values.index("-c")
        except ValueError:
            dash_c = -1
        if executable in ("sh", "bash") and dash_c != -1 and dash_c + 1 < len(tokens):
            static_arg = tokens[dash_c + 1]
            wrapped, wrapped_children = _parse_region(
                static_arg.value, 0, len(static_arg.value), static_arg.raw_start, depth + 1, counter
            )
            children.extend(wrapped)
            children.extend(wrapped_children)
        elif executable == "eval" and len(tokens) > 1:
            rest = tokens[1:]
            joined = " ".join(tok.value for tok in rest)
            start_at = rest[0].raw_start
            wrapped, wrapped_children = _parse_region(
                joined, 0, len(joined), start_at, depth + 1, counter
            )
            children.extend(wrapped)
            children.extend(wrapped_children)

    return Span(base + start, base + end, executable, effective_tokens, children, depth, stop)


def _parse_region(text: str, start: int, end: int, base: int, depth: int, counter: list[int]):
    """Parse [start,end) of `region_text` treating offsets relative to `base`."""
    spans: list[Span] = []
    i = start
    while i < end:
        if depth > MAX_DEPTH:
            raise LexError("GUARD_RECURSION_LIMIT")
        sep = _sep_len(text, i)
        if sep:
            i += sep
            continue
        if text[i] in (" ", "\t") or text[i] == "\n":
            i += 1
            continue
        span = parse_command(text, i, end, base, depth, counter)
        spans.append(span)
        consumed = span.region_stop
        if consumed <= i:
            consumed = i + 1
        i = consumed
    return spans, []


def normalize(command: str) -> tuple[list[Span], list[str]]:
    counter = [0]
    try:
        spans, extras = _parse_region(command, 0, len(command), 0, 0, counter)
        return spans, extras
    except LexError as err:
        return [], [err.code]


def _token_json(tok: Token) -> dict:
    return {
        "value": tok.value,
        "rawStart": tok.raw_start,
        "rawEnd": tok.raw_end,
        "quoteContext": tok.quote_context,
        "hadEscape": tok.had_escape,
    }


def _span_json(span: Span, flat: list[dict]) -> dict:
    node = {
        "rawStart": span.raw_start,
        "rawEnd": span.raw_end,
        "executable": span.executable,
        "depth": span.depth,
        "tokens": [_token_json(tok) for tok in span.tokens],
        "argv": [tok.value for tok in span.tokens],
        "children": [],
    }
    flat.append(node)
    node["children"] = [_span_json(child, flat) for child in span.children]
    return node


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("mode", choices=("normalize",))
    parser.add_argument("--format", choices=("json",), default="json")
    parser.add_argument("--stdin", action="store_true")
    parser.add_argument("command", nargs="*")
    args = parser.parse_args(argv)
    if args.stdin:
        command = sys.stdin.read()
    else:
        command = "\n".join(args.command)
    spans, diagnostics = normalize(command)
    flat: list[dict] = []
    top = [_span_json(span, flat) for span in spans]
    payload = {
        "schemaVersion": 1,
        "verdict": "engine-fail" if diagnostics else "allow",
        "diagnostics": [{"code": code} for code in diagnostics],
        "commands": top,
        "flat": flat,
    }
    print(json.dumps(payload, sort_keys=True))
    return 2 if diagnostics else 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except OSError as error:
        print(f"ENGINE_INTERNAL: {error}", file=sys.stderr)
        raise SystemExit(2)
