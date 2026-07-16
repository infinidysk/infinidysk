using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Utils;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class EnsureJellyfinWebhookTokenExists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "ConfigItems",
                columns: new[] { "ConfigName", "ConfigValue" },
                values: new object[,]
                {
                    {
                        "jellyfin.webhook-token",
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
