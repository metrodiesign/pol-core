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
import contextlib
import ctypes
import errno
import fcntl
import hashlib
import io
import json
import os
import re
import secrets
import stat
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
if str(SCRIPTS) not in sys.path:
    sys.path.insert(0, str(SCRIPTS))

import spec_contract as sc  # noqa: E402

CURRENT_FEATURE = "sdd-operating-layer-parity"
ARCHIVE_CONTAINER = "archive"
ARTIFACT_FILES = ("requirements.md", "design.md", "tasks.md", "bugfix.md", "handoff.md")
CANONICAL_HISTORICAL_FEATURES = (
    "admin-account-management",
    "admin-actor-rename",
    "admin-console-real-api",
    "admin-merchant-provisioning-contract",
    "admin-oidc-session",
    "admin-role-rbac",
    "admin-workforce-jit",
    "api-route-scheme",
    "bugfix-host-test-tenant-pin",
    "bugfix-merchant-prebind-wiring",
    "bugfix-merchant-spa-runtime-origin",
    "bugfix-merchant-tier1-dev-oidc",
    "bugfix-offices-403",
    "bugfix-order-paid-link",
    "bugfix-producer-ticket-dedup",
    "bugfix-scalar-return-to",
    "bugfix-sim-guardrails",
    "captive-payment-alignment",
    "checkout-chain-document-fields",
    "console-auth-config-contract",
    "db-rename-vcentralpay",
    "demo-seed-data",
    "entra-scoped-preprovision",
    "enum-one-based-storage",
    "extended-login-session-lifetime",
    "external-sim-documentno-format",
    "external-sim-realistic-branch-codes",
    "external-sim-separate-containers",
    "external-sim-shared-agent-network",
    "foundation-scaffold",
    "frontend-real-api-integration",
    "hierarchical-naming",
    "host-test-config-precedence",
    "identity-rbac",
    "insurance-pivot",
    "local-api-port-5001",
    "masterdata-module",
    "masterdata-split",
    "merchant-commerce-erd-reset",
    "merchant-local-http-callback",
    "merchant-user-payment-method-access",
    "microsoft-oidc-ciam-alignment",
    "multi-tier-deployment",
    "openapi-documents",
    "policy-reference-record",
    "probe-dependency-failure-mapping",
    "producer-google-sso",
    "production-hardening",
    "products-external-source-of-truth",
    "products-sp-53-alignment",
    "products-sp-gateway",
    "purchase-flow-completion",
    "registration-attempt-history",
    "rf1-schema-reset",
    "rf2-iam-rbac",
    "rls-to-query-filter",
    "search-filter-sort",
    "sim-seed-date-stability",
    "system-completion",
    "tenant",
    "tier-0-microsoft-canonical-email",
)
HISTORICAL_FEATURES = CANONICAL_HISTORICAL_FEATURES
HISTORICAL_COUNT = len(HISTORICAL_FEATURES)
assert HISTORICAL_COUNT == 61
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


class EngineFailure(RuntimeError):
    pass


class MigrationFileChanged(EngineFailure):
    pass


class MigrationRecoveryFailure(EngineFailure):
    pass


class MigrationRecoveryRequired(EngineFailure):
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


def _unsafe_path(path: Path, detail: str) -> EngineFailure:
    try:
        display = path.relative_to(repo_root()).as_posix()
    except ValueError:
        display = path.as_posix()
    return EngineFailure(f"PATH_UNSAFE: {display}: {detail}")


def _repo_candidate(path: str | Path) -> Path:
    root = repo_root()
    raw = Path(path)
    candidate = raw if raw.is_absolute() else root / raw
    try:
        relative = candidate.relative_to(root)
    except ValueError as error:
        raise _unsafe_path(candidate, "path อยู่นอก resolved repo root") from error
    if ".." in relative.parts:
        raise _unsafe_path(candidate, "path traversal อยู่นอก canonical tree")
    return candidate


def _guard_repo_path(
    path: str | Path,
    *,
    leaf_kind: str,
    allow_missing_leaf: bool = False,
) -> Path:
    """ใช้ lstat ทุก component และไม่ตาม symlink ภายใต้ resolved repo root."""
    root = repo_root()
    candidate = _repo_candidate(path)
    try:
        root_stat = os.lstat(root)
    except OSError as error:
        raise _unsafe_path(root, f"resolved repo root ใช้งานไม่ได้: {error}") from error
    if stat.S_ISLNK(root_stat.st_mode) or not stat.S_ISDIR(root_stat.st_mode):
        raise _unsafe_path(root, "resolved repo root ต้องเป็น directory จริง")

    relative = candidate.relative_to(root)
    current = root
    for index, part in enumerate(relative.parts):
        current = current / part
        is_leaf = index == len(relative.parts) - 1
        try:
            current_stat = os.lstat(current)
        except FileNotFoundError:
            if is_leaf and allow_missing_leaf:
                break
            raise _unsafe_path(current, "path component ไม่มีอยู่")
        except OSError as error:
            raise _unsafe_path(current, f"lstat ไม่สำเร็จ: {error}") from error
        if stat.S_ISLNK(current_stat.st_mode):
            raise _unsafe_path(current, "path component เป็น symlink")
        if not is_leaf and not stat.S_ISDIR(current_stat.st_mode):
            raise _unsafe_path(current, "parent component ต้องเป็น directory")
        if is_leaf:
            valid_leaf = (
                leaf_kind == "directory" and stat.S_ISDIR(current_stat.st_mode)
                or leaf_kind == "file" and stat.S_ISREG(current_stat.st_mode)
            )
            if not valid_leaf:
                raise _unsafe_path(
                    current,
                    "leaf ต้องเป็น directory จริง" if leaf_kind == "directory"
                    else "leaf ต้องเป็น regular file",
                )

    try:
        candidate.resolve(strict=False).relative_to(root)
    except ValueError as error:
        raise _unsafe_path(candidate, "resolved path escape จาก repo root") from error
    return candidate


def _guard_repo_directory(path: str | Path, *, allow_missing: bool = False) -> Path:
    return _guard_repo_path(
        path, leaf_kind="directory", allow_missing_leaf=allow_missing
    )


def _guard_repo_file(path: str | Path, *, allow_missing: bool = False) -> Path:
    return _guard_repo_path(path, leaf_kind="file", allow_missing_leaf=allow_missing)


def _open_trusted_directory(
    path: Path,
    trusted_root: Path,
    *,
    create: bool = False,
    missing_ok: bool = False,
) -> int | None:
    """เปิด directory chain ด้วย dir fd + O_NOFOLLOW ใต้ trusted root เท่านั้น."""
    root = trusted_root.absolute()
    candidate = path.absolute()
    try:
        relative = candidate.relative_to(root)
    except ValueError as error:
        raise _unsafe_path(candidate, "directory escape จาก trusted root") from error

    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    parts = root.parts
    fd = os.open(parts[0], flags)
    try:
        for part in parts[1:]:
            next_fd = os.open(part, flags, dir_fd=fd)
            os.close(fd)
            fd = next_fd
        for part in relative.parts:
            try:
                next_fd = os.open(part, flags, dir_fd=fd)
            except FileNotFoundError:
                if not create:
                    if missing_ok:
                        os.close(fd)
                        return None
                    raise
                try:
                    os.mkdir(part, mode=0o700, dir_fd=fd)
                except FileExistsError:
                    pass
                next_fd = os.open(part, flags, dir_fd=fd)
            os.close(fd)
            fd = next_fd
        if not stat.S_ISDIR(os.fstat(fd).st_mode):
            raise _unsafe_path(candidate, "trusted directory ต้องเป็น directory จริง")
        return fd
    except BaseException:
        os.close(fd)
        raise


def _open_child_directory(
    parent_fd: int,
    name: str,
    *,
    create: bool = False,
    missing_ok: bool = False,
) -> int | None:
    if Path(name).name != name:
        raise EngineFailure("directory entry ต้องเป็น basename เท่านั้น")
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    try:
        return os.open(name, flags, dir_fd=parent_fd)
    except FileNotFoundError:
        if not create:
            if missing_ok:
                return None
            raise
        try:
            os.mkdir(name, mode=0o700, dir_fd=parent_fd)
        except FileExistsError:
            pass
        return os.open(name, flags, dir_fd=parent_fd)


def _entry_stat(directory_fd: int, name: str, *, allow_missing: bool = False) -> os.stat_result | None:
    if Path(name).name != name:
        raise EngineFailure("filesystem entry ต้องเป็น basename เท่านั้น")
    try:
        entry_stat = os.stat(name, dir_fd=directory_fd, follow_symlinks=False)
    except FileNotFoundError:
        if allow_missing:
            return None
        raise
    if stat.S_ISLNK(entry_stat.st_mode) or not stat.S_ISREG(entry_stat.st_mode):
        raise EngineFailure(f"filesystem entry ต้องเป็น regular file และห้ามเป็น symlink: {name}")
    return entry_stat


def _same_inode(left: os.stat_result, right: os.stat_result) -> bool:
    return (left.st_dev, left.st_ino) == (right.st_dev, right.st_ino)


_DISPOSABLE_DIRECTORY = "sdd-retrofit-disposables/v1"


def _disposable_root() -> Path:
    return git_dir() / _DISPOSABLE_DIRECTORY


def _mount_identity(directory_fd: int) -> tuple[str, int]:
    if sys.platform.startswith("linux"):
        statx = getattr(ctypes.CDLL(None, use_errno=True), "statx", None)
        if statx is None:
            raise MigrationRecoveryFailure(
                "DISPOSABLE_RETENTION_MOUNT_ID_UNAVAILABLE"
            )
        statx.argtypes = (
            ctypes.c_int,
            ctypes.c_char_p,
            ctypes.c_int,
            ctypes.c_uint,
            ctypes.c_void_p,
        )
        statx.restype = ctypes.c_int
        buffer = ctypes.create_string_buffer(256)
        ctypes.set_errno(0)
        if statx(
            directory_fd,
            b"",
            0x1000 | 0x4000,
            0x1000,
            ctypes.byref(buffer),
        ) != 0:
            error_number = ctypes.get_errno() or errno.EIO
            raise MigrationRecoveryFailure(
                f"DISPOSABLE_RETENTION_MOUNT_ID_UNAVAILABLE: {error_number}"
            )
        returned_mask = ctypes.c_uint32.from_buffer(buffer, 0).value
        if returned_mask & 0x1000 == 0:
            raise MigrationRecoveryFailure(
                "DISPOSABLE_RETENTION_MOUNT_ID_UNAVAILABLE"
            )
        return "linux-statx", ctypes.c_uint64.from_buffer(buffer, 144).value
    if sys.platform == "darwin":
        return "darwin-mount-device", os.fstat(directory_fd).st_dev
    raise MigrationRecoveryFailure(
        "DISPOSABLE_RETENTION_MOUNT_ID_UNAVAILABLE"
    )


def _require_disposable_retention_device(directory_fd: int) -> None:
    retention_path = git_dir()
    retention_fd = _open_trusted_directory(
        retention_path, retention_path
    )
    assert retention_fd is not None
    try:
        if _mount_identity(directory_fd) != _mount_identity(retention_fd):
            raise MigrationRecoveryFailure(
                "DISPOSABLE_RETENTION_CROSS_DEVICE"
            )
    finally:
        os.close(retention_fd)


def _claim_disposable_generation(operation: str) -> tuple[int, int]:
    base_fd = _open_trusted_directory(
        _disposable_root(), git_dir(), create=True
    )
    assert base_fd is not None
    root_fd: int | None = None
    lock_fd: int | None = None
    try:
        with _recovery_mutation_lock(base_fd, operation):
            token = f".retained-{secrets.token_hex(16)}"
            os.mkdir(token, mode=0o700, dir_fd=base_fd)
            os.fsync(base_fd)
            root_fd = os.open(
                token,
                os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                dir_fd=base_fd,
            )
            _ensure_private_directory(root_fd, operation)
            lock_fd = os.open(
                _OWNER_LOCK,
                os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                0o600,
                dir_fd=root_fd,
            )
            fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            os.fchmod(lock_fd, 0o600)
            os.fsync(lock_fd)
            os.fsync(root_fd)
        claimed = (root_fd, lock_fd)
        root_fd = None
        lock_fd = None
        return claimed
    finally:
        if lock_fd is not None:
            os.close(lock_fd)
        if root_fd is not None:
            os.close(root_fd)
        os.close(base_fd)


def _unlink_disposable_entry(
    directory_fd: int,
    name: str,
    expected: os.stat_result,
) -> None:
    _require_disposable_retention_device(directory_fd)
    if expected.st_nlink != 1:
        raise MigrationRecoveryFailure(
            f"disposable entry identity ไม่ตรง: {name}"
        )
    current = _entry_stat(directory_fd, name)
    assert current is not None
    if current.st_nlink != 1 or not _same_inode(current, expected):
        raise MigrationRecoveryFailure(
            f"disposable entry identity ไม่ตรง: {name}"
        )
    root_fd, lock_fd = _claim_disposable_generation(
        "disposable retention"
    )
    retired = False
    try:
        _atomic_rename_noreplace(
            directory_fd,
            name,
            "entry",
            target_directory_fd=root_fd,
        )
        os.fsync(directory_fd)
        os.fsync(root_fd)
        retained = _entry_stat(root_fd, "entry")
        assert retained is not None
        matched = retained.st_nlink == 1 and _same_inode(retained, expected)
        _retire_claimed_recovery_root(root_fd, lock_fd, "disposable")
        retired = True
        if not matched:
            raise MigrationRecoveryFailure(
                f"disposable entry foreign ถูกเก็บใน recovery: {name}"
            )
    except FileNotFoundError:
        raise
    except MigrationRecoveryFailure:
        raise
    except OSError as error:
        raise MigrationRecoveryFailure(
            f"disposable entry เก็บใน recovery ไม่สำเร็จ: {name}"
        ) from error
    finally:
        if root_fd is not None and lock_fd is not None and not retired:
            _retire_claimed_recovery_root(root_fd, lock_fd, "disposable-error")
        os.close(lock_fd)
        os.close(root_fd)


