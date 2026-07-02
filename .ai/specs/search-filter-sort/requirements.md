# Requirements: Search / Filter / Sort (SFS) + Pagination

> Status: approved 2026-07-02 (design-first — architecture in `docs/reference/search-filter-sort.md`; spec-architect adversarial review applied, spec-trace 59/59; approved for team implementation)

## Overview

pol-core list endpoints วันนี้คืน full result set, sort แบบ hardcode, ไม่มี pagination/filter/search/
validation. spec นี้กำหนด **convention SFS ที่ใช้ซ้ำได้ทั้งโปรเจกต์**: query contract แบบ JSON-DSL
(เหมือน sibling project `nong-kaewta-api` เพื่อ contract ข้ามโปรเจกต์เหมือนกัน) parse ที่ Hosts layer เป็น
shared contract types แล้ว apply ใน repository บน EF Core `IQueryable<T>` -> SQL Server ด้วย per-field
whitelist, silent-drop, escaped LIKE, NULLS-last emulation และ error เป็น ProblemDetails. SFS ประกอบ
**ทับบน** tenant RLS floor เดิมและ **ไม่ขยาย tenant scope**.

design + C# ที่ verify แล้วอยู่ครบใน `docs/reference/search-filter-sort.md` (companion ของ `design.md`).
spec นี้คือ requirements/design/tasks spine ให้ทีม implement. **การ adopt เป็น opt-in ต่อ endpoint** —
endpoint เดิมไม่เปลี่ยนจนกว่าจะถูก migrate. **ไม่เพิ่ม dependency** (System.Text.Json + EF Core เท่านั้น).

## REQ-1: Query contract (JSON-DSL) + parsing

**User Story:** As an API client, I want one consistent query-string contract for search/filter/sort across
every list endpoint, so that I write the same query shape everywhere.

**Acceptance Criteria (EARS):**
- 1.1 THE SYSTEM SHALL accept SFS parameters on a list endpoint as query string: `page` (int), `limit` (int),
  `filters` (JSON array), `sort` (JSON array), `search` (JSON object).
- 1.2 THE SYSTEM SHALL parse `filters`/`sort`/`search` as URL-encoded JSON at the Hosts layer using
  `System.Text.Json` into `FilterOption` / `SortOption` / `SearchOption` values.
- 1.3 THE SYSTEM SHALL interpret operator tokens as the lowercase/snake strings `eq, ne, gt, gte, lt, lte,
  like, ilike, in, not_in, is_null, is_not_null, between, contains`.
- 1.4 THE SYSTEM SHALL interpret sort `order` as the literal string `"ASC"` or `"DESC"`.
- 1.5 IF a `filters`/`sort`/`search` value is present but not valid JSON, THEN THE SYSTEM SHALL reject the
  request with 400 ProblemDetails and not execute the query.
- 1.6 THE SYSTEM SHALL treat absent SFS parameters as empty (no filter, no search, default sort, page 1).

## REQ-2: Pagination

**User Story:** As an API client, I want bounded, paged results with a total, so that large lists are safe and
navigable.

**Acceptance Criteria (EARS):**
- 2.1 THE SYSTEM SHALL default `page` to 1 and `limit` to 25 when absent.
- 2.2 THE SYSTEM SHALL clamp `limit` into `[1..100]`.
- 2.3 THE SYSTEM SHALL clamp `page` to `>= 1` (a non-positive page becomes 1) so the computed offset is never
  negative.
- 2.4 THE SYSTEM SHALL return results as `PagedResult<T>` carrying `Items`, `Page`, `Limit`, and `Total`.
- 2.5 THE SYSTEM SHALL compute `Total` after applying filter and search but before `Skip`/`Take`.
- 2.6 THE SYSTEM SHALL bound `page` so the computed `Skip` offset is always non-negative and within `int`
  range (no overflow), rejecting or clamping any page beyond the offset ceiling, so a large `page` cannot
  trigger a 500.
- 2.7 THE SYSTEM SHALL expose `TotalPages` on `PagedResult<T>`, computed as `ceil(Total / Limit)`.

