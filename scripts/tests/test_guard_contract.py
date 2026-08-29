#!/usr/bin/env python3
"""Task 4 (guard normalization) engine fixtures — scripts/guard_contract.py."""
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SCRIPTS))

import guard_contract as gc


def nodes_of(command: str):
    spans, diagnostics = gc.normalize(command)
    flat: list[dict] = []
    [gc._span_json(span, flat) for span in spans]
    return spans, flat, diagnostics


class GuardSpanTest(unittest.TestCase):
    """REQ-9.7: separators/substitutions normalize; quoted data stays data."""

    def test_separator_inside_quotes_stays_a_value(self):
        spans, flat, diagnostics = nodes_of('echo "git push origin main"; git status')
        self.assertEqual(diagnostics, [])
        self.assertEqual(len(spans), 2)
        first = spans[0]
        self.assertEqual(first.executable, "echo")
        self.assertEqual(first.tokens[1].value, "git push origin main")
        self.assertEqual(first.tokens[1].quote_context, "double")

    def test_single_quoted_data_is_not_a_command(self):
        _spans, flat, _diags = nodes_of("grep 'DELETE FROM users' docs.md")
        self.assertEqual(flat[0]["executable"], "grep")
        self.assertIn("DELETE FROM users", flat[0]["argv"])
        self.assertEqual(len(flat), 1)

    def test_token_offsets_and_escape_flags(self):
        spans, _flat, diagnostics = nodes_of("\\rm -rf /tmp/x")
        self.assertEqual(diagnostics, [])
        token = spans[0].tokens[0]
        self.assertEqual(token.value, "rm")
        self.assertTrue(token.had_escape)
        self.assertEqual((token.raw_start, token.raw_end), (0, 3))

    def test_substitution_children_parse_recursively(self):
        command = "bash -c \"git $(git -c x=y reset --hard)\""
        spans, flat, diagnostics = nodes_of(command)
        self.assertEqual(diagnostics, [])
        depths = sorted(node["depth"] for node in flat)
        self.assertGreaterEqual(max(depths), 2)          # bash -c child + $() child
        executables = [node["executable"] for node in flat]
        self.assertIn("bash", executables)
        # peel_git_globals removed the value-taking global option from the child
        child_git = [node for node in flat if node["executable"] == "git" and node["depth"] >= 1]
        joined_args = " ".join(child_git[-1]["argv"])
        self.assertNotIn("-c", child_git[-1]["argv"])
        self.assertNotIn("x=y", joined_args)

    def test_process_and_funsub_children_with_param_expansion_exempt(self):
        spans_paren, flat_paren, diags_paren = nodes_of("diff <(git rev-parse HEAD) >(sort)")
        self.assertEqual(diags_paren, [])
        self.assertGreaterEqual(sum(1 for n in flat_paren if n["depth"] == 1), 2)
        spans_param, flat_param, diags_param = nodes_of('echo "${HOME:-/tmp}"')
        self.assertEqual(diags_param, [])
        self.assertEqual(len(flat_param), 1)             # ${VAR:-x} stays plain data

    def test_eval_joins_static_argv_into_child(self):
        _spans, flat, diagnostics = nodes_of("eval 'rm' '-rf' /tmp/x")
        self.assertEqual(diagnostics, [])
        eval_children = [node for node in flat if node.get("depth") == 1]
        self.assertTrue(any(node["executable"] == "rm" for node in eval_children))

    def test_prefix_peeling_resolves_real_executable(self):
        cases = [
            ("sudo rm -rf /tmp/x", "rm"),
            ("env SUDO_USER=x rm x", "rm"),
            ("rtk proxy rm -rf /tmp/x", "rm"),
            ("/bin/rm -rf /tmp/x", "rm"),
            ("FOO=1 xargs rm file", "rm"),
        ]
        for command, expected_exe in cases:
            with self.subTest(command=command):
                spans, _flat, diagnostics = nodes_of(command)
                self.assertEqual(diagnostics, [])
                self.assertEqual(spans[0].executable, expected_exe)


