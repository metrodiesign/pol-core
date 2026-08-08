# Source Structure

> As-built 2026-08-07.

```text
src/
  BuildingBlocks/
    BuildingBlocks.Application/
    BuildingBlocks.Infrastructure/   migration owner, vault/outbox/security base
    BuildingBlocks.Web/
  Contracts/                         versioned cross-module events
  Hosts/Api/                         composition, BFF, routes, Order coordinator
  Modules/
    Admins/ Iam/ Positions/ Offices/ Levels/ Divisions/
    Merchants/ Products/ Carts/ Orders/ Payments/
  Persistence/
    Persistence.ControlPlane/
    Persistence.MerchantUsers/
    Persistence.MerchantRuntime/
    Persistence.Provisioning/
  SharedKernel/
tests/
  * module tests
  Architecture.Tests/
  Hosts.Tests/
  Integration.Tests/
```

## Dependency direction

Domain has no infrastructure dependency. Application depends on Domain + published ports/contracts. Infrastructure
implements owner ports. Host composes modules and owns narrow cross-module coordinator. Architecture tests enforce project
references, raw/bypass allowlists, transaction ownership and retired-surface bans.

Commerce flow: `Products -> Carts -> Orders -> Payments`. No Checkouts project. Cross-module payment events are
`PaymentPaid`, `PaymentFailed`, `PaymentExpired` with closed outbox registries.

## Persistence

`PolDbContext` owns migrations only. Runtime contexts are ControlPlane, MerchantUsers and MerchantRuntime. Fresh migration
folder contains exactly InitialSchema, SecurityObjects, SeedData plus snapshot. EF configuration remains colocated with
module Infrastructure or runtime persistence adapter according to ownership.

## API

Routes use `/api/v1/{area}`. Infrastructure routes are `/health/*`, `/openapi/*`, `/scalar`. Merchant-user and Admin BFF
schemes/cookies/CORS are separate. Mutations require corresponding CSRF policy. OpenAPI contract tests boot real host.

## Tests

- unit/domain/application: module test projects
- boundary/model/security: Architecture.Tests
- HTTP/OpenAPI/transaction composition: Hosts.Tests
- SQL catalog/grants/concurrency/JSON: Integration.Tests on SQL Server 2025

Frontend repositories are outside this tree and not modified by backend specs.
