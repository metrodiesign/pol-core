# Requirements: System Completion (ecommerce breadth + customer notification + reporting)
> Status: approved 2026-06-23 (autonomous, AFK — /spec-quick style, no human gate)

## Overview
เติมฟีเจอร์ที่ reference (`docs/reference/payment-orchestration-modules.md`) นิยามไว้แต่ยังไม่ build บน core E2E loop ที่ทำงานแล้ว (Products->Cart->Checkout->Orders->Payments->PaymentPaid->Paid). สามกลุ่ม: (1) Cart management ครบ (domain รองรับแล้ว — ขาด app+host), (2) Order summary link + TTL + customer notification แบบ background ผ่าน transactional outbox ที่มีอยู่ (reference บรรทัด 34/43), (3) reconciliation = reporting (บรรทัด 55). ทุกอย่างยังคง multi-tenant (ITenantScoped + RLS) และ captive.

Product decisions (locked autonomously, AFK): notification ขี่ outbox + OutboxDispatcher เดิม (retry/backoff/DLQ = MaxAttempts ที่มีอยู่; ไม่เพิ่ม message broker ภายนอก); การส่งจริงผ่าน `INotificationSender` port + default impl ที่ log (provider email/SMS จริง wire ภายหลังผ่าน config); summary-link TTL default 72 ชม.; reconciliation = สรุปต่อ tenant (นับ + รวมยอด Paid vs PendingPayment).

## REQ-1: Cart management (read + edit + clear)
**User Story:** As a producer (Merchant Console), I want to view, adjust, and clear my cart, so that I can correct selections before checkout.
**Acceptance Criteria (EARS):**
- 1.1 WHEN a producer requests a cart by id THE SYSTEM SHALL return its lines + subtotal, or 404 if no cart with that id exists in the bound tenant.
- 1.2 WHEN a producer removes a product line from an open cart THE SYSTEM SHALL drop that line and leave the rest unchanged.
- 1.3 WHEN a producer sets a line's quantity to a positive value on an open cart THE SYSTEM SHALL update that line's quantity.
- 1.4 IF a quantity is set to zero or negative THEN THE SYSTEM SHALL reject it (400) and change nothing.
- 1.5 WHEN a producer clears an open cart THE SYSTEM SHALL remove all lines.
- 1.6 IF any edit targets a cart that is not Open (already CheckedOut) THEN THE SYSTEM SHALL reject it (409) and change nothing.
- 1.7 THE SYSTEM SHALL scope every cart operation to the bound tenant (ITenantScoped + RLS).

## REQ-2: Order summary link with TTL
**User Story:** As a customer, I want a time-limited link to my order summary, so that I can review and pay securely.
**Acceptance Criteria (EARS):**
- 2.1 WHEN an order is created THE SYSTEM SHALL issue an opaque summary token with an expiry (default 72h from creation).
- 2.2 WHEN the summary is requested with a valid, unexpired token THE SYSTEM SHALL return the order summary.
- 2.3 IF the token is unknown THEN THE SYSTEM SHALL respond 404.
- 2.4 IF the token has expired THEN THE SYSTEM SHALL respond 410 Gone and SHALL NOT return the summary.
- 2.5 WHEN a producer requests a resend THE SYSTEM SHALL issue a NEW token, extend the expiry, and invalidate the old token.
- 2.6 WHEN a producer requests a resend AND a notification recipient was captured for the order THE SYSTEM SHALL enqueue a customer notification carrying the NEW token, in the SAME unit of work as the rotation; the order retains the recipient captured at creation/checkout for this purpose (REQ-3).

## REQ-3: Customer notification (background, via outbox)
**User Story:** As the platform, I want the customer notified with their summary link in the background, so that order creation is not blocked on delivery.
**Acceptance Criteria (EARS):**
- 3.1 WHEN an order is created THE SYSTEM SHALL enqueue a customer-notification integration event in the SAME unit of work as the order (transactional outbox).
- 3.2 THE SYSTEM SHALL dispatch the notification from the background worker, never inline on the create request.
- 3.3 WHEN the worker dispatches a notification THE SYSTEM SHALL send it through an `INotificationSender` port carrying the recipient + summary link.
- 3.4 IF a send fails THEN THE SYSTEM SHALL rely on the existing outbox retry/backoff and leave the message for DLQ review after the max attempts (no message lost, no inline failure).
- 3.5 THE SYSTEM SHALL NOT log the recipient's PII beyond non-secret identifiers.

## REQ-4: Reconciliation reporting
**User Story:** As a producer/admin, I want a reconciliation summary, so that I can compare orders against payments.
**Acceptance Criteria (EARS):**
- 4.1 WHEN a reconciliation summary is requested for the bound tenant THE SYSTEM SHALL return counts and total amounts of orders grouped by status (Paid, PendingPayment, Cancelled).
- 4.2 THE SYSTEM SHALL scope the report to the bound tenant only (RLS).
- 4.3 THE SYSTEM SHALL compute totals per currency (never sum across currencies).

## REQ-5: Checkout confirms into an order (the keystone wire)
**User Story:** As a producer, I want confirming a checkout to create the order (and notify the customer), so that the cart -> checkout -> order -> pay flow runs end to end.
**Acceptance Criteria (EARS):**
- 5.1 WHEN a checkout is confirmed THE SYSTEM SHALL emit a `CheckoutConfirmed` integration event in the SAME unit of work as the confirmation (transactional outbox), carrying the agreed amount + the notification recipient.
- 5.2 WHEN the worker consumes `CheckoutConfirmed` THE SYSTEM SHALL create an order awaiting payment for that tenant with the agreed amount.
- 5.3 IF an order already exists for the checkout session THEN THE SYSTEM SHALL NOT create a second one (idempotent under at-least-once delivery).
- 5.4 WHEN the order is created from a confirmed checkout that carried a recipient THE SYSTEM SHALL enqueue the customer notification (REQ-3).
- 5.5 THE SYSTEM SHALL capture an optional notification recipient at checkout start.

## Edge Cases & Open Questions
- Real email/SMS provider for `INotificationSender` is a deferred infra choice; this slice ships a logging default impl (swappable via DI). The port + outbox flow are complete and testable now.
- Reconciliation is a read-model over Orders; a cross-module reconciliation against Payments rows (PSP settlement files) is out of scope (reference: settlement is outside the platform).
