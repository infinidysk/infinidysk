using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.UsenetMigrations
{
    /// <inheritdoc />
    public partial class InitializeUsenetMigrationDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CategoryMap",
                columns: table => new
                {
                    AltmountCategory = table.Column<string>(type: "TEXT", nullable: false),
                    AltmountDir = table.Column<string>(type: "TEXT", nullable: true),
                    AltmountSanitizedDir = table.Column<string>(type: "TEXT", nullable: true),
                    AltmountType = table.Column<string>(type: "TEXT", nullable: true),
                    TargetCategory = table.Column<string>(type: "TEXT", nullable: true),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    DiscoveredBy = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryMap", x => x.AltmountCategory);
                });

            migrationBuilder.CreateTable(
                name: "MigratedReleases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceType = table.Column<string>(type: "TEXT", nullable: false),
                    SourceReleaseId = table.Column<string>(type: "TEXT", nullable: false),
                    FirstRunId = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRunId = table.Column<long>(type: "INTEGER", nullable: false),
                    NzoId = table.Column<string>(type: "TEXT", nullable: true),
                    TargetCategory = table.Column<string>(type: "TEXT", nullable: true),
                    JobName = table.Column<string>(type: "TEXT", nullable: true),
                    MountPath = table.Column<string>(type: "TEXT", nullable: true),
                    ExpectedFileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MappedFileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MigratedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastVerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigratedReleases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MigrationPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    AltmountMetadataRoot = table.Column<string>(type: "TEXT", nullable: true),
                    AltmountConfigPath = table.Column<string>(type: "TEXT", nullable: true),
                    AltmountStoreRoot = table.Column<string>(type: "TEXT", nullable: true),
                    MaxQueueDepth = table.Column<int>(type: "INTEGER", nullable: false),
                    SubmitWorkers = table.Column<int>(type: "INTEGER", nullable: false),
                    SymlinkLibraryRoot = table.Column<string>(type: "TEXT", nullable: true),
                    SymlinkBackupDir = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationPreferences", x => x.Id);
                    table.CheckConstraint("CK_MigrationPreferences_Singleton", "Id = 1");
                });

            migrationBuilder.CreateTable(
                name: "MigrationRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceType = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Releases",
                columns: table => new
                {
                    StoreRef = table.Column<string>(type: "TEXT", nullable: false),
                    StoreBasename = table.Column<string>(type: "TEXT", nullable: false),
                    QueueId = table.Column<long>(type: "INTEGER", nullable: true),
                    SubmitFileName = table.Column<string>(type: "TEXT", nullable: false),
                    QueueFileName = table.Column<string>(type: "TEXT", nullable: false),
                    JobName = table.Column<string>(type: "TEXT", nullable: false),
                    JobNameDiverges = table.Column<bool>(type: "INTEGER", nullable: false),
                    AltmountCategory = table.Column<string>(type: "TEXT", nullable: true),
                    TargetCategory = table.Column<string>(type: "TEXT", nullable: true),
                    Verdict = table.Column<string>(type: "TEXT", nullable: false),
                    VerdictReasons = table.Column<string>(type: "TEXT", nullable: false),
                    VerdictDetail = table.Column<string>(type: "TEXT", nullable: true),
                    CollisionGroupKey = table.Column<string>(type: "TEXT", nullable: true),
                    MetaFileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    NzbFileCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SegmentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EstFetchBytesLazy = table.Column<long>(type: "INTEGER", nullable: false),
                    EstFetchBytesEager = table.Column<long>(type: "INTEGER", nullable: false),
                    IsRarRelease = table.Column<bool>(type: "INTEGER", nullable: false),
                    EstStatCommands = table.Column<long>(type: "INTEGER", nullable: false),
                    ReleaseDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Encryption = table.Column<string>(type: "TEXT", nullable: true),
                    HasPassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasFilenamePassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    WorstFileStatus = table.Column<string>(type: "TEXT", nullable: true),
                    HasNestedSources = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasClipBoundaries = table.Column<bool>(type: "INTEGER", nullable: false),
                    SourceNzbdavId = table.Column<string>(type: "TEXT", nullable: true),
                    Included = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScannedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Releases", x => x.StoreRef);
                });

            migrationBuilder.CreateTable(
                name: "ScanErrors",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanErrors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SessionState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    AltmountMetadataRoot = table.Column<string>(type: "TEXT", nullable: true),
                    AltmountConfigPath = table.Column<string>(type: "TEXT", nullable: true),
                    AltmountStoreRoot = table.Column<string>(type: "TEXT", nullable: true),
                    SymlinkLibraryRoot = table.Column<string>(type: "TEXT", nullable: true),
                    SymlinkBackupDir = table.Column<string>(type: "TEXT", nullable: true),
                    MaxQueueDepth = table.Column<int>(type: "INTEGER", nullable: false),
                    SubmitWorkers = table.Column<int>(type: "INTEGER", nullable: false),
                    ScanLazyRarEnabled = table.Column<bool>(type: "INTEGER", nullable: true),
                    ScanWindowsSafePaths = table.Column<bool>(type: "INTEGER", nullable: true),
                    ScanStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ScanCompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RunStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RunCompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CurrentRunId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionState", x => x.Id);
                    table.CheckConstraint("CK_SessionState_Singleton", "Id = 1");
                });

            migrationBuilder.CreateTable(
                name: "SymlinkRewrites",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SymlinkPath = table.Column<string>(type: "TEXT", nullable: false),
                    OldTarget = table.Column<string>(type: "TEXT", nullable: false),
                    NewTarget = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    MatchMethod = table.Column<string>(type: "TEXT", nullable: true),
                    StoreRef = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SymlinkRewrites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MigratedFiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MigratedReleaseId = table.Column<long>(type: "INTEGER", nullable: false),
                    VirtualPath = table.Column<string>(type: "TEXT", nullable: false),
                    NormalisedRelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    NormalisedName = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: true),
                    DavItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NzbBlobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MatchMethod = table.Column<string>(type: "TEXT", nullable: false),
                    LastVerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigratedFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MigratedFiles_MigratedReleases_MigratedReleaseId",
                        column: x => x.MigratedReleaseId,
                        principalTable: "MigratedReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseFiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StoreRef = table.Column<string>(type: "TEXT", nullable: false),
                    MetaPath = table.Column<string>(type: "TEXT", nullable: false),
                    VirtualPath = table.Column<string>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    NormalisedName = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: true),
                    FileStatus = table.Column<string>(type: "TEXT", nullable: true),
                    NzbdavId = table.Column<string>(type: "TEXT", nullable: true),
                    NewDavItemId = table.Column<string>(type: "TEXT", nullable: true),
                    Flags = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReleaseFiles_Releases_StoreRef",
                        column: x => x.StoreRef,
                        principalTable: "Releases",
                        principalColumn: "StoreRef",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Submissions",
                columns: table => new
                {
                    StoreRef = table.Column<string>(type: "TEXT", nullable: false),
                    NzoId = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    HistoryClearedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MountPath = table.Column<string>(type: "TEXT", nullable: true),
                    DavItemCount = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Submissions", x => x.StoreRef);
                    table.ForeignKey(
                        name: "FK_Submissions_Releases_StoreRef",
                        column: x => x.StoreRef,
                        principalTable: "Releases",
                        principalColumn: "StoreRef",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MigratedFiles_DavItemId",
                table: "MigratedFiles",
                column: "DavItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MigratedFiles_MigratedReleaseId_VirtualPath",
                table: "MigratedFiles",
                columns: new[] { "MigratedReleaseId", "VirtualPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MigratedFiles_NzbBlobId",
                table: "MigratedFiles",
                column: "NzbBlobId");

            migrationBuilder.CreateIndex(
                name: "IX_MigratedReleases_LastRunId",
                table: "MigratedReleases",
                column: "LastRunId");

            migrationBuilder.CreateIndex(
                name: "IX_MigratedReleases_NzoId",
                table: "MigratedReleases",
                column: "NzoId");

            migrationBuilder.CreateIndex(
                name: "IX_MigratedReleases_SourceType_SourceReleaseId",
                table: "MigratedReleases",
                columns: new[] { "SourceType", "SourceReleaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MigrationRuns_StartedAt",
                table: "MigrationRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseFiles_NormalisedName",
                table: "ReleaseFiles",
                column: "NormalisedName");

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseFiles_StoreRef",
                table: "ReleaseFiles",
                column: "StoreRef");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_CollisionGroupKey",
                table: "Releases",
                column: "CollisionGroupKey");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_JobName",
                table: "Releases",
                column: "JobName");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_TargetCategory",
                table: "Releases",
                column: "TargetCategory");

            migrationBuilder.CreateIndex(
                name: "IX_Releases_TargetCategory_QueueFileName",
                table: "Releases",
                columns: new[] { "TargetCategory", "QueueFileName" });

            migrationBuilder.CreateIndex(
                name: "IX_Releases_Verdict_Included",
                table: "Releases",
                columns: new[] { "Verdict", "Included" });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_State",
                table: "Submissions",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_SymlinkRewrites_Status",
                table: "SymlinkRewrites",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CategoryMap");

            migrationBuilder.DropTable(
                name: "MigratedFiles");

            migrationBuilder.DropTable(
                name: "MigrationPreferences");

            migrationBuilder.DropTable(
                name: "MigrationRuns");

            migrationBuilder.DropTable(
                name: "ReleaseFiles");

            migrationBuilder.DropTable(
                name: "ScanErrors");

            migrationBuilder.DropTable(
                name: "SessionState");

            migrationBuilder.DropTable(
                name: "Submissions");

            migrationBuilder.DropTable(
                name: "SymlinkRewrites");

            migrationBuilder.DropTable(
                name: "MigratedReleases");

            migrationBuilder.DropTable(
                name: "Releases");
        }
    }
}
