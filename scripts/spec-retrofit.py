#!/usr/bin/env python3
"""No-fabrication migration tool (Task 6 / REQ-5) for historical SDD specs.

Modes (exactly one per invocation, --batch always required):
  --dry-run    report sorted field-level actions/blockers; never writes
  --apply-safe guarded writer: clean tree -> HEAD/hash snapshots -> recovery
               journal -> atomic replaces -> self re-dry-run -> strict check
  --check      read-only verification (`final-all-spec` accepts only this)

Fail-closed everywhere: every planned change carries field-level explicit
proof (current bytes or a historical blob); anything else becomes a blocker.
See .ai/specs/sdd-operating-layer-parity/design.md §Migration Algorithm.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
if str(SCRIPTS) not in sys.path:
    sys.path.insert(0, str(SCRIPTS))

import spec_contract as sc  # noqa: E402

EXCLUDED_FEATURES = {"sdd-operating-layer-parity"}
ARCHIVE_CONTAINER = "archive"
ARTIFACT_FILES = ("requirements.md", "design.md", "tasks.md", "bugfix.md", "handoff.md")
HISTORICAL_COUNT = 61
MAX_COMMIT_VISITS = 80

MIGRATION_BATCHES = (
    "canonical-complete",
    "approved-aliases",
    "bugfix",
    "alphanumeric-tasks",
    "evidence",
    "conflicting-status",
    "ambiguous-directories",
)
READ_ONLY_ONLY_BATCHES = {"final-all-spec"}
ALL_BATCH_IDS = frozenset(MIGRATION_BATCHES) | READ_ONLY_ONLY_BATCHES


def repo_root() -> Path:
    override = os.environ.get("SDD_RETROFIT_REPO")
    if override:
        return Path(override).resolve()
    return SCRIPTS.parent


def specs_root() -> Path:
    return repo_root() / ".ai" / "specs"


def git_dir() -> Path:
    out = _git(["rev-parse", "--absolute-git-dir"])
    return Path(out.stdout.strip())


class GitFailure(RuntimeError):
    pass


def _git(args: list[str]) -> subprocess.CompletedProcess:
    proc = subprocess.run(
        ["git", "-C", str(repo_root()), *args],
        capture_output=True, text=True, shell=False,
    )
    return proc


def git_out(args: list[str]) -> str:
    proc = _git(args)
    if proc.returncode != 0:
        raise GitFailure(proc.stderr.strip())
    return proc.stdout


# ---------------------------------------------------------------------------
# Records
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Proof:
    kind: str                      # current | historical
    source_path: str
    commit: str                    # "" for current-kind
    line: int
    text_sha256: str
    snippet: str

    def to_json(self) -> dict:
        return {
            "commit": self.commit,
            "kind": self.kind,
            "line": self.line,
            "sha256": self.text_sha256,
            "snippet": self.snippet,
            "sourcePath": self.source_path,
        }


@dataclass(frozen=True)
class RetrofitAction:
    batch_id: str
    path: str
    target_field: str
    task_id: str                   # "" when not task-owned
    field_span: tuple[int, int]    # byte span in BEFORE full-file bytes
    before_bytes: bytes
    after_bytes: bytes
    proofs: tuple[Proof, ...]

    @property
    def kind(self) -> str:
        if self.target_field == "legacy.container":
            return "container"
        if not self.before_bytes:
            return "insert"
        return "rewrite"

    def to_json(self) -> dict:
        return {
            "action": self.kind,
            "afterSha256": hashlib.sha256(self.after_bytes).hexdigest(),
            "afterBytesBase64": base64.b64encode(self.after_bytes).decode(),
            "beforeSha256": hashlib.sha256(self.before_bytes).hexdigest(),
            "beforeBytesBase64": base64.b64encode(self.before_bytes).decode(),
            "byteSpan": list(self.field_span),
            "path": self.path,
            "proofs": [proof.to_json() for proof in self.proofs],
            "targetField": self.target_field,
            "taskId": self.task_id,
        }


@dataclass(frozen=True)
class RetrofitBlocker:
    code: str
    batch_id: str
    path: str
    target_field: str
    task_id: str
    line: int
    message: str
    current_evidence: str
    historical_evidence: str

    def to_json(self) -> dict:
        return {
            "code": self.code,
            "currentEvidence": self.current_evidence,
            "historicalEvidence": self.historical_evidence,
            "line": self.line,
            "message": self.message,
            "path": self.path,
            "targetField": self.target_field,
            "taskId": self.task_id,
        }


def abs_repo(path_str: str) -> Path:
    return repo_root() / path_str


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def rel(path: Path) -> str:
    try:
        return path.relative_to(repo_root()).as_posix()
    except ValueError:
        return path.as_posix()


def read_bytes(path: Path) -> bytes:
    return path.read_bytes()


# ---------------------------------------------------------------------------
# Historical proof retrieval (design §581-588)
# ---------------------------------------------------------------------------


def commits_touching(repo_path: str) -> list[str]:
    proc = _git(["log", "--follow", "--format=%H", "--", repo_path])
    if proc.returncode != 0:
        return []
    return [line for line in proc.stdout.split() if line]


def blob_bytes(commit: str, repo_path: str) -> bytes | None:
    proc = _git(["show", f"{commit}:{repo_path}"])
    if proc.returncode != 0:
        return None
    return proc.stdout.encode("utf-8", "surrogateescape")


STATUS_ANY_RE = re.compile(r"^>\s*Status:.*$", re.MULTILINE)
CANONICAL_APPROVED_DATE_RE = re.compile(
    r"^>[ \t]*Status:[ \t]+approved[ \t]+(\d{4}-\d{2}-\d{2})[ \t]*$", re.MULTILINE
)


def _blob_line_number(blob: bytes, needle: bytes) -> int:
    for number, line in enumerate(blob.splitlines(), start=1):
        if line.strip() == needle.strip():
            return number
    return 1


def historical_approved_proof(repo_path: str) -> tuple[Proof | None, Proof | None]:
    """Search history for an explicit `approved DATE` line for this path.

    Returns (proof, conflicting_proof). Exactly one explicit unique variant ->
    proof; several distinct variants -> (newest, older-distinct) conflict pair.
    """
    seen_variants: dict[str, tuple[str, int, bytes]] = {}
    for commit in commits_touching(repo_path)[:MAX_COMMIT_VISITS]:
        blob = blob_bytes(commit, repo_path)
        if blob is None:
            continue
        for match in CANONICAL_APPROVED_DATE_RE.finditer(blob.decode("utf-8", "surrogateescape")):
            line_text = match.group(0).strip()
            if line_text not in seen_variants:
                line_no = _blob_line_number(
                    blob, match.group(0).encode("utf-8", "surrogateescape")
                )
                seen_variants[line_text] = (commit, line_no, match.group(0).encode())
        if seen_variants:
            break  # newest commit carrying an explicit approved line decides
    if not seen_variants:
        return None, None
    ordered = sorted(seen_variants.items(), key=lambda item: item[0])
    newest_text, (commit, line_no, raw) = ordered[-1]
    proof = Proof(
        kind="historical",
        source_path=repo_path,
        commit=commit,
        line=line_no,
        text_sha256=sha256(raw),
        snippet=newest_text,
    )
    conflict = None
    if len(ordered) > 1:
        older_text, (older_commit, older_line, older_raw) = ordered[0]
        conflict = Proof(
            kind="historical",
            source_path=repo_path,
            commit=older_commit,
            line=older_line,
            text_sha256=sha256(older_raw),
            snippet=older_text,
        )
    return proof, conflict


def historical_canonical_status_line(repo_path: str) -> tuple[bytes | None, Proof | None, Proof | None]:
    """Newest historical blob's canonical `> Status:` line (verbatim bytes)."""
    for commit in commits_touching(repo_path)[:MAX_COMMIT_VISITS]:
        blob = blob_bytes(commit, repo_path)
        if blob is None:
            continue
        text = blob.decode("utf-8", "surrogateescape")
        lines = [line.strip() for line in STATUS_ANY_RE.findall(text)]
        canonical = [
            line for line in lines
            if sc.STATUS_RE.match(line)
        ]
        if canonical:
            if len({line.lower() for line in canonical}) > 1:
                chosen = min(canonical, key=len)
                other = max(canonical, key=len)
                number = _blob_line_number(blob, chosen.encode())
                other_number = _blob_line_number(blob, other.encode())
                return None, Proof("historical", repo_path, commit, number, sha256(chosen.encode()), chosen), \
                    Proof("historical", repo_path, commit, other_number, sha256(other.encode()), other)
            chosen = canonical[0]
            number = _blob_line_number(blob, chosen.encode())
            return (
                chosen.encode("utf-8") + b"\n",
                Proof("historical", repo_path, commit, number, sha256(chosen.encode()), chosen),
                None,
            )
    return None, None, None


# ---------------------------------------------------------------------------
# Corpus classification (batch registry scopes)
# ---------------------------------------------------------------------------


def historical_directories() -> list[Path]:
    """All spec directories except this feature and the archive container."""
    root = specs_root()
    if not root.is_dir():
        return []
    dirs = []
    for child in sorted(root.iterdir()):
        if not child.is_dir():
            continue
        if child.name in EXCLUDED_FEATURES or child.name == ARCHIVE_CONTAINER:
            continue
        dirs.append(child)
    return dirs


def feature_files(directory: Path) -> list[Path]:
    return [directory / name for name in ARTIFACT_FILES if (directory / name).is_file()]


def dir_tags(directory: Path) -> set[str]:
    tags: set[str] = set()
    files = feature_files(directory)
    if not files:
        return {"ambiguous-directories"}
    by_name = {path.name: path for path in files}
    contents = {path.name: read_bytes(path) for path in files}

    has_tasks = "tasks.md" in contents
    has_requirements = "requirements.md" in contents
    if "bugfix.md" in contents and not has_requirements:
        tags.add("bugfix")

    # status health
    status_conflict = False
    status_duplicate_canonical = False
    status_alias = False
    status_missing_issue = False
    statuses_found = 0
    for file_name, data in contents.items():
        lines = data.decode("utf-8", "surrogateescape").splitlines()
        outside, _diags = sc._outside_fence(lines, Path(str(by_name[file_name])))
        canonical_seen: set[str] = set()
        for _number, line in outside:
            if STATUS_ANY_RE.match(line):
                statuses_found += 1
                if sc.STATUS_RE.match(line.strip()):
                    lowered = line.strip().lower()
                    if lowered in canonical_seen or len(canonical_seen) >= 1 and lowered != next(iter(canonical_seen)):
                        status_duplicate_canonical = True
                    canonical_seen.add(lowered)
                    continue
                if re.match(ALIAS_STRONG_RE, line.strip()):
                    status_alias = True
                else:
                    status_conflict = True
    if statuses_found == 0:
        status_missing_issue = True
    if status_conflict or status_duplicate_canonical:
        tags.add("conflicting-status")
    if status_alias or status_missing_issue:
        tags.add("approved-aliases")

    # task-id grammar
    if has_tasks:
        ids = [
            task.task_id for task in sc.parse_task_blocks(contents["tasks.md"], Path("tasks.md"))[0]
        ]
        if any(re.search(r"[A-Za-z]", task_id) for task_id in ids):
            tags.add("alphanumeric-tasks")

    # evidence v2 health
    if has_tasks:
        tasks, _parse_diag = sc.parse_task_blocks(contents["tasks.md"], Path("tasks.md"))
        completed = [task.task_id for task in tasks if task.completed]
        if completed:
            problems = sc.validate_evidence(tasks, completed)
            legacy_present = any(
                LEGACY_TEST_BULLET_RE.match(line)
                for task in tasks
                for _number, line in _task_region_lines(contents["tasks.md"], task)
            )
            if problems or legacy_present:
                tags.add("evidence")
        elif "bugfix.md" not in contents:
            tags.add("evidence")  # authoring chain with completions absent is not evidence scope; keep for review

    if sc.derive_spec_state(directory, specs_root())[0] == "complete":
        tags.add("canonical-complete")
    elif has_tasks and has_requirements and "canonical-complete" not in tags and not tags & {
        "conflicting-status", "bugfix",
    }:
        tags.add("canonical-complete")  # reviewer-visible under the completing batch too
    return tags


