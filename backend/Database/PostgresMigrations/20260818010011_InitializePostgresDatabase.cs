using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // EF Core seed-data APIs require multidimensional arrays.

namespace NzbWebDAV.Database.PostgresMigrations
{
    /// <inheritdoc />
    public partial class InitializePostgresDatabase : Migration
    {
        private static readonly DateTime SeedEpoch =
            DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Unspecified);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    RandomSalt = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => new { x.Type, x.Username });
                });

            migrationBuilder.CreateTable(
                name: "BlobCleanupItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlobCleanupItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigItems",
                columns: table => new
                {
                    ConfigName = table.Column<string>(type: "text", nullable: false),
                    ConfigValue = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigItems", x => x.ConfigName);
                });

            migrationBuilder.CreateTable(
                name: "DavCleanupItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DavCleanupItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DavItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdPrefix = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    SubType = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    ReleaseDate = table.Column<long>(type: "bigint", nullable: true),
                    LastHealthCheck = table.Column<long>(type: "bigint", nullable: true),
                    NextHealthCheck = table.Column<long>(type: "bigint", nullable: true),
                    HistoryItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    FileBlobId = table.Column<Guid>(type: "uuid", nullable: true),
                    NzbBlobId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DavItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HealthCheckResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    DavItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    NzbFileName = table.Column<string>(type: "text", nullable: true),
                    JobName = table.Column<string>(type: "text", nullable: true),
                    Result = table.Column<int>(type: "integer", nullable: false),
                    RepairStatus = table.Column<int>(type: "integer", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthCheckResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HealthCheckStats",
                columns: table => new
                {
                    DateStartInclusive = table.Column<long>(type: "bigint", nullable: false),
                    DateEndExclusive = table.Column<long>(type: "bigint", nullable: false),
                    Result = table.Column<int>(type: "integer", nullable: false),
                    RepairStatus = table.Column<int>(type: "integer", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthCheckStats", x => new { x.DateStartInclusive, x.DateEndExclusive, x.Result, x.RepairStatus });
                });

            migrationBuilder.CreateTable(
                name: "HistoryCleanupItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeleteMountedFiles = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryCleanupItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HistoryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    JobName = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    DownloadStatus = table.Column<int>(type: "integer", nullable: false),
                    TotalSegmentBytes = table.Column<long>(type: "bigint", nullable: false),
                    DownloadTimeSeconds = table.Column<int>(type: "integer", nullable: false),
                    FailMessage = table.Column<string>(type: "text", nullable: true),
                    DownloadDirId = table.Column<Guid>(type: "uuid", nullable: true),
                    NzbBlobId = table.Column<Guid>(type: "uuid", nullable: true),
                    IndexerName = table.Column<string>(type: "text", nullable: true),
                    ContentGroupKey = table.Column<string>(type: "text", nullable: true),
                    LastPlayedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoryItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IndexerApiHits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IndexerName = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    AccessedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexerApiHits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ListSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Cap = table.Column<int>(type: "integer", nullable: false),
                    SeriesScope = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUnix = table.Column<long>(type: "bigint", nullable: false),
                    LastSyncedAtUnix = table.Column<long>(type: "bigint", nullable: true),
                    LastSyncError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NzbBlobCleanupItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NzbBlobCleanupItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NzbNames",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NzbNames", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NzbResolutionGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    ProfileToken = table.Column<string>(type: "text", nullable: false),
                    SearchId = table.Column<string>(type: "text", nullable: false),
                    CandidatesJson = table.Column<string>(type: "text", nullable: false),
                    TokensJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUnix = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NzbResolutionGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueueItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SortOrder = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    JobName = table.Column<string>(type: "text", nullable: false),
                    NzbFileSize = table.Column<long>(type: "bigint", nullable: false),
                    TotalSegmentBytes = table.Column<long>(type: "bigint", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    PostProcessing = table.Column<int>(type: "integer", nullable: false),
                    PauseUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    IndexerName = table.Column<string>(type: "text", nullable: true),
                    ContentGroupKey = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WantedItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    ContentId = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Provenance = table.Column<string>(type: "text", nullable: false),
                    Shortlist = table.Column<string>(type: "text", nullable: false),
                    WinnerNzb = table.Column<byte[]>(type: "bytea", nullable: true),
                    ResponderHost = table.Column<string>(type: "text", nullable: true),
                    FailReason = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUnix = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUnix = table.Column<long>(type: "bigint", nullable: false),
                    LastResolvedAtUnix = table.Column<long>(type: "bigint", nullable: true),
                    LastVerifiedAtUnix = table.Column<long>(type: "bigint", nullable: true),
                    NextCheckAtUnix = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WantedItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WatchdogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClickId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptedAt = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "text", nullable: false),
                    RequestedTitle = table.Column<string>(type: "text", nullable: false),
                    CandidateTitle = table.Column<string>(type: "text", nullable: false),
                    IndexerName = table.Column<string>(type: "text", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    RankIndex = table.Column<int>(type: "integer", nullable: false),
                    Result = table.Column<int>(type: "integer", nullable: false),
                    FailReason = table.Column<string>(type: "text", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    IsWinner = table.Column<bool>(type: "boolean", nullable: false),
                    ProviderHost = table.Column<string>(type: "text", nullable: true),
                    QueueItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContentGroupKey = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchdogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DavMultipartFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Metadata = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DavMultipartFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DavMultipartFiles_DavItems_Id",
                        column: x => x.Id,
                        principalTable: "DavItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DavNzbFiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
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

            migrationBuilder.CreateTable(
                name: "QueueNzbContents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NzbContents = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueNzbContents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QueueNzbContents_QueueItems_Id",
                        column: x => x.Id,
                        principalTable: "QueueItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_SingleAdmin",
                table: "Accounts",
                column: "Type",
                unique: true,
                filter: "\"Type\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_HistoryItemId_SubType_CreatedAt",
                table: "DavItems",
                columns: new[] { "HistoryItemId", "SubType", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_HistoryItemId_Type_CreatedAt",
                table: "DavItems",
                columns: new[] { "HistoryItemId", "Type", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_IdPrefix_Type",
                table: "DavItems",
                columns: new[] { "IdPrefix", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_NzbBlobId",
                table: "DavItems",
                column: "NzbBlobId");

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_ParentId_Name",
                table: "DavItems",
                columns: new[] { "ParentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_Path",
                table: "DavItems",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DavItems_Type_HistoryItemId_NextHealthCheck_ReleaseDate_Id",
                table: "DavItems",
                columns: new[] { "Type", "HistoryItemId", "NextHealthCheck", "ReleaseDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckResults_CreatedAt",
                table: "HealthCheckResults",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckResults_DavItemId",
                table: "HealthCheckResults",
                column: "DavItemId",
                filter: "\"RepairStatus\" = 3");

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckResults_RepairStatus_CreatedAt",
                table: "HealthCheckResults",
                columns: new[] { "RepairStatus", "CreatedAt" },
                filter: "\"RepairStatus\" IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckResults_Result_RepairStatus_CreatedAt",
                table: "HealthCheckResults",
                columns: new[] { "Result", "RepairStatus", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_Category",
                table: "HistoryItems",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_Category_CreatedAt",
                table: "HistoryItems",
                columns: new[] { "Category", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_Category_DownloadDirId",
                table: "HistoryItems",
                columns: new[] { "Category", "DownloadDirId" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_ContentGroupKey_DownloadStatus",
                table: "HistoryItems",
                columns: new[] { "ContentGroupKey", "DownloadStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_CreatedAt",
                table: "HistoryItems",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_HistoryItems_NzbBlobId",
                table: "HistoryItems",
                column: "NzbBlobId");

            migrationBuilder.CreateIndex(
                name: "IX_IndexerApiHits_AccessedAt",
                table: "IndexerApiHits",
                column: "AccessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_IndexerApiHits_IndexerName_Type_AccessedAt",
                table: "IndexerApiHits",
                columns: new[] { "IndexerName", "Type", "AccessedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_NzbResolutionGroups_CreatedAtUnix",
                table: "NzbResolutionGroups",
                column: "CreatedAtUnix");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Category",
                table: "QueueItems",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Category_FileName",
                table: "QueueItems",
                columns: new[] { "Category", "FileName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Category_Priority_SortOrder",
                table: "QueueItems",
                columns: new[] { "Category", "Priority", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_ContentGroupKey",
                table: "QueueItems",
                column: "ContentGroupKey");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_CreatedAt",
                table: "QueueItems",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Priority",
                table: "QueueItems",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_QueueItems_Priority_SortOrder",
                table: "QueueItems",
                columns: new[] { "Priority", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_WantedItems_Key",
                table: "WantedItems",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WantedItems_NextCheckAtUnix",
                table: "WantedItems",
                column: "NextCheckAtUnix");

            migrationBuilder.CreateIndex(
                name: "IX_WantedItems_State",
                table: "WantedItems",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_WantedItems_UpdatedAtUnix",
                table: "WantedItems",
                column: "UpdatedAtUnix");

            migrationBuilder.CreateIndex(
                name: "IX_WatchdogEntries_AttemptedAt",
                table: "WatchdogEntries",
                column: "AttemptedAt");

            migrationBuilder.CreateIndex(
                name: "IX_WatchdogEntries_ContentGroupKey",
                table: "WatchdogEntries",
                column: "ContentGroupKey");

            migrationBuilder.CreateIndex(
                name: "IX_WatchdogEntries_QueueItemId",
                table: "WatchdogEntries",
                column: "QueueItemId");

#pragma warning disable CA1814 // EF Core migrationBuilder.InsertData requires object[,] values.
            migrationBuilder.InsertData(
                table: "DavItems",
                columns: new[] { "Id", "IdPrefix", "CreatedAt", "ParentId", "Name", "Type", "SubType", "Path" },
                values: new object[,]
                {
                    // CreatedAt columns are timestamp without time zone; Npgsql cannot
                    // render a UTC-kind DateTime literal for them, so seed with the
                    // unspecified-kind wall-clock epoch.
                    { Guid.Parse("00000000-0000-0000-0000-000000000000"), "00000", SeedEpoch, null, "/", 1, 102, "/" },
                    { Guid.Parse("00000000-0000-0000-0000-000000000001"), "00000", SeedEpoch, Guid.Parse("00000000-0000-0000-0000-000000000000"), "nzbs", 1, 103, "/nzbs" },
                    { Guid.Parse("00000000-0000-0000-0000-000000000002"), "00000", SeedEpoch, Guid.Parse("00000000-0000-0000-0000-000000000000"), "content", 1, 104, "/content" },
                    { Guid.Parse("00000000-0000-0000-0000-000000000003"), "00000", SeedEpoch, Guid.Parse("00000000-0000-0000-0000-000000000000"), "completed-symlinks", 1, 105, "/completed-symlinks" },
                    { Guid.Parse("00000000-0000-0000-0000-000000000004"), "00000", SeedEpoch, Guid.Parse("00000000-0000-0000-0000-000000000000"), ".ids", 1, 106, "/.ids" },
                });
            migrationBuilder.InsertData(
                table: "ConfigItems",
                columns: new[] { "ConfigName", "ConfigValue" },
                values: new object[,]
                {
                    { "api.key", Guid.NewGuid().ToString("N") },
                    { "api.strm-key", Guid.NewGuid().ToString("N") },
                });
#pragma warning restore CA1814

            migrationBuilder.Sql(
                """
                CREATE FUNCTION "fn_QueueItems_AddNzbBlobCleanup"() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO "NzbBlobCleanupItems" ("Id") VALUES (OLD."Id")
                    ON CONFLICT ("Id") DO NOTHING;
                    RETURN OLD;
                END $$;
                CREATE TRIGGER "TR_QueueItems_AddNzbBlobCleanup"
                AFTER DELETE ON "QueueItems" FOR EACH ROW
                EXECUTE FUNCTION "fn_QueueItems_AddNzbBlobCleanup"();

                CREATE FUNCTION "fn_HistoryItems_Delete_AddNzbBlobCleanup"() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."NzbBlobId" IS NOT NULL THEN
                        INSERT INTO "NzbBlobCleanupItems" ("Id") VALUES (OLD."NzbBlobId")
                        ON CONFLICT ("Id") DO NOTHING;
                    END IF;
                    RETURN OLD;
                END $$;
                CREATE TRIGGER "TR_HistoryItems_Delete_AddNzbBlobCleanup"
                AFTER DELETE ON "HistoryItems" FOR EACH ROW
                EXECUTE FUNCTION "fn_HistoryItems_Delete_AddNzbBlobCleanup"();

                CREATE FUNCTION "fn_DavItems_BlobCleanup"() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' AND OLD."FileBlobId" IS NOT NULL THEN
                        INSERT INTO "BlobCleanupItems" ("Id") VALUES (OLD."FileBlobId")
                        ON CONFLICT ("Id") DO NOTHING;
                    ELSIF TG_OP = 'UPDATE' AND OLD."FileBlobId" IS NOT NULL
                        AND OLD."FileBlobId" IS DISTINCT FROM NEW."FileBlobId" THEN
                        INSERT INTO "BlobCleanupItems" ("Id") VALUES (OLD."FileBlobId")
                        ON CONFLICT ("Id") DO NOTHING;
                    END IF;
                    RETURN COALESCE(NEW, OLD);
                END $$;
                CREATE TRIGGER "TR_DavItems_Delete_AddBlobCleanup"
                AFTER DELETE ON "DavItems" FOR EACH ROW
                EXECUTE FUNCTION "fn_DavItems_BlobCleanup"();
                CREATE TRIGGER "TR_DavItems_Update_AddBlobCleanup"
                AFTER UPDATE OF "FileBlobId" ON "DavItems" FOR EACH ROW
                EXECUTE FUNCTION "fn_DavItems_BlobCleanup"();

                CREATE FUNCTION "fn_DavItems_Delete_Cleanup"() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."NzbBlobId" IS NOT NULL THEN
                        INSERT INTO "NzbBlobCleanupItems" ("Id") VALUES (OLD."NzbBlobId")
                        ON CONFLICT ("Id") DO NOTHING;
                    END IF;
                    IF OLD."SubType" = 101 THEN
                        INSERT INTO "DavCleanupItems" ("Id") VALUES (OLD."Id")
                        ON CONFLICT ("Id") DO NOTHING;
                    END IF;
                    RETURN OLD;
                END $$;
                CREATE TRIGGER "TR_DavItems_Delete_Cleanup"
                AFTER DELETE ON "DavItems" FOR EACH ROW
                EXECUTE FUNCTION "fn_DavItems_Delete_Cleanup"();

                CREATE FUNCTION "fn_HistoryItems_Delete_AddHistoryCleanup"() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."Id" IS NOT NULL THEN
                        INSERT INTO "HistoryCleanupItems" ("Id", "DeleteMountedFiles")
                        VALUES (OLD."Id", false)
                        ON CONFLICT ("Id") DO UPDATE
                        SET "DeleteMountedFiles" = EXCLUDED."DeleteMountedFiles";
                    END IF;
                    RETURN OLD;
                END $$;
                CREATE TRIGGER "TR_HistoryItems_Delete_AddHistoryCleanup"
                AFTER DELETE ON "HistoryItems" FOR EACH ROW
                EXECUTE FUNCTION "fn_HistoryItems_Delete_AddHistoryCleanup"();

                CREATE FUNCTION "fn_HealthCheckResults_Stats"() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    old_day bigint;
                    new_day bigint;
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        old_day := floor(OLD."CreatedAt" / 86400) * 86400;
                        UPDATE "HealthCheckStats" SET "Count" = "Count" - 1
                        WHERE "DateStartInclusive" = old_day
                          AND "DateEndExclusive" = old_day + 86400
                          AND "Result" = OLD."Result"
                          AND "RepairStatus" = OLD."RepairStatus";
                        DELETE FROM "HealthCheckStats"
                        WHERE "DateStartInclusive" = old_day
                          AND "DateEndExclusive" = old_day + 86400
                          AND "Result" = OLD."Result"
                          AND "RepairStatus" = OLD."RepairStatus"
                          AND "Count" <= 0;
                    END IF;
                    IF TG_OP <> 'DELETE' THEN
                        new_day := floor(NEW."CreatedAt" / 86400) * 86400;
                        INSERT INTO "HealthCheckStats"
                            ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus", "Count")
                        VALUES (new_day, new_day + 86400, NEW."Result", NEW."RepairStatus", 1)
                        ON CONFLICT ("DateStartInclusive", "DateEndExclusive", "Result", "RepairStatus")
                        DO UPDATE SET "Count" = "HealthCheckStats"."Count" + 1;
                    END IF;
                    RETURN COALESCE(NEW, OLD);
                END $$;
                CREATE TRIGGER "TR_HealthCheckResults_IncrementStats"
                AFTER INSERT ON "HealthCheckResults" FOR EACH ROW
                EXECUTE FUNCTION "fn_HealthCheckResults_Stats"();
                CREATE TRIGGER "TR_HealthCheckResults_DecrementStats"
                AFTER DELETE ON "HealthCheckResults" FOR EACH ROW
                EXECUTE FUNCTION "fn_HealthCheckResults_Stats"();
                CREATE TRIGGER "TR_HealthCheckResults_UpdateStats"
                AFTER UPDATE OF "CreatedAt", "Result", "RepairStatus" ON "HealthCheckResults" FOR EACH ROW
                EXECUTE FUNCTION "fn_HealthCheckResults_Stats"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP FUNCTION IF EXISTS "fn_QueueItems_AddNzbBlobCleanup"() CASCADE;
                DROP FUNCTION IF EXISTS "fn_HistoryItems_Delete_AddNzbBlobCleanup"() CASCADE;
                DROP FUNCTION IF EXISTS "fn_DavItems_BlobCleanup"() CASCADE;
                DROP FUNCTION IF EXISTS "fn_DavItems_Delete_Cleanup"() CASCADE;
                DROP FUNCTION IF EXISTS "fn_HistoryItems_Delete_AddHistoryCleanup"() CASCADE;
                DROP FUNCTION IF EXISTS "fn_HealthCheckResults_Stats"() CASCADE;
                """);

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "BlobCleanupItems");

            migrationBuilder.DropTable(
                name: "ConfigItems");

            migrationBuilder.DropTable(
                name: "DavCleanupItems");

            migrationBuilder.DropTable(
                name: "DavMultipartFiles");

            migrationBuilder.DropTable(
                name: "DavNzbFiles");

            migrationBuilder.DropTable(
                name: "DavRarFiles");

            migrationBuilder.DropTable(
                name: "HealthCheckResults");

            migrationBuilder.DropTable(
                name: "HealthCheckStats");

            migrationBuilder.DropTable(
                name: "HistoryCleanupItems");

            migrationBuilder.DropTable(
                name: "HistoryItems");

            migrationBuilder.DropTable(
                name: "IndexerApiHits");

            migrationBuilder.DropTable(
                name: "ListSources");

            migrationBuilder.DropTable(
                name: "NzbBlobCleanupItems");

            migrationBuilder.DropTable(
                name: "NzbNames");

            migrationBuilder.DropTable(
                name: "NzbResolutionGroups");

            migrationBuilder.DropTable(
                name: "QueueNzbContents");

            migrationBuilder.DropTable(
                name: "WantedItems");

            migrationBuilder.DropTable(
                name: "WatchdogEntries");

            migrationBuilder.DropTable(
                name: "DavItems");

            migrationBuilder.DropTable(
                name: "QueueItems");
        }
    }
}
