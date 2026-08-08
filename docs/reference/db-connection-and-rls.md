# Database Connections and Isolation Floor

> As-built 2026-08-07. SQL RLS was retired. Current floor is app-layer query filter + sealed write guard.

## Connections

| Connection | Purpose | Principal |
|---|---|---|
| `ConnectionStrings:App` | all API/background runtime work on `VCentralPay` | `pol_app` |
| `ConnectionStrings:Migrator` / `POL_DESIGN_SQL` | Development/out-of-band migration | `sa` or DDL migrator |
| `SpDocument:MotorConnectionString` | Motor source | `hippo_app` |
| `SpDocument:NonMotorConnectionString` | Non-Motor source | `mammoth_app` |

Credentials come from environment/file secret manager only. Runtime principal has no DDL. SQL 2025 build must be
`17.0.4045.5`+ and `VCentralPay` compatibility level 170.

## Context ownership

- `ControlPlaneDbContext`: admin/IAM/cfg; no merchant dimension
- `MerchantUserDbContext`: merchant identity/session; merchant query filters on owned rows
- `MerchantRuntimeDbContext`: merchant profile/vault/shop/txn; deny-default merchant filters
- `PolDbContext`: full migration model only; never runtime registered

All runtime contexts derive guarded base. Read filter requires bound current merchant. Write guard rejects unbound actor,
empty/mismatched/changed tenant key, forbidden operation and banned bulk/raw bypass.

## Escape hatches

Cross-merchant operations use named ports with explicit admin accessible set, reason/correlation and architecture allowlist.
Direct `IgnoreQueryFilters`, raw SQL, `ExecuteUpdate` or `ExecuteDelete` outside allowlist fails tests. Retired policy reader/
writer escape hatches are absent.

## Database security objects

`SecurityObjects` creates raw sequence/table and one-principal grant matrix. No `SECURITY POLICY`, predicate function,
`SESSION_CONTEXT` isolation, `EXECUTE AS` bypass procedure or bypass principal remains. Security telemetry sends redacted
denials/reconciliation events to Seq; logs exclude secrets and PII.

## Migration safety

Fresh chain: `InitialSchema -> SecurityObjects -> SeedData`. Preflight rejects non-empty/legacy target before DDL.
Production wrapper requires exact `host:port/VCentralPay`, explicit approval, backup URI/SHA-256 and rollback evidence.
Production rollback restores backup; migration Down is non-production proof only.
