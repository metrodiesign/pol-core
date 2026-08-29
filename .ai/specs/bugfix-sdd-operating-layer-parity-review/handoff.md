# Handoff Note: SDD operating-layer parity review bugfix

เอกสารส่งต่องานแก้ review findings หลัง Tasks 1–8 ผ่านครบ

## Task Summary

ปิด failure classes ด้าน guard ownership, phase wiring, strict historical coverage, crash-consistent retrofit writes, initial-push range, pane-loop failure, shell setup และ verification metadata ตาม `bugfix-sdd-operating-layer-parity-review`.

## Current Status

Done: Tasks 1–8 เป็น `[x]`; strict trace และ full local gates ผ่าน

## Files Changed

- `scripts/spec-retrofit.py` — crash-consistent writer, recovery resolver และ strict historical inventory
- `scripts/spec_contract.py` — strict all-spec behavior และ contract fixes
- `scripts/ci-evidence-scope.sh` — initial-push empty-tree range
- `scripts/pane-loop.sh` — surface engine และ retrospective failures
- `.claude/hooks/tests/` และ `scripts/tests/` — regression, mutation และ fail-fast fixtures
- CI/spec artifacts — strict cutover และ evidence alignment

## Important Decisions

- Recovery history เป็น append-only; physical cleanup จำกัดเฉพาะ disposable proven entries
- Initial push ใช้ empty tree สำหรับ Evidence scope แต่ comparator ใช้ `HEAD~1`
- Remote GitHub/GitLab และ live-SQL ต้องคง unverified จนมี observed result จริง

## Constraints

- ห้ามแตะ product/runtime paths สำหรับ migration นี้
- ห้าม push ตรง `main` หรือ `develop`; ห้าม force push

## Tests Run

- `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` -> 329 tests; OK
- shell fixture inventory -> 16/16 suites passed
- `python3 scripts/spec_contract.py check --all --strict` -> 63 checked / 0 failing / 0 unchecked
- `python3 scripts/spec-retrofit.py --check --batch final-all-spec` -> historical 61/61 และ current scopes ผ่าน
- `dotnet build pol-core.slnx --no-restore -warnaserror` -> 0 warnings / 0 errors
- Linux SDK non-integration test -> 1929 passed / 0 failed

## Known Issues

- Remote required-check rules, GitLab runner และ live-SQL integration ยัง unverified ตาม scope record

## Next Recommended Agent

Human review และ CI verification บน pull request

## Next Steps

1. เปิด pull request เข้า `develop`
2. รอ required CI checks แล้ว review ก่อน merge
