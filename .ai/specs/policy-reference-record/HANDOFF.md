> Status: unknown
# HANDOFF — policy-reference-record (orchestrated build)

> Cross-task handoff log. Each teammate implements ONE task then appends a section
> here for the next teammate. tasks.md Evidence blocks stay the formal DoD record;
> this file carries forward-looking gotchas/decisions that do NOT fit Evidence
> (new file locations, retired tokens, changed test counts, sharp edges hit).
>
> Orchestration: Opus lead spawns one fresh-context Sonnet teammate per task in
> dependency order (1 -> 2 -> 3 -> 4 -> 5 -> 6 -> 7). Each reads the spec
> (requirements.md, design.md, tasks.md) + this file before starting.

## Environment (verified by lead before T1)
- Branch: `feat/policy-reference-record` (do NOT commit or push — lead handles PR at the end).
- SQL Server dev DB: docker `pol-db` up on `localhost:11433` (healthy).
- Integration tests: `source .env.integration` in the SAME Bash call as `dotnet test`.
- `dotnet 10.0.300`. Gate scripts: `scripts/check-rename-identifiers.sh`, `scripts/spec-trace.sh`, `scripts/spec-state.sh`.

---

<!-- teammates append below, one "## T<n> — <task title>" section each -->

## T1 — Rename OrderLine -> OrderItem

Status: DONE. All build/gate/test/integration verification green (see tasks.md Evidence block under task 1
for exact commands + numbers). Branch left uncommitted (working tree only) per instructions — lead handles
the PR.

**New canonical names you MUST use from here on (task 2-7):**
- Entity: `Orders.Domain.Items.Item` (was `Orders.Domain.Lines.Line`). Sibling checkout: `Checkouts.Domain.Items.Item`.
- `Order.Items` (was `Order.Lines`), `Session.Items` (was `Session.Lines`) — internal domain collection properties.
- `OrderItemInput` (was `OrderLineInput`), `CheckoutItemInput` (was `CheckoutLineInput`) — domain factory inputs.
- Read models: `OrderItemListItem`/`OrderItemDetail` (were `OrderLineListItem`/`OrderLineDetail`) — their WIRE
  property is still literally named `Lines` (`OrderListItem.Lines`, `OrderDetailView.Lines`) — deliberately NOT
  renamed, see "Sharp edges" below.
- `Orders.Domain.Items.RevealAudit` (namespace moved, type name unchanged) — property `OrderItemId` (was `OrderLineId`).
- `Contracts.CheckoutConfirmedItem` (was `CheckoutConfirmedLine`), `CheckoutConfirmed.Items` (was `.Lines`) — L8
  rename, deliberate per REQ-7.6, safe because this event has no external-compat contract (see its own doc comment).
- DB: table `shop.OrderItems` (was `OrderLines`), `shop.OrderItemRevealAudits`, `shop.CheckoutSessionItems`,
  column `OrderItemId` on `OrderItemRevealAudits`. **`ItemPolicy` (task 2/4) targets `OrderItemId`, table name
  `OrderItemPolicies`/`OrderItemPolicyAudits`** per design — these are the names to build against.
- EF config classes renamed `LineConfiguration` -> `ItemConfiguration` in all 4 locations (Orders/Checkouts x
  Infrastructure/Persistence.MerchantRuntime), moved into `Items/` folders alongside the entity.
- `MerchantRuntimeDbContext` DbSets: `OrderItems`, `OrderItemRevealAudits`, `CheckoutSessionItems`.

**Retired tokens — the gate (`scripts/check_rename_identifiers.py`) now fails CI if any of these
reappear as live code:** `OrderLine`, `Line` (bare), `OrderLineId`, `OrderLineInput`, `OrderLineListItem`,
`OrderLineDetail`, `CheckoutSessionLine`, `CheckoutLineInput`, `CheckoutConfirmedLine`. Don't reintroduce them
even as file-local aliases outside the L6 exception mechanism.

