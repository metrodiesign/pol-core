# Requirements: Multi-Tier Deployment (App tier / DB tier split, PCI-DSS L1)

> Status: approved 2026-07-22, amended 2026-07-22 (EARS-lint wording only, no semantic change)

## Overview

pol-core is a captive insurance sales + payment platform (see
[PROJECT_CONTEXT.md](../../shared/PROJECT_CONTEXT.md)) that must keep its
merchants' PSP-facing surface at PCI SAQ A while the platform itself sits in
a PCI-DSS L1-graded production environment. This feature replaces the current
single-host self-host deployment (API + Worker + SQL Server all on one Docker
host) with a two-tier topology: an Application tier (Docker, API host only —
Worker's background services merge in) and a separate Database tier (bare-VM
SQL Server 2025, no Docker), connected over TCP 1433 with real TLS certificate
validation instead of the dev-oriented "trust any certificate" shortcut. These
requirements are derived from the approved
[design.md](design.md) (approved 2026-07-22) — each criterion cites the
design section it comes from.

## REQ-1: Remote Database Tier Connectivity

**User Story:** As an infra operator, I want the App tier's `migrate` and
`api` containers to connect to a SQL Server instance on a separate DB tier
host, so that the database can be provisioned and operated independently of
the application servers per the PCI-DSS L1 network segmentation shown in the
source topology diagram.

**Acceptance Criteria (EARS):**
- 1.1  THE SYSTEM SHALL resolve the database host from the `DB_SERVER` environment variable rather than a hardcoded or Docker-internal service name. *(design.md > Data Models & Interfaces > New/changed environment variables)*
- 1.2  THE SYSTEM SHALL resolve the database port from the `DB_PORT` environment variable, defaulting to `1433` when unset. *(same)*
- 1.3  WHEN the `api` or `migrate` container starts THE SYSTEM SHALL build its SQL connection string using `${DB_SERVER},${DB_PORT}` as the `Server` value. *(design.md > entrypoint.sh / migrate-entrypoint.sh new shape)*
- 1.4  WHERE `docker-compose.prod.yml` no longer defines a `sql` service THE SYSTEM SHALL NOT declare a `depends_on` dependency on any local database container. *(design.md > compose service shape)*
- 1.5  THE SYSTEM SHALL keep the compose filename `docker-compose.prod.yml` unchanged. *(design.md > explicit naming decision)*

## REQ-2: TLS Certificate Validation to DB Tier

**User Story:** As a security/compliance owner, I want every connection from
the App tier to the DB tier to validate the server's TLS certificate against
a real trusted CA rather than accepting any certificate, so that data in
transit meets PCI-DSS L1 encryption requirements and is not exposed to a
MITM risk.

**Acceptance Criteria (EARS):**
- 2.1  THE SYSTEM SHALL NOT set `TrustServerCertificate=True` anywhere in `docker-compose.prod.yml` or the scripts it invokes. *(design.md > Non-Functional Considerations > PCI-DSS L1)*
- 2.2  WHERE `DB_CA_CERTIFICATE_FILE` is set to a non-empty value THE SYSTEM SHALL build the API connection string with `Encrypt=Strict` and a `Certificate=` parameter pointing at that file. *(design.md > entrypoint.sh new shape; AN-9: empty string = not set)*
- 2.3  WHERE `DB_CA_CERTIFICATE_FILE` is unset or empty THE SYSTEM SHALL build the connection string with `Encrypt=True;TrustServerCertificate=False` (validation against the OS trust store). *(design.md > entrypoint.sh new shape; AN-1: hardcoded False)*
- 2.4  THE SYSTEM SHALL NOT expose any configuration variable that enables an unvalidated database connection — there is no `DB_TRUST_SERVER_CERTIFICATE` knob; the trust mode is not operator-overridable. *(AN-1: hard invariant, fail-closed)*
- 2.5  IF the TLS handshake fails certificate validation THEN THE SYSTEM SHALL fail the connection attempt and SHALL NOT fall back to an unvalidated connection. *(design.md > Error Handling Strategy)*
- 2.6  IF a connection or TLS failure occurs THEN THE SYSTEM SHALL surface only a generic failure message on externally-served responses (`/health/ready`), WHILE container logs SHALL distinguish network-unreachable from TLS-validation failure. *(design.md > Error Handling Strategy; AN-2: operator logs keep diagnostic detail)*
- 2.7  THE SYSTEM SHALL install the DB tier's CA certificate into the `migrate` container image's OS trust store at build time, so `sqlcmd`'s own TLS validation succeeds without a blind-trust flag. This build-time/runtime asymmetry vs the API's mounted-file pinning (2.2) is deliberate — `sqlcmd` supports no `Certificate=` pin, and a start-as-root-then-drop workaround would weaken the non-root container posture. *(design.md > Data Models & Interfaces > migrate-entrypoint.sh; AN-3)*

