# Changelog

## Unreleased

### Changed

- Rebuilt merchant-commerce persistence from approved ERD with SQL Server 2025 baseline.
- Replaced Checkout flow with atomic direct Cart-to-Order API.
- Published generic `productCode`/`variantCode` Cart and Order contracts.
- Serialized payment lifecycle and retired policy surfaces.
- Added guarded fresh-database reset, staging rehearsal and release evidence gates.
