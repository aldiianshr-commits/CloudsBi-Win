namespace CloudSBiian.Core.Abstractions;

public enum ProviderKind
{
    GoogleDrive,
    OneDrive,
    Dropbox,
    WebDav,
    Sftp,
    S3,
    TeraBox,
    Smb,
    LocalFolder,
    GenericRclone,
    /// <summary>In-memory provider used only by automated tests and diagnostics.</summary>
    Mock,
}

/// <summary>Honest implementation status shown in the UI and docs.</summary>
public enum ImplementationStatus
{
    /// <summary>Integrated and covered by automated tests for the claimed capabilities.</summary>
    Supported,
    /// <summary>Available but with limitations or without tests against the real service.</summary>
    Experimental,
    /// <summary>Cannot currently be delivered reliably (missing API/backend).</summary>
    Blocked,
    /// <summary>Not implemented yet.</summary>
    Planned,
}

public enum AccountConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    AuthenticationExpired,
    AuthenticationFailed,
    Offline,
    Error,
    Blocked,
}