_RECOVERY_MUTATION_LOCK = ".mutation.lock"
_RETIRED_MARKER = ".retired-v1"
_HELD_RECOVERY_MUTATION_BASES: set[tuple[int, int]] = set()


def _recovery_entry_names(base_fd: int) -> list[str]:
    return [
        name for name in os.listdir(base_fd)
        if name != _RECOVERY_MUTATION_LOCK
    ]


def _ensure_private_directory(directory_fd: int, operation: str) -> None:
    directory = os.fstat(directory_fd)
    if not stat.S_ISDIR(directory.st_mode):
        raise MigrationRecoveryFailure(f"{operation}: recovery root ไม่ใช่ directory")
    if stat.S_IMODE(directory.st_mode) != 0o700:
        try:
            os.fchmod(directory_fd, 0o700)
            os.fsync(directory_fd)
        except OSError as error:
            raise MigrationRecoveryFailure(
                f"{operation}: ตั้ง recovery root mode 0700 ไม่สำเร็จ"
            ) from error


@contextlib.contextmanager
def _recovery_mutation_lock(base_fd: int, operation: str):
    _ensure_private_directory(base_fd, operation)
    base_stat = os.fstat(base_fd)
    base_identity = (base_stat.st_dev, base_stat.st_ino)
    if base_identity in _HELD_RECOVERY_MUTATION_BASES:
        yield
        return
    flags = os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW
    try:
        lock_fd = os.open(_RECOVERY_MUTATION_LOCK, flags, 0o600, dir_fd=base_fd)
    except OSError as error:
        raise MigrationRecoveryFailure(
            f"{operation}: parent mutation lock เปิดไม่ได้"
        ) from error
    try:
        try:
            entry = os.stat(
                _RECOVERY_MUTATION_LOCK,
                dir_fd=base_fd,
                follow_symlinks=False,
            )
            opened = os.fstat(lock_fd)
        except OSError as error:
            raise MigrationRecoveryFailure(
                f"{operation}: parent mutation lock ตรวจไม่ได้"
            ) from error
        if (
            stat.S_ISLNK(entry.st_mode)
            or not stat.S_ISREG(entry.st_mode)
            or entry.st_nlink != 1
            or not stat.S_ISREG(opened.st_mode)
            or opened.st_nlink != 1
            or not _same_inode(entry, opened)
        ):
            raise MigrationRecoveryFailure(
                f"{operation}: parent mutation lock ไม่ใช่ regular single-link file"
            )
        try:
            fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise MigrationRecoveryRequired("MIGRATION_RECOVERY_REQUIRED") from error
        os.fchmod(lock_fd, 0o600)
        os.fsync(lock_fd)
        _HELD_RECOVERY_MUTATION_BASES.add(base_identity)
        try:
            yield
        finally:
            _HELD_RECOVERY_MUTATION_BASES.discard(base_identity)
    finally:
        os.close(lock_fd)


def _require_claimed_directory(
    base_fd: int,
    name: str,
    claimed_fd: int,
    operation: str,
) -> None:
    try:
        basename = os.stat(name, dir_fd=base_fd, follow_symlinks=False)
        claimed = os.fstat(claimed_fd)
    except OSError as error:
        raise MigrationRecoveryFailure(f"{operation}: claimed basename ตรวจไม่ได้") from error
    if (
        stat.S_ISLNK(basename.st_mode)
        or not stat.S_ISDIR(basename.st_mode)
        or not stat.S_ISDIR(claimed.st_mode)
        or not _same_inode(basename, claimed)
    ):
        raise MigrationRecoveryFailure(f"{operation}: claimed basename identity ไม่ตรง")


def _retired_marker_state(claimed_fd: int) -> bool:
    try:
        entry = os.stat(
            _RETIRED_MARKER, dir_fd=claimed_fd, follow_symlinks=False
        )
    except FileNotFoundError:
        return False
    except OSError as error:
        raise MigrationRecoveryFailure("retirement marker ตรวจไม่ได้") from error
    if (
        stat.S_ISLNK(entry.st_mode)
        or not stat.S_ISREG(entry.st_mode)
        or entry.st_nlink != 1
        or entry.st_size != 0
        or stat.S_IMODE(entry.st_mode) != 0o600
    ):
        raise MigrationRecoveryFailure("retirement marker malformed")
    try:
        marker_fd = os.open(
            _RETIRED_MARKER, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=claimed_fd
        )
    except OSError as error:
        raise MigrationRecoveryFailure("retirement marker เปิดแบบ no-follow ไม่สำเร็จ") from error
    try:
        opened = os.fstat(marker_fd)
        if (
            not stat.S_ISREG(opened.st_mode)
            or opened.st_nlink != 1
            or opened.st_size != 0
            or stat.S_IMODE(opened.st_mode) != 0o600
            or not _same_inode(entry, opened)
        ):
            raise MigrationRecoveryFailure("retirement marker inode ไม่ตรง directory entry")
    finally:
        os.close(marker_fd)
    return True


def _validate_claimed_owner_lock(claimed_fd: int, owner_lock_fd: int) -> None:
    try:
        entry = os.stat(_OWNER_LOCK, dir_fd=claimed_fd, follow_symlinks=False)
        opened = os.fstat(owner_lock_fd)
    except OSError as error:
        raise MigrationRecoveryFailure("retirement owner lock ตรวจไม่ได้") from error
    if (
        stat.S_ISLNK(entry.st_mode)
        or not stat.S_ISREG(entry.st_mode)
        or entry.st_nlink != 1
        or not stat.S_ISREG(opened.st_mode)
        or opened.st_nlink != 1
        or not _same_inode(entry, opened)
    ):
        raise MigrationRecoveryFailure("retirement owner lock identity ไม่ตรง")
    try:
        fcntl.flock(owner_lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError as error:
        raise MigrationRecoveryRequired("MIGRATION_RECOVERY_REQUIRED") from error


def _retire_claimed_recovery_root(
    claimed_fd: int, owner_lock_fd: int | None, operation: str
) -> None:
    if not operation:
        raise MigrationRecoveryFailure("retirement operation ว่างไม่ได้")
    if owner_lock_fd is None and operation != "create-error":
        raise MigrationRecoveryFailure(
            "retirement ที่ไม่มี owner lock อนุญาตเฉพาะ create-error"
        )
    claimed = os.fstat(claimed_fd)
    if not stat.S_ISDIR(claimed.st_mode):
        raise MigrationRecoveryFailure("retirement root ไม่ใช่ claimed directory")
    acquired_lock_fd: int | None = None
    try:
        if owner_lock_fd is None:
            try:
                acquired_lock_fd = os.open(
                    _OWNER_LOCK,
                    os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                    0o600,
                    dir_fd=claimed_fd,
                )
            except FileExistsError:
                raise MigrationRecoveryFailure(
                    "retirement ที่ไม่มี owner lock พบ owner lock เดิม"
                )
            except OSError as error:
                raise MigrationRecoveryFailure(
                    "retirement owner lock สร้างไม่ได้"
                ) from error
            else:
                try:
                    fcntl.flock(
                        acquired_lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB
                    )
                    os.fchmod(acquired_lock_fd, 0o600)
                    os.fsync(acquired_lock_fd)
                except BlockingIOError as error:
                    raise MigrationRecoveryRequired(
                        "MIGRATION_RECOVERY_REQUIRED"
                    ) from error
            owner_lock_fd = acquired_lock_fd
        if owner_lock_fd is not None:
            _validate_claimed_owner_lock(claimed_fd, owner_lock_fd)
        if _retired_marker_state(claimed_fd):
            return
        _write_phase_hook("retire-before-marker")
        try:
            marker_fd = os.open(
                _RETIRED_MARKER,
                os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                0o600,
                dir_fd=claimed_fd,
            )
        except FileExistsError:
            if not _retired_marker_state(claimed_fd):
                raise MigrationRecoveryFailure("retirement marker publish ไม่สำเร็จ")
            return
        except OSError as error:
            raise MigrationRecoveryFailure("retirement marker create ไม่สำเร็จ") from error
        try:
            os.fchmod(marker_fd, 0o600)
            _write_phase_hook("retire-marker-entry")
            os.fsync(marker_fd)
            _write_phase_hook("retire-marker-fsync")
        finally:
            os.close(marker_fd)
        os.fsync(claimed_fd)
        _write_phase_hook("retire-directory-fsync")
        if not _retired_marker_state(claimed_fd):
            raise MigrationRecoveryFailure("retirement marker durability ยืนยันไม่ได้")
    finally:
        if acquired_lock_fd is not None:
            os.close(acquired_lock_fd)


def _read_regular_snapshot_at(
    directory_fd: int,
    name: str,
    *,
    require_single_link: bool = False,
) -> tuple[bytes, os.stat_result]:
    entry_stat = _entry_stat(directory_fd, name)
    assert entry_stat is not None
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=directory_fd)
    try:
        opened_stat = os.fstat(fd)
        if not stat.S_ISREG(opened_stat.st_mode) or not _same_inode(entry_stat, opened_stat):
            raise EngineFailure(f"filesystem entry ถูกสลับระหว่างเปิด: {name}")
        if require_single_link and opened_stat.st_nlink != 1:
            raise EngineFailure(f"filesystem entry ต้องเป็น regular file แบบ link เดียว: {name}")
        chunks: list[bytes] = []
        while chunk := os.read(fd, 1024 * 1024):
            chunks.append(chunk)
        final_stat = os.fstat(fd)
        if (
            not _same_inode(opened_stat, final_stat)
            or opened_stat.st_size != final_stat.st_size
            or opened_stat.st_mtime_ns != final_stat.st_mtime_ns
            or opened_stat.st_ctime_ns != final_stat.st_ctime_ns
        ):
            raise EngineFailure(f"filesystem entry เปลี่ยนระหว่างอ่าน: {name}")
        return b"".join(chunks), final_stat
    finally:
        os.close(fd)


def _read_regular_at(directory_fd: int, name: str) -> bytes:
    return _read_regular_snapshot_at(directory_fd, name)[0]


_WRITE_INTENT_SCHEMA = 3
_WRITE_INTENT_TOKEN_RE = re.compile(r"[0-9a-f]{32}")
_WRITE_INTENT_FILE = "intent.json"
_WRITE_INTENT_DIRECTORY = "sdd-retrofit-write-intents/v1"


@dataclass
class WriteIntentClaim:
    token: str
    base_fd: int
    root_fd: int
    lock_fd: int

    def close(self) -> None:
        for field_name in ("lock_fd", "root_fd", "base_fd"):
            fd = getattr(self, field_name)
            if fd >= 0:
                os.close(fd)
                setattr(self, field_name, -1)


def _write_intent_root() -> Path:
    return git_dir() / _WRITE_INTENT_DIRECTORY


def _write_phase_hook(phase: str) -> None:
    if os.environ.get("SDD_RETROFIT_TEST_STOP_PHASE") != phase:
        return
    try:
        ready_fd = int(os.environ["SDD_RETROFIT_TEST_READY_FD"])
        gate_fd = int(os.environ["SDD_RETROFIT_TEST_GATE_FD"])
    except (KeyError, ValueError) as error:
        raise EngineFailure("write phase test handshake ไม่สมบูรณ์") from error
    os.write(ready_fd, b"R")
    os.read(gate_fd, 1)


def _write_new_regular_at(
    directory_fd: int,
    name: str,
    payload: bytes,
    mode: int,
    *,
    before_write=None,
) -> tuple[int, os.stat_result]:
    fd = os.open(
        name,
        os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
        mode,
        dir_fd=directory_fd,
    )
    try:
        opened = os.fstat(fd)
        if not stat.S_ISREG(opened.st_mode) or opened.st_nlink != 1:
            raise EngineFailure(f"temporary entry ต้องเป็น regular file แบบ link เดียว: {name}")
        if before_write is not None:
            before_write(opened)
        view = memoryview(payload)
        while view:
            written = os.write(fd, view)
            if written <= 0:
                raise EngineFailure("atomic write ไม่คืบหน้า")
            view = view[written:]
        os.fchmod(fd, mode)
        os.fsync(fd)
        final = os.fstat(fd)
        if not _same_inode(opened, final) or final.st_nlink != 1:
            raise EngineFailure(f"temporary entry เปลี่ยนระหว่างเขียน: {name}")
        return fd, final
    except BaseException:
        os.close(fd)
        raise