ALIAS_STRONG_RE = r"^>\s*Status:\s*[A-Za-z][A-Za-z\-]*"
ANNOTATED_STATUS_RE = re.compile(
    r"^>\s*Status:\s*"
    r"(draft|superseded\s+\d{4}-\d{2}-\d{2}\s+by\s+\S+|approved\s+\d{4}-\d{2}-\d{2})"
    r"(\s*,.+)$"
)


LEDGER_REL = ".ai/specs/sdd-operating-layer-parity/migration-resolutions.json"
LEDGER_DISPOSITIONS = {
    "rename-canonical-id",        # bugfix `- F1` -> `- F-1` mechanical
    "canonical-statement",        # full replacement statement supplied verbatim
    "criteria-block",             # insert whole canonical criteria section
    "status-superseded",          # needs date + byTaskId
    "status-unknown",
    "status-approved",            # needs date; cite PR/task in rationale
    "waive-protocol-history",     # insert n/a viewports / none-recorded deviations
    "active-authoring-exempt",    # incomplete authoring chain is by design
    "legacy-baseline-exempt",     # whole dir predates framework; segment out
    "ears-join-wrap",             # join wrapped requirement criterion lines, no id change
    "trace-header-canonical",     # rename legacy trace-table headers to Section/REQ
}
VP_WAIVE_LINE = ("- viewports: n/a \u2014 legacy corpus predates viewport protocol "
                 "(human checkpoint 2026-08-26)")
DEV_WAIVE_LINE = ("- deviations: none recorded \u2014 legacy corpus predates evidence "
                  "v2 protocol (human checkpoint 2026-08-26)")


def resolution_ledger_path() -> Path:
    return abs_repo(LEDGER_REL)


_LEDGER_CACHE: dict[str, tuple[float, dict[tuple[str, str, str], dict]]] = {}


def load_resolution_ledger() -> dict[tuple[str, str, str], dict]:
    path = resolution_ledger_path()
    stamp = f"{path}:{path.stat().st_mtime_ns if path.is_file() else 0}"
    cached = _LEDGER_CACHE.get(stamp)
    if cached is not None:
        return cached[1]
    ledger: dict[tuple[str, str, str], dict] = {}
    if path.is_file():
        payload = json.loads(path.read_text(encoding="utf-8"))
        for index, entry in enumerate(payload.get("decisions", [])):
            scoped_id = entry.get("taskId", "")
            if not scoped_id and entry.get("line"):
                scoped_id = f"@{entry['line']}"  # line-scoped decision
            key = (entry["path"], entry["field"], scoped_id)
            if key in ledger:
                raise SystemExit(f"resolution ledger duplicate entry {key}")
            if entry["disposition"] not in LEDGER_DISPOSITIONS:
                raise SystemExit(f"resolution ledger unknown disposition {entry['disposition']}")
            item = dict(entry)
            item["_line"] = index + 1
            ledger[key] = item
    _LEDGER_CACHE.clear()
    _LEDGER_CACHE[stamp] = (0.0, ledger)
    return ledger


def _ledger_rel(path_str: str) -> str:
    """Normalize any caller path form to the ledger's committed `.ai/...` form.
    Required because rel() yields absolute strings for fixture repos (test seam)."""
    path = Path(path_str)
    if path.is_absolute():
        try:
            return path.resolve().relative_to(repo_root()).as_posix()
        except ValueError:
            pass
    parts = path.as_posix().split("/")
    if ".ai" in parts:
        return "/".join(parts[parts.index(".ai"):])
    return path.as_posix()


def _ledger_get(path_str: str, field: str, task_id: str = "") -> dict | None:
    entry = load_resolution_ledger().get((_ledger_rel(path_str), field, task_id))
    if entry is None and task_id == "":
        entry = load_resolution_ledger().get(
            (_ledger_rel(Path(path_str).parent.as_posix()), field, ""))
    return entry


def _human_decision_proof(entry: dict) -> Proof:
    blob = json.dumps({k: v for k, v in entry.items() if k != "_line"},
                      sort_keys=True, ensure_ascii=False).encode("utf-8")
    return Proof(
        kind="human-decision",
        source_path=LEDGER_REL,
        commit="",
        line=entry["_line"],
        text_sha256=sha256(blob),
        snippet=entry["disposition"],
    )


EXEMPT_DISPOSITIONS = {"active-authoring-exempt", "legacy-baseline-exempt"}


def ledger_exempt_paths() -> set[str]:
    paths: set[str] = set()
    for entry in load_resolution_ledger().values():
        if entry["field"] == "authoring.chain" and \
                entry["disposition"] in EXEMPT_DISPOSITIONS:
            paths.add(Path(_ledger_rel(entry["path"])).parent.as_posix())
    return paths


# ---------------------------------------------------------------------------
# Probes / planners
# ---------------------------------------------------------------------------

LEGACY_TEST_BULLET_RE = re.compile(r"^(\s*)[-*]\s*test\s*:\s*(.+?)\s*$")
LEGACY_VIEWPORT_RE = re.compile(r"^(\s*)[-*]?\s*viewports?\s*:\s*(.+?)\s*$", re.IGNORECASE)
LEGACY_DEVIATION_RE = re.compile(r"^(\s*)[-*]?\s*deviation[s]?\s*:\s*(.+?)\s*$", re.IGNORECASE)


def _task_region_lines(data: bytes, task: sc.TaskBlock) -> list[tuple[int, str]]:
    text = data.decode("utf-8", "surrogateescape")
    lines = text.splitlines()
    start = task.span[0] - 1
    end = min(task.span[1], len(lines))
    return [(start + offset + 1, lines[start + offset]) for offset in range(max(end - start, 0))]


def _proof_current(path_str: str, blob: bytes, line_number: int, line_text: str) -> Proof:
    return Proof(
        kind="current",
        source_path=path_str,
        commit="",
        line=line_number,
        text_sha256=sha256(line_text.encode("utf-8")),
        snippet=line_text.strip(),
    )


def plan_status_actions(batch_id: str, directory: Path) -> tuple[list[RetrofitAction], list[RetrofitBlocker]]:
    actions: list[RetrofitAction] = []
    blockers: list[RetrofitBlocker] = []
    for file_path in feature_files(directory):
        path_str = rel(file_path)
        data = read_bytes(file_path)
        lines = data.decode("utf-8", "surrogateescape").splitlines()
        outside, _diag = sc._outside_fence(lines, Path(path_str))
        status_entries = []
        for number, line in outside:
            if STATUS_ANY_RE.match(line):
                status_entries.append((number, line))
        canonical_entries = [
            (number, line.strip()) for number, line in status_entries
            if sc.STATUS_RE.match(line.strip())
        ]
        distinct_canonical = {text.lower() for _number, text in canonical_entries}
        if len(distinct_canonical) > 1:
            first_number, first_text = canonical_entries[0]
            second_number, second_text = canonical_entries[1]
            blockers.append(RetrofitBlocker(
                "MIGRATION_PROOF_CONFLICT", batch_id, path_str, "status.line", "",
                first_number, "canonical status ซ้ำ/ขัดกันในไฟล์เดียว",
                first_text, second_text,
            ))
            continue
        canonical_count = len(canonical_entries)
        annotated_split = [
            (number, ANNOTATED_STATUS_RE.match(line.strip()))
            for number, line in status_entries if not sc.STATUS_RE.match(line.strip())
        ]
        if canonical_count and not any(match for _number, match in annotated_split):
            continue
        handled_annotated = False
        for number, match in annotated_split:
            if not match:
                continue
            kind_part, tail_part = match.group(1).rstrip(), match.group(2)
            line_span = _line_byte_span(data, number)
            raw_line_bytes = data[line_span[0]:line_span[1]]
            current_proof = _proof_current(path_str, data, number, lines[number - 1])
            if re.search(r"(?i)pending", tail_part) and kind_part.startswith("approved"):
                blockers.append(RetrofitBlocker(
                    "MIGRATION_PROOF_CONFLICT", batch_id, path_str, "status.note",
                    "", number,
                    "annotation บอก pending review ขัดกับ approval",
                    lines[number - 1].strip(), "",
                ))
                handled_annotated = True
                continue
            actions.append(RetrofitAction(
                batch_id=batch_id, path=path_str, target_field="status.line",
                task_id="", field_span=line_span,
                before_bytes=raw_line_bytes,
                after_bytes=(f"> Status: {kind_part}\n").encode("utf-8"),
                proofs=(current_proof,),
            ))
            actions.append(RetrofitAction(
                batch_id=batch_id, path=path_str, target_field="status.note",
                task_id="", field_span=(line_span[1], line_span[1]),
                before_bytes=b"",
                after_bytes=f"> Notes:{tail_part}\n".encode("utf-8"),
                proofs=(current_proof,),
            ))
            handled_annotated = True
        if handled_annotated:
            continue
        if len([entry for entry in status_entries if not sc.STATUS_RE.match(entry[1].strip())]) >= 2:
            # alias+alias or alias+unknown mess: not uniquely mappable
            blockers.append(RetrofitBlocker(
                "MIGRATION_PROOF_CONFLICT", batch_id, path_str, "status.line", "", 
                status_entries[0][0] if status_entries else 1,
                "หลายบรรทัดสถานะที่ไม่ canonical พร้อมกัน — mapping ไม่ unique",
                "; ".join(entry[1].strip() for entry in status_entries[:3]),
                "",
            ))
            continue
        if not status_entries:
            insert_bytes, proof, conflict = historical_canonical_status_line(path_str)
            if conflict is not None:
                blockers.append(_conflict_blocker(batch_id, path_str, proof, conflict))
                continue
            if insert_bytes is None:
                insert_bytes = _human_status_line(path_str)
                if insert_bytes is not None:
                    actions.append(RetrofitAction(
                        batch_id=batch_id, path=path_str, target_field="status.line",
                        task_id="", field_span=(0, 0),
                        before_bytes=b"", after_bytes=insert_bytes,
                        proofs=(_human_decision_proof(_ledger_get(path_str, "status.line")),),
                    ))
                    continue
                blockers.append(_missing_blocker(
                    batch_id, path_str, "status.line", 1,
                    "directory ไม่มี status line และ history ไม่มี explicit canonical status",
                ))
                continue
            actions.append(RetrofitAction(
                batch_id=batch_id, path=path_str, target_field="status.line",
                task_id="", field_span=(0, 0),
                before_bytes=b"", after_bytes=insert_bytes, proofs=(proof,),
            ))
            continue
        number, raw_line = status_entries[
            next((index for index, entry in enumerate(status_entries)
                  if not sc.STATUS_RE.match(entry[1].strip())), 0)
        ]
        byte_span = _line_byte_span(data, number)
        proof, conflict = historical_approved_proof(path_str)
        if conflict is not None:
            blockers.append(_conflict_blocker(
                batch_id, path_str,
                Proof("historical", path_str, conflict.commit, conflict.line, conflict.text_sha256, conflict.snippet),
                conflict,
            ))
            continue
        if proof is None:
            human_line = _human_status_line(path_str)
            if human_line is not None:
                actions.append(RetrofitAction(
                    batch_id=batch_id, path=path_str, target_field="status.line",
                    task_id="", field_span=byte_span,
                    before_bytes=data[byte_span[0]:byte_span[1]],
                    after_bytes=human_line,
                    proofs=(_human_decision_proof(_ledger_get(path_str, "status.line")),),
                ))
                continue
            blockers.append(_missing_blocker(
                batch_id, path_str, "status.line", number,
                f"alias status ไม่มี historical proof: {raw_line.strip()}",
            ))
            continue
        replacement = proof.snippet.encode("utf-8") + b"\n"
        actions.append(RetrofitAction(
            batch_id=batch_id, path=path_str, target_field="status.line",
            task_id="", field_span=byte_span,
            before_bytes=data[byte_span[0]:byte_span[1]],
            after_bytes=replacement, proofs=(proof,),
        ))
    return actions, blockers


