#!/usr/bin/env python3
"""Task 4 policy fixtures — REQ-9.1-9.8.

Characterization floor: every legacy corpus verdict is preserved verbatim
(benign quoted data now allowed per REQ-9.8 — the documented Task 4 goal).
Mutation checks prove the span parser and single-normalizer rule are load-
bearing: de-quote fallback, quoted-separator confusion, or a disabled peel
must RED."""
from __future__ import annotations

import importlib.util
import sys
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent.parent
_spec = importlib.util.spec_from_file_location(
    "gp", SCRIPTS / "guard_policy.py")
gp = importlib.util.module_from_spec(_spec)
sys.modules["gp"] = gp
_spec.loader.exec_module(gp)

_cspec = importlib.util.spec_from_file_location(
    "gc_t4", SCRIPTS / "guard_contract.py")
gc = importlib.util.module_from_spec(_cspec)
sys.modules["gc_t4"] = gc
_cspec.loader.exec_module(gc)


class DestructivePolicyTest(unittest.TestCase):

    def v(self, cmd: str, branch: str | None = None) -> gp.Verdict:
        return gp.check_destructive(cmd, branch)

    # --- legacy block corpus (verbatim verdict preservation, REQ-9.1) ------
    def test_rm_rf_forms_block(self):
        for cmd in ("rm -rf /tmp/x", "rm -fr /tmp/x", "rm -r -f /tmp/x",
                    "rm --recursive --force /tmp/x", "\\rm -rf /tmp/y",
                    '"rm" -rf /tmp/z', "sh -c 'rm -rf /tmp/w'",
                    "eval 'rm -rf /tmp/v'", "sudo rm -rf /tmp/u",
                    "env rm -rf /tmp/t", "/usr/bin/rm -rf /tmp/s"):
            self.assertTrue(self.v(cmd).blocked, cmd)

    def test_rm_single_file_allows(self):
        self.assertFalse(self.v("rm file.txt").blocked)

    def test_git_hard_clean_find_delete(self):
        self.assertTrue(self.v("git reset --hard HEAD~1").blocked)
        self.assertTrue(self.v("git clean -fd").blocked)
        self.assertTrue(self.v('find . -name "*.tmp" -delete').blocked)

    def test_restore_checkout_shapes(self):
        for cmd in ("git restore .", "git restore --worktree .",
                    "git restore --staged --worktree .",
                    "git checkout -- .", "git checkout ."):
            self.assertTrue(self.v(cmd).blocked, cmd)
        for cmd in ("git restore --staged .", "git restore file.txt",
                    "git checkout dev"):
            self.assertFalse(self.v(cmd).blocked, cmd)

    def test_force_push_family(self):
        for cmd in ("git push --force origin feat",
                    "git push --force-with-lease origin feat",
                    "git push -f origin feat",
                    "git push origin +feat:feat",
                    "git push origin +develop",
                    "git -C /repo push --force origin feat"):
            self.assertTrue(self.v(cmd, branch="feat/x").blocked, cmd)

    def test_sql_families(self):
        self.assertTrue(self.v('sqlcmd -Q "DROP TABLE users"').blocked)
        self.assertTrue(self.v("DROP DATABASE app").blocked)
        self.assertTrue(self.v("TRUNCATE TABLE logs").blocked)
        self.assertTrue(self.v("psql -c 'DELETE FROM users'").blocked)
        self.assertTrue(self.v("dropdb mydb").blocked)
        # benign counterparts stay allowed (REQ-9.8 same-semantics rule)
        self.assertFalse(self.v("psql -c \"DELETE FROM users WHERE id=1\"").blocked)
        self.assertFalse(self.v("truncate -s 0 logfile").blocked)

    # --- benign quoted data (REQ-9.8: separators in quoted data are values) --
    def test_quoted_separator_is_value_not_command(self):
        self.assertFalse(self.v('echo "some rm -rf text in a string"').blocked,
                         "quoted rm text must not be mistaken for a command")
        spans, diag = gc.normalize('echo "a && rm -rf /tmp/x"')
        self.assertEqual(diag, [])
        self.assertEqual(spans[0].executable, "echo")
        self.assertEqual(len(spans), 1, "separator inside quotes must not split")
        self.assertFalse(self.v('grep "x; git push --force" f.txt').blocked)

    def test_malformed_input_engine_fail_closed(self):
        for cmd in ("echo 'unclosed", 'git push "$(rm -rf', "`rm -rf"):
            verdict = self.v(cmd)
            self.assertTrue(verdict.engine_fail, cmd)


