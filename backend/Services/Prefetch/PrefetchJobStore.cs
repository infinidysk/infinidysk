using Microsoft.Data.Sqlite;
using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Services.Prefetch;

public sealed record PrefetchJob(string Id, Guid ItemId, string Trigger, int Priority, string State,
    long Start, long Length, string? Generation, long CommittedBytes, string? Error, long Updated)
{
    public string? DisplayName { get; init; }
    public string? Source { get; init; }
    public string? Reason { get; init; }
    public long? FileSize { get; init; }
}

public sealed record PrefetchEnqueueResult(PrefetchJob Job, bool Created);

public sealed class PrefetchJobStore : IDisposable
{
    private readonly SqliteConnection _database;
    private readonly Lock _gate = new();
    private readonly int _capacity;
    private readonly Func<PrefetchSettings>? _settings;
    private int Capacity => Math.Min(_capacity, _settings?.Invoke().QueueCapacity ?? _capacity);
    private SqliteTransaction? _transaction;
    private int _wireBudgetBlocked;
    private readonly CancellationTokenSource _wireBudgetFailure = new();
    public bool WireBudgetBlocked => Volatile.Read(ref _wireBudgetBlocked) != 0;
    public CancellationToken WireBudgetFailure => _wireBudgetFailure.Token;
    public void BlockWireBudget()
    {
        if (Interlocked.Exchange(ref _wireBudgetBlocked, 1) == 0)
            try { _wireBudgetFailure.Cancel(); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }
    public PrefetchJobStore(string path, int capacity = 256, Func<PrefetchSettings>? settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        NativeFileSystem.RequireLocalMetadata(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _capacity = capacity;
        _settings = settings;
        _database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _database.Open();
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            PRAGMA cache_size=-2048;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS Jobs(
                Id TEXT PRIMARY KEY,ItemId TEXT NOT NULL,Trigger TEXT NOT NULL,Priority INTEGER NOT NULL,
                State TEXT NOT NULL,Start INTEGER NOT NULL,Length INTEGER NOT NULL,Generation TEXT NULL,
                CommittedBytes INTEGER NOT NULL DEFAULT 0,Error TEXT NULL,Updated INTEGER NOT NULL,Created INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS JobReady ON Jobs(State,Priority DESC,Created);
            CREATE INDEX IF NOT EXISTS JobItem ON Jobs(ItemId,Start,Length,State);
            CREATE TABLE IF NOT EXISTS State(Key TEXT PRIMARY KEY,Value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Deferred(Id TEXT PRIMARY KEY,Until INTEGER NOT NULL);
            CREATE TRIGGER IF NOT EXISTS JobDeleted AFTER DELETE ON Jobs BEGIN DELETE FROM Deferred WHERE Id=OLD.Id; END;
            CREATE TABLE IF NOT EXISTS DailyBudget(Day TEXT PRIMARY KEY,Bytes INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS Owners(Id TEXT NOT NULL REFERENCES Jobs(Id) ON DELETE CASCADE,Owner TEXT NOT NULL,PRIMARY KEY(Id,Owner));
            CREATE TABLE IF NOT EXISTS Attempts(Id TEXT PRIMARY KEY REFERENCES Jobs(Id) ON DELETE CASCADE,Count INTEGER NOT NULL);
            INSERT OR IGNORE INTO State VALUES('paused','false');
            DELETE FROM Jobs WHERE State IN ('queued','running','paused') AND Id NOT IN (SELECT Id FROM Owners WHERE Owner='manual');
            UPDATE Jobs SET State='paused',Error='Restored after restart; resume to recheck verified coverage.' WHERE State IN ('running','queued');
            DELETE FROM Deferred WHERE Id NOT IN (SELECT Id FROM Jobs);
            """);
    }

    public PrefetchJob Enqueue(Guid itemId, string trigger, int priority, long start = 0, long length = 0)
        => EnqueueWithOutcome(itemId, trigger, priority, start, length).Job;

    public PrefetchEnqueueResult EnqueueWithOutcome(Guid itemId, string trigger, int priority, long start = 0, long length = 0)
    {
        if (itemId == Guid.Empty || trigger is not { Length: > 0 and <= 128 } || priority is < -100 or > 100
            || start < 0 || length < 0 || start > long.MaxValue - length)
            throw new ArgumentException("Invalid prefetch item, trigger, priority, or range.");
        lock (_gate)
        return Atomic(() =>
        {
            // Inactive exact requests retain their retry/pause semantics. Running
            // work is never widened; larger requests become queued successors.
            var existing = ReadOne("SELECT * FROM Jobs WHERE ItemId=$item AND State IN ('running','paused','failed') AND ((Start=$start AND Length=$length) OR (State='running' AND Start=0 AND Length=0)) ORDER BY Created,Id LIMIT 1",
                ("$item", itemId.ToString("N")), ("$start", start), ("$length", length));
            if (existing is not null)
            {
                Execute("UPDATE Jobs SET Priority=MAX(Priority,$priority) WHERE Id=$id", ("$priority", priority), ("$id", existing.Id));
                AddOwner(existing.Id, trigger);
                return new PrefetchEnqueueResult(existing with { Priority = Math.Max(existing.Priority, priority) }, false);
            }
            var merged = MergeQueued(itemId, trigger, priority, start, length);
            if (merged is not null) return new PrefetchEnqueueResult(merged, false);
            using var count = Command("SELECT COUNT(*) FROM Jobs WHERE State IN ('queued','running','paused')");
            if ((long)count.ExecuteScalar()! >= Capacity) throw new ArgumentException("Prefetch queue is full. Cancel or complete existing work first.");
            // Retain only bounded operation history; never grow a media-library-sized memory/index queue.
            Execute("DELETE FROM Jobs WHERE Id IN (SELECT Id FROM Jobs WHERE State IN ('completed','failed','cancelled') ORDER BY Updated DESC LIMIT -1 OFFSET 255)");
            var id = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var created = NextCreated(now);
            Execute("INSERT INTO Jobs(Id,ItemId,Trigger,Priority,State,Start,Length,Updated,Created) VALUES($id,$item,$trigger,$priority,'queued',$start,$length,$now,$created)",
                ("$id", id), ("$item", itemId.ToString("N")), ("$trigger", trigger), ("$priority", priority), ("$start", start), ("$length", length), ("$now", now), ("$created", created));
            AddOwner(id, trigger);
            return new PrefetchEnqueueResult(new PrefetchJob(id, itemId, trigger, priority, "queued", start, length, null, 0, null, now), true);
        });
    }

    private sealed record QueuedIntent(PrefetchJob Job, long Created, long Attempts, long? DeferredUntil);

    private PrefetchJob? MergeQueued(Guid itemId, string owner, int priority, long start, long length)
    {
        var candidates = new List<QueuedIntent>();
        using (var query = Command("""
            SELECT j.*,COALESCE(a.Count,0) AS AttemptCount,d.Until FROM Jobs j
            LEFT JOIN Attempts a ON a.Id=j.Id LEFT JOIN Deferred d ON d.Id=j.Id
            WHERE j.ItemId=$item AND j.State='queued' ORDER BY j.Created,j.Id LIMIT 257
            """, ("$item", itemId.ToString("N"))))
        using (var reader = query.ExecuteReader())
            while (reader.Read()) candidates.Add(new(Read(reader), reader.GetInt64(reader.GetOrdinal("Created")),
                reader.GetInt64(reader.GetOrdinal("AttemptCount")), reader.IsDBNull(reader.GetOrdinal("Until")) ? null : reader.GetInt64(reader.GetOrdinal("Until"))));
        if (candidates.Count > 256) throw new ArgumentException("Prefetch queue exceeds its bounded capacity. Clear excess intents first.");

        // Null is an open-ended interval, including a nonzero-start tail request.
        long? end = length == 0 ? null : start + length;
        var selected = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var candidate in candidates)
            {
                var job = candidate.Job;
                long? candidateEnd = job.Length == 0 ? null : checked(job.Start + job.Length);
                if (selected.Contains(job.Id) || (end.HasValue && job.Start > end.Value)
                    || (candidateEnd.HasValue && start > candidateEnd.Value)) continue;
                selected.Add(job.Id);
                start = Math.Min(start, job.Start);
                end = !end.HasValue || !candidateEnd.HasValue ? null : Math.Max(end.Value, candidateEnd.Value);
                changed = true;
            }
        } while (changed);
        if (selected.Count == 0) return null;
        var merged = candidates.Where(candidate => selected.Contains(candidate.Job.Id)).ToArray();
        var survivor = merged[0]; // Query order preserves the oldest intent and stable identity.
        var owners = new HashSet<string>(StringComparer.Ordinal) { owner };
        foreach (var candidate in merged)
        {
            using var query = Command("SELECT Owner FROM Owners WHERE Id=$id LIMIT 130", ("$id", candidate.Job.Id));
            using var reader = query.ExecuteReader();
            while (reader.Read()) owners.Add(reader.GetString(0));
            if (owners.Count - (owners.Contains("manual") ? 1 : 0) > 128)
                throw new ArgumentException("Merging these ranges would exceed the prefetch source-owner limit.");
        }

        var id = survivor.Job.Id;
        foreach (var source in owners) Execute("INSERT OR IGNORE INTO Owners(Id,Owner) VALUES($id,$owner)", ("$id", id), ("$owner", source));
        var attempts = merged.Max(candidate => candidate.Attempts);
        var until = merged.Max(candidate => candidate.DeferredUntil);
        Execute("UPDATE Jobs SET Start=$start,Length=$length,Priority=$priority,Created=$created WHERE Id=$id",
            ("$start", start), ("$length", end.HasValue ? end.Value - start : 0),
            ("$priority", Math.Max(priority, merged.Max(candidate => candidate.Job.Priority))), ("$created", survivor.Created), ("$id", id));
        Execute("INSERT INTO Attempts(Id,Count) VALUES($id,$count) ON CONFLICT(Id) DO UPDATE SET Count=excluded.Count", ("$id", id), ("$count", attempts));
        if (until.HasValue)
            Execute("INSERT INTO Deferred(Id,Until) VALUES($id,$until) ON CONFLICT(Id) DO UPDATE SET Until=excluded.Until", ("$id", id), ("$until", until.Value));
        foreach (var candidate in merged.Skip(1)) Execute("DELETE FROM Jobs WHERE Id=$id", ("$id", candidate.Job.Id));
        return ReadOne("SELECT * FROM Jobs WHERE Id=$id", ("$id", id))!;
    }

    public bool Paused
    {
        get { lock (_gate) { using var command = Command("SELECT Value FROM State WHERE Key='paused'"); return command.ExecuteScalar() as string == "true"; } }
    }

    public void Defer(string id, string reason, TimeSpan delay, bool consumeAttempt = true)
    {
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(delay));
        lock (_gate)
        Atomic(() =>
        {
            if (!IsRunning(id)) return;
            if (consumeAttempt) Execute("INSERT INTO Attempts(Id,Count) SELECT Id,1 FROM Jobs WHERE Id=$id AND State='running' ON CONFLICT(Id) DO UPDATE SET Count=Count+1", ("$id", id));
            Execute("UPDATE Jobs SET State='queued',Error=$reason WHERE Id=$id AND State='running'", ("$id", id), ("$reason", reason[..Math.Min(512, reason.Length)]));
            Execute("UPDATE Jobs SET State='failed' WHERE Id=$id AND State='queued' AND Id IN (SELECT Id FROM Attempts WHERE Count>$retries)",
                ("$id", id), ("$retries", _settings?.Invoke().MaxRetries ?? 3));
            Execute("INSERT INTO Deferred(Id,Until) VALUES($id,$until) ON CONFLICT(Id) DO UPDATE SET Until=excluded.Until",
                ("$id", id), ("$until", DateTimeOffset.UtcNow.Add(delay).ToUnixTimeMilliseconds()));
        });
    }

    public void PruneOwners(Func<string, bool> keep)
    {
        lock (_gate)
        Atomic(() =>
        {
            var removed = new List<(string Id, string Owner)>();
            using (var command = Command("SELECT Id,Owner FROM Owners"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) if (!keep(reader.GetString(1))) removed.Add((reader.GetString(0), reader.GetString(1)));
            foreach (var owner in removed) Execute("DELETE FROM Owners WHERE Id=$id AND Owner=$owner", ("$id", owner.Id), ("$owner", owner.Owner));
            Execute("UPDATE Jobs SET State='cancelled',Error='All sources disabled.' WHERE State IN ('queued','running','paused') AND Id NOT IN (SELECT Id FROM Owners)");
        });
    }

    private void AddOwner(string id, string owner)
    {
        using var existing = Command("SELECT 1 FROM Owners WHERE Id=$id AND Owner=$owner", ("$id", id), ("$owner", owner));
        if (existing.ExecuteScalar() is not null) return;
        using var count = Command("SELECT COUNT(*) FROM Owners WHERE Id=$id AND Owner<>'manual'", ("$id", id));
        if ((long)count.ExecuteScalar()! >= 128 && owner != "manual")
            throw new ArgumentException("Prefetch source-owner limit reached.");
        Execute("INSERT OR IGNORE INTO Owners(Id,Owner) VALUES($id,$owner)", ("$id", id), ("$owner", owner));
    }

    public bool IsRunning(string id)
    {
        lock (_gate) return ReadOne("SELECT * FROM Jobs WHERE Id=$id", ("$id", id))?.State == "running";
    }

    public bool TrySpendDailyBudget(long bytes, long budget)
    {
        if (bytes < 0 || budget < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_gate)
        {
            var day = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            Execute("DELETE FROM DailyBudget WHERE Day<>$day", ("$day", day));
            using var lookup = Command("SELECT Bytes FROM DailyBudget WHERE Day=$day", ("$day", day));
            var spent = lookup.ExecuteScalar() as long? ?? 0;
            if (budget != 0 && bytes > budget - spent) return false;
            Execute("INSERT INTO DailyBudget(Day,Bytes) VALUES($day,$bytes) ON CONFLICT(Day) DO UPDATE SET Bytes=Bytes+excluded.Bytes", ("$day", day), ("$bytes", bytes));
            return true;
        }
    }

    public long ReserveDailyCredit(long requested, long budget, string day)
    {
        if (WireBudgetBlocked) throw new IOException("Warming budget accounting failed; repair local metadata storage and restart.");
        if (requested < 0 || budget < 0) throw new ArgumentOutOfRangeException(nameof(requested));
        lock (_gate)
        return Atomic(() =>
        {
            Execute("DELETE FROM DailyBudget WHERE Day<$day", ("$day", day));
            using var lookup = Command("SELECT Bytes FROM DailyBudget WHERE Day=$day", ("$day", day));
            var spent = lookup.ExecuteScalar() as long? ?? 0;
            var grant = budget == 0 ? requested : Math.Min(requested, Math.Max(0, budget - spent));
            Execute("INSERT INTO DailyBudget(Day,Bytes) VALUES($day,$bytes) ON CONFLICT(Day) DO UPDATE SET Bytes=Bytes+excluded.Bytes", ("$day", day), ("$bytes", grant));
            return grant;
        });
    }

    public void ReturnDailyCredit(long bytes, string day)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate) Execute("UPDATE DailyBudget SET Bytes=MAX(0,Bytes-$bytes) WHERE Day=$day", ("$day", day), ("$bytes", bytes));
    }

    public long GetDailyBudgetUsed()
    {
        lock (_gate)
        {
            var day = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            using var lookup = Command("SELECT Bytes FROM DailyBudget WHERE Day=$day", ("$day", day));
            return lookup.ExecuteScalar() as long? ?? 0;
        }
    }

    public PrefetchJob? ClaimNext()
    {
        lock (_gate)
        {
            if (Paused) return null;
            Execute("UPDATE Jobs SET State='failed',Error='Intent expired; retry explicitly.' WHERE State='queued' AND Created<$cutoff",
                ("$cutoff", DateTimeOffset.UtcNow.AddHours(-(_settings?.Invoke().IntentTtlHours ?? 24)).ToUnixTimeMilliseconds()));
            var job = ReadOne("SELECT * FROM Jobs WHERE State='queued' AND Id NOT IN (SELECT Id FROM Deferred WHERE Until>$now) ORDER BY Priority DESC,Created,Id LIMIT 1",
                ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            if (job is null) return null;
            Execute("UPDATE Jobs SET State='running',Error=NULL WHERE Id=$id", ("$id", job.Id));
            return job with { State = "running", Error = null };
        }
    }

    public void Progress(string id, string generation, long committedBytes)
    {
        if (committedBytes < 0 || generation.Length > 512) throw new ArgumentException("Invalid committed progress.");
        lock (_gate) Execute("UPDATE Jobs SET Generation=$generation,CommittedBytes=$bytes,Updated=$now WHERE Id=$id AND State='running'",
            ("$generation", generation), ("$bytes", committedBytes), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$id", id));
    }

    public void Change(string id, string operation, int? priority = null)
    {
        lock (_gate)
        Atomic(() =>
        {
            var job = ReadOne("SELECT * FROM Jobs WHERE Id=$id", ("$id", id)) ?? throw new ArgumentException("Unknown prefetch job.");
            if (operation == "prioritize")
            {
                if (priority is null or < -100 or > 100) throw new ArgumentException("Priority must be between -100 and 100.");
                Execute("UPDATE Jobs SET Priority=$priority WHERE Id=$id", ("$priority", priority.Value), ("$id", id));
                return;
            }
            var state = operation switch
            {
                "pause" when job.State is "queued" or "running" => "paused",
                "resume" when job.State == "paused" => "queued",
                "cancel" when job.State is "queued" or "running" or "paused" => "cancelled",
                "retry" when job.State is "failed" or "cancelled" => "queued",
                _ => throw new ArgumentException("This operation is not valid for the job's current state.")
            };
            if (state == "queued" && job.State is "failed" or "cancelled")
            {
                using var count = Command("SELECT COUNT(*) FROM Jobs WHERE State IN ('queued','running','paused')");
                if ((long)count.ExecuteScalar()! >= Capacity) throw new ArgumentException("Prefetch queue is full.");
                var duplicate = ReadOne("SELECT * FROM Jobs WHERE ItemId=$item AND Id<>$id AND Start=$start AND Length=$length AND State IN ('queued','running','paused') LIMIT 1",
                    ("$item", job.ItemId.ToString("N")), ("$id", id), ("$start", job.Start), ("$length", job.Length));
                if (duplicate is not null) throw new ArgumentException("This file range is already queued.");
            }
            Execute("UPDATE Jobs SET State=$state,Error=NULL,Updated=$now WHERE Id=$id", ("$state", state), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$id", id));
            if (operation is "retry" or "resume")
            {
                Execute("DELETE FROM Deferred WHERE Id=$id", ("$id", id));
                if (operation == "retry") Execute("DELETE FROM Attempts WHERE Id=$id", ("$id", id));
                Execute("UPDATE Jobs SET Created=$created WHERE Id=$id", ("$id", id),
                    ("$created", NextCreated(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
            }
        });
    }

    public void Finish(string id, bool success, string? error)
    {
        lock (_gate) Execute("UPDATE Jobs SET State=$state,Error=$error,Updated=$now WHERE Id=$id AND State='running'",
            ("$state", success ? "completed" : "failed"), ("$error", error is null ? DBNull.Value : error[..Math.Min(error.Length, 512)]),
            ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$id", id));
    }

    public void SetPaused(bool paused)
    {
        lock (_gate) Execute("UPDATE State SET Value=$value WHERE Key='paused'", ("$value", paused ? "true" : "false"));
    }

    public IReadOnlyList<PrefetchJob> List()
    {
        lock (_gate)
        {
            using var command = Command("SELECT * FROM Jobs ORDER BY Updated DESC,Id LIMIT $limit", ("$limit", _capacity + 256));
            using var reader = command.ExecuteReader();
            var result = new List<PrefetchJob>();
            while (reader.Read()) result.Add(Read(reader));
            return result;
        }
    }

    private PrefetchJob? ReadOne(string sql, params (string Key, object Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }
    private static PrefetchJob Read(SqliteDataReader reader) => new(reader.GetString(0), Guid.Parse(reader.GetString(1)),
        reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6),
        reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt64(10));
    private long NextCreated(long wallClockMilliseconds)
    {
        using var command = Command("SELECT COALESCE(MAX(Created),0) FROM Jobs");
        var latest = (long)command.ExecuteScalar()!;
        return Math.Max(wallClockMilliseconds, checked(latest + 1));
    }
    private SqliteCommand Command(string sql, params (string Key, object Value)[] parameters)
    {
        var command = _database.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value);
        return command;
    }
    private void Execute(string sql, params (string Key, object Value)[] parameters)
    { using var command = Command(sql, parameters); command.ExecuteNonQuery(); }
    private T Atomic<T>(Func<T> mutation)
    {
        using var transaction = _database.BeginTransaction();
        _transaction = transaction;
        try
        {
            var result = mutation();
            transaction.Commit();
            return result;
        }
        finally { _transaction = null; }
    }
    private void Atomic(Action mutation) => Atomic(() => { mutation(); return true; });
    public void Dispose()
    {
        BlockWireBudget();
        lock (_gate) _database.Dispose();
        _wireBudgetFailure.Dispose();
    }
}
