using System.Security.Cryptography;
using CloudSBiian.CacheEngine;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Core.Logging;
using CloudSBiian.Core.Paths;
using CloudSBiian.Monitoring;
using CloudSBiian.PredictionEngine;

namespace CloudSBiian.GameLauncher;

public sealed record PrepareReport(
    IReadOnlyList<CheckResult> Checks,
    long BytesToPrefetch,
    long BytesAlreadyCached,
    int TraceChunks,
    long GameSizeBytes,
    int GameFiles,
    double? EstimatedSeconds,
    string EstimateNote)
{
    public bool HasErrors => Checks.Any(c => c.Severity == CheckSeverity.Error);
}

public sealed record PrepareProgress(string Stage, long Done, long Total);

/// <summary>
/// "Optimize &amp; Prepare": checks that an entry can start and warms the cache with the data that
/// earlier launches actually read (recorded trace). Never downloads a whole game without the user's
/// explicit request (Full Local install).
/// </summary>
public sealed class PrepareService
{
    private readonly IProviderResolver _providers;
    private readonly CacheService _cache;
    private readonly UploadQueue _uploads;
    private readonly AccessProfileStore _profiles;
    private readonly TransferTracker _transfers;
    private readonly Func<AppSettings> _settings;
    private readonly LaunchService _launcher;
    private readonly IAppLog _log;

    public PrepareService(
        IProviderResolver providers,
        CacheService cache,
        UploadQueue uploads,
        AccessProfileStore profiles,
        TransferTracker transfers,
        Func<AppSettings> settings,
        LaunchService launcher,
        IAppLog? log = null)
    {
        _providers = providers;
        _cache = cache;
        _uploads = uploads;
        _profiles = profiles;
        _transfers = transfers;
        _settings = settings;
        _launcher = launcher;
        _log = log ?? NullLog.Instance;
    }

