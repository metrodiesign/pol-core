# Implementation Tasks: Search / Filter / Sort (SFS) + Pagination

> Status: unknown
> Each task is a cohesive, independently verifiable slice — implement a whole task in one pass
> (it may touch many files). Decompose into sub-steps at execution time; do NOT pre-split here.
> Full reference implementation for every snippet: `docs/reference/search-filter-sort.md` (cited per task).

- [x] 1. Contract types in `BuildingBlocks.Application` + JSON round-trip tests — add `FilterOperator`
     (14 members, `[JsonStringEnumMemberName]` + enum-level `[JsonConverter(JsonStringEnumConverter<>)]`),
     `SortDirection` (`"ASC"`/`"DESC"`), records `FilterOption` (`JsonElement? Value`, `JsonElement[]? Values`),
     `SortOption`, `SearchOption`, `abstract record PagedQuery` (Page=1/Limit=25/Filters=[]/Sort=[]/Search=null),
     `record PagedResult<T>` (+`TotalPages`). No ASP.NET reference. Ref: doc 2.3.
     REQ-9, REQ-1.3, REQ-1.4, REQ-2.7, REQ-11.

- [x] 2. `SfsQueryParser` in `Hosts/Api` + parser unit tests — static parser returning the named tuple
     `(Page, Limit, Filters, Sort, Search)`: `page` clamp `>=1` (`Math.Max`) AND clamp to an offset ceiling so
     `(long)(page-1)*limit` never overflows `int` (REQ-2.6), `limit` clamp `[1..100]` (`Math.Clamp`)
     [หมายเหตุ 2026-07-30: เพดานถูก supersede เป็น `[1..25]` โดย spec `products-sp-53-alignment` REQ-4]; reject
     with `ArgumentException` (->400) when `filters` > 50 / `sort` > 10 / any `values[]` > 200 (REQ-6.6);
     deserialize `filters`/`sort`/`search` with `JsonSerializerDefaults.Web`; malformed JSON ->
     `throw new ArgumentException(...)` (NOT `BadHttpRequestException`). Ref: doc 2.5, 3.
     Satisfies: REQ-1.1, REQ-1.2, REQ-1.5, REQ-1.6, REQ-2.1, REQ-2.2, REQ-2.3, REQ-2.6, REQ-6.6, REQ-8.1.

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
     REQ-2.5, REQ-3, REQ-4, REQ-5, REQ-6, REQ-8.5, REQ-8.6. Depends on: 1.

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
     REQ-1.1, REQ-2.4, REQ-8, REQ-12.1, REQ-12.2, REQ-13. Depends on: 2, 3.

- [x] 5. Tenant-scoped exemplar `GET /products` + typed filter DTO + RLS non-widening
     **[หมายเหตุ 2026-07-30 — บันทึกประวัติ, ถูก supersede โดย spec `products-sp-53-alignment` (§5.2 field parity
     + REQ-7 SFS teardown)]**: ณ ตอนทำ task นี้ `Product` ยังมี `Name`/`Price`/`IsActive`/`CreatedAt` และ
     `GET /products` ยังเป็น SFS exemplar. ปัจจุบัน `Product` ไม่มี `IsActive`/`CreatedAt` แล้ว (gate ย้ายไป
     `PaymentStatus == UNPAID`), `ProductSfs.cs` ถูกลบ, `ListProductsQuery` เลิกสืบทอด `PagedQuery`, และ
     `ProductListItem` เป็น mirror ของ SP §5.2 (32 field + `Id`) ไม่ใช่ shape ที่เขียนไว้ข้างล่างนี้ —
     ข้อความเดิมคงไว้ตามที่เคยส่งมอบ อย่าใช้เป็นสเปกปัจจุบัน. exemplar ที่ยังตรงกับโค้ดจริง = admins/roles
     — introduce a **new**
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
     REQ-11, REQ-12.3. Depends on: 4, 5.
> tasks 3-4 as the template.