def _atomic_exchange(directory_fd: int, left: str, right: str) -> None:
    if Path(left).name != left or Path(right).name != right:
        raise EngineFailure("atomic exchange รับเฉพาะ canonical basename")
    library = ctypes.CDLL(None, use_errno=True)
    if sys.platform == "darwin":
        symbol_name = "renameatx_np"
    elif sys.platform.startswith("linux"):
        symbol_name = "renameat2"
    else:
        raise EngineFailure(f"ATOMIC_EXCHANGE_UNSUPPORTED: platform={sys.platform}")
    try:
        exchange = getattr(library, symbol_name)
    except AttributeError as error:
        raise EngineFailure(f"ATOMIC_EXCHANGE_UNSUPPORTED: missing {symbol_name}") from error
    exchange.argtypes = (
        ctypes.c_int,
        ctypes.c_char_p,
        ctypes.c_int,
        ctypes.c_char_p,
        ctypes.c_uint,
    )
    exchange.restype = ctypes.c_int
    ctypes.set_errno(0)
    if exchange(
        directory_fd,
        os.fsencode(left),
        directory_fd,
        os.fsencode(right),
        0x2,
    ) != 0:
        error_number = ctypes.get_errno() or errno.EIO
        raise OSError(error_number, os.strerror(error_number))


def _atomic_rename_noreplace(
    directory_fd: int,
    source: str,
    target: str,
    *,
    target_directory_fd: int | None = None,
) -> None:
    if Path(source).name != source or Path(target).name != target:
        raise EngineFailure("atomic no-replace รับเฉพาะ canonical basename")
    library = ctypes.CDLL(None, use_errno=True)
    if sys.platform == "darwin":
        symbol_name = "renameatx_np"
        flags = 0x4
    elif sys.platform.startswith("linux"):
        symbol_name = "renameat2"
        flags = 0x1
    else:
        raise EngineFailure(f"ATOMIC_NOREPLACE_UNSUPPORTED: platform={sys.platform}")
    try:
        rename_noreplace = getattr(library, symbol_name)
    except AttributeError as error:
        raise EngineFailure(
            f"ATOMIC_NOREPLACE_UNSUPPORTED: missing {symbol_name}"
        ) from error
    rename_noreplace.argtypes = (
        ctypes.c_int,
        ctypes.c_char_p,
        ctypes.c_int,
        ctypes.c_char_p,
        ctypes.c_uint,
    )
    rename_noreplace.restype = ctypes.c_int
    ctypes.set_errno(0)
    if rename_noreplace(
        directory_fd,
        os.fsencode(source),
        directory_fd if target_directory_fd is None else target_directory_fd,
        os.fsencode(target),
        flags,
    ) != 0:
        error_number = ctypes.get_errno() or errno.EIO
        raise OSError(error_number, os.strerror(error_number))


def _probe_atomic_exchange(directory_fd: int) -> None:
    _require_disposable_retention_device(directory_fd)
    root_fd, lock_fd = _claim_disposable_generation(
        "atomic exchange probe"
    )
    left = "probe-a"
    right = "probe-b"
    left_fd: int | None = None
    right_fd: int | None = None
    retired = False
    try:
        left_fd, left_stat = _write_new_regular_at(
            root_fd, left, b"a", 0o600
        )
        right_fd, right_stat = _write_new_regular_at(
            root_fd, right, b"b", 0o600
        )
        try:
            _atomic_exchange(root_fd, left, right)
            exchanged_left = os.stat(
                left, dir_fd=root_fd, follow_symlinks=False
            )
            exchanged_right = os.stat(
                right, dir_fd=root_fd, follow_symlinks=False
            )
            if not _same_inode(exchanged_left, right_stat) or not _same_inode(
                exchanged_right, left_stat
            ):
                raise EngineFailure("ATOMIC_EXCHANGE_UNSUPPORTED: probe identity mismatch")
            _atomic_exchange(root_fd, left, right)
            restored_left = os.stat(
                left, dir_fd=root_fd, follow_symlinks=False
            )
            restored_right = os.stat(
                right, dir_fd=root_fd, follow_symlinks=False
            )
            if not _same_inode(restored_left, left_stat) or not _same_inode(
                restored_right, right_stat
            ):
                raise EngineFailure("ATOMIC_EXCHANGE_UNSUPPORTED: probe restore mismatch")
        except (EngineFailure, OSError) as error:
            raise EngineFailure(
                f"ATOMIC_EXCHANGE_UNSUPPORTED: {error}"
            ) from error
        os.fsync(root_fd)
        _retire_claimed_recovery_root(
            root_fd, lock_fd, "atomic-exchange-probe"
        )
        retired = True
    finally:
        if left_fd is not None:
            os.close(left_fd)
        if right_fd is not None:
            os.close(right_fd)
        try:
            if not retired:
                _retire_claimed_recovery_root(
                    root_fd, lock_fd, "atomic-exchange-probe-error"
                )
        finally:
            os.close(lock_fd)
            os.close(root_fd)


def _open_owner_lock(directory_fd: int) -> int:
    try:
        entry = os.stat(_OWNER_LOCK, dir_fd=directory_fd, follow_symlinks=False)
    except (FileNotFoundError, OSError) as error:
        raise MigrationRecoveryFailure("owner lock ไม่มีหรือเปิดไม่ได้") from error
    if stat.S_ISLNK(entry.st_mode) or not stat.S_ISREG(entry.st_mode) or entry.st_nlink != 1:
        raise MigrationRecoveryFailure("owner lock ต้องเป็น regular file แบบ link เดียว")
    try:
        lock_fd = os.open(_OWNER_LOCK, os.O_RDWR | os.O_NOFOLLOW, dir_fd=directory_fd)
    except OSError as error:
        raise MigrationRecoveryFailure("owner lock เปิดแบบ no-follow ไม่สำเร็จ") from error
    opened = os.fstat(lock_fd)
    if not stat.S_ISREG(opened.st_mode) or opened.st_nlink != 1 or not _same_inode(
        entry, opened
    ):
        os.close(lock_fd)
        raise MigrationRecoveryFailure("owner lock inode ไม่ตรง directory entry")
    try:
        fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError as error:
        os.close(lock_fd)
        raise MigrationRecoveryRequired("MIGRATION_RECOVERY_REQUIRED") from error
    return lock_fd


def _intent_locator(anchor: str, path: str) -> tuple[Path, str]:
    if anchor not in {"repo", "git"}:
        raise MigrationRecoveryFailure("write intent anchor ไม่รู้จัก")
    relative = Path(path)
    if (
        not path
        or relative.is_absolute()
        or ".." in relative.parts
        or relative.as_posix() != path
        or relative.name in {"", ".", ".."}
    ):
        raise MigrationRecoveryFailure("write intent path ไม่เป็น canonical relative path")
    root = repo_root() if anchor == "repo" else git_dir()
    return root / relative.parent, relative.name


def _create_write_intent(
    *,
    directory_fd: int,
    anchor: str,
    path: str,
    expected_payload: bytes | None,
    expected_stat: os.stat_result | None,
    planned_payload: bytes,
    mode: int,
) -> tuple[WriteIntentClaim, str, os.stat_result]:
    _intent_locator(anchor, path)
    expected_missing = expected_stat is None
    if expected_missing != (expected_payload is None):
        raise EngineFailure("write intent expected snapshot ไม่สมบูรณ์")
    token = secrets.token_hex(16)
    swap_name = f".sdd-retrofit-swap-{secrets.token_hex(16)}"
    base_fd = _open_trusted_directory(_write_intent_root(), git_dir(), create=True)
    assert base_fd is not None
    root_fd: int | None = None
    lock_fd: int | None = None
    planned_stat: os.stat_result | None = None
    swap_created = False
    created = False
    intent_published = False
    claim: WriteIntentClaim | None = None
    mutation_lock = _recovery_mutation_lock(base_fd, "write intent create")
    mutation_lock_entered = False
    try:
        mutation_lock.__enter__()
        mutation_lock_entered = True
        os.mkdir(token, mode=0o700, dir_fd=base_fd)
        created = True
        os.fsync(base_fd)
        root_fd = os.open(
            token,
            os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
            dir_fd=base_fd,
        )
        _ensure_private_directory(root_fd, "write intent create")
        lock_fd = os.open(
            _OWNER_LOCK,
            os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
            0o600,
            dir_fd=root_fd,
        )
        fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        os.fchmod(lock_fd, 0o600)
        os.fsync(lock_fd)
        os.fsync(root_fd)

        def publish_intent(opened: os.stat_result) -> None:
            nonlocal intent_published, mutation_lock, mutation_lock_entered, planned_stat
            assert root_fd is not None
            planned_stat = opened
            intent_payload = (json.dumps({
                "anchor": anchor,
                "expectedDevice": None if expected_missing else expected_stat.st_dev,
                "expectedInode": None if expected_missing else expected_stat.st_ino,
                "expectedMissing": expected_missing,
                "expectedSha256": None if expected_missing else sha256(expected_payload),
                "path": path,
                "plannedDevice": opened.st_dev,
                "plannedInode": opened.st_ino,
                "plannedSha256": sha256(planned_payload),
                "schemaVersion": _WRITE_INTENT_SCHEMA,
                "swapName": swap_name,
            }, sort_keys=True, separators=(",", ":")) + "\n").encode()
            intent_fd, _intent_stat = _write_new_regular_at(
                root_fd, _WRITE_INTENT_FILE, intent_payload, 0o600
            )
            os.close(intent_fd)
            os.fsync(root_fd)
            os.fsync(base_fd)
            intent_published = True
            mutation_lock.__exit__(None, None, None)
            mutation_lock_entered = False
            mutation_lock = None

        swap_created = True
        planned_fd, planned_stat = _write_new_regular_at(
            directory_fd,
            swap_name,
            planned_payload,
            mode,
            before_write=publish_intent,
        )
        os.close(planned_fd)
        os.fsync(directory_fd)
        claim = WriteIntentClaim(token, base_fd, root_fd, lock_fd)
        base_fd = -1
        root_fd = None
        lock_fd = None
        _write_phase_hook("planned-fsync")
        _write_phase_hook("intent-fsync")
        return claim, swap_name, planned_stat
    except BaseException:
        if mutation_lock_entered:
            mutation_lock.__exit__(*sys.exc_info())
            mutation_lock_entered = False
            mutation_lock = None
        if not intent_published and created and root_fd is not None:
            _retire_claimed_recovery_root(root_fd, lock_fd, "create-error")
        if not intent_published and swap_created and planned_stat is not None:
            try:
                _unlink_disposable_entry(
                    directory_fd, swap_name, planned_stat
                )
                os.fsync(directory_fd)
            except (FileNotFoundError, EngineFailure, OSError):
                pass
        if claim is not None:
            claim.close()
        if lock_fd is not None:
            os.close(lock_fd)
        if root_fd is not None:
            os.close(root_fd)
        raise
    finally:
        if mutation_lock_entered:
            mutation_lock.__exit__(None, None, None)
        if base_fd >= 0:
            os.close(base_fd)


def _delete_write_intent(claim: WriteIntentClaim, operation: str) -> None:
    if claim.root_fd < 0 or claim.lock_fd < 0:
        raise MigrationRecoveryFailure("write intent claim ถูกปิดก่อน retirement")
    _retire_claimed_recovery_root(claim.root_fd, claim.lock_fd, operation)


def write_intents_pending() -> bool:
    base_fd = _open_trusted_directory(
        _write_intent_root(), git_dir(), missing_ok=True
    )
    if base_fd is None:
        return False
    try:
        pending = False
        for token in sorted(_recovery_entry_names(base_fd)):
            if _WRITE_INTENT_TOKEN_RE.fullmatch(token) is None:
                raise MigrationRecoveryFailure("write intent token ไม่เป็น canonical basename")
            try:
                root_fd = os.open(
                    token,
                    os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                    dir_fd=base_fd,
                )
            except OSError as error:
                raise MigrationRecoveryFailure(
                    "write intent root ไม่ใช่ trusted directory"
                ) from error
            lock_fd: int | None = None
            try:
                if not _retired_marker_state(root_fd):
                    _load_preintent_state(root_fd)
                    lock_fd = _open_structural_owner_lock(root_fd)
                    pending = True
            finally:
                if lock_fd is not None:
                    os.close(lock_fd)
                os.close(root_fd)
        return pending
    finally:
        os.close(base_fd)


