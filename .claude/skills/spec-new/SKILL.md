---
name: spec-new
description: Start a new feature spec. Use at the beginning of any non-trivial feature to choose a workflow and gather requirements before coding.
argument-hint: <short description of the feature>
---

# Start a Feature Spec

The feature idea is: $ARGUMENTS

Step 0 — Right-size first: a trivial one-sentence change needs NO spec — just make the edit.
Use this spec flow only for non-trivial work; for small but well-understood features prefer
`/spec-quick` (writes all artifacts, no approval gates).

Step 1 — Recommend ONE workflow and explain why in two sentences:

- Requirements-First (Requirements → Design → Tasks): I know the behavior I want;
  architecture is flexible. Best for product/customer-driven features.
- Design-First (Design → Requirements → Tasks): I have an architecture in mind or
  strict non-functional constraints (latency, compliance).
- Quick (`/spec-quick`): well-understood feature, no approval gates wanted.

Step 2 — Create the spec folder at `.ai/specs/<kebab-case-name>/`.

Step 3 — Ask me ALL clarifying questions you need in a single message:
who the user is, what they want, why, success criteria, edge cases, constraints.

Do NOT generate any artifact yet. Wait for my answers, then tell me to run
`/spec-requirements` (or `/spec-design` for Design-First).
