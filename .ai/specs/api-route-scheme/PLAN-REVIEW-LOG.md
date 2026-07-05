# Plan Review Log: API Route Scheme migration

Act 1 (grill) complete — plan locked with the user via the preceding /grill-me session (approved requirements.md 2026-07-05 + design.md). MAX_ROUNDS=5. PLAN_FILE=.ai/specs/api-route-scheme/PLAN.md. THREAD_ID=019f3261-4653-7c12-a86e-d56d0b54e189.

## Round 1 — Codex

Verdict: REVISE.

**High**
- PLAN:10 "sub-path unchanged" contradicts the `/payment-sessions`→`/payments/sessions`, `POST /admins`, `/checkout`, `/reports/reconciliation` renames; risks duplicated area segments. Fix: full old→new mapping table for all 47 endpoints incl method/name.
- CorsExtensions.cs:87 CORS selects the credentialed policy by `/admin` and `/producer` path prefix; moving to `/api/v1/admins`/`producers` drops BFF cookie XHR to the tenant default (no credentials). Fix: update path selector + CORS tests.
- Program.cs:765 OIDC callback is not a mapped endpoint (handler uses `CallbackPath` from config); plan implies mapping it, so the arch test won't catch it. Fix: update `AdminOidcOptions`/`ProducerOidcOptions`, `appsettings*`, login-redirect + callback tests, redirect_uri.
- PLAN:13 arch-test regex uses `v\d+` though the goal is a single global `v1`; `/api/v2/...` would pass with no design. Fix: enforce literal `/api/v1/` until a real v2 spec exists.

**Medium**
- Program.cs:860 `Results.Created(...)` hardcodes legacy `Location` (`/admin/...`, `/producer/...`). Fix: LinkGenerator or update all `Location` + assert headers.
- Program.cs:243 OpenAPI descriptions name legacy login paths; "regenerate, no manual doc edit" is false for these strings. Fix: update descriptions + OpenAPI path/security tests.
- PLAN:15 "legacy→404 per area" too weak; one legacy endpoint can survive inside an area and still pass. Fix: assert legacy 404 for every old method+path incl OIDC callback paths.
- PLAN:13 EndpointDataSource test misses middleware routes (OIDC callback) and CORS behavior. Fix: targeted integration tests for CORS preflight, OIDC challenge redirect_uri, callback, `Location`.

### Claude's response

Verified all 8 against the code — every one confirmed. ACCEPTED ALL:
- **CORS (H2):** confirmed `PolCorsPolicyProvider:87` path-selects by `/admin`/`/producer`. Added Approach step 5 + integration test (step 8). This breaks the "path-only" thesis — reframed Goal/Key-decisions accordingly.
- **OIDC callback (H3):** confirmed `CallbackPath` in `AdminAuthOptions.cs`/`ProducerOidcOptions.cs` + `appsettings.json`, middleware-handled. Added Approach step 4 (move via config, not routing) + note the arch test can't see it → integration test. `ErrorPath`/`RegisterUrl`/`DefaultReturnPath` confirmed frontend (`localhost:5200`) → out of scope.
- **Location (M1):** confirmed 5 hardcoded `Results.Created` legacy Locations (860/987/1136/1293/1425). Added Approach step 6 + assert.
- **OpenAPI strings (M2):** confirmed lines 243/251. Folded into step 6.
- **Full inventory (H1):** enumerated all 47 + 2 middleware callbacks. Found the admin-accounts sub-collection (`/admins/{id}/suspend|tenants|roles`) my O2 missed — resolved to the `admins` area root (no doubling; guid vs literal disambiguates). Full table deferred to tasks.md; PLAN wording de-contradicted.
- **Literal v1 (H4):** ACCEPTED — reversed my earlier `v\d+` choice. Guard is fail-closed on `/api/v1`; a v2 spec updates it. Will amend requirements REQ-1.5 on convergence.
- **Legacy 404 completeness (M3):** step 9 now asserts every old method+path (all 47) + 2 callback paths, generated from the mapping table.
- **Middleware/CORS test gap (M4):** step 8 adds CORS-preflight + OIDC-challenge/callback + Location integration tests.

