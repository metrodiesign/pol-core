# Workflow: Frontend Task (UI / visual / a11y / responsive)

Vendor-neutral procedure for any agent (Claude / Codex / OpenCode / Pi). recipe สำหรับ
งาน UI ของ repo นี้ (สร้างด้วย the project UI framework + styling system) ที่ต้องผ่าน
visual completeness, a11y, และ responsive ที่ 375 / 768 / 1440px.

## Purpose

สร้าง/แก้ UI ให้ "ดูเสร็จ ดูแพง" และทำงานจริง: ใช้ component model ของ the project UI framework
ตาม convention ของมัน (render บน server เป็นค่าเริ่มต้นถ้า framework รองรับ, แยก client/interactive
boundary เฉพาะส่วนที่ต้องโต้ตอบจริง), design tokens ที่เดียว, ทุกพื้นที่ภาพดูเสร็จ (ไม่มีกล่องว่าง),
keyboard นำทางได้, contrast ผ่าน, ไม่มี horizontal overflow — แล้วยืนยันผลใน the project target
runtime ที่ viewport จริง (ไม่ใช่แค่ build เขียว).

## When to use

- สร้าง/แก้ component UI, section ของ homepage, interaction (เมนู/slider/filter/form),
  responsive fix, a11y fix, visual polish.
- ไม่ใช้กับ: pure logic (อยู่ใน `test-generation.md` / the project logic directory), root-cause bug
  ที่ไม่ใช่ UI (ใช้ `bug-fix.md`).

## Required context files

อ่านก่อนเริ่ม (relative จากไฟล์นี้):

- [../shared/stack/README.md](../shared/stack/README.md) — stack profile (optional): บทเรียน
  stack-specific ของ the project UI framework / styling system / test runner — โหลดเฉพาะเมื่อมี
  `<stack>.md` profile อยู่จริง (patterns ที่ promote จาก retrospectives).
- [../shared/ARCHITECTURE.md](../shared/ARCHITECTURE.md) — folder layout, naming, render/interactive
  boundary, container wrapper, design tokens, state ครบ (default/hover/focus/active/disabled),
  anti-patterns.
- [../shared/CODING_STANDARDS.md](../shared/CODING_STANDARDS.md) — hard constraints: ห้ามกล่องว่าง,
  ห้าม raw hex ซ้ำ, ทุก image มี alt, semantic HTML, contrast, design tokens ของโปรเจกต์.
- [../shared/PROJECT_CONTEXT.md](../shared/PROJECT_CONTEXT.md) — rubric (design 20, visual 18,
  responsive 12, interaction 8, a11y 4) + acceptance checklist.
- UI-verify reference (the project UI-verify reference, เช่น `.claude/skills/spec-implement/
  references/browser-verify.md`) — ต้อง Read ก่อนทุกครั้งที่จะ verify UI ใน the project target
  runtime (probe interaction / overflow / focus-ring / viewport).

## Step-by-step process

1. **อ่าน context + ระบุ component boundary.** ดูว่า component อยู่ section ไหน, รับ props/import
   data จาก the project data directory อย่างไร, มี anchor ใน navigation data ไหม. ตัดสิน
   render/interactive boundary ตาม convention ของ the project UI framework: ทำเป็น interactive/client
   เฉพาะที่ต้องโต้ตอบจริง (menu, slider, filter, form) — ไม่ทำทั้งหน้า/root เป็น client โดยไม่จำเป็น.
   -> verify: รู้ว่าเป็น render-only หรือ interactive component + แตะไฟล์ไหนบ้าง (ตรง ARCHITECTURE.md).

2. **Implement ตาม tokens + state ครบ.** ใช้ semantic class/token จาก design tokens ของ the project
   styling system — ห้าม hardcode hex ดิบซ้ำ. logic คำนวณ/validate เรียกจาก the project logic
   directory ไม่ฝังสูตรใน markup. ปุ่ม/การ์ด/อินพุตมี state ครบ: default/hover/focus(ring)/active/
   disabled + transition 150-250ms. ครอบเนื้อหาด้วย container wrapper ของโปรเจกต์ (max-width
   ~1200-1280px). ทุกพื้นที่ภาพดูเสร็จ: inline SVG มีรายละเอียด / รูปจริงผ่าน image primitive ของ
   the project UI framework / gradient + ลวดลายที่จัดองค์ประกอบ — ห้ามกล่อง/สี่เหลี่ยมสีเดียวเป็น
   placeholder. ทุก image มี `alt`.
   -> verify: ไม่มี raw hex ซ้ำ, ไม่มีสูตรใน markup, state ครบ, ไม่มีกล่องว่าง, ทุก image มี alt.

