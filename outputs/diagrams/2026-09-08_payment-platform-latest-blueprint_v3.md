# POL Platform — Latest Blueprint ERD v3

> Source: `pol-platform-latest/01-business-overview.md` และ `04-data-model-and-migration.md`
> Scope: Conceptual Model ของ 7 modules แบ่ง 6 ภาพ ไม่ใช่ Physical DDL
> Generated: 2026-09-09

> ขอบเขตรุ่นแรก: ภาพนี้เป็นแผนที่แนวคิดทั้ง scope ไม่บังคับสร้างทุกตารางหรือฟีเจอร์พร้อมกัน ใช้ [แบบเรียบง่าย §8](../../docs/proposals/payment-platform-redesign-2026-09-08/latest-blueprint.md#8-โครงสร้างที่ทีมเล็กดูแลได้) เลือกส่วนที่ทำจริง และไม่เก็บ state ซ้ำกับ OAuth component ที่เลือก

| § | Diagram | เจ้าของหลัก |
|---|---|---|
| 1 | Account และ Token State | Account |
| 2 | Registration และประวัติ | Account |
| 3 | Access และสิทธิ์ส่วนกลาง | Access |
| 4 | Merchant และ Provider Configuration | Merchant |
| 5 | Order และ Checkout | Order + Checkout |
| 6 | Transaction และ Notification | Transaction + Notification |

---

## 1. Account และ Token State

Account เป็น kill switch กลาง Human ใช้ LoginAccount/Session ส่วน SYSTEM ใช้ Client ที่ปิดแยกได้

```mermaid
erDiagram
    ACCOUNT ||--o| EMPLOYEE : "profiles EMPLOYEE"
    ACCOUNT ||--o| AGENT : "profiles AGENT"
    ACCOUNT ||--o{ LOGIN_ACCOUNT : "authenticates human"
    ACCOUNT ||--o{ SYSTEM_CLIENT : "registers clients"
    SYSTEM_CLIENT ||--o{ SYSTEM_CLIENT_KEY : "registers public keys"
    ACCOUNT ||--o{ HUMAN_SESSION : "owns sessions"
    HUMAN_SESSION ||--o{ REFRESH_TOKEN : "rotates family"

    ACCOUNT {
        uuid AccountId PK
        string AccountType
        string DisplayName
        string Status
        bigint AuthorizationVersion
        datetime CreatedAt
        datetime UpdatedAt
    }
    EMPLOYEE {
        uuid AccountId PK, FK
        string EmployeeCode
        string DepartmentCode
        json Metadata
    }
    AGENT {
        uuid AccountId PK, FK
        uuid SaleId FK, UK
        json Metadata
    }
    LOGIN_ACCOUNT {
        uuid LoginAccountId PK
        uuid AccountId FK
        string Provider
        uuid TenantId
        uuid ExternalUserId
        string Email
        string DisplayName
        datetime LastLoginAt
    }
    SYSTEM_CLIENT {
        uuid SystemClientId PK
        uuid AccountId FK
        string ClientId UK
        uuid MerchantId FK
        string Environment
        string Status
        string AllowedGrantTypes
    }
    SYSTEM_CLIENT_KEY {
        uuid SystemClientKeyId PK
        uuid SystemClientId FK
        string KeyId
        string Algorithm
        string PublicKey
        datetime ValidFrom
        datetime ValidUntil
        string Status
    }
    HUMAN_SESSION {
        uuid HumanSessionId PK
        uuid AccountId FK
        string ClientId
        binary TokenHash
        datetime ExpiresAt
        datetime RevokedAt
    }
    REFRESH_TOKEN {
        uuid RefreshTokenId PK
        uuid HumanSessionId FK
        uuid FamilyId
        binary TokenHash
        datetime ExpiresAt
        datetime RevokedAt
    }
```

| Constraint | กติกา |
|---|---|
| Profile | EMPLOYEE มี Employee และ AGENT มี Agent หนึ่งแถวต่อ Account ส่วน SYSTEM ใช้ Client bindings ตามจำนวนที่เลือกใน spec ไม่สร้าง Human profile |
| Human identity | `(Provider, TenantId, ExternalUserId)` unique Email ไม่ใช่ identity key |
| Display name | Accounts.DisplayName เป็นชื่อใช้งาน LoginAccounts.DisplayName/Email เป็นค่า provider-observed ไม่ใช่ current profile อีกชุด |
| Human token | เก็บ opaque token hash, family, rotation/reuse และ revoke state ไม่เก็บ raw token |
| Kill switch | Account.Status ปิดทั้งบัญชี SystemClient.Status ปิดเฉพาะ Client ทั้งคู่ต้อง active |
| Environment | Dev/UAT/Prod ไม่ใช้ credential หรือ issuer boundary ปะปนโดยไม่ตรวจ |
| SYSTEM key | เมื่อเลือก `private_key_jwt` Platform เก็บ public key เท่านั้น ClientKey จึงยังเป็น optional ก่อนมติใน spec |

---

## 2. Registration และประวัติ

ผู้สมัครและ session ก่อนมี Account แยกจากประวัติการยื่นที่ต้องเก็บไว้

```mermaid
erDiagram
    AGENT_REGISTRATION ||--o{ AGENT_REGISTRATION_ATTEMPT : "keeps submissions"
    AGENT_REGISTRATION o|--o| ACCOUNT : "creates when approved"
    AGENT_REGISTRATION o|--o{ REGISTRATION_SESSION : "authorizes applicant"
    AGENT_REGISTRATION o|--o{ CONTACT_VERIFICATION : "verifies contact"
    REGISTRATION_SESSION o|--o{ CONTACT_VERIFICATION : "starts verification"

    AGENT_REGISTRATION {
        uuid RegistrationId PK
        string Provider
        uuid TenantId
        uuid ExternalUserId
        uuid MerchantId FK
        uuid ProposedSaleId FK "nullable in draft"
        string Email
        string PhoneNumber
        string Status
        uuid CurrentAttemptId FK "nullable before submit"
        int CurrentAttemptNo
        uuid ApprovedAccountId FK "nullable before approval"
        bigint Version
    }
    AGENT_REGISTRATION_ATTEMPT {
        uuid AttemptId PK
        uuid RegistrationId FK
        int AttemptNo
        json SubmittedData
        string Status
        datetime SubmittedAt
        uuid ReviewedByAccountId FK "nullable while pending"
        datetime ReviewedAt "nullable while pending"
        string RejectionReason
        string InternalReviewNote
    }
    REGISTRATION_SESSION {
        uuid RegistrationSessionId PK
        uuid RegistrationId FK "nullable before registration"
        binary TokenHash
        string Provider
        uuid TenantId
        uuid ExternalUserId
        uuid MerchantId FK
        datetime ExpiresAt
    }
    CONTACT_VERIFICATION {
        uuid ContactVerificationId PK
        uuid RegistrationId FK "nullable"
        uuid RegistrationSessionId FK "nullable"
        string Recipient
        string Channel
        binary CodeHash
        datetime ExpiresAt
        int AttemptCount
    }
```

| Constraint | กติกา |
|---|---|
| Registration identity | Unique ต่อ stable identity หรือ identity+Merchant ยังเป็น decision ห้ามล็อก DDL ก่อนสรุป |
| Registration session | ผูก verified identity+Merchant และอ้าง Registration ได้เมื่อมีแล้ว ไม่ใช่ Business Account |
| Contact verification | อ้าง Registration หรือ RegistrationSession อย่างใดอย่างหนึ่งและใช้เฉพาะเมื่อเลือก OTP |
| Attempt | `(RegistrationId, AttemptNo)` unique มี current pending attempt ได้หนึ่งรอบและ decision เติมได้ครั้งเดียว |
| Approval | Account/Login/Agent/MerchantAccess/decision/Outbox commit พร้อมกัน Reject ไม่สร้าง Account |

---

## 3. Access และสิทธิ์ส่วนกลาง

DataScope อยู่ MerchantAccess ส่วน PlatformAccess แยกสำหรับ Employee

```mermaid
erDiagram
    ACCOUNT ||--o{ MERCHANT_ACCESS : "receives"
    MERCHANT_ACCESS ||--o{ ACCESS_ROLE : "assigns roles"
    ROLE ||--o{ ACCESS_ROLE : "is assigned"
    ROLE ||--o{ ROLE_PERMISSION : "contains"
    PERMISSION ||--o{ ROLE_PERMISSION : "is granted"
    MERCHANT_ACCESS ||--o{ BRANCH_ACCESS : "assigns branches"
    EMPLOYEE ||--o| PLATFORM_ACCESS : "administers platform"
    PLATFORM_ACCESS }o--o{ ROLE : "assigns platform roles"
    SYSTEM_CLIENT ||--o{ SYSTEM_SCOPE : "allows APIs"
    MERCHANT_ACCESS o|--o{ METHOD_ACCESS : "allows human methods"
    SYSTEM_CLIENT o|--o{ METHOD_ACCESS : "allows system methods"

    MERCHANT_ACCESS {
        uuid MerchantAccessId PK
        uuid AccountId FK
        uuid MerchantId FK
        string DataScope
        string Status
        bigint Version
    }
    ACCESS_ROLE {
        uuid MerchantAccessId PK, FK
        uuid RoleId PK, FK
    }
    BRANCH_ACCESS {
        uuid MerchantAccessId PK, FK
        uuid BranchId PK, FK
    }
    ROLE {
        uuid RoleId PK
        string RoleCode
        string AllowedAccountTypes
        string Scope
        string Status
    }
    PERMISSION {
        uuid PermissionId PK
        string PermissionCode UK
    }
    ROLE_PERMISSION {
        uuid RoleId PK, FK
        uuid PermissionId PK, FK
    }
    PLATFORM_ACCESS {
        uuid EmployeeAccountId PK, FK
        string Status
    }
    SYSTEM_SCOPE {
        uuid SystemClientId PK, FK
        string ScopeCode PK
    }
    METHOD_ACCESS {
        uuid MethodAccessId PK
        uuid MerchantAccessId FK "nullable"
        uuid SystemClientId FK "nullable"
        string MethodCode
    }
```

| Constraint | กติกา |
|---|---|
| MerchantAccess | DataScope อยู่ที่ entity นี้ จำนวน access context ต่อ Account+Merchant ยังต้องตัดสิน |
| Access children | AccessRoles และ BranchAccess อ้าง MerchantAccess เดียว สาขาต้องอยู่ Merchant เดียวกัน |
| PlatformAccess | ใช้เฉพาะ Employee และอ้าง Role catalog แบบ many-to-many ไม่เก็บ role list ซ้ำเป็น string |
| SYSTEM | SystemScopes/MethodAccess จำกัด Client/Access ไม่ทำให้ SYSTEM เห็นทุกข้อมูลโดยปริยาย |
| MethodAccess | ผูก MerchantAccess หรือ SystemClient อย่างใดอย่างหนึ่ง ไม่ผูกทั้งสองพร้อมกัน |

---

## 4. Merchant และ Provider Configuration

Merchant เป็นเจ้าของ Sale/Branch และค่าที่ Transaction นำไปตรึงเมื่อเริ่มจ่าย

```mermaid
erDiagram
    MERCHANT ||--o{ BRANCH : "owns"
    MERCHANT ||--o{ SALE : "owns"
    BRANCH ||--o{ SALE : "is home of"
    SALE ||--o| AGENT : "is assigned to"
    PROVIDER ||--o{ PROVIDER_METHOD : "offers"
    MERCHANT ||--o{ PROVIDER_ACCOUNT : "configures"
    PROVIDER ||--o{ PROVIDER_ACCOUNT : "serves"
    PROVIDER_ACCOUNT ||--o{ CREDENTIAL_VERSION : "rotates API credential"
    MERCHANT ||--o{ PAYMENT_SETTING_REQUEST : "governs changes"

    MERCHANT {
        uuid MerchantId PK
        string MerchantCode UK
        string Name
        string BusinessType
        string Status
        int ConfigurationVersion
        json Metadata
    }
    BRANCH {
        uuid BranchId PK
        uuid MerchantId FK
        string BranchCode
        string Name
        string Status
    }
    SALE {
        uuid SaleId PK
        uuid MerchantId FK
        uuid BranchId FK
        string SaleCode
        string Name
        string Status
    }
    PROVIDER {
        uuid ProviderId PK
        string ProviderCode UK
        string Status
    }
    PROVIDER_METHOD {
        uuid ProviderId PK, FK
        string MethodCode PK
        string OfferedStatus
        string VerifiedStatus
    }
    PROVIDER_ACCOUNT {
        uuid ProviderAccountId PK
        uuid MerchantId FK
        uuid ProviderId FK
        string Environment
        boolean Enabled
        json Configuration
    }
    CREDENTIAL_VERSION {
        uuid CredentialVersionId PK
        uuid ProviderAccountId FK
        string SecretStoreReference
        string KeyId
        int Version
        string Validity
        string Status
    }
    PAYMENT_SETTING_REQUEST {
        uuid SettingRequestId PK
        uuid MerchantId FK
        int BaseVersion
        json ProposedConfiguration
        string Status
        uuid MakerId FK
        uuid CheckerId FK
        string Reason
    }
```

| Constraint | กติกา |
|---|---|
| Master codes | `(MerchantId, BranchCode)` และ `(MerchantId, SaleCode)` unique |
| Agent sale | Agents.SaleId unique Sale มี Home Branch หนึ่งแห่งและอยู่ Merchant เดียวกัน |
| Provider readiness | Offered, adapter Verified และ Merchant Enabled ต้องผ่านครบก่อนแสดงช่องทาง |
| Credential | Secret อยู่ protected store ส่วน configuration เก็บ reference/version เท่านั้น |
| Maker-checker | MakerId ต่าง CheckerId และค่าที่อนุมัติใหม่ไม่เปลี่ยน Transaction เก่า |
| Key lifecycle | API credential กับ Webhook signature key อาจต่างกัน ต้องกำหนดตาม provider contract |

---

## 5. Order และ Checkout

Order เก็บสิ่งที่ขายและสถานะเงิน Checkout เก็บลิงก์และ capability โดยไม่มี Payment/CheckoutSession entity

```mermaid
erDiagram
    MERCHANT ||--o{ ORDER : "owns"
    SALE o|--o{ ORDER : "owns business work"
    BRANCH o|--o{ ORDER : "snapshots owner branch"
    ORDER ||--|{ ORDER_ITEM : "contains"
    ORDER ||--o{ PAYMENT_LINK : "issues"

    ORDER {
        uuid OrderId PK
        uuid MerchantId FK
        string OrderNo
        string BusinessType
        char Currency
        decimal SubtotalAmount
        decimal OrderDiscountAmount
        decimal OrderChargeAmount
        decimal TotalAmount
        string OrderStatus
        string PaymentStatus
        uuid SuccessfulTransactionId FK "nullable"
        uuid CreatedByAccountId FK
        uuid OwnerSaleId FK "nullable"
        uuid OwnerBranchIdAtCreation FK "nullable"
        string SaleCodeSnapshot
        string BranchCodeSnapshot
        json Metadata
        bigint Version
        datetime ExpiresAt
    }
    ORDER_ITEM {
        uuid OrderItemId PK
        uuid OrderId FK
        int ItemNo
        string ProductReference
        string ProductCode
        string ProductName
        decimal Quantity
        decimal UnitPrice
        decimal DiscountAmount
        decimal TaxAmount
        decimal LineAmount
        json Metadata
    }
    PAYMENT_LINK {
        uuid PaymentLinkId PK
        uuid OrderId FK
        binary TokenHash UK
        string Status
        datetime ExpiresAt
        datetime CreatedAt
        datetime RevokedAt
    }
```

| Constraint | กติกา |
|---|---|
| Order items | `(OrderId, ItemNo)` unique ทุก item อยู่ currency เดียวกับ Order |
| Amount | `Subtotal = SUM(LineAmount)` และ `Total = Subtotal - OrderDiscount + OrderCharge` |
| Owner | CreatedBy ต้องมีสิทธิ์ OwnerSale/Branch nullable ได้เมื่อ Business Handler อนุญาต ไม่สร้าง Sale ปลอม |
| Immutable issue | OPEN/CANCELLED ไม่แก้ยอด/items BusinessType/ownership ที่ตรึงแล้ว |
| Payment state | OrderStatus แยกจาก PaymentStatus และไม่มี Payment entity |
| PaymentLink | ประวัติหลายลิงก์ได้ หนึ่ง active link เป็นข้อเสนอรุ่นแรก ไม่สร้าง Order/Transaction ตอน reissue |
| Token replay | Raw token อ่านจาก hash ไม่ได้ replay ผลเดิมได้เฉพาะ encrypted idempotent result ที่มี TTL |
| Multiple tabs | Cookie/confirm ผูก OrderId+Version และ recheck revoked/expired link ทุกครั้ง |

---

## 6. Transaction และ Notification

Transaction คือหนึ่งการเริ่มจ่าย Notification ส่งผลโดยไม่เปลี่ยน Order หรือ Registration ย้อนกลับ

```mermaid
erDiagram
    ORDER ||--o{ TRANSACTION : "has attempts"
    TRANSACTION ||--|{ TRANSACTION_EVENT : "keeps history"
    TEMPLATE_VERSION ||--o{ NOTIFICATION_DELIVERY : "renders"
    NOTIFICATION ||--|{ NOTIFICATION_DELIVERY : "sends"
    NOTIFICATION_DELIVERY ||--o{ DELIVERY_ATTEMPT : "retries"
    MERCHANT ||--o| BUSINESS_EVENT_ENDPOINT : "configures"

    TRANSACTION {
        uuid TransactionId PK
        uuid OrderId FK
        uuid MerchantId FK
        string TransactionNo
        int AttemptNo
        decimal Amount
        char Currency
        string PaymentMethod
        uuid ProviderAccountId FK
        string Environment
        uuid CredentialVersionId FK
        string Status
        string ProviderStatus
        string ProviderReference "nullable until PSP responds"
        string RedirectUrl
        json OrderSnapshot
        string SnapshotProvenance
        json ProviderMetadata
        boolean NeedsReview
        bigint Version
    }
    TRANSACTION_EVENT {
        uuid TransactionEventId PK
        uuid TransactionId FK
        string EventType
        string Source
        string ProviderEventReference
        json SafeDetails
        datetime OccurredAt
    }
    NOTIFICATION {
        uuid NotificationId PK
        uuid SourceEventId
        string Type
        uuid MerchantId FK
        uuid RegistrationAttemptId FK "nullable"
        uuid OrderId FK "nullable"
        uuid AccountId FK "nullable"
    }
    NOTIFICATION_DELIVERY {
        uuid DeliveryId PK
        uuid NotificationId FK
        string Channel
        string RecipientSnapshot
        uuid TemplateVersionId FK
        string Status
        string ProviderReference
        datetime NextAttemptAt
    }
    DELIVERY_ATTEMPT {
        uuid DeliveryAttemptId PK
        uuid DeliveryId FK
        int AttemptNo
        datetime RequestedAt
        datetime FinishedAt
        string Status
        string SafeErrorCode
    }
    TEMPLATE_VERSION {
        uuid TemplateVersionId PK
        string TemplateCode
        int Version
        string Channel
        string Subject
        string Body
        string AllowedVariables
    }
    BUSINESS_EVENT_ENDPOINT {
        uuid MerchantId PK, FK
        string HttpsUrl
        string Status
        string SigningKeyReference
        int ConfigurationVersion
    }
```

| Constraint | กติกา |
|---|---|
| Attempt | `(OrderId, AttemptNo)` unique และ Order มี potentially-chargeable Transaction พร้อมกันไม่เกินหนึ่ง |
| Provider reference | Scope uniqueness ด้วย ProviderAccount+Environment+Reference ตาม contract จริง |
| Result path | Webhook, browser verification และ worker ใช้ verifier เดียว Browser Return ไม่ใช่ผลสำเร็จ |
| Success | Transaction SUCCEEDED, Order PAID, SuccessfulTransactionId และ Outbox commit สอดคล้องกัน |
| Duplicate money | ห้าม unique SUCCEEDED ต่อ Order จนเก็บเงินจริงซ้ำไม่ได้ ให้ NeedsReview และไม่ส่งธุรกิจซ้ำ |
| Snapshot | ไม่มี TransactionItems OrderSnapshot immutable และบอก provenance หากประกอบตอน migration |
| Notification | SourceEventId กันงานซ้ำ Registration decision มี EMAIL+SMS deliveries แยกกัน |
| Delivery | Retry แยก channel/recipient และเก็บ attempt append-only โดยไม่ย้อนผล source |
| Business webhook | หนึ่ง endpoint ต่อ Merchant ยังเป็นข้อเสนอ ต้องตรวจ SSRF และลงลายมือชื่อ |

Platform Core เป็นเจ้าของ Audit, Inbox, Outbox และ IdempotencyRecords เชิงกลไก แต่ไม่เป็นเจ้าของ business state ข้างต้น

**Mapping →** [Latest blueprint](../../docs/proposals/payment-platform-redesign-2026-09-08/latest-blueprint.md)

## Notes

Registration uniqueness, จำนวน MerchantAccess context, `private_key_jwt`, Employee SELF/BRANCH, OTP, active-link cardinality และ business endpoint cardinality ยังต้องตัดสินใน spec

**Render**: GitHub / Obsidian / VS Code Mermaid
