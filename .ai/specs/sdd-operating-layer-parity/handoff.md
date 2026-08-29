# Handoff Note: SDD operating-layer parity

เอกสารส่งต่อหลัง Tasks 1–10 และ final local verification ผ่านครบ

## Task Summary

ย้าย SDD operating layer ไป shared strict owners, retrofit historical corpus, align adapters และ cut over GitHub/GitLab verify paths โดยไม่เปลี่ยน product runtime.

## Current Status

Done: Tasks 1–10 เป็น `[x]`; local final gate ผ่านและพร้อมเปิด pull request เข้า `develop`

## Files Changed

- `scripts/spec_contract.py`, `scripts/spec-retrofit.py`, `scripts/spec_trace.py` — shared contracts และ strict migration
- `scripts/ci-evidence-scope.sh`, `scripts/pane-loop.sh` — CI range และ orchestration failure handling
- `.github/workflows/ci.yml`, `.gitlab-ci.yml` — strict verify cutover เท่านั้น
- `.claude/hooks/tests/`, `scripts/tests/` — regression, mutation และ cross-harness coverage
- `.ai/specs/` — canonical evidence และ historical retrofit artifacts

## Important Decisions

- Python stdlib เป็น owner เดียวของ SDD contract semantics
- Protected workflow comparator และ product-path diff fail closed
- Verification record แยก local/static pass ออกจาก remote/environment unverified

## Constraints

- Product/runtime paths และ protected CI jobs ต้อง byte-stableเทียบ merge-base
- ห้าม push ตรง `main` หรือ `develop`; ห้าม force push

## Tests Run

- `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` -> 329 tests; OK
- shell fixture inventory -> 16/16 suites passed; cross-harness 18/18
- `python3 scripts/spec_contract.py check --all --strict` -> 63 checked / 0 failing / 0 unchecked
- `scripts/spec-trace.sh sdd-operating-layer-parity` -> 178 criteria covered
- protected comparator -> allow; product/runtime diff -> empty
- `dotnet restore pol-core.slnx` -> exit 0
- `dotnet build pol-core.slnx --no-restore -warnaserror` -> 0 warnings / 0 errors
- Linux SDK non-integration test -> 1929 passed / 0 failed

## Known Issues

- macOS VSTest testhost transport timeout; Linux SDK test runเป็น passing substitute
- Remote GitHub rules, GitLab runner และ live-SQL integration ยัง unverified

## Next Recommended Agent

Human review และ CI verification บน pull request

## Next Steps

1. เปิด pull request เข้า `develop`
2. รอ required CI checks แล้ว review ก่อน merge
