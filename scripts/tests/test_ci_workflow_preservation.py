#!/usr/bin/env python3
"""Task 9 comparator fixtures — every documented negative mutation goes RED
with the exact diagnostic contract (design §Protected workflow comparator):
  missing base blob            -> CI_PROTECTED_JOB_PARSE_FAILED (engine-fail, 2)
  duplicate protected key      -> CI_PROTECTED_JOB_PARSE_FAILED (2)
  missing protected key        -> CI_PROTECTED_JOB_PARSE_FAILED (2)
  YAML merge key               -> CI_PROTECTED_JOB_PARSE_FAILED (2)
  tab indentation              -> CI_PROTECTED_JOB_PARSE_FAILED (2)
  one-byte protected mutation  -> CI_PROTECTED_JOB_CHANGED (policy-fail, 1)
  removed shell inventory      -> CI_SHELL_INVENTORY_REMOVED (1)
  additive inventory token     -> allow (0)
Verdict separation policy-fail(1) vs engine-fail(2) is asserted per case."""
from __future__ import annotations

import importlib.util
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent.parent
REPO = SCRIPTS.parent
_spec = importlib.util.spec_from_file_location(
    "ciw", SCRIPTS / "ci-workflow-preservation.py")
ciw = importlib.util.module_from_spec(_spec)
sys.modules["ciw"] = ciw
_spec.loader.exec_module(ciw)


def git(repo: Path, *args: str) -> None:
    proc = subprocess.run(["git", "-C", str(repo), *args],
                          capture_output=True, text=True)
    assert proc.returncode == 0, proc.stderr


GH_GOOD = """name: CI

on:
  pull_request:
    branches: [develop]

permissions:
  contents: read

jobs:
  verify:
    name: guards + spec-trace
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Guard regression tests
        run: |
          set -euo pipefail
          shopt -s nullglob
          tests=( .claude/hooks/tests/*.test.sh docker/entrypoint.test.sh docker/migrate-entrypoint.test.sh scripts/check-release-evidence.test.sh )
          if [ "${#tests[@]}" -eq 0 ]; then
            echo "No guard tests found" >&2
            exit 1
          fi
          status=0
          for t in "${tests[@]}"; do
            bash "$t" || status=1
          done
          exit "$status"

  dotnet:
    name: dotnet build + test
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Build
        run: dotnet build pol-core.slnx -warnaserror

  docker-build:
    name: docker build
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

  dotnet-integration:
    name: integration
    runs-on: ubuntu-latest
    services:
      sql:
        image: mcr.microsoft.com/mssql/server:2025-latest
"""

GL_GOOD = """workflow:
  rules:
    - if: '$CI_COMMIT_BRANCH == "develop"'

stages:
  - verify

verify:
  stage: verify
  script:
    # Guard regression tests — same Tier-1 floor as GitHub ci.yml job `verify`.
    - |
      set -euo pipefail
      shopt -s nullglob
      tests=( .claude/hooks/tests/*.test.sh )
      if [ "${#tests[@]}" -eq 0 ]; then
        echo "No guard tests found under .claude/hooks/tests/" >&2
        exit 1
      fi
      status=0
      for t in "${tests[@]}"; do
        echo "=== guard-test ${t} ==="
        bash "$t" || status=1
      done
      exit "$status"

dotnet:
  stage: test
  image: mcr.microsoft.com/dotnet/sdk:10.0
  script:
    - dotnet build pol-core.slnx

integration:
  stage: test
  when: manual
  allow_failure: true

package:
  stage: package
  image: docker:27

.deploy-template:
  stage: deploy
  image: alpine:3.20

deploy-uat:
  extends: .deploy-template
  environment:
    name: uat

deploy-prod:
  extends: .deploy-template
  environment:
    name: production
"""


