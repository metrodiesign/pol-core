# Tasks: Hermetic Host Tests for Workforce Tenant Pin

> Status: approved 2026-08-23

One cohesive test-harness fix isolates DB-less host tests from ambient Admin Microsoft configuration while preserving production tenant-pin behavior.

- [x] 1. **Isolate Hosts.Tests configuration** — make testhost ignore ambient Admin Microsoft provider values unless a factory explicitly supplies them, then verify DB-less startup, tenant pin/drift, disabled-provider, real-store, and Merchant authentication contracts.
     Satisfies: F-1, F-2, B-1, B-2, B-3, B-4, B-5
  - deviations: none