    public async Task<PrepareReport> AnalyzeAsync(LibraryEntry entry, IProgress<PrepareProgress>? progress, CancellationToken ct)
    {
        var checks = new List<CheckResult>();
        long gameSize = 0;
        var gameFiles = 0;
        long toPrefetch = 0;
        long cached = 0;
        var traceChunks = 0;

        var argError = LaunchService.ValidateArguments(entry.Arguments);
        checks.Add(argError == null
            ? new CheckResult("Launch arguments", CheckSeverity.Ok, string.IsNullOrEmpty(entry.Arguments) ? "No arguments." : $"Arguments: {entry.Arguments}")
            : new CheckResult("Launch arguments", CheckSeverity.Error, argError, "Edit the launch arguments."));

        if (!entry.IsCloudBased)
        {
            var exeOk = File.Exists(entry.Executable);
            checks.Add(new CheckResult("Executable", exeOk ? CheckSeverity.Ok : CheckSeverity.Error,
                exeOk ? $"Found {entry.Executable}" : $"Missing: {entry.Executable}", exeOk ? null : "Select the executable again."));
            return new PrepareReport(checks, 0, 0, 0, 0, 0, null, "Local application: nothing to prepare.");
        }

        var accountId = entry.AccountId!;
        progress?.Report(new PrepareProgress("Checking connection", 0, 0));
        var provider = _providers.TryGetProvider(accountId);
        if (provider == null)
        {
            checks.Add(new CheckResult("Connection", CheckSeverity.Error, "The account is not connected.", "Open Cloud Accounts and use Connect/Reconnect."));
        }
        else
        {
            var test = await provider.TestConnectionAsync(ct).ConfigureAwait(false);
            checks.Add(test.Status switch
            {
                ConnectionTestStatus.Ok => new CheckResult("Connection", CheckSeverity.Ok, $"Connected ({test.Latency.TotalMilliseconds:F0} ms)."),
                ConnectionTestStatus.AuthenticationExpired or ConnectionTestStatus.AuthenticationFailed =>
                    new CheckResult("Credentials", CheckSeverity.Error, "The sign-in for this account is no longer valid.", "Use Reconnect on the Cloud Accounts page."),
                _ => new CheckResult("Connection", CheckSeverity.Error, test.Message, "Check the network and retry."),
            });
        }

        // Main executable and folder contents.
        var files = new List<CloudEntry>();
        if (provider != null)
        {
            progress?.Report(new PrepareProgress("Reading game folder", 0, 0));
            try
            {
                await CollectAsync(accountId, provider, CloudPath.Normalize(entry.CloudFolder), files, 0, ct).ConfigureAwait(false);
                gameFiles = files.Count(f => !f.IsDirectory);
                gameSize = files.Where(f => !f.IsDirectory).Sum(f => f.Size);
                var exePath = CloudPath.CombineRelative(entry.CloudFolder!, entry.Executable);
                var exe = files.FirstOrDefault(f => string.Equals(f.Path, exePath, StringComparison.OrdinalIgnoreCase));
                checks.Add(exe == null
                    ? new CheckResult("Executable", CheckSeverity.Error, $"Not found in the cloud folder: {exePath}", "Select the executable again.")
                    : new CheckResult("Executable", CheckSeverity.Ok, $"Found {exePath} ({Format(exe.Size)})."));
                var root = CloudPath.Normalize(entry.CloudFolder);
                checks.AddRange(CompatibilityScanner.Scan(files.Select(f => (CloudPath.Rebase(f.Path, root, CloudPath.Root).TrimStart('/'), f.IsDirectory))));
            }
            catch (CloudProviderException ex)
            {
                checks.Add(new CheckResult("Game folder", CheckSeverity.Error, ex.Message, "Verify the cloud folder in the entry settings."));
            }
        }

        if (entry.Mode != LaunchMode.FullLocal)
        {
            var drive = _launcher.ResolveGameFolder(entry, out var driveError);
            checks.Add(drive == null
                ? new CheckResult("Virtual drive", CheckSeverity.Error, driveError ?? "Drive not mounted.", "Mount a drive that includes this account.")
                : new CheckResult("Virtual drive", CheckSeverity.Ok, $"Available at {drive}."));
        }
        else
        {
            var local = _launcher.ResolveGameFolder(entry, out var localError);
            checks.Add(local == null
                ? new CheckResult("Local copy", CheckSeverity.Warning, localError ?? "Not installed.", "Use 'Install locally'.")
                : new CheckResult("Local copy", CheckSeverity.Ok, $"Installed at {local}."));
        }

        // Known-needed data from previous launches.
        var trace = _profiles.GetTrace(entry.TraceId);
        traceChunks = trace.Count;
        var byFile = files.Where(f => !f.IsDirectory).ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var group in trace.GroupBy(t => t.Path))
        {
            if (!byFile.TryGetValue(group.Key, out var file))
            {
                continue;
            }

            var missing = _cache.MissingChunks(accountId, file).ToHashSet();
            foreach (var t in group)
            {
                var size = Math.Min(_cache.ChunkSize, file.Size - ((long)t.ChunkIndex * _cache.ChunkSize));
                if (size <= 0)
                {
                    continue;
                }

                if (missing.Contains(t.ChunkIndex))
                {
                    toPrefetch += size;
                }
                else
                {
                    cached += size;
                }
            }
        }

        checks.Add(traceChunks == 0
            ? new CheckResult("Access profile", CheckSeverity.Info,
                "No recorded launch yet. The first launch records which data the game reads; later preparations prefetch exactly that.")
            : new CheckResult("Access profile", CheckSeverity.Ok,
                $"{traceChunks} recorded blocks: {Format(cached)} already cached, {Format(toPrefetch)} to prefetch."));