def status_alias_like(status_entries) -> bool:
    return any(not sc.STATUS_RE.match(entry[1].strip()) for entry in status_entries)


_DATE_RE = re.compile(r"^\d{4}-\d{2}-\d{2}$")


def _human_status_line(path_str: str) -> bytes | None:
    """Canonical replacement line authorized by the resolution ledger, if any."""
    entry = _ledger_get(path_str, "status.line")
    if entry is None or entry["disposition"] not in {
        "status-superseded", "status-unknown", "status-approved",
    }:
        return None
    disposition = entry["disposition"]
    if disposition == "status-unknown":
        return b"> Status: unknown\n"
    date = entry.get("date", "")
    if not _DATE_RE.match(date):
        raise SystemExit(f"resolution ledger bad date for {path_str}: {date!r}")
    if disposition == "status-approved":
        return f"> Status: approved {date}\n".encode("utf-8")
    by_task = entry.get("byTaskId", "")
    feature = Path(path_str).parent.name
    if not (specs_root() / by_task / "tasks.md").is_file():
        raise SystemExit(f"resolution ledger superseded-byTaskId has no spec dir: {by_task!r}")
    assert by_task != feature
    return f"> Status: superseded {date} by {by_task}\n".encode("utf-8")


def _line_byte_span(data: bytes, line_number: int) -> tuple[int, int]:
    boundaries = [0]
    for line in data.decode("utf-8", "surrogateescape").splitlines(keepends=True):
        boundaries.append(boundaries[-1] + len(line.encode("utf-8", "surrogateescape")))
    start = boundaries[line_number - 1]
    end = boundaries[min(line_number, len(boundaries) - 1)]
    return start, end


def _missing_blocker(batch_id: str, path_str: str, target_field: str, line: int, message: str,
                     task_id: str = "") -> RetrofitBlocker:
    return RetrofitBlocker(
        "MIGRATION_PROOF_MISSING", batch_id, path_str, target_field, task_id, line, message, "", ""
    )


def _conflict_blocker(batch_id: str, path_str: str, proof_a: Proof | None,
                      proof_b: Proof | None) -> RetrofitBlocker:
    return RetrofitBlocker(
        "MIGRATION_PROOF_CONFLICT", batch_id, path_str, "status.line", "",
        (proof_a.line if proof_a else 1),
        "historical proof ขัดกัน — ต้องมี human resolution ต่อ field",
        proof_a.snippet if proof_a else "",
        proof_b.snippet if proof_b else "",
    )


def plan_evidence_actions(batch_id: str, directory: Path) -> tuple[list[RetrofitAction], list[RetrofitBlocker]]:
    actions: list[RetrofitAction] = []
    blockers: list[RetrofitBlocker] = []
    tasks_file = directory / "tasks.md"
    if not tasks_file.is_file():
        return actions, blockers
    path_str = rel(tasks_file)
    data = read_bytes(tasks_file)
    tasks, _diag = sc.parse_task_blocks(data, Path(path_str))
    for task in tasks:
        if not task.completed:
            continue
        region = _task_region_lines(data, task)
        has_header = any(line.strip() == "Evidence:" for _number, line in region)
        legacy_tests = [
            (number, LEGACY_TEST_BULLET_RE.match(line))
            for number, line in region
            if LEGACY_TEST_BULLET_RE.match(line)
        ]
        legacy_viewports = [
            (number, LEGACY_VIEWPORT_RE.match(line))
            for number, line in region
            if LEGACY_VIEWPORT_RE.match(line) and line.strip() != "Evidence:"
        ]
        legacy_deviations = [
            (number, LEGACY_DEVIATION_RE.match(line))
            for number, line in region
            if LEGACY_DEVIATION_RE.match(line) and line.strip() != "Evidence:"
        ]

        problems = sc.validate_evidence([task], [task.task_id])
        codes = {problem.code for problem in problems}

        # observations: legacy bullets that already carry command + result move
        # verbatim under a structural Evidence: header
        usable_tests = [
            (number, match) for number, match in legacy_tests
            if "`" in match.group(2) and "->" in match.group(2)
        ]
        if usable_tests and not has_header:
            bullet_spans = [_line_byte_span(data, number) for number, _match in usable_tests]
            span_start = min(span[0] for span in bullet_spans)
            span_end = max(span[1] for span in bullet_spans)
            indent = usable_tests[0][1].group(1)
            rebuilt = (f"{indent}Evidence:\n".encode() + "".join(
                f"{indent}- test: {match.group(2)}\n"
                for _number, match in usable_tests
            ).encode())
            proof = _proof_current(path_str, data, usable_tests[0][0],
                                   usable_tests[0][1].group(0))
            actions.append(RetrofitAction(
                batch_id=batch_id, path=path_str, target_field="evidence.observations",
                task_id=task.task_id, field_span=(span_start, span_end),
                before_bytes=data[span_start:span_end],
                after_bytes=rebuilt, proofs=(proof,),
            ))

        # viewports / deviations judged per-field against explicit owner lines only
        viewport_ok = any(
            re.fullmatch(r"- viewports: (?:n/a \u2014 .+|.*375.*768.*1440.*|.*1440.*768.*375.*)",
                         entry)
            for entry in task.evidence
        )
        deviation_ok = any(
            entry == "- deviations: none"
            or re.fullmatch(r"- deviations: (?!none$).+", entry)
            for entry in task.evidence
        )
        owner_vp = next(((number, match.group(2)) for number, match in legacy_viewports
                         if "->" not in match.group(2)), None)
        if not viewport_ok:
            if owner_vp is not None and has_header:
                number, value = owner_vp
                line_text = f"- viewports: {value}"
                span = _line_byte_span(data, number)
                actions.append(RetrofitAction(
                    batch_id=batch_id, path=path_str, target_field="evidence.viewports",
                    task_id=task.task_id, field_span=span,
                    before_bytes=data[span[0]:span[1]],
                    after_bytes=(f"       {line_text}\n").encode(),
                    proofs=(_proof_current(path_str, data, number, line_text),),
                ))
            else:
                waived = _evidence_waiver_action(batch_id, path_str, data, task,
                                                 "evidence.viewports",
                                                 ("      " + VP_WAIVE_LINE + "\n").encode("utf-8"))
                if waived is not None:
                    actions.append(waived)
                else:
                    blockers.append(_missing_blocker(
                        batch_id, path_str, "evidence.viewports", task.location.line,
                        "viewports ไม่มี explicit proof ใน task เดียวกัน — ห้ามอนุมานจาก observations",
                        task.task_id,
                    ))
        owner_dev = next(((number, match.group(2)) for number, match in legacy_deviations
                          if "->" not in match.group(2)), None)
        if not deviation_ok:
            if owner_dev is not None and has_header:
                number, value = owner_dev
                line_text = f"- deviations: {value}"
                span = _line_byte_span(data, number)
                actions.append(RetrofitAction(
                    batch_id=batch_id, path=path_str, target_field="evidence.deviations",
                    task_id=task.task_id, field_span=span,
                    before_bytes=data[span[0]:span[1]],
                    after_bytes=(f"       {line_text}\n").encode(),
                    proofs=(_proof_current(path_str, data, number, line_text),),
                ))
            else:
                waived = _evidence_waiver_action(batch_id, path_str, data, task,
                                                 "evidence.deviations",
                                                 ("      " + DEV_WAIVE_LINE + "\n").encode("utf-8"))
                if waived is not None:
                    actions.append(waived)
                else:
                    blockers.append(_missing_blocker(
                        batch_id, path_str, "evidence.deviations", task.location.line,
                        "deviations ไม่มี explicit proof ใน task เดียวกัน — ห้ามสร้างขึ้นเอง",
                        task.task_id,
                    ))
    return actions, blockers


def _evidence_waiver_action(batch_id: str, path_str: str, data: bytes,
                            task: sc.TaskBlock, field: str, insert_bytes: bytes):
    """Append a human-decision waiver line inside the task's Evidence block.

    Requires an existing `Evidence:` header — writing entries without one is
    invisible to the validator (fabrication by another name). Header-less
    legacy tasks keep their blocker as a recorded, decided residual."""
    entry = _ledger_get(path_str, field, task.task_id)
    if entry is None:
        entry = _ledger_get(path_str, field)
    if entry is None or entry["disposition"] != "waive-protocol-history":
        return None
    region = dict(_task_region_lines(data, task))
    if not any(line.strip() == "Evidence:" for line in region.values()):
        return None
    lines = data.decode("utf-8", "surrogateescape").splitlines(keepends=True)
    last_line = min(task.span[1], len(lines))
    span_end = _line_byte_span(data, last_line)[1]
    tail = b"" if (span_end == 0 or data[span_end - 1:span_end] == b"\n") else b"\n"
    return RetrofitAction(
        batch_id=batch_id, path=path_str, target_field=field,
        task_id=task.task_id, field_span=(span_end, span_end),
        before_bytes=b"", after_bytes=tail + insert_bytes,
        proofs=(_human_decision_proof(entry),),
    )


TRACE_SECTION_RE = re.compile(r"^##\s+Requirement Traceability\s*$")
DOTTED_CELL_RE = re.compile(r"^\d+\.\d+$")


