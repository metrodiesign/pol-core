# Changelog

## Unreleased

### Changed

- Extended Admin and Merchant User server-side login sessions to a 24-hour idle timeout and a 7-day absolute lifetime while retaining browser-session cookies.
- Rebuilt merchant-commerce persistence from approved ERD with SQL Server 2025 baseline.
- Replaced Checkout flow with atomic direct Cart-to-Order API.
- Published generic `productCode`/`variantCode` Cart and Order contracts.
- Serialized payment lifecycle and retired policy surfaces.
- Added guarded fresh-database reset, staging rehearsal and release evidence gates.
