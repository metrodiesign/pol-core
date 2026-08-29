# Implementation Tasks: Admin Merchant Provisioning Contract

> Status: unknown

- [x] 1. Document existing atomic Merchant provisioning/vault relationships and add HTTP approval-boundary regression tests for missing, inactive and active Merchants.
     Satisfies: REQ-1, REQ-2, REQ-3, REQ-4.
  - deviations: solution-level offline command ถูกยกเลิกหลังทุก project ยกเว้น `Hosts.Tests` จบ เพราะ local macOS process รอ file watcher; rerun `Hosts.Tests` แยกพร้อมปิด config reload ผ่าน 444/444. `Architecture.Tests` full suite ผ่าน 233/233.
