# Search / Filter / Sort (SFS) Reference

> As-built 2026-08-07. SFS เป็น shared query contract สำหรับ endpoint ที่ประกาศใช้เท่านั้น; Products และ
> master-data CRUD ใช้ typed surface ของตนเอง.

## Generic contract

Parser อยู่ที่ `src/Hosts/Api/SfsQueryParser.cs` และแปลง query string เป็น shared value types ใน
`BuildingBlocks.Application`.

| Parameter | รูปแบบ | กฎ |
|---|---|---|
| `page` | integer | default `1`, clamp เป็นอย่างน้อย 1 และไม่เกิน offset ceiling |
| `limit` | integer | default `25`, clamp ช่วง `1..25` |
| `filters` | JSON array ของ `FilterOption` | สูงสุด 50 clauses; values สูงสุด 200 ต่อ clause |
| `sort` | JSON array ของ `SortOption` | สูงสุด 10 keys |
| `search` | JSON object `SearchOption` | endpoint เป็นผู้กำหนด field whitelist |

ตัวอย่าง:

```text
GET /api/v1/admins?page=1&limit=25&filters=[{"field":"status","operator":"eq","value":"active"}]&sort=[{"field":"createdAt","order":"DESC"}]&search={"query":"alice","fields":["email"]}
```

`filters`, `sort` และ `search` ต้องเป็น JSON ที่ valid. malformed JSON หรือเกิน cap ได้ `400` ผ่าน shared
`ProblemDetailsExceptionHandler`; `page`/`limit` ไม่ valid จะใช้ default/clamp ตาม parser.

## Filter shape

```json
{
  "field": "status",
  "operator": "eq",
  "value": "active"
}
```

Set operators ใช้ `values`:

```json
{
  "field": "status",
  "operator": "in",
  "values": ["active", "suspended"]
}
```

Current operator tokens:

`eq`, `ne`, `gt`, `gte`, `lt`, `lte`, `like`, `ilike`, `in`, `not_in`, `is_null`, `is_not_null`, `between`,
`contains`.

ทุก module implementation ใช้ deny-by-default whitelist. Unknown field/operator ต้องไม่ถูกแปลงเป็น SQL แบบ
dynamic. ค่า filter ถูก parse เป็น type ของ field และ query ใช้ parameterized EF/LINQ.

## Current endpoint surfaces

### Admin directory

`GET /api/v1/admins` ใช้ generic SFS:

- filters: `email`, `tier`, `status`
- sort: `email`, `createdAt`
- search: `email`
- authorization: policy `admin` + permission `user.view`

### Admin roles

`GET /api/v1/admins/roles` ใช้ generic SFS. Role implementation รองรับ whitelist:

- filters: `status`, `code`, `name`, `description`
- operators ต่างกันตาม field; ตรวจที่ `src/Persistence/Persistence.ControlPlane/Iam/RoleSfs.cs`
- sort/search ใช้ whitelist ใน implementation เดียวกัน

### Merchant order list

`GET /api/v1/orders` parse SFS แต่ใช้เฉพาะ `filters` field `orderNo` + operator `eq`. `page`, `limit`, `sort`
และ `search` ไม่ถูกนำไปใช้กับ query นี้ใน current contract; field/operator อื่นถูกทิ้งตาม contract ที่ host
ประกาศไว้.

ตัวอย่าง:

```text
GET /api/v1/orders?filters=[{"field":"orderNo","operator":"eq","value":"ORD6900000001"}]
```

### Master data

`GET /api/v1/positions`, `/offices`, `/levels`, `/divisions` ใช้ query parameter `q` สำหรับ search ชื่อหรือ code
และใช้ `page`/`limit` ของ route. ไม่ใช่ generic `filters`/`sort` surface.

### Products

`GET /api/v1/products` ใช้ `page`, `limit` และ typed `productFilters` ตาม Products contract. ไม่ใช้ generic
`filters`, `sort`, `search`.

`productFilters` ไม่มี local entity filter; ส่งต่อไปยัง upstream stored procedure หลัง validation. รายละเอียดอยู่
ที่ [`products.md`](products.md).

## Security and query cost

- whitelist field/operator ก่อนสร้าง predicate
- ห้ามเอา field หรือ SQL fragment จาก client ต่อ string เป็น SQL
- escape `%`, `_` และ escape character เมื่อใช้ `LIKE`; helper อยู่ `BuildingBlocks.Application/SfsLike.cs`
- จำกัดจำนวน filters, values และ sort keys ตาม parser
- ทุก list route จำกัด `limit` ไม่เกิน 25
- error response ไม่คืน SQL, connection string, merchant อื่น หรือ internal stack trace

## Current implementation map

| Concern | Path |
|---|---|
| Query parser | `src/Hosts/Api/SfsQueryParser.cs` |
| Filter value | `src/BuildingBlocks/BuildingBlocks.Application/FilterOption.cs` |
| Filter operators | `src/BuildingBlocks/BuildingBlocks.Application/FilterOperator.cs` |
| Sort value/direction | `src/BuildingBlocks/BuildingBlocks.Application/SortOption.cs`, `SortDirection.cs` |
| Search value | `src/BuildingBlocks/BuildingBlocks.Application/SearchOption.cs` |
| Admin whitelist | `src/Persistence/Persistence.ControlPlane/Admins/UserSfs.cs` |
| Role whitelist | `src/Persistence/Persistence.ControlPlane/Iam/RoleSfs.cs` |
| Products typed filters | `src/Modules/Products/Products.Application/ListProducts.cs` |
| API route composition | `src/Hosts/Api/Program.cs` |

## Explicitly not current

- ไม่มี `shop.Products`, `ProductRepository` หรือ `ProductSfs.cs`.
- ไม่มี generic SFS endpoint `/api/v1/reports/policies` หรือ `/api/v1/admins/reports/policies`.
- Generic examples in older design/spec documents are not current API promises unless route code above declares them.
