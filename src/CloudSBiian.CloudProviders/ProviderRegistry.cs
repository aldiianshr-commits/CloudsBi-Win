using CloudSBiian.CloudProviders.Rclone;
using CloudSBiian.Core.Abstractions;

namespace CloudSBiian.CloudProviders;

public enum ProviderAuthKind
{
    /// <summary>Browser-based OAuth handled by "rclone authorize".</summary>
    OAuthBrowser,
    /// <summary>Fields entered by the user (host, user, password, keys...).</summary>
    Form,
    /// <summary>A local or UNC folder path.</summary>
    FolderPath,
    None,
}

/// <summary>Static description of a provider type. Status is the honest, documented status.</summary>
public sealed record ProviderDescriptor(
    ProviderKind Kind,
    string DisplayName,
    string? RcloneBackend,
    ProviderAuthKind Auth,
    ImplementationStatus Status,
    string StatusNote,
    bool UsesRclone,
    bool AllowsMultipleAccounts = true);

public static class ProviderRegistry
{
    public static readonly IReadOnlyList<ProviderDescriptor> All =
    [
        new(ProviderKind.GoogleDrive, "Google Drive", "drive", ProviderAuthKind.OAuthBrowser, ImplementationStatus.Experimental,
            "Via the rclone 'drive' backend (official Google Drive API). Adapter is tested in CI against rclone; not yet verified against a live Google account by this project.",
            UsesRclone: true),
        new(ProviderKind.OneDrive, "Microsoft OneDrive", "onedrive", ProviderAuthKind.OAuthBrowser, ImplementationStatus.Experimental,
            "Via the rclone 'onedrive' backend (Microsoft Graph API). Not yet verified against a live account by this project.",
            UsesRclone: true),
        new(ProviderKind.Dropbox, "Dropbox", "dropbox", ProviderAuthKind.OAuthBrowser, ImplementationStatus.Experimental,
            "Via the rclone 'dropbox' backend (official Dropbox API). Not yet verified against a live account by this project.",
            UsesRclone: true),
        new(ProviderKind.WebDav, "WebDAV", null, ProviderAuthKind.Form, ImplementationStatus.Supported,
            "Native implementation (PROPFIND/GET Range/PUT/MOVE/DELETE/MKCOL). Integration-tested in CI against an rclone WebDAV server.",
            UsesRclone: false),
        new(ProviderKind.Sftp, "SFTP", "sftp", ProviderAuthKind.Form, ImplementationStatus.Experimental,
            "Via the rclone 'sftp' backend. Integration-tested in CI against an rclone SFTP server when available (see TEST_REPORT.md).",
            UsesRclone: true),
        new(ProviderKind.S3, "S3-compatible storage", "s3", ProviderAuthKind.Form, ImplementationStatus.Experimental,
            "Via the rclone 's3' backend (AWS, MinIO, Wasabi, R2...). Integration-tested in CI against an rclone S3 server when available (see TEST_REPORT.md).",
            UsesRclone: true),
        new(ProviderKind.TeraBox, "TeraBox", "terabox", ProviderAuthKind.Form, ImplementationStatus.Blocked,
            "No public TeraBox API was verified for third-party apps. Official rclone has no 'terabox' backend; an rclone build that provides one (unofficial forks) can be selected in Settings, which makes TeraBox Experimental.",
            UsesRclone: true),
        new(ProviderKind.Smb, "SMB / NAS (rclone)", "smb", ProviderAuthKind.Form, ImplementationStatus.Experimental,
            "Via the rclone 'smb' backend. For NAS shares already reachable in Windows, prefer 'Local / NAS folder'.",
            UsesRclone: true),
        new(ProviderKind.LocalFolder, "Local / NAS folder (UNC)", null, ProviderAuthKind.FolderPath, ImplementationStatus.Supported,
            "Native implementation over a local or \\\\server\\share path. Covered by automated tests.",
            UsesRclone: false),
        new(ProviderKind.GenericRclone, "Other rclone backend", null, ProviderAuthKind.Form, ImplementationStatus.Experimental,
            "Any backend offered by the configured rclone binary. Capabilities depend on that backend.",
            UsesRclone: true),
    ];

    public static ProviderDescriptor Get(ProviderKind kind) =>
        All.FirstOrDefault(d => d.Kind == kind) ??
        new ProviderDescriptor(kind, kind.ToString(), null, ProviderAuthKind.None, ImplementationStatus.Planned, "Not implemented.", false);

    /// <summary>
    /// Effective status at runtime. rclone-based providers are Blocked while no usable rclone is
    /// running (the note says exactly why: missing, too old, failed to start), or when the
    /// running rclone has no backend for the provider (checked against its real backend list).
    /// TeraBox is only ever Experimental, and only when that list contains a 'terabox' backend.
    /// </summary>
    public static (ImplementationStatus Status, string Note) Effective(ProviderDescriptor d, RcloneBackendCatalog? catalog, RcloneStatus? rclone = null)
    {
        if (!d.UsesRclone)
        {
            return (d.Status, d.StatusNote);
        }

        if (catalog == null)
        {
            return (ImplementationStatus.Blocked, rclone is { IsReady: false } ? rclone.BlockedNote : "rclone is not running yet; it will be checked again.");
        }

        if (d.RcloneBackend != null && !catalog.Has(d.RcloneBackend))
        {
            var why = d.Kind == ProviderKind.TeraBox
                ? $"rclone {catalog.Version} has no 'terabox' backend (official rclone does not include one). {d.StatusNote}"
                : $"rclone {catalog.Version} has no '{d.RcloneBackend}' backend. Update rclone (Download rclone) or choose another build in Settings.";
            return (ImplementationStatus.Blocked, why);
        }

        if (d.Kind == ProviderKind.TeraBox)
        {
            return (ImplementationStatus.Experimental,
                $"Using the 'terabox' backend of rclone {catalog.Version} (third-party, unofficial). Cookie-based login entered by you; may break when TeraBox changes its web API.");
        }

        return (d.Status, d.StatusNote);
    }
}
