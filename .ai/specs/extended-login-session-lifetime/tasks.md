# Tasks: Extended Login Session Lifetime

> Status: unknown

หนึ่ง task ครอบ vertical slice ทั้ง Admin และ Merchant User เพราะใช้ configuration pattern เดียวกัน

- [x] 1. เปลี่ยน fallback default และ committed config เป็น idle 24 ชั่วโมง / absolute 7 วันทั้งสองฝั่ง,
  เพิ่ม regression assertions สำหรับ expiry และยืนยัน cookie ยังไม่ persistent
     Satisfies: REQ-1.1, REQ-1.2, REQ-1.3, REQ-1.4, REQ-1.5, REQ-1.6, REQ-2.1, REQ-2.2, REQ-2.3, REQ-2.4.
      - deviations: none recorded — legacy corpus predates evidence v2 protocol (human checkpoint 2026-08-26)
