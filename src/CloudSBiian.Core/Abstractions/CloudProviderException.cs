namespace CloudSBiian.Core.Abstractions;

public enum CloudErrorKind
{
    Unknown,
    NotFound,
    AlreadyExists,
    NotEmpty,
    AuthenticationFailed,
    AuthenticationExpired,
    PermissionDenied,
    RateLimited,
    Network,
    Timeout,
    NotSupported,
    Conflict,
    InsufficientStorage,
    InvalidPath,
    DependencyMissing,
}

/// <summary>
/// Normalized provider error. Messages must never contain secrets; providers pass them
/// through <see cref="Security.SecretRedactor"/> before throwing.
/// </summary>
public class CloudProviderException : Exception
{
    public CloudProviderException(CloudErrorKind kind, string message, Exception? inner = null, TimeSpan? retryAfter = null)
        : base(Security.SecretRedactor.Redact(message), inner)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    public CloudErrorKind Kind { get; }

    public TimeSpan? RetryAfter { get; }

    /// <summary>True for errors where a later retry can reasonably succeed.</summary>
    public bool IsTransient => Kind is CloudErrorKind.Network or CloudErrorKind.Timeout or CloudErrorKind.RateLimited;

    public bool IsAuthentication => Kind is CloudErrorKind.AuthenticationExpired or CloudErrorKind.AuthenticationFailed;

    public static CloudProviderException NotSupported(string operation, ProviderKind kind) =>
        new(CloudErrorKind.NotSupported, $"Operation '{operation}' is not supported by provider {kind}.");

    public static CloudProviderException NotFound(string path) =>
        new(CloudErrorKind.NotFound, $"Not found: {path}");

    /// <summary>Maps an arbitrary exception to a <see cref="CloudErrorKind"/>.</summary>
    public static CloudErrorKind Classify(Exception ex) => ex switch
    {
        CloudProviderException c => c.Kind,
        OperationCanceledException => CloudErrorKind.Timeout,
        TimeoutException => CloudErrorKind.Timeout,
        HttpRequestException => CloudErrorKind.Network,
        IOException io when io.InnerException is System.Net.Sockets.SocketException => CloudErrorKind.Network,
        System.Net.Sockets.SocketException => CloudErrorKind.Network,
        UnauthorizedAccessException => CloudErrorKind.PermissionDenied,
        FileNotFoundException => CloudErrorKind.NotFound,
        DirectoryNotFoundException => CloudErrorKind.NotFound,
        _ => CloudErrorKind.Unknown,
    };
}