## REQ-3: Migrate Container Remote Reachability

**User Story:** As an operator running a first deploy or an upgrade, I want
the `migrate` job to reliably detect whether the DB tier is reachable before
attempting bootstrap/migration, so that a network or firewall
misconfiguration fails loudly instead of hanging indefinitely.

**Acceptance Criteria (EARS):**
- 3.1  WHEN the `migrate` container starts THE SYSTEM SHALL attempt to connect to the DB tier host for up to a configurable bounded number of total attempts including the first (default 30), with a configurable delay between attempts (default 5 seconds). *(design.md > Sequence Diagrams > First deploy/migrate; AN-8: 30 = total)*
- 3.2  IF all connection attempts are exhausted without success THEN THE SYSTEM SHALL exit with a non-zero status and log a message indicating the DB tier was unreachable. *(design.md > Error Handling Strategy)*
- 3.3  WHEN a connection attempt to the DB tier succeeds THE SYSTEM SHALL proceed immediately to bootstrap principals rather than exhausting the remaining retry budget. *(design.md > Sequence Diagrams)*
- 3.4  THE SYSTEM SHALL perform bootstrap (`01-principals.sql`) and EF migration idempotently, applying only migrations not already recorded as applied. *(preserved existing behavior — design.md > Architecture Overview > migrate responsibilities)*
- 3.5  THE SYSTEM SHALL state in its deployment documentation, as a precondition, that the DB tier must provide the `sa` login (or an equivalent sysadmin-capable login) reachable from the App tier for bootstrap/migration — hardened installs that disable/rename `sa` must be coordinated with infra/DBA before first deploy. *(AN-6: unstated assumption made explicit)*

## REQ-4: Worker Hosted Services Merge into API Host

**User Story:** As a platform operator, I want the outbox dispatchers and
other background services that previously ran in a separate Worker
container to run inside the single API process, so there is one fewer
deployable unit and one fewer image to build, push, and operate, per the
approved consolidation decision.

**Acceptance Criteria (EARS):**
- 4.1  THE SYSTEM SHALL register the MerchantRuntime outbox dispatcher hosted service in the API host's composition root (`src/Hosts/Api/Program.cs`). *(design.md > Data Models & Interfaces > Program.cs merge)*
- 4.2  THE SYSTEM SHALL register the MerchantUsers outbox dispatcher hosted service in the API host's composition root. *(same)*
- 4.3  THE SYSTEM SHALL NOT include a `worker` service in `docker-compose.prod.yml` or `docker-compose.registry.yml`. *(design.md > Architecture Overview > Deleted from this topology)*
- 4.4  THE SYSTEM SHALL NOT build or publish a `worker` container image from CI. *(same)*
- 4.5  THE SYSTEM SHALL remove the `src/Hosts/Worker/` project, including its dead-code stub port implementations (`Unsupported*` role/provisioning/audit stores), once their responsibilities are confirmed covered by the API host's existing registrations. *(design.md > files that become dead code)*
- 4.6  WHERE `WorkerModuleAssemblies.cs`'s assembly list is confirmed a subset of the API host's `HostModuleAssemblies.All` THE SYSTEM SHALL delete `WorkerModuleAssemblies.cs` and rely solely on the API host's assembly list. *(design.md > flagged verification item)*
- 4.7  THE SYSTEM SHALL keep the outbox dispatchers safe under multiple concurrent API replicas — the existing per-row SQL lease claim (`READPAST + UPDLOCK + ROWLOCK` + per-row owner, `OutboxDispatcher.cs`) SHALL be preserved unchanged by the merge. *(AN-5: verified existing multi-instance-safe design; merge must not regress it)*

## REQ-5: Actor Context and Write-Authorizer Resolution by Execution Scope

**User Story:** As a security reviewer, I want the merged API host to resolve
the correct actor identity and write-authorization policy depending on
whether code is executing inside an HTTP request or a background dispatcher
batch, so that the multi-merchant write-authorization boundary is never
silently bypassed or misattributed after the Worker merge.

