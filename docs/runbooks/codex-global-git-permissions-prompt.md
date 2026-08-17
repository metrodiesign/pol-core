# Prompt: แก้ Codex global Git permissions

ใช้ prompt นี้แก้ profile ระดับ Codex Desktop เพื่อให้ Git ใช้ได้ในทุก workspace ที่ผู้ใช้เปิด โดยคง secret boundary เดิม.

## Objective

ตั้งค่า Codex Desktop global permission profile ให้ทุก workspace เขียน Git metadata และใช้ GitHub HTTPS ได้. Session ใหม่ต้องใช้ profile นี้จริง ไม่ถูก profile ระดับ session จำกัดเหลือ `.git` read-only หรือปิด network.

## Context

- Global config: `/Users/king_developer/.codex/config.toml`
- Profile ปัจจุบัน `github_global` มี `.git = "write"` ใต้ `:workspace_roots` และ allow GitHub domains อยู่แล้ว.
- Session ที่พบปัญหากลับมี `.git` เป็น read-only และ network restricted; จึงเป็น profile/session override ไม่ใช่ Git config ของ repo.
- อาการจริง:

  ```text
  Unable to create '.git/...lock': Operation not permitted
  error connecting to api.github.com
  ```

- `.env.prod.example` เป็น tracked template แต่ policy deny `.env.*`; ห้ามอ่านหรือเปิดเผย content. (assumed: ไม่เพิ่ม allowlist กว้างสำหรับ `.env.*` — ถ้าจำเป็น ให้ใช้ exception แบบ path-specific เท่านั้น)

## Scope

- In: Codex Desktop global config, permission profile selection และ project/session overrides
- In: restart หรือ reopen session ที่จำเป็นเพื่อให้ profile ใหม่ถูกโหลด
- Out: source code, Git history, Git credential, SSH config และ secret files

## Constraints

- ทุก workspace ต้องมี `.git = "write"` ผ่าน `:workspace_roots`; ห้ามให้ write นอก workspace โดยไม่จำเป็น
- อนุญาต network เฉพาะ GitHub HTTPS: `github.com`, `api.github.com`, `uploads.github.com`, `objects.githubusercontent.com`, `*.github.com`, `*.githubusercontent.com`
- คง deny สำหรับ `.env`, `.env.*`, `*.key`, `*.pem`, `auth.json`, `credentials.json`, `secrets/**`
- ห้ามตั้ง `:danger-full-access`, ห้ามเปิด `~/.ssh`, `~/.aws` หรือ `/Users` ทั้งหมด
- ห้าม log หรือแสดง token, credential หรือเนื้อหาไฟล์ `.env*`

## Success criteria

หลังเปิด session ใหม่ใน repo ทดสอบ:

```bash
git branch --create-reflog codex-permission-probe
git branch -d codex-permission-probe
git fetch origin
gh api user --jq .login
```

- ทุกคำสั่งจบ exit `0`
- การสร้างและลบ branch สร้าง `.git` lock ได้
- GitHub CLI ติดต่อ API ได้ด้วย keychain credential โดยไม่ต้องตั้ง `GH_TOKEN`
- ตรวจว่า policy ยัง deny `.env`, private key และ credential files โดยไม่อ่าน content

## Output contract

- สรุปไฟล์และ setting ที่เปลี่ยน พร้อมเหตุผล
- ระบุ profile ที่ session ใหม่ใช้จริง
- รายงานผลแต่ละคำสั่ง verify แบบไม่มี secret
- ระบุว่าต้อง restart/reopen Codex Desktop หรือไม่
- ปิดท้าย: `STATUS: DONE | BLOCKED | NEEDS-INPUT`
