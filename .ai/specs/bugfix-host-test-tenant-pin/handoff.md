# Handoff: Hermetic Host Tests for Workforce Tenant Pin

Bugfix complete. `Hosts.Tests` now ignores ambient Admin Microsoft credentials/config while explicit factory settings and production tenant-pin behavior remain intact.

## Files Changed

- `tests/Hosts.Tests/Hosts.Tests.csproj` selects project-local VSTest settings.
- `tests/Hosts.Tests/Hosts.Tests.runsettings` clears inherited Admin Microsoft provider values inside testhost.
- `bugfix.md` records approved F/B contract and hard scope.
- `tasks.md` records implementation evidence for F-1, F-2, and B-1 through B-5.

## Verification

- Ambient-config repro: 1 passed, 0 failed after fix; same test failed before fix.
- Focused Hosts contracts: 29 passed, 0 failed.
- Tenant store tests: 3 passed, 0 failed.
- SQL integration tenant tests: 4 passed, 0 failed.
- Full non-integration solution: 1,936 passed, 0 failed.
- Hosts build: 0 warnings, 0 errors.
- Secret scan and `git diff --check`: passed.

## Remaining Work

None for this bugfix. Existing `admin-workforce-jit` staging Task 6 remains separate and unchanged.