def _load_write_intent(root_fd: int) -> dict[str, object]:
    try:
        payload, _payload_stat = _read_regular_snapshot_at(
            root_fd, _WRITE_INTENT_FILE, require_single_link=True
        )
        intent = json.loads(payload.decode("utf-8"))
    except (EngineFailure, OSError, UnicodeError, json.JSONDecodeError) as error:
        raise MigrationRecoveryFailure(f"write intent validation failed: {error}") from error
    required = {
        "anchor", "expectedDevice", "expectedInode", "expectedMissing",
        "expectedSha256", "path", "plannedDevice", "plannedInode",
        "plannedSha256", "schemaVersion", "swapName",
    }
    if not isinstance(intent, dict) or set(intent) != required:
        raise MigrationRecoveryFailure("write intent fields ไม่ตรง schema")
    if intent["schemaVersion"] != _WRITE_INTENT_SCHEMA:
        raise MigrationRecoveryFailure("write intent schemaVersion ไม่รองรับ")
    if type(intent["expectedMissing"]) is not bool:
        raise MigrationRecoveryFailure("write intent expectedMissing ไม่ถูกต้อง")
    for field_name in ("plannedDevice", "plannedInode"):
        if type(intent[field_name]) is not int or intent[field_name] < 0:
            raise MigrationRecoveryFailure(f"write intent {field_name} ไม่ถูกต้อง")
    if intent["expectedMissing"]:
        if any(intent[field_name] is not None for field_name in (
            "expectedDevice", "expectedInode", "expectedSha256"
        )):
            raise MigrationRecoveryFailure("write intent missing snapshot ต้องเป็น null")
    else:
        for field_name in ("expectedDevice", "expectedInode"):
            if type(intent[field_name]) is not int or intent[field_name] < 0:
                raise MigrationRecoveryFailure(f"write intent {field_name} ไม่ถูกต้อง")
        if not isinstance(intent["expectedSha256"], str) or re.fullmatch(
            r"[0-9a-f]{64}", intent["expectedSha256"]
        ) is None:
            raise MigrationRecoveryFailure("write intent expectedSha256 ไม่ถูกต้อง")
    if not isinstance(intent["plannedSha256"], str) or re.fullmatch(
        r"[0-9a-f]{64}", intent["plannedSha256"]
    ) is None:
        raise MigrationRecoveryFailure("write intent plannedSha256 ไม่ถูกต้อง")
    if not isinstance(intent["anchor"], str) or not isinstance(intent["path"], str):
        raise MigrationRecoveryFailure("write intent locator ไม่ถูกต้อง")
    _intent_locator(intent["anchor"], intent["path"])
    if not isinstance(intent["swapName"], str) or Path(intent["swapName"]).name != intent[
        "swapName"
    ]:
        raise MigrationRecoveryFailure("write intent swapName ไม่ถูกต้อง")
    return intent


def _intent_entry_state(
    directory_fd: int,
    name: str,
    intent: dict[str, object],
) -> tuple[str, bytes | None, os.stat_result | None]:
    try:
        payload, entry = _read_regular_snapshot_at(
            directory_fd, name, require_single_link=True
        )
    except FileNotFoundError:
        return "missing", None, None
    except (EngineFailure, OSError) as error:
        raise MigrationRecoveryFailure(f"write intent entry ไม่ปลอดภัย: {name}: {error}") from error
    digest = sha256(payload)
    for state in ("expected", "planned"):
        if intent[f"{state}Sha256"] is not None and (
            digest == intent[f"{state}Sha256"]
            and entry.st_dev == intent[f"{state}Device"]
            and entry.st_ino == intent[f"{state}Inode"]
        ):
            return state, payload, entry
    return "foreign", payload, entry


def _snapshot_matches(
    payload: bytes | None,
    entry: os.stat_result | None,
    expected_payload: bytes | None,
    expected_entry: os.stat_result | None,
) -> bool:
    return (
        payload is not None
        and entry is not None
        and expected_payload is not None
        and expected_entry is not None
        and payload == expected_payload
        and _same_inode(entry, expected_entry)
    )


def _reconcile_one_write_intent(
    claim: WriteIntentClaim,
    intent: dict[str, object] | None,
) -> None:
    if intent is None:
        _delete_write_intent(claim, "uncommitted")
        return
    parent, name = _intent_locator(str(intent["anchor"]), str(intent["path"]))
    trusted_root = repo_root() if intent["anchor"] == "repo" else git_dir()
    directory_fd = _open_trusted_directory(parent, trusted_root)
    assert directory_fd is not None
    swap_name = str(intent["swapName"])
    try:
        canonical = _intent_entry_state(directory_fd, name, intent)
        swap = _intent_entry_state(directory_fd, swap_name, intent)
        state = (canonical[0], swap[0])
        if bool(intent["expectedMissing"]):
            if state == ("missing", "planned"):
                try:
                    _atomic_rename_noreplace(directory_fd, swap_name, name)
                except FileExistsError as error:
                    raise MigrationFileChanged(f"MIGRATION_FILE_CHANGED: {name}") from error
                os.fsync(directory_fd)
                _delete_write_intent(claim, "committed")
                return
            if state == ("planned", "missing"):
                _delete_write_intent(claim, "committed")
                return
            if state == ("foreign", "planned"):
                assert swap[2] is not None
                _unlink_disposable_entry(directory_fd, swap_name, swap[2])
                os.fsync(directory_fd)
                _delete_write_intent(claim, "foreign-conflict")
                raise MigrationFileChanged(f"MIGRATION_FILE_CHANGED: {name}")
            raise MigrationRecoveryFailure(
                f"missing write intent state กำกวม: canonical={state[0]} swap={state[1]}"
            )

        if state == ("expected", "planned"):
            assert swap[2] is not None
            _unlink_disposable_entry(directory_fd, swap_name, swap[2])
            os.fsync(directory_fd)
            _delete_write_intent(claim, "uncommitted")
            return
        if state == ("planned", "expected"):
            assert swap[2] is not None
            _unlink_disposable_entry(directory_fd, swap_name, swap[2])
            os.fsync(directory_fd)
            _delete_write_intent(claim, "committed")
            return
        if state == ("planned", "missing"):
            _delete_write_intent(claim, "committed")
            return
        if state == ("expected", "missing"):
            _delete_write_intent(claim, "uncommitted")
            return
        if state == ("planned", "foreign"):
            _atomic_exchange(directory_fd, name, swap_name)
            os.fsync(directory_fd)
            restored = _intent_entry_state(directory_fd, name, intent)
            planned_swap = _intent_entry_state(directory_fd, swap_name, intent)
            if not _snapshot_matches(
                restored[1], restored[2], swap[1], swap[2]
            ) or planned_swap[0] != "planned":
                raise MigrationRecoveryFailure("write intent atomic swap-back ยืนยันไม่ได้")
            assert planned_swap[2] is not None
            _unlink_disposable_entry(directory_fd, swap_name, planned_swap[2])
            os.fsync(directory_fd)
            _delete_write_intent(claim, "foreign-conflict")
            raise MigrationFileChanged(f"MIGRATION_FILE_CHANGED: {name}")
        if state == ("foreign", "planned"):
            assert swap[2] is not None
            _unlink_disposable_entry(directory_fd, swap_name, swap[2])
            os.fsync(directory_fd)
            _delete_write_intent(claim, "foreign-conflict")
            raise MigrationFileChanged(f"MIGRATION_FILE_CHANGED: {name}")
        raise MigrationRecoveryFailure(
            f"write intent state กำกวม: canonical={state[0]} swap={state[1]}"
        )
    finally:
        os.close(directory_fd)


def _reconcile_write_intents() -> None:
    with _preflight_recovery_state() as recovery:
        _reconcile_claimed_write_intents(recovery.write_intents)


def _atomic_write_at(
    directory_fd: int,
    name: str,
    payload: bytes,
    *,
    default_mode: int = 0o666,
    expected_sha256: str | None = None,
    expected_missing: bool = False,
    intent_anchor: str | None = None,
    intent_path: str | None = None,
) -> None:
    """ติดตั้ง payload แบบ no-clobber หรือ crash-consistent existing-file exchange."""
    if intent_anchor is None or intent_path is None:
        raise EngineFailure("atomic writer ต้องมี durable intent locator")
    _require_disposable_retention_device(directory_fd)
    existing = _entry_stat(directory_fd, name, allow_missing=True)
    existing_payload: bytes | None = None
    if existing is not None:
        if expected_missing:
            raise MigrationRecoveryRequired("MIGRATION_RECOVERY_REQUIRED")
        existing_payload, opened = _read_regular_snapshot_at(
            directory_fd, name, require_single_link=True
        )
        if not _same_inode(existing, opened):
            raise MigrationFileChanged(f"MIGRATION_FILE_CHANGED: {name}")
        observed_sha256 = sha256(existing_payload)
        if expected_sha256 is not None and observed_sha256 != expected_sha256:
            raise MigrationFileChanged(f"MIGRATION_FILE_CHANGED: {name}")
        expected_sha256 = observed_sha256
    elif expected_sha256 is not None:
        raise MigrationFileChanged(f"MIGRATION_FILE_CHANGED: {name}")

    mode = stat.S_IMODE(existing.st_mode) if existing is not None else default_mode
    if existing is not None:
        _probe_atomic_exchange(directory_fd)
    intent_claim, swap_name, planned_stat = _create_write_intent(
        directory_fd=directory_fd,
        anchor=intent_anchor,
        path=intent_path,
        expected_payload=existing_payload,
        expected_stat=existing,
        planned_payload=payload,
        mode=mode,
    )
    try:
        intent = _load_write_intent(intent_claim.root_fd)
        if existing is None:
            try:
                _atomic_rename_noreplace(directory_fd, swap_name, name)
            except FileExistsError as error:
                _unlink_disposable_entry(directory_fd, swap_name, planned_stat)
                os.fsync(directory_fd)
                _delete_write_intent(intent_claim, "foreign-conflict")
                raise MigrationFileChanged(f"MIGRATION_FILE_CHANGED: {name}") from error
            _write_phase_hook("no-clobber-publish")
            canonical = _intent_entry_state(directory_fd, name, intent)
            if canonical[0] != "planned":
                raise MigrationRecoveryFailure("no-clobber publication identity ไม่ตรง")
            os.fsync(directory_fd)
            _delete_write_intent(intent_claim, "committed")
            return

        assert existing_payload is not None
        current_payload, current_stat = _read_regular_snapshot_at(
            directory_fd, name, require_single_link=True
        )
        if not _same_inode(current_stat, existing) or sha256(current_payload) != expected_sha256:
            _unlink_disposable_entry(directory_fd, swap_name, planned_stat)
            os.fsync(directory_fd)
            _delete_write_intent(intent_claim, "uncommitted")
            raise MigrationFileChanged(f"MIGRATION_FILE_CHANGED: {name}")

        _atomic_exchange(directory_fd, name, swap_name)
        _write_phase_hook("exchange")
        canonical = _intent_entry_state(directory_fd, name, intent)
        displaced = _intent_entry_state(directory_fd, swap_name, intent)
        if canonical[0] != "planned":
            raise MigrationRecoveryFailure("existing-file exchange ไม่ได้ planned canonical")
        if displaced[0] == "foreign":
            _atomic_exchange(directory_fd, name, swap_name)
            os.fsync(directory_fd)
            restored = _read_regular_snapshot_at(
                directory_fd, name, require_single_link=True
            )
            planned_swap = _intent_entry_state(directory_fd, swap_name, intent)
            if not _snapshot_matches(
                restored[0], restored[1], displaced[1], displaced[2]
            ) or planned_swap[0] != "planned":
                raise MigrationRecoveryFailure("foreign swap-back ยืนยันไม่ได้")
            assert planned_swap[2] is not None
            _unlink_disposable_entry(directory_fd, swap_name, planned_swap[2])
            os.fsync(directory_fd)
            _delete_write_intent(intent_claim, "foreign-conflict")
            raise MigrationFileChanged(f"MIGRATION_FILE_CHANGED: {name}")
        if displaced[0] != "expected":
            raise MigrationRecoveryFailure("existing-file displaced entry ไม่ใช่ expected")
        assert displaced[2] is not None
        os.fsync(directory_fd)
        _write_phase_hook("directory-fsync")
        _unlink_disposable_entry(directory_fd, swap_name, displaced[2])
        _write_phase_hook("displaced-unlink")
        os.fsync(directory_fd)
        _delete_write_intent(intent_claim, "committed")
    finally:
        intent_claim.close()


def _repo_parent_fd(path: str | Path, *, create: bool = False) -> tuple[Path, int]:
    target = _repo_candidate(path)
    parent_fd = _open_trusted_directory(target.parent, repo_root(), create=create)
    assert parent_fd is not None
    return target, parent_fd


def _atomic_write_repo_file(
    path: str | Path,
    payload: bytes,
    *,
    create_parents: bool = False,
    expected_sha256: str | None = None,
) -> Path:
    target, parent_fd = _repo_parent_fd(path, create=create_parents)
    try:
        _atomic_write_at(
            parent_fd,
            target.name,
            payload,
            expected_sha256=expected_sha256,
            intent_anchor="repo",
            intent_path=target.relative_to(repo_root()).as_posix(),
        )
    except MigrationFileChanged as error:
        raise MigrationFileChanged(f"MIGRATION_FILE_CHANGED: {rel(target)}") from error
    finally:
        os.close(parent_fd)
    return target


def abs_repo(path_str: str) -> Path:
    return _repo_candidate(path_str)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def rel(path: Path) -> str:
    try:
        return path.relative_to(repo_root()).as_posix()
    except ValueError:
        return path.as_posix()


def read_bytes(path: Path) -> bytes:
    target, parent_fd = _repo_parent_fd(path)
    try:
        return _read_regular_at(parent_fd, target.name)
    finally:
        os.close(parent_fd)


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


@dataclass(frozen=True)
class HistoricalMembership:
    expected: tuple[str, ...]
    present: tuple[str, ...]
    missing: tuple[str, ...]
    outside_scope: tuple[str, ...]
    inventory: tuple[str, ...]


