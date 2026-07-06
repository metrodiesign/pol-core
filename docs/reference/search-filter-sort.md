# Search / Filter / Sort — คู่มือ convention (pol-core)

คู่มือมาตรฐานฉบับละเอียดสำหรับทำ search / filter / sort / pagination (เรียกรวมว่า SFS) บน list endpoint
ของ pol-core. พอร์ต *แนวคิด* มาจาก guide ต้นฉบับของโปรเจกต์ `nong-kaewta-api`
(NestJS / TypeORM / PostgreSQL) แต่ปรับ server-side ทั้งหมดให้ตรง stack จริงของเรา:
C# 14 / .NET 10 / EF Core 10 / SQL Server 2025 / martinothamar Mediator (source-generated CQRS) + RLS floor.

> สถานะ: **pol-core ยังไม่ได้ implement SFS วันนี้** — list endpoint ปัจจุบัน (เช่น `GET /api/v1/admins/roles`,
> `GET /api/v1/producers/roles`) คืน full set, ไม่มี `OrderBy`, ไม่มี paging, ไม่รับ query-string sort/filter เลย.
> (`GetProductsQuery` + `IProductRepository.ListByTenantAsync` มีอยู่จริงและ implement ครบ แต่ **ยังไม่ถูก
> wire เข้า HTTP endpoint ใด** — endpoint ของ product วันนี้มีแค่ `POST /products`.) เอกสารนี้คือ
> **target convention**: เมื่อทีมเริ่มเพิ่ม SFS ให้ endpoint ใด ให้ทำตามรูปแบบนี้ทั้งโปรเจกต์ เพื่อให้ทุกโมดูล
> มี contract เดียว. Query-string contract คงรูปแบบเดียวกับ `nong-kaewta-api` โดยตั้งใจ (contract เดียวข้ามโปรเจกต์).

> ข้อควรรู้ก่อนเริ่ม (จาก research ของ pol-core stack): (1) SFS เป็น **greenfield** — ยังไม่มี
> `PagedResult<T>` หรือ pagination type ใดใน repo; บ้านของ contract types คือ `BuildingBlocks.Application`.
> (2) โปรเจกต์ **ไม่มี** FluentValidation และ **ไม่มี** dynamic-LINQ (`System.Linq.Dynamic.Core`) — SFS
> ต้องสร้างบน `System.Text.Json` + strongly-typed EF Core LINQ เท่านั้น, ห้ามเพิ่ม dependency.
> (3) host เป็น Minimal API **ไม่มี** global `JsonStringEnumConverter` — string enum บน wire **ไม่ทำงานเอง**
> ต้อง annotate converter ที่ enum โดยตรง (ดู section 2.3). (4) `TreatWarningsAsErrors=true` + `Nullable enable`
> ทั้ง solution — ทุก snippet ต้อง warning-clean และ null-annotated.

- ต้นฉบับ (แนวคิด): `nong-kaewta-api/docs/developer-guide/SEARCH_FILTER_SORT_GUIDE.md`
- โครงสร้าง handler/repository: `docs/reference/src-structure.md`
- entity fields: `docs/reference/entity-fields.md`
- RLS floor + tenant isolation: `../../.ai/shared/ARCHITECTURE.md`, `../../.ai/shared/SECURITY_RULES.md`

---

## สารบัญ

