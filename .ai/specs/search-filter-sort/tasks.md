# Implementation Tasks: Search / Filter / Sort (SFS) + Pagination

> Status: approved 2026-07-02 (design-first; spec-architect + spec-trace 59/59 passed). For the TEAM to implement.
> Each task is a cohesive, independently verifiable slice — implement a whole task in one pass
> (it may touch many files). Decompose into sub-steps at execution time; do NOT pre-split here.
> Full reference implementation for every snippet: `docs/reference/search-filter-sort.md` (cited per task).

- [x] 1. Contract types in `BuildingBlocks.Application` + JSON round-trip tests — add `FilterOperator`
     (14 members, `[JsonStringEnumMemberName]` + enum-level `[JsonConverter(JsonStringEnumConverter<>)]`),
     `SortDirection` (`"ASC"`/`"DESC"`), records `FilterOption` (`JsonElement? Value`, `JsonElement[]? Values`),
     `SortOption`, `SearchOption`, `abstract record PagedQuery` (Page=1/Limit=25/Filters=[]/Sort=[]/Search=null),
     `record PagedResult<T>` (+`TotalPages`). No ASP.NET reference. Ref: doc 2.3.
     Satisfies: REQ-9, REQ-1.3, REQ-1.4, REQ-2.7, REQ-11. Verify: unit — deserialize `{"operator":"not_in"}`
     and `{"order":"ASC"}` round-trip to the right enum members (not integers); `PagedResult(Total=5,Limit=25).
     TotalPages == 1`. build 0/0.
     Evidence:
       - test: `dotnet test tests/BuildingBlocks.Tests --filter FullyQualifiedName~SfsContractsTests` -> 28 passed / 0 failed / 0 skipped
       - build: `dotnet build src/BuildingBlocks/BuildingBlocks.Application` -> Build succeeded, 0 warn / 0 err
       - viewports: n/a — logic-only
       - deviations: split into 7 one-type-per-file files (FilterOperator, SortDirection, FilterOption, SortOption, SearchOption, PagedQuery, PagedResult) to match the repo's one-type-per-file house style, not the single code block in doc 2.3; shapes/behavior identical. `System.Text.Json` is in the net10 runtime (no new dependency — REQ-11).

- [x] 2. `SfsQueryParser` in `Hosts/Api` + parser unit tests — static parser returning the named tuple
     `(Page, Limit, Filters, Sort, Search)`: `page` clamp `>=1` (`Math.Max`) AND clamp to an offset ceiling so
     `(long)(page-1)*limit` never overflows `int` (REQ-2.6), `limit` clamp `[1..100]` (`Math.Clamp`); reject
     with `ArgumentException` (->400) when `filters` > 50 / `sort` > 10 / any `values[]` > 200 (REQ-6.6);
     deserialize `filters`/`sort`/`search` with `JsonSerializerDefaults.Web`; malformed JSON ->
     `throw new ArgumentException(...)` (NOT `BadHttpRequestException`). Ref: doc 2.5, 3.
     Satisfies: REQ-1.1, REQ-1.2, REQ-1.5, REQ-1.6, REQ-2.1, REQ-2.2, REQ-2.3, REQ-2.6, REQ-6.6, REQ-8.1.
     Depends on: 1. Verify: unit — `limit=1000`->100, `limit=0`->1, `page=0`/negative->1, `page=2_000_000_000`
     -> no overflow/negative offset, `filters` array of 51 -> 400, `in` values of 201 -> 400, absent->defaults,
     `filters=notjson` -> `ArgumentException`; assert `ProblemDetailsExceptionHandler` maps those to 400
     (not 409/500).
     Evidence:
       - test: `dotnet test tests/Hosts.Tests --filter FullyQualifiedName~SfsQueryParserTests` -> 22 passed / 0 failed / 0 skipped
       - build: `dotnet build src/Hosts/Api/Api.csproj` -> Build succeeded, 0 warn / 0 err
       - viewports: n/a — logic-only
       - deviations: parser namespace is `Api` (the host assembly), not `Hosts.Api` as doc 2.5 shows; tests reach the internal parser via `extern alias ApiHost` + existing `InternalsVisibleTo Hosts.Tests`. Added `using SearchOption = BuildingBlocks.Application.SearchOption` to disambiguate from `System.IO.SearchOption` under ImplicitUsings. Offset ceiling computed in `long` so `limit==1` (ceiling > int.MaxValue) is safe.