        // Space.
        var s = _settings();
        var status = _cache.GetStatus(0);
        var budgetFree = Math.Max(0, s.CacheBudgetBytes - status.PhysicalBytes);
        if (entry.Mode == LaunchMode.FullLocal)
        {
            var target = entry.LocalPath ?? s.DefaultFullLocalDirectory;
            var free = FreeSpace(target);
            checks.Add(free == null
                ? new CheckResult("Disk space", CheckSeverity.Warning, "Could not determine free space for the local copy.", "Choose a local folder.")
                : free.Value - gameSize < s.MinFreeDiskBytes
                    ? new CheckResult("Disk space", CheckSeverity.Error, $"Not enough space: game {Format(gameSize)}, free {Format(free.Value)} (keeping {Format(s.MinFreeDiskBytes)} free).", "Free disk space or pick another folder.")
                    : new CheckResult("Disk space", CheckSeverity.Ok, $"{Format(free.Value)} free for a {Format(gameSize)} copy."));
        }
        else
        {
            checks.Add(toPrefetch > budgetFree && toPrefetch > 0
                ? new CheckResult("Cache space", CheckSeverity.Warning, $"Prefetch needs {Format(toPrefetch)} but only {Format(budgetFree)} of the cache budget is free; older data will be evicted.", "Increase the cache budget in Cache Manager.")
                : new CheckResult("Cache space", CheckSeverity.Ok, $"{Format(budgetFree)} of cache budget free; disk free: {(status.DiskFreeBytes is long d ? Format(d) : "N/A")}."));
            if (status.DiskFreeBytes is long diskFree && diskFree < s.MinFreeDiskBytes)
            {
                checks.Add(new CheckResult("Disk space", CheckSeverity.Warning, $"Free disk space ({Format(diskFree)}) is below the configured minimum ({Format(s.MinFreeDiskBytes)}); caching is limited.", "Free disk space."));
            }
        }

        // Pending transfers on this account.
        var pending = _uploads.GetAll(accountId);
        if (pending.Count > 0)
        {
            var conflicts = pending.Count(p => p.State == UploadState.Conflict);
            checks.Add(new CheckResult("Pending uploads", conflicts > 0 ? CheckSeverity.Warning : CheckSeverity.Info,
                $"{pending.Count} pending upload(s) on this account{(conflicts > 0 ? $", {conflicts} in conflict" : string.Empty)}.",
                conflicts > 0 ? "Resolve conflicts on the Transfer Queue page." : null));
        }
        else
        {
            checks.Add(new CheckResult("Pending uploads", CheckSeverity.Ok, "No pending uploads."));
        }

        var speed = _transfers.MeasuredSpeed(accountId);
        var bytesForEstimate = entry.Mode == LaunchMode.FullLocal ? gameSize : toPrefetch;
        double? estimate = speed is > 0 && bytesForEstimate > 0 ? bytesForEstimate / speed.Value : null;
        var note = speed is > 0
            ? $"Estimate based on measured average speed {Format((long)speed.Value)}/s for this account (approximate)."
            : "No speed measurement yet for this account; no time estimate.";