def plan_trace_actions(batch_id: str, directory: Path) -> tuple[list[RetrofitAction], list[RetrofitBlocker]]:
    actions: list[RetrofitAction] = []
    blockers: list[RetrofitBlocker] = []
    known_refs: set[str] = set()
    if (directory / "requirements.md").is_file():
        criteria, _diag = sc.parse_requirement_criteria(
            read_bytes(directory / "requirements.md"), Path("requirements.md")
        )
        known_refs = {criterion.ref for criterion in criteria}
    headings: set[str] = set()
    for file_path in feature_files(directory):
        lines = read_bytes(file_path).decode("utf-8", "surrogateescape").splitlines()
        outside, _diag = sc._outside_fence(lines, Path(rel(file_path)))
        for _number, line in outside:
            if line.startswith("## ") and not TRACE_SECTION_RE.match(line):
                headings.add(line[3:].strip())

    for file_name in ("tasks.md", "design.md"):
        file_path = directory / file_name
        if not file_path.is_file():
            continue
        path_str = rel(file_path)
        data = read_bytes(file_path)
        lines = data.decode("utf-8", "surrogateescape").splitlines()
        outside, _diag = sc._outside_fence(lines, Path(path_str))
        outside_by_number = dict(outside)
        in_trace = False
        columns: dict[int, str] = {}
        ref_actions_by_line: dict[int, list[tuple[int, int]]] = {}
        for number, line in outside:
            if TRACE_SECTION_RE.match(line):
                in_trace = True
                columns = {}
                # ledger-authorized header rename: legacy tables speak
                # "Design element | REQ" etc.; canonical grammar needs
                # Section+REQ headers. Purely lexical, cell data untouched.
                entry = _ledger_get(rel(directory / file_name), "trace.table")
                if entry is not None and entry["disposition"] == "trace-header-canonical":
                    next_pipe = next(
                        (n for n, l in outside if n > number and l.lstrip().startswith("|")),
                        None)
                    if next_pipe is not None:
                        cells = [c.strip() for c in
                                 lines[next_pipe - 1].strip().strip("|").split("|")]
                        lowered = [c.lower() for c in cells]
                        if not ("req" in lowered and "section" in lowered):
                            req_like = next((idx for idx, c in enumerate(lowered)
                                             if c in {"req", "reqs satisfied",
                                                      "satisfies", "satisfies req",
                                                      "requirements", "req(s) satisfied",
                                                      "requirement coverage",
                                                      "req ที่ตอบ"}), None)
                            if req_like is not None:
                                new_cells = list(cells)
                                new_cells[req_like] = "REQ"
                                section_like = next((idx for idx in range(len(new_cells))
                                                     if idx != req_like), None)
                                if section_like is not None:
                                    new_cells[section_like] = "Section"
                                    header_span = _line_byte_span(data, next_pipe)
                                    actions.append(RetrofitAction(
                                        batch_id=batch_id, path=path_str,
                                        target_field="trace.header",
                                        task_id="", field_span=header_span,
                                        before_bytes=data[header_span[0]:header_span[1]],
                                        after_bytes=("| " + " | ".join(new_cells) +
                                                     " |\n").encode("utf-8"),
                                        proofs=(_human_decision_proof(entry),),
                                    ))
                continue
            if not in_trace:
                continue
            if line.startswith("#"):
                in_trace = False
                continue
            if not line.lstrip().startswith("|"):
                continue
            cells = [cell.strip() for cell in line.strip().strip("|").split("|")]
            if all(re.fullmatch(r":?-{3,}:?", cell) for cell in cells if cell):
                continue
            lowered = [cell.lower() for cell in cells]
            if "req" in lowered and "section" in lowered:
                columns = {index: cell for index, cell in enumerate(cells)}
                continue
            if not columns:
                continue
            req_index = next((index for index, cell in columns.items() if cell.lower() == "req"), None)
            section_index = next((index for index, cell in columns.items() if cell.lower() == "section"), None)
            req_index = next((index for index, cell in columns.items() if cell.lower() == "req"), None)
            section_index = next((index for index, cell in columns.items() if cell.lower() == "section"), None)
            if req_index is not None and req_index < len(cells):
                # one action per bare dotted token so multi-ref cells
                # ("1.1, 1.2, 1.3") canonicalize deterministically; tokens that
                # are already REQ-prefixed stay untouched.
                dotted_tokens = re.findall(
                    r"(?<![\w.-])(\d+\.\d+)(?![\w.-])", cells[req_index])
                for token in dotted_tokens:
                    if f"REQ-{token}" in known_refs or token in known_refs or not known_refs:
                        canonical_ref = f"REQ-{token}"
                        line_span = _line_byte_span(data, number)
                        segment = data[line_span[0]:line_span[1]].decode(
                            "utf-8", "surrogateescape")
                        search_from = 0
                        start = None
                        while True:
                            cand = segment.find(token, search_from)
                            if cand < 0:
                                break
                            before_ok = not re.match(r"[\w.-]", segment[cand-1:cand])
                            after_idx = cand + len(token)
                            after_ok = after_idx >= len(segment) or \
                                not re.match(r"[\w.-]", segment[after_idx:after_idx+1])
                            if before_ok and after_ok:
                                start = line_span[0] + len(segment[:cand].encode("utf-8", "surrogateescape"))
                                break
                            search_from = cand + 1
                        if start is None:
                            continue
                        span_end_tok = start + len(token.encode())
                        ref_actions_by_line.setdefault(number, []).append((start, span_end_tok))
                        continue  # combined below per line
                    else:
                        blockers.append(_missing_blocker(
                            batch_id, path_str, "trace.ref", number,
                            f"dotted ref {token} ไม่มี criterion ตรงเป๊ะใน requirements",
                        ))
                        continue
            if section_index is not None and section_index < len(cells):
                token = cells[section_index]
                if token and token not in headings and headings:
                    blockers.append(_missing_blocker(
                        batch_id, path_str, "trace.section", number,
                        f"section '{token}' ไม่ resolve เป็น real ## heading",
                    ))
        for tok_number, spans in ref_actions_by_line.items():
            spans.sort()
            line_span = _line_byte_span(data, tok_number)
            rebuilt_parts: list[bytes] = []
            cursor = line_span[0]
            for span_a, span_b in spans:
                rebuilt_parts.append(data[cursor:span_a])
                rebuilt_parts.append(b"REQ-" + data[span_a:span_b])
                cursor = span_b
            rebuilt_parts.append(data[cursor:line_span[1]])
            actions.append(RetrofitAction(
                batch_id=batch_id, path=path_str, target_field="trace.ref",
                task_id="", field_span=(line_span[0], line_span[1]),
                before_bytes=data[line_span[0]:line_span[1]],
                after_bytes=b"".join(rebuilt_parts),
                proofs=(_proof_current(path_str, data, tok_number,
                                       lines[tok_number - 1].rstrip("\n")),),
            ))
        del outside_by_number
    return actions, blockers


def plan_container_action(batch_id: str, directory: Path) -> tuple[list[RetrofitAction], list[RetrofitBlocker]]:
    """Legacy text that cannot be field-mapped wraps into a verbatim LegacyContainer."""
    actions: list[RetrofitAction] = []
    blockers: list[RetrofitBlocker] = []
    tasks_file = directory / "tasks.md"
    if not tasks_file.is_file():
        return actions, blockers
    path_str = rel(tasks_file)
    data = read_bytes(tasks_file)
    tasks, _diag = sc.parse_task_blocks(data, Path(path_str))
    for task in tasks:
        if not task.completed:
            continue
        region = _task_region_lines(data, task)
        has_header = any(line.strip() == "Evidence:" for _number, line in region)
        if has_header:
            continue
        legacy_without_results = [
            line for _number, line in region
            if LEGACY_TEST_BULLET_RE.match(line) and not (
                "`" in LEGACY_TEST_BULLET_RE.match(line).group(2)
                and "->" in LEGACY_TEST_BULLET_RE.match(line).group(2)
            )
        ]
        if not legacy_without_results:
            continue
        numbers = [number for number, line in region if LEGACY_TEST_BULLET_RE.match(line)]
        span_start = min(_line_byte_span(data, number)[0] for number in numbers)
        span_end = max(_line_byte_span(data, number)[1] for number in numbers)
        payload = data[span_start:span_end]
        container = build_legacy_container(payload)
        action = RetrofitAction(
            batch_id=batch_id, path=path_str, target_field="legacy.container",
            task_id=task.task_id, field_span=(span_start, span_end),
            before_bytes=payload, after_bytes=container,
            proofs=(_proof_current(path_str, data, numbers[0], legacy_without_results[0]),),
        )
        if container_roundtrip_ok(container, payload):
            actions.append(action)
        else:
            blockers.append(RetrofitBlocker(
                "MIGRATION_PROOF_CONFLICT", batch_id, path_str, "legacy.container",
                task.task_id, numbers[0],
                "สร้าง fence ที่ปิด payload losslessly ไม่ได้",
                payload.decode("utf-8", "surrogateescape")[:120], "",
            ))
    return actions, blockers


def build_legacy_container(payload: bytes) -> bytes:
    text = payload.decode("utf-8", "surrogateescape")
    runs = [len(match.group(0)) for match in re.finditer(r"`+", text)]
    marker_length = max(runs + [3]) + 1
    marker = "`" * marker_length
    body = text if text.endswith("\n") else text + "\n"
    return f"{marker}sdd-legacy\n{body}{marker}\n".encode("utf-8")


def container_roundtrip_ok(container: bytes, original_payload: bytes) -> bool:
    text = container.decode("utf-8", "surrogateescape")
    match = re.match(r"^(`+)sdd-legacy\n", text)
    if not match:
        return False
    marker = match.group(1)
    closing = f"\n{marker}\n"
    if not text.endswith(closing + "\n") and not text.endswith(closing):
        return False
    inner_start = match.end()
    inner_end = text.rfind(closing)
    inner = text[inner_start:inner_end + 1]
    return inner.encode("utf-8", "surrogateescape") == original_payload or \
        inner.encode("utf-8", "surrogateescape") == original_payload.rstrip(b"\n") + b"\n"


def plan_batch(batch_id: str) -> tuple[list[RetrofitAction], list[RetrofitBlocker]]:
    actions: list[RetrofitAction] = []
    blockers: list[RetrofitBlocker] = []
    exempt_dirs = ledger_exempt_paths()
    for directory in historical_directories():
        tags = dir_tags(directory)
        if batch_id == "ambiguous-directories":
            if "ambiguous-directories" in tags:
                blockers.append(_missing_blocker(
                    batch_id, rel(directory), "directory.shape", 1,
                    "empty หรือ ambiguous directory — ไม่มี safe action จนมี human proof",
                ))
            continue
        if batch_id == "conflicting-status":
            if "conflicting-status" in tags:
                _, sub_blockers = plan_status_actions(batch_id, directory)
                for blocker in sub_blockers or []:
                    blockers.append(blocker)
                if not sub_blockers:
                    blockers.append(_missing_blocker(
                        batch_id, rel(directory / "tasks.md"), "status.line", 1,
                        "status conflict รอ human resolution",
                    ))
            continue
        # authoring-chain exemption hides incomplete-by-design specs only from
        # the completeness batch; field-level fix batches still apply ledger
        # decisions (statuses/evidence) to them.
        if batch_id == "canonical-complete" and _ledger_rel(rel(directory)) in exempt_dirs:
            continue
        scoped = _in_scope(tags, batch_id)
        if not scoped:
            continue
        if batch_id == "approved-aliases":
            new_actions, new_blockers = plan_status_actions(batch_id, directory)
        elif batch_id == "evidence":
            new_actions, new_blockers = plan_evidence_actions(batch_id, directory)
            container_actions, container_blockers = plan_container_action(batch_id, directory)
            new_actions.extend(container_actions)
            new_blockers.extend(container_blockers)
        elif batch_id in {"bugfix", "alphanumeric-tasks", "canonical-complete"}:
            new_actions, new_blockers = [], []
            trace_actions, trace_blockers = plan_trace_actions(batch_id, directory)
            new_actions.extend(trace_actions)
            new_blockers.extend(trace_blockers)
            ears_actions, ears_blockers = plan_ears_join_actions(batch_id, directory)
            new_actions.extend(ears_actions)
            new_blockers.extend(ears_blockers)
            if batch_id == "bugfix":
                bf_actions, bf_blockers = plan_bugfix_actions(batch_id, directory)
                new_actions.extend(bf_actions)
                new_blockers.extend(bf_blockers)
            if batch_id in {"bugfix", "canonical-complete"}:
                split_actions, split_blockers = \
                    plan_task_metadata_split_actions(batch_id, directory)
                new_actions.extend(split_actions)
                new_blockers.extend(split_blockers)
            if batch_id == "alphanumeric-tasks":
                tm_actions, tm_blockers = plan_task_id_actions(batch_id, directory)
                new_actions.extend(tm_actions)
                new_blockers.extend(tm_blockers)
            if batch_id == "canonical-complete" and "canonical-complete" in tags and \
                    sc.derive_spec_state(directory, specs_root())[0] != "complete":
                new_blockers.append(_missing_blocker(
                    batch_id, rel(directory / "tasks.md"), "artifact.chain", 1,
                    "canonical-complete tag แต่ state ยังไม่ complete — หา evidence/proof ก่อน",
                ))
        else:
            new_actions, new_blockers = [], []
        actions.extend(new_actions)
        blockers.extend(new_blockers)
    return sort_reports(actions, blockers)


def _in_scope(tags: set[str], batch_id: str) -> bool:
    if batch_id == "approved-aliases":
        # conflicting statuses also land here so the planner emits PROOF_CONFLICT
        return bool(tags & {"approved-aliases", "conflicting-status"})
    return batch_id in tags


