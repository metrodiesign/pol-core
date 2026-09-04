# Handoff: Admin Employee Profile Sync

> From: Pi session   To: any reviewer   Date: 2026-09-03

## Task Summary

Implemented all four tasks in `.ai/specs/admin-employee-profile-sync/`. Every new Admin Microsoft OIDC callback now resolves immutable `tid + oid`, requires Graph `User.Read` employeeId, reads only three columns from `dbo.VibEmp`, and commits identity/profile/audits before session creation.

## Current Status

- Requirements, design and tasks are approved.
- Tasks 1–4 are complete with Evidence blocks.
- Build, non-integration, integration, migration drift, secret scan and 159-criterion spec trace pass.
- No commit, push, PR, deploy or production query was performed.

## Files Changed

- `.ai/specs/admin-employee-profile-sync/requirements.md` — created — approved EARS requirements and analyze decisions.
- `.ai/specs/admin-employee-profile-sync/design.md` — created — approved architecture and traceability.
- `.ai/specs/admin-employee-profile-sync/tasks.md` — created — completed tasks and observed evidence.
- `src/Hosts/Api/Program.cs` — edited — Production Graph origin pin.
- `src/Hosts/Api/OidcProviderOptions.cs` — edited — removed optional profile switch.
- `src/Hosts/Api/Admins/OidcAuthentication.cs` — edited — mandatory User.Read/Graph and exact consent classification.
- `src/Hosts/Api/Admins/MicrosoftWorkforceClaims.cs` — edited — safe consent failure marker.
- `src/Hosts/Api/Admins/LoginService.cs` — edited — retired unmapped outcome.
- `src/Modules/Admins/Admins.Domain/Users/Audit.cs` — edited — `employee-profile-sync` action.
- `src/Modules/Admins/Admins.Domain/Users/User.cs` — edited — three-field profile writer and change flags.
- `src/Modules/Admins/Admins.Application/Users/EmployeeProfile.cs` — edited — three-column HR contract and validation.
- `src/Modules/Admins/Admins.Application/Users/ResolveAdmin.cs` — edited — final profile outcomes.
- `src/Modules/Admins/Admins.Application/Users/ResolveMicrosoftAdmin.cs` — edited — atomic profile/audit flow.
- `src/Persistence/Persistence.ControlPlane/Admins/EmployeeProfileReader.cs` — edited — exact parameterized VibEmp query.
- `.env.example`, `docker-compose.prod.yml` — edited — removed retired default-false switch.
- `docs/runbooks/admin-microsoft-oidc.md` — edited — mandatory Graph flow, consent behavior and late-table SELECT grant.
- `tests/Admins.Tests/*EmployeeProfile*` — edited — policy, aggregate and handler behavior.
- `tests/Architecture.Tests/EmployeeProfileReaderIntegrationTests.cs` — edited — real SQL exact query, permissions and failures.
- `tests/Architecture.Tests/Tier0EmployeeProfileTransactionTests.cs` — edited — atomic JIT/refresh/no-op/race.
- `tests/Architecture.Tests/Tier0WorkforceArchitectureTests.cs` — edited — privacy and retired-path static gates.
- `tests/Architecture.Tests/BypassPrimitiveTests.cs` — edited — narrowed raw-SQL allowlist rationale.
- `tests/Hosts.Tests/AdminGraphEmployeeProfileE2ETests.cs` — edited — mandatory scope, consent/cancel and session-rotation isolation.
- `tests/Hosts.Tests/OidcCallbackE2ETests.cs` — edited — mandatory Graph fixture for Admin callbacks.
- `tests/Hosts.Tests/AdminAuthLoginRedirectTests.cs`, `MicrosoftAuthLoginRedirectTests.cs` — edited — Admin User.Read scope with Merchant scope preserved.
- `tests/Hosts.Tests/AdminLoginServiceTests.cs` — edited — final denial outcomes.
- `tests/Hosts.Tests/ProvisioningGuardsTests.cs` — edited — Production Graph origin guard.

## Important Decisions

- Authentication identity remains `(microsoft, validated tid, validated oid)`.
- New Admin Microsoft OIDC callbacks always request `User.Read` and call Graph exactly once after validation.
- Exact `consent_required` maps to profile unavailable; user cancel `access_denied` remains distinct.
- Existing session requests and rotation never call Graph.
- EmployeeId remains globally unique while production admits one workforce tenant.
- Existing schema and filtered unique index are reused; no migration was added.
- Mismatch, taken and Suspended outcomes stop before HR access.
- Existing name changes append `employee-profile-sync`; profile no-op creates no version, timestamp or audit change.
- Position, Office, Level, Division, Tier, roles and MerchantAccess are preserved.

## Constraints

- Do not reintroduce branch, DepartmentID, Office, Division, HR status or email-based fallback into the runtime profile path.
- Do not map `dbo.VibEmp` as an EF entity or add a write path.
- Do not log token, employee identifiers, names, Graph body, SQL values or exception messages.
- Preserve unrelated untracked `scripts/load-hr-mirror.sh` and `scripts/load-hr-mirror.test.sh`; they were not edited by Task 4.

## Tests Run

- `dotnet build pol-core.slnx --no-restore -warnaserror` -> succeeded, 0 warnings and 0 errors.
- `dotnet test pol-core.slnx --no-build --filter "Category!=Integration"` -> 2,080 passed, 0 failed.
- `dotnet test tests/Hosts.Tests/Hosts.Tests.csproj --no-build --filter "FullyQualifiedName~AdminGraphEmployeeProfileE2ETests|FullyQualifiedName~OidcCallbackE2ETests|FullyQualifiedName~AdminSessionAuthHandlerTests|FullyQualifiedName~MicrosoftAuthLoginRedirectTests"` -> 70 passed, 0 failed.
- `dotnet test tests/Architecture.Tests/Architecture.Tests.csproj --no-build --filter "FullyQualifiedName~Tier0WorkforceArchitectureTests"` -> 20 passed, 0 failed.
- `set -a; source .env.integration; set +a; dotnet test pol-core.slnx --filter "Category=Integration"` -> 196 passed, 0 failed.
- `scripts/check-migration-script.sh` -> schema script drift gate passed.
- `.ai/bin/check-secrets.sh --all` -> exit 0, no findings.
- `scripts/spec-trace.sh admin-employee-profile-sync` -> 159 criteria covered, EARS lint passed.
- `git diff --check` -> exit 0.

## Known Issues

- One targeted Architecture test command was initially run without loading `.env.integration`; it failed only because `POL_SA_PASSWORD` was absent. The exact command was rerun with the env loaded and passed 26/26.
- Local `pol-db` volume was reset with explicit user approval after startup detected 8 stale unbound Admin rows. Fresh DB has 23 migrations, zero Users, completed tenant identity state and zero invalid final users; API stayed running for 30 seconds.
- Local `dbo.VibEmp` provisioning is operator-managed and must remain present with `pol_app` SELECT before testing login.
- Microsoft Graph, consent error and Entra callbacks were tested with synthetic OIDC/HTTP fixtures, not a live tenant.
- Mermaid parser was unavailable because the installed diagrams skill has no `check-mermaid.mjs`.

## Next Recommended Agent

Use a code reviewer to compare the complete diff against this spec before any commit or PR decision.

## Next Steps

1. Read requirements, design and tasks under `.ai/specs/admin-employee-profile-sync/`.
2. Review `git diff` against REQ-1 through REQ-9.
3. Re-run the recorded gates if the working tree changes.