## REQ-3: Filtering (whitelist + operators)

**User Story:** As an API client, I want to filter a list by allowed fields and operators, so that I retrieve
only the rows I need.

**Acceptance Criteria (EARS):**
- 3.1 THE SYSTEM SHALL define, per list endpoint, a filter whitelist mapping each filterable field to the set
  of operators allowed for it.
- 3.2 WHEN a filter names a whitelisted field with an allowed operator, THE SYSTEM SHALL apply it as a
  parameterized EF Core predicate over the mapped entity property.
- 3.3 IF a filter names a field absent from the whitelist, THEN THE SYSTEM SHALL silently drop that filter and
  continue.
- 3.4 IF a filter uses an operator not allowed for its field, THEN THE SYSTEM SHALL silently drop that filter.
- 3.5 THE SYSTEM SHALL support all 14 operators with their value shape: scalar `value` for comparison/text;
  `values[]` for `in`/`not_in`; `values[2]` for `between`; no value for `is_null`/`is_not_null`.
- 3.6 IF an `in`/`not_in` filter carries no values, or a `between` carries fewer than 2, THEN THE SYSTEM SHALL
  silently drop that filter.
- 3.7 WHEN multiple filters are supplied (including several targeting the same field), THE SYSTEM SHALL
  combine them with AND.

## REQ-4: Sorting (whitelist + NULLS-last + default)

**User Story:** As an API client, I want deterministic multi-field sort on allowed fields, so that paged
results are stable and ordered as requested.

**Acceptance Criteria (EARS):**
- 4.1 THE SYSTEM SHALL define, per list endpoint, a sort whitelist of allowed field names.
- 4.2 WHEN sort options name whitelisted fields, THE SYSTEM SHALL order by the mapped properties in the order
  given (first `OrderBy`, subsequent `ThenBy`), honoring ASC/DESC.
- 4.3 IF a sort names a field absent from the whitelist, THEN THE SYSTEM SHALL silently drop that sort key.
- 4.4 WHILE sorting on a nullable column, THE SYSTEM SHALL place NULLs last in both directions.
- 4.5 THE SYSTEM SHALL apply a mandatory deterministic default sort when no whitelisted sort key survives, so
  pagination is stable.
- 4.6 THE SYSTEM SHALL map sort fields to entity properties via compile-checked code and SHALL NOT interpolate
  a client-supplied string into `ORDER BY`.

## REQ-5: Search (whitelist + escaped LIKE)

**User Story:** As an API client, I want free-text search across allowed text fields, so that I can find rows
by substring safely.

**Acceptance Criteria (EARS):**
- 5.1 THE SYSTEM SHALL define, per list endpoint, a search whitelist of non-sensitive text fields.
- 5.2 WHEN a search query is present, THE SYSTEM SHALL match it as a case-insensitive substring across the
  requested whitelisted fields, OR-combined into a single grouped predicate.
- 5.3 THE SYSTEM SHALL restrict searched fields to the intersection of requested fields and the whitelist,
  defaulting to all whitelisted fields when none are requested.
- 5.4 THE SYSTEM SHALL escape LIKE wildcards (`\ % _ [`) in the search term and issue an explicit `ESCAPE`
  clause, so wildcard characters in input are treated as literals.
- 5.5 IF the search query is empty or whitespace, THEN THE SYSTEM SHALL apply no search predicate.

## REQ-6: Security (deny-by-default, parameterized, escaped)

**User Story:** As a security owner, I want SFS to be injection-proof by construction, so that untrusted query
input can never reach SQL as code or unescaped wildcards.

**Acceptance Criteria (EARS):**
- 6.1 THE SYSTEM SHALL enforce deny-by-default: no field name reaches SQL unless it is present in a whitelist.
- 6.2 THE SYSTEM SHALL store whitelists as immutable collections (`FrozenDictionary` / `FrozenSet`).
- 6.3 THE SYSTEM SHALL parameterize every filter/search value via EF Core and SHALL NOT concatenate or
  interpolate values into SQL text.