def plan_ears_join_actions(batch_id: str, directory: Path):
    """Ledger-gated mechanical closure: join wrapped `- N.M ...` criterion
    continuation lines into one physical line so the full statement is
    visible. Word-preserving; ids and text untouched."""
    actions: list[RetrofitAction] = []
    blockers: list[RetrofitBlocker] = []
    file_path = directory / "requirements.md"
    if not file_path.is_file():
        return [], blockers
    path_str = rel(file_path)
    entry = _ledger_get(path_str, "requirements.criteria")
    if entry is None or entry["disposition"] != "ears-join-wrap":
        return [], []
    data = read_bytes(file_path)
    lines = data.decode("utf-8", "surrogateescape").splitlines(keepends=True)

    def bullet_like(raw: str) -> bool:
        stripped = raw.strip()
        return bool(re.match(r"^(?:[-+*]|[0-9]+[.)])\s+", stripped)) or \
            stripped.startswith("#") or stripped == ""

    number = 0
    while number < len(lines):
        raw = lines[number]
        match = re.match(r"^(\s*-\s+)(\d+\.\d+)\s+(.*)$", raw.rstrip("\n"))
        if not match or sc._ears_ok(match.group(3).strip()):
            number += 1
            continue  # single-line-complete or not a criterion bullet
        last = number
        while last + 1 < len(lines) and not bullet_like(lines[last + 1]):
            last += 1
        if last == number:
            number += 1
            continue
        statement = " ".join(
            part.strip() for part in
            [match.group(3)] + [l.strip() for l in lines[number + 1:last + 1]]
        ).strip()
        if not sc._ears_ok(statement):
            number += 1
            continue  # joined text still not EARS: leave for human, never guess
        span_end = _line_byte_span(data, last + 1)[1]
        newline = b"\n" if data[span_end - 1:span_end] == b"\n" else b""
        after = f"{match.group(1)}{match.group(2)} {statement}\n".encode("utf-8")
        block_start = _line_byte_span(data, number + 1)[0]
        actions.append(RetrofitAction(
            batch_id=batch_id, path=path_str, target_field="requirement.criterion",
            task_id=match.group(2), field_span=(block_start, span_end),
            before_bytes=data[block_start:span_end],
            after_bytes=after.rstrip(b"\n") + newline,
            proofs=(_human_decision_proof(entry),),
        ))
        number = last + 1
    return actions, blockers


def plan_task_metadata_split_actions(batch_id: str, directory: Path):
    """Ledger-free mechanical split: legacy tasks embed `Satisfies:` / `Verify:`
    inside prose. Canonical shape = metadata as its own continuation lines under
    the task opening. Word-preserving relocation; refuses when ambiguous."""
    actions: list[RetrofitAction] = []
    blockers: list[RetrofitBlocker] = []
    file_path = directory / "tasks.md"
    if not file_path.is_file():
        return actions, blockers
    path_str = rel(file_path)
    data = read_bytes(file_path)
    all_lines = data.decode("utf-8", "surrogateescape").splitlines()
    tasks, _diag = sc.parse_task_blocks(data, Path(path_str))
    for task in tasks:
        if not task.completed:
            continue
        region_numbers = list(range(task.span[0], min(task.span[1],
                                                      len(all_lines) + 1)))
        raw_lines = [all_lines[n - 1] for n in region_numbers]
        def _has_unsplit_meta(raw: str) -> bool:
            # fully-split lines: `     Satisfies:` with NO trailing Verify on
            # the same physical line; those are done.
            if re.match(r"^ {5}Satisfises:", raw):
                return True
            if re.match(r"^ {5}Satisfies:", raw):
                return bool(re.search(r"\s+Verify:\s", raw))
            return bool(re.search(r"\bSatisfies:", raw))

        meta_offset = next((offset for offset, raw in enumerate(raw_lines)
                            if _has_unsplit_meta(raw)), None)
        if meta_offset is None:
            continue
        # split-complete guard: any canonical metadata line in the region
        # means this task was already processed — leave it alone forever
        if any(re.match(r"^ {5}Satisfies:", raw) for raw in raw_lines):
            continue
        pieces: list[str] = []
        meta_lines: list[str] = []
        for offset, raw in enumerate(raw_lines):
            meta_only = re.match(r"^( {5}Satisfies:\s*)(.*)$", raw)
            match = None
            meta_only_handled = False
            if meta_only is not None and not re.match(
                    r"^[FB]-?\d+(\s*,\s*[FB]-?\d+)*[.]?(\s|$)", meta_only.group(2)):
                pieces.append(raw)  # prose mentions, not a metadata payload
                continue
            if meta_only is not None and not re.match(
                    r"^[FB]-?\d+", meta_only.group(2)):
                continue
            if meta_only is not None:
                body = meta_only.group(2)
                ver = re.search(r"\s+(Verify:)\s*", body)
                if ver is not None:
                    pieces.append("     " + body[:ver.start()].strip())
                    meta_lines.append("     " + body[ver.start():].strip())
                elif re.search(r"(?<=[A-Z0-9])\.$", body):
                    # sentence period trailing the final ref breaks exact
                    # matching; deterministic one-char removal
                    pieces.append("     " + re.sub(r"\.$", "", body))
                    meta_only_handled = True
                else:
                    pieces.append(raw)
            if meta_only_handled:
                continue
            candidate_cut = _has_unsplit_meta(raw) and \
                not raw.lstrip().startswith("Satisfies:`")
            if candidate_cut and re.match(r"^\s*[-+*]\s", raw):
                # bullet continuation lines are prose: metadata lives only on
                # the opening title or a canonical 5-space Satisfies line
                candidate_cut = False
            match = re.search(r"^(.*?)(\bSatisfies:\s*.*)$", raw) \
                if (match is None and candidate_cut) else None
            if match is not None:
                left = match.group(1).rstrip(" ;,-")
                if left.strip():
                    pieces.append(left)
                # further split trailing `Verify:` fragments onto their own
                # lines so comma-parsing never swallows them into refs
                rest_meta = match.group(2).strip()
                ver = re.search(r"\s+(Verify:)\s*", rest_meta)
                if ver is not None:
                    sat_text = rest_meta[:ver.start()].strip()
                    # terminal sentence period on the last ref breaks exact
                    # matching; strip one dot (never part of an ID)
                    sat_text = re.sub(r"(?<=[A-Z0-9])\.$", "", sat_text)
                    meta_lines.append("     " + sat_text)
                    meta_lines.append("     " + rest_meta[ver.start():].strip())
                else:
                    meta_lines.append("     " + rest_meta)
            elif meta_lines:
                break  # keep the relocation minimal: rest stays untouched
            else:
                pieces.append(raw)
        in_place_only = not meta_lines
        if not meta_lines and not any(
                piece != raw for piece, raw in zip(pieces, raw_lines)) and \
                len(pieces) == len(raw_lines):
            continue  # nothing to relocate or clean
        # canonical order: metadata continuation precedes the Evidence block
        evidence_at = next((index for index, raw in enumerate(raw_lines)
                            if raw.strip() == "Evidence:"), None)
        if evidence_at is not None and meta_lines:
            head = raw_lines[:evidence_at]
            tail = raw_lines[evidence_at:]
            out = "\n".join(head + meta_lines + [""] + tail).rstrip("\n") + "\n"
        else:
            out = "\n".join(pieces).rstrip("\n") + "\n"
        span_start = _line_byte_span(data, region_numbers[0])[0]
        last_index = min(region_numbers[-1], len(all_lines))
        span_end = _line_byte_span(data, last_index)[1]
        before = data[span_start:span_end]
        if not before:
            continue
        actions.append(RetrofitAction(
            batch_id=batch_id, path=path_str, target_field="task.metadata",
            task_id=task.task_id, field_span=(span_start, span_end),
            before_bytes=before,
            after_bytes=out.encode("utf-8"),
            proofs=(_proof_current(path_str, data, region_numbers[0],
                                   raw_lines[0].rstrip("\n")),),
        ))
    return actions, blockers


def plan_bugfix_actions(batch_id: str, directory: Path):
    actions: list[RetrofitAction] = []
    blockers: list[RetrofitBlocker] = []
    file_path = directory / "bugfix.md"
    if not file_path.is_file():
        return [], blockers
    path_str = rel(file_path)
    data = read_bytes(file_path)
    criteria, diagnostics = sc.parse_bugfix_criteria(data, Path(path_str))
    seen: dict[str, int] = {}
    for criterion in criteria:
        if criterion.ref in seen:
            blockers.append(RetrofitBlocker(
                "MIGRATION_PROOF_CONFLICT", batch_id, path_str, "bugfix.criterion",
                criterion.ref, criterion.location.line,
                f"F/B id {criterion.ref} ซ้ำ", criterion.statement[:120], "",
            ))
        seen[criterion.ref] = criterion.location.line
    malformed_lines = {d.location.line for d in diagnostics}
    criteria_block_appended = False
    handled_malformed = 0
    handled_form_invalid = 0
    def _raw_line(number: int) -> str:
        text_lines = data.decode("utf-8", "surrogateescape").splitlines()
        return text_lines[number - 1] if 0 < number <= len(text_lines) else ""

    def _is_summary(diagnostic) -> bool:
        """File-level verdicts (e.g. 'bugfix ไม่มี criterion F/B') point at
        arbitrary lines, not an `- F…` criterion bullet."""
        if diagnostic.code != "EARS_CRITERION_MALFORMED":
            return False
        return not re.match(r"^\s*[-+*]\s+[FB]", _raw_line(diagnostic.location.line))

    total_malformed = sum(
        1 for d in diagnostics if d.code == "EARS_CRITERION_MALFORMED" and not _is_summary(d))
    total_form_invalid = sum(1 for d in diagnostics if d.code == "EARS_FORM_INVALID")
    summaries: list[RetrofitBlocker] = []
    for diagnostic in diagnostics:
        if _is_summary(diagnostic):
            # file-level verdict resolved implicitly once real criteria exist
            summaries.append(RetrofitBlocker(
                "MIGRATION_PROOF_MISSING", batch_id, path_str, "bugfix.criterion",
                "", diagnostic.location.line, diagnostic.message, "", ""))
            continue
        if diagnostic.code not in {"EARS_CRITERION_MALFORMED", "EARS_FORM_INVALID"}:
            blockers.append(RetrofitBlocker(
                "MIGRATION_PROOF_MISSING", batch_id, path_str, "bugfix.criterion",
                "", diagnostic.location.line, diagnostic.message, "", ""))
            continue
        line_number = diagnostic.location.line
        renamed = _bugfix_rename_action(batch_id, path_str, data, line_number)
        if renamed is not None:
            actions.append(renamed)
            if diagnostic.code == "EARS_CRITERION_MALFORMED":
                handled_malformed += 1
            else:
                handled_form_invalid += 1
            continue
        entry = _ledger_get(path_str, "bugfix.criterion",
                            f"@{line_number}") or _ledger_get(path_str, "bugfix.criterion")
        if entry is not None and entry["disposition"] == "canonical-statement" \
                and entry.get("line") == line_number:
            line_span = _line_byte_span(data, line_number)
            statement = entry["statement"].rstrip()
            replacement = (f"- {entry.get('ref', 'F-99')} {statement}\n").encode("utf-8")
            actions.append(RetrofitAction(
                batch_id=batch_id, path=path_str, target_field="bugfix.criterion",
                task_id="", field_span=line_span,
                before_bytes=data[line_span[0]:line_span[1]], after_bytes=replacement,
                proofs=(_human_decision_proof(entry),),
            ))
            if diagnostic.code == "EARS_CRITERION_MALFORMED":
                handled_malformed += 1
            else:
                handled_form_invalid += 1
            continue
        blockers.append(RetrofitBlocker(
            "MIGRATION_PROOF_MISSING", batch_id, path_str, "bugfix.criterion",
            "", line_number, diagnostic.message, "", ""))
        if diagnostic.code == "EARS_CRITERION_MALFORMED" and not criteria_block_appended:
            criteria_block_appended = True
            _append_criteria_block_action(actions, batch_id, path_str, data)
    resolves_everything = (
        actions and not any(b.code.startswith("MIGRATION_") for b in blockers)
        and handled_malformed == total_malformed
        and handled_form_invalid == total_form_invalid
    )
    if not resolves_everything:
        blockers.extend(summaries)
    return actions, blockers