**Acceptance Criteria (EARS):**
- 5.1  WHEN a DI scope is created within an HTTP request pipeline THE SYSTEM SHALL resolve `IActorContext` to `HttpActorContext`. *(design.md > Program.cs merge — flagged highest-risk item)*
- 5.2  WHEN a DI scope is created by the outbox dispatcher's background batch processing (no `HttpContext` present) THE SYSTEM SHALL resolve `IActorContext` to `WorkerActorContext`. *(same)*
- 5.3  THE SYSTEM SHALL use `IHttpContextAccessor.HttpContext` presence/absence as the sole discriminator for this resolution, without introducing a new marker interface. *(design.md > Technology Decisions)*
- 5.4  THE SYSTEM SHALL apply the same HTTP-vs-background discriminator to `IWriteAuthorizer` resolution, selecting `WorkerWriteAuthorizer` for background scopes and the existing per-request authorizer selection for HTTP scopes. *(design.md > Program.cs merge)*
- 5.5  IF a background-created scope resolves `HttpActorContext` (or an HTTP-request scope resolves `WorkerActorContext`) THEN an automated composition-root test SHALL fail — this boundary SHALL NOT depend on manual review alone. *(design.md > Testing Strategy > composition-root merge test)*
- 5.6  THE SYSTEM SHALL resolve the background-scope branch (`WorkerActorContext`/background write authorizer) for the API host's pre-existing background hosted services (`SessionPruneService`, `UserSessionPruneService`) after the merge, and an automated test SHALL verify their prune writes still succeed under that resolution. *(AN-4: verified — their deletes pass through guarded contexts, so authorizer resolution path changes for them too)*

## REQ-6: Compose and CI Topology Changes

**User Story:** As whoever operates CI/CD and the App-tier compose files, I
want the SQL container and Worker service fully removed from the App-tier
deployment artifacts, so the deployed topology matches the two-tier
architecture with no stale service definitions left behind.

**Acceptance Criteria (EARS):**
- 6.1  THE SYSTEM SHALL NOT define a `sql` service in `docker-compose.prod.yml`. *(design.md > Architecture Overview)*
- 6.2  THE SYSTEM SHALL define a `db_ca_cert` entry in the compose `secrets:` block, mounted read-only and referenced via `DB_CA_CERTIFICATE_FILE`. *(design.md > Data Models & Interfaces)*
- 6.3  THE SYSTEM SHALL remove the `worker` image override block from `docker-compose.registry.yml`. *(design.md > Architecture Overview)*
- 6.4  THE SYSTEM SHALL build and push exactly two images (`api`, `migrate`) from CI, down from three (`api`, `worker`, `migrate`). *(same)*

## REQ-7: Non-Functional Constraints (PCI-DSS L1, Availability Ceiling, Secrets)

**User Story:** As a compliance/infra stakeholder, I want the design's
stated non-functional constraints to be explicit, testable requirements
rather than implicit assumptions, so reviewers and future implementers can
verify them directly.

**Acceptance Criteria (EARS):**
- 7.1  THE SYSTEM SHALL NOT provide any configuration path in `docker-compose.prod.yml` that results in an unvalidated (trust-any-certificate) database connection. *(design.md > Non-Functional Considerations > PCI-DSS L1)*
- 7.2  THE SYSTEM SHALL handle the DB tier CA certificate file with the same discipline as existing `./secrets/*` files (gitignored, `chmod 600`, never committed). *(design.md > Non-Functional Considerations > Secrets handling)*
- 7.3  WHERE DB tier high availability (replica/AG) or Edge-tier load-balancer failover is not implemented by this feature THE SYSTEM SHALL document this as an accepted ceiling, consistent with the existing self-host runbook's stated ceilings. *(design.md > Non-Functional Considerations > Availability ceiling)*
- 7.4  THE SYSTEM SHALL surface DB tier connectivity/TLS failures as `Unhealthy` via the readiness health check (`/health/ready`) without requiring a code change to `AppDbReadinessCheck` itself. *(design.md > Error Handling Strategy)*

## REQ-8: Documentation Sync

**User Story:** As an operator following the runbooks, I want every document
that describes the deployment topology updated to match the two-tier
architecture, so the runbooks never describe a `sql`/`worker` service or a
trust-any-certificate connection that no longer exists.