- 6.4 THE SYSTEM SHALL escape user input in every LIKE (`like`/`ilike`/`contains` filter and search) with an
  `ESCAPE` clause.
- 6.5 THE SYSTEM SHALL NOT use string-evaluated dynamic LINQ or raw SQL in the SFS apply path.
- 6.6 IF the count of `filters`, of `sort` keys, or of any `values[]` array exceeds the configured caps
  (defaults: 50 filters, 10 sort keys, 200 values), THEN THE SYSTEM SHALL reject the request with 400
  ProblemDetails, bounding query cost and staying under the SQL Server parameter limit.
- 6.7 THE SYSTEM SHALL match whitelist field names case-sensitively (exact camelCase); a field whose case
  does not match is treated as absent (silent-drop).

## REQ-7: RLS non-widening (tenant isolation preserved)

**User Story:** As the data-protection owner, I want SFS to only narrow results within the tenant floor, so
that no filter/sort/search can widen tenant scope.

**Acceptance Criteria (EARS):**
- 7.1 THE SYSTEM SHALL apply SFS predicates only as additional narrowing `.Where`/ordering on an `IQueryable`
  already bound by the tenant RLS floor.
- 7.2 WHERE a list query targets tenant-owned data, THE SYSTEM SHALL mark the query `ITenantScoped` so
  `TenantGuardBehavior` rejects it when no tenant context is present.
- 7.3 THE SYSTEM SHALL exclude cross-tenant fields (`TenantId`, cross-aggregate FKs) from every whitelist.
- 7.4 THE SYSTEM SHALL NOT mark control-plane (non-tenant) list queries `ITenantScoped`.

## REQ-8: Error contract

**User Story:** As an API client, I want predictable status codes, so that a malformed query is a clear 400
and never an opaque 500.

**Acceptance Criteria (EARS):**
- 8.1 WHEN SFS JSON fails to parse, THE SYSTEM SHALL surface HTTP 400 by throwing `ArgumentException` (mapped
  to 400 by `ProblemDetailsExceptionHandler`) and SHALL NOT throw `BadHttpRequestException` (which maps to
  500 via `IOException`).
- 8.2 THE SYSTEM SHALL NOT leak `exception.Message` in the ProblemDetails body (fixed per-bucket detail).
- 8.3 WHERE a typed filter DTO is used and its validation fails, THE SYSTEM SHALL respond 400 ProblemDetails.
- 8.4 THE SYSTEM SHALL NOT return an error for whitelist-dropped fields/operators (silent-drop is not an
  error).
- 8.5 IF a filter value cannot be coerced to the CLR type of its mapped field, THEN THE SYSTEM SHALL respond
  400 ProblemDetails (via `ArgumentException`) and SHALL NOT surface a 409 (`InvalidOperationException`) or
  500 (`FormatException`) from the raw `JsonElement.Get*()` accessor.
- 8.6 WHEN a filter/sort/search key is dropped by the whitelist, THE SYSTEM SHALL log the dropped
  field/operator name at debug level and SHALL NOT log the supplied value.

## REQ-9: Shared contract types (BuildingBlocks.Application)

**User Story:** As a developer adding SFS to a module, I want the contract types provided once, so that every
endpoint uses the same shapes.

**Acceptance Criteria (EARS):**
- 9.1 THE SYSTEM SHALL define `FilterOperator`, `SortDirection`, `FilterOption`, `SortOption`, `SearchOption`,
  `PagedQuery`, and `PagedResult<T>` in `BuildingBlocks.Application` (no ASP.NET dependency).
- 9.2 THE SYSTEM SHALL serialize `FilterOperator`/`SortDirection` as their string tokens via a converter
  annotated directly on the enum (the host has no global string-enum converter).
- 9.3 THE SYSTEM SHALL represent filter `value`/`values` as `JsonElement` and convert to the target CLR type
  at apply time, after the whitelist check.

## REQ-10: Module-specific typed filter DTO (optional strict surface)

