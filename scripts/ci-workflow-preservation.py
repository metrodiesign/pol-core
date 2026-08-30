#!/usr/bin/env python3
"""ci-workflow-preservation.py — protected-workflow byte comparator (task 9).

Proves the protected GitHub/GitLab job blocks are byte-identical to their
merge-base blobs and that the GitHub `verify` shell inventory is preserved
(additive allowed, removal forbidden), per design §Protected workflow comparator.

Exit: 0 preserved · 1 policy-fail · 2 engine-fail.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

GITHUB_WORKFLOW = ".github/workflows/ci.yml"
GITLAB_WORKFLOW = ".gitlab-ci.yml"

# verify itself is the cutover target — everything else is frozen
GITHUB_PROTECTED = ("dotnet", "docker-build", "dotnet-integration")
GITLAB_PROTECTED = ("dotnet", "integration", "package", ".deploy-template",
                    "deploy-uat", "deploy-prod")

# SDD operating-layer paths. The protected-job comparator and the product-path guard
# prove that a change to THIS layer did not touch product runtime or product CI jobs
# (REQ-1.1-1.11, REQ-7.10). A range that leaves the layer untouched is a product
# change, which those two guards were never scoped to judge — `--sdd-scope` reports
# `untouched` and the verify path skips them (bugfix-ci-sdd-scope-gate F-1/F-2).
SDD_LAYER_PREFIXES = (".ai/bin/", ".claude/hooks/", ".githooks/", "scripts/tests/")
SDD_LAYER_PATTERN = re.compile(
    r"^scripts/(spec[_-]|ci-|guard_contract\.py$|guard_policy\.py$"
    r"|repo_policy_alignment\.py$|pane-loop)")


def is_sdd_layer_path(rel: str) -> bool:
    return rel.startswith(SDD_LAYER_PREFIXES) or bool(SDD_LAYER_PATTERN.match(rel))


def sdd_layer_paths(repo: Path, base_sha: str) -> list[str]:
    """Changed paths (base..working tree) that belong to the SDD operating layer."""
    proc = _git(repo, "diff", "--name-only", base_sha)
    return sorted(p for p in proc.stdout.splitlines() if p and is_sdd_layer_path(p))


def repo_root() -> Path:
    override = os.environ.get("SDD_CI_PRESERVE_REPO")
    if override:
        return Path(override).resolve()
    return Path(__file__).resolve().parent.parent


def _git(repo: Path, *args: str, allow_fail: bool = False) -> subprocess.CompletedProcess:
    proc = subprocess.run(["git", "-C", str(repo), *args],
                          capture_output=True, text=True)
    if proc.returncode != 0 and not allow_fail:
        raise RuntimeError(proc.stderr.strip() or f"git {' '.join(args)} failed")
    return proc


def _blob_at_commit(repo: Path, sha: str, rel: str):
    """Return base blob bytes or None when the file did not exist there."""
    proc = _git(repo, "cat-file", "blob", f"{sha}:{rel}", allow_fail=True)
    return proc.stdout.encode("utf-8") if proc.returncode == 0 else None


class EngineFail(Exception):
    """CI_PROTECTED_JOB_PARSE_FAILED — fail the verify job closed."""


def extract_github_job_block(text: str, key: str) -> str:
    """Cut `jobs:`-level protected block: two-space indent key until next
    two-space-indent sibling. Raises EngineFail when shape unresolvable."""
    jobs_at = re.search(r"^jobs:\s*$", text, re.MULTILINE)
    if jobs_at is None:
        raise EngineFail("GitHub workflow has no top-level jobs:")
    tail_start = jobs_at.end()
    rest = text[tail_start:]
    pattern = re.compile(r"^  ([A-Za-z][\w-]*):\s*(?:#.*)?$", re.MULTILINE)
    matches = list(pattern.finditer(rest))
    keyed = [m for m in matches if m.group(1) == key]
    if len(keyed) != 1:
        raise EngineFail(f"protected job '{key}' occurs {len(keyed)} times")
    start_match = keyed[0]
    siblings_after = [m for m in matches if m.start() > start_match.start()]
    body_end = siblings_after[0].start() if siblings_after else len(rest)
    return rest[start_match.start():body_end]


def extract_gitlab_job_block(text: str, key: str) -> str:
    """Cut top-level (indent-0) protected block until next indent-0 key."""
    escaped = re.escape(key)
    pattern = re.compile(rf"^{escaped}:\s*(?:#.*)?$", re.MULTILINE)
    matches = list(pattern.finditer(text))
    if len(matches) != 1:
        raise EngineFail(f"protected job '{key}' occurs {len(matches)} times")
    start = matches[0].start()
    rest_from = matches[0].end()
    next_top = re.search(r"^[A-Za-z][\w.-]*:\s*(?:#.*)?$",
                         text[rest_from:], re.MULTILINE)
    body_end = rest_from + next_top.start() if next_top else len(text)
    return text[start:body_end]


def parse_failures_in_file(text: str) -> str | None:
    if re.search(r"^[ \t]*\t", text, re.MULTILINE):
        return "tab indentation"
    if re.search(r"(^|\s)<<:", text):
        return "YAML merge key"
    return None


def github_shell_inventory(github_text: str) -> set[str]:
    match = re.search(r"tests=\(([^)]*)\)", github_text, re.DOTALL)
    if match is None:
        return set()
    return {token for token in match.group(1).split() if token}


GITLAB_GLOB_TOKEN = ".claude/hooks/tests/*.test.sh"


def gitlab_verify_shape_ok(gitlab_text: str) -> tuple[bool, str]:
    """Fixture-style structural comparison (design: not byte equality)."""
    verify_at = re.search(r"^verify:\s*$", gitlab_text, re.MULTILINE)
    if verify_at is None:
        return False, "no verify job"
    rest = gitlab_text[verify_at.end():]
    nxt = re.search(r"^[A-Za-z][\w.-]*:\s*$", rest, re.MULTILINE)
    body = rest[: nxt.start()] if nxt else rest
    if GITLAB_GLOB_TOKEN not in body:
        return False, "shell inventory glob token removed"
    if "if [ \"${#tests[@]}\" -eq 0 ]" not in body.replace("'",
                                                          '"'):
        return False, "empty-suite failure structure removed"
    if "status=0" not in body or "|| status=1" not in body.replace('"', "'"):
        return False, "aggregated status structure removed"
    return True, ""


def _diagnose_current_file(current: bytes, label: str):
    text = current.decode("utf-8", errors="replace")
    problem = parse_failures_in_file(text)
    if problem:
        raise EngineFail(f"{label}: {problem}")


def run_compare(base_sha: str) -> tuple[list[dict], bool]:
    """Returns (diagnostics list, engine_failed flag)."""
    repo = repo_root()
    verify = _git(repo, "rev-parse", "--verify", f"{base_sha}^{{commit}}",
                  allow_fail=True)
    if verify.returncode != 0:
        return ([{"code": "CI_PROTECTED_JOB_PARSE_FAILED",
                  "message": f"base SHA '{base_sha}' does not resolve"}], True)
    diagnostics: list[dict] = []
    failed = False

    def fail_parse(message: str) -> None:
        nonlocal failed
        failed = True
        diagnostics.append({"code": "CI_PROTECTED_JOB_PARSE_FAILED",
                            "message": message})

    def fail_policy(code: str, message: str) -> None:
        diagnostics.append({"code": code, "message": message})

    for rel, protected_keys, extractor, kind in (
            (GITHUB_WORKFLOW, GITHUB_PROTECTED,
             extract_github_job_block, "github"),
            (GITLAB_WORKFLOW, GITLAB_PROTECTED,
             extract_gitlab_job_block, "gitlab")):
        base_blob = _blob_at_commit(repo, base_sha, rel)
        current_path = repo / rel
        current_blob = current_path.read_bytes() if current_path.is_file() else b""
        base_text = None
        if base_blob is None:
            fail_parse(f"{rel}: missing base blob at {base_sha}")
        else:
            base_text = base_blob.decode("utf-8", errors="replace")
            problem = parse_failures_in_file(base_text)
            if problem:
                fail_parse(f"{rel} (base): {problem}")
        try:
            current_text = current_blob.decode("utf-8", errors="replace")
        except Exception as error:  # pragma: no cover - decode guards above
            fail_parse(f"{rel}: current unreadable ({error})")
            continue
        problem = parse_failures_in_file(current_text)
        if problem:
            fail_parse(f"{rel}: {problem}")

        if base_text is None:
            continue

        usable = not failed
        if usable:
            try:
                for key in protected_keys:
                    base_block = extractor(base_text, key)
                    current_block = extractor(current_text, key)
                    if base_block != current_block:
                        fail_policy(
                            "CI_PROTECTED_JOB_CHANGED",
                            f"{kind}:{key} block bytes differ from {base_sha[:12]}")
            except EngineFail as error:
                fail_parse(f"{rel}: {error}")

        if kind == "github":
            base_tokens = github_shell_inventory(base_text)
            current_tokens = github_shell_inventory(current_text)
            removed = sorted(base_tokens - current_tokens)
            if not base_tokens:
                fail_parse("GitHub verify tests=(...) inventory not found")
            elif removed:
                fail_policy("CI_SHELL_INVENTORY_REMOVED",
                            ", ".join(removed))
        else:
            ok, why = gitlab_verify_shape_ok(current_text)
            if not ok:
                fail_policy("CI_PROTECTED_JOB_CHANGED",
                            f"gitlab verify shape drifted: {why}")
    return diagnostics, failed


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--base", required=True)
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--sdd-scope", action="store_true",
                        help="print `touched` or `untouched` for the SDD layer and exit 0")
    args, extras = parser.parse_known_args(argv)
    if extras:
        parser.print_usage(sys.stderr)
        return 2
    if args.sdd_scope:
        try:
            paths = sdd_layer_paths(repo_root(), args.base)
        except (RuntimeError, OSError) as error:
            print(f"CI_PROTECTED_JOB_PARSE_FAILED: {error}", file=sys.stderr)
            return 2
        print("touched" if paths else "untouched")
        for rel in paths:
            print(f"  {rel}", file=sys.stderr)
        return 0
    try:
        diagnostics, engine_failed = run_compare(args.base)
    except (RuntimeError, OSError) as error:
        print(json.dumps({"schemaVersion": 1, "verdict": "engine-fail",
                          "diagnostics": [{"code": "CI_PROTECTED_JOB_PARSE_FAILED",
                                           "message": str(error)}]},
                         sort_keys=True))
        return 2
    verdict = ("engine-fail" if engine_failed
               else ("allow" if not diagnostics else "policy-fail"))
    if args.json or True:
        print(json.dumps({"schemaVersion": 1, "verdict": verdict,
                          "base": args.base,
                          "diagnostics": diagnostics}, sort_keys=True))
    else:  # pragma: no cover - JSON is the canonical transport
        for diag in diagnostics:
            print(f"[{diag['code']}] {diag['message']}")
    if engine_failed:
        return 2
    return 0 if verdict == "allow" else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