def historical_membership() -> HistoricalMembership:
    """เทียบ direct all-spec inventory กับ canonical named set ที่ตรึงไว้."""
    root = specs_root()
    ai_root = repo_root() / ".ai"
    _guard_repo_directory(ai_root, allow_missing=True)
    if not ai_root.exists():
        entries = ()
    else:
        _guard_repo_directory(root, allow_missing=True)
        entries = sc._all_spec_directories(root) if root.exists() else ()
    expected = tuple(HISTORICAL_FEATURES)
    if root.exists():
        _guard_repo_directory(root / CURRENT_FEATURE, allow_missing=True)
        for feature in expected:
            _guard_repo_directory(root / feature, allow_missing=True)
    inventory = tuple(path.name for path in entries)
    regular_directories = {
        path.name for path in entries if not path.is_symlink() and path.is_dir()
    }
    expected_set = set(expected)
    present = tuple(feature for feature in expected if feature in regular_directories)
    missing = tuple(feature for feature in expected if feature not in regular_directories)
    outside = tuple(sorted(set(inventory) - expected_set - {CURRENT_FEATURE}))
    return HistoricalMembership(expected, present, missing, outside, inventory)


def historical_directories() -> tuple[Path, ...]:
    """คืนเฉพาะ canonical historical directories ที่มีอยู่จริง."""
    membership = historical_membership()
    return tuple(specs_root() / feature for feature in HISTORICAL_FEATURES
                 if feature in membership.present)


def feature_files(directory: Path) -> list[Path]:
    _guard_repo_directory(directory)
    files: list[Path] = []
    for name in ARTIFACT_FILES:
        path = directory / name
        try:
            os.lstat(path)
        except FileNotFoundError:
            continue
        files.append(_guard_repo_file(path))
    return files


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
    ai_root = _guard_repo_directory(repo_root() / ".ai", allow_missing=True)
    if not ai_root.exists():
        return {}
    specs = _guard_repo_directory(specs_root(), allow_missing=True)
    if not specs.exists():
        return {}
    owner = path.parent
    _guard_repo_directory(owner, allow_missing=True)
    if not owner.exists():
        return {}
    safe_path = _guard_repo_file(path, allow_missing=True)
    stamp = f"{safe_path}:{os.lstat(safe_path).st_mtime_ns if safe_path.exists() else 0}"
    cached = _LEDGER_CACHE.get(stamp)
    if cached is not None:
        return cached[1]
    ledger: dict[tuple[str, str, str], dict] = {}
    if safe_path.exists():
        try:
            payload = json.loads(read_bytes(safe_path).decode("utf-8"))
            decisions = payload["decisions"] if "decisions" in payload else []
            if not isinstance(payload, dict) or not isinstance(decisions, list):
                raise TypeError("root หรือ decisions มีรูปแบบไม่ถูกต้อง")
            for index, entry in enumerate(decisions):
                if not isinstance(entry, dict):
                    raise TypeError(f"decision {index + 1} ต้องเป็น object")
                scoped_id = entry.get("taskId", "")
                if not scoped_id and entry.get("line"):
                    scoped_id = f"@{entry['line']}"  # line-scoped decision
                key = (entry["path"], entry["field"], scoped_id)
                if key in ledger:
                    raise EngineFailure(f"resolution ledger duplicate entry {key}")
                if entry["disposition"] not in LEDGER_DISPOSITIONS:
                    raise EngineFailure(
                        f"resolution ledger unknown disposition {entry['disposition']}"
                    )
                item = dict(entry)
                item["_line"] = index + 1
                ledger[key] = item
        except EngineFailure:
            raise
        except (KeyError, TypeError, UnicodeDecodeError, ValueError) as error:
            raise EngineFailure(f"resolution ledger malformed: {error}") from error
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
        raise EngineFailure(f"resolution ledger bad date for {path_str}: {date!r}")
    if disposition == "status-approved":
        return f"> Status: approved {date}\n".encode("utf-8")
    by_task = entry.get("byTaskId", "")
    feature = Path(path_str).parent.name
    target_directory = _guard_repo_directory(
        specs_root() / by_task, allow_missing=True
    )
    target_tasks = _guard_repo_file(
        target_directory / "tasks.md", allow_missing=True
    )
    if not target_directory.exists() or not target_tasks.exists():
        raise EngineFailure(f"resolution ledger superseded-byTaskId has no spec dir: {by_task!r}")
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
        # already wrapped into a verbatim LegacyContainer: never re-wrap
        if any("sdd-legacy" in line for _number, line in region):
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
        raise EngineFailure(f"resolution ledger criteria-block without criterion: {path_str}")
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
    manifest_sha256: str = field(default="", repr=False)

    def to_json(self) -> dict:
        return {
            "batchId": self.batch_id,
            "capturedHead": self.captured_head,
            "schemaVersion": 1,
            "state": "preparing",
            "targets": [target.to_json() for target in self.targets],
        }


def _journal_batch_id(batch_id: str) -> str:
    if Path(batch_id).name != batch_id or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}", batch_id):
        raise EngineFailure("journal batch id ไม่เป็น canonical basename")
    return batch_id


def journal_root(batch_id: str) -> Path:
    batch_id = _journal_batch_id(batch_id)
    names = _journal_root_names_for_batch(batch_id)
    if len(names) > 1:
        raise MigrationRecoveryFailure("logical batch มี unretired generation ซ้ำ")
    if names:
        return _journal_base() / names[0]
    return _journal_base() / batch_id


def _journal_base() -> Path:
    return git_dir() / "sdd-retrofit-recovery" / "v1"


_CLEANING_PREFIX = ".clearing-"
_JOURNAL_GENERATION_RE = re.compile(r"\.journal-[0-9a-f]{32}")
_OWNER_LOCK = ".owner.lock"
_HASH_RE = re.compile(r"[0-9a-f]{64}")


@dataclass
class JournalClaim:
    batch_id: str
    root_name: str
    root_fd: int
    lock_fd: int

    def close(self) -> None:
        if self.lock_fd >= 0:
            os.close(self.lock_fd)
            self.lock_fd = -1
        if self.root_fd >= 0:
            os.close(self.root_fd)
            self.root_fd = -1

    def __enter__(self) -> JournalClaim:
        return self

    def __exit__(self, _exc_type, _exc, _traceback) -> None:
        self.close()


def _validate_journal_claim(claim: JournalClaim, batch_id: str) -> None:
    if claim.batch_id != _journal_batch_id(batch_id):
        raise MigrationRecoveryRequired("MIGRATION_RECOVERY_REQUIRED")
    root_stat = os.fstat(claim.root_fd)
    lock_stat = os.fstat(claim.lock_fd)
    entry_stat = os.stat(_OWNER_LOCK, dir_fd=claim.root_fd, follow_symlinks=False)
    if (
        not stat.S_ISDIR(root_stat.st_mode)
        or not stat.S_ISREG(lock_stat.st_mode)
        or lock_stat.st_nlink != 1
        or not _same_inode(lock_stat, entry_stat)
    ):
        raise MigrationRecoveryRequired("MIGRATION_RECOVERY_REQUIRED")


