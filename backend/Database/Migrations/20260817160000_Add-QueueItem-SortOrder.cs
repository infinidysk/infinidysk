using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260817160000_Add-QueueItem-SortOrder")]
    public partial class AddQueueItemSortOrder : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SortOrder",
                table: "QueueItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql(
                """
                UPDATE QueueItems
                SET SortOrder = (
                    SELECT ranked.SortOrder
                    FROM (
                        SELECT Id, ROW_NUMBER() OVER (
                            PARTITION BY Priority
                            ORDER BY CreatedAt, Id
                        ) * 1024 AS SortOrder
                        FROM QueueItems
                    ) AS ranked
                    WHERE ranked.Id = QueueItems.Id
                );
                """);

            migrationBuilder.DropIndex(
                name: "IX_QueueItems_Priority_CreatedAt",
                table: "QueueItems");
            migrationBuilder.DropIndex(
                name: "IX_QueueItems_Category_Priority_CreatedAt",
                table: "QueueItems");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Priority_SortOrder",
                table: "QueueItems",
                columns: new[] { "Priority", "SortOrder" });
            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Category_Priority_SortOrder",
                table: "QueueItems",
                columns: new[] { "Category", "Priority", "SortOrder" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QueueItems_Priority_SortOrder",
                table: "QueueItems");
            migrationBuilder.DropIndex(
                name: "IX_QueueItems_Category_Priority_SortOrder",
                table: "QueueItems");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Priority_CreatedAt",
                table: "QueueItems",
                columns: new[] { "Priority", "CreatedAt" });
            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Category_Priority_CreatedAt",
                table: "QueueItems",
                columns: new[] { "Category", "Priority", "CreatedAt" });

            migrationBuilder.DropColumn(name: "SortOrder", table: "QueueItems");
        }
    }
}
