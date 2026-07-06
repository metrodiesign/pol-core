using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminMasterDataSeed : Migration
    {
        // Baseline HR org master data for the Admin console (Positions / Offices / Levels / Divisions).
        // Fixed GUIDs (deterministic — never NEWID in a migration) so every environment shares the same
        // Ids and the AdminAccount FKs stay stable. Ids are namespaced by table for readability:
        //   Positions a1…  Offices b2…  Levels c3…  Divisions d4…
        // Runtime CRUD (/master-data/*) manages these lists afterwards; rows are soft-deactivated via
        // IsActive, never hard-deleted (the AdminAccount FK is Restrict).
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO producer.Positions (Id, Code, Name, IsActive) VALUES
                  ('a1000000-0000-0000-0000-000000000001', 'ceo',               N'ประธานเจ้าหน้าที่บริหาร',            1),
                  ('a1000000-0000-0000-0000-000000000002', 'coo',               N'ประธานเจ้าหน้าที่ปฏิบัติการ',        1),
                  ('a1000000-0000-0000-0000-000000000003', 'cfo',               N'ประธานเจ้าหน้าที่การเงิน',           1),
                  ('a1000000-0000-0000-0000-000000000004', 'cto',               N'ประธานเจ้าหน้าที่เทคโนโลยีสารสนเทศ', 1),
                  ('a1000000-0000-0000-0000-000000000005', 'director',          N'ผู้อำนวยการ',                       1),
                  ('a1000000-0000-0000-0000-000000000006', 'deputy_director',   N'รองผู้อำนวยการ',                    1),
                  ('a1000000-0000-0000-0000-000000000007', 'manager',           N'ผู้จัดการ',                         1),
                  ('a1000000-0000-0000-0000-000000000008', 'assistant_manager', N'ผู้ช่วยผู้จัดการ',                   1),
                  ('a1000000-0000-0000-0000-000000000009', 'supervisor',        N'หัวหน้างาน',                        1),
                  ('a1000000-0000-0000-0000-00000000000a', 'senior_officer',    N'เจ้าหน้าที่อาวุโส',                 1),
                  ('a1000000-0000-0000-0000-00000000000b', 'officer',           N'เจ้าหน้าที่',                       1),
                  ('a1000000-0000-0000-0000-00000000000c', 'staff',             N'พนักงาน',                           1);
                """);

            migrationBuilder.Sql("""
                INSERT INTO producer.Offices (Id, Code, Name, IsActive) VALUES
                  ('b2000000-0000-0000-0000-000000000001', 'hq',        N'สำนักงานใหญ่',                     1),
                  ('b2000000-0000-0000-0000-000000000002', 'north',     N'สำนักงานภาคเหนือ',                 1),
                  ('b2000000-0000-0000-0000-000000000003', 'northeast', N'สำนักงานภาคตะวันออกเฉียงเหนือ',    1),
                  ('b2000000-0000-0000-0000-000000000004', 'central',   N'สำนักงานภาคกลาง',                  1),
                  ('b2000000-0000-0000-0000-000000000005', 'east',      N'สำนักงานภาคตะวันออก',              1),
                  ('b2000000-0000-0000-0000-000000000006', 'west',      N'สำนักงานภาคตะวันตก',               1),
                  ('b2000000-0000-0000-0000-000000000007', 'south',     N'สำนักงานภาคใต้',                   1),
                  ('b2000000-0000-0000-0000-000000000008', 'remote',    N'ปฏิบัติงานนอกสถานที่',             1);
                """);

            migrationBuilder.Sql("""
                INSERT INTO producer.Levels (Id, Code, Name, IsActive) VALUES
                  ('c3000000-0000-0000-0000-000000000001', 'level_1',  N'ระดับ 1',  1),
                  ('c3000000-0000-0000-0000-000000000002', 'level_2',  N'ระดับ 2',  1),
                  ('c3000000-0000-0000-0000-000000000003', 'level_3',  N'ระดับ 3',  1),
                  ('c3000000-0000-0000-0000-000000000004', 'level_4',  N'ระดับ 4',  1),
                  ('c3000000-0000-0000-0000-000000000005', 'level_5',  N'ระดับ 5',  1),
                  ('c3000000-0000-0000-0000-000000000006', 'level_6',  N'ระดับ 6',  1),
                  ('c3000000-0000-0000-0000-000000000007', 'level_7',  N'ระดับ 7',  1),
                  ('c3000000-0000-0000-0000-000000000008', 'level_8',  N'ระดับ 8',  1),
                  ('c3000000-0000-0000-0000-000000000009', 'level_9',  N'ระดับ 9',  1),
                  ('c3000000-0000-0000-0000-00000000000a', 'level_10', N'ระดับ 10', 1);
                """);

            migrationBuilder.Sql("""
                INSERT INTO producer.Divisions (Id, Code, Name, IsActive) VALUES
                  ('d4000000-0000-0000-0000-000000000001', 'executive',        N'สำนักผู้บริหาร',                    1),
                  ('d4000000-0000-0000-0000-000000000002', 'finance',          N'ฝ่ายการเงินและบัญชี',               1),
                  ('d4000000-0000-0000-0000-000000000003', 'technology',       N'ฝ่ายเทคโนโลยีสารสนเทศ',             1),
                  ('d4000000-0000-0000-0000-000000000004', 'operations',       N'ฝ่ายปฏิบัติการ',                    1),
                  ('d4000000-0000-0000-0000-000000000005', 'product',          N'ฝ่ายผลิตภัณฑ์',                     1),
                  ('d4000000-0000-0000-0000-000000000006', 'sales_marketing',  N'ฝ่ายขายและการตลาด',                 1),
                  ('d4000000-0000-0000-0000-000000000007', 'risk_compliance',  N'ฝ่ายบริหารความเสี่ยงและกำกับดูแล',  1),
                  ('d4000000-0000-0000-0000-000000000008', 'legal',            N'ฝ่ายกฎหมาย',                        1),
                  ('d4000000-0000-0000-0000-000000000009', 'hr',               N'ฝ่ายทรัพยากรบุคคล',                 1),
                  ('d4000000-0000-0000-0000-00000000000a', 'customer_service', N'ฝ่ายบริการลูกค้า',                  1);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove only the seeded rows (matched by their namespaced Id prefix). Fails by design if an
            // AdminAccount still references one (FK Restrict) — clear the reference before rolling back.
            migrationBuilder.Sql("DELETE FROM producer.Positions WHERE Id LIKE 'a1000000-0000-0000-0000-%';");
            migrationBuilder.Sql("DELETE FROM producer.Offices   WHERE Id LIKE 'b2000000-0000-0000-0000-%';");
            migrationBuilder.Sql("DELETE FROM producer.Levels    WHERE Id LIKE 'c3000000-0000-0000-0000-%';");
            migrationBuilder.Sql("DELETE FROM producer.Divisions WHERE Id LIKE 'd4000000-0000-0000-0000-%';");
        }
    }
}