**Sharp edges hit — read before touching anything Order/Checkout-adjacent:**
1. **Wire DTOs were deliberately NOT fully renamed.** `OrderListItem.Lines` and `OrderDetailView.Lines` (the
   `GET /orders` / `GET /orders/{id}` JSON response shape) still say `Lines`, even though their element type is
   now `OrderItemListItem`/`OrderItemDetail`. This was a deliberate scope boundary (REQ-7 named only the record
   TYPE for rename, not this property; it's a live HTTP JSON contract, unlike the internal domain properties).
   If task 6 (policy report) or any future task touches these same endpoints, don't "fix" this inconsistency as
   a drive-by — it needs its own reviewed contract-change decision, same as any other wire rename.
2. **`OrderSummaryLine`/`OrderSummaryLineResponse`/`ReconciliationLine` are UNTOUCHED** — different identifiers,
   not the renamed entity, still say "Line" everywhere (`GET /orders/{token}/summary`, `GET /reports/reconciliation`).
   Don't be surprised these still exist; they're out of REQ-7's scope, not a missed spot.
3. **EF migration footgun (production-relevant for whoever writes task 2/4's migration):** the FIRST
   `dotnet ef migrations add` I scaffolded got silently APPLIED to the local dev DB by something other than an
   explicit `database update` call, using the pre-hand-edit Drop+Create body — before I'd even reviewed it. This
   dropped the GRANTs on the 3 renamed tables without any error. I never fully root-caused the mechanism (no
   `dotnet watch`/docker migrate-service was running — `ps aux`/`docker ps -a` came up empty). **Always verify a
   migration's ACTUAL DB effect (`sys.tables.create_date`, `sys.database_permissions`) after generating it,
   before trusting `dotnet ef migrations add`'s own "may result in data loss" warning as the only signal** — I
   caught this only by comparing `create_date` timestamps, not by anything failing loudly. If you scaffold a
   migration and the dev DB already shows the new table/column before you've run `database update` yourself,
   STOP and inspect before continuing — don't assume it's a coincidence.
4. **Historical migrations were NOT edited** (`OrderLinesAndCheckoutSessionLines.cs`, `RevealAudits.cs`,
   `GrantInsuranceLineTables.cs` still say `OrderLine`/`OrderLines` — correct, they're frozen snapshots of a
   past schema state). The rename gate now excludes the whole `Migrations/` directory for this reason — don't
   add a new gate token that would false-positive on old migration files again.
5. **`dotnet-ef` needs `POL_DESIGN_SQL`** (not `POL_SQL_SERVER`/`POL_DB`/`POL_SA_PASSWORD` directly) — build it
   from `.env.integration`'s vars: `Server=$POL_SQL_SERVER;Database=$POL_DB;User Id=sa;Password=$POL_SA_PASSWORD;
   Encrypt=True;TrustServerCertificate=True;`, `export` it, then pass `--project src/BuildingBlocks/
   BuildingBlocks.Infrastructure/BuildingBlocks.Infrastructure.csproj --startup-project src/Hosts/Api/Api.csproj
   --context BuildingBlocks.Infrastructure.Persistence.PolDbContext` to every `dotnet ef` invocation.

## T2 — ItemPolicy domain + invariants

Status: DONE. `dotnet build pol-core.slnx -warnaserror` green (64 projects, 0/0); `dotnet test
tests/Orders.Tests` 68 passed / 0 failed (24 new `ItemPolicyTests`). tasks.md task 2 flipped + Evidence
appended. Working tree only, not committed.

**Files created (all new — task 4 wires these, no other file touched):**
- `src/Modules/Orders/Orders.Domain/Items/ItemPolicy.cs` — the entity.
- `src/Modules/Orders/Orders.Domain/Items/ItemPolicyInput.cs` — the `Apply` input record.
- `src/Modules/Orders/Orders.Domain/Items/InsuranceCategory.cs`
- `src/Modules/Orders/Orders.Domain/Items/ReferenceNumberType.cs`
- `src/Modules/Orders/Orders.Domain/Items/PremiumRemittanceStatus.cs`
- `tests/Orders.Tests/ItemPolicyTests.cs` — 24 unit tests, no DB.

**Exact shape task 4 must match:**
- `ItemPolicy : Entity<Guid>`. Fields: `OrderItemId`, `MerchantId` (set once at `Create`, never touched by
  `Apply`), `InsuranceCategory?`, `ReferenceNumberType?`, `ReferenceNumber`/`EndorsementNumber`/
  `RenewalReminderNumber`/`InsuredObjectReference` (all `string?`), `NetPremiumAmount`/`GrossPremiumAmount`
  (`decimal?`), `NetPremiumCurrency`/`GrossPremiumCurrency` (`string?`, always `"THB"` once set — `Apply`
  rejects anything else), `PremiumRemittanceStatus` (default `NotApplicable`), `DeductedAt` (`DateOnly?`),
  `CreatedAt`/`UpdatedAt` (`DateTime`). Computed `Money? NetPremium`/`GrossPremium` from the Amount+Currency
  pair via `Money.Of` — **EF config must map the 4 scalar columns, never the computed properties** (the
  complex-type approach `Session.Amount` uses is explicitly NOT used here — see design.md Tech Decision #7).
- `public static ItemPolicy Create(Guid id, Guid orderItemId, Guid merchantId, DateTime nowUtc)` —
  **deviation from design.md's `internal static`**: made `public` because this repo has zero
  `InternalsVisibleTo` anywhere (a deliberate rejected pattern per `MerchantUserOutboxDispatcher.cs:18` /
  `MerchantRuntimeDbContextFactory.cs:9`) and `Order.Create` — the identical
  Domain-factory-called-from-an-Application-handler shape — is already `public`. Call it exactly like
  `Order.Create` is called from `CreateOrderCommand.cs`.
- `public void Apply(ItemPolicyInput input, DateTime nowUtc)` — throws `ArgumentException` (never
  `InvalidOperationException`/`BadHttpRequestException`) for every REQ-3/REQ-2 violation; safe to call
  directly from the handler after a load-or-create, no other domain method needed.
- `ItemPolicyInput` is a `sealed record` in `Orders.Domain.Items` (not `.Application` — same
  insurance-pivot reverse-reference reason `OrderItemInput` already documents). Field order:
  `InsuranceCategory?, ReferenceNumberType?, string? ReferenceNumber, string? EndorsementNumber,
  string? RenewalReminderNumber, string? InsuredObjectReference, Money? NetPremium, Money? GrossPremium,
  PremiumRemittanceStatus, DateOnly? DeductedAt`. `NetPremium`/`GrossPremium` are already-constructed
  `Money` (so `Money.Of`'s own non-negative/scale/known-currency checks already ran before `Apply` sees
  them) — `Apply` only additionally enforces `Currency == "THB"` and `Net <= Gross`.

**Invariant subtleties task 4 (and any integration test) must not re-litigate:**
- **Blank ref strings collapse to unset, not to a separate error.** `Apply` trims every ref string first;
  an empty/whitespace-only value is treated identically to `null` for the (type,value) pairing checks
  (REQ-3.9 says "ว่าง" — empty — which already reads as "unset" in the requirement's own wording). So
  `ReferenceNumber = "   "` with `ReferenceNumberType` set still 400s via 3.9, it does NOT 500/persist a
  blank string. If task 4's endpoint/DTO layer does its own separate empty-string handling before calling
  `Apply`, make sure it does not fight this (e.g. don't convert `""` to `null` twice or diverge on trimming
  rules — there is no length/regex validation in the domain per design Tech Decision #6, only trim +
  collapse-blank-to-null; the `nvarchar(100)` ceiling is task 4's job to enforce at the EF config layer).
- **`DeductedAt` is unconditionally cleared whenever the incoming status is not `Deducted`** — this is one
  rule that satisfies both REQ-2.4 ("NotApplicable must not require DeductedAt") and REQ-2.6 ("revert
  clears DeductedAt") at once. It means if a caller sends `PremiumRemittanceStatus=NotApplicable` together
  with some `DeductedAt` value, that value is silently dropped rather than rejected — deliberate, documented
  interpretation (REQ-2.4 only says N/A doesn't *require* it, doesn't say a stray value must 400).
- **`DeductedAt`'s "not in the future" check uses the Thai local date, not `nowUtc.Date`.** Basis =
  `DateOnly.FromDateTime(nowUtc.AddHours(7))`. This matters near UTC midnight — e.g. `nowUtc` =
  `2026-07-23T20:00Z` has Thai date `2026-07-24`; a `DeductedAt` of `2026-07-24` must be ACCEPTED even
  though it's "tomorrow" by raw UTC date. `ItemPolicyTests.Apply_accepts_a_deducted_at_date_that_is_today_in_thai_local_time_but_tomorrow_in_utc`
  pins this exactly — if task 4/5's handler independently re-derives "today" for any UI/validation purpose,
  reuse this same `nowUtc.AddHours(7)` basis, don't recompute a different one.
- **No uniqueness check anywhere in the domain (REQ-1.10)** — two different `ItemPolicy` instances can hold
  the identical `ReferenceNumber`; don't add a uniqueness index keyed on `ReferenceNumber` in the EF config,
  only the `(OrderItemId)` unique index design.md specifies.
- **Currency check is per-premium, not just "must match each other"** — both `NetPremium.Currency` and
  `GrossPremium.Currency` are checked against the literal `"THB"` independently (REQ-3.8 wording: "ไม่ใช่
  THB" for either one). Since `Money.Of` already stores currency upper-invariant, the comparison is
  `StringComparison.Ordinal` against `"THB"`.
- **`Apply` is idempotent-safe to call for both create-then-fill and edit-existing** — task 4's
  load-or-create in the handler is exactly `existing ?? ItemPolicy.Create(...)` then `Apply(input, nowUtc)`
  on either branch, no separate "first write" vs "update" code path needed in the domain.

## T3 — iam policy permission keys

Status: DONE. `dotnet build pol-core.slnx -warnaserror` green (64 projects, 0/0). Catalog live on `pol-db`
(`:11433`): 10 groups / 24 keys / 4 roles / 34 grants, per-role `platform_admin`=15/`platform_auditor`=5/
`merchant_manager`=9/`merchant_staff`=5 — exact match to design's stated delta (actual pre-change baseline
already equalled design's stated 8/20/4/28/13-4-7-4, no reconciliation needed). `IamCatalogGrantsTests` 7/7,
`KeysTests` 15/15, whole-repo `Category!=Integration` sweep green (no regression). tasks.md task 3 flipped +
Evidence appended. Working tree only, not committed.

**Exact permission-key strings + `Keys.cs` C# const identifiers — task 4/5/6 reference these:**
- Platform (admin cross-merchant), group `merchants.policies` (`Keys.GroupMerchantsPolicies`):
  - `"merchants.policies.read"` = `Keys.MerchantsPoliciesRead`
  - `"merchants.policies.write"` = `Keys.MerchantsPoliciesWrite`
- Merchant (producer self-scope), group `policies` (`Keys.GroupPolicies`):
  - `"policies.read"` = `Keys.PoliciesRead`
  - `"policies.write"` = `Keys.PoliciesWrite`

Task 4's merchant write endpoint gates on `Keys.PoliciesWrite`; task 5's admin write endpoint gates on
`Keys.MerchantsPoliciesWrite`; task 6's two report-read endpoints gate on `Keys.PoliciesRead` (merchant) /
`Keys.MerchantsPoliciesRead` (admin) — per design.md's endpoint table and tasks.md task 4/5/6 lines.

**Migration:** `20260723150000_SeedPolicyPermissions` (+ matching `.Designer.cs`) in
`src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/`. Hand-written, mirrors
`20260712185912_SeedData.cs`'s structure (raw `migrationBuilder.Sql` INSERTs, no EF model change) — per
sharp edge #3 below, did NOT run `dotnet ef migrations add`. Instead: hand-wrote the `.cs` (Up/Down SQL) +
`cp`'d the previous migration's `.Designer.cs` (updating only the `[Migration(...)]` id and partial class
name) since this migration has zero model diff — confirmed after the fact with
`dotnet ef migrations has-pending-model-changes` -> "No changes have been made to the model since the last
migration." **`PolDbContextModelSnapshot.cs` was NOT touched** (correctly — a pure-data migration never
changes it). Applied to the live dev DB with an EXPLICIT target
(`dotnet ef database update 20260723150000_SeedPolicyPermissions`, not a bare `database update`), and
verified via direct `sqlcmd` count queries before/after (8/20/4/28 -> 10/24/4/34) rather than trusting the
`dotnet ef` command's own success output alone — same discipline HANDOFF sharp edge #3 asks for.

**Files touched (only these — no catalog refactor, no unrelated key touched):**
- `src/Modules/Iam/Iam.Domain/Permissions/Keys.cs` — added the 2 group consts, 4 key consts, registered all
  in `GroupScope`/`GroupKeys`/`All` (appended at the end, did not reorder or touch any existing entry); bumped
  the class doc comment's "20 keys / 8 groups" note to "24 keys / 10 groups" with a one-line pointer to this task.
- `src/BuildingBlocks/BuildingBlocks.Infrastructure/Persistence/Migrations/20260723150000_SeedPolicyPermissions.cs`
  + `.Designer.cs` (new).
- `tests/Iam.Tests/KeysTests.cs` — `ExpectedKeys`/`ExpectedGroups` +4/+2; renamed
  `All_has_exactly_20_keys_across_8_groups` -> `..._24_keys_across_10_groups` and
  `Platform_side_has_13_keys_and_merchant_side_has_7` -> `..._15_keys_and_..._9`, updated the asserted numbers;
  added 2 `[InlineData]` rows to `GroupScope_matches_the_v5_plan`.
- `tests/Integration.Tests/IamCatalogGrantsTests.cs` — updated the 4 total counts (10/24/4/34) and the 4
  per-role `GrantCount` assertions (15/5/9/5) in `Catalog_seed_matches_the_advertised_shape`.

**Naming choice worth knowing (deviation from the nearest precedent):** the existing `merchants.users.*`
key consts drop plurality inconsistently (`GroupMerchantUsers` keeps "Users" plural, but
`MerchantUserApprove`/`MerchantUserReject` singularize to "MerchantUser"). I did NOT mirror that
inconsistency — the new consts map 1:1, un-abbreviated, onto their literal dotted strings
(`GroupMerchantsPolicies`="merchants.policies", `MerchantsPoliciesRead`="merchants.policies.read", etc.) so
there's no guessing required downstream. If this bothers a future reviewer, it's a pure rename of unshipped
C# identifiers (the DB/wire string literals are unaffected either way) — cheap to change, not a contract.

**Doc drift NOT fixed here (out of this task's scope, flagging for whoever owns it):**
`.ai/shared/ARCHITECTURE.md`'s rf2 section still says "Vocabulary = 20 keys / 8 groups" — now stale (24/10).
Left untouched per the task's surgical scope (catalog additions + migration + test-count updates only); the
historical `rf2-iam-rbac` spec docs (`requirements.md`/`design.md`/`tasks.md`) were also left untouched
deliberately — they're a historical record of what rf2 originally shipped, same pattern as insurance-pivot's
superseded REQs being left in place rather than rewritten.

**For task 4/5/6:** nothing else about the catalog changed — `RequirePermission`/`PermissionAuthorization`,
the boot parity guard, and `MerchantRequestWriteAuthorizer` all consume `Keys.AllKeys`/`Keys.KeySide`
dynamically (no hardcoded totals found anywhere outside the two test files above), so they need zero changes
to recognize the 4 new keys — just reference the consts above in your endpoint policies.

**Correction for task 5/6:** T3's own Evidence claimed `IamRoleResolutionTests` regression-free, but that file
was NOT actually updated — 2 asserts (`Platform_admin_role_grants_every_action_key_regardless_of_tier`,
`Bootstrap_binds_platform_admin_by_code_idempotently`) still hardcoded platform_admin's key count as `13`.
Only surfaced when task 4 ran the FULL `Integration.Tests` project (T3 only ever ran the
`IamCatalogGrantsTests` filter). Fixed as part of task 4 — both now assert `15`. If you add MORE
`platform_admin`/`platform_auditor`/`merchant_manager`/`merchant_staff` grants in task 5/6, remember
`IamRoleResolutionTests.cs` is a SECOND place (beyond `IamCatalogGrantsTests.cs`/`KeysTests.cs`) that
hardcodes a per-role key count — grep for the literal role name before touching the catalog again.

## T4 — persistence + merchant write path

Status: DONE. `dotnet build pol-core.slnx -warnaserror` green (0/0, no new project). `dotnet test
pol-core.slnx --filter "Category!=Integration"` all green (`Orders.Tests` 68/68 unchanged, `Hosts.Tests`
309/309, `Architecture.Tests` 218/218). `source .env.integration && dotnet test
tests/Integration.Tests/Integration.Tests.csproj --filter "Category=Integration"` -> 44/44 (42 pre-existing +
2 new GRANT tests). tasks.md task 4 flipped + Evidence appended; `spec-trace.sh` OK, 51 criteria. Working
tree only, not committed.

**New types/ports/files (all under existing T1-T3 conventions, no new abstraction beyond what design named):**
- `Orders.Domain.Items.ItemPolicyAudit` (append-only entity) + `ActorKind` enum (`Admin`|`MerchantUser`) +
  `AuditOperation` enum (`Created`|`Updated`) — 3 new files in `src/Modules/Orders/Orders.Domain/Items/`.
- `Orders.Application.IItemPolicyRepository` — `ItemExistsAsync(orderItemId)`, `GetPolicyByItemAsync(orderItemId)`,
  `Add(ItemPolicy)`, `AddAudit(ItemPolicyAudit)`. **Deviation from my own task brief's literal wording**:
  bundles the audit-add into the SAME port (design.md's sequence diagram names one repository actor `R` for
  both "load-or-create" AND "add/update policy + add audit row" — I matched the diagram, not a separate
  `IRevealAuditWriter`-style port; task 5's admin escape-hatch writer (`IAdminItemPolicyWriter`) is a DIFFERENT
  interface per design, so this doesn't set a precedent for merging things there).
- `Orders.Application.UpsertItemPolicyCommand` + `UpsertItemPolicyResult` + `UpsertItemPolicyHandler` — one
  file, `src/Modules/Orders/Orders.Application/UpsertItemPolicyCommand.cs`, mirrors `CreateOrderCommand.cs`'s
  bundling style. `IMerchantScoped`. Command shape: `(Guid MerchantId, Guid OrderItemId, ItemPolicyInput Input,
  string ActorId)`. **`ActorId` resolution** (task 5's admin analogue needs the same shape): the endpoint
  passes `actor.UserId!.Value.ToString()` — exactly the same expression `GetOrderDetailCommand`'s endpoint
  already uses for its `ActorId`. Handler hardcodes `ActorKind.MerchantUser` when building the audit row (no
  admin path reaches this handler at all — task 5 needs its own `UpsertItemPolicyAdminCommand`/handler that
  passes `ActorKind.Admin`, it must NOT try to reuse this handler with a flag).
- `ChangeSummary` computation: the handler snapshots the 10 mutable `ItemPolicy` fields into a private
  `readonly record struct FieldSnapshot` BEFORE calling `Apply`, then diffs against the policy's post-`Apply`
  state field-by-field, joining changed `nameof(...)` names with `,`. `Apply` itself (T2, untouched) has no
  diff-reporting return, so this diff logic lives entirely in the handler — task 5's admin handler will need
  the identical diff block (copy, don't share — no shared base exists and design didn't ask for one).
  An audit row is written on EVERY successful call, even when the diff is empty (REQ-3.5 reads as "audit every
  write", not "audit every change").
- Dual EF config: `Orders.Infrastructure/Items/ItemPolicyConfiguration.cs` + `ItemPolicyAuditConfiguration.cs`
  (migration-owner, columns/indexes only) and `Persistence.MerchantRuntime/Orders/Items/ItemPolicyConfiguration.cs`
  + `ItemPolicyAuditConfiguration.cs` (runtime twin, + `TenantKeyDescriptor.Require`/`HasQueryFilter`/
  `AppendOnlyDescriptor.Mark`). **Both twins call `builder.Ignore(x => x.NetPremium)`/`.Ignore(x =>
  x.GrossPremium)`** — EF Core tries to map a get-only computed property by convention unless explicitly
  ignored (confirmed via the existing `Cart.Subtotal`/`Item.LineTotal` precedent, same shape). Forgetting this
  Ignore on a similar computed-Money property elsewhere will silently break model-build.
- `Persistence.MerchantRuntime.Orders.Items.ItemPolicyRepository` (impl) — registered
  `AddScoped<IItemPolicyRepository, ItemPolicyRepository>()` in `MerchantRuntimePersistenceRegistration.cs`.
  `ItemExistsAsync` queries `_db.Set<Item>()` (the `OrderItem` alias), NOT `ItemPolicy` — the item's own
  query filter IS the merchant boundary (REQ-3.3), so it doesn't matter whether a policy row exists yet.
- `MerchantRuntimeDbContext.cs`: added `DbSet<ItemPolicy> OrderItemPolicies` / `DbSet<ItemPolicyAudit>
  OrderItemPolicyAudits` (aliased `OrderItemPolicy`/`OrderItemPolicyAudit` in this file, same alias pattern as
  every other entity here) + 2 `ApplyConfiguration` calls.
- `Hosts/Api/Persistence/WriteAuthorizers.cs`: added `typeof(ItemPolicy)`/`typeof(ItemPolicyAudit)` (aliased
  `OrderItemPolicy`/`OrderItemPolicyAudit`) to `MerchantRequestWriteAuthorizer.OwnedTypes` — opens ONLY the
  merchant path. Task 5's admin path needs a NEW authorizer class entirely (`AdminItemPolicyWriteAuthorizer`
  per design — `OwnedTypes` allowlisting here does nothing for an unbound-merchant admin request).
- `Hosts/Api/Program.cs`: `using Orders.Domain.Items;` added (no collision — grepped, nothing else in this
  huge file bare-references `Item`). `UpsertItemPolicyRequest` wire DTO (field-for-field twin of
  `ItemPolicyInput`) + `PUT /api/v1/orders/{orderId:guid}/items/{itemId:guid}/policy` mapped right after
  `GetOrderDetail`, before the reconciliation-report endpoint. `orderId` route param is bound but UNUSED in
  the handler body (only `itemId` scopes the lookup, per this task's own port signature) — deliberate, matches
  the URL's RESTful nesting without adding a redundant query; does NOT trigger `-warnaserror` (unused lambda
  params aren't a compiler warning here, no `EnforceCodeStyleInBuild` set).
- Migration `20260723160000_OrderItemPolicies` (CreateTable x2 + 4 indexes, scaffolded via real `dotnet ef
  migrations add`, not hand-written — this task has genuine model changes unlike T3's data-only migration) +
  `20260723160500_GrantOrderItemPolicyTables` (hand-filled raw SQL, mirrors `GrantInsuranceLineTables.cs`:
  `OrderItemPolicies` gets SELECT+INSERT+UPDATE, `OrderItemPolicyAudits` gets SELECT+INSERT only).

**Sharp edge hit — EF migration timestamp ordering (extends HANDOFF sharp edge #3):** the scaffolded
`dotnet ef migrations add` timestamps its filename off wall-clock time. My first scaffold landed at
`20260723132322` — EARLIER than T3's already-applied `20260723150000_SeedPolicyPermissions` (T3 hand-picked a
timestamp ahead of when it actually ran). Running `dotnet ef database update` targeted at my migration made EF
compute "roll back to before 150000, then forward to my migration" — it silently REVERTED
`SeedPolicyPermissions` (ran its Down script, deleting T3's 4 new permission keys + role grants) as a side
effect, with NO error or warning. Caught immediately by re-running the same `sys.tables`/iam-count verification
queries this HANDOFF already asks for. **Fix procedure** (safe, no full `docker compose down -v` needed): (1)
rename the migration's `.cs`+`.Designer.cs` files AND the `[Migration("...")]` attribute string inside the
Designer.cs to a timestamp that sorts after the true latest-applied migration; (2) if the OLD id was already
applied to the DB, `UPDATE __EFMigrationsHistory SET MigrationId = '<new-id>' WHERE MigrationId = '<old-id>'`
(one row, exact string match) — this keeps EF's bookkeeping in sync with the ALREADY-CREATED tables/columns
without re-running `Up` (which would fail — table already exists); (3) `dotnet ef migrations list` to confirm
the tool now shows the correct pending set (reverted migration `(Pending)`, renamed one NOT pending); (4)
`dotnet ef database update <final-target>` re-applies the reverted migration forward and your (renamed, now
correctly-ordered) ones. **Lesson for task 5/6/7: before your FIRST `dotnet ef migrations add`, run `dotnet ef
migrations list` and note the actual latest-applied id — if your scaffold's auto-timestamp sorts before it,
rename before ever running `database update`, not after.**

**GRANT verified:** yes — `sys.database_permissions` confirms `pol_app` holds exactly SELECT+INSERT+UPDATE on
`shop.OrderItemPolicies` and SELECT+INSERT on `shop.OrderItemPolicyAudits`; `Integration.Tests.
OrderItemPolicyGrantsTests` proves both INSERT/UPDATE actually succeed (not just declared) and that UPDATE on
the audit table is denied at the DB level too (belt-and-suspenders under `AppendOnlyDescriptor`'s app-layer
guard).

**Test-location split (deviation from the task brief's literal "put it all in tests/Integration.Tests"):**
`Integration.Tests` has NO reference to the Api host or `Persistence.MerchantRuntime` (by its own csproj
comment: "the tests drive raw connections... never Persistence.MerchantRuntime") — it cannot exercise
`RequirePermission`/403 or the EF query-filter/404 boundary at all, only raw SQL. So: GRANT + fresh-connection
persistence proof live in `Integration.Tests` (real SQL Server, raw SQL, mirrors
`OrderSummaryReaderIntegrationTests`); write/read/upsert/cross-merchant-404/Cancelled-writable/400-validation
all live in `Hosts.Tests` (SQLite, REAL handler + REAL `MerchantRequestWriteAuthorizer`, mirrors
`InsuranceCheckoutEndToEndTests.cs` exactly) — same split T1's own Evidence uses implicitly (rename-migration
correctness -> Integration.Tests; behavior -> Hosts.Tests/Orders.Tests). "Missing `policies.write` -> 403" is
proven at the generic-mechanism level only (`PermissionAuthorizationTests`/`PermissionParityTests`/
`PermissionGateSitesTests`, all updated to include the new site+key+policy — `Keys.PoliciesWrite` needs zero
endpoint-specific 403 test of its own since the gate mechanism is generic and already covered).

**For task 5 (admin cross-merchant write):** re-read design.md's "Write guard registration" §Admin plane
section again before starting — it is NOT `MerchantRequestWriteAuthorizer.OwnedTypes` (that only ever opens
the MERCHANT path; an admin request has `HasActor=false` and is denied unconditionally regardless of
`OwnedTypes`). You need a brand-new `AdminItemPolicyWriteAuthorizer(IAdminScope)` + a SEPARATE
`MerchantRuntimeDbContext` instance built with it (mirror `AddProvisioning`'s pattern, `Program.cs:178`) — this
is genuinely new machinery, not an extension of anything task 4 built. Reuse from task 4: the `ItemPolicy`/
`ItemPolicyAudit` domain types, entity configs, DB tables/grants, and the `ChangeSummary` diff shape (copy the
block, no shared base). Do NOT reuse `UpsertItemPolicyHandler`/`IItemPolicyRepository` directly — the admin
path needs `IgnoreQueryFilters` + `IAdminScope.Accessible` checks task 4's merchant-scoped repository
deliberately does not have.

## T5 — admin cross-merchant write

Status: DONE. `dotnet build pol-core.slnx -warnaserror` green (0/0, no new project). `dotnet test pol-core.slnx
--filter "Category!=Integration"` all green (`Orders.Tests` 68/68, `Iam.Tests` 62/62, `Architecture.Tests`
218/218 incl. `BypassPrimitiveTests`, `Hosts.Tests` 319/319, +10 vs T4's 309). `source .env.integration &&
dotnet test tests/Integration.Tests --filter Category=Integration` -> 44/44 unchanged (no new migration, no
new DB object). `dotnet ef migrations has-pending-model-changes` -> "No changes" (confirms zero migration
added). `check-rename-identifiers.sh`/`spec-trace.sh` both OK. tasks.md task 5 flipped + Evidence appended.
Working tree only, not committed.

**New files:**
- `src/Modules/Orders/Orders.Application/IAdminItemPolicyWriter.cs` — the escape-hatch port
  (`LoadAsync`/`Add`/`AddAudit`/`SaveChangesAsync`) + `AdminItemPolicyLoad` record (`ItemExists`,
  `MerchantId` = the item's REAL owner, `Policy`).
- `src/Modules/Orders/Orders.Application/UpsertItemPolicyAdminCommand.cs` — `UpsertItemPolicyAdminCommand` +
  `UpsertItemPolicyAdminResult` + `UpsertItemPolicyAdminHandler`. Deliberately does NOT implement
  `IMerchantScoped`. **Does NOT reference `IAdminScope`** (see deviation below) — carries `bool
  IsUnrestrictedAdmin, IReadOnlySet<Guid> AccessibleMerchantIds` as plain data instead; the handler computes
  `IsUnrestrictedAdmin || AccessibleMerchantIds.Contains(load.MerchantId)` itself, AFTER loading the item
  (you cannot know the merchant to check before the cross-merchant load runs). Both "item missing" and "item
  outside scope" throw the SAME `NotFoundException` (no existence leak). `ChangeSummary`/`FieldSnapshot` diff
  block is copied verbatim from T4's `UpsertItemPolicyHandler` (no shared base, as T4's own note anticipated).
- `src/Persistence/Persistence.MerchantRuntime/Orders/Items/AdminItemPolicyWriter.cs` — `internal sealed class
  AdminItemPolicyWriter : IAdminItemPolicyWriter, IAsyncDisposable`. `LoadAsync` does TWO
  `IgnoreQueryFilters()` reads (the `Item` for its real `MerchantId`, then `ItemPolicy` by `OrderItemId`) and
  emits exactly ONE `DenialEvent(AdminCrossMerchantAction)` per call (pattern `ConnectionRepository.cs`).
  Internally owns a private `MerchantRuntimeUnitOfWork` (constructed directly — same assembly, no new DI
  shape) wrapping its OWN `MerchantRuntimeDbContext`, so `SaveChangesAsync` gets T4's `DbUpdateException`
  translation for free. Owns disposal of that context (`IAsyncDisposable`) since it is never itself registered
  in DI as `MerchantRuntimeDbContext` — see registration note below.
- `tests/Hosts.Tests/UpsertItemPolicyAdminEndToEndTests.cs` — 5 facts, mirrors
  `UpsertItemPolicyEndToEndTests.cs`'s style exactly. Items are seeded through the ORDINARY merchant floor
  (`MerchantRequestWriteAuthorizer`) so the admin path proves a real cross-merchant boundary, not a fixture
  shortcut.

**Files touched:**
- `src/Hosts/Api/Persistence/WriteAuthorizers.cs` — added `internal sealed class
  AdminItemPolicyWriteAuthorizer(IAdminScope) : IWriteAuthorizer`. Allowed set = `(OrderItemPolicy,
  Insert|Update)` + `(OrderItemPolicyAudit, Insert)` — NOT unconditional like `ProvisioningSuperWriteAuthorizer`;
  ALSO requires `_scope.Accessible.Allows(targetMerchant)`. `AccessibleMerchants.Allows` already folds in
  `IsUnrestricted`, so there is no separate Super/Scoped branch to write.
- `src/Persistence/Persistence.MerchantRuntime/MerchantRuntimePersistenceRegistration.cs` — added
  `AddAdminItemPolicyWriter(services, connectionString, Func<IServiceProvider, IWriteAuthorizer>
  authorizerFactory)` + a private `UnboundActorContext` (mirrors `Persistence.Provisioning`'s own — query
  filters never matter for this context since every read is an explicit `IgnoreQueryFilters()`). Registers
  `IAdminItemPolicyWriter` ONLY — the admin-authorizer `MerchantRuntimeDbContext` instance is constructed
  INLINE inside the factory lambda and is NEVER itself registered as a DI service, so it can never collide
  with or override the ambient `MerchantRuntimeDbContext` registration `AddMerchantRuntimePersistence` already
  owns (registering it a second time under the same unkeyed type would have silently replaced the ambient one
  for every OTHER repository that constructor-injects `MerchantRuntimeDbContext` — avoided entirely by this
  shape). Lives in `Persistence.MerchantRuntime` itself, NOT a new `Persistence.AdminItemPolicy` assembly like
  `Persistence.Provisioning` — Provisioning needed its own assembly because it coordinates TWO contexts
  sharing one transaction; this task needs only one context, and `Persistence.MerchantRuntime` can already
  construct its own internal `MerchantRuntimeDbContext` with zero new `InternalsVisibleTo` grants.
- `src/Hosts/Api/Program.cs` — `builder.Services.AddAdminItemPolicyWriter(appConnString, sp => new
  AdminItemPolicyWriteAuthorizer(sp.GetRequiredService<IAdminScope>()))` registered right after
  `AddMerchantRuntimePersistence`. Endpoint `PUT /api/v1/admins/orders/{orderId:guid}/items/{itemId:guid}/policy`
  mapped on the `admin` group (after the approve/reject block, before "Admin identity foundation management"),
  `RequireAuthorization("admin").RequirePermission(Keys.MerchantsPoliciesWrite)`. Reuses T4's
  `UpsertItemPolicyRequest` wire DTO verbatim (field-for-field identical to the merchant body) — no second DTO.
  `orderId` route param unused in the handler body, same deliberate pattern T4 already established.
- `tests/Architecture.Tests/BypassPrimitiveTests.cs` — allowlisted
  `src/Persistence/Persistence.MerchantRuntime/Orders/Items/AdminItemPolicyWriter.cs` (the ONLY new bypass
  call site this task adds — the registration file itself calls no bypass primitive).
- `tests/Hosts.Tests/WriteAuthorizersTests.cs` — added a `--- AdminItemPolicyWriteAuthorizer ---` pure-unit
  section (4 facts, no DB) + a new `FakeAdminScope(AccessibleMerchants)` (the existing `FakeScope` throws on
  `.Accessible` — fine for `ControlPlaneAdminWriteAuthorizer`, which never reads it, but
  `AdminItemPolicyWriteAuthorizer` reads it unconditionally so it needed a fake that returns a real value).
- `tests/Hosts.Tests/PermissionGateSitesTests.cs` — added the new admin `Site`, bumped 23->24
  (`Exactly_24_gate_sites_are_pinned`, admin sub-count 15->16).
- `tests/Hosts.Tests/PermissionAuthorizationTests.cs` — added `("merchants.policies.write", "admin")` to
  `PermissionParityTests.RealGateSites`.

**Deviation worth knowing for task 6 (admin read path, `IAdminItemPolicyReader`):** design's sequence diagram
has the admin handler talk to `IAdminScope` directly ("H->>S: Accessible.Allows(merchantId)?"), but
`Orders.Application` has ZERO reference to `Admins.Application` (grepped every module's `*.csproj` in the
repo — no module Application project references another module's Application project, confirmed precedent:
`ApproveCommand`'s handler takes a raw `Guid adminId`, never `IAdminScope`). So `UpsertItemPolicyAdminCommand`
carries the caller's accessible-merchant decision as PLAIN DATA (`bool IsUnrestrictedAdmin, IReadOnlySet<Guid>
AccessibleMerchantIds`) — the Program.cs endpoint reads `scope.Accessible.IsUnrestricted`/`scope.Accessible.
Merchants` and passes them into the command; the handler does the one-line `Allows`-equivalent check itself
AFTER the cross-merchant load (you cannot know which merchant to check before loading — that's the whole
point of the escape-hatch). **Task 6's `ListPolicyReportAdminQuery` should mirror this same shape** — do NOT
add an `Orders.Application -> Admins.Application` project reference to reach `IAdminScope`/`AccessibleMerchants`
directly; pass the same two primitives (or a `?merchantId=` filter value, per design's admin report param)
from the host endpoint instead.

**Escape-hatch pattern to reuse for task 6's `IAdminItemPolicyReader`:** same shape as
`IAdminItemPolicyWriter`/`AdminItemPolicyWriter` here — a port in `Orders.Application`, an `internal sealed`
impl in `Persistence.MerchantRuntime/Orders/Items/`, `IgnoreQueryFilters()` + one `DenialEvent
(AdminCrossMerchantAction)` per cross-floor read, allowlisted in `BypassPrimitiveTests.AllowedPorts`. The
READ path does NOT need a second `MerchantRuntimeDbContext`/authorizer factory the way the WRITE path did
(reads never touch `SaveChanges`, so there is no write-floor authorizer to swap) — it can likely just take the
AMBIENT `MerchantRuntimeDbContext` (already DI-registered, `MerchantRequestWriteAuthorizer` is irrelevant for
a pure read) and call `IgnoreQueryFilters()` on it directly, closer to `ConnectionRepository.ListByTenantAsync`
than to anything task 5 built. Don't over-mirror task 5's DI-factory machinery where a plain repository
suffices.

## T6 — policy report read (2 plane)

Status: DONE. `dotnet build pol-core.slnx -warnaserror` green (64 projects, 0/0, no new project). `dotnet test
pol-core.slnx --filter "Category!=Integration"` all green (`Orders.Tests` 68/68, `Iam.Tests` 62/62,
`Architecture.Tests` 218/218 incl. `BypassPrimitiveTests`, `Hosts.Tests` 329/329, +10 vs T5's 319). `source
.env.integration && dotnet test tests/Integration.Tests --filter Category=Integration` -> 44/44 unchanged (no
new migration, no new DB object). `dotnet ef migrations has-pending-model-changes` -> "No changes" (confirms
zero migration added). `check-rename-identifiers.sh`/`spec-trace.sh` both OK, 51 criteria. tasks.md task 6
flipped + Evidence appended. Working tree only, not committed.

**Read model + endpoints:**
- `Orders.Application.PolicyReportItem` — the shared wire record (camelCase JSON): `orderId`, `itemId`,
  `insuredName` (first+last joined), `insuredIdNumberMasked`, `insuranceCategory?`, `referenceNumberType?`,
  `referenceNumber?`/`endorsementNumber?`/`renewalReminderNumber?`/`insuredObjectReference?`,
  `netPremium`/`grossPremium` (`Money?` — pinned nullable per design m5, verified with a direct
  `JsonSerializer` test, never a garbage `{"amount":"0.0000","currency":null}`), `premiumRemittanceStatus`
  (never null — coalesced) + `deductedAt`, `paymentStatus` (Thai label string, never blank), `merchantId`
  (`Guid?`, populated admin-only). `orderId`/`itemId` are additions beyond design's literal field list — the
  row's own identity, needed to link back to the write endpoints; not a scope violation, just filling an
  obvious gap design's paragraph didn't spell out.
- Merchant: `Orders.Application.ListPolicyReportQuery`(`IMerchantScoped`)/`IPolicyReportRepository`/
  `ListPolicyReportHandler` -> `GET /api/v1/reports/policies`, `RequireAuthorization("merchant-user")` +
  `RequirePermission(Keys.PoliciesRead)`, `.WithMetadata(new SfsQueryParamsMarker())`. Mapped in `Program.cs`
  right after `/reports/reconciliation`, before the admin group.
- Admin: `Orders.Application.ListPolicyReportAdminQuery` (NOT `IMerchantScoped`, carries `bool
  IsUnrestrictedAdmin`/`IReadOnlySet<Guid> AccessibleMerchantIds`/`Guid? MerchantId` as plain data — same
  T5-established reason, no `Orders.Application -> Admins.Application` dependency) /`IAdminItemPolicyReader`/
  `ListPolicyReportAdminHandler` -> `GET /api/v1/admins/reports/policies`, `RequireAuthorization("admin")` +
  `RequirePermission(Keys.MerchantsPoliciesRead)`. Mapped right after the admin PUT policy endpoint, before
  "Admin identity foundation management". `?merchantId=` is parsed with a bare `Guid.TryParse` at the endpoint
  (NOT part of the SFS filter whitelist, mirrors `ProductSfs.cs`'s own `merchantId` exclusion).

**Persistence (`Persistence.MerchantRuntime/Orders/Items/`):**
- `PolicyReportSfs.cs` — shared join + SFS pipeline + row->wire mapping, used by both
  `PolicyReportRepository` (merchant) and `AdminItemPolicyReader` (admin).
  - `Joined(OrderItem Item, OrderAggregate Order, ItemPolicy? Policy)` — one row per `OrderItem`, `Policy` null
    when no `ItemPolicy` row exists yet.
  - `BuildQuery(db, ignoreFilters, confineToMerchants)` — `INNER JOIN OrderItems/Orders` + a correlated
    `FirstOrDefault` subquery for the optional 1:1 `ItemPolicy` (NOT `GroupJoin`+`DefaultIfEmpty`/`LeftJoin` —
    see the EF translation trap below). `confineToMerchants` (`IReadOnlySet<Guid>?`, null = every merchant) is
    applied to `items`/`orders`/`policies` BEFORE the join — this is the part that stays real SQL and bounds
    what gets fetched.
  - `ApplyFilters`/`ApplySort` now operate on `IEnumerable<Joined>` (LINQ-to-Objects), not `IQueryable` — see
    the EF trap below for why. Whitelist unchanged from what was planned: `insuranceCategory`/
    `referenceNumberType`/`premiumRemittanceStatus`/`paymentStatus` (Equals only) + `createdAt` (range ops,
    mirrors `ProductSfs`'s own `createdAt`). NULLS-last only needed for `insuranceCategory`/
    `referenceNumberType` (the only two nullable sortable fields — `premiumRemittanceStatus`/`paymentStatus`
    are both coalesced/always-set by the time sorting runs).
  - `ToItem(joined, includeMerchantId)` — the mask-helper copy (private static, NOT shared with
    `GetOrders.cs`'s own copy) + the `Order.Status` -> Thai label switch, both run here (client-side), not in
    the Application-layer handler.
- `PolicyReportRepository : IPolicyReportRepository` — merchant plane, `confineToMerchants = { query.MerchantId
  }`, `ignoreFilters: false` (ambient ActorContext-based query filter ALSO still applies — the explicit
  confinement is defense-in-depth on top, mirrors `ProductRepository.ListAsync`).
- `AdminItemPolicyReader : IAdminItemPolicyReader` — admin plane, `ignoreFilters: true`. Computes
  `confineToMerchants` from `(IsUnrestrictedAdmin, AccessibleMerchantIds, MerchantId)`: unrestricted+no-filter
  -> `null` (every merchant); unrestricted+filter -> `{merchantId}`; scoped+no-filter -> the whole accessible
  set; scoped+filter -> `{merchantId}` if in the accessible set else the EMPTY set (empty page, no leak, never
  a 404/error for a list endpoint). Emits exactly ONE `DenialEvent(AdminCrossMerchantAction)` per call (not per
  row), same as `ConnectionRepository.ListByTenantAsync`.
- DI: both `IPolicyReportRepository`/`IAdminItemPolicyReader` registered as plain `AddScoped<...>` inside the
  EXISTING `AddMerchantRuntimePersistence` (no new factory/authorizer machinery needed — reads never call
  `SaveChanges`, confirmed correct per T5's own forward-note above).
- `BypassPrimitiveTests.AllowedPorts` — allowlisted `.../Orders/Items/PolicyReportSfs.cs` (where
  `IgnoreQueryFilters()` textually lives, inside the shared `BuildQuery`), **NOT**
  `AdminItemPolicyReader.cs` itself — the regex scan is file-content-based, so point it at the file that
  actually contains the bypass primitive call.

**EF Core translation trap hit (new lesson, not in prior HANDOFF sections) — read before writing ANY further
multi-entity JOIN + post-Select filter/sort in this codebase:** this repo's EF Core version (SQLite provider,
the one `Hosts.Tests` runs against) refuses to translate a `Where`/`OrderBy` chained AFTER a `Select` into a
custom projection type (record) built from a JOIN — tried 3 shapes, all failed identically ("could not be
translated... LeftJoin(...).Where(x => new Joined(...).Item.MerchantId == ...)", i.e. EF inlines the
constructor call into the predicate instead of simplifying `new Joined(a,b,c).Item` back down to `a`):
1. A record with ternary-coalesce fields baked into the constructor call (the FIRST attempt, mirroring what
   `PolicyReportRow` originally looked like).
2. A trivial 3-field wrapper record `Joined(Item, Order, ItemPolicy?)` with NO computed fields at all — still
   failed identically.
3. Swapping the optional-policy join from `GroupJoin`+`SelectMany(DefaultIfEmpty())` (which EF10 compiles to
   its newer native `LeftJoin` operator) to a correlated `FirstOrDefault` subquery — still failed identically.
This rules out "ternary complexity" and "GroupJoin/LeftJoin specifically" as the cause; it looks like a
genuine limitation with `Where`/`OrderBy`-after-`Select`-into-a-record for THIS EF Core+SQLite combination,
regardless of shape. **Fix that worked:** never chain `Where`/`OrderBy` onto the already-`Select`-projected
`IQueryable<Joined>` at all. Do the SQL-side confinement (the part that bounds row count, i.e. the
merchant/accessible-set predicate) on the RAW entity sets BEFORE the join/select, materialize with
`ToListAsync()` immediately after, then run the fine-grained SFS filter/sort/paging in-memory
(`IEnumerable`/LINQ-to-Objects) over that already-bounded list. If a future task hits the SAME "could not be
translated" error on a join+custom-projection, this is very likely the same wall — don't re-litigate it from
scratch, go straight to the entity-set-confine-then-materialize-then-filter-in-memory shape.

**For task 7 (seed/demo data) — what must be populated for the report to render like the target UI:**
- Every seeded `OrderItemPolicies` row needs its owning `OrderItem`'s `Order` to exist with a real `Status`
  (`AwaitingPayment`/`Paid`/`Cancelled`) — `paymentStatus` is ALWAYS derived from that, never a seedable column
  of its own.
- For a row that should show FULL data in the report (mirroring the target UI's populated rows): seed
  `InsuranceCategory`, `ReferenceNumberType`, `ReferenceNumber` (all three travel together, REQ-3.9/3.10), plus
  optionally `EndorsementNumber`/`RenewalReminderNumber` (only valid alongside a `ReferenceNumber`, REQ-3.11),
  `InsuredObjectReference` (ทะเบียนรถ, generic — not Motor-only), and BOTH `NetPremium`+`GrossPremium` together
  (REQ-3.12, both-or-neither) with `Currency = "THB"`. Set `PremiumRemittanceStatus.Deducted` +
  `DeductedAt` for the "เบี้ยตัดชำระแล้ว" demo rows; leave `PremiumRemittanceStatus.NotApplicable` (default) +
  no `DeductedAt` for the "ยังไม่ตัด" ones.
- For a row that should render EXACTLY like the target UI's blank/unfilled rows (REQ-1.7/4.7): create the
  `OrderItem`/`Order` but write NO `OrderItemPolicies` row at all (don't insert a row with all-null fields —
  the report's LEFT JOIN + coalesce already produces the exact same "blank ref/N-A remittance, populated
  paymentStatus" shape for an item with zero policy rows; inserting an empty-but-present row is unnecessary
  and untested by this task).
- REQ-5.1's motor case needs 2 items (แยก line ภาคสมัครใจ/ภาคบังคับ, per the requirements' own edge case) —
  `InsuranceCategory.Voluntary` on one item, `InsuranceCategory.Compulsory` on the sibling item, both can share
  the same `InsuredObjectReference` (ทะเบียนรถ) if they're meant to represent the same vehicle/person.

## T7 — seed / demo data

Status: DONE. Only file touched: `docker/bootstrap/seed-demo.sql`. `dotnet build pol-core.slnx -warnaserror`
64/0/0; `dotnet test pol-core.slnx --filter "Category!=Integration"` all green, identical counts to T6
(`Orders.Tests` 68, `Iam.Tests` 62, `Architecture.Tests` 218, `Hosts.Tests` 329) — expected, zero `src/`/`tests/`
changes; `source .env.integration && dotnet test tests/Integration.Tests --filter Category=Integration` ->
44/44 unchanged; `dotnet ef migrations has-pending-model-changes` -> "No changes" (no migration added);
`check-rename-identifiers.sh` OK; `spec-trace.sh policy-reference-record` -> **OK, 51/51 criteria traced —
the whole spec is now covered.** tasks.md task 7 flipped + Evidence appended. Working tree only, not committed.

**Blocker discovered before any SQL could be written — read this before touching `seed-demo.sql` again for
ANY future spec:** the `demo-seed-data` spec (closed 2026-07-13, all 6 sub-tasks done) predates
insurance-pivot/`OrderItem` entirely. It seeds `shop.Orders` (40 rows, prefix `ed000000-%`) but has **zero**
`shop.OrderItems` rows — that table/concept didn't exist yet when `demo-seed-data` was written. Since
`ItemPolicy` is 1:1 keyed on `OrderItemId` and the report query is item-driven (`OrderItems JOIN Orders LEFT
JOIN OrderItemPolicies`, design.md §Report query details), there was **no real `OrderItemId` anywhere in demo
data** to attach a policy row to — seeding `OrderItemPolicies` alone, as the task brief assumed, would have
produced a permanently-empty report no matter what values were inside those rows. Fixed by adding a
`shop.OrderItems` INSERT block (4 rows) to the SAME script, immediately before the `OrderItemPolicies` block,
referencing 3 **existing** `shop.Orders` rows (no new Orders added) — still entirely within "the existing
demo-seed mechanism," just one FK layer lower than expected. **If a future feature needs to seed anything
keyed off `OrderItemId`, check first whether `shop.OrderItems` demo rows still only number 4** (this task's
own) — don't assume the funnel-depth seeding that `demo-seed-data` T1-T6 built out (merchants ->
users -> products -> carts -> checkouts -> orders -> payment sessions) actually reaches item-level, it stops
at `Order`.

**Also discovered — this specific dev DB (`pol-db`, `:11433`) had never actually run the FULL demo seed at
all before this task**, or it was wiped by an earlier `docker compose down -v` (T1's own HANDOFF entry
describes doing exactly that) and never re-seeded afterward. A pre-seed query found `shop.Orders` had 0 rows
matching the demo prefix `ed000000-%` — only 14 unrelated rows with random GUIDs (leftover fixture data from
`Integration.Tests.OrderItemPolicyGrantsTests`-style runs, e.g. `InsuredFirstName='Somchai'` — coincidentally
the same fake name this task also picked, pure coincidence, not a collision since prefixes differ). Ran
`./scripts/seed-demo.sh` fresh (not just my delta) to populate the ENTIRE demo dataset — this is expected/
intended usage of that script, not scope creep; it now needs to be (re-)run again by anyone spinning up a
fresh `pol-db` container before the report has anything in it. The 14 non-demo leftover rows were left
untouched (same "don't touch non-demo rows" precedent `demo-seed-data` T4's own Evidence already set for
unrelated `shop.Orders` noise on this same DB) — flagging again for whoever owns dev-DB hygiene.

**Seed shape added** (all within `seed-demo.sql`'s existing zone (ค) DELETE list + zone (ง) INSERT zone +
zone (จ) `@counts` self-check — no new script, no migration):
- `shop.OrderItems` (prefix `ef000000-%`, 4 rows) — `ef…0001`/`ef…0002` share ONE existing order (`ed…0016`,
  vprivilege/Paid) and the SAME insured person (`สมชาย ใจดี`) + will get the SAME `InsuredObjectReference`
  plate on their policies below — this is the REQ-5.1/Edge-Cases "Voluntary + Compulsory, same vehicle" case.
  `ef…0003` sits on `ed…0008` (vcommerce/Paid, non-motor/health product). `ef…0004` sits on `ed…0005`
  (vcommerce/AwaitingPayment) and deliberately gets NO policy row at all (next bullet).
- `shop.OrderItemPolicies` (prefix `f1000000-%`, 3 rows, ONE per item except `ef…0004`) —
  `f1…0001` (for `ef…0001`): `Voluntary`+`PolicyNumber`+`Endorsement`+plate `กข-1234 กรุงเทพมหานคร`+Net
  15000/Gross 15900 THB+`Deducted`+`DeductedAt='2026-07-15'` (past). `f1…0002` (for `ef…0002`): `Compulsory`+
  `NotificationNumber`+SAME plate+Net 600/Gross 645.21 THB+`NotApplicable`+no `DeductedAt`. `f1…0003` (for
  `ef…0003`): `Voluntary`+`PolicyNumber`+`RenewalReminder`+no plate (non-motor, proves REQ-1.8 genericness)+
  Net==Gross==18500 THB (equal, valid per REQ-3.7)+`Deducted`+`DeductedAt='2026-06-30'`. `ef…0004` gets no row
  at all — the report's own `LEFT JOIN` + coalesce already produces the blank-external/N-A/non-blank-
  paymentStatus shape for it (REQ-1.7/4.7), confirmed by direct query, not by inserting an empty row.
- **No `OrderItemPolicyAudits` rows seeded** — design.md's traceability table only lists "seed
  `OrderItemPolicies` demo rows" against REQ-5; every other table in this script is a raw INSERT bypassing its
  own aggregate/audit path too (Orders bypass `Order.Create`, Products bypass nothing since they have no
  audit table) — consistent, not an oversight.

**How to re-run:** `source .env.integration && ./scripts/seed-demo.sh` (same as every other `demo-seed-data`
increment — nothing new to know). Idempotent, verified via 2 consecutive runs with identical row counts.

**Confirmed the report renders like the target UI** — not via a live HTTP call (no merchant-user session
available to authenticate one; `RequireAuthorization("merchant-user")`/`RequirePermission(policies.read)`
gate the endpoint, and standing one up would mean faking an OIDC session cookie purely to smoke-test seed
data whose query SHAPE T6's `ListPolicyReportEndToEndTests` already covers exhaustively against its own
fixtures). Instead ran a direct SQL simulation of `PolicyReportSfs`'s exact join + `COALESCE(PremiumRemittance
Status,0)` + `CASE Order.Status WHEN 0/1/2 THEN 'รอชำระเงิน'/'ชำระสำเร็จ'/'ยกเลิก'` projection against the
seeded rows — all 4 rows come back exactly as designed (3 fully populated incl. the same-vehicle two-category
pair, 1 fully blank-external with a non-blank paymentStatus). See tasks.md task 7's Evidence block for the
literal query + output.

## Build complete

All 7 tasks of `policy-reference-record` are DONE. Final state: `dotnet build pol-core.slnx -warnaserror` ->
64 projects, 0 errors, 0 warnings. `dotnet test pol-core.slnx --filter "Category!=Integration"` -> all green
(`Orders.Tests` 68, `Iam.Tests` 62, `Architecture.Tests` 218, `Hosts.Tests` 329, plus every other project
unchanged from its own pre-feature baseline). `source .env.integration && dotnet test tests/Integration.Tests
--filter Category=Integration` -> 44/44. `bash scripts/check-rename-identifiers.sh` -> OK. `bash
scripts/spec-trace.sh policy-reference-record` -> OK, 51/51 criteria traced. `dotnet ef migrations
has-pending-model-changes` -> "No changes" (model + snapshot fully in sync). Feature spans: the OrderLine->
OrderItem rename (T1), the `ItemPolicy` domain (T2), 4 new iam permission keys (T3), merchant write path +
persistence (T4), admin cross-merchant write escape-hatch (T5), 2-plane policy report read (T6), and dev/demo
seed data proving it all renders (T7). Working tree left uncommitted throughout — lead handles the PR.
