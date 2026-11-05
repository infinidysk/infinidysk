-- The tables the importer reads, as 1.x v1.7.1 creates them on PostgreSQL
-- (v1:backend/Database/PostgresMigrations/PostgresDavDatabaseContextModelSnapshot.cs).
CREATE TABLE "__EFMigrationsHistory" (
    "MigrationId" character varying(150) PRIMARY KEY,
    "ProductVersion" character varying(32) NOT NULL);
CREATE TABLE "Accounts" (
    "Type" integer NOT NULL, "Username" character varying(255) NOT NULL,
    "PasswordHash" text NOT NULL, "RandomSalt" text NOT NULL,
    PRIMARY KEY ("Type", "Username"));
CREATE TABLE "ConfigItems" ("ConfigName" text PRIMARY KEY, "ConfigValue" text NOT NULL);
CREATE TABLE "DavItems" (
    "Id" uuid PRIMARY KEY, "IdPrefix" text NOT NULL,
    "CreatedAt" timestamp without time zone NOT NULL, "ParentId" uuid,
    "Name" character varying(255) NOT NULL, "FileSize" bigint,
    "Type" integer NOT NULL, "SubType" integer NOT NULL, "Path" text NOT NULL,
    "ReleaseDate" bigint, "LastHealthCheck" bigint, "NextHealthCheck" bigint,
    "HistoryItemId" uuid, "FileBlobId" uuid, "NzbBlobId" uuid,
    "GeneratedStrmOutputRoot" text, "GeneratedStrmPath" text, "GeneratedStrmTarget" text,
    "GeneratedSymlinkOutputRoot" text, "GeneratedSymlinkPath" text, "GeneratedSymlinkTarget" text,
    "HealthRepairPending" boolean NOT NULL DEFAULT false,
    "ArrDownloadId" uuid, "UrgentRepairFailures" integer);
CREATE TABLE "HistoryItems" (
    "Id" uuid PRIMARY KEY, "CreatedAt" timestamp without time zone NOT NULL,
    "FileName" text NOT NULL, "JobName" text NOT NULL, "Category" text NOT NULL,
    "DownloadStatus" integer NOT NULL, "TotalSegmentBytes" bigint NOT NULL,
    "DownloadTimeSeconds" integer NOT NULL, "FailMessage" text, "DownloadDirId" uuid,
    "NzbBlobId" uuid, "ArrDownloadId" uuid, "ContentGroupKey" text, "IndexerName" text,
    "LastPlayedAt" bigint);
CREATE TABLE "QueueItems" (
    "Id" uuid PRIMARY KEY, "CreatedAt" timestamp without time zone NOT NULL,
    "FileName" text NOT NULL, "JobName" text NOT NULL, "NzbFileSize" bigint NOT NULL,
    "TotalSegmentBytes" bigint NOT NULL, "Category" text NOT NULL,
    "Priority" integer NOT NULL, "PostProcessing" integer NOT NULL,
    "PauseUntil" timestamp without time zone, "SortOrder" bigint NOT NULL,
    "ArrDownloadId" uuid, "ContentGroupKey" text, "IndexerName" text);
CREATE TABLE "QueueNzbContents" ("Id" uuid PRIMARY KEY, "NzbContents" text NOT NULL);

INSERT INTO "__EFMigrationsHistory" VALUES
    ('20260818010011_InitializePostgresDatabase', '10.0.11'),
    ('20260824160000_Add-Health-Repair-Pending', '10.0.11'),
    ('20260921210000_Add-Health-Alert-History-Lookup-Index', '10.0.11');
INSERT INTO "DavItems" ("Id", "IdPrefix", "CreatedAt", "ParentId", "Name", "Type", "SubType", "Path") VALUES
    ('00000000-0000-0000-0000-000000000000', '00000', '1970-01-01', NULL, '/', 1, 102, '/'),
    ('00000000-0000-0000-0000-000000000001', '00000', '1970-01-01', '00000000-0000-0000-0000-000000000000', 'nzbs', 1, 103, '/nzbs'),
    ('00000000-0000-0000-0000-000000000002', '00000', '1970-01-01', '00000000-0000-0000-0000-000000000000', 'content', 1, 104, '/content'),
    ('00000000-0000-0000-0000-000000000003', '00000', '1970-01-01', '00000000-0000-0000-0000-000000000000', 'completed-symlinks', 1, 105, '/completed-symlinks'),
    ('00000000-0000-0000-0000-000000000004', '00000', '1970-01-01', '00000000-0000-0000-0000-000000000000', '.ids', 1, 106, '/.ids'),
    ('0198a3c2-5a10-7000-8000-00000000000a', '0198a', '2026-01-02 03:04:05', '00000000-0000-0000-0000-000000000002', 'tv', 1, 101, '/content/tv'),
    ('0198a3c2-5a10-7000-8000-00000000000b', '0198a', '2026-01-02 03:04:06', '0198a3c2-5a10-7000-8000-00000000000a', 'job', 1, 101, '/content/tv/job');
INSERT INTO "DavItems" ("Id", "IdPrefix", "CreatedAt", "ParentId", "Name", "Type", "SubType", "Path",
                        "FileSize", "FileBlobId", "ReleaseDate", "HealthRepairPending") VALUES
    ('0198a3c2-5a10-7000-8000-00000000000c', '0198a', '2026-10-02 09:50:39.5', '0198a3c2-5a10-7000-8000-00000000000b',
     'f.mkv', 2, 203, '/content/tv/job/f.mkv', 5000000000, '0198a3c2-5a10-7000-8000-0000000000ff', 1700000000, true);
INSERT INTO "HistoryItems" ("Id", "CreatedAt", "FileName", "JobName", "Category", "DownloadStatus",
                            "TotalSegmentBytes", "DownloadTimeSeconds", "DownloadDirId") VALUES
    ('0198a3c2-5a10-7000-8000-0000000000a1', '2026-01-02 03:04:07', 'job.nzb', 'job', 'tv', 1, 100, 1,
     '0198a3c2-5a10-7000-8000-00000000000b');
INSERT INTO "QueueItems" ("Id", "CreatedAt", "FileName", "JobName", "NzbFileSize", "TotalSegmentBytes",
                          "Category", "Priority", "PostProcessing", "SortOrder") VALUES
    ('0198a3c2-5a10-7000-8000-0000000000b1', '2026-01-02 03:04:08', 'next.nzb', 'next', 18, 1, 'tv', 0, 0, 1);
INSERT INTO "QueueNzbContents" VALUES ('0198a3c2-5a10-7000-8000-0000000000b1', '<nzb>it''s ü</nzb>');
INSERT INTO "ConfigItems" VALUES
    ('api.key', '0123456789abcdef0123456789abcdef'), ('usenet.providers', '{"Providers":[]}');
INSERT INTO "Accounts" VALUES (1, 'admin', 'hash', 'salt');