**User Story:** As a module owner, I want an optional strictly-validated filter object, so that back-office
lists can return field-level 400s and express related-field rules.

**Acceptance Criteria (EARS):**
- 10.1 WHERE a module needs validated or related filters, THE SYSTEM SHALL accept a `{module}Filters` JSON
  object parsed into a typed DTO validated with DataAnnotations.
- 10.2 WHEN a typed filter DTO is present and valid, THE SYSTEM SHALL apply its fields as compile-time-safe
  predicates without runtime whitelisting.
- 10.3 THE SYSTEM SHALL allow a typed filter DTO and the generic `filters[]` to coexist on one query.

## REQ-11: No new dependency

**User Story:** As a maintainer, I want SFS built on the existing platform, so that we add no library to
review, audit, or pin.

**Acceptance Criteria (EARS):**
- 11.1 THE SYSTEM SHALL implement SFS using only `System.Text.Json` and EF Core LINQ (no new NuGet package
  such as a dynamic-LINQ library).

## REQ-12: Opt-in adoption / no regression

**User Story:** As the existing API, I want SFS to be purely additive, so that current endpoints keep working
until deliberately migrated.

**Acceptance Criteria (EARS):**
- 12.1 THE SYSTEM SHALL keep endpoints not yet migrated to SFS behaviorally unchanged.
- 12.2 WHEN an endpoint adopts SFS, THE SYSTEM SHALL return `PagedResult<T>` and map items to the wire DTO by
  constructing a new `PagedResult` (a record `with`-expression cannot change the item type).
- 12.3 THE SYSTEM SHALL keep module dependency boundaries intact (Architecture.Tests pass).

## REQ-13: API discoverability (OpenAPI / Scalar)

**User Story:** As an API consumer, I want the SFS query parameters documented in OpenAPI, so that they show
in Scalar and client generators despite being read from the raw query string.

**Acceptance Criteria (EARS):**
- 13.1 THE SYSTEM SHALL describe `page`, `limit`, `filters`, `sort`, and `search` as query parameters in the
  OpenAPI document of every SFS-enabled endpoint (they are read from `HttpContext.Request.Query`, not bound as
  typed minimal-API parameters, so they must be declared explicitly).

## Edge Cases & Open Questions

- **`ilike` under SQL Server:** SQL Server default collation is case-insensitive, so `like` and `ilike` behave
  identically; both translate to `EF.Functions.Like`. `ilike` is kept only for cross-project contract parity.
- **`Like` escapes client wildcards too:** unlike raw SQL LIKE, the convention escapes user input for `like`
  as well (all `%`/`_` become literal); clients needing substring use `contains`. Deliberate safety choice
  (documented in the doc), a departure from the TS source which left filter-side LIKE unescaped (latent bug).
- **Deep-offset overflow:** addressed by REQ-2.6 (bound `page`, widen offset arithmetic). Keyset/seek
  pagination remains the future optimization for very deep paging, out of scope for v1.
- **Coercion posture:** REQ-8.5 makes a wrong-typed filter value a 400 (guard every `JsonElement.Get*()`),
  distinct from an unknown field/operator (silent-drop, REQ-3.3/3.4) and malformed JSON (400, REQ-1.5).
- **Query-cost caps:** REQ-6.6 bounds `filters`/`sort`/`values[]` counts; excess is a 400, not silent
  truncation.
- **`LongCountAsync` cost:** the total is a second query per request; acceptable for console lists. Endpoints
  that do not need a true total may later opt out of the count.
- **NULLS-last only on nullable columns:** a generic `OrderByNullsLast<T,TKey>` helper must guard the
  null-flag to nullable columns; `Expression.Equal(valueTypeNonNullable, null)` throws at build. v1 uses the
  inline per-field pattern.
- **Unmapped computed properties:** properties like `Product.Price` (`Money.Of(...)`, a validating factory)
  cannot be projected server-side; project the backing scalars (`PriceMinorUnits`+`PriceCurrency`) and
  reconstitute client-side.
- **Frontend adoption** (clients switching to paged responses) is out of scope; server contract is additive.