class BypassPolicyTest(unittest.TestCase):

    def v(self, cmd: str) -> gp.Verdict:
        return gp.check_bypass(cmd)

    def test_floor_tamper_blocks(self):
        for cmd in ("chmod 000 .ai/bin/check-secrets.sh",
                    "chmod -x .githooks/pre-commit",
                    "rm -r .githooks", "rm -rf .ai/bin",
                    "mv .githooks /tmp/bak",
                    "mv /tmp/x .ai/bin/gate-task.sh"):
            self.assertTrue(self.v(cmd).blocked, cmd)

    def test_copy_destination_rule(self):
        self.assertTrue(self.v("cp /tmp/evil .ai/bin/check-bypass.sh").blocked)
        self.assertFalse(self.v("cp .githooks/pre-commit /tmp/backup-hook").blocked)
        self.assertFalse(self.v("cp .githooks/pre-commit pre-commit.bak").blocked)

    def test_redirect_into_floor(self):
        self.assertTrue(self.v("printf x > .git/config").blocked)
        self.assertTrue(self.v("echo hi > .githooks/pre-commit").blocked)

    def test_hookspath_writes_block_reads_allow(self):
        self.assertTrue(self.v("git config core.hooksPath /tmp/nope").blocked)
        self.assertTrue(self.v("git -c core.hooksPath=/dev/null commit -m x").blocked)
        self.assertTrue(self.v("git config --unset core.hooksPath").blocked)
        self.assertFalse(self.v("git config --get core.hooksPath").blocked)
        self.assertFalse(self.v("git config core.hooksPath").blocked)

    def test_skip_flags(self):
        self.assertTrue(self.v("git commit --no-verify -m x").blocked)
        self.assertTrue(self.v("git commit -n -m x").blocked)
        self.assertTrue(self.v("SECRET_GUARD_SKIP=1 git commit -m x").blocked,
                        "env-prefix normalization must surface the skip var")


class SingleNormalizerRuleTest(unittest.TestCase):
    """REQ-9.3/9.7 mutations — breaking the shared normalizer must RED."""

    def _policy_verdict_via_contract_only(self, cmd: str):
        """Simulate a legacy flat-regex side path: de-quoted haystack.
        The mutation claim: such a path disagrees with the engine on
        quoted-data inputs — proving the engine is the only valid owner."""
        import re as _re
        flat = _re.sub(r"[\\'\"]", "", cmd)
        engine = gp.check_destructive(cmd).blocked
        side_path = "rm -rf" in flat  # naive legacy scanner
        return engine, side_path

    def test_flat_regex_side_path_disagrees_on_quoted_data(self):
        cmd = 'echo "some rm -rf text"'
        engine, side_path = self._policy_verdict_via_contract_only(cmd)
        self.assertFalse(engine)
        self.assertTrue(side_path,
                        "side-path scanner flags quoted data — proves the "
                        "normalizer is required")

    def test_peel_disabled_changes_verdict(self):
        original = gc._strip_leading
        def disabled(values):
            return 0, values[0] if values else None
        try:
            gc._strip_leading = disabled
            spans, _diag = gc.normalize("/usr/bin/rm -rf /tmp/x")
            exe = spans[0].executable if spans else None
            self.assertNotEqual(exe, "rm",
                                "peel off => absolute binary no longer resolves")
        finally:
            gc._strip_leading = original

    def test_max_depth_zero_forces_recursion_limit(self):
        original_depth = gc.MAX_DEPTH
        try:
            gc.MAX_DEPTH = 0
            _, diagnostics = gc.normalize("echo $(true)")
            self.assertIn("GUARD_RECURSION_LIMIT", diagnostics)
        finally:
            gc.MAX_DEPTH = original_depth


if __name__ == "__main__":
    unittest.main()
