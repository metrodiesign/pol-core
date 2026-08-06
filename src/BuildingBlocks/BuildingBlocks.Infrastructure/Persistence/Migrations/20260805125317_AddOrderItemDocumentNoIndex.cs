using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingBlocks.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// products-external-source-of-truth REQ-5.15: the index <c>DocumentSaleProbe</c> seeks. The sold-check
    /// asks about every document of one request in a single read, and <c>DocumentNo</c> is its only IN
    /// predicate — <c>OrderId</c> and <c>ProductGroup</c> ride along as INCLUDE columns so the read never
    /// leaves the index. NOT unique (design decision #4): a cancelled order and the order that actually sells
    /// the document may both hold the same number, so uniqueness would fire on a legitimate INSERT.
    /// </summary>
    public partial class AddOrderItemDocumentNoIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_DocumentNo",
                schema: "shop",
                table: "OrderItems",
                column: "DocumentNo")
                .Annotation("SqlServer:Include", new[] { "OrderId", "ProductGroup" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OrderItems_DocumentNo",
                schema: "shop",
                table: "OrderItems");
        }
    }
}