1. [ภาพรวม + ขอบเขต](#1-ภาพรวม--ขอบเขต)
2. [Query API contract (JSON-DSL)](#2-query-api-contract-json-dsl)
3. [Pagination](#3-pagination)
4. [Filter](#4-filter)
5. [Sort](#5-sort)
6. [Search](#6-search)
7. [Module-specific typed filter DTO](#7-module-specific-typed-filter-dto)
8. [Whitelist implementation variants](#8-whitelist-implementation-variants)
9. [Security + RLS interplay](#9-security--rls-interplay)
10. [Common mistakes / anti-patterns](#10-common-mistakes--anti-patterns)
11. [Testing guidance](#11-testing-guidance)
12. [ตัวอย่าง end-to-end (C#)](#12-ตัวอย่าง-end-to-end-c)

---

## 1. ภาพรวม + ขอบเขต

SFS คือชั้นที่แปลง query string จาก client ให้กลายเป็น EF Core `IQueryable<T>` ที่ปลอดภัย แล้ว execute
เป็น SQL บน SQL Server. flow แบบ layered (mirror pattern ของ list handler ที่มีอยู่ เช่น `ListRolesHandler`):

```
HTTP query string  (?page=&limit=&filters=&sort=&search=)
        |  parse (System.Text.Json) — Hosts/Api layer เท่านั้น (ที่เดียวที่รู้จัก HttpContext)
        v
FilterOption / SortOption / SearchOption records  (BuildingBlocks.Application)
        |  Mediator IQuery<PagedResult<T>>  ->  IQueryHandler (ValueTask<T>, method Handle)
        |  [TenantGuardBehavior] ถ้า query : ITenantScoped และไม่มี tenant -> TenantBindingException
        v
Repository: whitelist -> EF .Where / .OrderBy / .Skip / .Take  (+ LongCountAsync)
        |  tenant floor ครอบอยู่แล้วเสมอ (SQL Server RLS security policy + SESSION_CONTEXT
        |  + explicit .Where(TenantId) ใน tenant repo) — SFS แคบผลลง ไม่มีทางขยาย tenant scope
        v
SQL Server 2025  ->  PagedResult<T>
```

**สถานะ pol-core:** ทั้ง 4 list endpoint ที่ ship แล้ว (`GET /api/v1/admins/roles`, `/api/v1/admins/permissions`,
`/api/v1/producers/roles`, `/api/v1/producers/permissions`) คืน full set; roles คืนแบบ **ไม่มี `OrderBy`** (unordered),
permission catalog เรียงตาม `SortOrder`. **ไม่มี endpoint ใดใช้ `Skip`/`Take` หรือรับ sort/filter จาก
query string.** เอกสารนี้จึงเป็นการ **แนะนำพฤติกรรมใหม่** ไม่ใช่บันทึกพฤติกรรมเดิม.

**ขอบเขตเอกสารนี้:** เฉพาะ search / filter / sort / pagination + security + RLS interplay + ตัวอย่าง.
ไม่รวมเรื่องอื่นจาก guide ต้นฉบับ (auth, transaction, job, file management ฯลฯ) — คนละแกน.

**หลักการพอร์ต:** เอา *แนวคิด* (per-field whitelist, silent-drop, escape-LIKE, default-sort, NULLS-last)
มาปรับเป็นสำนวน .NET — **ไม่ก๊อป syntax PostgreSQL**. ข้อต่างสำคัญเมื่อพอร์ตจาก Postgres มา SQL Server:

| ประเด็น (Postgres ต้นฉบับ)        | SQL Server / EF Core 10 (pol-core)                                                       |
| --------------------------------- | ---------------------------------------------------------------------------------------- |
| `ILIKE` (case-insensitive)        | ไม่มี `ILIKE`; default collation เป็น **CI** อยู่แล้ว -> `LIKE` ≈ `ILIKE`                 |
| `NULLS LAST` / `NULLS FIRST`      | ไม่มี syntax; จำลองด้วย `ORDER BY CASE WHEN col IS NULL THEN 1 ELSE 0 END, col`           |
| escape set ของ LIKE = `% _ \`     | SQL Server เพิ่ม `[` เป็น metachar -> escape `% _ [` (และ `\` เป็น escape char)           |
| `Object.hasOwn` กัน prototype-pollution | เป็น hazard เฉพาะ JS; ฝั่ง C# = allow-list membership (`FrozenDictionary`/`FrozenSet`) + ห้าม interpolate field เข้า SQL |
| `@Transform` + `plainToInstance`  | custom `JsonConverter` / `JsonSerializer.Deserialize` เข้า typed DTO + data-annotation   |

---

## 2. Query API contract (JSON-DSL)

ทุก list endpoint ที่รองรับ SFS ใช้ query param ชุดเดียวกัน. ค่าที่ซับซ้อน (`filters`, `sort`, `search`)
เป็น **JSON ที่ url-encode แล้ว** วางใน query string.

### 2.1 Query parameters

| Param     | ชนิด             | Default | หมายเหตุ                                              |
| --------- | ---------------- | ------- | ---------------------------------------------------- |
| `page`    | int              | `1`     | หน้า เริ่มที่ 1, clamp `>= 1` (ดู section 3)          |
| `limit`   | int              | `25`    | ขนาดหน้า clamp `[1..100]` (ดู section 3)              |
| `filters` | JSON array       | `[]`    | `[{ "field", "operator", "value" \| "values" }]`      |
| `sort`    | JSON array       | `[]`    | `[{ "field", "order": "ASC" \| "DESC" }]` เรียงตามลำดับ |
| `search`  | JSON object      | `null`  | `{ "query", "fields": [...] }`                        |

casing convention (ตรงกับต้นฉบับ, ให้ contract ข้ามโปรเจกต์เหมือนกัน): field เป็น **camelCase**
(`createdAt`, `priceMinorUnits`); operator เป็น **snake/lower** (`eq`, `gte`, `not_in`, `is_null`);
sort order เป็น literal `"ASC"` / `"DESC"`.

### 2.2 Operators (14 ตัว)

`operator` เป็น string ตาม enum ด้านล่าง. field ที่ยอมให้ใช้ operator ใดถูกกำหนดด้วย per-field whitelist
(section 4) — operator ที่ field ไม่อนุญาตจะถูก **silent-drop**.

| Enum (C#)            | JSON value     | Value shape          | SQL Server                       |
| -------------------- | -------------- | -------------------- | -------------------------------- |
| `Equals`             | `eq`           | `value` (scalar)     | `= @p`                           |
| `NotEquals`          | `ne`           | `value`              | `<> @p`                          |
| `GreaterThan`        | `gt`           | `value`              | `> @p`                           |
| `GreaterThanOrEqual` | `gte`          | `value`              | `>= @p`                          |
| `LessThan`           | `lt`           | `value`              | `< @p`                           |
| `LessThanOrEqual`    | `lte`          | `value`              | `<= @p`                          |
| `Like`               | `like`         | `value` (string)     | `LIKE @p ESCAPE '\'`             |
| `ILike`              | `ilike`        | `value` (string)     | `LIKE @p ESCAPE '\'` (ดูหมายเหตุ) |
| `In`                 | `in`           | `values[]`           | `IN (@p0, @p1, ...)`             |
| `NotIn`              | `not_in`       | `values[]`           | `NOT IN (...)`                   |
| `IsNull`             | `is_null`      | —                    | `IS NULL`                        |
| `IsNotNull`          | `is_not_null`  | —                    | `IS NOT NULL`                    |
| `Between`            | `between`      | `values[2]`          | `BETWEEN @lo AND @hi`            |
| `Contains`           | `contains`     | `value` (string)     | `LIKE '%'+@p+'%' ESCAPE '\'`     |

> หมายเหตุ `ilike`: SQL Server ไม่มี `ILIKE` แยกต่างหาก. ค่า default collation ของ SQL Server เป็น
> **case-insensitive (CI)** อยู่แล้ว ดังนั้น `like` กับ `ilike` ให้ผลเหมือนกันภายใต้ CI collation.
> คง `ilike` ไว้ใน contract เพื่อ compat กับต้นฉบับ แต่ทั้งคู่แปลเป็น `EF.Functions.Like(...)` เดียวกัน.
> ถ้าคอลัมน์ใช้ collation แบบ CS (case-sensitive) และต้องการ case-insensitive จริง ให้ระบุ
> `COLLATE` ที่คอลัมน์นั้นตอน map — เป็น per-field decision ไม่ใช่ default.

> หมายเหตุ `like` vs `contains`: ในต้นฉบับ TS enum ประกาศ `LIKE` ไว้แต่ **ไม่มี case ใน switch** (มีแต่
> `ILIKE`/`CONTAINS`) — filter ที่ส่ง `like` มาจึงไม่ทำอะไรเลยแบบเงียบ ๆ. ใน pol-core เราปิดช่องนี้:
> `Like`/`ILike` = client เป็นคนใส่ `%`/`_` เอง (pattern ทั้งก้อน), ส่วน `Contains` = เราห่อ `%...%` ให้.
> **ทั้ง `Like`, `ILike`, `Contains` ต้องผ่าน `EscapeLike` เหมือนฝั่ง search** (ต้นฉบับ TS ลืม escape ฝั่ง
> filter — เป็น latent bug; อย่าพอร์ตข้อผิดนั้นมา).

### 2.3 Contract types (BuildingBlocks.Application)

records/enum วางที่ `BuildingBlocks.Application` (lib เบา ๆ ที่พึ่งแค่ `SharedKernel` + `Contracts` +
`Mediator.Abstractions`, ไม่พึ่ง ASP.NET) — บ้านเดียวกับ `ITenantScoped`, exceptions, `TenantGuardBehavior`
ที่ map เป็น HTTP problem อยู่แล้ว. ให้ทุกโมดูลใช้ร่วม:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildingBlocks.Application;

// host ไม่มี global JsonStringEnumConverter -> ต้อง annotate converter ที่ enum โดยตรง
// มิฉะนั้น default (JsonSerializerDefaults.Web) จะ (de)serialize enum เป็น integer
[JsonConverter(typeof(JsonStringEnumConverter<FilterOperator>))]
public enum FilterOperator
{
    [JsonStringEnumMemberName("eq")]           Equals,
    [JsonStringEnumMemberName("ne")]           NotEquals,
    [JsonStringEnumMemberName("gt")]           GreaterThan,
    [JsonStringEnumMemberName("gte")]          GreaterThanOrEqual,
    [JsonStringEnumMemberName("lt")]           LessThan,
    [JsonStringEnumMemberName("lte")]          LessThanOrEqual,
    [JsonStringEnumMemberName("like")]         Like,
    [JsonStringEnumMemberName("ilike")]        ILike,
    [JsonStringEnumMemberName("in")]           In,
    [JsonStringEnumMemberName("not_in")]       NotIn,
    [JsonStringEnumMemberName("is_null")]      IsNull,
    [JsonStringEnumMemberName("is_not_null")]  IsNotNull,
    [JsonStringEnumMemberName("between")]      Between,
    [JsonStringEnumMemberName("contains")]     Contains,
}

// order บน wire คือ "ASC"/"DESC" (string) -> ต้องมี converter + member name เช่นกัน
// ไม่งั้น "order":"ASC" จะ deserialize ไม่ผ่าน (default = integer enum) -> โยน JsonException -> 400
[JsonConverter(typeof(JsonStringEnumConverter<SortDirection>))]
public enum SortDirection
{
    [JsonStringEnumMemberName("ASC")] Asc,
    [JsonStringEnumMemberName("DESC")] Desc,
}

// value/values เป็น JsonElement เพราะชนิดจริงขึ้นกับ field — แปลงเป็นชนิดปลายทางตอน apply (section 4).
public sealed record FilterOption(string Field, FilterOperator Operator,
    JsonElement? Value = null, JsonElement[]? Values = null);

public sealed record SortOption(string Field, SortDirection Order = SortDirection.Asc);

public sealed record SearchOption(string Query, string[]? Fields = null);

/// base ของทุก paged query — โมดูลสืบทอดแล้วเติม field เฉพาะทางได้
public abstract record PagedQuery
{
    public int Page { get; init; } = 1;
    public int Limit { get; init; } = 25;
    public IReadOnlyList<FilterOption> Filters { get; init; } = [];
    public IReadOnlyList<SortOption> Sort { get; init; } = [];
    public SearchOption? Search { get; init; }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Limit, long Total)
{
    public int TotalPages => Limit <= 0 ? 0 : (int)Math.Ceiling((double)Total / Limit);
}
```

> ทำไม `JsonElement?` ไม่ใช่ `object?`: ค่าจริงของ filter ขึ้นกับ field (bool/long/string/DateTime) และ
> deserializer ไม่รู้ชนิดปลายทางตอน parse. `JsonElement` เก็บ raw JSON ไว้ให้ apply-step แปลงเป็นชนิดจริง
> (`GetBoolean()`, `GetInt64()`, `GetString()`, `GetDateTime()`) หลังผ่าน whitelist แล้ว — type-safe และ
> ไม่ต้องเดาชนิดตั้งแต่ parse.

### 2.4 ตัวอย่าง query string

```http
# sort field เดียว
GET /api/v1/admins/roles?sort=[{"field":"name","order":"DESC"}]

# sort หลาย field (ตามลำดับ)
GET /api/v1/admins/roles?sort=[{"field":"name","order":"ASC"},{"field":"code","order":"DESC"}]

# filter เดียว  (status บน wire เป็น lowercase เสมอ — ดู section 4)
GET /api/v1/admins/roles?filters=[{"field":"status","operator":"eq","value":"active"}]

# filter IN
GET /api/v1/admins/roles?filters=[{"field":"code","operator":"in","values":["super_admin","support"]}]

# filter ช่วงวัน (ใช้ gte + lte — สอง filter, ตรงกับ pattern ต้นฉบับ)
GET /products?filters=[{"field":"createdAt","operator":"gte","value":"2026-01-01"},{"field":"createdAt","operator":"lte","value":"2026-12-31"}]

# filter BETWEEN (ทางเลือก — values[2])
GET /products?filters=[{"field":"priceMinorUnits","operator":"between","values":[1000,5000]}]

# search
GET /api/v1/admins/roles?search={"query":"admin","fields":["name","description"]}

# รวม SFS + pagination
GET /products?page=1&limit=25&sort=[{"field":"createdAt","order":"DESC"}]&filters=[{"field":"isActive","operator":"eq","value":true}]&search={"query":"iphone","fields":["name"]}

# module-specific filter (ดู section 7)
GET /products?productFilters={"minPriceMinorUnits":1000,"activeOnly":true}
```

> JSON ในตัวอย่างเขียนแบบอ่านง่าย — ของจริง client ต้อง `encodeURIComponent(...)` ทั้งค่าก่อนใส่ query string.

### 2.5 Parsing (Hosts/Api layer)

parse ที่ชั้น Hosts (ที่เดียวที่รู้จัก `HttpContext`). ใช้ `System.Text.Json` — ไม่เพิ่ม dependency.
คืนค่าเป็น named tuple (สำนวนที่ใช้จริงในโปรเจกต์):

```csharp
using System.Text.Json;
using BuildingBlocks.Application;

namespace Hosts.Api;

internal static class SfsQueryParser
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static (int Page, int Limit, IReadOnlyList<FilterOption> Filters,
                   IReadOnlyList<SortOption> Sort, SearchOption? Search) Parse(IQueryCollection q)
        => (
            Page:    Math.Max(TryInt(q["page"], 1), 1),            // clamp page >= 1 กัน OFFSET ติดลบ
            Limit:   Math.Clamp(TryInt(q["limit"], 25), 1, 100),   // clamp = safety, ไม่ 400
            Filters: Deserialize<List<FilterOption>>(q["filters"]) ?? [],
            Sort:    Deserialize<List<SortOption>>(q["sort"]) ?? [],
            Search:  Deserialize<SearchOption>(q["search"])
        );

    private static T? Deserialize<T>(string? raw) where T : class
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonSerializer.Deserialize<T>(raw, Json); }
        catch (JsonException ex)
        {
            // JSON พัง = client error ที่ระบุตัวได้ -> โยน ArgumentException ซึ่ง ProblemDetailsExceptionHandler
            // map เป็น 400 ProblemDetails (ดู section 2.6) — ห้าม BadHttpRequestException (มัน = IOException -> 500)
            throw new ArgumentException("Malformed SFS query parameter.", ex);
        }
    }

    private static int TryInt(string? s, int fallback) => int.TryParse(s, out var v) ? v : fallback;
}
```

> **query-cost caps (REQ-6.6):** parser ควร reject ด้วย `ArgumentException` (-> 400) เมื่อ `filters` เกิน 50,
> `sort` เกิน 10, หรือ `values[]` ของ `in`/`not_in` เกิน 200 — กัน expression/SQL บวม และกันชน SQL Server
> parameter limit (~2100). ค่าคงตัวปรับได้ แต่ต้องมีเพดานเสมอ (ไม่ใช่ silent truncation).

### 2.6 Error contract

| กรณี                                          | ผลลัพธ์                                                            |
| --------------------------------------------- | ----------------------------------------------------------------- |
| JSON ใน `filters`/`sort`/`search` พัง         | **400 ProblemDetails** (โยน `ArgumentException` -> 400 bucket ของ `ProblemDetailsExceptionHandler`) |
| field / operator / sort-field ไม่อยู่ใน whitelist | **silent-drop** (ข้ามเงียบ ๆ, query ที่เหลือทำงานต่อ — ดู section 9) |
| typed filter DTO validate ไม่ผ่าน (ถ้าโมดูลมี) | **400 ProblemDetails** (ดู section 7)                              |
| `limit` เกิน `[1..100]` / `page` < 1          | **clamp เงียบ ๆ** (ไม่ 400)                                        |

> **ทำไมต้อง `ArgumentException` ไม่ใช่ `BadHttpRequestException`:** `ProblemDetailsExceptionHandler.Map`
> เลือก status ตาม type ด้วย `switch` และมี arm `ArgumentException => 400` (ทั้ง repo ใช้ pattern นี้ เช่น
> `ParseRoleStatus`, `ProvisionTenant`). `Microsoft.AspNetCore.Http.BadHttpRequestException` **สืบทอดจาก
> `IOException`** ไม่ใช่ `ArgumentException` -> จะตกไป arm `_ => 500` (opaque 500) ทำ client error พลิกเป็น
> server error. จึงต้องโยน `ArgumentException` (หรือ subclass เช่น `ArgumentOutOfRangeException`) เสมอ.

> pol-core มี response surface มาตรฐานสองแบบ: **JSON API -> RFC7807 `ProblemDetails`** (ผ่าน shared
> `ProblemDetailsExceptionHandler`, detail เป็น fixed string ต่อ bucket ไม่เคย leak `exception.Message`);
> ส่วน browser callback -> 302 redirect + `?reason=`. SFS อยู่ฝั่ง JSON API ทั้งหมด -> ใช้ `ProblemDetails`.

---

## 3. Pagination

- `page` เริ่มที่ 1, **clamp `>= 1` ที่ parser** — กัน `page` = 0/ติดลบ ทำ `Skip((page-1)*limit)` เป็นค่าลบ
  -> SQL Server `OFFSET` ติดลบ -> opaque 500 (client-triggerable DoS).
- `limit` default 25, **clamp `[1..100]`** — กัน `limit` มหาศาลลาก DB ล่ม.
- `PagedResult<T>` ห่อผลลัพธ์ + metadata (`Page`, `Limit`, `Total`, `TotalPages`).
- ลำดับใน repository สำคัญ: **count หลัง filter/search แต่ก่อน paging**.

```csharp
// src = IQueryable หลัง ApplySearch + ApplyFilters แล้ว (ยังไม่ Sort/Skip/Take)
long total = await src.LongCountAsync(ct);          // นับหลัง Where/Search
var items = await src.ApplySort(q.Sort)             // section 5 (มี default fallback บังคับ)
                     .Skip((q.Page - 1) * q.Limit)  // q.Page >= 1 แล้วจาก parser -> offset >= 0
                     .Take(q.Limit)
                     .Select(project)
                     .ToListAsync(ct);
return new PagedResult<T>(items, q.Page, q.Limit, total);
```

`LongCountAsync<T>(IQueryable<T>, CancellationToken)` คืน `Task<long>` — ใช้ `long` (ไม่ใช่ `int`) กัน
overflow บน table ใหญ่. ทุก async EF overload รับ `CancellationToken` เสมอ — ส่ง `ct` ทุกครั้ง.

> `page` clamp `>= 1` ที่ parser แล้ว จึงไม่มี OFFSET ติดลบ. หมายเหตุ: ถ้าต้องรองรับ `page` ที่ใหญ่มาก
> ระวัง `(q.Page - 1) * q.Limit` overflow `int` — ให้คำนวณ offset เป็น `long` หรือ clamp `page` เพดานบน
> ตามจำนวนหน้าจริงก่อน `Skip` (deep-offset แบบนั้นควรพิจารณา keyset แทน ดูด้านล่าง).

> ต้นทุน: `LongCountAsync` เป็น query แยกอีก 1 รอบ. ยอมรับได้สำหรับ list console ปกติ. ถ้า table ใหญ่มาก
> และไม่ต้องโชว์ total จริง ค่อยพิจารณา **keyset / seek pagination** เป็น optimization ทีหลัง — ไม่ทำ default.
> (keyset ใช้ `WHERE (sortKey, id) > (@lastKey, @lastId)` แทน `Skip` เพื่อเลี่ยง deep-offset scan; ต้องมี
> sort key ที่ stable + unique tiebreaker. เกินขอบเขต baseline นี้.)

---

## 4. Filter

per-field whitelist บอกว่า field ไหนกรองได้ และด้วย operator ใดบ้าง. เก็บเป็น `FrozenDictionary<string,
FilterOperator[]>` (immutable, เทียบ `Object.freeze` ในต้นฉบับ) วางไว้ข้าง repository ของโมดูลนั้น:

```csharp
using System.Collections.Frozen;
using BuildingBlocks.Application;

file static class RoleQueryFields
{
    public static readonly FrozenDictionary<string, FilterOperator[]> Filter =
        new Dictionary<string, FilterOperator[]>
        {
            ["status"] = [FilterOperator.Equals, FilterOperator.In],
            ["code"]   = [FilterOperator.Equals, FilterOperator.In],
        }.ToFrozenDictionary();
    // ... Search / Sort whitelist ดู section 5-6
}
```

apply ด้วย **two-gate guard** (field ต้องอยู่ใน whitelist **และ** operator ต้องอยู่ใน list ของ field นั้น)
แล้ว switch `(field, operator)` map เป็น property จริง — **type-safe, ไม่ reflection, ไม่ interpolate ชื่อ
field เข้า SQL**:

```csharp
public static IQueryable<AdminRole> ApplyFilters(
    this IQueryable<AdminRole> q, IReadOnlyList<FilterOption> filters)
{
    foreach (var f in filters)
    {
        if (!RoleQueryFields.Filter.TryGetValue(f.Field, out var allowed)) continue; // silent-drop: field
        if (!allowed.Contains(f.Operator)) continue;                                  // silent-drop: operator

        q = (f.Field, f.Operator) switch
        {
            ("status", FilterOperator.Equals) =>
                q.Where(r => r.Status == ParseStatus(f.Value!.Value.GetString())),
            // In ต้องมี Values[] — null/ว่าง จะไม่แมตช์ arm นี้ แล้วตกไป _ => q (silent-drop)
            ("status", FilterOperator.In) when f.Values is { Length: > 0 } =>
                q.Where(r => f.Values!.Select(v => ParseStatus(v.GetString())).Contains(r.Status)),
            ("code", FilterOperator.Equals) => q.Where(r => r.Code == f.Value!.Value.GetString()),
            ("code", FilterOperator.In) when f.Values is { Length: > 0 } =>
                q.Where(r => f.Values!.Select(v => v.GetString()).Contains(r.Code)),
            _ => q,   // combo ที่หลุด whitelist / Values ว่าง (กันพลาด, silent-drop)
        };
    }
    return q;
}

// wire ของ role status เป็น lowercase เสมอ (B2: host ไม่มี global string-enum converter) — parse ให้ตรง
// ParseRoleStatus ฝั่ง write. Enum.Parse<AdminRoleStatus> ตรง ๆ จะ throw กับ "active"/"inactive"
// (case-sensitive + สมาชิก enum เป็น PascalCase). ParseStatus ถูก EF eval ฝั่ง client (ไม่พึ่ง r) -> เป็น constant.
private static AdminRoleStatus ParseStatus(string? s) => s?.ToLowerInvariant() switch
{
    "active"   => AdminRoleStatus.Active,
    "inactive" => AdminRoleStatus.Inactive,
    _ => throw new ArgumentException($"Invalid role status '{s}'."),   // -> 400 ProblemDetails
};
```

### 4.1 Per-operator apply reference (ครบ 14 ตัว)

ตัวช่วยแปลง `JsonElement` -> typed ขึ้นกับชนิดคอลัมน์ปลายทาง: `GetString()`, `GetBoolean()`,
`GetInt64()`, `GetDateTime()`, `GetGuid()`. ด้านล่างใช้คอลัมน์ตัวแทน — `Name` (`string`),
`PriceMinorUnits` (`long`), `IsActive` (`bool`), `CreatedAt` (`DateTime`), `Description` (`string?`) —
เพื่อโชว์ทุก operator พร้อม SQL ที่ EF Core 10 แปลออกมา (`EscapeLike` ดู section 6):

```csharp
// ---- comparison / equality (value: scalar) ----

// Equals ("eq") — SQL: [Name] = @p
q.Where(x => x.Name == f.Value!.Value.GetString());

// NotEquals ("ne") — SQL: [Name] <> @p   (NULL ถูกกรองออกตาม 3-valued logic ของ SQL — ตั้งใจ)
q.Where(x => x.Name != f.Value!.Value.GetString());

// GreaterThan ("gt") — SQL: [PriceMinorUnits] > @p
q.Where(x => x.PriceMinorUnits > f.Value!.Value.GetInt64());

// GreaterThanOrEqual ("gte") — SQL: [CreatedAt] >= @p
q.Where(x => x.CreatedAt >= f.Value!.Value.GetDateTime());

// LessThan ("lt") — SQL: [PriceMinorUnits] < @p
q.Where(x => x.PriceMinorUnits < f.Value!.Value.GetInt64());

// LessThanOrEqual ("lte") — SQL: [CreatedAt] <= @p
q.Where(x => x.CreatedAt <= f.Value!.Value.GetDateTime());

// ---- text pattern (value: string) — escape เสมอ (ต่างจากต้นฉบับ TS ที่ลืม escape ฝั่ง filter) ----

// Like ("like") — client ใส่ pattern เอง (%/_ ที่ client ตั้งใจจะถูก escape เป็น literal ด้วย -> ใช้ Contains
//   ถ้าต้องการ substring). SQL: [Name] LIKE @p ESCAPE N'\'
q.Where(x => EF.Functions.Like(x.Name, $"{EscapeLike(f.Value!.Value.GetString()!)}", "\\"));

// ILike ("ilike") — เหมือน Like ภายใต้ CI collation. SQL: [Name] LIKE @p ESCAPE N'\'
q.Where(x => EF.Functions.Like(x.Name, $"{EscapeLike(f.Value!.Value.GetString()!)}", "\\"));

// Contains ("contains") — เราห่อ %...% ให้. SQL: [Name] LIKE @p ESCAPE N'\'  (@p = %escaped%)
q.Where(x => EF.Functions.Like(x.Name, $"%{EscapeLike(f.Value!.Value.GetString()!)}%", "\\"));

// ---- set membership (values[]) — ต้องมี Values และ length > 0 มิฉะนั้นข้าม ----

// In ("in") — SQL: [Code] IN (@p0, @p1, ...)
if (f.Values is { Length: > 0 })
    q = q.Where(x => f.Values!.Select(v => v.GetString()).Contains(x.Code));   // f.Values! = ตัด CS8602 ใน lambda

// NotIn ("not_in") — SQL: [Code] NOT IN (@p0, @p1, ...)
if (f.Values is { Length: > 0 })
    q = q.Where(x => !f.Values!.Select(v => v.GetString()).Contains(x.Code));

// ---- null checks (ไม่มี value) ----

// IsNull ("is_null") — SQL: [Description] IS NULL
q.Where(x => x.Description == null);

// IsNotNull ("is_not_null") — SQL: [Description] IS NOT NULL
q.Where(x => x.Description != null);

// ---- range (values[2]) — ต้องมี >= 2 element มิฉะนั้นข้าม ----

// Between ("between") — SQL: [PriceMinorUnits] >= @lo AND [PriceMinorUnits] <= @hi
if (f.Values is { Length: >= 2 })
{
    long lo = f.Values[0].GetInt64(), hi = f.Values[1].GetInt64();
    q = q.Where(x => x.PriceMinorUnits >= lo && x.PriceMinorUnits <= hi);
}
```

> **null-forgiving ใน lambda:** pattern `f.Values is { Length: > 0 }` narrow ค่าให้ non-null เฉพาะใน scope
> ตรง ๆ เท่านั้น — nullable flow analysis **ไม่** พา state นั้นเข้าไปใน lambda body ที่ capture `f`. ดังนั้น
> ต้องใช้ `f.Values!` ภายใน lambda (มิฉะนั้น CS8602 -> build fail ภายใต้ `TreatWarningsAsErrors=true`).

> **coercion ต้อง guard -> 400:** `f.Value!.Value.GetInt64()` / `GetDateTime()` กับค่าที่ชนิดไม่ตรง (client
> ส่ง `priceMinorUnits eq "abc"`) จะ **throw** — `GetInt64` โยน `InvalidOperationException` (-> 409),
> `GetDateTime` โยน `FormatException` (-> 500) ตาม `ProblemDetailsExceptionHandler`. ทั้งคู่ผิดสัญญา REQ-8.
> ห่อทุก `Get*()` ด้วย try/catch หรือใช้ `TryGet*` แล้ว **re-throw `ArgumentException` (-> 400)** เสมอ
> (แบบเดียวกับ `ParseStatus`).

| Operator            | value/values | JsonElement accessor (ตัวอย่าง) | SQL Server ที่ได้                          |
| ------------------- | ------------ | ------------------------------- | ------------------------------------------ |
| `Equals`            | value        | `GetString()` / `GetBoolean()`  | `col = @p`                                  |
| `NotEquals`         | value        | ตามชนิดคอลัมน์                    | `col <> @p`                                |
| `GreaterThan`       | value        | `GetInt64()` / `GetDateTime()`  | `col > @p`                                  |
| `GreaterThanOrEqual`| value        | ตามชนิด                          | `col >= @p`                                |
| `LessThan`          | value        | ตามชนิด                          | `col < @p`                                 |
| `LessThanOrEqual`   | value        | ตามชนิด                          | `col <= @p`                                |
| `Like` / `ILike`    | value string | `GetString()` + `EscapeLike`    | `col LIKE @p ESCAPE N'\'`                   |
| `Contains`          | value string | `GetString()` + `EscapeLike`    | `col LIKE @p ESCAPE N'\'` (`@p = %esc%`)    |
| `In`                | values[]     | `Select(v => v.Get...())`       | `col IN (@p0, @p1, ...)`                    |
| `NotIn`             | values[]     | เดียวกัน                         | `col NOT IN (@p0, @p1, ...)`               |
| `IsNull`            | —            | —                               | `col IS NULL`                              |
| `IsNotNull`         | —            | —                               | `col IS NOT NULL`                          |
| `Between`           | values[2]    | `Values[0]`, `Values[1]`        | `col >= @lo AND col <= @hi`                |

> **ทำไม switch ไม่ใช่ dynamic-LINQ / expression-tree แบบ generic:** switch อ่านออก, type-safe, EF แปลตรง
> ไปตรงมา, และ **ไม่มี dependency ใหม่** — โปรเจกต์ตั้งใจไม่มี `System.Linq.Dynamic.Core` (ซึ่งจะเป็นทั้ง dep
> ที่ต้อง review และ injection surface). ถ้า field เยอะมากจน switch บวม ให้ประกอบ `Expression` ด้วยมือ
> (section 6) — ไม่ใช่ string-eval'd dynamic LINQ.

---

## 5. Sort

whitelist เป็น `FrozenSet<string>`. apply แล้ว silent-drop field นอก list. **ต้องมี default-sort fallback
เสมอ** ไม่งั้น paging (`Skip/Take`) ไม่ deterministic (ผลจะสลับหน้าไปมาระหว่าง request).

SQL Server ไม่มี `NULLS LAST` — จำลองด้วยการเรียง boolean `col == null` ก่อน (false=0 มาก่อน, null=1 ไปท้าย).
EF Core 10 แปล `OrderBy(x => x.Col == null).ThenBy(x => x.Col)` เป็น
`ORDER BY CASE WHEN [Col] IS NULL THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END, [Col]` — verified บน
EF Core 10 SqlServer จริง. คอลัมน์ที่ non-nullable ไม่ต้องทำขั้น NULLS-last.

ตัวอย่างด้านล่าง typed กับ `AdminRole` (ใช้ตลอดทั้งเอกสาร): `Code`/`Name` เป็น **non-nullable**,
`Description` เป็น `string?` (**nullable** — ใช้เดโม NULLS-last). `AdminRole` **ไม่มี `CreatedAt`** จึง
default-fallback ใช้ `Code`:

```csharp
public static readonly FrozenSet<string> SortFields =
    new[] { "code", "name", "description" }.ToFrozenSet();

public static IQueryable<AdminRole> ApplySort(
    this IQueryable<AdminRole> q, IReadOnlyList<SortOption> sort)
{
    IOrderedQueryable<AdminRole>? o = null;
    foreach (var s in sort)
    {
        if (!SortFields.Contains(s.Field)) continue;           // silent-drop
        bool asc = s.Order == SortDirection.Asc;

        o = (s.Field, first: o is null) switch
        {
            // description = nullable -> NULLS LAST: เรียง (Description == null) ก่อน แล้วค่อย Description
            ("description", true)  => asc ? q.OrderBy(r => r.Description == null).ThenBy(r => r.Description)
                                          : q.OrderBy(r => r.Description == null).ThenByDescending(r => r.Description),
            ("description", false) => asc ? o!.ThenBy(r => r.Description == null).ThenBy(r => r.Description)
                                          : o!.ThenBy(r => r.Description == null).ThenByDescending(r => r.Description),

            // name / code = non-null -> ไม่ต้องทำ NULLS LAST (plain OrderBy/OrderByDescending)
            ("name", true)  => asc ? q.OrderBy(r => r.Name) : q.OrderByDescending(r => r.Name),
            ("name", false) => asc ? o!.ThenBy(r => r.Name) : o!.ThenByDescending(r => r.Name),

            ("code", true)  => asc ? q.OrderBy(r => r.Code) : q.OrderByDescending(r => r.Code),
            ("code", false) => asc ? o!.ThenBy(r => r.Code) : o!.ThenByDescending(r => r.Code),

            _ => o,
        };
    }

    return o ?? q.OrderByDescending(r => r.Code);   // default fallback (บังคับ) — AdminRole ไม่มี CreatedAt
}
```

- **field -> property mapping** ทำผ่าน `switch` (`"name"` -> `r.Name`) — ไม่เคยเอา string จาก client ไปต่อ
  เป็น `ORDER BY`. ชื่อ column ทุกตัว compile-checked.
- **multi-field** = field แรกใช้ `OrderBy`/`OrderByDescending` (reset ordering), field ถัด ๆ ไปใช้
  `ThenBy`/`ThenByDescending` (สะสม). ตามลำดับที่ client ส่งใน `sort` array.
- **default-sort fallback** (`o ?? ...`) ทำงานเมื่อไม่มี field ใด survive whitelist — บังคับต้องมีเสมอ.
  entity ที่มี `CreatedAt` (เช่น `Product`) ควร fallback เป็น `OrderByDescending(x => x.CreatedAt)`.

> **caveat helper generic `OrderByNullsLast<T,TKey>`:** ถ้าจะทำ helper generic ต้องสร้าง expression
> `x => key(x) == null` เอง และ **guard ให้ทำ NULLS-last เฉพาะคอลัมน์ nullable** (reference type หรือ
> `Nullable<>`). `Expression.Equal(keyBody, Expression.Constant(null))` กับ **value-type ที่ไม่ nullable**
> (เช่น `long`, `DateTime`, `bool`) จะ **throw ตอน build expression**. baseline ที่แนะนำคือ inline ต่อ field
> แบบด้านบน (ตรงไปตรงมา ถูกเสมอ, warning-clean) — ทำ helper generic เมื่อ field ซ้ำเยอะจนคุ้มเท่านั้น.

> ต้นฉบับ TS เก็บ `ISortOption.nullsFirst` ไว้เพื่อ backward-compat แต่ repository **ไม่สนใจ** (NULLS LAST
> unconditional). ใน pol-core เราไม่รับ flag นั้นตั้งแต่ contract (`SortOption` ไม่มี `nullsFirst`) —
> NULLS-last เป็น invariant คงที่, ตัด flag ที่ไม่มีผลออกไปเลย.

---

## 6. Search

free-text OR ข้ามหลาย field ที่ whitelist. `SEARCH_FIELDS` ต้องเป็น **เฉพาะ non-sensitive text field**
เท่านั้น — ห้ามใส่ field ที่เป็น secret/token/hash. escape wildcard ของ SQL Server (`\ % _ [`) ด้วย `\`
แล้วส่ง `ESCAPE '\'` ผ่าน overload ที่ 3 ของ `EF.Functions.Like`:

```csharp
public static readonly FrozenSet<string> SearchFields =
    new[] { "code", "name", "description" }.ToFrozenSet();

// escape wildcard ของ SQL Server: \ % _ [   (ใช้ \ เป็น ESCAPE char)
// หมายเหตุ: ] และ ^ ไม่ต้อง escape — มันพิเศษเฉพาะภายใน [...] เท่านั้น; เมื่อ [ ถูก escape เป็น \[ แล้ว
// character-class จะไม่เกิด -> ]/^ กลายเป็น literal อัตโนมัติ (ตรงตาม T-SQL semantics)
private static string EscapeLike(string s) => s
    .Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

public static IQueryable<AdminRole> ApplySearch(this IQueryable<AdminRole> q, SearchOption? s)
{
    if (s is null || string.IsNullOrWhiteSpace(s.Query)) return q;

    var fields = (s.Fields is { Length: > 0 } req ? req : SearchFields.ToArray())
        .Where(SearchFields.Contains).ToArray();     // silent-drop field ที่ไม่ whitelist
    if (fields.Length == 0) return q;

    var pattern = $"%{EscapeLike(s.Query.Trim())}%";

    // guard boolean (fields.Contains(...)) เป็นค่าคงที่ตอน build -> EF parameterize + ตัด branch ที่ไม่ขอ
    return q.Where(r =>
        (fields.Contains("code")        && EF.Functions.Like(r.Code, pattern, "\\")) ||
        (fields.Contains("name")        && EF.Functions.Like(r.Name, pattern, "\\")) ||
        (fields.Contains("description") && r.Description != null
                                        && EF.Functions.Like(r.Description, pattern, "\\")));
}
```

- **CI-collation note:** ภายใต้ default CI collation ของ SQL Server, `LIKE` case-insensitive อยู่แล้ว
  = `ILIKE` ของ Postgres โดยไม่ต้องทำอะไรเพิ่ม.
- **multi-field OR:** ทุก field ที่ขอถูก OR รวมกันใน `Where` เดียว — เทียบ `Brackets(...)` ของต้นฉบับที่ห่อ OR
  ทั้งก้อนเพื่อกัน precedence bug. ใน EF LINQ, `Where(r => a || b || c)` เป็น group เดียวอยู่แล้ว (ไม่ปน
  `AndWhere` ภายนอก) — ปลอดภัยจากปัญหา precedence โดยธรรมชาติ.

### 6.1 Dynamic predicate composition (เมื่อ field เยอะ)

> **สถานะ: out of scope สำหรับ v1 (design D14).** `Expression.Constant(pattern)` อาจถูก EF ฝังเป็น SQL literal
> (plan-cache pollution + user term โผล่ใน log) ขัด REQ-6.3. v1 ใช้ inline OR ของ section 6 (parameterize
> แน่นอน). ถ้าจะรื้อฟื้น ต้อง capture pattern ผ่าน member-access ของ closure object ไม่ใช่ `Expression.Constant`.
> ส่วนด้านล่างเก็บไว้เป็น reference เชิงเทคนิคเท่านั้น.

ถ้า search field เยอะจนเขียน OR ยาว ๆ ไม่ไหว ให้ประกอบ predicate ด้วย `Expression.OrElse` แล้วส่งเข้า
`Where` — แปลเป็น `... OR ... OR ...` ได้จริง. **เงื่อนไขสำคัญ: ต้องใช้ `ParameterExpression` ตัวเดียว
ร่วมกันทุก sub-predicate** มิฉะนั้น EF จะ throw:

```csharp
public static IQueryable<AdminRole> ApplySearchDynamic(this IQueryable<AdminRole> q, SearchOption? s)
{
    if (s is null || string.IsNullOrWhiteSpace(s.Query)) return q;

    var fields = (s.Fields is { Length: > 0 } req ? req : SearchFields.ToArray())
        .Where(SearchFields.Contains).ToArray();
    if (fields.Length == 0) return q;

    var pattern = $"%{EscapeLike(s.Query.Trim())}%";
    var like = typeof(DbFunctionsExtensions).GetMethod(
        nameof(DbFunctionsExtensions.Like),
        [typeof(DbFunctions), typeof(string), typeof(string), typeof(string)])!;

    var p = Expression.Parameter(typeof(AdminRole), "r");   // parameter ตัวเดียว ใช้ร่วมทุก term
    Expression? body = null;
    foreach (var field in fields)
    {
        Expression col = Expression.Property(p, field);     // field มาจาก whitelist แล้วเท่านั้น
        Expression call = Expression.Call(like,
            Expression.Constant(EF.Functions), col,
            Expression.Constant(pattern), Expression.Constant("\\"));
        body = body is null ? call : Expression.OrElse(body, call);
    }

    var lambda = Expression.Lambda<Func<AdminRole, bool>>(body!, p);
    return q.Where(lambda);
}
```

> `Expression.Property(p, field)` ปลอดภัยก็ต่อเมื่อ `field` ผ่าน whitelist มาแล้วเท่านั้น (ทำ `.Where(
> SearchFields.Contains)` ก่อน) — อย่าเอา string จาก client ดิบ ๆ มาสร้าง property expression. `LinqKit
> PredicateBuilder` ทำ parameter-rebinding ให้อัตโนมัติ (seed `New(false)` สำหรับ OR) แต่นั่นคือ dep ใหม่ —
> baseline ใช้ `Expression.OrElse` + shared parameter เอง ไม่เพิ่ม dep.

> **caveat parameterization:** `Expression.Constant(pattern)` (ค่าที่มี user input) อาจถูก EF ฝังเป็น **SQL
> literal** (inline) แทน parameter ในบาง provider/เวอร์ชัน — ต่างจากการ capture ตัวแปรใน closure (section
> 4-6) ที่ EF parameterize ให้เสมอ. ผลข้างเคียงถ้าถูก inline: plan-cache pollution (search term ต่างกัน =
> SQL text ใหม่) และ user term อาจโผล่ใน SQL text ที่ถูก log (ไม่ใช่ SQL injection — EF escape single-quote
> ให้). ถ้ากังวล ให้ประกอบ `pattern` ผ่าน member-access ของ capture object แทน `Expression.Constant` หรือ
> กลับไปใช้ inline OR (section 6) ที่ใช้ตัวแปร `pattern` ใน lambda ตรง ๆ (EF parameterize ให้). สำหรับ field
> ไม่กี่ตัว baseline section 6 เพียงพอ.

### 6.2 ทำไมไม่ใช้ `.Contains(userInput)`

ต้นฉบับ TS เตือนว่า `.Contains` เสี่ยง wildcard injection — **แต่บน EF Core 10 นั่นไม่จริงแล้ว**:
`x.Name.Contains(userInput)` / `StartsWith` / `EndsWith` ที่รับ **ตัวแปร** (non-constant) EF Core จัดการ
wildcard ให้เอง — กลไกจริงขึ้นกับ provider/เวอร์ชัน (SQL Server มักแปลเป็น `CHARINDEX(@p, col) > 0` ซึ่ง
treat ค่าเป็น literal ตรง ๆ ไม่ต้อง escape; บาง path เป็น `LIKE @p ESCAPE N'\'` พร้อม escape ค่า parameter
ที่ runtime). ไม่ว่าทางไหน ผล**เหมือนกัน**: `%`/`_`/`[` ใน input ถูก treat เป็น literal — `.Contains(userInput)`
**ปลอดภัยจาก LIKE-wildcard injection** เท่ากับ `EF.Functions.Like` + `EscapeLike` ของเรา.

> **สำคัญ:** auto-escape นี้ใช้ได้เฉพาะ method `.Contains` / `.StartsWith` / `.EndsWith` เท่านั้น.
> `EF.Functions.Like(col, pattern)` **ไม่** auto-escape — ถ้าส่ง user input ดิบเข้าไปตรง ๆ wildcard จะ active.
> ต้อง `EscapeLike(input)` เอง + ใช้ overload 3-arg (`ESCAPE '\'`) เสมอ (ตาม section 6).

เหตุผลที่ **ยังเลือก `EF.Functions.Like` แบบ explicit** ไม่ใช่เรื่อง injection แต่คือ:

1. **parity กับฝั่ง filter** — `Like`/`ILike`/`Contains` ใน filter (section 4) ก็ใช้ `EF.Functions.Like`
   ตัวเดียวกัน; ใช้ path เดียวทั้งระบบอ่านง่ายกว่า.
2. **pattern reuse ข้าม field** — search ใช้ `pattern` (`%esc%`) ก้อนเดียว OR ข้ามหลาย field; `.Contains`
   ผูกกับ property ทีละตัวและสร้าง parameter แยกกัน ไม่เหมาะกับ dynamic composition (section 6.1).
3. **ESCAPE clause เห็นชัดใน code + SQL** — control ตรง ๆ ว่าใช้ escape char อะไร, review ง่าย, ไม่ฝากไว้
   กับพฤติกรรม implicit ของ translator (ซึ่งอาจเป็น `CHARINDEX` path ในบาง case).

---

## 7. Module-specific typed filter DTO

เมื่อ filter ซับซ้อนเกินกว่า generic `filters[]` (เช่นต้อง validate รูปแบบ, มี field คู่ที่สัมพันธ์กัน,
หรืออยากได้ 400 พร้อม field-level error แทน silent-drop) ให้ใช้ **typed filter DTO** เพิ่มขนานกับ generic
`filters[]`. รับเป็น JSON object แยก param ชื่อ `{module}Filters` (camelCase).

naming convention (พอร์ตจากต้นฉบับ):

| สิ่งที่ตั้งชื่อ            | รูปแบบ               | ตัวอย่าง                |
| ------------------------- | ------------------- | ---------------------- |
| Filter DTO record         | `{Module}FilterDto` | `ProductFilterDto`     |
| Query record              | `List{Module}Query` | `ListProductsQuery`    |
| Query param name (wire)   | `{module}Filters`   | `productFilters`       |

```csharp
namespace Products.Application;

using System.ComponentModel.DataAnnotations;

// typed DTO — validate ด้วย data annotation (ไม่มี FluentValidation ในโปรเจกต์)
public sealed record ProductFilterDto
{
    [Range(0, long.MaxValue)] public long? MinPriceMinorUnits { get; init; }
    [Range(0, long.MaxValue)] public long? MaxPriceMinorUnits { get; init; }
    public bool? ActiveOnly { get; init; }
}

// query record สืบทอด PagedQuery + carry typed filter (tenant data -> ITenantScoped, ดู section 9)
public sealed record ListProductsQuery : PagedQuery,
    IQuery<PagedResult<ProductListItem>>, ITenantScoped
{
    public ProductFilterDto? ProductFilters { get; init; }
}
```

parse ที่ Hosts (แทน `@Transform` + `plainToInstance` ของต้นฉบับ): deserialize JSON string เข้า DTO แล้ว
validate ด้วย `Validator.TryValidateObject`; ไม่ผ่าน -> 400 ProblemDetails.

```csharp
static ProductFilterDto? ParseProductFilters(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    ProductFilterDto dto;
    try { dto = JsonSerializer.Deserialize<ProductFilterDto>(raw, Json)!; }
    catch (JsonException ex) { throw new ArgumentException("Malformed productFilters.", ex); }  // -> 400

    var ctx = new ValidationContext(dto);
    var errors = new List<ValidationResult>();
    if (!Validator.TryValidateObject(dto, ctx, errors, validateAllProperties: true))
        throw new ArgumentException("Invalid productFilters.");   // -> 400 ProblemDetails (ArgumentException bucket)
    return dto;
}
```

apply ใน repository (typed field ไม่ต้อง whitelist runtime เพราะ compile-time ปลอดภัยอยู่แล้ว):

```csharp
if (q.ProductFilters is { } pf)
{
    if (pf.MinPriceMinorUnits is { } min) src = src.Where(p => p.PriceMinorUnits >= min);
    if (pf.MaxPriceMinorUnits is { } max) src = src.Where(p => p.PriceMinorUnits <= max);
    if (pf.ActiveOnly == true)            src = src.Where(p => p.IsActive);
}
```

> **เมื่อไรใช้ typed DTO แทน generic `filters[]`:** ใช้เมื่อ (a) ต้อง validate เข้ม + คืน error ให้ client
> (admin console/back-office ที่ผู้ใช้ควรรู้ว่าพิมพ์ผิด), หรือ (b) filter มีความสัมพันธ์เชิงธุรกิจ (เช่น
> `minPrice <= maxPrice`) ที่ generic operator แสดงไม่ได้. **ใช้ generic `filters[]`** เมื่อเป็น list ทั่วไป
> ที่อยากได้ contract เดียวกันทุกโมดูล + posture แบบ silent-drop. ทั้งสองอยู่ร่วมกันบน query เดียวได้.

---

## 8. Whitelist implementation variants

เลือกที่วาง whitelist ตามขนาด/ทีม (พอร์ต 3 variant จากต้นฉบับมาเป็นสำนวน C#):

### Variant 1 — inline `file static class` ข้าง repository (default ที่แนะนำ)

```csharp
// ต้นไฟล์ repository ของโมดูล (file-scoped -> ไม่รั่วออกนอกไฟล์)
file static class RoleQueryFields
{
    public static readonly FrozenSet<string> Sort   = new[] { "code", "name", "description" }.ToFrozenSet();
    public static readonly FrozenSet<string> Search = new[] { "code", "name", "description" }.ToFrozenSet();
    public static readonly FrozenDictionary<string, FilterOperator[]> Filter =
        new Dictionary<string, FilterOperator[]>
        {
            ["status"] = [FilterOperator.Equals, FilterOperator.In],
            ["code"]   = [FilterOperator.Equals, FilterOperator.In],
        }.ToFrozenDictionary();
}
```

ใช้เมื่อ: repository ไม่ใหญ่, โมดูลเรียบง่าย, whitelist ไม่ต้องใช้ข้ามไฟล์. **เป็น default.**

### Variant 2 — `private static readonly` ใน class ของ repository

```csharp
// ของจริง: AdminRoleRepository เป็น public sealed + ctor ปกติ, inject ProducerDbContext (DbContext เดียวใน repo)
// bound เป็น pol_admin keyed context สำหรับ control-plane tables (AdminRole/AdminRoleAssignment/...)
public sealed class AdminRoleRepository : IAdminRoleRepository
{
    private readonly ProducerDbContext _db;
    public AdminRoleRepository(ProducerDbContext db) => _db = db;

    // AdminRole ไม่มี CreatedAt -> sort whitelist ใช้ code/name (default fallback = Code)
    private static readonly FrozenSet<string> SortFields =
        new[] { "code", "name" }.ToFrozenSet();
    // ... apply methods อยู่ใน class เดียวกัน เข้าถึง SortFields ได้ตรง ๆ
}
```

ใช้เมื่อ: apply logic ผูกกับ instance ของ repository (เช่นต้องใช้ field อื่นของ class) และไม่ต้อง export.

### Variant 3 — แยกไฟล์ `{Module}QueryFields.cs` (frozen, export ได้)

```csharp
// Products.Application/ProductQueryFields.cs
namespace Products.Application;

public static class ProductQueryFields
{
    public static readonly FrozenSet<string> Sort =
        new[] { "name", "priceMinorUnits", "createdAt" }.ToFrozenSet();

    public static readonly FrozenDictionary<string, FilterOperator[]> Filter =
        new Dictionary<string, FilterOperator[]>
        {
            ["isActive"]        = [FilterOperator.Equals],
            ["priceMinorUnits"] = [FilterOperator.GreaterThanOrEqual, FilterOperator.LessThanOrEqual,
                                   FilterOperator.Between],
        }.ToFrozenDictionary();
}
```

ใช้เมื่อ: repository จะใหญ่มาก, whitelist ถูกใช้ข้ามไฟล์ (เช่น test อ้างถึง หรือ handler/validator แชร์),
หรือทีมใหญ่. `FrozenDictionary`/`FrozenSet` immutable โดยธรรมชาติ = เทียบ `Object.freeze` + export ของต้นฉบับ.

| เกณฑ์                              | Variant 1 inline | Variant 2 class-private | Variant 3 extracted |
| --------------------------------- | ---------------- | ----------------------- | ------------------- |
| repo เล็ก, dev เดียว              | เหมาะสุด          | OK                      | เกินจำเป็น          |
| apply ผูกกับ instance             | —                | เหมาะสุด                | OK                  |
| ต้อง export/แชร์ข้ามไฟล์ (test/validator) | ทำไม่ได้     | ทำไม่ได้               | เหมาะสุด            |
| repo ใหญ่มาก, ทีมใหญ่             | อึดอัด            | OK                      | เหมาะสุด            |
| navigate code ง่าย                | ง่ายสุด           | ง่าย                    | ต้องข้ามไฟล์        |

---

## 9. Security + RLS interplay

### 9.1 Security mapping (ต้นฉบับ TS -> pol-core)

| กฎ (ต้นฉบับ TS)                       | pol-core                                                          |
| ------------------------------------- | ---------------------------------------------------------------- |
| `Object.hasOwn(WL, field)`            | `WL.ContainsKey(field)` / `WL.Contains(field)` (`FrozenDictionary`/`FrozenSet`) |
| `Object.freeze` whitelist             | `FrozenDictionary` / `FrozenSet` (immutable by construction)      |
| ESCAPE clause กัน wildcard inject     | `EscapeLike(...)` + `EF.Functions.Like(_, _, "\\")` (3-arg overload) |
| parameterized query (`:param`)        | EF Core parameterize ให้อัตโนมัติ — **ห้ามต่อ string เป็น SQL / interpolate ชื่อ field** |
| silent-drop field/operator ไม่รู้จัก  | `continue` ใน loop — ไม่ throw                                    |

> **prototype-pollution** (`'constructor' in {}` เป็น `true`) เป็น hazard เฉพาะ JS. ฝั่ง C# ไม่มีปัญหานี้ —
> `FrozenDictionary.ContainsKey` / `FrozenSet.Contains` เป็น membership check ตรง ๆ. เจตนาด้าน security ที่
> พอร์ตมาคือ **deny-by-default whitelist ก่อนชื่อ column ใด ๆ จะไปถึง SQL** + parameterize ค่าเสมอ.

**ทำไม silent-drop ไม่ 400:** กัน client เดิมพังเพราะพิมพ์ field ผิดตัวเดียว. field/operator ที่ไม่อยู่
whitelist ถูกข้ามเงียบ ๆ query ที่เหลือยังทำงาน. (ต่างจาก JSON พังทั้งก้อน ซึ่ง = 400 เพราะ parse ไม่ได้เลย,
และต่างจาก typed filter DTO ที่ validate ไม่ผ่าน ซึ่ง = 400 โดยเจตนา — เป็น strict surface ของ section 7.)
ข้อยกเว้น: strict admin-tool ที่อยากให้ผู้ใช้รู้ว่าพิมพ์ผิด ค่อยเลือก throw ผ่าน typed DTO — document ให้ชัด.

### 9.2 RLS non-widening (สำคัญที่สุด และเป็นจุดที่ต่างจากต้นฉบับ)

pol-core ไม่มี **EF Core global query filter** (`HasQueryFilter` = 0 hit ทั้ง src). tenant isolation เป็น
**SQL-native floor 3 ชั้น** และ SFS ประกอบ **ทับบน** floor นั้นเสมอ:

1. **SQL layer** — migration สร้าง `producer.fn_tenant_predicate(@TenantId)` (inline TVF, `SCHEMABINDING`)
   ที่คืน row เฉพาะเมื่อ `@TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)` **หรือ**
   `IS_ROLEMEMBER(N'pol_rls_bypass') = 1`, แล้ว `CREATE SECURITY POLICY producer.TenantIsolationPolicy`
   ADD FILTER + BLOCK PREDICATE บนทุก tenant table (Products, Carts, Orders, ...).
2. **Connection layer** — `SessionContextConnectionInterceptor` ตั้ง `sp_set_session_context @key=N'TenantId'
   @read_only=1` ตอน physical connection open ทุกครั้ง (SESSION_CONTEXT เป็น per-pooled-connection).
3. **App layer** — `TenantGuardBehavior<TMessage,TResponse>` (Mediator pipeline, Scoped): ถ้า `message is
   ITenantScoped && !_tenant.HasTenant` -> throw `TenantBindingException` (-> opaque 500) ก่อนถึง handler.

filter/search/sort เป็นการ **แคบ** ผลลัพธ์ลงเท่านั้น (`.Where` เพิ่มบน `IQueryable` ที่ RLS ครอบแล้ว) —
**ไม่มีทางขยาย tenant scope**. เงื่อนไขบังคับ:

- entity ที่เป็น **tenant data** (มี `TenantId`: Product, Cart, Order, PaymentSession, ...) query record ที่
  สืบทอด `PagedQuery` ต้อง mark `ITenantScoped` ด้วย เพื่อให้ `TenantGuardBehavior` ปฏิเสธเมื่อไม่มี tenant:

  ```csharp
  public sealed record ListProductsQuery : PagedQuery,
      IQuery<PagedResult<ProductListItem>>, ITenantScoped;   // tenant data -> ต้อง ITenantScoped
  ```

  และ tenant repository เติม explicit `.Where(p => p.TenantId == tenantId)` เป็น defence-in-depth (RLS ยัง
  gate row อยู่ดี) — belt-and-suspenders ตาม pattern `ProductRepository.ListByTenantAsync`.

- entity ที่เป็น **control-plane** (ไม่มี `TenantId`, ผูก `pol_admin` RLS-bypass: AdminRole,
  AdminRoleAssignment, AdminPermission, ProducerRole) **ห้าม** mark `ITenantScoped` — มันไม่ใช่ tenant data.
- whitelist **ห้าม** เปิด field ที่ข้าม tenant (เช่น `TenantId`, FK ไปตารางอื่น) เป็นอันขาด.
- อย่าเขียน raw SQL ใน apply-step — จะ bypass ทั้ง RLS floor และ parameterization (ผิด SECURITY_RULES).

---

## 10. Common mistakes / anti-patterns

พอร์ตข้อผิดพลาดที่เจอซ้ำจากต้นฉบับมาเป็นสำนวน C# (WRONG/CORRECT):

```csharp
// A1 — ไม่มี whitelist / เอา field จาก client ไปสร้าง SQL/expression ตรง ๆ (SQL injection surface)
// WRONG
q = q.OrderBy(EF.Property<object>(r, sort.Field));                 // field ดิบจาก client
// CORRECT — whitelist ก่อนเสมอ แล้ว map เป็น property จริง
if (!SortFields.Contains(sort.Field)) continue;
q = sort.Field switch { "name" => q.OrderBy(r => r.Name), _ => q };
```

```csharp
// A2 — ลืม ESCAPE clause (wildcard ของ user match ทุก row -> data leak + slow)
// WRONG
q.Where(r => EF.Functions.Like(r.Name, $"%{input}%"));            // 2-arg, ไม่ escape
// CORRECT
q.Where(r => EF.Functions.Like(r.Name, $"%{EscapeLike(input)}%", "\\"));   // escape + ESCAPE char
```

```csharp
// A3 — ไม่มี default-sort fallback (paging ไม่ deterministic)
// WRONG
return o!;                                                         // null ได้ถ้าไม่มี field valid
// CORRECT — fallback บังคับ. AdminRole ไม่มี CreatedAt -> ใช้ Code (entity ที่มี CreatedAt เช่น Product ใช้ CreatedAt DESC)
return o ?? q.OrderByDescending(r => r.Code);
```

```csharp
// A4 — throw 400 สำหรับ field/operator ที่ไม่รู้จัก (พัง client เดิม)
// WRONG
if (!SortFields.Contains(s.Field)) throw new ArgumentException($"bad sort: {s.Field}");
// CORRECT — silent-drop
if (!SortFields.Contains(s.Field)) continue;
```

```csharp
// A5 — OR ไม่ group -> precedence bug ปนกับ AND ของ filter
// WRONG — || หลุดออกไปรวมกับ Where ก่อนหน้าผิดชั้น
q = q.Where(r => r.IsActive);
q = q.Where(r => EF.Functions.Like(r.Name, p, "\\")).Where(r => EF.Functions.Like(r.Code, p, "\\"));
// CORRECT — รวม OR ใน Where เดียว (group เดียว)
q = q.Where(r => EF.Functions.Like(r.Name, p, "\\") || EF.Functions.Like(r.Code, p, "\\"));
```

```csharp
// A6 — ใช้ Value กับ operator ที่ต้องการ Values[] (In/NotIn/Between)
// WRONG
case FilterOperator.In:
    q = q.Where(r => r.Code == f.Value!.Value.GetString());        // scalar ไม่ใช่ array
// CORRECT
case FilterOperator.In when f.Values is { Length: > 0 }:
    q = q.Where(r => f.Values!.Select(v => v.GetString()).Contains(r.Code));
```

```csharp
// A7 — ลืมจำลอง NULLS LAST (row ที่ NULL โผล่หัว list ตอน DESC หรือหัว list ตอน ASC ไม่ตามคาด)
// WRONG
q = q.OrderByDescending(r => r.Description);                       // NULL ไปกองผิดที่
// CORRECT — NULLS LAST: เรียง (Description == null) ก่อน (เฉพาะคอลัมน์ nullable)
q = q.OrderBy(r => r.Description == null).ThenByDescending(r => r.Description);
```

---

## 11. Testing guidance

pol-core มี test 2 tier (xUnit, ไม่มี mocking library, ไม่มี in-memory EF provider):

**Tier 1 — unit/handler test ด้วย hand-written fakes** (`Fakes.cs` implement repository interface).
ใช้พิสูจน์ contract-level logic ที่ไม่ต้องพึ่ง SQL จริง:

- **silent-drop:** ส่ง `FilterOption` ที่ field/operator นอก whitelist -> assert ว่า fake ได้รับ query ที่
  ไม่มี predicate นั้น (หรือผลลัพธ์เท่ากับไม่ส่ง filter). ส่ง sort-field นอก whitelist -> assert ว่า order
  ตกไปที่ default fallback.
- **clamp / paging boundary:** `limit=0` -> clamp เป็น 1; `limit=1000` -> clamp เป็น 100; `page=0`/ติดลบ
  -> clamp เป็น 1 (กัน OFFSET ติดลบ); `page=1` -> `Skip(0)`. test `SfsQueryParser.Parse` โดยตรง.
- **JSON พัง -> 400:** ส่ง `filters=` ที่ไม่ใช่ JSON -> assert `ArgumentException` (ซึ่ง handler map เป็น 400).
- **PagedResult metadata:** `TotalPages` = `ceil(Total/Limit)`; `Total=5, Limit=25` -> `TotalPages=1`.

```csharp
[Fact]
public void Parse_clamps_limit_to_100()
{
    var q = new QueryCollection(new() { ["limit"] = "1000" });
    var (_, limit, _, _, _) = SfsQueryParser.Parse(q);
    Assert.Equal(100, limit);
}

[Fact]
public void Parse_clamps_nonpositive_page_to_1()
{
    var q = new QueryCollection(new() { ["page"] = "0" });
    var (page, _, _, _, _) = SfsQueryParser.Parse(q);
    Assert.Equal(1, page);
}
```

**Tier 2 — Integration.Tests บน SQL Server จริง** (`IntegrationDb.cs`, `Pooling=False`, creds จาก
`.env.integration`, tenant ผูกด้วย `sp_set_session_context @key='TenantId' @read_only=1`). ใช้พิสูจน์สิ่งที่
เป็น SQL-Server-specific และ RLS composition — สิ่งที่ fake พิสูจน์ไม่ได้:

- **NULLS-last order จริง:** insert แถวที่ `Description` เป็น NULL ปน -> sort ascending/descending -> assert
  ว่าแถว NULL ไปอยู่ท้ายเสมอ (พิสูจน์ `CASE WHEN ... IS NULL` แปลถูก).
- **escape จริง:** insert ชื่อที่มี `%`/`_`/`[` -> search ด้วย literal เดียวกัน -> assert match เฉพาะแถวที่
  ตรงจริง (ไม่ match ทุกแถวจาก wildcard ที่ไม่ escape).
- **RLS composition (สำคัญ):** bind tenant A, query list ที่มี filter -> assert เห็นเฉพาะ row ของ tenant A;
  SFS filter **ไม่** ทำให้เห็น row ของ tenant B. พิสูจน์ว่า `.Where` ของ SFS ประกอบทับ RLS predicate ไม่ทะลุ.

```csharp
[Fact]
public async Task Search_filter_does_not_widen_tenant_scope()
{
    await using var conn = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, tenant: TenantA);
    // ... seed products ทั้ง TenantA และ TenantB (ผ่าน pol_admin), แล้ว query ด้วย tenant A + filter
    // assert: ผลลัพธ์ทุกแถว TenantId == TenantA (RLS + explicit .Where ยังกันอยู่แม้ใส่ filter)
}
```

> `EF.Functions.Like` **ไม่มี in-memory implementation** — ถ้า query switch ไป client-eval จะ throw. เป็น
> อีกเหตุผลที่ต้อง test path นี้บน SQL จริง (Tier 2) ไม่ใช่ fake.

---

## 12. ตัวอย่าง end-to-end (C#)

### 12.1 Admin roles — control-plane (ไม่ `ITenantScoped`)

`GET /api/v1/admins/roles` วันนี้ **มีอยู่จริงแต่ non-paginated** (คืน full set, ไม่มี `OrderBy`). admin role เป็น
**control-plane** (ไม่มี `TenantId`) จึง **ไม่** mark `ITenantScoped`.

**ก่อน (วันนี้)** — `Admin.Application/RoleQueries.cs`:

```csharp
public sealed record ListRolesQuery : IQuery<IReadOnlyList<AdminRoleListItem>>;

public sealed class ListRolesHandler(IAdminRoleRepository roles)
    : IQueryHandler<ListRolesQuery, IReadOnlyList<AdminRoleListItem>>
{
    public async ValueTask<IReadOnlyList<AdminRoleListItem>> Handle(ListRolesQuery query, CancellationToken ct) =>
        await roles.ListAsync(ct);   // คืน full set, ไม่มี OrderBy
}
```

**หลัง (target convention)** — query สืบทอด `PagedQuery`, คืน `PagedResult<T>`:

```csharp
public sealed record ListRolesQuery : PagedQuery, IQuery<PagedResult<AdminRoleListItem>>;
// control-plane -> ไม่ ITenantScoped

public sealed class ListRolesHandler(IAdminRoleRepository roles)
    : IQueryHandler<ListRolesQuery, PagedResult<AdminRoleListItem>>
{
    public async ValueTask<PagedResult<AdminRoleListItem>> Handle(ListRolesQuery query, CancellationToken ct) =>
        await roles.ListAsync(query, ct);
}
```

**Endpoint** — `Hosts/Api/Program.cs`:

```csharp
admin.MapGet("/roles", async (HttpContext http, IMediator mediator, CancellationToken ct) =>
    {
        var p = SfsQueryParser.Parse(http.Request.Query);
        var result = await mediator.Send(new ListRolesQuery
        {
            Page = p.Page, Limit = p.Limit, Filters = p.Filters, Sort = p.Sort, Search = p.Search,
        }, ct);
        // map item -> wire DTO: สร้าง PagedResult ใหม่ (with{} เปลี่ยน generic type ไม่ได้)
        return Results.Ok(new PagedResult<RoleResponse>(
            [.. result.Items.Select(RoleToWire)], result.Page, result.Limit, result.Total));
    })
    .RequireAuthorization("admin")
    .WithTags("Admin Roles")
    .WithName("ListRoles")
    .WithSummary("List roles")
    .Produces<PagedResult<RoleResponse>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status401Unauthorized);
```

**Repository** — ประกอบ SFS ต่อกัน แล้ว count + page (ลำดับตาม section 3):

```csharp
public async Task<PagedResult<AdminRoleListItem>> ListAsync(ListRolesQuery q, CancellationToken ct)
{
    IQueryable<AdminRole> src = _db.Set<AdminRole>().AsNoTracking()
        .ApplySearch(q.Search)       // section 6
        .ApplyFilters(q.Filters);    // section 4

    long total = await src.LongCountAsync(ct);   // นับหลัง filter/search ก่อน paging

    // materialize หน้านี้เป็น ENTITY ก่อน — ToListItem() และ role.PermissionKeys เป็น computed member
    // ที่ EF แปลใน server-side .Select ไม่ได้ (hazard เดียวกับ Product.Price). offset คำนวณเป็น long กัน overflow.
    var roles = await src
        .ApplySort(q.Sort)           // section 5 (มี default fallback บังคับ)
        .Skip((int)Math.Min((long)(q.Page - 1) * q.Limit, int.MaxValue))
        .Take(q.Limit)
        .Include(r => r.Permissions)
        .ToListAsync(ct);

    // คง UserCount ไว้ (ห้ามหาย = REQ-12.1 regression): grouped count เฉพาะ role id ของหน้านี้
    var ids = roles.Select(r => r.Id).ToList();
    var counts = await _db.Set<AdminRoleAssignment>().AsNoTracking()
        .Where(a => ids.Contains(a.RoleId))
        .GroupBy(a => a.RoleId)
        .Select(g => new { RoleId = g.Key, Count = g.Count() })
        .ToDictionaryAsync(x => x.RoleId, x => x.Count, ct);

    var items = roles.Select(r => ToListItem(r, counts.GetValueOrDefault(r.Id))).ToList();  // map client-side
    return new PagedResult<AdminRoleListItem>(items, q.Page, q.Limit, total);
}
```

**Whitelist** (variant 1, ต้นไฟล์ repository):

```csharp
file static class RoleQueryFields
{
    public static readonly FrozenSet<string> Sort   = new[] { "code", "name" }.ToFrozenSet();
    public static readonly FrozenSet<string> Search = new[] { "code", "name", "description" }.ToFrozenSet();
    public static readonly FrozenDictionary<string, FilterOperator[]> Filter =
        new Dictionary<string, FilterOperator[]>
        {
            ["status"] = [FilterOperator.Equals, FilterOperator.In],
            ["code"]   = [FilterOperator.Equals, FilterOperator.In],
        }.ToFrozenDictionary();
    // NB: AdminRole ไม่มี CreatedAt -> default sort ใช้ Code (ดูหมายเหตุ)
}
```

> `AdminRole` **ไม่มี field `CreatedAt`** (ต่างจาก `Product`). default-sort fallback ของ role จึงใช้
> `OrderByDescending(r => r.Code)` ไม่ใช่ `CreatedAt` — อย่าประดิษฐ์ `CreatedAt` ขึ้นมาบน AdminRole.

**Response ที่ client ได้** (ตรงตาม `RoleResponse` — status เป็น lowercase เสมอ ตาม B2):

```json
{
  "items": [
    {
      "code": "super_admin",
      "name": "Super Admin",
      "description": null,
      "color": null,
      "status": "active",
      "permissions": [],
      "userCount": 1
    }
  ],
  "page": 1,
  "limit": 25,
  "total": 5,
  "totalPages": 1
}
```

### 12.2 Products — tenant-scoped (`ITenantScoped`, RLS composes ทับ)

product เป็น **tenant data** (`Product : AggregateRoot<Guid>` มี `TenantId`, `Name`, `PriceMinorUnits`,
`PriceCurrency`, `IsActive`, `CreatedAt`). query ต้อง mark `ITenantScoped` และ repository เติม explicit
`.Where(TenantId)` บน RLS floor.

> วันนี้ `GetProductsQuery` + `ListByTenantAsync` มีอยู่จริงแต่ **ยังไม่ถูก wire เข้า HTTP endpoint** —
> endpoint ของ product มีแค่ `POST /products`. ตัวอย่างนี้คือ target ของ `GET /products`.
> หมายเหตุ: มี read model `Products.Application.ProductView(ProductId, ..., Money Price, ...)` อยู่แล้ว
> (ใช้โดย `GetProductsHandler`). ที่นี่ใช้ **`ProductListItem` ตัวใหม่** (scalar) แทน — เป็นการเพิ่ม/เปลี่ยนที่
> ประกาศไว้ชัด ไม่ใช่ redefine `ProductView` เดิมเงียบ ๆ (จะพัง `GetProductsHandler`).

**Query** — สืบทอด `PagedQuery` + `ITenantScoped` + carry typed filter (section 7):

```csharp
public sealed record ListProductsQuery : PagedQuery,
    IQuery<PagedResult<ProductListItem>>, ITenantScoped   // tenant data -> ITenantScoped
{
    public required Guid TenantId { get; init; }
    public ProductFilterDto? ProductFilters { get; init; }
}

public sealed class ListProductsHandler(IProductRepository products)
    : IQueryHandler<ListProductsQuery, PagedResult<ProductListItem>>
{
    public async ValueTask<PagedResult<ProductListItem>> Handle(ListProductsQuery query, CancellationToken ct) =>
        await products.ListAsync(query, ct);
}

// ProductListItem carry สอง scalar (PriceMinorUnits + PriceCurrency) ไม่ใช่ Money — ดูหมายเหตุใต้ repository
public sealed record ProductListItem(
    Guid Id, Guid TenantId, string Name, long PriceMinorUnits, string PriceCurrency, bool IsActive, DateTime CreatedAt);
```

**Endpoint** — tenant มาจาก `ITenantContext` (principal) ไม่ใช่จาก client:

```csharp
app.MapGet("/products", async (HttpContext http, ITenantContext tenant, IMediator mediator, CancellationToken ct) =>
    {
        var p = SfsQueryParser.Parse(http.Request.Query);
        var result = await mediator.Send(new ListProductsQuery
        {
            TenantId = tenant.TenantId,      // จาก principal เท่านั้น
            Page = p.Page, Limit = p.Limit, Filters = p.Filters, Sort = p.Sort, Search = p.Search,
            ProductFilters = ParseProductFilters(http.Request.Query["productFilters"]),
        }, ct);
        return Results.Ok(result);
    })
    .RequireAuthorization("tenant")
    .WithTags("Products")
    .WithName("ListProducts")
    .Produces<PagedResult<ProductListItem>>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest);
```

**Repository** — RLS floor + explicit tenant guard + SFS ประกอบทับ:

```csharp
public async Task<PagedResult<ProductListItem>> ListAsync(ListProductsQuery q, CancellationToken ct)
{
    IQueryable<Product> src = _db.Set<Product>().AsNoTracking()
        .Where(p => p.TenantId == q.TenantId)   // defence-in-depth บน RLS floor
        .ApplySearch(q.Search)                  // section 6
        .ApplyFilters(q.Filters);               // section 4

    if (q.ProductFilters is { } pf)             // typed filter (section 7)
    {
        if (pf.MinPriceMinorUnits is { } min) src = src.Where(p => p.PriceMinorUnits >= min);
        if (pf.MaxPriceMinorUnits is { } max) src = src.Where(p => p.PriceMinorUnits <= max);
        if (pf.ActiveOnly == true)            src = src.Where(p => p.IsActive);
    }

    long total = await src.LongCountAsync(ct);

    var items = await src
        .ApplySort(q.Sort)                      // default fallback = CreatedAt DESC
        .Skip((q.Page - 1) * q.Limit)
        .Take(q.Limit)
        // project scalar columns เท่านั้น — ห้ามอ้าง p.Price ใน server-side projection (ดูหมายเหตุ)
        .Select(p => new ProductListItem(
            p.Id, p.TenantId, p.Name, p.PriceMinorUnits, p.PriceCurrency, p.IsActive, p.CreatedAt))
        .ToListAsync(ct);

    return new PagedResult<ProductListItem>(items, q.Page, q.Limit, total);
}
```

> **ห้าม project `p.Price` ใน `Select` ของ IQueryable:** `Product.Price` เป็น **unmapped computed property**
> (`public Money Price => Money.Of(PriceMinorUnits, PriceCurrency);`) และ `Money.Of` เป็น validating static
> factory — EF Core 10 แปลใน server-side projection ไม่ได้ (`could not be translated` ตอน execute). ให้
> project สอง scalar column (`PriceMinorUnits` + `PriceCurrency`) แล้วถ้าต้องการ `Money` ค่อย reconstitute
> `Money.Of(view.PriceMinorUnits, view.PriceCurrency)` ฝั่ง client หลัง `ToListAsync`.

**Whitelist** (`Product` มี `CreatedAt` -> default fallback = `CreatedAt DESC`):

```csharp
file static class ProductQueryFields
{
    public static readonly FrozenSet<string> Sort   = new[] { "name", "priceMinorUnits", "createdAt" }.ToFrozenSet();
    public static readonly FrozenSet<string> Search = new[] { "name" }.ToFrozenSet();
    public static readonly FrozenDictionary<string, FilterOperator[]> Filter =
        new Dictionary<string, FilterOperator[]>
        {
            ["isActive"]        = [FilterOperator.Equals],
            ["priceMinorUnits"] = [FilterOperator.GreaterThanOrEqual, FilterOperator.LessThanOrEqual,
                                   FilterOperator.Between],
            ["createdAt"]       = [FilterOperator.GreaterThanOrEqual, FilterOperator.LessThanOrEqual],
        }.ToFrozenDictionary();
    // NB: ไม่มี "tenantId" ใน whitelist ใด ๆ — SFS ห้ามเปิด field ข้าม tenant (section 9.2)
}
```

**RLS composition:** ถ้า request ไม่มี tenant context, `TenantGuardBehavior` เห็น `ListProductsQuery is
ITenantScoped && !HasTenant` -> throw `TenantBindingException` -> opaque 500 (ไม่ยืนยันสถานะ tenant).
ถ้ามี tenant context, SQL ที่ execute จะเป็น `SELECT ... FROM producer.Products WHERE [TenantId] = @tenant
AND <filters> AND <search> ORDER BY <sort> OFFSET/FETCH` — ประกอบทับ RLS FILTER PREDICATE ที่ SQL Server
บังคับด้วย SESSION_CONTEXT อีกชั้น. SFS filter/search/sort ทั้งหมด **แคบผลลง** ไม่มีทางเห็น row ข้าม tenant.

---

## 13. As-built notes (implementation deviations)

SFS ถูก implement จริงแล้ว (spec `.ai/specs/search-filter-sort/`). โค้ด production เป็นไปตามคู่มือนี้
ยกเว้นจุดที่ปรับให้เข้ากับ repo จริง — บันทึกไว้ให้ผู้อ่านคู่มือทราบว่า symbol จริงอยู่ที่ไหน:

- **Contract types** อยู่ที่ `BuildingBlocks.Application` (one-type-per-file: `FilterOperator`, `SortDirection`,
  `FilterOption`, `SortOption`, `SearchOption`, `PagedQuery`, `PagedResult<T>`) ตาม §2.3.
- **Parser namespace = `Api`** (ไม่ใช่ `Hosts.Api` ตาม §2.5) — `src/Hosts/Api/SfsQueryParser.cs` (`internal static`,
  ทดสอบผ่าน `extern alias ApiHost` + `InternalsVisibleTo`).
- **`EscapeLike` = shared `BuildingBlocks.Application.SfsLike.Escape`** (single source ของ security escape ใช้ทั้ง
  Admin + Products) — §6 แสดงเป็น per-module private แต่ของจริงรวมศูนย์.
- **`SearchOption` ชนกับ `System.IO.SearchOption`** ใต้ `ImplicitUsings` → ทุกไฟล์ที่ใช้ต้องมี
  `using SearchOption = BuildingBlocks.Application.SearchOption;`.
- **OpenAPI SFS params (REQ-13)** ประกาศผ่าน built-in `AddOperationTransformer` + metadata marker
  `SfsQueryParamsMarker` (`src/Hosts/Api/SfsOpenApi.cs`) — **ไม่ใช่ `.WithOpenApi(...)`** (§12.1/§12.2/D13):
  โปรเจกต์ใช้ .NET 10 built-in OpenAPI (document/operation transformers) ไม่ใช่ Swashbuckle.
- **Apply pipeline ต่อโมดูล**: `AdminRoleSfs` (`Admin.Infrastructure`) + `ProductSfs` (`Products.Infrastructure`) —
  whitelist + `ApplyFilters`/`ApplySort`/`ApplySearch` เป็น `public static` co-located ข้าง repository (แทน
  `file static RoleQueryFields` ใน §8/§12.1). repo/port `ListAsync` ของ Admin รับ `PagedQuery` base.
- **`ProductFilterDto.Parse`** อยู่ที่ `Products.Application` (pure `System.Text.Json` + DataAnnotations) ไม่ใช่ Hosts
  `ParseProductFilters` (§7) — testable + endpoint บาง.
- **Coverage 14 operator แบ่งสองตัวอย่าง**: AdminRole (คอลัมน์ string/enum) = eq, ne, in, not_in, like, ilike,
  contains, is_null, is_not_null; Products (คอลัมน์ numeric/date) = eq + gt, gte, lt, lte, between. Product whitelist
  ขยายจาก §12.2 (เดิม gte/lte/between) ให้รวม gt/lt ด้วย เพื่อครบ 14 ตัวข้ามสองตัวอย่าง (+ reference §4.1).
- **Whitelist-drop logging (REQ-8.6)** ผ่าน optional `ILogger?` บน `ApplyFilters`/`ApplySort`; repository ส่ง
  `ILogger<T>` (wire ผ่าน DI).
- **Relational test** ใช้ in-memory SQLite (EF relational provider ที่ repo มีอยู่แล้ว) สำหรับ NULLS-last, LIKE-escape
  (`%`/`_`), tenant-narrowing, paging. **SQL-native RLS floor** + wildcard `[` (SQL-Server-only) ยังคุมด้วย
  Integration `RlsIsolationTests` + assertion ที่ output ของ `SfsLike.Escape` ตามลำดับ.

ทุก snippet + โค้ดจริง warning-clean ภายใต้ `-warnaserror` + `Nullable enable`, ไม่พึ่ง dependency นอกกล่อง
(`System.Text.Json` + EF Core LINQ เท่านั้น — REQ-11).