        return new PrepareReport(checks, toPrefetch, cached, traceChunks, gameSize, gameFiles, estimate, note);
    }

    /// <summary>Prefetches the recorded trace in first-access order (P1). Cancellable.</summary>
    public async Task<long> PrefetchTraceAsync(LibraryEntry entry, IProgress<PrepareProgress>? progress, CancellationToken ct)
    {
        if (!entry.IsCloudBased)
        {
            return 0;
        }

        var provider = _providers.TryGetProvider(entry.AccountId!)
            ?? throw new CloudProviderException(CloudErrorKind.Network, "The account is not connected.");
        if (entry.CachePriority >= 1)
        {
            _cache.Pin(entry.AccountId!, entry.CloudFolder!);
        }

        var trace = _profiles.GetTrace(entry.TraceId);
        var total = (long)trace.Count * _cache.ChunkSize;
        long done = 0;
        long bytes = 0;
        foreach (var group in trace.GroupBy(t => t.Path).OrderBy(g => g.Min(t => t.FirstMs)))
        {
            ct.ThrowIfCancellationRequested();
            var file = await _cache.Metadata.StatAsync(entry.AccountId!, provider, group.Key, ct).ConfigureAwait(false);
            if (file == null || file.IsDirectory)
            {
                done += group.Count() * (long)_cache.ChunkSize;
                continue;
            }

            foreach (var batch in group.OrderBy(t => t.FirstMs).Select(t => t.ChunkIndex).Chunk(8))
            {
                ct.ThrowIfCancellationRequested();
                bytes += await _cache.PrefetchAsync(entry.AccountId!, file, batch, FetchPriority.P1, ct).ConfigureAwait(false);
                done += batch.Length * (long)_cache.ChunkSize;
                progress?.Report(new PrepareProgress($"Prefetching {CloudPath.GetName(file.Path)}", Math.Min(done, total), total));
            }
        }

        _log.Info("library", $"Prepared {entry.Name}: {Format(bytes)} prefetched.");
        return bytes;
    }

    /// <summary>
    /// Full Local install: copies the cloud folder to <paramref name="targetDirectory"/>, verifying every
    /// file (size + SHA-256 of what was written vs. what was received). Existing identical files are skipped.
    /// </summary>
    public async Task InstallLocalAsync(LibraryEntry entry, string targetDirectory, IProgress<PrepareProgress>? progress, CancellationToken ct)
    {
        var provider = _providers.TryGetProvider(entry.AccountId!)
            ?? throw new CloudProviderException(CloudErrorKind.Network, "The account is not connected.");
        var root = CloudPath.Normalize(entry.CloudFolder);
        var files = new List<CloudEntry>();
        await CollectAsync(entry.AccountId!, provider, root, files, 0, ct, maxDepth: 64).ConfigureAwait(false);
        var total = files.Where(f => !f.IsDirectory).Sum(f => f.Size);
        var s = _settings();
        var free = FreeSpace(targetDirectory);
        if (free != null && free.Value - total < s.MinFreeDiskBytes)
        {
            throw new IOException($"Not enough disk space: need {Format(total)}, free {Format(free.Value)} (keeping {Format(s.MinFreeDiskBytes)} free).");
        }

        Directory.CreateDirectory(targetDirectory);
        long done = 0;
        foreach (var f in files.OrderBy(f => f.IsDirectory ? 0 : 1).ThenBy(f => f.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var rel = CloudPath.Rebase(f.Path, root, CloudPath.Root);
            var local = CloudPath.ToLocalPath(targetDirectory, rel);
            if (f.IsDirectory)
            {
                Directory.CreateDirectory(local);
                continue;
            }

            var existing = new FileInfo(local);
            if (existing.Exists && existing.Length == f.Size && existing.LastWriteTimeUtc == f.Modified.UtcDateTime)
            {
                done += f.Size;
                progress?.Report(new PrepareProgress($"Up to date: {f.Name}", done, total));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            var tmp = local + ".csb-part";
            using var transfer = _transfers.Begin(entry.AccountId!, f.Path, TransferDirection.LocalInstall, f.Size);
            byte[] receivedHash;
            try
            {
                await using (var src = await provider.OpenReadAsync(f.Path, 0, null, ct).ConfigureAwait(false))
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[1024 * 1024];
                    int n;
                    while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        sha.AppendData(buffer, 0, n);
                        await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        transfer.Add(n);
                        done += n;
                        progress?.Report(new PrepareProgress($"Copying {f.Name}", done, total));
                    }

                    receivedHash = sha.GetHashAndReset();
                }

                var written = new FileInfo(tmp);
                if (written.Length != f.Size)
                {
                    throw new IOException($"Size mismatch for {f.Path}: expected {f.Size}, got {written.Length}.");
                }

                byte[] diskHash;
                await using (var verify = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    diskHash = await SHA256.HashDataAsync(verify, ct).ConfigureAwait(false);
                }

                if (!CryptographicOperations.FixedTimeEquals(receivedHash, diskHash))
                {
                    throw new IOException($"Verification failed for {f.Path}: data on disk differs from data received.");
                }

                File.Move(tmp, local, overwrite: true);
                File.SetLastWriteTimeUtc(local, f.Modified.UtcDateTime);
                transfer.Complete();
            }
            catch (Exception ex)
            {
                transfer.Fail(ex.Message);
                try
                {
                    File.Delete(tmp);
                }
                catch (IOException)
                {
                }

                throw;
            }
        }

        entry.LocalPath = targetDirectory;
        _log.Info("library", $"Installed {entry.Name} locally at {targetDirectory} ({Format(total)}, verified).");
    }

    private async Task CollectAsync(string accountId, ICloudStorageProvider provider, string folder, List<CloudEntry> into, int depth, CancellationToken ct, int maxDepth = 6)
    {
        var listing = await _cache.Metadata.ListAsync(accountId, provider, folder, ct).ConfigureAwait(false);
        foreach (var e in listing.Entries)
        {
            into.Add(e);
            if (e.IsDirectory && depth < maxDepth)
            {
                await CollectAsync(accountId, provider, e.Path, into, depth + 1, ct, maxDepth).ConfigureAwait(false);
            }
        }
    }

    private static long? FreeSpace(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            return root == null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }

    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return $"{v:0.##} {units[u]}";
    }
}
