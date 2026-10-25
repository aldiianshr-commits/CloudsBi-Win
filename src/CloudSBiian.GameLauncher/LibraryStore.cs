using System.Security.Cryptography;
using CloudSBiian.Core.Storage;

namespace CloudSBiian.GameLauncher;

public enum LaunchMode
{
    /// <summary>Run from the virtual drive; data is fetched on demand.</summary>
    CloudDirect,
    /// <summary>Run from the virtual drive with recorded-trace prefetch and pinning.</summary>
    HybridOptimized,
    /// <summary>Copy to a local folder first and run from there.</summary>
    FullLocal,
}

public sealed class LibraryEntry
{
    public required string Id { get; init; }

    public required string Name { get; set; }

    /// <summary>Null for applications that live only on a local disk.</summary>
    public string? AccountId { get; set; }

    /// <summary>Cloud folder that contains the game/app (normalized cloud path).</summary>
    public string? CloudFolder { get; set; }

    /// <summary>Executable relative to <see cref="CloudFolder"/> (or absolute for local-only entries).</summary>
    public required string Executable { get; set; }

    public string Arguments { get; set; } = string.Empty;

    /// <summary>Working directory relative to the game folder (empty = executable's folder), or absolute.</summary>
    public string? WorkingDirectory { get; set; }

    public LaunchMode Mode { get; set; } = LaunchMode.CloudDirect;

    /// <summary>0 = normal, 1 = high (pin while installed), 2 = pinned permanently.</summary>
    public int CachePriority { get; set; } = 1;

    /// <summary>Folder of the local copy (Full Local mode).</summary>
    public string? LocalPath { get; set; }

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset? LastLaunchAt { get; set; }

    public int? LastExitCode { get; set; }

    public string? LastStatus { get; set; }

    public int? LastPid { get; set; }

    public long? LastPidStartTicks { get; set; }

    public string TraceId => "lib:" + Id;

    public bool IsCloudBased => AccountId != null && CloudFolder != null;

    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}

public sealed class LibraryStore
{
    private readonly AppDatabase _db;

    public LibraryStore(AppDatabase db) => _db = db;

    public event EventHandler? Changed;

    public IReadOnlyList<LibraryEntry> GetAll() =>
        _db.Query("SELECT id, name, account_id, cloud_folder, executable, arguments, working_dir, mode, cache_priority, local_path, added_at, last_launch_at, last_exit_code, last_status, last_pid, last_pid_start FROM library ORDER BY name COLLATE NOCASE", Map);

    public LibraryEntry? Get(string id) =>
        _db.Query("SELECT id, name, account_id, cloud_folder, executable, arguments, working_dir, mode, cache_priority, local_path, added_at, last_launch_at, last_exit_code, last_status, last_pid, last_pid_start FROM library WHERE id=@id", Map, ("@id", id)).FirstOrDefault();

    public void Save(LibraryEntry e)
    {
        _db.Execute(
            """
            INSERT INTO library(id, name, account_id, cloud_folder, executable, arguments, working_dir, mode, cache_priority, local_path, added_at, last_launch_at, last_exit_code, last_status, last_pid, last_pid_start)
            VALUES(@id,@name,@acc,@folder,@exe,@args,@wd,@mode,@prio,@local,@added,@launch,@exit,@status,@pid,@pidstart)
            ON CONFLICT(id) DO UPDATE SET name=@name, account_id=@acc, cloud_folder=@folder, executable=@exe, arguments=@args, working_dir=@wd, mode=@mode,
              cache_priority=@prio, local_path=@local, last_launch_at=@launch, last_exit_code=@exit, last_status=@status, last_pid=@pid, last_pid_start=@pidstart
            """,
            ("@id", e.Id), ("@name", e.Name), ("@acc", e.AccountId), ("@folder", e.CloudFolder), ("@exe", e.Executable), ("@args", e.Arguments),
            ("@wd", e.WorkingDirectory), ("@mode", e.Mode.ToString()), ("@prio", e.CachePriority), ("@local", e.LocalPath),
            ("@added", e.AddedAt.ToUnixTimeMilliseconds()), ("@launch", e.LastLaunchAt?.ToUnixTimeMilliseconds()), ("@exit", e.LastExitCode),
            ("@status", e.LastStatus), ("@pid", e.LastPid), ("@pidstart", e.LastPidStartTicks));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        _db.Execute("DELETE FROM library WHERE id=@id", ("@id", id));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveForAccount(string accountId)
    {
        _db.Execute("UPDATE library SET last_status=@s WHERE account_id=@a", ("@s", "Account removed"), ("@a", accountId));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static LibraryEntry Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Name = r.GetString(1),
        AccountId = AppDatabase.GetStringOrNull(r, 2),
        CloudFolder = AppDatabase.GetStringOrNull(r, 3),
        Executable = r.GetString(4),
        Arguments = r.GetString(5),
        WorkingDirectory = AppDatabase.GetStringOrNull(r, 6),
        Mode = Enum.TryParse<LaunchMode>(r.GetString(7), out var m) ? m : LaunchMode.CloudDirect,
        CachePriority = (int)r.GetInt64(8),
        LocalPath = AppDatabase.GetStringOrNull(r, 9),
        AddedAt = Clock.FromMs(r.GetInt64(10)),
        LastLaunchAt = AppDatabase.GetInt64OrNull(r, 11) is long l ? Clock.FromMs(l) : null,
        LastExitCode = AppDatabase.GetInt64OrNull(r, 12) is long x ? (int)x : null,
        LastStatus = AppDatabase.GetStringOrNull(r, 13),
        LastPid = AppDatabase.GetInt64OrNull(r, 14) is long p ? (int)p : null,
        LastPidStartTicks = AppDatabase.GetInt64OrNull(r, 15),
    };
}
