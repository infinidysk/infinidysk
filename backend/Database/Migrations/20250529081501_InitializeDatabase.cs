using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database.Models;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitializeDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DavItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DavItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DavItems_DavItems_ParentId",
                        column: x => x.ParentId,
                        principalTable: "DavItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HistoryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    JobName = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    DownloadStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalSegmentBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DownloadTimeSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    FailMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueueItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    JobName = table.Column<string>(type: "TEXT", nullable: false),
                    NzbContents = table.Column<string>(type: "TEXT", nullable: false),
                    NzbFileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalSegmentBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    PostProcessing = table.Column<int>(type: "INTEGER", nullable: false),
                    PauseUntil = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DavNzbFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SegmentIds = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DavNzbFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DavNzbFiles_DavItems_Id",
                        column: x => x.Id,
                        principalTable: "DavItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DavRarFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RarParts = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DavRarFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DavRarFiles_DavItems_Id",
                        column: x => x.Id,
                        principalTable: "DavItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_ParentId_Name",
                table: "DavItems",
                columns: new[] { "ParentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_Category",
                table: "HistoryItems",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_Category_CreatedAt",
                table: "HistoryItems",
                columns: new[] { "Category", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_CreatedAt",
                table: "HistoryItems",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Category",
                table: "QueueItems",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Category_Priority_CreatedAt",
                table: "QueueItems",
                columns: new[] { "Category", "Priority", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_CreatedAt",
                table: "QueueItems",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_FileName",
                table: "QueueItems",
                column: "FileName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Priority",
                table: "QueueItems",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Priority_CreatedAt",
                table: "QueueItems",
                columns: new[] { "Priority", "CreatedAt" });

            migrationBuilder.InsertData(
                table: "DavItems",
                columns: new[] { "Id", "ParentId", "Name", "FileSize", "Type" },
#pragma warning disable CA1814 // EF Core migrationBuilder.InsertData requires the object[,] values shape
                values: new object[,]
                #pragma warning restore CA1814
                {
                    {
                        // Root
                        Guid.Parse("00000000-0000-0000-0000-000000000000"),
                        null,
                        "/",
                        null,
                        1, // Directory
                    },
                    {
                        // NzbFolder
                        Guid.Parse("00000000-0000-0000-0000-000000000001"),
                        Guid.Parse("00000000-0000-0000-0000-000000000000"),
                        "nzbs",
                        null,
                        1, // Directory
                    },
                    {
                        // ContentFolder
                        DavItem.ContentFolder.Id,
                        Guid.Parse("00000000-0000-0000-0000-000000000000"),
                        "content",
                        null,
                        1, // Directory
                    },
                    {
                        // SymlinkFolder
                        DavItem.SymlinkFolder.Id,
                        Guid.Parse("00000000-0000-0000-0000-000000000000"),
                        "completed-symlinks",
                        null,
                        2, // Symlink-Root
                    }
                }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DavNzbFiles");

            migrationBuilder.DropTable(
                name: "DavRarFiles");

            migrationBuilder.DropTable(
                name: "HistoryItems");

            migrationBuilder.DropTable(
                name: "QueueItems");

            migrationBuilder.DropTable(
                name: "DavItems");
        }
    }
}
