# Staging Reset, Smoke and Rollback Rehearsal Evidence

> Repository-local staging-equivalent rehearsal. Production release still requires fresh environment evidence URI,
> human approval and verified backup artifact.

## Scope

- Date: 2026-08-07 Asia/Bangkok
- Feature: `merchant-commerce-erd-reset`
- Target: isolated SQL Server 2025 scratch database/container
- Production database: not touched
- Baseline: `InitialSchema -> SecurityObjects -> SeedData`

## Reset and migration proof

- SQL Server engine/build and database compatibility 170 gate passed.
- Empty target accepted; non-empty target refused before DDL.
- Legacy migration-history target refused before DDL.
- Fresh apply completed exactly three migrations.
- `docker/bootstrap/assert-fresh-db.sql` passed schema, raw objects, native JSON, grants, seed and retired-table assertions.

## Smoke proof

- Focused migration/native JSON suite: 5 passed, 0 failed.
- Full live SQL Integration suite on isolated fresh database: 144 passed, 0 failed.
- Five native JSON columns accepted valid typed JSON and rejected invalid JSON.
- Order sequence, registration notice raw table, one-principal grants and 19/7/25 IAM seed asserted.
- Host/unit suites cover direct Cart-to-Order, serialized payment lifecycle and retired route `404` behavior.

## Rollback rehearsal

- `dotnet ef database update 0` completed against isolated scratch database.
- Dependency-safe `Down` revoked/dropped raw security objects before schema teardown.
- Scratch database removed after proof; existing `VCentralPay` catalog remained untouched.

## Production decision

Repository proof validates mechanism, not production authorization. Before production release attach:

- staging target-specific reset/apply logs
- Cart -> Order -> Payment smoke result
- staging backup restore rehearsal result and duration
- production backup URI + SHA-256
- named human reset approval
- production rollback evidence/owner

Release gate rejects missing evidence.