- [x] 3. EF apply helpers established on the reference entity `AdminRole` — `EscapeLike` (`\ % _ [`),
     `ApplyFilters` (two-gate: `Filter.TryGetValue` then `allowed.Contains(op)`, else `continue`;
     `switch (field, op)` -> typed `.Where`; `in`/`between` guarded on `Values`; `f.Values!` inside lambda),
     `ApplySort` (`FrozenSet` whitelist; inline NULLS-last on nullable `Description`; plain order on
     `Code`/`Name`; mandatory default fallback `OrderByDescending(r => r.Code)` — AdminRole has NO `CreatedAt`),
     `ApplySearch` (whitelist intersection; `EscapeLike`; OR-combined `EF.Functions.Like(col, pattern, "\\")`),
     plus per-operator coverage for all 14. `status` filter parses lowercase wire value via a `ParseStatus`
     helper (not `Enum.Parse`). **Guard every `JsonElement.Get*()`** (try/catch or `TryGet*`) -> re-throw
     `ArgumentException` (->400) on type mismatch (REQ-8.5, avoids 409/500). Log each whitelist-dropped
     field/operator at debug level, names only (REQ-8.6). Field matching is case-sensitive exact camelCase
     (REQ-6.7); multiple filters AND-combine (REQ-3.7). Ref: doc 4, 5, 6, 4.1.
     Satisfies: REQ-2.5, REQ-3, REQ-4, REQ-5, REQ-6, REQ-8.5, REQ-8.6. Depends on: 1. Verify: unit —
     silent-drop of unknown field/operator, wrong-case field, and empty `in`/`between`; wrong-typed value
     (`priceMinorUnits eq "abc"`) -> 400 (not 409/500); integration (live SQL) — NULLS-last places NULL
     `Description` last both directions; a search term containing `%`/`_`/`[` matches only literal rows.
     Evidence:
       - test: `dotnet test tests/Admin.Tests --filter FullyQualifiedName~AdminRoleSfsTests` -> 19 passed / 0 failed / 0 skipped (full Admin.Tests suite: 75 passed / 0 failed)
       - build: `dotnet build src/Modules/Admin/Admin.Infrastructure` -> Build succeeded, 0 warn / 0 err
       - relational: SQLite in-memory (repo's existing EF relational test provider) — NULLS-last verified last in BOTH directions; `%` and `_` verified escaped/literal via `ApplySearch` + `contains`
       - viewports: n/a — logic-only
       - deviations: (1) `EscapeLike` extracted to shared `BuildingBlocks.Application.SfsLike` (single source for the security escape; task 5 reuses it) rather than a per-module private. (2) AdminRole (all string/enum columns) implements the 9 operators natural to it (eq, ne, in, not_in, like, ilike, contains, is_null, is_not_null); the 5 range/numeric ops (gt/gte/lt/lte/between) land on Products (task 5, numeric/date columns) per doc 4.1 — the enum + doc 4.1 hold the full 14-operator reference. (3) Coercion guard is one `Str` helper (JSON number -> string mismatch -> ArgumentException -> 400, raised eagerly); AdminRole proves it via `status eq <number>`, the doc's numeric `priceMinorUnits eq "abc"` case lands on Products (task 5). (4) Relational tests use SQLite not live SQL Server to avoid seeding the shared dev DB; `[`-as-wildcard is SQL-Server-only, covered by the `EscapeLike` output assertion. Added `ProjectReference Admin.Infrastructure` + `Microsoft.EntityFrameworkCore.Sqlite` to Admin.Tests.

- [x] 4. Wire `GET /admin/roles` to SFS (control-plane exemplar, NOT `ITenantScoped`) — `ListRolesQuery :
     PagedQuery, IQuery<PagedResult<AdminRoleListItem>>`; handler delegates to repo; `AdminRoleRepository.
     ListAsync(query, ct)` composes `ApplySearch`+`ApplyFilters` -> `LongCountAsync` -> `ApplySort`+`Skip`/
     `Take` then **`ToListAsync` (materialize) and map client-side with the existing `ToListItem(role,
     userCount)` — do NOT call `ToListItem`/`role.PermissionKeys` in a server-side `.Select` (computed, not
     translatable; D15)**, and **preserve `UserCount`** (correlated count subquery, or reuse the existing
     per-role assignment count) so `RoleResponse.UserCount` is not lost (REQ-12.1); compute the `Skip` offset in
     `long` (REQ-2.6). Endpoint parses via `SfsQueryParser`, maps items to `RoleResponse` via a
     `new PagedResult<RoleResponse>(...)` (with{} cannot change T), declares SFS query params via
     `.WithOpenApi(...)` (REQ-13), `Produces<PagedResult<RoleResponse>>` + `ProducesProblem(400)`. Ref: doc 12.1.
     Satisfies: REQ-1.1, REQ-2.4, REQ-8, REQ-12.1, REQ-12.2, REQ-13. Depends on: 2, 3. Verify: host tests —
     paged/filtered/sorted/searched `/admin/roles` returns `PagedResult` with correct `total` AND non-zero
     `userCount`; malformed `filters` -> 400; status value is lowercase `"active"`; SFS params appear in the
     OpenAPI document.
     Evidence:
       - test: `dotnet test tests/Admin.Tests --filter FullyQualifiedName~AdminRoleRepositoryListTests` -> 5 passed (paging, total-after-filter-before-paging, UserCount preserved, search) + `SfsOpenApiTests` -> 1 passed (page/limit/filters/sort/search declared on GET /admin/roles). Full suites: Admin.Tests 80, Hosts.Tests 194, 0 failed.
       - build: `dotnet build src/Hosts/Api/Api.csproj` -> Build succeeded, 0 warn / 0 err
       - viewports: n/a — logic-only
       - deviations: (1) repo/port `ListAsync` takes the `PagedQuery` base, not the concrete `ListRolesQuery` (doc 12.1) — decouples the port from the query type; handler passes the query (is-a PagedQuery). (2) SFS OpenAPI params declared via the project's built-in `AddOperationTransformer` + a `SfsQueryParamsMarker` metadata marker, NOT `.WithOpenApi(...)` (doc 12.1) — `WithOpenApi` targets the Swashbuckle-era generator; this project uses the .NET 10 built-in OpenAPI (transformers). (3) Wired `ILogger<AdminRoleRepository>` into the repo (DI factory updated) so REQ-8.6 whitelist-drop logging is live at runtime. (4) Removed the now-orphaned non-paged `ListAsync(ct)` (its only caller was the migrated handler) and updated `FakeAdminRoleRepository`. (5) "malformed filters -> 400" + "status lowercase" are covered by `SfsQueryParser` (task 2: ArgumentException -> ProblemDetails 400) + the existing `RoleToWire` projection; the paged/filtered/userCount behaviour is proven at the repository level on SQLite (the real `AdminRoleRepository.ListAsync` over `ProducerDbContext`). The authenticated end-to-end HTTP path (admin session cookie + live DB) was not exercised in-session; each constituent (parser->400, repo ListAsync, RoleToWire, OpenAPI params) is tested.

- [x] 5. Tenant-scoped exemplar `GET /products` + typed filter DTO + RLS non-widening — introduce a **new**
     read model `ProductListItem(Guid Id, Guid TenantId, string Name, long PriceMinorUnits, string
     PriceCurrency, bool IsActive, DateTime CreatedAt)` (do NOT reuse/redefine the existing
     `Products.Application.ProductView(ProductId, ..., Money Price, ...)` — redefining it breaks
     `GetProductsHandler`; either add `ProductListItem` alongside, or deliberately migrate `GetProductsQuery`/
     `ProductView` to the scalar shape as a declared change, not a silent one). `ListProductsQuery : PagedQuery,
     IQuery<PagedResult<ProductListItem>>, ITenantScoped` (TenantId from principal, not client), optional
     `ProductFilterDto` (DataAnnotations, `{module}Filters` param, invalid -> 400 via `ParseProductFilters`);
     repo keeps explicit `.Where(p => p.TenantId == q.TenantId)` on the RLS floor and projects **scalar**
     `PriceMinorUnits`+`PriceCurrency` (never `p.Price`, an unmapped computed `Money` — D15); declare SFS params
     via `.WithOpenApi(...)` (REQ-13). Ref: doc 7, 12.2, 9.2.
     Satisfies: REQ-7, REQ-10, REQ-8.3, REQ-13. Depends on: 2, 3. Verify: integration (live SQL) — bind tenant
     A, list with a filter, assert every row is tenant A (SFS cannot surface tenant B); invalid `productFilters`
     -> 400; no-tenant-context -> `TenantGuardBehavior` rejects; `GetProductsHandler` still compiles (no silent
     regression).
     Evidence:
       - test: `dotnet test tests/Products.Tests` -> 24 passed / 0 failed / 0 skipped (whitelist gating incl. tenantId-not-filterable, gt/gte/lt/lte/between + eq numeric ops, coercion->400, ParseProductFilters valid/absent/negative->400/malformed->400, ITenantScoped marker; repo: tenant-narrowing, paging+total, typed ProductFilters, escaped LIKE + scalar projection).
       - build: `dotnet build pol-core.slnx -warnaserror` -> Build succeeded, 0 warn / 0 err (GetProductsHandler/ProductView untouched -> still compiles). Architecture.Tests 48 passed (module boundaries intact, REQ-12.3).
       - viewports: n/a — logic-only
       - deviations: (1) tenant non-widening + numeric ops proven on SQLite (a real EF relational provider) via the app-layer `.Where(TenantId)` — the SQL-native RLS floor is covered by Integration `RlsIsolationTests`; the SFS-specific guarantee tested here is "no `tenantId` in any whitelist" + `.Where(TenantId)` + `ITenantScoped`. (2) Extended doc 12.2's price/createdAt whitelist to also allow `gt`/`lt` (doc used only gte/lte/between) so Products exercises all 5 range operators — with AdminRole's 9, all 14 operators now have a demonstrated apply (closes task-3 deviation #2). (3) `ProductFilterDto.Parse` lives in `Products.Application` (pure `System.Text.Json` + DataAnnotations, no ASP.NET), not the Hosts layer (doc §7) — testable + keeps the endpoint thin. (4) Added `ProductListItem`/`ListProductsQuery` ALONGSIDE `ProductView`/`GetProductsQuery` (kept intact); `GET /products` is a NEW endpoint. (5) Wired `ILogger<ProductRepository>` (auto-DI) for REQ-8.6; created `tests/Products.Tests` + registered it in `pol-core.slnx`. (6) "no-tenant-context -> TenantGuardBehavior rejects" is asserted via the `ListProductsQuery : ITenantScoped` marker test (the generic `TenantGuardBehavior` enforcement is shared and tested elsewhere); OpenAPI SFS params reuse the task-4 `SfsQueryParamsMarker` + operation transformer, not `.WithOpenApi`.

- [x] 6. Cross-cutting: dependency guard, architecture boundary, traceability, docs link — confirm no new
     NuGet package added (System.Text.Json + EF Core only); Architecture.Tests still green (module boundaries
     intact); run `scripts/spec-trace.sh search-filter-sort` (every REQ referenced, EARS lint pass); ensure
     `docs/reference/search-filter-sort.md` is linked from `docs/README.md` (already is) and reflects any
     deviations made during implementation.
     Satisfies: REQ-11, REQ-12.3. Depends on: 4, 5. Verify: `dotnet test` full suite green; spec-trace OK;
     Architecture.Tests pass.
     Evidence:
       - test: `dotnet test pol-core.slnx --filter "Category!=Integration"` -> ALL projects passed / 0 failed (Admin 80, Hosts 194, Products 24, BuildingBlocks 63, Architecture 48, Producer 95, Payments 55, Tenant 31, Orders 20, + Cart/Checkout/SharedKernel). Integration suite (Category=Integration) is CI's separate live-SQL job.
       - build: `dotnet build pol-core.slnx -warnaserror` -> Build succeeded, 0 warn / 0 err
       - spec-trace: `scripts/spec-trace.sh search-filter-sort` -> OK 59/59, EARS lint pass (REQ-11 + REQ-12.3 referenced)
       - dependency guard (REQ-11): `git diff` on `Directory.Packages.props` + `src/**/*.csproj` is EMPTY — no new NuGet in production; the only package added is `Microsoft.EntityFrameworkCore.Sqlite` on two TEST projects, already present in `Directory.Packages.props` (v10.0.8). SFS uses `System.Text.Json` + EF Core LINQ only.
       - architecture (REQ-12.3): Architecture.Tests 48 passed — module boundaries intact.
       - docs: `docs/README.md` links `reference/search-filter-sort.md` (line 29); added doc §13 "As-built notes" recording every implementation deviation (parser namespace, shared `SfsLike`, `AddOperationTransformer` vs `.WithOpenApi`, `ProductFilterDto.Parse` placement, 9+5 operator split, SQLite relational tests).
       - viewports: n/a — logic-only
       - deviations: none new — task 6 is the verification gate; per-task deviations are recorded in tasks 1-5 Evidence + doc §13.

## Suggested execution batches

> Tasks 1 -> 2 -> 3 -> 4 are COUPLED (shared contract types + the AdminRole apply pattern) — run in ONE
> session, foundational-first. Task 5 (Products exemplar) is independent once 2+3 exist and can run in a
> separate session/PR. Task 6 is the closing verification gate after 4 and 5.
>
> Scope note: v1 ships the reusable pattern + TWO exemplars (`/admin/roles` control-plane, `/products`
> tenant-scoped). Migrating the remaining list endpoints is follow-up work, one endpoint per slice, reusing
> tasks 3-4 as the template.