3. **Static check ก่อนเปิด UI.** รัน the project typecheck command (ผ่าน `SDD_TYPECHECK_CMD` env
   หรือ typecheck script ใน package.json) — ถ้าเป็น typed stack ให้ strict, ไม่มี escape hatch.
   ถ้ามี pure logic เกี่ยวข้องรัน the project test runner (ผ่าน `SDD_TEST_CMD` env หรือ test script
   ใน package.json สำหรับโปรเจกต์ Node). task gate code-green รันชุดเดียวกันนี้: `.ai/bin/gate-task.sh`
   อ่าน `SDD_TYPECHECK_CMD` / `SDD_TEST_CMD` (auto-detect package.json scripts สำหรับ Node) —
   เขียวทั้งคู่ task ถึง mark `[x]` ได้.
   -> verify: typecheck เขียว; test (ถ้ามี) เขียว.

4. **UI verify ใน the project target runtime.** ถ้าโปรเจกต์ ship UI ให้ verify ใน the project
   target runtime (ดู the project UI-verify reference) — dev/HMR mode บางตัวพังการ hydrate/mount
   -> ใช้ runtime/build ที่ reference กำหนด (เช่น production build) แทน. หลังแก้โค้ดทุกครั้ง **rebuild**
   ก่อนเทสซ้ำ ถ้า runtime นั้น serve artifact ที่ build ไว้. ชี้ client ไป loopback IPv4
   (`http://127.0.0.1:<port>/` ไม่ใช่ `localhost`). ก่อนเชื่อผลคลิก probe ว่า element โหลด/hydrate/
   mount แล้วจริงตามที่ the project UI-verify reference อธิบาย — ยังไม่พร้อม = คลิก no-op ไม่ใช่บั๊ก UI.
   Read the project UI-verify reference ก่อนเริ่ม probe.
   -> verify: build เขียว + runtime พร้อม serve; element โหลด/hydrate/mount แล้ว (probe ผ่าน).

5. **Responsive ที่ viewport เป๊ะ 375 / 768 / 1440.** ยืนยัน `document.documentElement.clientWidth
   === target` ทุกครั้งก่อนเชื่อผล. mobile 375 จริงต้องใช้ `emulate` viewport `375x812x2,mobile,touch`
   (chrome-devtools MCP `resize_page` มี min window ~485px บน macOS = false-pass ขอบล่าง). วัด
   overflow ด้วย `document.documentElement.scrollWidth > innerWidth` เป็น primary (ตัด SVG geometry
   + slider ที่ถูก clip ออกจาก culprit probe). culprit ที่เป็น component ของ task อื่น = รายงาน
   ไม่แก้เงียบ (task boundary).
   -> verify: clientWidth === target ที่ทั้ง 3 viewport; ไม่มี horizontal overflow (scrollWidth ไม่เกิน).

6. **a11y + interaction.** focus-ring ตรวจด้วย keyboard Tab จริง (`press_key Tab` -> อ่าน
   activeElement outline/boxShadow) ไม่ใช่ programmatic `el.focus()` (heuristic เพี้ยนใน headless).
   contrast: ไต่ ancestor อ่านทั้ง `backgroundColor` และ `backgroundImage` (พื้นเข้มหลายที่เป็น
   gradient -> `backgroundColor` = transparent ทำให้รายงาน contrast ปลอม). interaction:
   แยก "action (click/dispatch)" กับ "read DOM" เป็นคนละ evaluate call (UI framework re-render มัก
   async); keydown dispatch ได้, mouseenter dispatch ไม่ได้ (ใช้ `page.hover()` สำหรับ pointer).
   -> verify: keyboard นำทาง + focus-ring เห็นจริงทุกชนิด element; contrast ผ่านเกณฑ์ (เทียบ bg layer
   จริง); interaction (เมนู/slider/filter) ทำงานใน the project target runtime.

