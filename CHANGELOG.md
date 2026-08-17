# Changelog

## Unreleased

### Changed

- Standardized local backend API, OIDC callback, Scalar and development tooling on `https://localhost:5001` (`local-api-port-5001` REQ-1).
- Standardized local customer, admin and merchant SPA origins on HTTPS ports `3000`, `3001` and `3002` (`local-api-port-5001` REQ-2).
- Extended Admin and Merchant User server-side login sessions to a 24-hour idle timeout and a 7-day absolute lifetime while retaining browser-session cookies.
- Rebuilt merchant-commerce persistence from approved ERD with SQL Server 2025 baseline.
- Replaced Checkout flow with atomic direct Cart-to-Order API.
- Published generic `productCode`/`variantCode` Cart and Order contracts.
- Serialized payment lifecycle and retired policy surfaces.
- Added guarded fresh-database reset, staging rehearsal and release evidence gates.
