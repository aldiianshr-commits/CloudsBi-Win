using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Paths;

namespace CloudSBiian.VirtualDrive;

/// <summary>NTSTATUS values used by the file system (defined locally to avoid relying on binding internals).</summary>
internal static class NtStatus
{
    public const int Success = 0;
    public const int EndOfFile = unchecked((int)0xC0000011);
    public const int InvalidDeviceRequest = unchecked((int)0xC0000010);
    public const int AccessDenied = unchecked((int)0xC0000022);
    public const int ObjectNameInvalid = unchecked((int)0xC0000033);
    public const int ObjectNameNotFound = unchecked((int)0xC0000034);
    public const int ObjectNameCollision = unchecked((int)0xC0000035);
    public const int ObjectPathNotFound = unchecked((int)0xC000003A);
    public const int SharingViolation = unchecked((int)0xC0000043);
    public const int LogonFailure = unchecked((int)0xC000006D);
    public const int DiskFull = unchecked((int)0xC000007F);
    public const int InsufficientResources = unchecked((int)0xC000009A);
    public const int MediaWriteProtected = unchecked((int)0xC00000A2);
    public const int IoTimeout = unchecked((int)0xC00000B5);
    public const int FileIsADirectory = unchecked((int)0xC00000BA);
    public const int NotSupported = unchecked((int)0xC00000BB);
    public const int UnexpectedNetworkError = unchecked((int)0xC00000C4);
    public const int NotSameDevice = unchecked((int)0xC00000D4);
    public const int UnexpectedIoError = unchecked((int)0xC00000E9);
    public const int DirectoryNotEmpty = unchecked((int)0xC0000101);
    public const int NotADirectory = unchecked((int)0xC0000103);
    public const int CannotDelete = unchecked((int)0xC0000121);
    public const int NetworkUnreachable = unchecked((int)0xC000023C);

    public const uint FileDirectoryFile = 0x00000001;
    public const uint FileDeleteOnClose = 0x00001000;

    public const uint CleanupDelete = 0x01;
    public const uint CleanupSetAllocationSize = 0x02;

    public static int FromException(Exception ex) => ex switch
    {
        PathValidationException => ObjectNameInvalid,
        CloudProviderException c => c.Kind switch
        {
            CloudErrorKind.NotFound => ObjectNameNotFound,
            CloudErrorKind.AlreadyExists => ObjectNameCollision,
            CloudErrorKind.NotEmpty => DirectoryNotEmpty,
            CloudErrorKind.AuthenticationExpired or CloudErrorKind.AuthenticationFailed => LogonFailure,
            CloudErrorKind.PermissionDenied => AccessDenied,
            CloudErrorKind.RateLimited or CloudErrorKind.Timeout => IoTimeout,
            CloudErrorKind.Network => UnexpectedNetworkError,
            CloudErrorKind.DependencyMissing => NetworkUnreachable,
            CloudErrorKind.NotSupported => NotSupported,
            CloudErrorKind.Conflict => SharingViolation,
            CloudErrorKind.InsufficientStorage => DiskFull,
            CloudErrorKind.InvalidPath => ObjectNameInvalid,
            _ => UnexpectedIoError,
        },
        OperationCanceledException or TimeoutException => IoTimeout,
        UnauthorizedAccessException => AccessDenied,
        IOException io when io.HResult == unchecked((int)0x80070070) => DiskFull,
        IOException => UnexpectedIoError,
        _ => UnexpectedIoError,
    };
}