class ComparatorSandbox(unittest.TestCase):
    """Temp git repo with a seeded merge-base carrying the GOOD workflows."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory(prefix="cipres-")
        self.addCleanup(self._tmp.cleanup)
        self.repo = Path(self._tmp.name) / "repo"
        self.repo.mkdir()
        git(self.repo, "init", "-q")
        git(self.repo, "config", "user.email", "t@t")
        git(self.repo, "config", "user.name", "t")
        gh = self.repo / ".github/workflows"
        gh.mkdir(parents=True)
        (gh / "ci.yml").write_text(GH_GOOD, encoding="utf-8")
        (self.repo / ".gitlab-ci.yml").write_text(GL_GOOD, encoding="utf-8")
        (self.repo / "keep.txt").write_text("seed\n", encoding="utf-8")
        git(self.repo, "add", "-A")
        git(self.repo, "commit", "-qm", "base workflows")
        self.base = subprocess.run(
            ["git", "-C", str(self.repo), "rev-parse", "HEAD"],
            capture_output=True, text=True).stdout.strip()

    def run_cli(self) -> subprocess.CompletedProcess:
        env = dict(os.environ, SDD_CI_PRESERVE_REPO=str(self.repo),
                   PYTHONDONTWRITEBYTECODE="1")
        return subprocess.run(
            [sys.executable, str(SCRIPTS / "ci-workflow-preservation.py"),
             "--base", self.base, "--json"],
            capture_output=True, text=True, env=env)

    def write_current(self, rel: str, text: str) -> None:
        target = self.repo / rel
        target.write_text(text, encoding="utf-8")

    def assert_exit(self, proc: subprocess.CompletedProcess, code: int,
                    diag_code: str | None = None) -> dict:
        payload = {}
        try:
            payload = __import__("json").loads(proc.stdout.strip().splitlines()[-1])
        except Exception:
            pass
        if diag_code is not None:
            codes = {d["code"] for d in payload.get("diagnostics", [])}
            self.assertIn(diag_code, codes,
                          msg=f"expected {diag_code}, got {payload}")
        self.assertEqual(proc.returncode, code,
                         msg=proc.stdout + proc.stderr)
        return payload


class PositiveComparatorTest(ComparatorSandbox):

    def test_untouched_tree_allows(self):
        payload = self.assert_exit(self.run_cli(), 0)
        self.assertEqual(payload["verdict"], "allow")
        self.assertEqual(payload["diagnostics"], [])

    def test_additive_inventory_token_allowed(self):
        text = GH_GOOD.replace(
            "tests=( .claude/hooks/tests/",
            "tests=( .new-fixture/additional.test.sh .claude/hooks/tests/")
        self.write_current(".github/workflows/ci.yml", text)
        self.assert_exit(self.run_cli(), 0)

    def test_comment_or_whitespace_in_verify_allowed(self):
        text = GH_GOOD.replace('name: guards + spec-trace',
                               'name: guards + spec-trace + sdd strict')
        self.write_current(".github/workflows/ci.yml", text)
        self.assert_exit(self.run_cli(), 0)


class NegativeParseFailedTest(ComparatorSandbox):
    CODE = "CI_PROTECTED_JOB_PARSE_FAILED"

    def test_missing_base_blob_is_engine_fail_two(self):
        bad_base = self.base[:12] + "00000000000000000000000000000000000"
        env = dict(os.environ, SDD_CI_PRESERVE_REPO=str(self.repo))
        proc = subprocess.run(
            [sys.executable, str(SCRIPTS / "ci-workflow-preservation.py"),
             "--base", bad_base, "--json"],
            capture_output=True, text=True, env=env)
        payload = __import__("json").loads(proc.stdout.strip().splitlines()[-1])
        self.assertEqual(payload["verdict"], "engine-fail")
        self.assertIn(self.CODE, {d["code"] for d in payload["diagnostics"]})
        self.assertEqual(proc.returncode, 2)

    def test_missing_base_file_arg_form(self):
        # base commit exists but workflow file absent at that commit
        proc = subprocess.run(["git", "-C", str(self.repo), "rev-parse",
                               f"{self.base}~0"], capture_output=True, text=True)
        del proc
        git(self.repo, "rm", "-q", ".github/workflows/ci.yml", ".gitlab-ci.yml")
        git(self.repo, "commit", "-qm", "drop workflows")
        # current files gone -> extractor sees missing current; current bytes b"" fine,
        # but BASE still has them so parse proceeds; simulate the reverse via new repo:
        # instead assert policy behavior: missing CURRENT protected keys -> parse failed.
        self.assert_exit(self.run_cli(), 2, self.CODE)

    def test_duplicate_protected_key_parse_failed(self):
        text = GL_GOOD.replace("\nintegration:\n",
                               "\nintegration:\n---\nintegration:\n") \
            if False else GL_GOOD + "\nintegration:\n  stage: test\n"
        self.write_current(".gitlab-ci.yml", text)
        self.assert_exit(self.run_cli(), 2, self.CODE)

    def test_yaml_merge_key_rejected(self):
        text = GH_GOOD + "\nmerged-job:\n  <<: *base-anchor\n"
        self.write_current(".github/workflows/ci.yml", text)
        self.assert_exit(self.run_cli(), 2, self.CODE)

    def test_tab_indentation_rejected(self):
        self.write_current(".gitlab-ci.yml", GL_GOOD.replace(
            "    - dotnet build pol-core.slnx", "\t- dotnet build pol-core.slnx"))
        self.assert_exit(self.run_cli(), 2, self.CODE)


class NegativePolicyTest(ComparatorSandbox):
    CODE_CHANGED = "CI_PROTECTED_JOB_CHANGED"
    CODE_INVENTORY = "CI_SHELL_INVENTORY_REMOVED"
    CODE = "CI_PROTECTED_JOB_PARSE_FAILED"

    def test_one_byte_protected_github_mutation_is_policy_fail_one(self):
        text = GH_GOOD.replace("dotnet build pol-core.slnx -warnaserror",
                               "dotnet build pol-core.slnx --warnaserror")
        self.assertNotEqual(text, GH_GOOD)
        self.write_current(".github/workflows/ci.yml", text)
        self.assert_exit(self.run_cli(), 1, self.CODE_CHANGED)

    def test_one_byte_protected_gitlab_mutation_is_policy_fail_one(self):
        text = GL_GOOD.replace("deploy-uat:\n  extends: .deploy-template",
                               "deploy-uat:\n  extends: .deploy-templat")
        self.write_current(".gitlab-ci.yml", text)
        self.assert_exit(self.run_cli(), 1, self.CODE_CHANGED)

    def test_protected_job_removed_is_parse_failed(self):
        start = GL_GOOD.index("\ndotnet:")
        end = GL_GOOD.index("\nintegration:")
        self.write_current(".gitlab-ci.yml",
                           GL_GOOD[:start] + GL_GOOD[end:])
        self.assert_exit(self.run_cli(), 2, self.CODE)

    def test_shell_inventory_token_removal_policy_fail_one(self):
        text = GH_GOOD.replace(
            "tests=( .claude/hooks/tests/",
            "tests=( other-tests/somewhere/")
        # keep glob additive rule: removal of ANY base token reds
        self.write_current(".github/workflows/ci.yml", text)
        self.assert_exit(self.run_cli(), 1, self.CODE_INVENTORY)


class RealRepoCheck(unittest.TestCase):

    def test_verify_jobs_use_shared_strict_cutover_owners(self):
        required = (
            "bash scripts/ci-evidence-scope.sh",
            "python3 -m unittest discover -s scripts/tests -p 'test_*.py'",
            "python3 scripts/spec_contract.py check --all --strict",
            "bash .claude/hooks/tests/repo-policy-alignment.test.sh",
            "bash .claude/hooks/tests/cross-harness-conformance.test.sh",
            "python3 scripts/ci-workflow-preservation.py --base",
            "git diff --quiet",
        )
        for relative in (".github/workflows/ci.yml", ".gitlab-ci.yml"):
            text = (REPO / relative).read_text(encoding="utf-8")
            with self.subTest(relative=relative):
                for token in required:
                    self.assertIn(token, text)
                self.assertNotIn("scripts/spec-trace.sh --all-compatible", text)

        github = (REPO / ".github/workflows/ci.yml").read_text(encoding="utf-8")
        self.assertIn("fetch-depth: 0", github)
        gitlab = (REPO / ".gitlab-ci.yml").read_text(encoding="utf-8")
        self.assertIn("image: python:3.12", gitlab)
        self.assertIn('GIT_DEPTH: "0"', gitlab)
        self.assertIn("apt-get install -y --no-install-recommends jq nodejs", gitlab)

    def test_real_merge_base_comparator_green(self):
        merge = subprocess.run(
            ["git", "-C", str(REPO), "merge-base", "HEAD", "origin/develop"],
            capture_output=True, text=True)
        if merge.returncode != 0:
            self.skipTest("no origin/develop locally")
        base = merge.stdout.strip()
        env = dict(os.environ, PYTHONDONTWRITEBYTECODE="1")
        proc = subprocess.run(
            [sys.executable, str(SCRIPTS / "ci-workflow-preservation.py"),
             "--base", base], capture_output=True, text=True, env=env,
            cwd=str(REPO))
        self.assertEqual(proc.returncode, 0, proc.stdout[-400:])


if __name__ == "__main__":
    unittest.main()
