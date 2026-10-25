namespace CloudSBiian.Core.Abstractions;

/// <summary>
/// Priority of a data request. Lower value = more urgent. Active reads (P0) always win over prefetch.
/// </summary>
public enum FetchPriority
{
    /// <summary>Active read requested by the user or an application right now.</summary>
    P0 = 0,
    /// <summary>Very likely needed soon (sequential read-ahead, recorded launch traces).</summary>
    P1 = 1,
    /// <summary>Probably needed (co-access predictions).</summary>
    P2 = 2,
    /// <summary>Low-priority speculative prefetch.</summary>
    P3 = 3,
}

/// <summary>Gives lower layers access to connected providers without depending on the account UI logic.</summary>
public interface IProviderResolver
{
    ICloudStorageProvider? TryGetProvider(string accountId);

    void ReportFailure(string accountId, Exception ex);
}