7. **บันทึก Evidence.** ตาม TASK_PROTOCOL: บันทึก viewport values ที่สังเกตจริง (`375 OK | 768 OK |
   1440 OK` พร้อม clientWidth ที่วัด) + deviations. ห้าม assert pass ที่ไม่ได้เห็น; check ที่รันไม่ได้
   ระบุใน deviations.

## Expected output

- component ใต้ the project component directory ตาม ARCHITECTURE.md, render-only โดยค่าเริ่มต้น
  + interactive/client boundary เฉพาะส่วนที่ต้องโต้ตอบ.
- design tokens ผ่าน semantic class/token; data จาก the project data directory; logic จาก
  the project logic directory.
- Evidence: typecheck เขียว, build เขียว, viewport 375/768/1440 ยืนยันค่า clientWidth จริง, a11y +
  interaction ผ่าน.

## Definition of done

- [ ] typecheck ผ่าน; test ผ่าน (ถ้าแตะ pure logic) — ผ่าน command ที่โปรเจกต์ประกาศ (`$SDD_TYPECHECK_CMD` / `$SDD_TEST_CMD`).
- [ ] ถ้าโปรเจกต์ ship UI: verify ใน the project target runtime ที่ `127.0.0.1` (ดู the project UI-verify reference; rebuild หลังแก้ทุกครั้งถ้า runtime นั้น serve build artifact).
- [ ] `clientWidth === target` ยืนยันที่ 375 / 768 / 1440; ไม่มี horizontal overflow (scrollWidth check).
- [ ] focus-ring ตรวจด้วย Tab จริง; keyboard นำทางได้; contrast ผ่าน (เทียบ bg layer จริงรวม gradient).
- [ ] interaction (เมนู/slider/filter) ทำงานใน the project target runtime (probe โหลด/hydrate/mount ก่อนสรุป).
- [ ] ไม่มีกล่องว่าง/raw hex ซ้ำ/สูตรใน markup/interactive boundary ครอบทั้งหน้าโดยไม่จำเป็น; ทุก image มี alt.
- [ ] Evidence block บันทึกค่าที่สังเกตจริง (ไม่ใช่ค่าที่วางแผน).

## Common mistakes to avoid

- verify ใน dev/HMR mode — บางตัวพังการ hydrate/mount ที่นี่ -> false-negative ("คลิกไม่ทำงาน").
  ใช้ runtime/build ที่ the project UI-verify reference กำหนด.
- ไม่ rebuild หลังแก้โค้ดเมื่อ runtime serve build artifact — serve artifact เก่า -> ทดสอบโค้ดเก่า
  (build เขียว/200 พิสูจน์แค่ "มีอะไรรันอยู่" ไม่ใช่ "โค้ดล่าสุด").
- ตั้ง 375 ด้วย `resize_page` — macOS min window ~485px = false-pass ขอบล่าง. ใช้ `emulate` viewport.
- เชื่อผลคลิกก่อน probe โหลด/hydrate/mount — ยังไม่พร้อม = คลิก no-op ไม่ใช่บั๊ก (วิธี probe ดู the
  project UI-verify reference).
- อ่าน DOM ใน evaluate call เดียวกับ action — UI framework re-render มัก async อ่านค่าเก่า (race). แยก call.
- `el.focus()` programmatic ตรวจ focus-ring — heuristic `:focus-visible` เพี้ยนใน headless. ใช้ Tab จริง.
- contrast probe อ่าน `backgroundColor` อย่างเดียว — พื้น gradient = transparent -> รายงาน contrast
  ปลอม. อ่าน `backgroundImage` ด้วย.
- วัด overflow ด้วย `getBoundingClientRect().right` อย่างเดียว — SVG geometry + slider ที่ถูก clip =
  false positive. ใช้ `scrollWidth > innerWidth` เป็น primary.
- hardcode hex ดิบ / กล่องสีเดียวเป็น placeholder / ฝังสูตรใน markup / interactive boundary ครอบ root —
  anti-pattern.
- แก้ component ของ task อื่นเงียบเมื่อเจอ overflow culprit ข้าม boundary — รายงาน ไม่แก้เงียบ.
