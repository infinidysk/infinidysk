using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Benchmarks;

public sealed record NativeCacheScaleMeasurement(int Samples, double MedianMilliseconds, double MaximumMilliseconds, long ManagedAllocatedBytes);

public sealed record NativeCacheScaleResult(string Shape, int Entries, long LogicalBytes, long Blocks, long MissingBytes,
    int PayloadFiles, long CatalogueBytes, long CatalogueAllocatedBytes, long BeforeSeedWorkingSetBytes,
    long AfterSeedWorkingSetBytes, long FinalWorkingSetBytes, long PeakWorkingSetBytes, double SeedMilliseconds,
    IReadOnlyDictionary<string, NativeCacheScaleMeasurement> Measurements, IReadOnlyDictionary<string, string[]> QueryPlans)
{
    public int SchemaVersion => 1;
    public string Report => "native-cache-metadata-scale";
    public string Runtime => RuntimeInformation.FrameworkDescription;
    public string Platform => RuntimeInformation.OSDescription;
    public string Scope => "Synthetic metadata only; warm OS cache; no payload or NAS IO. RSS includes fixture setup; timings are not hardware throughput guarantees.";
}

/// <summary>Manual scale report using the real local catalogue schema, triggers and read APIs.</summary>
public static class NativeCacheScaleReport
{
    private const string CandidateSql = """
        SELECT Key,Access FROM Entries WHERE Folder=$folder AND Pinned=0 AND ($clear=1 OR Access<$cutoff)
        AND (Access>$afterAccess OR (Access=$afterAccess AND Key>$afterKey)) ORDER BY Access,Key LIMIT 64
        """;

