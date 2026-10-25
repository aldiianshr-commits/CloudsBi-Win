using System.Text.Json.Nodes;

namespace CloudSBiian.CloudProviders.Rclone;

public sealed record RcloneOptionExample(string Value, string Help);

/// <summary>One backend option as reported by "config/providers". Used to build forms dynamically.</summary>
public sealed record RcloneBackendOption(
    string Name,
    string Help,
    string DefaultValue,
    bool Required,
    bool IsPassword,
    bool Sensitive,
    bool Advanced,
    bool Exclusive,
    string Type,
    IReadOnlyList<RcloneOptionExample> Examples)
{
    /// <summary>Secret values go to the DPAPI store, never to the plain options.</summary>
    public bool IsSecret => IsPassword || Sensitive || Name is "token" or "client_secret" or "secret_access_key" or "pass" or "password" or "cookie" or "key_pem" or "bearer_token";

    public string ShortHelp
    {
        get
        {
            var first = Help.Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
            return first.Length > 160 ? first[..160] + "…" : first;
        }
    }
}

public sealed record RcloneBackendInfo(string Name, string Description, IReadOnlyList<RcloneBackendOption> Options);

/// <summary>Backends and option schemas of the configured rclone binary (queried, not assumed).</summary>
public sealed class RcloneBackendCatalog
{
    private readonly Dictionary<string, RcloneBackendInfo> _backends;

    public RcloneBackendCatalog(string version, IEnumerable<RcloneBackendInfo> backends)
    {
        Version = version;
        _backends = backends.ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string Version { get; }

    public IReadOnlyCollection<RcloneBackendInfo> Backends => _backends.Values;

    public bool Has(string backend) => _backends.ContainsKey(backend);

    public RcloneBackendInfo? Get(string backend) => _backends.TryGetValue(backend, out var b) ? b : null;

    public static async Task<RcloneBackendCatalog> LoadAsync(RcloneDaemon daemon, CancellationToken ct)
    {
        await daemon.EnsureStartedAsync(ct).ConfigureAwait(false);
        var result = await daemon.CallAsync("config/providers", null, ct).ConfigureAwait(false);
        return Parse(daemon.Version ?? "unknown", result);
    }

    public static RcloneBackendCatalog Parse(string version, JsonObject result)
    {
        var list = new List<RcloneBackendInfo>();
        if (result["providers"] is JsonArray providers)
        {
            foreach (var p in providers.OfType<JsonObject>())
            {
                var name = p["Name"]?.ToString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var options = new List<RcloneBackendOption>();
                if (p["Options"] is JsonArray opts)
                {
                    foreach (var o in opts.OfType<JsonObject>())
                    {
                        if (GetBool(o, "Hide") && o["Hide"]?.ToString() != "0")
                        {
                            // Hide is a bitmask in newer rclone; any non-zero value hides the option from config.
                            continue;
                        }

                        var examples = new List<RcloneOptionExample>();
                        if (o["Examples"] is JsonArray ex)
                        {
                            foreach (var e in ex.OfType<JsonObject>())
                            {
                                examples.Add(new RcloneOptionExample(e["Value"]?.ToString() ?? string.Empty, e["Help"]?.ToString() ?? string.Empty));
                            }
                        }

                        options.Add(new RcloneBackendOption(
                            o["Name"]?.ToString() ?? string.Empty,
                            o["Help"]?.ToString() ?? string.Empty,
                            o["DefaultStr"]?.ToString() ?? o["Default"]?.ToString() ?? string.Empty,
                            GetBool(o, "Required"),
                            GetBool(o, "IsPassword"),
                            GetBool(o, "Sensitive"),
                            GetBool(o, "Advanced"),
                            GetBool(o, "Exclusive"),
                            o["Type"]?.ToString() ?? "string",
                            examples));
                    }
                }

                list.Add(new RcloneBackendInfo(name, p["Description"]?.ToString() ?? name, options));
            }
        }

        return new RcloneBackendCatalog(version, list);
    }

    private static bool GetBool(JsonObject o, string key)
    {
        var node = o[key];
        if (node == null)
        {
            return false;
        }

        var s = node.ToString();
        return s.Equals("true", StringComparison.OrdinalIgnoreCase) || (int.TryParse(s, out var n) && n != 0);
    }
}