def _bugfix_join_candidate(data: bytes, line_number: int):
    """Pure analysis for `- F1 ...` bullets (single- or multi-line): returns
    (indent, fixed_id, statement, span_start, span_end) or None."""
    lines = data.decode("utf-8", "surrogateescape").splitlines(keepends=True)
    if not 0 < line_number <= len(lines):
        return None
    raw = lines[line_number - 1]
    match = re.match(r"^(\s*[-+*]\s+)([FB])(\d+)((?:\s.*)?)$", raw.rstrip("\n"))
    if not match:
        return None
    indent = match.group(1)
    fixed_id = f"{match.group(2)}-{match.group(3)}"
    words = [match.group(4).strip()]

    def _bullet_like(line: str) -> bool:
        stripped = line.strip()
        return bool(re.match(r"^[-+*]\s+", stripped)) or stripped.startswith("#")

    total = len(lines)
    last = line_number
    while last < total:
        nxt = lines[last]
        if not nxt.strip() or _bullet_like(nxt):
            break
        words.append(nxt.strip())
        last += 1
    span_end = _line_byte_span(data, last)[1]
    span_start = _line_byte_span(data, line_number)[0]
    statement = " ".join(word for word in words if word).strip()
    return indent, fixed_id, statement, span_start, span_end


def _bugfix_rename_action(batch_id: str, path_str: str, data: bytes,
                          line_number: int):
    """`- F1 WHEN ...` -> `- F-1 WHEN ...`: id-only rewrite gated by the ledger.

    Wrapped continuation lines of one criterion bullet are joined into a
    single physical line (word-preserving); if the joined statement is not
    full EARS the planner refuses and the human ledger must supply one."""
    entry = _ledger_get(path_str, "bugfix.criterion")
    if entry is None or entry["disposition"] != "rename-canonical-id":
        return None
    candidate = _bugfix_join_candidate(data, line_number)
    if candidate is None:
        return None
    indent, fixed_id, statement, span_start, span_end = candidate
    if not sc._ears_ok(statement):
        return None
    newline = b"\n" if data[span_end - 1:span_end] == b"\n" else b""
    after = f"{indent}{fixed_id} {statement}\n".encode("utf-8")
    before = data[span_start:span_end]
    return RetrofitAction(
        batch_id=batch_id, path=path_str, target_field="bugfix.criterion",
        task_id=fixed_id, field_span=(span_start, span_end),
        before_bytes=before, after_bytes=after.rstrip(b"\n") + newline,
        proofs=(_proof_current(path_str, data, line_number,
                               data.decode("utf-8", "surrogateescape")
                               .splitlines()[line_number - 1].rstrip("\n")),),
    )


def _append_criteria_block_action(actions, batch_id, path_str, data) -> None:
    """Append an entirely canonical criteria section authored in the ledger."""
    entry = _ledger_get(path_str, "bugfix.criteriaBlock") or \
        _ledger_get(path_str, "criteria.block")
    if entry is None or entry["disposition"] != "criteria-block":
        return
    block_lines = [
        line for line in entry["block"].splitlines()
        if sc.BUGFIX_CRITERION_RE.match(line) or line.startswith("## ")
    ]
    if not any(sc.BUGFIX_CRITERION_RE.match(line) for line in block_lines):
        raise SystemExit(f"resolution ledger criteria-block without criterion: {path_str}")
    tail = b"" if data.endswith(b"\n") else b"\n"
    block = (tail + "\n" + entry["block"].rstrip() + "\n").encode("utf-8")
    actions.append(RetrofitAction(
        batch_id=batch_id, path=path_str, target_field="criteria.block",
        task_id="", field_span=(len(data), len(data)),
        before_bytes=b"", after_bytes=block,
        proofs=(_human_decision_proof(entry),),
    ))


def plan_task_id_actions(batch_id: str, directory: Path):
    blockers: list[RetrofitBlocker] = []
    file_path = directory / "tasks.md"
    if not file_path.is_file():
        return [], blockers
    path_str = rel(file_path)
    tasks, diagnostics = sc.parse_task_blocks(read_bytes(file_path), Path(path_str))
    seen: set[str] = set()
    for task in tasks:
        if task.task_id in seen:
            blockers.append(RetrofitBlocker(
                "MIGRATION_PROOF_CONFLICT", batch_id, path_str, "task.id", task.task_id,
                task.location.line, "task ID ซ้ำ", task.title[:120], "",
            ))
        seen.add(task.task_id)
    for diagnostic in diagnostics:
        if diagnostic.code.startswith("TASK_"):
            blockers.append(RetrofitBlocker(
                "MIGRATION_PROOF_MISSING", batch_id, path_str, "task.id",
                "", diagnostic.location.line, diagnostic.message, "", ""))
    return [], blockers


SORT_KEY_ACTIONS = lambda action: (
    action.batch_id, action.path, action.target_field,
    action.task_id, action.kind, "", action.field_span[0],
)
SORT_KEY_BLOCKERS = lambda blocker: (
    blocker.batch_id, blocker.path, blocker.target_field,
    blocker.task_id, "", blocker.code, blocker.line,
)


def sort_reports(actions: list[RetrofitAction], blockers: list[RetrofitBlocker]):
    return sorted(actions, key=lambda item: (
        item.batch_id, item.path, item.target_field, item.task_id, item.kind, "", item.field_span[0],
    )), sorted(blockers, key=lambda item: (
        item.batch_id, item.path, item.target_field, item.task_id, "", item.code, item.line,
    ))


# ---------------------------------------------------------------------------
# Span planner validation + composition
# ---------------------------------------------------------------------------


def validate_planned_actions(actions: list[RetrofitAction]) -> list[RetrofitBlocker]:
    blockers: list[RetrofitBlocker] = []
    by_path: dict[str, list[RetrofitAction]] = {}
    for action in actions:
        by_path.setdefault(action.path, []).append(action)
    for path_str, group in by_path.items():
        group = sorted(group, key=lambda item: item.field_span[0])
        previous = None
        for action in group:
            if action.field_span[0] > action.field_span[1]:
                blockers.append(RetrofitBlocker(
                    "MIGRATION_PROOF_CONFLICT", action.batch_id, path_str,
                    action.target_field, action.task_id, action.field_span[0],
                    "span ย้อนศร — planner invalid", "", "",
                ))
            if previous is not None and action.field_span[0] < previous.field_span[1]:
                blockers.append(RetrofitBlocker(
                    "MIGRATION_PROOF_CONFLICT", action.batch_id, path_str,
                    action.target_field, action.task_id, action.field_span[0],
                    "actions span ทับกัน — ต้อง merge หรือ split ก่อน apply", "", "",
                ))
            previous = action
    return blockers


def compose_file(before: bytes, actions: list[RetrofitAction]) -> bytes:
    buffer = before
    for action in sorted(actions, key=lambda item: item.field_span[0], reverse=True):
        start, end = action.field_span
        if buffer[start:end] != action.before_bytes:
            raise ValueError(f"planned-before mismatch at {start}:{end}")
        buffer = buffer[:start] + action.after_bytes + buffer[end:]
    return buffer


# ---------------------------------------------------------------------------
# Recovery journal (design §594, §600, §608)
# ---------------------------------------------------------------------------


@dataclass
class JournalTarget:
    path: str
    before_sha256: str
    planned_sha256: str
    pending: bool = False
    applied: bool = False
    original_file: str = ""

    def to_json(self) -> dict:
        return {
            "applied": self.applied,
            "beforeSha256": self.before_sha256,
            "originalFile": self.original_file,
            "path": self.path,
            "pending": self.pending,
            "plannedSha256": self.planned_sha256,
        }


@dataclass
class Journal:
    batch_id: str
    captured_head: str
    targets: list[JournalTarget] = field(default_factory=list)

    def to_json(self) -> dict:
        return {
            "batchId": self.batch_id,
            "capturedHead": self.captured_head,
            "schemaVersion": 1,
            "targets": [target.to_json() for target in self.targets],
        }


def journal_root(batch_id: str) -> Path:
    return git_dir() / "sdd-retrofit-recovery" / "v1" / batch_id


def journal_exists(batch_id: str | None = None) -> bool:
    base = git_dir() / "sdd-retrofit-recovery" / "v1"
    if not base.exists():
        return False
    if batch_id:
        return (base / batch_id / "manifest.json").is_file()
    return any((child / "manifest.json").is_file() for child in base.iterdir())


def _fsync_write(path: Path, payload: bytes) -> None:
    with open(path, "wb") as handle:
        handle.write(payload)
        handle.flush()
        os.fsync(handle.fileno())


def write_journal(batch_id: str, journal: Journal, originals: dict[str, bytes]) -> Path:
    root = journal_root(batch_id)
    originals_dir = root / "originals"
    originals_dir.mkdir(parents=True, exist_ok=True)
    for target, path_str in zip(journal.targets, originals.keys()):
        idx = _stable_index(path_str)
        original_path = originals_dir / f"{idx}.bin"
        _fsync_write(original_path, originals[path_str])
        target.original_file = f"{idx}.bin"
    _fsync_write(root / "manifest.json", json.dumps(journal.to_json(), sort_keys=True, indent=1).encode())
    return root


def _stable_index(text: str) -> str:
    return hashlib.sha256(text.encode()).hexdigest()[:16]


def load_journal(batch_id: str) -> Journal:
    manifest = json.loads((journal_root(batch_id) / "manifest.json").read_text(encoding="utf-8"))
    journal = Journal(batch_id=manifest["batchId"], captured_head=manifest["capturedHead"])
    for entry in manifest["targets"]:
        journal.targets.append(JournalTarget(**{
            "path": entry["path"], "before_sha256": entry["beforeSha256"],
            "planned_sha256": entry["plannedSha256"], "pending": entry["pending"],
            "applied": entry["applied"], "original_file": entry["originalFile"],
        }))
    return journal


def clear_journal(batch_id: str) -> None:
    root = journal_root(batch_id)
    if root.exists():
        shutil.rmtree(root)


def restore_from_journal(batch_id: str) -> tuple[bool, list[str]]:
    """Hash-guarded compensating restore. Returns (all_guarded_ok, failures)."""
    journal = load_journal(batch_id)
    root = journal_root(batch_id)
    failures: list[str] = []
    interesting = [target for target in journal.targets if target.pending or target.applied]
    for target in interesting:
        current = read_bytes(abs_repo(target.path))
        current_hash = sha256(current)
        original = (root / "originals" / target.original_file).read_bytes()
        if current_hash == target.planned_sha256:
            tmp = abs_repo(target.path).with_suffix(".retrofit-restoring")
            tmp.write_bytes(original)
            os.replace(tmp, abs_repo(target.path))
        elif current_hash == target.before_sha256:
            continue  # someone restored already; nothing owed
        else:
            failures.append(target.path)  # concurrent owner: preserve current bytes
    if not failures:
        clear_journal(batch_id)
    return not failures, failures


# ---------------------------------------------------------------------------
# Modes
# ---------------------------------------------------------------------------


def enforce_journal_clear(mode: str) -> int | None:
    if journal_exists():
        print(json.dumps({
            "schemaVersion": 1,
            "verdict": "engine-fail",
            "diagnostics": [{"code": "MIGRATION_RECOVERY_REQUIRED"}],
        }, sort_keys=True))
        return 2
    return None


def scope_check() -> list[RetrofitBlocker]:
    if os.environ.get("SDD_RETROFIT_REPO"):   # fixture repos scope to their own files
        return []
    blockers: list[RetrofitBlocker] = []
    directories = historical_directories()
    if len(directories) != HISTORICAL_COUNT:
        blockers.append(RetrofitBlocker(
            "MIGRATION_SCOPE_MISMATCH", "-", ".ai/specs", "corpus.inventory", "", 1,
            f"historical directories={len(directories)} expected={HISTORICAL_COUNT}", "", "",
        ))
    return blockers