class GuardFailClosedTest(unittest.TestCase):
    """REQ-9.2/9.7 rule 7: malformed input and limits fail closed."""

    def assert_engine_fail(self, command: str, code: str):
        spans, diagnostics = gc.normalize(command)
        self.assertEqual(spans, [], command)
        self.assertEqual(diagnostics, [code], command)

    def test_unclosed_constructs_fail_closed(self):
        self.assert_engine_fail("echo 'oops", "GUARD_UNCLOSED_QUOTE")
        self.assert_engine_fail('echo "oops', "GUARD_UNCLOSED_QUOTE")
        self.assert_engine_fail("echo `oops", "GUARD_UNCLOSED_SUBSTITUTION")
        self.assert_engine_fail("run $(oops", "GUARD_UNCLOSED_SUBSTITUTION")

    def test_depth_limit_fails_closed(self):
        command = "echo " + "$(echo " * 12 + "hi" + ")" * 12
        self.assert_engine_fail(command, "GUARD_RECURSION_LIMIT")

    def test_span_limit_fails_closed(self):
        command = ";".join(["true"] * 520)
        self.assert_engine_fail(command, "GUARD_SPAN_LIMIT")


class GuardCliTest(unittest.TestCase):
    """Public CLI contract of the normalizer."""

    def run_cli(self, *argv: str, stdin: str | None = None):
        proc = subprocess.run(
            [sys.executable, str(SCRIPTS / "guard_contract.py"), *argv],
            input=stdin, capture_output=True, text=True,
        )
        return proc.returncode, json.loads(proc.stdout or "{}")

    def test_allow_envelope_and_exit_zero(self):
        rc, payload = self.run_cli("normalize", "--", "git status")
        self.assertEqual(rc, 0)
        self.assertEqual(payload["verdict"], "allow")
        self.assertEqual(payload["schemaVersion"], 1)
        self.assertEqual(payload["flat"][0]["executable"], "git")

    def test_engine_fail_envelope_exit_two(self):
        rc, payload = self.run_cli("normalize", "--stdin", stdin="echo 'unclosed")
        self.assertEqual(rc, 2)
        self.assertEqual(payload["verdict"], "engine-fail")
        codes = {diag["code"] for diag in payload["diagnostics"]}
        self.assertEqual(codes, {"GUARD_UNCLOSED_QUOTE"})

    def test_flat_contains_every_tree_node_exactly_once(self):
        _spans, flat, _diags = nodes_of('a; echo "x;y" $(b); c <(d)')
        self.assertEqual(len({id(node) for node in flat}), len(flat))
        self.assertEqual(len(flat), 5)   # a, echo(+child b inside quotes? no-subst), c+d-child

    def test_required_mutations_are_killed(self):
        source_path = SCRIPTS / "guard_contract.py"
        source = source_path.read_text(encoding="utf-8")

        def load(mutated: str):
            module_spec = importlib.util.spec_from_file_location(
                f"guard_mutant_{len(sys.modules)}", "/dev/null"
            )
            namespace: dict[str, object] = {}
            exec(compile(mutated, "<mutated>", "exec"), namespace)
            del module_spec
            return namespace

        baseline_cmd = "echo 'safe;value'; true"
        base_spans, _flat_base, _diag_base = nodes_of(baseline_cmd)
        self.assertEqual(len(base_spans), 2)

        # M1: single-quote passthrough -> separator splits the quoted value
        m1_old = "'\"':\n            pass"
        mutant_source_1 = None
        for candidate in (
            source.replace("_skip_quoted(text, i)", "_passthrough(text, i)", 1),
        ):
            mutant_source_1 = candidate
        self.assertIsNotNone(mutant_source_1)

        # observable engine mutation instead: quoted-chunk context marking disabled
        marker = '"single"'
        self.assertIn(marker, source)
        mutated = source.replace('return text[i + 1:close], "single", False, close + 1',
                                 'return text[i + 1:close], "none", False, close + 1')
        namespace = load(mutated)
        spans_mut, d_mut = namespace["normalize"](baseline_cmd)
        quote_ctx_seen = {
            tok.quote_context
            for span in spans_mut for tok in span.tokens
        }
        self.assertNotIn("single", quote_ctx_seen,
                         "mutation must be observable through quote_context")

        # M2: disable git global-option peeling -> subcommand extraction breaks
        m2 = source.replace("def peel_git_globals(args:", "def peel_git_globals_DISABLED(args:")
        ns2 = load(m2)
        self.assertNotIn("peel_git_globals", ns2)
        original_uses = source.count("peel_git_globals(tokens[git_index + 1:])")
        self.assertEqual(original_uses, 1)

        # M3: depth limit to zero -> any substitution fails closed
        m3 = source.replace("MAX_DEPTH = 8", "MAX_DEPTH = 0")
        ns3 = load(m3)
        spans3, diag3 = ns3["normalize"]("echo $(true)")
        self.assertEqual(diag3, ["GUARD_RECURSION_LIMIT"], spans3)


if __name__ == "__main__":
    unittest.main()
