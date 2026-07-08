# Security Rules

> Vendor-neutral, PROJECT-SCOPED. Distilled from the global operating rules.
> These rules apply to EVERY agent (Claude, Codex, OpenCode, Pi) and to humans.

## Enforcement model: a cross-agent floor + a per-harness layer

Enforcement is layered. The bottom layer is the same for everyone; the top layer exists
only for harnesses that support pre-tool hooks.

| Tier | Mechanism | Covers | Notes |
|---|---|---|---|
| 1. Git + CI (the floor) | `.githooks/` (enabled via `core.hooksPath`) + `.github/workflows/ci.yml`, both calling `.ai/bin/check-*.sh` | **ALL agents + humans** | Cannot be bypassed by choosing a different agent. This is the real, cross-agent enforcement. |
| 2. Harness pre-tool hook | Claude: `.claude/hooks/*` -> `.ai/bin/`; Codex: `.codex/config.toml` `[hooks]` -> `.codex/hooks/*` -> `.ai/bin/` (single source `config.toml`; the legacy `.codex/hooks.json` was removed — Codex 0.139 loaded both, see issue #26 — and these in-session hooks fire only after interactive `/hooks` trust); OpenCode: `.opencode/plugins/ai-guard.js` -> `.ai/bin/` | Claude, Codex, OpenCode | Pre-execution interception. Pi has no core pre-tool hook, so it falls back to Tier 1 + Tier 3. |
| 3. Procedural | root `AGENTS.md` + `.ai/roles/` + `.ai/workflows/` instruct the agent to run `.ai/bin/check-*` before risky commands | ALL agents (the only AI-side layer Pi has) | Advisory; relies on the agent following instructions. The git+CI floor backstops it. |

**Hooks are Claude/Codex/OpenCode-only. The git + CI floor is the enforcement that
spans every agent.** When in doubt about whether a harness layer caught something,
trust Tier 1: a clean commit and a green CI run are the proof.

All three tiers call the SAME single-source check logic in `.ai/bin/`
(`check-destructive.sh`, `check-bypass.sh`, `check-secrets.sh`, `gate-task.sh`). Do not
fork or weaken these checks per harness.

## The rules

### Secrets

- Never commit any secret: API key, token, password, private key, connection string,
  or credential file.
- `.env` and `.env.*` must always be in `.gitignore`. Only `.env.example` (with fake
  values) may be committed.
- Never hardcode a credential — read it from an environment variable or a secret
  manager.
- Never log sensitive data (tokens, passwords, PII).
- If a secret leaks: rotate/revoke it immediately. Deleting the commit or force-pushing
  is NOT enough — history still holds it.
- **Enforced by:** `.githooks/pre-commit` -> `.ai/bin/check-secrets.sh` and the CI
  secret-scan job, for ALL agents and humans (Tier 1). Claude/Codex/OpenCode also get
  pre-execution interception via their harness hook (Tier 2).
- **Detection details** (so the rule and the engine agree): the generic detector
  inspects the matched `key=VALUE` substring, not the whole line — a placeholder word
  in a trailing comment no longer whitelists a real secret; only a placeholder VALUE
  passes. It covers secret values containing `@ ! $ # %` punctuation (`.` is excluded so
  dotted member-access like `process.env.API_TOKEN` is not misread as a secret), `*_SECRET`
  names (e.g. `JWT_SECRET`), and connection strings with embedded credentials
  (`scheme://user:<password>@host`, an explicitly forbidden secret). The forbidden
  dotenv-filename rule matches real dotenv files only (basename `== .env`, starting with
  `.env.`, or ending in `.env`), not arbitrary names containing `.env.` mid-string. The
  guard's own adversarial fixtures under `.claude/hooks/tests/` are excluded from the scan.
  Pre-existing true
  blocks (AWS `AKIA`, Stripe, GitHub PAT, PEM private-key block, real `.env`,
  `.pem`/`.key`) remain intact. `SECRET_GUARD_SKIP=1` is a STAGED-path-only escape
  hatch for the in-session human; it is IGNORED in `--all`/CI mode (a non-bypassable
  hard gate).

### Destructive operations

- No `DROP` / `DELETE` / `TRUNCATE` on production data without a `WHERE` clause and an
  explicit human confirmation.
- No `rm -rf`, `git reset --hard`, or `git clean -fd` without confirming the target
  first.
- DB migrations require a rollback plan and a backup before running on production.
- Any destructive command on production must be confirmed by a human.
- **Enforced by:** `.ai/bin/check-destructive.sh` (exit 2 = block), invoked by the
  harness pre-tool hook for Claude/Codex/OpenCode (Tier 2). Pi and humans rely on the
  procedural instruction in `AGENTS.md` (Tier 3) plus the git + CI floor (Tier 1).
- **What the engine actually blocks** (so docs and the engine agree exactly):
  - `rm` recursive+force in every spelling — `-rf`/`-fr`/`-r -f`/`--recursive --force`,
    and the same when written `\rm`, `"rm"`, `'rm'`, or wrapped in `sh -c '...'` /
    `bash -c '...'` / `eval '...'`. It inspects ALL argv, not just the first word.
  - `git reset --hard`, `git clean -f`, `find -delete`.
  - SQL Destructive-Ops: `DROP TABLE`, `DROP DATABASE`, `TRUNCATE`, `dropdb`, and
    `DELETE FROM ...` with NO `WHERE` clause. A `DELETE ... WHERE ...` is allowed.
  - Branch/force-push protection (see Branch / push discipline below): force push,
    `+refspec`, `--mirror`, `--all --force`, and direct push/commit to `main`/`develop`
    including a fully-qualified `HEAD:refs/heads/main`.
  - **Intentionally NOT blocked** (high false-positive risk; the Tier 1 git hooks + CI
    are the durable floor for these): `git checkout`/`restore`, `git branch -D`,
    `find -exec`. Documented gap, not an oversight.
  - Known fail-safe trade-off: the engine treats the command as a flat string and does
    not parse shell quoting, so destructive-looking content inside a quoted string may
    over-block, by design. The `.ai/bin` engine and the Claude adapter are tested for
    identical exit codes.

### Bypass prevention

- Do not attempt to disable, skip, or route around the guards (no
  `--no-verify`, no overriding `core.hooksPath`, no `HUSKY=0`-style escapes, no editing
  the guards to weaken them).
- **Enforced by:** `.ai/bin/check-bypass.sh` (exit 2 = block) via the harness pre-tool
  hook (Tier 2); the git + CI floor (Tier 1) re-checks on the server side regardless.
- **What the bypass engine catches** (expanded — superseding the old "only inspects
  git commands" description): a `-n`/`--no-verify` skip-verify flag at ANY position in a
  `git commit`, including after a quoted commit message and after a `\` line
  continuation (it strips quoted segments and flattens newlines before scanning), while
  a commit message that merely mentions `-n` still passes. It also independently blocks
  tamper that disables or overwrites the enforcement floor — `chmod`/`mv`/`rm`/redirect
  against `.githooks/*`, `.ai/bin/check-*.sh`, `.ai/bin/gate-task.sh`, or pointing
  git's `core.hooksPath` / `hooksPath` away — even when the command contains no
  standalone `git` token. The CI guard-regression suite (below) is the backstop for the
  "do not weaken the guards" rule.

### CI gate

- A PR may merge only when CI passes as a required check.
- Never merge past a failing check.
- Never leave `.only` / `.skip` in committed tests.
- Coverage must not drop below the project threshold.
- **Enforced by:** `.github/workflows/ci.yml` as a required check for ALL contributors
  (Tier 1), triggered on both `pull_request` and `push` to `main` AND `develop`.
  Server-side branch protection is the gate that cannot be skipped locally.
- **Checks CI actually runs** (so the doc matches the workflow): the guard-regression
  suite (every `.claude/hooks/tests/*.test.sh`, so the single check engine cannot be
  weakened silently), the full-tree secret scan (`.ai/bin/check-secrets.sh --all` with
  `SECRET_GUARD_SKIP` force-cleared), and spec-trace REQ coverage (Python 3.12 pinned).
  A dependency vulnerability audit is REQUIRED in CI for any project that ships a package
  manifest (see Dependencies below); this framework repo ships no runtime deps, so its CI
  runs the guard-suite + secret scan + spec-trace instead of an audit step. There is
  **no lint script** in this project, so CI runs no lint step — lint is not-yet-wired,
  not a silent failure.

### Deploy / release

- A production deploy must always go through staging first.
- Every release must have a rollback plan.
- Do not deploy to production on a Friday evening or before a long holiday, except for
  an emergency hotfix.
- Every release is tagged with a version + a changelog entry.
- **Enforced by:** procedural discipline (Tier 3) + release-pipeline checks where they
  exist. (This project ships a static frontend with no real backend; treat these as the
  standard to follow if a deploy pipeline is added.)

### Dependencies

- Do not add a new dependency without reviewing its license and maintenance status, and
  getting approval first (this project says: prefer the existing stack; new libraries
  need a stated reason and approval).
- Lock files (`package-lock.json`, etc.) must always be committed.
- Do not pin floating versions (`*` / `latest`) on a production dependency.
- A dependency vulnerability audit (the package manager's audit command or equivalent) is
  REQUIRED in CI for any project that ships a package manifest. When auditing, separate a
  dev-only chain from prod-core before acting — never force-fix a core dependency into a
  breaking downgrade.
- **Enforced by:** for a project that ships a package manifest, a blocking dependency
  audit in CI (Tier 1); this framework repo ships no runtime deps, so its CI has no audit
  step. Lockfile presence + review approval for new dependencies still apply (Tier 3, see
  [REVIEW_PROTOCOL.md](REVIEW_PROTOCOL.md)).

### Branch / push discipline

- Never push directly to `main` or `develop`; everything goes through a PR.
- Never force-push.
- Never commit directly without review.
- **Enforced by:** `.githooks/pre-push` (blocks pushes to `main`/`develop` and
  non-fast-forward force pushes, ref-based) for ALL agents and humans (Tier 1).

### Product security (pol-core) — payment platform hard guardrails

> These are DESIGN-level constraints, not automated `.ai/bin` checks — no script blocks them.
> Enforced by review + the spec gates + "stop and ask". Violating one changes the platform's
> legal status (ใบอนุญาต) or expands PCI scope. เจอ requirement/ticket/ไอเดียที่นำไปสู่ข้อใด → หยุดถามก่อน
> อย่า implement เอง. Full context: [PROJECT_CONTEXT.md](PROJECT_CONTEXT.md) (Non-Goals) + [ARCHITECTURE.md](ARCHITECTURE.md) (cross-cutting).

- **PCI SAQ A — redirect-only, ห้ามแตะข้อมูลบัตร.** No collect/store/transmit/tokenize PAN. ห้ามมี
  card input field / hosted-fields / iframe / Omise.js / display-QR บนโดเมนเรา. ใช้ **full redirect ไปหน้า PSP เท่านั้น**
  (2C2P hosted page · Omise บัตร = Links API `paymentUri` · Omise PromptPay = **Payment Links+ `transaction_url`** · Omise ผ่อน/e-wallet = source+charge `authorizeUri`). flow แบบ non-redirect = ห้าม.
  **กับดัก PromptPay:** Omise **direct source+charge** คืน QR `scannable_code.image.download_uri` (offline, ต้องแสดง QR เอง = ขัด SAQ A) — PromptPay ต้องผ่าน **Payment Links+ hosted page** เท่านั้น.
- **ไม่ถือเงิน (out of funds flow).** No settlement / payout / money ledger / wallet / float / escrow / disbursement.
  เงิน settle จาก PSP เข้าบัญชี merchant ของบริษัทโดยตรง. Reconciliation = **reporting เท่านั้น** ห้ามลอจิกเคลื่อนเงิน/ปรับยอดจริง.
- **Credential vault — สินทรัพย์อ่อนไหวสุด.** PSP key เก็บใน vault: **envelope encryption — per-tenant KEK ใน KMS/HSM**,
  DEK ต่อ secret, เก็บคนละที่กับ config DB. เก็บ **key id + version** + มี **rotation / re-encrypt runbook**.
  field `secrets.*` เป็น **write-only** — API อ่านกลับต้อง **mask เสมอ** (`••••3a9f`) ห้ามส่ง plaintext คืน. ห้าม log.
  (SQL Always Encrypted เข้าเกณฑ์เฉพาะเมื่อ CMK อยู่ใน external Key Vault/HSM แยกจาก config DB.)
- **Webhook = source of truth.** อัปเดตสถานะการจ่ายจาก webhook ที่ **verify ลายเซ็น + idempotent + fetch-to-confirm** เท่านั้น
  (`IWebhookVerifier`). **ห้ามตัดสินสถานะจาก browser return/redirect** (return handler = UX เท่านั้น).
  **ห้าม trust tenant/PSP จาก URL path ก่อน verify signature** — resolve connection จาก path/signed path → verify webhook secret → fetch-to-confirm ค่อยเชื่อ.
- **Multi-tenant isolation (RLS) — data-layer floor.** ชั้นจริง = **SQL Server native RLS + `SESSION_CONTEXT('TenantId')`**
  set ต่อ request (ไม่พึ่ง app code). EF global query filter = ชั้นสะดวกเสริม **ไม่ใช่** floor. **ban raw SQL / `IgnoreQueryFilters`**
  ที่ข้าม tenant scope + test พิสูจน์ leak ปิด (รวม pooled-connection ไม่ retain tenant เดิม). leak ข้าม tenant = ช่องโหว่ร้ายแรง.
- **แยก authz scope Admin ↔ Tenant ให้ขาด.** endpoint อำนาจสูง (cross-tenant / approve / config / vault) ต้องเรียกผ่าน
  session ของ Tenant Console **ไม่ได้**. การแยกเป็น 2 แอปเป็นแค่หน้าบ้าน — เส้นป้องกันจริงคือ backend authorization.
  Identity: verify Google id_token (sig/`iss`/`aud`/exp/`email_verified`) → `hd` guard → lookup ตาราง identity ของ console นั้น → scope `TenantId`.
  **Admin cross-tenant bypass RLS** ผ่าน **DB principal แยก** (admin connection) เท่านั้น — tenant console principal ทำไม่ได้ + ทุก bypass มี reason + correlation id → audit.
- **Maker-checker** สำหรับ action อ่อนไหว: approve tenant ใหม่, เปลี่ยน routing rule, แก้ allowlist.
- **Captive allowlist.** เปิดเฉพาะ vPrivilege / vCommerce / vSouvenir. ห้าม public/self-serve onboarding สำหรับคนนอก.
- **Idempotency.** webhook/payment ประมวลผลซ้ำไม่ได้ — unique key DB `(psp, eventId)` **และ** `(psp, externalChargeId, normalizedStatus)`
  (กัน PSP replay ด้วย event id ต่าง / ไม่มี stable id) + guard ที่ fetch-confirmed transition `(paymentId, transition)`, atomic upsert ใน tx.
  publish `PaymentPaid` ผ่าน **outbox** (เขียนใน tx เดียวกับ transition) + dispatcher poll ด้วย lock/lease + poison/DLQ + idempotent consumer. TTL = cleanup ไม่ใช่ guard หลัก.
- **Provisioning = saga (ไม่ใช่ single transaction).** DB กับ vault คนละ store → atomic tx เดียวเป็นไปไม่ได้.
  `PendingProvisioning` → write DB → write vault (idempotency key) → verify secrets → **activate ขั้นสุดท้าย** → compensation/retry ถ้าล้ม. idempotent ด้วย tenant key.
- **Audit log** append-only + **tamper-evident** (immutable table policy / hash-chain / WORM export) + actor correlation id: actor / scope / before-after / เหตุผล.
- **Spec-lint gate (CI-enforced, ไม่ใช่ design-level ล้วน).** regex/checklist fail บน: card field / `Omise.js` / hosted-fields / iframe จ่าย / display-QR ·
  response มี secret field ไม่ mask · query/handler ไม่มี tenant scope · term ของ 7 Non-Goals — + allowlist docs/fixtures (กัน false-pos) + human security checklist ควบ. hook เข้า `.ai/bin` + spec-trace.

## When a guard fires

A blocked command means the floor is working. Do not try to bypass it (that itself is
blocked). Read the message, fix the underlying problem, and if the rule looks wrong for
a legitimate case, stop and ask a human — do not weaken the guard.
