namespace CloudSBiian.CloudProviders.Rclone;

public enum RcloneState
{
    /// <summary>Not checked yet (application still starting).</summary>
    NotChecked,
    /// <summary>A check is running.</summary>
    Checking,
    /// <summary>No rclone executable in any searched location.</summary>
    Missing,
    /// <summary>Found, but "rclone version" failed or reported a version older than <see cref="RcloneLocator.MinimumVersion"/>.</summary>
    Unusable,
    /// <summary>Found and version OK, but the rc daemon did not start or the backend list could not be read.</summary>
    StartFailed,
    /// <summary>Daemon running, backend catalog loaded.</summary>
    Ready,
}

/// <summary>Result of the rclone dependency check. Every field comes from an actual check.</summary>
public sealed record RcloneStatus(
    RcloneState State,
    string? ExecutablePath,
    string? Source,
    string? Version,
    int BackendCount,
    IReadOnlyList<RcloneCandidate> Searched,
    string Message,
    DateTimeOffset CheckedAt)
{
    public static RcloneStatus NotChecked { get; } = new(RcloneState.NotChecked, null, null, null, 0, [], "rclone has not been checked yet.", DateTimeOffset.MinValue);

    public bool IsReady => State == RcloneState.Ready;

    /// <summary>True when the user should be offered Download / Browse.</summary>
    public bool NeedsAction => State is RcloneState.Missing or RcloneState.Unusable or RcloneState.StartFailed;

    /// <summary>Short reason used as the Blocked note of rclone-based providers.</summary>
    public string BlockedNote => State switch
    {
        RcloneState.NotChecked or RcloneState.Checking => "Checking rclone…",
        RcloneState.Missing => "rclone is not installed. Use \"Download rclone\" or \"Browse…\" on the Accounts page, or set its path in Settings.",
        _ => Message,
    };
}
