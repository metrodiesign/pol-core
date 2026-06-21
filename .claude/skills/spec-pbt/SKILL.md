---
name: spec-pbt
description: Extract testable properties from requirements and write property-based tests. Use to validate correctness across the whole input space, not just examples.
argument-hint: <feature folder name (optional)>
---

# Property-Based Testing

Resolve the target spec: use $ARGUMENTS if given; if `.ai/specs/` holds more
than one feature and none was named, list them and ask — never guess. If
requirements.md is still `> Status: draft`, warn in Thai and ask for
confirmation before proceeding.

Step 1 — From the active spec's requirements.md, extract PROPERTIES: universal
statements that must hold for ALL valid inputs. Express each as:
  "For any <inputs> where <precondition>, THE SYSTEM SHALL <invariant>"
Link each to its REQ ID and note the input space / generators needed. Present the
list and let me choose which to test.

Step 2 — For the chosen properties, write property-based tests. First check for
an installed PBT framework (fast-check / Hypothesis / jqwik / proptest): if none
is installed, do NOT install one silently — either write the properties as
randomized-input loops on the project test runner (declared via SDD_TEST_CMD env,
or a package.json test script for a Node project; tests must live in the project
test directory, co-located with the logic under test), or propose the framework
as a dependency (with license + maintenance status) and wait for approval per the
project's dependency rule. Generate wide input ranges including edge cases
(empty, max, special characters). Each test cites its REQ ID.

Step 3 — When a test finds a counter-example, report the minimal failing ("shrunk")
input, then ask whether to fix the implementation, the test, or the requirement.

Delegate to the `pbt-runner` subagent when the properties touch CORE domain
logic (validation / business rules in the project test directory); run inline otherwise.