Nothing rejected. Requirements.md/design.md sync (REQ-5.4 carve-out for path-bearing headers + CORS/OIDC coupling REQ) deferred until Codex converges, to avoid re-churning them each round.

## Round 2 — Codex

Verdict: REVISE. Prior substantive findings confirmed addressed in PLAN (CORS, OIDC callback config, Location, OpenAPI strings, literal v1, complete legacy 404, targeted tests). Remaining blockers = artifact consistency:
- PLAN:10 references a "complete old→new table" in tasks.md, but tasks.md does not exist.
- requirements.md:20 still says "path เท่านั้น"; REQ-5.4:78 forbids changing a response header, but PLAN must change `Location`.
- requirements.md:29 REQ-1.5 still accepts `/api/v{n}`; PLAN is literal `/api/v1`.
- design.md still "path-only" (:5), regex `v\d+` (:123), legacy 404 one-per-area (:155).

### Claude's response

All valid — this was exactly the deferred artifact sync; Codex is right that convergence requires the approved specs to match the plan. Done now, nothing rejected:
- **requirements.md** amended, re-stamped `approved 2026-07-05, amended 2026-07-05`: Overview reworded (not "path เท่านั้น"); REQ-1.5 → literal `/api/v1` fail-closed; REQ-5.4 carve-out (path-bearing headers DO update; body/status/method/query unchanged); NEW **REQ-9** (CORS selector, `Location` headers, preflight, integration tests); **REQ-3.7** (filter-membership preservation); **REQ-6.2** (callback via config, not routing); REQ-5.3 → every old method+path + 2 callbacks; Codex-amendments note in the findings log.
- **design.md** revised: reframed (not path-only); FULL 47-row + 2-callback old→new table (Data Models); literal-`v1` arch regex; legacy-404-for-all; new CORS / OIDC-callback / `Location` / OpenAPI-string handling + updated sequence diagram; traceability incl REQ-9. Kept draft.
- **PLAN.md**: table reference now points to design.md; sync marked done.

## Round 3 — Codex

Verdict: REVISE. Prior blockers resolved (mapping table 47+2, REQ-5.4 carve-out, REQ-9, literal `/api/v1`, path-coupled sections). New:
- Contradiction: design.md:26 (admins "2 group ref: unfiltered anon entry") vs design.md:127 ("admin login on filtered group as today") vs requirements REQ-3.7 ("admin login SHALL NOT gain filter") — but the actual code has admin login under `AdminCsrfFilter`. Fix: state admin `GET /auth/login` stays filtered (GET-exempt), or change membership and sync all three.
- design.md still `Status: draft`; not implement-ready per workflow.

### Claude's response

Finding 1 ACCEPTED (my error — I over-generalized producer's unfiltered anon tier onto admin). Fixed across all three: design.md:26 diagram (admins = single filtered group; only producers is 2-tier), design.md:127 paragraph (admin all-filtered/GET-exempt, producer login+register unfiltered), requirements REQ-3.7 (precise per-console statement), PLAN step 2. The invariant is now "preserve EXACT current filter membership" — NOT "make anon entries unfiltered uniformly."

Finding 2: design's `draft` status is the correct pre-approval state — approval is the USER's `/spec-design` gate, which happens at the grill-me-codex sign-off AFTER this plan review converges. It is not a plan defect and I will not self-approve. Flagged to Codex to judge plan soundness, not the approval bit.

## Round 4 — Codex

Verdict: **APPROVED**. Confirmed resolved: mapping table (47 mapped + 2 OIDC callbacks), REQ-5.4 carve-out for `Location`/redirect path-bearing data, REQ-9 (CORS/OIDC/Location/test-gap), REQ-1.5 literal `/api/v1`, admin/producer filter membership (admin single `AdminCsrfFilter` group, producer login/register unfiltered). One minor non-blocking wording nit: design.md traceability row said "2-tier group" broadly though 2-tier is producer-only.

### Claude's response

Fixed the wording nit (design.md traceability row → "admin single filtered group; producer 2-tier"). No functional change.

## Resolution — CONVERGED (APPROVED after 4 Codex rounds)

Act 1 (grill) locked intent; Act 2 (Codex) hardened the plan over 4 rounds. Plan is APPROVED. design.md stays `draft` pending the user's `/spec-design` approval at sign-off. No code written during either act.
