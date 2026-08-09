# Local Development Runbook

## 1. Configure

Copy `.env.example` to gitignored `.env` and replace local placeholders. Required DB passwords must differ per principal
and satisfy SQL Server password policy. Never commit `.env`, credential file, KYC object or real customer PII.

```bash
set -a && source .env && set +a
```

## 2. Start dependencies

```bash
docker compose up -d
docker compose ps
```

Expected ports: core SQL `11433`, Motor `11434`, Non-Motor `11435`, Seq loopback `5341`.

Bootstrap scripts create databases/principals and require SQL Server 2025 build `17.0.4045.5`+ with compatibility 170.

## 3. Apply fresh baseline

Use empty local `VCentralPay`. Baseline rejects database with app objects or migration history before DDL. Preserve needed
local data first; reset only exact local target through approved local procedure.

```bash
dotnet tool restore
dotnet ef database update --context PolDbContext \
  --project src/BuildingBlocks/BuildingBlocks.Infrastructure \
  --startup-project src/Hosts/Api
scripts/check-migration-lineage.sh
```

Chain must be `InitialSchema -> SecurityObjects -> SeedData -> OneBasedPersistedEnumStorage`. Fresh seed includes IAM/cfg and one synthetic disabled
merchant/PSP only. No standalone demo seed script.

## 4. Run API

```bash
dotnet watch --project src/Hosts/Api/Api.csproj run
```

- API: `http://localhost:5100`
- OpenAPI: `/openapi/v1.json` in Development
- Scalar: `/scalar`
- health: `/health/live`, `/health/ready`

Config/DI changes require full restart. Development auto-migrate runs only when `ConnectionStrings:Migrator` exists.

## 5. Test

```bash
dotnet build pol-core.slnx -warnaserror
dotnet test pol-core.slnx --no-build --filter "Category!=Integration"
dotnet test tests/Integration.Tests/Integration.Tests.csproj --no-build --filter "Category=Integration"
scripts/spec-trace.sh merchant-commerce-erd-reset
.ai/bin/check-secrets.sh --all
```

Integration target must be fresh baseline and isolated from shared/prod data. Use scratch DB for baseline apply/down tests.

## 6. Commerce smoke

1. login merchant-user
2. query Product with bound SaleCode
3. create Cart and add `productCode`/`variantCode`/quantity
4. `POST /api/v1/orders`
5. confirm `201`, `Location`, `Pending`, Cart `CheckedOut`
6. create/redirect payment session and confirm via webhook
7. verify Order `Paid` and replay idempotency
8. verify retired Checkout/policy routes `404`

Frontend mapping: `.ai/specs/merchant-commerce-erd-reset/FE-MIGRATION.md`.
