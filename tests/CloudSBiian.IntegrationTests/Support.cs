using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CloudSBiian.CloudProviders.Rclone;
using CloudSBiian.VirtualDrive;

namespace CloudSBiian.IntegrationTests;

/// <summary>Runs only when the WinFsp driver is installed (real kernel-level mount).</summary>
public sealed class WinFspFactAttribute : FactAttribute
{
    public WinFspFactAttribute()
    {
        if (!DriveManager.CheckWinFsp().Installed)
        {
            Skip = "WinFsp is not installed on this machine.";
        }
    }
}

/// <summary>Runs only when an rclone executable is available.</summary>
public sealed class RcloneFactAttribute : FactAttribute
{
    public RcloneFactAttribute()
    {
        if (RcloneLocator.Find(null) == null)
        {
            Skip = "rclone is not installed on this machine.";
        }
    }
}

public static class Net
{
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static async Task WaitForPortAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
    }
}

/// <summary>An "rclone serve ..." test server (WebDAV, SFTP, S3) over a local folder.</summary>
public sealed class RcloneServer : IDisposable
{
    private readonly Process _process;

    private RcloneServer(Process process, int port, string directory)
    {
        _process = process;
        Port = port;
        Directory = directory;
    }

    public int Port { get; }

    public string Directory { get; }

    public static async Task<RcloneServer> StartAsync(string protocol, string directory, params string[] extraArgs)
    {
        var exe = RcloneLocator.Find(null)!;
        var port = Net.FreePort();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add(protocol);
        psi.ArgumentList.Add(directory);
        psi.ArgumentList.Add("--addr");
        psi.ArgumentList.Add($"127.0.0.1:{port}");
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(string.Empty);
        foreach (var a in extraArgs)
        {
            psi.ArgumentList.Add(a);
        }

        var p = Process.Start(psi)!;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await Net.WaitForPortAsync(port, TimeSpan.FromSeconds(20));
        return new RcloneServer(p, port, directory);
    }

    public void Dispose()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }
}

/// <summary>Collects benchmark numbers; CI publishes the JSON file next to the test results.</summary>
public static class Bench
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, object> Results = new();

    public static void Record(string name, object value)
    {
        lock (Gate)
        {
            Results[name] = value;
            var file = Path.Combine(AppContext.BaseDirectory, "bench-results.json");
            File.WriteAllText(file, JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