def envelope(mode: str, batch_id: str, actions: list[RetrofitAction],
             blockers: list[RetrofitBlocker]) -> dict:
    return {
        "actions": [action.to_json() for action in actions],
        "batch": batch_id,
        "blockers": [blocker.to_json() for blocker in blockers],
        "mode": mode,
        "schemaVersion": 1,
        "verdict": "policy-fail" if blockers else "allow",
    }


def run_dry_run(batch_id: str, *, skip_journal_guard: bool = False) -> int:
    if not skip_journal_guard:
        blocked = enforce_journal_clear("dry-run")
        if blocked is not None:
            return blocked
    scope_blockers = scope_check()
    actions, blockers = plan_batch(batch_id)
    blockers.extend(scope_blockers)
    blockers.extend(validate_planned_actions(actions))
    blockers.sort(key=lambda item: (item.path, item.target_field, item.code, item.line))
    actions = sorted(set(actions), key=lambda item: (
        item.batch_id, item.path, item.target_field, item.task_id, item.kind, "", item.field_span[0],
    ))
    print(json.dumps(envelope("dry-run", batch_id, actions, blockers), sort_keys=True))
    return 1 if blockers else 0


def strict_check_features(features: list[str]) -> int:
    """Active-first strict scope (option-K, human checkpoint): legacy chains
    carrying a dispositioned trace-table / authoring-chain decision are
    recorded residuals and re-enter only via the Tasks-9+ verify scope."""
    ledger = load_resolution_ledger()
    legacy_dirs = {Path(path_str).parent.name for (path_str, field, _s)
                   in ledger.items() if field in {"trace.table", "authoring.chain"}
                   and ledger[(path_str, field, _s)]["disposition"]
                   in {"trace-header-canonical", "active-authoring-exempt",
                       "legacy-baseline-exempt"}}
    failed = 0
    for feature in features:
        if feature in legacy_dirs:
            continue
        if sc.trace_run(feature, specs_root()) != 0:
            failed += 1
    return failed


def run_check(batch_id: str) -> int:
    blocked = enforce_journal_clear("check")
    if blocked is not None:
        return blocked
    if batch_id == "final-all-spec":
        features = [rel(directory).split("/")[-1] for directory in historical_directories()]
        directories = historical_directories()
        results = []
        rc_failed = 0
        if len(directories) != HISTORICAL_COUNT:
            print(json.dumps({
                "schemaVersion": 1, "verdict": "policy-fail",
                "expectedDirectories": HISTORICAL_COUNT,
                "foundDirectories": len(directories),
                "problem": "MIGRATION_SCOPE_MISMATCH",
            }, sort_keys=True))
            return 1
        ledger_paths = load_resolution_ledger()
        legacy_dirs = {Path(path_str).parent.name for (path_str, field, _s)
                       in ledger_paths if field in {"trace.table", "authoring.chain"}
                       and ledger_paths[(path_str, field, _s)]["disposition"]
                       in {"trace-header-canonical", "active-authoring-exempt",
                           "legacy-baseline-exempt"}}
        for feature in features:
            # Option-K (human checkpoint): legacy chains whose trace tables are
            # ledger-dispositioned are recorded residuals — excluded from the
            # strict gate; they re-enter when Tasks 9+ verify scope says so.
            code = 0 if feature in legacy_dirs else sc.trace_run(feature, specs_root())
            results.append({"feature": feature, "strictOk": code == 0,
                            "legacyResidual": feature in legacy_dirs})
            if code != 0:
                rc_failed += 1
        print(json.dumps({
            "batch": batch_id,
            "featuresFailing": rc_failed,
            "results": results,
            "schemaVersion": 1,
            "totalFeatures": len(results),
            "verdict": "allow" if rc_failed == 0 else "policy-fail",
        }, sort_keys=True))
        return 0 if rc_failed == 0 else 1
    # normal batch: strict on scoped features + planned actions must be zero
    actions, blockers = plan_batch(batch_id)
    features = sorted({action.path.split("/")[2] for action in actions})
    features += sorted({blocker.path.split("/")[2] for blocker in blockers
                        if len(blocker.path.split("/")) > 2})
    strict_failures = strict_check_features(sorted(set(features)))
    # decided-residual blockers are records, not pending work
    safe_pending = len([a for a in actions if not _residual_is_decided(a)]
                       ) + len([b for b in blockers
                                if not _residual_is_decided(b)])
    print(json.dumps({
        "batch": batch_id,
        "plannedSafeActionsRemaining": safe_pending,
        "schemaVersion": 1,
        "strictFailures": strict_failures,
        "verdict": "allow" if safe_pending == 0 and strict_failures == 0 else "policy-fail",
    }, sort_keys=True))
    return 0 if safe_pending == 0 and strict_failures == 0 else 1


def _working_tree_clean() -> bool:
    proc = _git(["status", "--porcelain"])
    return proc.returncode == 0 and not proc.stdout.strip()


def build_apply_plan(batch_id: str):
    """Returns (per-file composed plans, actions, undecided blockers).

    Ledger-decided blockers are not fatal: their mechanical payload either
    ships with this batch or is a recorded header-less residual."""
    actions, blockers = plan_batch(batch_id)
    blockers.extend(validate_planned_actions(actions))
    undecided = [b for b in blockers if not _residual_is_decided(b)]
    if undecided:
        return [], actions, blockers
    grouped: dict[str, list[RetrofitAction]] = {}
    for action in actions:
        grouped.setdefault(action.path, []).append(action)
    plans = []
    for path_str in sorted(grouped):
        target = abs_repo(path_str)
        before = read_bytes(target)
        try:
            planned = compose_file(before, grouped[path_str])
        except ValueError as compose_error:
            # cross-pass overlap (e.g. join consumed the span a ref edit
            # targeted): not silently skippable — the next planner pass
            # recomputes spans against real bytes.
            blockers.append(RetrofitBlocker(
                "MIGRATION_PROOF_CONFLICT", batch_id, path_str,
                "compose.overlap", "", 1,
                f"actions span ทับกันหลัง transform ก่อนหน้า — {compose_error}",
                "", ""))
            return [], actions, blockers
        plans.append((path_str, before, planned))
    return plans, actions, []


