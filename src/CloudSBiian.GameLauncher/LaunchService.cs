using System.Collections.Concurrent;
using System.Diagnostics;
using CloudSBiian.Core.Logging;
using CloudSBiian.Core.Paths;
using CloudSBiian.PredictionEngine;

namespace CloudSBiian.GameLauncher;

public sealed class RunningApp
{
    internal RunningApp(LibraryEntry entry, Process process, string executable)
    {
        Entry = entry;
        Process = process;
        Executable = executable;
        StartedAt = DateTimeOffset.Now;
    }

    public LibraryEntry Entry { get; }

    public Process Process { get; }

    public string Executable { get; }

    public DateTimeOffset StartedAt { get; }
}

/// <summary>
/// Starts and stops library entries. Does not bypass DRM, anti-cheat or launcher requirements:
/// it starts the configured executable exactly like Explorer would.
/// </summary>
public sealed class LaunchService
{
    private static readonly TimeSpan TraceDuration = TimeSpan.FromMinutes(10);
    private readonly LibraryStore _library;
    private readonly Func<string, string, string?> _resolveDrivePath;
    private readonly PredictionService? _prediction;
    private readonly IAppLog _log;
    private readonly ConcurrentDictionary<string, RunningApp> _running = new();
    private readonly object _gate = new();

    public LaunchService(LibraryStore library, Func<string, string, string?> resolveDrivePath, PredictionService? prediction, IAppLog? log = null)
    {
        _library = library;
        _resolveDrivePath = resolveDrivePath;
        _prediction = prediction;
        _log = log ?? NullLog.Instance;
    }

    public event EventHandler<string>? Changed;

    public IReadOnlyCollection<RunningApp> Running => _running.Values.ToList();

    public bool IsRunning(string entryId) => _running.ContainsKey(entryId);

    /// <summary>Folder the entry runs from (virtual drive folder or local copy).</summary>
    public string? ResolveGameFolder(LibraryEntry e, out string? error)
    {
        error = null;
        if (e.Mode == LaunchMode.FullLocal)
        {
            if (string.IsNullOrEmpty(e.LocalPath) || !Directory.Exists(e.LocalPath))
            {
                error = "The local copy does not exist yet. Use 'Optimize & Prepare' / 'Install locally' first.";
                return null;
            }

            return e.LocalPath;
        }

        if (!e.IsCloudBased)
        {
            var dir = Path.GetDirectoryName(e.Executable);
            if (dir == null || !Directory.Exists(dir))
            {
                error = "The application's folder was not found.";
                return null;
            }

            return dir;
        }

        var path = _resolveDrivePath(e.AccountId!, e.CloudFolder!);
        if (path == null)
        {
            error = "No mounted CloudSBiian drive exposes this account. Mount a drive on the Virtual Drive page.";
        }

        return path;
    }

    public string? ResolveExecutable(LibraryEntry e, out string? error)
    {
        if (!e.IsCloudBased && e.Mode != LaunchMode.FullLocal)
        {
            error = File.Exists(e.Executable) ? null : $"Executable not found: {e.Executable}";
            return error == null ? e.Executable : null;
        }

        var folder = ResolveGameFolder(e, out error);
        if (folder == null)
        {
            return null;
        }

        var exe = Path.Combine(folder, CloudPath.Normalize(e.Executable).TrimStart('/').Replace('/', '\\'));
        if (!File.Exists(exe))
        {
            error = $"Executable not found: {exe}";
            return null;
        }

        return exe;
    }

    public string ResolveWorkingDirectory(LibraryEntry e, string executable)
    {
        if (string.IsNullOrWhiteSpace(e.WorkingDirectory))
        {
            return Path.GetDirectoryName(executable)!;
        }

        if (Path.IsPathRooted(e.WorkingDirectory))
        {
            return e.WorkingDirectory;
        }

        var folder = ResolveGameFolder(e, out _) ?? Path.GetDirectoryName(executable)!;
        return Path.GetFullPath(Path.Combine(folder, e.WorkingDirectory));
    }

    public static string? ValidateArguments(string? arguments)
    {
        if (arguments == null)
        {
            return null;
        }

        if (arguments.Contains('\n') || arguments.Contains('\r') || arguments.Contains('\0'))
        {
            return "Launch arguments must be a single line.";
        }

        return arguments.Length > 4000 ? "Launch arguments are too long." : null;
    }