def _read_journal_manifest_header(root_fd: int) -> dict[str, object] | None:
    try:
        payload, _entry = _read_regular_snapshot_at(
            root_fd, "manifest.json", require_single_link=True
        )
    except FileNotFoundError:
        return None
    except (EngineFailure, OSError) as error:
        raise MigrationRecoveryFailure("journal manifest เปิดไม่สำเร็จ") from error
    try:
        manifest = json.loads(payload.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as error:
        raise MigrationRecoveryFailure("journal manifest parse ไม่สำเร็จ") from error
    if (
        not isinstance(manifest, dict)
        or manifest.get("schemaVersion") != 1
        or manifest.get("state") != "preparing"
        or not isinstance(manifest.get("batchId"), str)
        or not isinstance(manifest.get("capturedHead"), str)
        or not isinstance(manifest.get("targets"), list)
    ):
        raise MigrationRecoveryFailure("journal manifest contract ไม่ถูกต้อง")
    _journal_batch_id(str(manifest["batchId"]))
    return manifest


def _journal_root_names_for_batch(batch_id: str) -> list[str]:
    batch_id = _journal_batch_id(batch_id)
    base_fd = _open_trusted_directory(_journal_base(), git_dir(), missing_ok=True)
    if base_fd is None:
        return []
    names: list[str] = []
    try:
        for name in sorted(_recovery_entry_names(base_fd)):
            try:
                root_fd = os.open(
                    name,
                    os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                    dir_fd=base_fd,
                )
            except OSError as error:
                raise MigrationRecoveryFailure(
                    "journal root ไม่ใช่ trusted directory"
                ) from error
            try:
                if _retired_marker_state(root_fd) or name.startswith(_CLEANING_PREFIX):
                    continue
                manifest = _read_journal_manifest_header(root_fd)
                if manifest is not None and manifest["batchId"] == batch_id:
                    names.append(name)
            finally:
                os.close(root_fd)
        return names
    finally:
        os.close(base_fd)


def _claim_new_journal(batch_id: str) -> JournalClaim:
    batch_id = _journal_batch_id(batch_id)
    with _preflight_recovery_state() as recovery:
        _process_claimed_recovery_roots(recovery)
        _rescan_recovery_state(recovery)
        base_fd = recovery.journal_base_fd
        if base_fd < 0:
            raise MigrationRecoveryFailure("journal parent claim ไม่มี fd")
        root_name = f".journal-{secrets.token_hex(16)}"
        root_fd: int | None = None
        lock_fd: int | None = None
        try:
            os.mkdir(root_name, mode=0o700, dir_fd=base_fd)
            os.fsync(base_fd)
            root_fd = os.open(
                root_name,
                os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                dir_fd=base_fd,
            )
            _ensure_private_directory(root_fd, "journal create")
            lock_fd = os.open(
                _OWNER_LOCK,
                os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                0o600,
                dir_fd=root_fd,
            )
            fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            os.fchmod(lock_fd, 0o600)
            os.fsync(lock_fd)
            os.fsync(root_fd)
            claim = JournalClaim(batch_id, root_name, root_fd, lock_fd)
            root_fd = None
            lock_fd = None
            preparing = Journal(batch_id=batch_id, captured_head="")
            payload = json.dumps(
                preparing.to_json(), sort_keys=True, indent=1
            ).encode()
            manifest_fd, _manifest_stat = _write_new_regular_at(
                claim.root_fd, "manifest.json", payload, 0o600
            )
            os.close(manifest_fd)
            _write_phase_hook("no-clobber-publish")
            os.fsync(claim.root_fd)
            _validate_journal_claim(claim, batch_id)
            return claim
        except BaseException:
            if root_fd is not None:
                _retire_claimed_recovery_root(root_fd, lock_fd, "create-error")
            raise
        finally:
            if lock_fd is not None:
                os.close(lock_fd)
            if root_fd is not None:
                os.close(root_fd)


def _claim_existing_journal(batch_id: str) -> JournalClaim:
    batch_id = _journal_batch_id(batch_id)
    names = _journal_root_names_for_batch(batch_id)
    if len(names) != 1:
        if len(names) > 1:
            raise MigrationRecoveryFailure("logical batch มี unretired generation ซ้ำ")
        raise MigrationRecoveryRequired("MIGRATION_RECOVERY_REQUIRED")
    root_name = names[0]
    base_fd = _open_trusted_directory(_journal_base(), git_dir())
    assert base_fd is not None
    root_fd: int | None = None
    lock_fd: int | None = None
    try:
        root_fd = os.open(
            root_name,
            os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
            dir_fd=base_fd,
        )
        lock_fd = _open_owner_lock(root_fd)
        claim = JournalClaim(batch_id, root_name, root_fd, lock_fd)
        _validate_journal_claim(claim, batch_id)
        root_fd = None
        lock_fd = None
        return claim
    finally:
        if lock_fd is not None:
            os.close(lock_fd)
        if root_fd is not None:
            os.close(root_fd)
        os.close(base_fd)


@dataclass
class CleanupClaim:
    name: str
    root_fd: int
    lock_fd: int

    def close(self) -> None:
        for field_name in ("lock_fd", "root_fd"):
            fd = getattr(self, field_name)
            if fd >= 0:
                os.close(fd)
                setattr(self, field_name, -1)


@dataclass
class RecoveryJournalClaim:
    name: str
    kind: str
    claim: JournalClaim
    manifest: dict[str, object] | None

    def close(self) -> None:
        self.claim.close()


@dataclass
class RecoveryPreflight:
    write_intents: list[tuple[WriteIntentClaim, dict[str, object] | None]]
    tombstones: list[CleanupClaim]
    journals: list[RecoveryJournalClaim]
    intent_base_fd: int
    journal_base_fd: int
    locks: contextlib.ExitStack

    def close(self) -> None:
        for claim, _intent in self.write_intents:
            claim.close()
        for claim in self.tombstones:
            claim.close()
        for journal in self.journals:
            journal.close()
        self.locks.close()
        for field_name in ("intent_base_fd", "journal_base_fd"):
            fd = getattr(self, field_name)
            if fd >= 0:
                os.close(fd)
                setattr(self, field_name, -1)

    def __enter__(self) -> RecoveryPreflight:
        return self

    def __exit__(self, _exc_type, _exc, _traceback) -> None:
        self.close()


def _load_preintent_state(root_fd: int) -> dict[str, object] | None:
    entries = set(os.listdir(root_fd))
    if _WRITE_INTENT_FILE in entries:
        _require_exact_child_inventory(
            root_fd,
            {_OWNER_LOCK, _WRITE_INTENT_FILE},
            "write intent root",
        )
        return _load_write_intent(root_fd)
    if entries != {_OWNER_LOCK}:
        raise MigrationRecoveryFailure("pre-intent root มี entry ที่พิสูจน์ไม่ได้")
    return None


def _open_structural_owner_lock(root_fd: int) -> int:
    try:
        entry = os.stat(_OWNER_LOCK, dir_fd=root_fd, follow_symlinks=False)
        lock_fd = os.open(_OWNER_LOCK, os.O_RDWR | os.O_NOFOLLOW, dir_fd=root_fd)
    except OSError as error:
        raise MigrationRecoveryFailure("owner lock ไม่มีหรือเปิดไม่ได้") from error
    opened = os.fstat(lock_fd)
    if (
        stat.S_ISLNK(entry.st_mode)
        or not stat.S_ISREG(entry.st_mode)
        or entry.st_nlink != 1
        or not stat.S_ISREG(opened.st_mode)
        or opened.st_nlink != 1
        or not _same_inode(entry, opened)
    ):
        os.close(lock_fd)
        raise MigrationRecoveryFailure("owner lock ต้องเป็น regular single-link file")
    return lock_fd


def _require_exact_child_inventory(
    directory_fd: int,
    expected: set[str],
    operation: str,
    *,
    allowed: set[str] | None = None,
) -> None:
    try:
        actual = set(os.listdir(directory_fd))
    except OSError as error:
        raise MigrationRecoveryFailure(
            f"{operation}: อ่าน direct child inventory ไม่สำเร็จ"
        ) from error
    allowed = set() if allowed is None else allowed
    if not expected.issubset(actual) or actual - expected - allowed:
        raise MigrationRecoveryFailure(
            f"{operation}: direct child inventory ไม่ตรง contract: "
            f"expected={sorted(expected)} actual={sorted(actual)}"
        )


def _claim_write_intent_states(
    base_fd: int | None,
    *,
    claim_stale: bool = True,
) -> list[tuple[WriteIntentClaim, dict[str, object] | None]]:
    if base_fd is None:
        return []
    claims: list[tuple[WriteIntentClaim, dict[str, object] | None]] = []
    malformed: list[str] = []
    active = False
    unretired = False
    for token in sorted(_recovery_entry_names(base_fd)):
        root_fd: int | None = None
        lock_fd: int | None = None
        try:
            if _WRITE_INTENT_TOKEN_RE.fullmatch(token) is None:
                raise MigrationRecoveryFailure("write intent token ไม่เป็น canonical basename")
            root_fd = os.open(
                token,
                os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                dir_fd=base_fd,
            )
            if _retired_marker_state(root_fd):
                continue
            unretired = True
            intent = _load_preintent_state(root_fd)
            lock_fd = _open_structural_owner_lock(root_fd)
            if not claim_stale:
                continue
            try:
                fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                active = True
                continue
            claim = WriteIntentClaim(token, os.dup(base_fd), root_fd, lock_fd)
            root_fd = None
            lock_fd = None
            _require_claimed_directory(
                claim.base_fd, claim.token, claim.root_fd, "write intent preflight"
            )
            claims.append((claim, intent))
        except (MigrationRecoveryFailure, OSError) as error:
            malformed.append(f"{token}: {error}")
        finally:
            if lock_fd is not None:
                os.close(lock_fd)
            if root_fd is not None:
                os.close(root_fd)
    if malformed:
        for claim, _intent in claims:
            claim.close()
        raise MigrationRecoveryFailure("; ".join(malformed))
    if active:
        for claim, _intent in claims:
            claim.close()
        raise MigrationRecoveryRequired("MIGRATION_RECOVERY_REQUIRED")
    if not claim_stale and unretired:
        raise MigrationRecoveryRequired("MIGRATION_RECOVERY_REQUIRED")
    return claims


def _journal_swap_allowlist(
    write_intents: list[tuple[WriteIntentClaim, dict[str, object] | None]],
) -> dict[str, dict[str, set[str]]]:
    allowed: dict[str, dict[str, set[str]]] = {}
    base_relative = _journal_base().relative_to(git_dir())
    for _claim, intent in write_intents:
        if intent is None or intent.get("anchor") != "git":
            continue
        try:
            relative = Path(str(intent["path"])).relative_to(base_relative)
        except (KeyError, ValueError):
            continue
        parts = relative.parts
        if len(parts) == 2:
            child = ""
        elif len(parts) == 3 and parts[1] == "originals":
            child = "originals"
        else:
            continue
        allowed.setdefault(parts[0], {}).setdefault(child, set()).add(
            str(intent["swapName"])
        )
    return allowed


def _canonical_journal_root_name(name: str) -> tuple[str, bool]:
    if name.startswith(_CLEANING_PREFIX):
        return _journal_batch_id(name.removeprefix(_CLEANING_PREFIX)), True
    if _JOURNAL_GENERATION_RE.fullmatch(name):
        return "", False
    return _journal_batch_id(name), False


def _claim_journal_states(
    base_fd: int,
    *,
    claim_stale: bool = True,
    allowed_swaps: dict[str, dict[str, set[str]]] | None = None,
) -> tuple[list[CleanupClaim], list[RecoveryJournalClaim]]:
    tombstones: list[CleanupClaim] = []
    journals: list[RecoveryJournalClaim] = []
    structural: list[tuple[str, str, bool, int, int]] = []
    malformed: list[str] = []
    active = False
    unretired = False
    batches: dict[str, list[str]] = {}
    allowed_swaps = {} if allowed_swaps is None else allowed_swaps

    for name in sorted(_recovery_entry_names(base_fd)):
        root_fd: int | None = None
        lock_fd: int | None = None
        try:
            root_fd = os.open(
                name,
                os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                dir_fd=base_fd,
            )
            if _retired_marker_state(root_fd):
                continue
            legacy_batch, cleanup = _canonical_journal_root_name(name)
            unretired = True
            lock_fd = _open_structural_owner_lock(root_fd)
            if claim_stale:
                try:
                    fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                except BlockingIOError:
                    active = True
                    continue
            structural.append((name, legacy_batch, cleanup, root_fd, lock_fd))
            root_fd = None
            lock_fd = None
        except (MigrationRecoveryFailure, EngineFailure, OSError) as error:
            malformed.append(f"{name}: {error}")
        finally:
            if lock_fd is not None:
                os.close(lock_fd)
            if root_fd is not None:
                os.close(root_fd)

    for name, legacy_batch, cleanup, root_fd, lock_fd in structural:
        try:
            if cleanup:
                _require_exact_child_inventory(
                    root_fd, {_OWNER_LOCK}, "legacy cleanup root"
                )
                tombstones.append(CleanupClaim(name, root_fd, lock_fd))
                continue

            manifest = _read_journal_manifest_header(root_fd)
            batch_id = legacy_batch
            kind = "opaque"
            if manifest is None:
                _require_exact_child_inventory(
                    root_fd, {_OWNER_LOCK}, "opaque journal root"
                )
            else:
                batch_id = str(manifest["batchId"])
                kind = "manifest"
                claim = JournalClaim(batch_id, name, root_fd, lock_fd)
                _read_journal(
                    batch_id,
                    claim=claim,
                    allowed_entries=allowed_swaps.get(name),
                )
                batches.setdefault(batch_id, []).append(name)
            claim = JournalClaim(batch_id, name, root_fd, lock_fd)
            journals.append(RecoveryJournalClaim(name, kind, claim, manifest))
        except (MigrationRecoveryFailure, EngineFailure, OSError) as error:
            malformed.append(f"{name}: {error}")
            os.close(lock_fd)
            os.close(root_fd)

    duplicates = sorted(batch for batch, names in batches.items() if len(names) > 1)
    if malformed or active or duplicates or not claim_stale and unretired:
        for claim in tombstones:
            claim.close()
        for journal in journals:
            journal.close()
    if malformed:
        raise MigrationRecoveryFailure("; ".join(malformed))
    if active:
        raise MigrationRecoveryRequired("MIGRATION_RECOVERY_REQUIRED")
    if duplicates:
        raise MigrationRecoveryFailure(
            "logical batch มี unretired generation ซ้ำ: " + ",".join(duplicates)
        )
    if not claim_stale and unretired:
        raise MigrationRecoveryRequired("MIGRATION_RECOVERY_REQUIRED")
    return tombstones, journals


def _preflight_recovery_state() -> RecoveryPreflight:
    locks = contextlib.ExitStack()
    intent_base_fd = -1
    journal_base_fd = -1
    write_intents: list[tuple[WriteIntentClaim, dict[str, object] | None]] = []
    tombstones: list[CleanupClaim] = []
    journals: list[RecoveryJournalClaim] = []
    try:
        opened_intent = _open_trusted_directory(
            _write_intent_root(), git_dir(), missing_ok=True
        )
        if opened_intent is not None:
            intent_base_fd = opened_intent
            locks.enter_context(
                _recovery_mutation_lock(intent_base_fd, "write intent resolver")
            )
        opened_journal = _open_trusted_directory(
            _journal_base(), git_dir(), create=True
        )
        assert opened_journal is not None
        journal_base_fd = opened_journal
        locks.enter_context(
            _recovery_mutation_lock(journal_base_fd, "journal resolver")
        )
        write_intents = _claim_write_intent_states(
            None if intent_base_fd < 0 else intent_base_fd
        )
        tombstones, journals = _claim_journal_states(
            journal_base_fd,
            allowed_swaps=_journal_swap_allowlist(write_intents),
        )
        return RecoveryPreflight(
            write_intents,
            tombstones,
            journals,
            intent_base_fd,
            journal_base_fd,
            locks,
        )
    except BaseException:
        for claim, _intent in write_intents:
            claim.close()
        for claim in tombstones:
            claim.close()
        for journal in journals:
            journal.close()
        locks.close()
        if intent_base_fd >= 0:
            os.close(intent_base_fd)
        if journal_base_fd >= 0:
            os.close(journal_base_fd)
        raise


def _reconcile_claimed_write_intents(
    claims: list[tuple[WriteIntentClaim, dict[str, object] | None]],
) -> None:
    for claim, intent in claims:
        _reconcile_one_write_intent(claim, intent)


def _remove_cleanup_tombstone(claim: CleanupClaim) -> None:
    if claim.root_fd < 0 or claim.lock_fd < 0:
        raise MigrationRecoveryFailure("cleanup claim ถูกปิดก่อน retirement")
    _retire_claimed_recovery_root(
        claim.root_fd, claim.lock_fd, "legacy-cleaning"
    )


def _remove_cleanup_claim(claim: CleanupClaim) -> None:
    _remove_cleanup_tombstone(claim)


def _remove_claimed_tombstones(claims: list[CleanupClaim]) -> None:
    for claim in claims:
        _remove_cleanup_tombstone(claim)


def _remove_incomplete_journals(journals: list[RecoveryJournalClaim]) -> None:
    for journal in journals:
        if journal.kind == "opaque":
            _retire_claimed_recovery_root(
                journal.claim.root_fd,
                journal.claim.lock_fd,
                "incomplete-before-manifest",
            )


def _process_claimed_recovery_roots(recovery: RecoveryPreflight) -> None:
    _reconcile_claimed_write_intents(recovery.write_intents)
    roots: list[tuple[str, str, object]] = [
        (claim.name, "cleanup", claim) for claim in recovery.tombstones
    ] + [
        (journal.name, journal.kind, journal) for journal in recovery.journals
    ]
    for _name, kind, item in sorted(roots, key=lambda row: row[0]):
        if kind == "cleanup":
            _remove_cleanup_tombstone(item)
            continue
        journal = item
        if kind == "opaque":
            _remove_incomplete_journals([journal])
            continue
        targets = journal.manifest.get("targets", []) if journal.manifest else []
        if not any(
            isinstance(target, dict)
            and (target.get("pending") is True or target.get("applied") is True)
            for target in targets
        ):
            clear_journal(
                journal.claim.batch_id,
                claim=journal.claim,
                preflighted=True,
                operation="uncommitted",
            )
            continue
        recovered, failures = restore_from_journal(
            journal.claim.batch_id,
            claim=journal.claim,
            recovery_preflighted=True,
        )
        if not recovered:
            raise MigrationRecoveryFailure(
                "journal restore hash guard failed: " + ",".join(failures)
            )


def _rescan_recovery_state(recovery: RecoveryPreflight) -> None:
    _claim_write_intent_states(
        None if recovery.intent_base_fd < 0 else recovery.intent_base_fd,
        claim_stale=False,
    )
    _claim_journal_states(recovery.journal_base_fd, claim_stale=False)


def _prepare_for_new_journal() -> None:
    with _preflight_recovery_state() as recovery:
        _process_claimed_recovery_roots(recovery)
        _rescan_recovery_state(recovery)


def _finish_pending_cleanup(_base_fd: int) -> None:
    _resume_pending_cleanup()


def _resume_pending_cleanup() -> None:
    with _preflight_recovery_state() as recovery:
        _process_claimed_recovery_roots(recovery)
        _rescan_recovery_state(recovery)


def journal_exists(batch_id: str | None = None) -> bool:
    canonical = None if batch_id is None else _journal_batch_id(batch_id)
    base_fd = _open_trusted_directory(
        _journal_base(), git_dir(), missing_ok=True
    )
    if base_fd is None:
        return False
    matches = False
    malformed: list[str] = []
    batches: dict[str, list[str]] = {}
    try:
        for name in sorted(_recovery_entry_names(base_fd)):
            root_fd: int | None = None
            lock_fd: int | None = None
            try:
                root_fd = os.open(
                    name,
                    os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                    dir_fd=base_fd,
                )
                if _retired_marker_state(root_fd):
                    continue
                legacy_batch, cleanup = _canonical_journal_root_name(name)
                manifest = None if cleanup else _read_journal_manifest_header(root_fd)
                logical_batch = legacy_batch
                if manifest is not None:
                    logical_batch = str(manifest["batchId"])
                    batches.setdefault(logical_batch, []).append(name)
                lock_fd = _open_structural_owner_lock(root_fd)
                if manifest is not None:
                    _read_journal(
                        logical_batch,
                        claim=JournalClaim(logical_batch, name, root_fd, lock_fd),
                    )
                if canonical is None or manifest is None or logical_batch == canonical:
                    matches = True
            except (MigrationRecoveryFailure, EngineFailure, OSError) as error:
                malformed.append(f"{name}: {error}")
            finally:
                if lock_fd is not None:
                    os.close(lock_fd)
                if root_fd is not None:
                    os.close(root_fd)
        duplicates = sorted(
            logical_batch
            for logical_batch, names in batches.items()
            if len(names) > 1
        )
        if malformed:
            raise MigrationRecoveryFailure("; ".join(malformed))
        if duplicates:
            raise MigrationRecoveryFailure(
                "logical batch มี unretired generation ซ้ำ: "
                + ",".join(duplicates)
            )
        return matches
    finally:
        os.close(base_fd)


def _validate_hash(value: object, field_name: str) -> str:
    if not isinstance(value, str) or _HASH_RE.fullmatch(value) is None:
        raise MigrationRecoveryFailure(f"journal {field_name} ต้องเป็น SHA-256 lowercase 64 ตัว")
    return value


def _validate_target_path(path_str: object) -> str:
    if not isinstance(path_str, str) or not path_str or Path(path_str).is_absolute():
        raise MigrationRecoveryFailure("journal target path ต้องเป็น repo-relative canonical path")
    target = _repo_candidate(path_str)
    if rel(target) != path_str or path_str == ".":
        raise MigrationRecoveryFailure("journal target path ต้องเป็น repo-relative canonical path")
    return path_str


def _prepare_journal_write(journal: Journal, originals: dict[str, bytes]) -> None:
    if journal.batch_id != _journal_batch_id(journal.batch_id):
        raise MigrationRecoveryFailure("journal batch id ไม่ตรง canonical contract")
    if not isinstance(journal.captured_head, str):
        raise MigrationRecoveryFailure("journal capturedHead ต้องเป็น string")
    paths: set[str] = set()
    original_names: set[str] = set()
    for target in journal.targets:
        target.path = _validate_target_path(target.path)
        _validate_hash(target.before_sha256, "beforeSha256")
        _validate_hash(target.planned_sha256, "plannedSha256")
        if type(target.pending) is not bool or type(target.applied) is not bool:
            raise MigrationRecoveryFailure("journal pending/applied ต้องเป็น boolean")
        if target.pending and target.applied:
            raise MigrationRecoveryFailure("journal target ห้าม pending และ applied พร้อมกัน")
        if target.path in paths:
            raise MigrationRecoveryFailure("journal target path ซ้ำ")
        paths.add(target.path)
        target.original_file = f"{_stable_index(target.path)}.bin"
        if target.original_file in original_names:
            raise MigrationRecoveryFailure("journal originalFile mapping ชนกัน")
        original_names.add(target.original_file)
    if set(originals) != paths:
        raise MigrationRecoveryFailure("journal originals mapping ไม่ตรง target ทั้งชุด")
    for target in journal.targets:
        if sha256(originals[target.path]) != target.before_sha256:
            raise MigrationRecoveryFailure("journal original bytes ไม่ตรง beforeSha256")


def write_journal(
    batch_id: str,
    journal: Journal,
    originals: dict[str, bytes],
    *,
    claim: JournalClaim | None = None,
) -> Path:
    batch_id = _journal_batch_id(batch_id)
    if journal.batch_id != batch_id:
        raise MigrationRecoveryFailure("journal batchId ไม่ตรง caller")
    _prepare_journal_write(journal, originals)
    owns_claim = claim is None
    if claim is None:
        claim = _claim_new_journal(batch_id)
    originals_fd: int | None = None
    try:
        _validate_journal_claim(claim, batch_id)
        entries = set(os.listdir(claim.root_fd))
        if entries != {_OWNER_LOCK, "manifest.json"}:
            raise MigrationRecoveryRequired("MIGRATION_RECOVERY_REQUIRED")
        current_manifest, _current_stat = _read_regular_snapshot_at(
            claim.root_fd, "manifest.json", require_single_link=True
        )
        try:
            os.mkdir("originals", mode=0o700, dir_fd=claim.root_fd)
        except FileExistsError as error:
            raise MigrationRecoveryRequired("MIGRATION_RECOVERY_REQUIRED") from error
        originals_fd = os.open(
            "originals",
            os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
            dir_fd=claim.root_fd,
        )
        for target in journal.targets:
            _atomic_write_at(
                originals_fd,
                target.original_file,
                originals[target.path],
                default_mode=0o600,
                expected_missing=True,
                intent_anchor="git",
                intent_path=(
                    _journal_base()
                    / claim.root_name
                    / "originals"
                    / target.original_file
                ).relative_to(git_dir()).as_posix(),
            )
        manifest_payload = json.dumps(
            journal.to_json(), sort_keys=True, indent=1
        ).encode()
        _atomic_write_at(
            claim.root_fd,
            "manifest.json",
            manifest_payload,
            default_mode=0o600,
            expected_sha256=sha256(current_manifest),
            intent_anchor="git",
            intent_path=(
                _journal_base() / claim.root_name / "manifest.json"
            ).relative_to(git_dir()).as_posix(),
        )
        journal.manifest_sha256 = sha256(manifest_payload)
        os.fsync(claim.root_fd)
        return _journal_base() / claim.root_name
    finally:
        if originals_fd is not None:
            os.close(originals_fd)
        if owns_claim:
            claim.close()


def _write_journal_manifest(
    batch_id: str,
    journal: Journal,
    *,
    claim: JournalClaim | None = None,
) -> None:
    owns_fd = claim is None
    root_fd: int | None = None
    if claim is None:
        root_path = journal_root(batch_id)
        root_fd = _open_trusted_directory(root_path, git_dir())
        assert root_fd is not None
        root_name = root_path.name
    else:
        _validate_journal_claim(claim, batch_id)
        root_fd = claim.root_fd
        root_name = claim.root_name
    try:
        manifest_payload = json.dumps(
            journal.to_json(), sort_keys=True, indent=1
        ).encode()
        _atomic_write_at(
            root_fd,
            "manifest.json",
            manifest_payload,
            default_mode=0o600,
            expected_sha256=journal.manifest_sha256 or None,
            intent_anchor="git",
            intent_path=(
                _journal_base() / root_name / "manifest.json"
            ).relative_to(git_dir()).as_posix(),
        )
        journal.manifest_sha256 = sha256(manifest_payload)
    finally:
        if owns_fd and root_fd is not None:
            os.close(root_fd)


def _stable_index(text: str) -> str:
    return hashlib.sha256(text.encode()).hexdigest()[:16]


def _read_journal(
    batch_id: str,
    *,
    claim: JournalClaim | None = None,
    allowed_entries: dict[str, set[str]] | None = None,
) -> tuple[Journal, dict[str, bytes]]:
    batch_id = _journal_batch_id(batch_id)
    owns_root_fd = claim is None
    root_fd: int | None = None
    originals_fd: int | None = None
    try:
        if claim is None:
            root_fd = _open_trusted_directory(journal_root(batch_id), git_dir())
            assert root_fd is not None
        else:
            _validate_journal_claim(claim, batch_id)
            root_fd = claim.root_fd
        manifest_bytes, _manifest_stat = _read_regular_snapshot_at(
            root_fd, "manifest.json", require_single_link=True
        )
        manifest = json.loads(manifest_bytes.decode("utf-8"))
        if (
            not isinstance(manifest, dict)
            or manifest.get("schemaVersion") != 1
            or manifest.get("state") != "preparing"
            or manifest.get("batchId") != batch_id
            or not isinstance(manifest.get("capturedHead"), str)
            or not isinstance(manifest.get("targets"), list)
        ):
            raise MigrationRecoveryFailure("journal manifest contract ไม่ถูกต้อง")

        journal = Journal(
            batch_id=batch_id,
            captured_head=manifest["capturedHead"],
            manifest_sha256=sha256(manifest_bytes),
        )
        paths: set[str] = set()
        original_names: set[str] = set()
        for entry in manifest["targets"]:
            if not isinstance(entry, dict):
                raise MigrationRecoveryFailure("journal target contract ไม่ถูกต้อง")
            path_str = _validate_target_path(entry.get("path"))
            before_sha256 = _validate_hash(entry.get("beforeSha256"), "beforeSha256")
            planned_sha256 = _validate_hash(entry.get("plannedSha256"), "plannedSha256")
            pending = entry.get("pending")
            applied = entry.get("applied")
            original_file = entry.get("originalFile")
            if type(pending) is not bool or type(applied) is not bool or pending and applied:
                raise MigrationRecoveryFailure("journal pending/applied contract ไม่ถูกต้อง")
            expected_original = f"{_stable_index(path_str)}.bin"
            if original_file != expected_original or Path(str(original_file)).name != original_file:
                raise MigrationRecoveryFailure("journal originalFile mapping ไม่ถูกต้อง")
            if path_str in paths or original_file in original_names:
                raise MigrationRecoveryFailure("journal target/original mapping ต้อง unique")
            paths.add(path_str)
            original_names.add(original_file)
            journal.targets.append(JournalTarget(
                path=path_str,
                before_sha256=before_sha256,
                planned_sha256=planned_sha256,
                pending=pending,
                applied=applied,
                original_file=original_file,
            ))

        allowed_entries = {} if allowed_entries is None else allowed_entries
        root_inventory = {_OWNER_LOCK, "manifest.json"}
        root_children = set(os.listdir(root_fd))
        if journal.targets or "originals" in root_children:
            root_inventory.add("originals")
        _require_exact_child_inventory(
            root_fd,
            root_inventory,
            "journal root",
            allowed=allowed_entries.get("", set()),
        )

        originals: dict[str, bytes] = {}
        if "originals" in root_inventory:
            originals_fd = _open_child_directory(root_fd, "originals")
            assert originals_fd is not None
            _require_exact_child_inventory(
                originals_fd,
                original_names,
                "journal originals",
                allowed=allowed_entries.get("originals", set()),
            )
        original_inodes: set[tuple[int, int]] = set()
        for target in journal.targets:
            original, original_stat = _read_regular_snapshot_at(
                originals_fd, target.original_file, require_single_link=True
            )
            inode = (original_stat.st_dev, original_stat.st_ino)
            if inode in original_inodes:
                raise MigrationRecoveryFailure("journal originals ห้าม share inode")
            original_inodes.add(inode)
            if sha256(original) != target.before_sha256:
                raise MigrationRecoveryFailure("journal original ไม่ตรง beforeSha256")
            originals[target.original_file] = original
        return journal, originals
    except MigrationRecoveryFailure:
        raise
    except (EngineFailure, KeyError, TypeError, UnicodeError, json.JSONDecodeError, OSError) as error:
        raise MigrationRecoveryFailure(f"journal validation failed: {error}") from error
    finally:
        if originals_fd is not None:
            os.close(originals_fd)
        if owns_root_fd and root_fd is not None:
            os.close(root_fd)


def load_journal(batch_id: str) -> Journal:
    return _read_journal(batch_id)[0]


def _validate_loaded_journal_snapshot(
    batch_id: str,
    journal: Journal,
    originals: dict[str, bytes],
    *,
    claim: JournalClaim | None = None,
) -> None:
    owns_root_fd = claim is None
    root_fd: int | None = None
    originals_fd: int | None = None
    try:
        if claim is None:
            root_fd = _open_trusted_directory(journal_root(batch_id), git_dir())
            assert root_fd is not None
        else:
            _validate_journal_claim(claim, batch_id)
            root_fd = claim.root_fd
        if journal.targets:
            originals_fd = _open_child_directory(root_fd, "originals")
            assert originals_fd is not None
        manifest, _manifest_stat = _read_regular_snapshot_at(
            root_fd, "manifest.json", require_single_link=True
        )
        if sha256(manifest) != journal.manifest_sha256:
            raise MigrationFileChanged("MIGRATION_FILE_CHANGED: journal manifest")
        for target in journal.targets:
            original, _original_stat = _read_regular_snapshot_at(
                originals_fd, target.original_file, require_single_link=True
            )
            if original != originals[target.original_file]:
                raise MigrationRecoveryFailure("journal original เปลี่ยนหลัง validation")
    except (MigrationFileChanged, MigrationRecoveryFailure):
        raise
    except (EngineFailure, OSError) as error:
        raise MigrationRecoveryFailure(f"journal snapshot validation failed: {error}") from error
    finally:
        if originals_fd is not None:
            os.close(originals_fd)
        if owns_root_fd and root_fd is not None:
            os.close(root_fd)


def clear_journal(
    batch_id: str,
    *,
    claim: JournalClaim | None = None,
    preflighted: bool = False,
    operation: str = "verified",
) -> None:
    batch_id = _journal_batch_id(batch_id)
    owns_claim = False
    if claim is None:
        if not _journal_root_names_for_batch(batch_id):
            return
        claim = _claim_existing_journal(batch_id)
        owns_claim = True
    try:
        _validate_journal_claim(claim, batch_id)
        _read_journal(batch_id, claim=claim)
        _retire_claimed_recovery_root(
            claim.root_fd, claim.lock_fd, operation
        )
    finally:
        if owns_claim:
            claim.close()


def restore_from_journal(
    batch_id: str,
    *,
    claim: JournalClaim | None = None,
    recovery_preflighted: bool = False,
) -> tuple[bool, list[str]]:
    """ตรวจ journal ทั้งก้อนก่อน restore และไม่แตะ foreign bytes."""
    if claim is None:
        with _claim_existing_journal(batch_id) as owned_claim:
            return restore_from_journal(batch_id, claim=owned_claim)
    _validate_journal_claim(claim, batch_id)
    journal, originals = _read_journal(batch_id, claim=claim)
    interesting = [target for target in journal.targets if target.pending or target.applied]
    snapshots: list[tuple[JournalTarget, str]] = []
    try:
        for target in interesting:
            target_path, parent_fd = _repo_parent_fd(target.path)
            try:
                current, _current_stat = _read_regular_snapshot_at(
                    parent_fd, target_path.name, require_single_link=True
                )
            finally:
                os.close(parent_fd)
            snapshots.append((target, sha256(current)))
    except (EngineFailure, OSError) as error:
        raise MigrationRecoveryFailure(f"journal target validation failed: {error}") from error

    failures = [
        target.path
        for target, current_hash in snapshots
        if current_hash not in {target.before_sha256, target.planned_sha256}
    ]
    if failures:
        return False, failures

    _validate_loaded_journal_snapshot(
        batch_id, journal, originals, claim=claim
    )
    for target, current_hash in snapshots:
        if current_hash == target.planned_sha256:
            _atomic_write_repo_file(
                target.path,
                originals[target.original_file],
                expected_sha256=target.planned_sha256,
            )
    clear_journal(
        batch_id,
        claim=claim,
        preflighted=recovery_preflighted,
        operation="recovered",
    )
    return True, []


# ---------------------------------------------------------------------------
# Modes
# ---------------------------------------------------------------------------


def enforce_journal_clear(mode: str) -> int | None:
    if write_intents_pending() or journal_exists():
        print(json.dumps({
            "schemaVersion": 1,
            "verdict": "engine-fail",
            "diagnostics": [{"code": "MIGRATION_RECOVERY_REQUIRED"}],
        }, sort_keys=True))
        return 2
    return None


def scope_check() -> list[RetrofitBlocker]:
    membership = historical_membership()
    if not membership.missing:
        return []
    return [RetrofitBlocker(
        "MIGRATION_SCOPE_MISMATCH", "-", ".ai/specs", "corpus.inventory", "", 1,
        f"missing={','.join(membership.missing)}; "
        f"outsideScope={','.join(membership.outside_scope) or 'none'}; "
        f"expected={len(membership.expected)}",
        ",".join(membership.present),
        ",".join(membership.outside_scope),
    )]


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


def strict_check_features(features: list[str]) -> list[dict]:
    """รัน strict validator จริงทุก feature โดยรักษา policy/engine tri-state."""
    return [_observed_strict_result(feature, set()) for feature in features]


def _legacy_residual_features() -> set[str]:
    ledger = load_resolution_ledger()
    return {
        Path(path_str).parent.name
        for (path_str, field, _scoped), entry in ledger.items()
        if field in {"trace.table", "authoring.chain"}
        and entry["disposition"] in {
            "trace-header-canonical",
            "active-authoring-exempt",
            "legacy-baseline-exempt",
        }
    }


def _observed_strict_result(feature: str, legacy_dirs: set[str]) -> dict:
    """หนึ่ง invocation ต่อ feature; exception เป็น unchecked ไม่ใช่ strict pass."""
    output = io.StringIO()
    try:
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(output):
            code = sc.all_tree_trace_run(feature, specs_root())
    except (OSError, ValueError) as error:
        return {
            "checked": False,
            "engineFailure": True,
            "feature": feature,
            "legacyResidual": feature in legacy_dirs,
            "observedOutput": f"ENGINE_INTERNAL: {error}",
            "strictOk": False,
        }
    if code == 2:
        return {
            "checked": False,
            "engineFailure": True,
            "exitCode": code,
            "feature": feature,
            "legacyResidual": feature in legacy_dirs,
            "observedOutput": output.getvalue().strip(),
            "strictOk": False,
        }
    return {
        "checked": True,
        "engineFailure": False,
        "exitCode": code,
        "feature": feature,
        "legacyResidual": feature in legacy_dirs,
        "observedOutput": output.getvalue().strip(),
        "strictOk": code == 0,
    }


def run_check(batch_id: str) -> int:
    blocked = enforce_journal_clear("check")
    if blocked is not None:
        return blocked
    if batch_id == "final-all-spec":
        membership = historical_membership()
        if membership.missing:
            print(json.dumps({
                "batch": batch_id,
                "expectedFeatures": list(membership.expected),
                "missingFeatures": list(membership.missing),
                "outsideHistoricalScope": list(membership.outside_scope),
                "problem": "MIGRATION_SCOPE_MISMATCH",
                "schemaVersion": 1,
                "verdict": "policy-fail",
            }, sort_keys=True))
            return 1

        legacy_dirs = _legacy_residual_features() & set(membership.expected)
        historical_results = [
            _observed_strict_result(feature, legacy_dirs)
            for feature in membership.expected
        ]
        current_result = (
            _observed_strict_result(CURRENT_FEATURE, set())
            if CURRENT_FEATURE in membership.inventory
            else {
                "checked": False,
                "engineFailure": False,
                "feature": CURRENT_FEATURE,
                "legacyResidual": False,
                "observedOutput": "canonical current feature directory missing",
                "strictOk": False,
            }
        )
        outside_results = [
            _observed_strict_result(feature, set())
            for feature in membership.outside_scope
        ]
        checked_count = sum(result["checked"] for result in historical_results)
        unchecked = [result["feature"] for result in historical_results if not result["checked"]]
        historical_strict_ok = (
            checked_count == len(membership.expected)
            and all(result["strictOk"] for result in historical_results)
        )
        aggregate_results = historical_results + [current_result] + outside_results
        engine_failures = [
            result["feature"] for result in aggregate_results if result["engineFailure"]
        ]
        aggregate_strict_ok = (
            historical_strict_ok
            and current_result["strictOk"]
            and all(result["strictOk"] for result in outside_results)
        )
        verdict = (
            "engine-fail" if engine_failures
            else "allow" if aggregate_strict_ok
            else "policy-fail"
        )
        print(json.dumps({
            "batch": batch_id,
            "currentFeature": current_result,
            "engineFailureFeatures": engine_failures,
            "historicalInventory": {
                "checkedCount": checked_count,
                "expectedCount": len(membership.expected),
                "expectedFeatures": list(membership.expected),
                "results": historical_results,
                "strictOk": historical_strict_ok,
                "uncheckedFeatures": unchecked,
            },
            "outsideHistoricalScope": outside_results,
            "schemaVersion": 1,
            "strictOk": aggregate_strict_ok,
            "verdict": verdict,
        }, sort_keys=True))
        if engine_failures:
            return 2
        return 0 if aggregate_strict_ok else 1
    # normal batch: canonical scope + strict on scoped features + no planned actions
    scope_blockers = scope_check()
    if scope_blockers:
        print(json.dumps(envelope("check", batch_id, [], scope_blockers), sort_keys=True))
        return 1
    actions, blockers = plan_batch(batch_id)
    features = sorted({action.path.split("/")[2] for action in actions})
    features += sorted({blocker.path.split("/")[2] for blocker in blockers
                        if len(blocker.path.split("/")) > 2})
    strict_results = strict_check_features(sorted(set(features)))
    strict_failures = sum(
        result["checked"] and not result["strictOk"] for result in strict_results
    )
    engine_failures = [
        result["feature"] for result in strict_results if result["engineFailure"]
    ]
    # decided-residual blockers are records, not pending work
    safe_pending = len([a for a in actions if not _residual_is_decided(a)]
                       ) + len([b for b in blockers
                                if not _residual_is_decided(b)])
    safe_pending = max(safe_pending, 0)
    verdict = (
        "engine-fail" if engine_failures
        else "allow" if safe_pending == 0 and strict_failures == 0
        else "policy-fail"
    )
    print(json.dumps({
        "batch": batch_id,
        "engineFailureFeatures": engine_failures,
        "plannedSafeActionsRemaining": safe_pending,
        "schemaVersion": 1,
        "strictFailures": strict_failures,
        "strictResults": strict_results,
        "verdict": verdict,
    }, sort_keys=True))
    if engine_failures:
        return 2
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
    with _preflight_recovery_state() as recovery:
        _process_claimed_recovery_roots(recovery)
        _rescan_recovery_state(recovery)
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
        _atomic_write_repo_file(
            ".ai/specs/apply-demo/probe.txt", b"moved\n", create_parents=True
        )
        _git(["add", "-A"])
        _git(["commit", "-qm", "interloper"])
    journal = Journal(batch_id=batch_id, captured_head=captured_head)
    originals: dict[str, bytes] = {}
    for path_str, before, planned in plans:
        journal.targets.append(JournalTarget(
            path=path_str, before_sha256=sha256(before), planned_sha256=sha256(planned),
        ))
        originals[path_str] = before
    with _claim_new_journal(batch_id) as claim:
        write_journal(batch_id, journal, originals, claim=claim)
        # reload to bind original_file names
        journal = load_journal(batch_id)

        for index, (path_str, before, planned) in enumerate(plans):
            target_record = journal.targets[index]
            # precondition recheck (HEAD + exact bytes)
            if git_out(["rev-parse", "HEAD"]).strip() != captured_head or \
                    sha256(read_bytes(abs_repo(path_str))) != target_record.before_sha256:
                restored_ok, failures = restore_from_journal(batch_id, claim=claim)
                print(json.dumps({
                    "diagnostics": [{"code": "MIGRATION_HEAD_CHANGED", "restored": restored_ok,
                                     "failedPaths": failures}],
                    "schemaVersion": 1, "verdict": "engine-fail",
                }, sort_keys=True))
                return 2
            target_record.pending = True
            _write_journal_manifest(batch_id, journal, claim=claim)
            target_path = abs_repo(path_str)
            _atomic_write_repo_file(
                target_path,
                planned,
                expected_sha256=target_record.before_sha256,
            )
            _post_diags = sc.parse_task_blocks(read_bytes(target_path), Path(path_str))[1]
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
                restored_ok, failures = restore_from_journal(batch_id, claim=claim)
                print(json.dumps({
                    "diagnostics": [{"code": "MIGRATION_FILE_CHANGED", "restored": restored_ok,
                                     "failedPaths": failures}],
                    "schemaVersion": 1, "verdict": "engine-fail",
                }, sort_keys=True))
                return 2
            target_record.applied = True
            target_record.pending = False
            _write_journal_manifest(batch_id, journal, claim=claim)

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
            restored_ok, failures = restore_from_journal(batch_id, claim=claim)
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
        clear_journal(batch_id, claim=claim, operation="verified")
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
    target = _atomic_write_repo_file(
        path,
        (json.dumps(payload, ensure_ascii=False, indent=1) + "\n").encode("utf-8"),
        create_parents=True,
    )
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
    try:
        if args.emit_resolution_template:
            _LEDGER_CACHE.clear()
            blocked = enforce_journal_clear("emit-resolution-template")
            if blocked is not None:
                return blocked
            scope_blockers = scope_check()
            if scope_blockers:
                print(json.dumps(envelope(
                    "emit-resolution-template", args.batch, [], scope_blockers
                ), sort_keys=True))
                return 1
            return emit_resolution_template(args.emit_resolution_template, [args.batch])
        modes = [flag for flag, given in
                 (("dry-run", args.dry_run), ("apply-safe", args.apply_safe), ("check", args.check)) if given]
        if len(modes) != 1:
            return 2
        mode = modes[0]
        if args.batch in READ_ONLY_ONLY_BATCHES and mode != "check":
            return 2
        if mode == "dry-run":
            return run_dry_run(args.batch)
        if mode == "check":
            return run_check(args.batch)
        return run_apply_safe(args.batch)
    except MigrationRecoveryRequired:
        print(json.dumps({
            "diagnostics": [{"code": "MIGRATION_RECOVERY_REQUIRED"}],
            "schemaVersion": 1,
            "verdict": "engine-fail",
        }, sort_keys=True))
        return 2
    except MigrationFileChanged as failure:
        print(json.dumps({
            "diagnostics": [{"code": "MIGRATION_FILE_CHANGED", "detail": str(failure)}],
            "schemaVersion": 1,
            "verdict": "engine-fail",
        }, sort_keys=True))
        return 2
    except MigrationRecoveryFailure as failure:
        print(json.dumps({
            "diagnostics": [{"code": "MIGRATION_RECOVERY_FAILED", "detail": str(failure)}],
            "schemaVersion": 1,
            "verdict": "engine-fail",
        }, sort_keys=True))
        return 2
    except (EngineFailure, GitFailure, OSError, ValueError) as failure:
        print(json.dumps({
            "diagnostics": [{"code": "ENGINE_INTERNAL", "detail": str(failure)}],
            "schemaVersion": 1,
            "verdict": "engine-fail",
        }, sort_keys=True))
        return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
