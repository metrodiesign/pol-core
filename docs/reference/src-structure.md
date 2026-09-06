# Source Structure

> As-built 2026-08-13.

```text
src/
  BuildingBlocks/
    BuildingBlocks.Application/
    BuildingBlocks.Infrastructure/   migration owner, vault/outbox/security base
    BuildingBlocks.Web/
  Contracts/                         versioned cross-module events
  Hosts/Api/                         composition, BFF, routes, Order coordinator
  Modules/
    Admins/ Iam/ Governance/ Notifications/ Reporting/
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

Commerce flow: `Products -> Carts -> Orders -> Payments`. Admin support is split into `Governance`,
`Notifications` and `Reporting`; it reads/writes through existing module owners and does not add a Transaction
ledger. No Checkouts project. Cross-module events are `PaymentPaid`, `PaymentFailed`, `PaymentExpired` plus
notification/governance outbox messages.

## Persistence

`PolDbContext` owns migrations only. Runtime contexts are ControlPlane, MerchantUsers and MerchantRuntime. Fresh migration
folder contains the initial schema, one-based enum storage, Merchant/Admin API identity, governance, resource-version,
tenant/PSP/routing, commerce lifecycle and delivery/inbound-webhook migrations plus snapshot. EF configuration remains
colocated with module Infrastructure or runtime persistence adapter according to ownership.

## API

Routes use `/api/v1/{area}`. Infrastructure routes are `/health/*`, `/openapi/*`, `/scalar`. Merchant-user and Admin BFF,
Admin control-plane, governance, delivery and reporting routes run in the same `Api` host. Schemes/cookies/CORS are
separate. Mutations require corresponding CSRF policy. OpenAPI contract tests boot real host.

## Tests

- unit/domain/application: module test projects, including `Governance.Tests`
- boundary/model/security: Architecture.Tests
- HTTP/OpenAPI/transaction composition: Hosts.Tests
- SQL catalog/grants/concurrency/JSON: Integration.Tests on SQL Server 2025

Frontend repositories are outside this tree and not modified by backend specs.