    public RunningApp Launch(LibraryEntry entry)
    {
        if (_running.ContainsKey(entry.Id))
        {
            throw new InvalidOperationException($"{entry.Name} is already running.");
        }

        var argError = ValidateArguments(entry.Arguments);
        if (argError != null)
        {
            throw new InvalidOperationException(argError);
        }

        var exe = ResolveExecutable(entry, out var error) ?? throw new InvalidOperationException(error);
        var workDir = ResolveWorkingDirectory(entry, exe);
        if (!Directory.Exists(workDir))
        {
            throw new InvalidOperationException($"Working directory not found: {workDir}");
        }

        var psi = new ProcessStartInfo(exe)
        {
            Arguments = entry.Arguments ?? string.Empty,
            WorkingDirectory = workDir,
            UseShellExecute = false,
        };

        if (entry.IsCloudBased && entry.Mode != LaunchMode.FullLocal)
        {
            _prediction?.StartTrace(entry.TraceId);
        }

        // Subscribe before starting and serialize with OnExited so a very short-lived process
        // can never have its exit recorded before the "Running" state is saved.
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var app = new RunningApp(entry, process, exe);
        int pid;
        process.Exited += (_, _) => OnExited(entry.Id);
        lock (_gate)
        {
            _running[entry.Id] = app;
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("The process could not be started.");
                }
            }
            catch
            {
                _running.TryRemove(entry.Id, out _);
                _prediction?.StopTrace(entry.TraceId);
                process.Dispose();
                throw;
            }

            entry.LastLaunchAt = DateTimeOffset.Now;
            entry.LastStatus = "Running";
            entry.LastExitCode = null;
            pid = process.Id;
            entry.LastPid = pid;
            entry.LastPidStartTicks = SafeStartTicks(process);
            _library.Save(entry);
        }

        _log.Info("library", $"Launched {entry.Name} (PID {pid}, mode {entry.Mode}).");

        if (_prediction != null && _prediction.IsTracing(entry.TraceId))
        {
            _ = Task.Delay(TraceDuration).ContinueWith(_ => _prediction.StopTrace(entry.TraceId), TaskScheduler.Default);
        }

        Changed?.Invoke(this, entry.Id);
        return app;
    }

    /// <summary>Asks the app to close, then terminates its process tree after <paramref name="grace"/>.</summary>
    public async Task StopAsync(string entryId, TimeSpan grace)
    {
        if (!_running.TryGetValue(entryId, out var app))
        {
            return;
        }

        try
        {
            if (!app.Process.HasExited)
            {
                app.Process.CloseMainWindow();
                using var cts = new CancellationTokenSource(grace);
                try
                {
                    await app.Process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    app.Process.Kill(entireProcessTree: true);
                    await app.Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }

        _log.Info("library", $"Stopped {app.Entry.Name}.");
    }

    /// <summary>After an app restart, re-attaches to processes that are still running, otherwise records the end.</summary>
    public void RecoverAfterRestart()
    {
        foreach (var entry in _library.GetAll().Where(e => e.LastStatus == "Running"))
        {
            Process? p = null;
            try
            {
                if (entry.LastPid is int pid)
                {
                    p = Process.GetProcessById(pid);
                    if (SafeStartTicks(p) != entry.LastPidStartTicks)
                    {
                        p.Dispose();
                        p = null;
                    }
                }
            }
            catch (ArgumentException)
            {
                p = null;
            }

            if (p != null)
            {
                p.EnableRaisingEvents = true;
                _running[entry.Id] = new RunningApp(entry, p, entry.Executable);
                p.Exited += (_, _) => OnExited(entry.Id);
                _log.Info("library", $"Re-attached to running {entry.Name} (PID {p.Id}).");
            }
            else
            {
                entry.LastStatus = "Ended while CloudSBiian was closed (exit code unknown)";
                _library.Save(entry);
            }
        }
    }

    private void OnExited(string entryId)
    {
        RunningApp? app;
        LibraryEntry entry;
        int? code = null;
        lock (_gate)
        {
            if (!_running.TryRemove(entryId, out app))
            {
                return;
            }

            try
            {
                code = app.Process.ExitCode;
            }
            catch (InvalidOperationException)
            {
            }

            entry = _library.Get(entryId) ?? app.Entry;
            entry.LastExitCode = code;
            entry.LastStatus = code == null ? "Exited" : $"Exited with code {code}";
            entry.LastPid = null;
            entry.LastPidStartTicks = null;
            _library.Save(entry);
        }

        _prediction?.StopTrace(app.Entry.TraceId);
        _log.Info("library", $"{entry.Name} exited (code {code?.ToString() ?? "?"}) after {(DateTimeOffset.Now - app.StartedAt).TotalMinutes:F1} min.");
        app.Process.Dispose();
        Changed?.Invoke(this, entryId);
    }

    private static long? SafeStartTicks(Process p)
    {
        try
        {
            return p.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
