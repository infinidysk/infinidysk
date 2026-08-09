using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Utils;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class EnsureStrmKeyExists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "ConfigItems",
                columns: new[] { "ConfigName", "ConfigValue" },
#pragma warning disable CA1814 // EF Core migrationBuilder.InsertData requires the object[,] values shape
                values: new object[,]
                #pragma warning restore CA1814
                {
                    {
                        "api.strm-key",
                        GuidUtil.GenerateSecureGuid().ToString("N")
                    },
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally left blank
        }
    }
}
