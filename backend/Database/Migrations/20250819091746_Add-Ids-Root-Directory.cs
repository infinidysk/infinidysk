using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddIdsRootDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "DavItems",
                columns: new[] { "Id", "ParentId", "Name", "FileSize", "Type" },
#pragma warning disable CA1814 // EF Core migrationBuilder.InsertData requires the object[,] values shape
                values: new object[,]
                #pragma warning restore CA1814
                {
                    {
                        // Root
                        Guid.Parse("00000000-0000-0000-0000-000000000004"),
                        Guid.Parse("00000000-0000-0000-0000-000000000000"),
                        ".ids",
                        null,
                        5, // IdsRoot
                    },
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "DavItems",
                keyColumn: "Id",
                keyValue: Guid.Parse("00000000-0000-0000-0000-000000000004")
            );
        }
    }
}