    public static async Task<bool> TryHandleAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--native-cache-scale-report") return false;
        var shape = "smoke";
        var samples = 10;
        string? json = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 == args.Length) throw new ArgumentException("Native scale report options require a value.");
            switch (args[index])
            {
                case "--shape": shape = args[index + 1]; break;
                case "--samples": samples = int.Parse(args[index + 1], CultureInfo.InvariantCulture); break;
                case "--json": json = args[index + 1]; break;
                default: throw new ArgumentException("Unknown native scale report option.");
            }
        }
        if (json is not null && File.Exists(json)) throw new IOException("Choose a new report output path; existing reports are not overwritten.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var report = await RunAsync(shape, samples, cancellation.Token).ConfigureAwait(false);
            var serialized = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            if (json is not null)
            {
                await using var output = new FileStream(json, FileMode.CreateNew, FileAccess.Write);
                await using var writer = new StreamWriter(output);
                await writer.WriteLineAsync(serialized).ConfigureAwait(false);
            }
            Console.WriteLine(serialized);
            return true;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    public static async Task<NativeCacheScaleResult> RunAsync(string shape, int samples = 10, CancellationToken cancellationToken = default)
    {
        var (entries, length) = shape switch
        {
            "smoke" => (32, 256L * 1024 * 1024),
            "50tb" => (5000, 10_000_000_000L),
            _ => throw new ArgumentException("Native catalogue shape must be smoke or 50tb.", nameof(shape))
        };
        if (samples is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(samples));
        cancellationToken.ThrowIfCancellationRequested();
        // No caller-supplied database or cache directory is accepted. Cleanup can
        // only remove this newly created, private benchmark fixture.
        var root = Directory.CreateTempSubdirectory("infinidysk-native-scale-").FullName;
        try
        {
            var path = Path.Combine(root, "catalogue.db");
            var cachePath = Path.Combine(root, "cache");
            Directory.CreateDirectory(cachePath);
            var folder = new NativeCacheFolder { Id = "synthetic", Path = cachePath, MaxBytes = long.MaxValue, MinFreeBytes = 0 };
            await using (var schema = new NativeCacheStore(path, [folder])) { }
            var identities = Enumerable.Range(0, entries).Select(index => new NativeCacheIdentity($"synthetic-{index:D5}", "metadata-only-v1", length)).ToArray();
            var target = identities[0];
            var beforeSeed = WorkingSet();
            var seed = Stopwatch.StartNew();
            Seed(path, folder.Id, identities, target.Key, cancellationToken);
            seed.Stop();
            var afterSeed = WorkingSet();
            var measurements = new Dictionary<string, NativeCacheScaleMeasurement>(StringComparer.Ordinal);
            measurements["startup"] = await MeasureAsync(samples, async () =>
            {
                await using var reopened = new NativeCacheStore(path, [folder]);
            }, cancellationToken).ConfigureAwait(false);

            await using var store = new NativeCacheStore(path, [folder]);
            var missing = length - ((length - 1) / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize);
            var cursor = identities.OrderBy(identity => identity.Key, StringComparer.Ordinal).ElementAt(entries / 2).Key;
            measurements["entries"] = await MeasureAsync(samples, async () =>
            {
                var page = await store.ListEntriesAsync(folder.Id, cursor, 100, cancellationToken).ConfigureAwait(false);
                if (page.Count is 0 or > 100) throw new InvalidOperationException("Invalid catalogue page.");
            }, cancellationToken).ConfigureAwait(false);
            measurements["coverage"] = await MeasureAsync(samples, async () =>
            {
                if (await store.GetCoverageAsync(target, cancellationToken).ConfigureAwait(false) != length - missing)
                    throw new InvalidOperationException("Synthetic coverage invariant failed.");
            }, cancellationToken).ConfigureAwait(false);
            measurements["missing-range"] = await MeasureAsync(samples, async () =>
            {
                if (await store.GetMissingRangeBytesAsync(target, 0, length, cancellationToken).ConfigureAwait(false) != missing)
                    throw new InvalidOperationException("Sparse-range invariant failed.");
            }, cancellationToken).ConfigureAwait(false);
            measurements["ranges"] = await MeasureAsync(samples, async () =>
            {
                var page = await store.ListVerifiedRangesAsync(target.Key, NativeCacheStore.BlockSize * 16L, 100, cancellationToken).ConfigureAwait(false);
                if (page.Count is 0 or > 100) throw new InvalidOperationException("Invalid integrity-range page.");
            }, cancellationToken).ConfigureAwait(false);
            using var database = Open(path);
            var candidateParameters = new (string, object)[] { ("$folder", folder.Id), ("$clear", 0), ("$cutoff", long.MaxValue),
                ("$afterAccess", entries / 2), ("$afterKey", "") };
            measurements["pressure-candidates"] = await MeasureAsync(samples, () =>
            {
                using var command = Command(database, CandidateSql, candidateParameters);
                using var reader = command.ExecuteReader();
                var count = 0;
                while (reader.Read()) count++;
                if (count is 0 or > 64) throw new InvalidOperationException("Invalid pressure-candidate page.");
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
            var plans = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["entries"] = Explain(database, "SELECT Key FROM Entries WHERE Folder=$folder AND Key>$after ORDER BY Key LIMIT 100", ("$folder", folder.Id), ("$after", cursor)),
                ["coverage"] = Explain(database, "SELECT VerifiedBytes FROM Entries WHERE Key=$key", ("$key", target.Key)),
                ["missing-range"] = Explain(database, "SELECT SUM(Count) FROM Blocks WHERE Key=$key AND Offset>=0 AND Offset<$end", ("$key", target.Key), ("$end", length)),
                ["ranges"] = Explain(database, "SELECT Offset,Count FROM Blocks WHERE Key=$key AND Offset>$after ORDER BY Offset LIMIT 100", ("$key", target.Key), ("$after", NativeCacheStore.BlockSize * 16L)),
                ["pressure-candidates"] = Explain(database, CandidateSql, candidateParameters)
            };
            var payloads = Directory.EnumerateFiles(cachePath, "*", SearchOption.AllDirectories)
                .Count(file => Path.GetFileName(file) is not (".infinidysk-volume" or ".infinidysk-owner"));
            if (payloads != 0) throw new InvalidOperationException("Metadata fixture unexpectedly created cache payload files.");
            var catalogueFiles = Directory.EnumerateFiles(root, "catalogue.db*").ToArray();
            using var process = Process.GetCurrentProcess();
            return new(shape, entries, checked(entries * length), checked(entries * ((length - 1) / NativeCacheStore.BlockSize + 1) - 1), missing,
                payloads, catalogueFiles.Sum(file => new FileInfo(file).Length), catalogueFiles.Sum(NativeFileSystem.GetAllocatedBytes),
                beforeSeed, afterSeed, WorkingSet(), process.PeakWorkingSet64, seed.Elapsed.TotalMilliseconds, measurements, plans);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Seed(string path, string folder, IEnumerable<NativeCacheIdentity> identities, string missingKey, CancellationToken cancellationToken)
    {
        using var database = Open(path);
        using var entry = Command(database, "INSERT INTO Entries(Key,Folder,Length,Bytes,Access,ItemId,Generation) VALUES($key,$folder,$length,$length,$access,$item,$generation)",
            ("$key", ""), ("$folder", folder), ("$length", 0L), ("$access", 0L), ("$item", ""), ("$generation", ""));
        using var block = Command(database, "INSERT INTO Blocks(Key,Offset,Count,Hash) VALUES($key,$offset,$count,$hash)",
            ("$key", ""), ("$offset", 0L), ("$count", 0), ("$hash", new byte[32]));
        entry.Prepare();
        block.Prepare();
        var access = 0;
        foreach (var identity in identities.OrderBy(identity => identity.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = identity.Key;
            using var transaction = database.BeginTransaction();
            entry.Transaction = transaction;
            block.Transaction = transaction;
            entry.Parameters["$key"].Value = key;
            entry.Parameters["$length"].Value = identity.Length;
            entry.Parameters["$access"].Value = access++;
            entry.Parameters["$item"].Value = identity.ItemId;
            entry.Parameters["$generation"].Value = identity.Generation;
            entry.ExecuteNonQuery();
            block.Parameters["$key"].Value = key;
            for (long offset = 0; offset < identity.Length; offset += NativeCacheStore.BlockSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(NativeCacheStore.BlockSize, identity.Length - offset);
                if (key == missingKey && offset + count == identity.Length) continue;
                block.Parameters["$offset"].Value = offset;
                block.Parameters["$count"].Value = count;
                block.ExecuteNonQuery();
            }
            transaction.Commit();
            if (access % 500 == 0) Console.Error.WriteLine($"Native metadata fixture: {access} entries seeded.");
        }
        using var checkpoint = Command(database, "PRAGMA wal_checkpoint(TRUNCATE)");
        checkpoint.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        database.Open();
        using var command = Command(database, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA cache_size=-4096");
        command.ExecuteNonQuery();
        return database;
    }

    private static SqliteCommand Command(SqliteConnection database, string sql, params (string Name, object Value)[] parameters)
    {
        var command = database.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static string[] Explain(SqliteConnection database, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = Command(database, "EXPLAIN QUERY PLAN " + sql, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(3));
        return result.ToArray();
    }

    private static async Task<NativeCacheScaleMeasurement> MeasureAsync(int samples, Func<Task> action, CancellationToken cancellationToken)
    {
        var elapsed = new double[samples];
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        for (var sample = 0; sample < samples; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            await action().ConfigureAwait(false);
            elapsed[sample] = timer.Elapsed.TotalMilliseconds;
        }
        Array.Sort(elapsed);
        return new(samples, (elapsed[(samples - 1) / 2] + elapsed[samples / 2]) / 2, elapsed[^1],
            GC.GetTotalAllocatedBytes(precise: true) - allocated);
    }

    private static long WorkingSet() { using var process = Process.GetCurrentProcess(); return process.WorkingSet64; }
}
