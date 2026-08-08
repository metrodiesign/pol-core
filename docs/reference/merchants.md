# Merchants Module Reference

> As-built 2026-08-07. Covers merchant profile, merchant-user OIDC BFF, registration/KYC and commerce actor binding.

## Identity and session

- OIDC BFF providers: Google and Microsoft Entra
- Browser holds opaque `__Host-mch_session` cookie; no bearer/id token in SPA
- CSRF double-submit on merchant-user mutations
- session family rotation, reuse detection, revoke one/all and bounded pruning
- request resolves active user, merchant, SaleCode and Active-only IAM permissions
- unknown/not-configured provider `404`; absent/expired session `401`; permission denial `403`

Frontend never supplies merchant ID or SaleCode for commerce operations.

## Registration and KYC

Anonymous registration uses signed Data Protection ticket. User submits scalar identity fields plus optional multipart
`kycPhoto`:

- maximum 2 MiB
- allowlisted media type + magic bytes
- deterministic private staged object keyed by `KycOperationId`
- staging returns `(Key, CreatedNew)`; same operation + same bytes is idempotent, different bytes is rejected
- DB stores key only
- lifecycle outbox commits/replaces/deletes object idempotently
- failed registration discards staging only when current attempt created the object
- orphan staging TTL is 24 hours; `PhotoStagingPruneService` sweeps every hour after a 5-minute startup delay
- omission keeps existing key

Public/API/history/log surfaces never expose object key, filesystem path, credentials or unnecessary PII.
Admin approve/reject uses authorization lease and concurrency checks. Registration notices live in raw
`merch.RegistrationNotices`.

## Merchant profile and provisioning

Merchant business/search fields are scalar. Optional extension uses typed metadata allowlist. Provisioning is idempotent
saga across DB + encrypted vault, with compensation/replay codec and closed outbox contract. PSP credential is write-only,
encrypted and never returned/logged. Baseline synthetic merchant has disabled PSP connection and no credential/PII.

## Commerce routes

Merchant user can:

- query live Products with server-bound SaleCode
- create/mutate/read Carts
- create Order directly from Cart
- create/read/redirect Payment session under IAM permissions
- read/cancel own Orders and resend summary

No Checkout or policy route exists. Full cutover mapping:
`.ai/specs/merchant-commerce-erd-reset/FE-MIGRATION.md`.

Production single-host deployment persists local staged/final photos in named volume
`merchant-user-photos:/app/merchant-user-photos`. A shared object-store adapter is still required for
horizontal or multi-host deployment.

## Persistence boundaries

- Merchant identity/session/outbox: `MerchantUserDbContext`
- Merchant profile/vault/Carts/Orders/Payments: `MerchantRuntimeDbContext`
- global query filters deny unbound/wrong merchant
- sealed write guard rechecks tenant key and operation authority
- `PolDbContext` is migration owner only
