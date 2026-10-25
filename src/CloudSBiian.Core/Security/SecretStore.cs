using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CloudSBiian.Core.Security;

/// <summary>
/// Per-account secret storage. Secrets of different accounts are stored in separate
/// containers and can never be read through another account's id.
/// </summary>
public interface ISecretStore
{
    string? Get(string accountId, string key);

    IReadOnlyDictionary<string, string> GetAll(string accountId);

    void Set(string accountId, string key, string value);

    void SetMany(string accountId, IReadOnlyDictionary<string, string> values);

    void Delete(string accountId, string key);

    void DeleteAll(string accountId);

    bool HasAny(string accountId);
}

public static partial class AccountIdValidator
{
    [GeneratedRegex("^[a-z0-9_]{3,64}$")]
    private static partial Regex IdRegex();

    public static void Validate(string accountId)
    {
        if (string.IsNullOrEmpty(accountId) || !IdRegex().IsMatch(accountId))
        {
            throw new ArgumentException("Invalid account id.", nameof(accountId));
        }
    }
}

/// <summary>Base class holding the shared logic; derived classes persist encrypted blobs.</summary>
public abstract class SecretStoreBase : ISecretStore
{
    private readonly object _gate = new();

    protected abstract Dictionary<string, string>? Load(string accountId);

    protected abstract void Save(string accountId, Dictionary<string, string> values);

    protected abstract void Remove(string accountId);

    public string? Get(string accountId, string key)
    {
        AccountIdValidator.Validate(accountId);
        lock (_gate)
        {
            var values = Load(accountId);
            if (values != null && values.TryGetValue(key, out var v))
            {
                SecretRedactor.Register(v);
                return v;
            }

            return null;
        }
    }

    public IReadOnlyDictionary<string, string> GetAll(string accountId)
    {
        AccountIdValidator.Validate(accountId);
        lock (_gate)
        {
            var values = Load(accountId) ?? new Dictionary<string, string>();
            foreach (var v in values.Values)
            {
                SecretRedactor.Register(v);
            }

            return values;
        }
    }

    public void Set(string accountId, string key, string value) =>
        SetMany(accountId, new Dictionary<string, string> { [key] = value });

    public void SetMany(string accountId, IReadOnlyDictionary<string, string> newValues)
    {
        AccountIdValidator.Validate(accountId);
        lock (_gate)
        {
            var values = Load(accountId) ?? new Dictionary<string, string>();
            foreach (var (k, v) in newValues)
            {
                values[k] = v;
                SecretRedactor.Register(v);
            }

            Save(accountId, values);
        }
    }

    public void Delete(string accountId, string key)
    {
        AccountIdValidator.Validate(accountId);
        lock (_gate)
        {
            var values = Load(accountId);
            if (values != null && values.Remove(key))
            {
                Save(accountId, values);
            }
        }
    }

    public void DeleteAll(string accountId)
    {
        AccountIdValidator.Validate(accountId);
        lock (_gate)
        {
            Remove(accountId);
        }
    }

    public bool HasAny(string accountId)
    {
        AccountIdValidator.Validate(accountId);
        lock (_gate)
        {
            var v = Load(accountId);
            return v is { Count: > 0 };
        }
    }
}

/// <summary>
/// Windows DPAPI (CurrentUser scope) store. Each account has its own file, and the
/// account id is mixed into the entropy so a blob copied to another account's file
/// cannot be decrypted under that account.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : SecretStoreBase
{
    private readonly string _directory;

    public DpapiSecretStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private string FileFor(string accountId) => Path.Combine(_directory, accountId + ".secret");

    private static byte[] Entropy(string accountId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("CloudSBiian.v1|" + accountId));

    protected override Dictionary<string, string>? Load(string accountId)
    {
        var file = FileFor(accountId);
        if (!File.Exists(file))
        {
            return null;
        }

        var blob = File.ReadAllBytes(file);
        var plain = ProtectedData.Unprotect(blob, Entropy(accountId), DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    protected override void Save(string accountId, Dictionary<string, string> values)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(values);
        try
        {
            var blob = ProtectedData.Protect(plain, Entropy(accountId), DataProtectionScope.CurrentUser);
            var file = FileFor(accountId);
            var tmp = file + ".tmp";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, file, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    protected override void Remove(string accountId)
    {
        var file = FileFor(accountId);
        if (File.Exists(file))
        {
            // Overwrite before delete so the encrypted blob does not linger in place.
            var len = new FileInfo(file).Length;
            File.WriteAllBytes(file, new byte[len]);
            File.Delete(file);
        }
    }
}

/// <summary>Non-persistent store for tests.</summary>
public sealed class InMemorySecretStore : SecretStoreBase
{
    private readonly Dictionary<string, Dictionary<string, string>> _data = new();

    protected override Dictionary<string, string>? Load(string accountId) =>
        _data.TryGetValue(accountId, out var v) ? new Dictionary<string, string>(v) : null;

    protected override void Save(string accountId, Dictionary<string, string> values) =>
        _data[accountId] = new Dictionary<string, string>(values);

    protected override void Remove(string accountId) => _data.Remove(accountId);
}
