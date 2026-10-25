using System.Security.Cryptography;
using System.Text.Json;
using CloudSBiian.Core.Abstractions;
using CloudSBiian.Core.Storage;

namespace CloudSBiian.Core.Accounts;

/// <summary>
/// A configured storage account. Contains no secrets: those live in the
/// <see cref="Security.ISecretStore"/> under <see cref="Id"/>.
/// </summary>
public sealed class CloudAccount
{
    public required string Id { get; init; }

    public required ProviderKind Kind { get; init; }

    public required string DisplayName { get; set; }

    /// <summary>User-visible identity (e-mail / user name) when the backend reports it.</summary>
    public string? Identity { get; set; }

    /// <summary>Folder on the backend used as this account's root.</summary>
    public string RootPath { get; set; } = "/";

    /// <summary>rclone backend name for rclone-based accounts (e.g. "drive", "terabox").</summary>
    public string? Backend { get; set; }

    /// <summary>Non-secret connection options (host, user name, region...).</summary>
    public Dictionary<string, string> Options { get; set; } = new();

    public bool Enabled { get; set; } = true;

    public bool AutoConnect { get; set; } = true;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public int SortOrder { get; set; }

    public static string NewId(ProviderKind kind)
    {
        var prefix = kind.ToString().ToLowerInvariant();
        if (prefix.Length > 12)
        {
            prefix = prefix[..12];
        }

        return $"{prefix}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}";
    }

    public override string ToString() => $"{DisplayName} ({Kind}, {Id})";
}

public sealed class AccountStore
{
    private readonly AppDatabase _db;

    public AccountStore(AppDatabase db) => _db = db;

    public event EventHandler? Changed;

    public IReadOnlyList<CloudAccount> GetAll() =>
        _db.Query(
            "SELECT id, kind, display_name, identity, root_path, backend, options_json, enabled, auto_connect, created_at, sort_order FROM accounts ORDER BY sort_order, created_at",
            Map);

    public CloudAccount? Get(string id) =>
        _db.Query(
            "SELECT id, kind, display_name, identity, root_path, backend, options_json, enabled, auto_connect, created_at, sort_order FROM accounts WHERE id=@id",
            Map,
            ("@id", id)).FirstOrDefault();

    public void Add(CloudAccount account)
    {
        Security.AccountIdValidator.Validate(account.Id);
        var order = (_db.Scalar<long?>("SELECT MAX(sort_order) FROM accounts") ?? 0) + 1;
        account.SortOrder = (int)order;
        _db.Execute(
            "INSERT INTO accounts(id, kind, display_name, identity, root_path, backend, options_json, enabled, auto_connect, created_at, sort_order) VALUES(@id,@kind,@name,@identity,@root,@backend,@options,@enabled,@auto,@created,@order)",
            ("@id", account.Id),
            ("@kind", account.Kind.ToString()),
            ("@name", account.DisplayName),
            ("@identity", account.Identity),
            ("@root", account.RootPath),
            ("@backend", account.Backend),
            ("@options", JsonSerializer.Serialize(account.Options)),
            ("@enabled", account.Enabled ? 1 : 0),
            ("@auto", account.AutoConnect ? 1 : 0),
            ("@created", account.CreatedAt.ToUnixTimeMilliseconds()),
            ("@order", account.SortOrder));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(CloudAccount account)
    {
        _db.Execute(
            "UPDATE accounts SET display_name=@name, identity=@identity, root_path=@root, backend=@backend, options_json=@options, enabled=@enabled, auto_connect=@auto, sort_order=@order WHERE id=@id",
            ("@id", account.Id),
            ("@name", account.DisplayName),
            ("@identity", account.Identity),
            ("@root", account.RootPath),
            ("@backend", account.Backend),
            ("@options", JsonSerializer.Serialize(account.Options)),
            ("@enabled", account.Enabled ? 1 : 0),
            ("@auto", account.AutoConnect ? 1 : 0),
            ("@order", account.SortOrder));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        _db.Execute("DELETE FROM accounts WHERE id=@id", ("@id", id));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns a display name not used by another account ("TeraBox 2", "TeraBox 3"...).</summary>
    public string SuggestDisplayName(string baseName)
    {
        var existing = new HashSet<string>(GetAll().Select(a => a.DisplayName), StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static CloudAccount Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Kind = Enum.TryParse<ProviderKind>(r.GetString(1), out var k) ? k : ProviderKind.GenericRclone,
        DisplayName = r.GetString(2),
        Identity = AppDatabase.GetStringOrNull(r, 3),
        RootPath = r.GetString(4),
        Backend = AppDatabase.GetStringOrNull(r, 5),
        Options = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(6)) ?? new(),
        Enabled = r.GetInt64(7) != 0,
        AutoConnect = r.GetInt64(8) != 0,
        CreatedAt = Clock.FromMs(r.GetInt64(9)),
        SortOrder = (int)r.GetInt64(10),
    };
}