**Acceptance Criteria (EARS):**
- 8.1  THE SYSTEM SHALL update `docs/runbooks/deploy-self-host.md` to the two-tier topology, including removing the literal `Server=sql;...TrustServerCertificate=True` rollback snippet and stating the REQ-3.5 `sa` precondition. *(AN-7)*
- 8.2  THE SYSTEM SHALL update `docs/runbooks/gitlab-cicd-setup.md` wherever it references three images or the `worker` container (B4 registry check, F2 deploy-log walkthrough). *(AN-7)*
- 8.3  THE SYSTEM SHALL update `docs/reference/db-connection-and-rls.md` and `docs/reference/src-structure.md` where they describe `ConnectionStrings__Worker` or the standalone Worker host. *(AN-7)*
- 8.4  WHERE local development remains on the local Docker SQL container (`localhost,11433`) THE SYSTEM SHALL leave `docs/runbooks/local-dev-run.md` and dev connection settings unchanged. *(AN-7: local dev unaffected by prod topology change)*

## Edge Cases & Open Questions

- **SqlClient version support for `Encrypt=Strict`**: must be verified against the `Microsoft.Data.SqlClient` version this repo already references before REQ-2.2 is implemented; if unsupported, fall back to the OS-trust-store-install alternative already documented in design.md's Technology Decisions (functionally equivalent, costs a rebuild per CA rotation instead of a secret-file swap).
- **`WorkerModuleAssemblies.cs` subset confirmation (REQ-4.6)**: not yet verified against `HostModuleAssemblies.All` — if the lists diverge, the assemblies must be reconciled into the API host's list rather than the file being deleted outright.
- **DB tier provisioning is out of this repo's scope**: host provisioning, certificate issuance, and firewall ACL opening between App tier and DB tier are owned by infra/DBA; this spec assumes those are completed before `migrate` can succeed, but does not implement or verify them itself.
- **Health check timeout tuning**: `AppDbReadinessCheck`'s current timeout is unchanged by this spec; real-world tuning against Server 1's actual network latency is deferred to implementation/operational follow-up, not specified as a numeric requirement here.
- **`WorkerActorContext`/`WorkerWriteAuthorizer` renaming**: left as an explicit open option for whoever implements this — no requirement here mandates a rename away from the "Worker" name now that the standalone Worker host is gone.
- **Edge/DMZ tier (LB pair, keepalived VIP, WAF) and the 3 Next.js web frontends**: confirmed out of scope for this spec (owned by another team / live in separate repos) — no requirement here covers them.

### /spec-analyze findings log (anchor: cde4892 — file uncommitted at analyze time, anchor = HEAD)

| # | Finding | Decision |
|---|---|---|
| AN-1 | REQ-2.1 (no trust-any) contradicted REQ-2.3/2.4's operator-overridable `DB_TRUST_SERVER_CERTIFICATE` | **Fixed** — variable removed entirely; fallback hardcodes `TrustServerCertificate=False`; REQ-2.4 rewritten as hard invariant. UAT self-signed certs use `DB_CA_CERTIFICATE_FILE` pinning instead |
| AN-2 | REQ-2.6 (generic-only messages) conflicted with REQ-3.2's diagnostic log need | **Fixed** — generic only on external responses; container logs distinguish network vs TLS failure (Imperva-diagnosis lesson) |
| AN-3 | REQ-2.2 vs 2.7 CA-delivery asymmetry (mounted file vs baked image) unexplained | **Accepted as deliberate** — `sqlcmd` has no `Certificate=` pin; start-as-root workaround would weaken non-root posture. Note added to REQ-2.7 |
| AN-4 | REQ-5.3 discriminator silently re-routes pre-existing prune services | **Fixed** — verified their deletes pass through guarded contexts; REQ-5.6 added with test obligation |
| AN-5 | Outbox dispatcher multi-replica concurrency unaddressed | **Fixed** — verified existing `READPAST+UPDLOCK+ROWLOCK` per-row lease in `OutboxDispatcher.cs`; REQ-4.7 pins it as preserved behavior |
| AN-6 | Unstated assumption: `sa` available on hardened DB tier | **Fixed** — REQ-3.5 states precondition for infra/DBA coordination; configurable principal rejected (YAGNI) |
| AN-7 | No requirement mandated doc updates | **Fixed** — REQ-8 added with explicit file list; local-dev docs explicitly exempted |
| AN-8 | REQ-3.1 attempt-count ambiguity (30 total vs 31) | **Fixed** — 30 total including first, matches design's `seq 1 30` loop |
| AN-9 | "set" ambiguity for empty `DB_CA_CERTIFICATE_FILE` | **Fixed** — non-empty = set; empty routes to fallback path, matches design's `[ -n ... ]` |
