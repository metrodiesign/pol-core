# Bugfix: Hermetic Host Tests for Workforce Tenant Pin

> Status: approved 2026-08-23

Host tests must ignore ambient Admin Microsoft configuration unless factory configures provider explicitly. Production tenant-pin startup invariant remains unchanged.

## Current Behavior (Defect)

WHEN `Hosts.Tests` runs with ambient Admin Microsoft `ClientId` and tenant-pinned `Authority` THEN 153 DB-less `WebApplicationFactory` tests attempt SQL startup against dummy `Server=(local)` and fail with `SqlException: Connection refused`.

Reproduction:

```bash
env AdminAuth__Providers__Microsoft__ClientId=test-client \
  AdminAuth__Providers__Microsoft__Authority=https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0 \
  DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false \
  dotnet test tests/Hosts.Tests/Hosts.Tests.csproj \
  --no-build --no-restore \
  --filter 'FullyQualifiedName=Hosts.Tests.RouteSchemeConventionTests.Every_routed_endpoint_is_under_api_v1_area_or_the_infra_allowlist'
```

Observed result: 0 passed, 1 failed in 16.5 seconds. Stack trace ends at `Program.cs:626` when `IWorkforceTenantBindingStore.EnsureAsync()` opens dummy SQL connection.

## Expected Behavior

- F-1 WHEN DB-less `Hosts.Tests` runs with ambient Admin Microsoft configuration THE SYSTEM SHALL complete host startup without opening live SQL.
- F-2 WHEN full non-integration test gate runs with ambient Admin Microsoft configuration THE SYSTEM SHALL use only provider configuration declared by each test factory.

## Unchanged Behavior

- B-1 WHEN Admin Microsoft provider is explicitly enabled by tenant-pin contract test THE SYSTEM SHALL CONTINUE TO call `EnsureAsync()` exactly once before listening.
- B-2 WHEN persisted workforce tenant differs from explicitly configured Authority THE SYSTEM SHALL CONTINUE TO fail host startup before listening.
- B-3 WHEN Admin Microsoft provider is disabled THE SYSTEM SHALL CONTINUE TO skip tenant-pin database initialization.
- B-4 WHEN SQL integration tests run THE SYSTEM SHALL CONTINUE TO exercise real `WorkforceTenantBindingStore` behavior.
- B-5 WHEN Merchant authentication tests run THE SYSTEM SHALL CONTINUE TO preserve Merchant Google and Microsoft behavior.

## Hard Scope

Fix is test-harness isolation only. Do not modify:

- `src/Hosts/Api/Program.cs`
- `src/Persistence/Persistence.ControlPlane/`
- database schema or migrations
- Merchant authentication production code