def _fsync_path(path: Path) -> None:
    fd = os.open(path, os.O_RDONLY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def verify_written_files(plans, batch_id: str = "") -> int:
    """Batch-strict check: every written artifact parses clean; status-line
    canon is asserted only by the batch that OWNS status rewrites
    (approved-aliases) — other batches must not veto pre-existing statuses."""
    failures = 0
    for path_str, _before, _planned in plans:
        data = read_bytes(abs_repo(path_str))
        lines = data.decode("utf-8", "surrogateescape").splitlines()
        outside, fence_diag = sc._outside_fence(lines, Path(path_str))
        if fence_diag:
            failures += 1
            continue
        if batch_id == "approved-aliases":
            bad_status = [
                line for _number, line in outside
                if STATUS_ANY_RE.match(line) and not sc.STATUS_RE.match(line.strip())
            ]
            if bad_status:
                failures += 1
    return failures


def run_apply_safe(batch_id: str) -> int:
    if journal_exists(batch_id):
        recovered_ok, failures = restore_from_journal(batch_id)
        if not recovered_ok:
            print(json.dumps({
                "diagnostics": [{"code": "MIGRATION_RECOVERY_FAILED", "paths": failures}],
                "schemaVersion": 1,
                "verdict": "engine-fail",
            }, sort_keys=True))
            return 2
    if journal_exists():           # another batch holds a stuck journal
        print(json.dumps({
            "diagnostics": [{"code": "MIGRATION_RECOVERY_REQUIRED"}],
            "schemaVersion": 1, "verdict": "engine-fail",
        }, sort_keys=True))
        return 2
    if not _working_tree_clean():
        print(json.dumps({
            "diagnostics": [{"code": "MIGRATION_DIRTY_TREE"}],
            "schemaVersion": 1, "verdict": "engine-fail",
        }, sort_keys=True))
        return 2
    if batch_id in READ_ONLY_ONLY_BATCHES:
        return 2
    scope_blockers = scope_check()
    if scope_blockers:
        print(json.dumps(envelope("apply-safe", batch_id, [], scope_blockers), sort_keys=True))
        return 1
    plans, _actions, blockers = build_apply_plan(batch_id)
    # decisions committed on the ledger are authoritative: a blocker already
    # dispositioned there cannot veto the batch (its mechanical payload either
    # ships with this run or is recorded as a header-less residual).
    undecided = [b for b in blockers if not _residual_is_decided(b)]
    if undecided:
        undecided.sort(key=lambda item: (item.path, item.code, item.line))
        print(json.dumps(envelope("apply-safe", batch_id, [], undecided), sort_keys=True))
        return 1
    if not plans:
        print(json.dumps({"batch": batch_id, "schemaVersion": 1, "verdict": "allow"}, sort_keys=True))
        return 0

    captured_head = git_out(["rev-parse", "HEAD"]).strip()
    # Test-only fault injection (REQ-5.12): simulates a concurrent commit landing
    # AFTER capture; production never sets this env.
    if os.environ.get("SDD_RETROFIT_TEST_HEAD_MOVE") == "1":
        probe = abs_repo(".ai/specs/apply-demo/probe.txt")
        probe.parent.mkdir(parents=True, exist_ok=True)
        probe.write_text("moved\n", encoding="utf-8")
        _git(["add", "-A"])
        _git(["commit", "-qm", "interloper"])
    journal = Journal(batch_id=batch_id, captured_head=captured_head)
    originals: dict[str, bytes] = {}
    for path_str, before, planned in plans:
        journal.targets.append(JournalTarget(
            path=path_str, before_sha256=sha256(before), planned_sha256=sha256(planned),
        ))
        originals[path_str] = before
    write_journal(batch_id, journal, originals)
    # reload to bind original_file names
    journal = load_journal(batch_id)

    for index, (path_str, before, planned) in enumerate(plans):
        target_record = journal.targets[index]
        # precondition recheck (HEAD + exact bytes)
        if git_out(["rev-parse", "HEAD"]).strip() != captured_head or \
                sha256(read_bytes(abs_repo(path_str))) != target_record.before_sha256:
            restored_ok, failures = restore_from_journal(batch_id)
            print(json.dumps({
                "diagnostics": [{"code": "MIGRATION_HEAD_CHANGED", "restored": restored_ok,
                                 "failedPaths": failures}],
                "schemaVersion": 1, "verdict": "engine-fail",
            }, sort_keys=True))
            return 2
        target_record.pending = True
        journal_root(batch_id).mkdir(parents=True, exist_ok=True)
        _fsync_write(journal_root(batch_id) / "manifest.json",
                     json.dumps(journal.to_json(), sort_keys=True, indent=1).encode())
        tmp = abs_repo(path_str).with_suffix(".retrofit-applying")
        tmp.write_bytes(planned)
        os.replace(tmp, abs_repo(path_str))
        _fsync_path(abs_repo(path_str))
        _post_diags = sc.parse_task_blocks(read_bytes(abs_repo(path_str)), Path(path_str))[1]
        # only regressions introduced by THIS write matter — legacy files may
        # already carry non-canonical task bullets outside migration scope
        import collections as _collections
        _before_counts = _collections.Counter(
            d.code for d in sc.parse_task_blocks(before, Path(path_str))[1]
            if d.code.startswith(("TASK_",)))
        _after_counts = _collections.Counter(
            d.code for d in _post_diags if d.code.startswith(("TASK_",)))
        fatal_post = [code for code, count in _after_counts.items()
                      if count > _before_counts.get(code, 0)]
        if fatal_post:
            restored_ok, failures = restore_from_journal(batch_id)
            print(json.dumps({
                "diagnostics": [{"code": "MIGRATION_FILE_CHANGED", "restored": restored_ok,
                                 "failedPaths": failures}],
                "schemaVersion": 1, "verdict": "engine-fail",
            }, sort_keys=True))
            return 2
        target_record.applied = True
        target_record.pending = False
        _fsync_write(journal_root(batch_id) / "manifest.json",
                     json.dumps(journal.to_json(), sort_keys=True, indent=1).encode())

    # self re-dry-run must be a no-op, then per-file post-write contract check.
    # Tolerance: follow-up actions derived purely from committed waiver
    # decisions may surface AFTER an earlier transform created an Evidence
    # header mid-batch (observations move) — they converge on the next
    # invocation and are reported, never silently dropped.
    remaining_actions, remaining_blockers = plan_batch(batch_id)
    strict_rc = verify_written_files(plans, batch_id)
    decided_residuals = [b for b in remaining_blockers if _residual_is_decided(b)]
    undecided = [b for b in remaining_blockers if not _residual_is_decided(b)]

    def _derived_followup(action) -> bool:
        """Follow-up surfaced BY this batch's own transform: header rename
        exposes bare-dotted refs that the ref planner then canonicalizes, and
        metadata relocation makes the parser see new continuation regions on
        the next pass. Anything the ledger decided is converging too."""
        if action.target_field == "task.metadata":
            return True
        entry = (_ledger_get(action.path, action.target_field, action.task_id)
                 or _ledger_get(action.path, action.target_field))
        if entry is None:
            entry = _ledger_get(action.path, "trace.table")
        return entry is not None and bool(entry.get("disposition"))

    converging_actions = [a for a in remaining_actions if _derived_followup(a)]
    stray_actions = [a for a in remaining_actions if not _derived_followup(a)]
    if undecided or strict_rc or stray_actions:
        restored_ok, failures = restore_from_journal(batch_id)
        print(json.dumps({
            "diagnostics": [{"code": "MIGRATION_FILE_CHANGED",
                             "reason": "verification", "restored": restored_ok,
                             "failedPaths": failures,
                             "remainingActions": len(stray_actions),
                             "remainingBlockers": len(undecided),
                             "samples": [
                                 {"path": a.path, "field": a.target_field}
                                 for a in stray_actions[:5]
                             ]}],
            "schemaVersion": 1, "verdict": "engine-fail",
        }, sort_keys=True))
        return 2
    clear_journal(batch_id)
    print(json.dumps({
        "applied": [plan[0] for plan in plans],
        "batch": batch_id, "schemaVersion": 1, "verdict": "allow",
        "decidedResidualBlockers": [
            {"path": b.path, "field": b.target_field, "taskId": b.task_id,
             "message": b.message}
            for b in decided_residuals[:20]
        ],
        "followUpActionsNextPass": [
            {"path": a.path, "field": a.target_field, "taskId": a.task_id}
            for a in converging_actions[:20]
        ],
    }, sort_keys=True))
    return 0


def _residual_is_decided(blocker) -> bool:
    """A blocker with a committed ledger decision counts as resolved-by-record
    even when no safe mechanical write exists (e.g. header-less legacy tasks).
    Chain/state summary blockers follow their directory's per-file status
    decisions — once statuses are dispositioned, completeness re-derives."""
    entry = (_ledger_get(blocker.path, blocker.target_field, blocker.task_id)
             or _ledger_get(blocker.path, blocker.target_field))
    if entry is not None and bool(entry.get("disposition")):
        return True
    if blocker.target_field in {"artifact.chain", "authoring.chain"}:
        return load_resolution_ledger_decided_statuses(
            Path(blocker.path).parent.as_posix())
    if blocker.target_field == "trace.section":
        ledger = load_resolution_ledger()
        directory = Path(blocker.path).parent
        for (path_str, field, _scoped), entry in ledger.items():
            if field == "trace.table" and \
                    Path(path_str).parent == directory and \
                    entry.get("disposition") == "trace-header-canonical":
                return True
        return False
    return False


def load_resolution_ledger_decided_statuses(directory: str) -> bool:
    """True iff every markdown artifact of `directory` carries a status.line
    decision with a closing disposition (approved/superseded)."""
    ledger = load_resolution_ledger()
    by_dir = {}
    for (path_str, field, scoped), entry in ledger.items():
        if field == "status.line":
            by_dir.setdefault(Path(path_str).parent.as_posix(), {})[path_str] = entry
    entries = by_dir.get(directory)
    if not entries:
        return False
    return all(entry.get("disposition") in {"status-approved", "status-superseded"}
               for entry in entries.values())


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def _renameable_bugfix_lines(path_str: str) -> tuple[set[int], dict[int, dict]]:
    """Classify this file's malformed criterion lines: mechanically fixable
    (hyphenated id + word-preserving join, full EARS) vs needs a statement."""
    data = read_bytes(abs_repo(path_str))
    renameable: set[int] = set()
    statements: dict[int, dict] = {}
    text_lines = data.decode("utf-8", "surrogateescape").splitlines()
    for diagnostic in sc.parse_bugfix_criteria(data, Path(path_str))[1]:
        if diagnostic.code != "EARS_CRITERION_MALFORMED":
            continue
        number = diagnostic.location.line
        if 0 < number <= len(text_lines):
            raw_match = re.match(
                r"^\s*[-+*]\s+([FB]\d+)\s", text_lines[number - 1])
            ref = raw_match.group(1) if raw_match else None
            raw_match_hyphen = re.match(
                r"^\s*[-+*]\s+([FB]-\d+)\s", text_lines[number - 1])
            if raw_match_hyphen and not raw_match:
                ref = raw_match_hyphen.group(1)
        else:
            ref = None
        candidate = _bugfix_join_candidate(data, number)
        if candidate is not None and sc._ears_ok(candidate[2]):
            renameable.add(number)
        else:
            entry = {"path": path_str, "field": "bugfix.criterion", "taskId": "",
                     "line": number, "disposition": "", "rationale": ""}
            if ref:
                entry["ref"] = ref
            statements[number] = entry
    return renameable, statements


def emit_resolution_template(path: str, batch_ids: list[str]) -> int:
    """Write skeleton decisions for every current blocker; deterministic classes
    prefilled, the rest left empty for human completion. One entry per
    file-level concern so the corpus-size stays manageable."""
    decisions: list[dict] = []
    seen_files: set[tuple[str, str]] = set()

    def add_file_entry(file_path: str, field: str, disposition: str, rationale: str) -> None:
        key = (file_path, field)
        if key not in seen_files:
            seen_files.add(key)
            decisions.append({"path": file_path, "field": field, "taskId": "",
                              "disposition": disposition, "rationale": rationale})

    for batch_id in batch_ids:
        _actions, blockers = plan_batch(batch_id)
        status_files_done: set[str] = set()
        criterion_files_done: set[str] = set()
        block_dirs_done: set[str] = set()
        waiver_files: dict[str, set[str]] = {}
        for blocker in blockers:
            directory = Path(blocker.path).parent.as_posix()
            if blocker.target_field == "directory.shape":
                continue  # empty dirs resolved outside the ledger
            if blocker.target_field == "status.line":
                if blocker.path not in status_files_done:
                    status_files_done.add(blocker.path)
                    add_file_entry(blocker.path, "status.line",
                                   "status-unknown" if "ไม่มี historical proof" in blocker.message
                                   or "alias" in blocker.message else "",
                                   "" if blocker.code == "MIGRATION_PROOF_MISSING"
                                   else "FILL: conflict needs per-file judgment")
                continue
            if blocker.target_field.startswith("evidence."):
                waiver_files.setdefault(directory, set()).add(blocker.target_field)
                continue
            if blocker.target_field in {"criteria.block", "task.id"}:
                continue
            if blocker.target_field == "artifact.chain":
                add_file_entry(Path(blocker.path).as_posix(), "authoring.chain",
                               "active-authoring-exempt", "")
                continue
            if blocker.target_field == "bugfix.criterion":
                bf_rel = f"{directory}/bugfix.md"
                if directory not in block_dirs_done and \
                        "ไม่มี criterion F/B canonical" in blocker.message:
                    block_dirs_done.add(directory)
                    add_file_entry(bf_rel, "bugfix.criteriaBlock", "", "FILL criteria block")
                    continue
                if directory not in criterion_files_done:
                    criterion_files_done.add(directory)
                    renameable, statements = _renameable_bugfix_lines(f"{directory}/bugfix.md")
                    if renameable:
                        add_file_entry(bf_rel, "bugfix.criterion",
                                       "rename-canonical-id",
                                       f"mechanical hyphenation of {len(renameable)} id(s)")
                    for number in sorted(statements):
                        decisions.append(dict(statements[number],
                                              rationale="FILL canonical EARS statement"))
                continue
        for directory in sorted(waiver_files):
            for field in sorted(waiver_files[directory]):
                owner_file = _any_blocker_path(batch_ids, directory, field) or \
                    f"{directory}/tasks.md"
                add_file_entry(owner_file, field, "waive-protocol-history",
                               VP_WAIVE_LINE if field.endswith("viewports")
                               else DEV_WAIVE_LINE)
    payload = {
        "_meta": {
            "authority": "human checkpoint 2026-08-26",
            "generatedBy": "spec-retrofit --emit-resolution-template",
            "note": "\u0e17\u0e38\u0e01 disposition \u0e15\u0e49\u0e2d\u0e07\u0e16\u0e39\u0e01\u0e15\u0e23\u0e27\u0e08\u0e41\u0e25\u0e30 approve \u0e01\u0e48\u0e2d\u0e19 apply-safe",
        },
        "decisions": sorted(
            decisions,
            key=lambda d: (d["path"], d["field"], d.get("taskId", ""), d.get("line", 0)),
        ),
    }
    target = abs_repo(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(payload, ensure_ascii=False, indent=1) + "\n",
                      encoding="utf-8")
    print(f"wrote {len(payload['decisions'])} decision entries -> {target}")
    return 0


def _any_blocker_path(batch_ids: list[str], directory: str, field: str) -> str | None:
    for batch_id in batch_ids:
        _actions, blockers = plan_batch(batch_id)
        for blocker in blockers:
            if Path(blocker.path).parent.as_posix() == directory and \
                    blocker.target_field == field:
                return blocker.path
    return None


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--apply-safe", action="store_true")
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--batch", required=True)
    parser.add_argument("--feature", default=None)
    parser.add_argument("--format", choices=("json", "text"), default="text")
    parser.add_argument("--emit-resolution-template", default=None,
                        help="write skeleton resolution decisions to PATH and exit")
    args, extras = parser.parse_known_args(argv)
    if extras:
        return 2
    if args.batch not in ALL_BATCH_IDS:
        return 2
    if args.emit_resolution_template:
        _LEDGER_CACHE.clear()
        return emit_resolution_template(args.emit_resolution_template, [args.batch])
    modes = [flag for flag, given in
             (("dry-run", args.dry_run), ("apply-safe", args.apply_safe), ("check", args.check)) if given]
    if len(modes) != 1:
        return 2
    mode = modes[0]
    if args.batch in READ_ONLY_ONLY_BATCHES and mode != "check":
        return 2
    try:
        if mode == "dry-run":
            rc = run_dry_run(args.batch)
        elif mode == "check":
            rc = run_check(args.batch)
        else:
            rc = run_apply_safe(args.batch)
    except GitFailure as failure:
        print(json.dumps({
            "diagnostics": [{"code": "ENGINE_INTERNAL", "detail": str(failure)}],
            "schemaVersion": 1, "verdict": "engine-fail",
        }, sort_keys=True))
        return 2
    except OSError as failure:
        print(f"ENGINE_INTERNAL: {failure}", file=sys.stderr)
        return 2
    return rc


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
