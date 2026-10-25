using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace CloudSBiian.Core.Security;

/// <summary>
/// Removes secrets from any text before it reaches logs, the UI or exception messages.
/// Two layers: (1) every secret value loaded at runtime is registered and replaced verbatim,
/// (2) regexes catch common token/password shapes that were never registered.
/// </summary>
public static partial class SecretRedactor
{
    public const string Mask = "***";

    private static readonly ConcurrentDictionary<string, byte> KnownSecrets = new(StringComparer.Ordinal);

    [GeneratedRegex("(?i)((?<![A-Za-z0-9_])\"?(?:access_token|refresh_token|token|password|pass|secret|secret_access_key|client_secret|cookie|authorization|api_key|apikey|session|ndus|key)\"?\\s*[:=]\\s*)(\"[^\"]*\"|[^\\s,;&}]+)")]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex("(?i)(Bearer|Basic)\\s+[A-Za-z0-9\\-\\._~\\+/=]{8,}")]
    private static partial Regex AuthHeaderRegex();

    [GeneratedRegex("(?i)(--rc-pass(?:=|\\s+))(\\S+)")]
    private static partial Regex RcPassRegex();

    /// <summary>Registers a secret value so it is masked wherever it appears.</summary>
    public static void Register(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 4)
        {
            KnownSecrets.TryAdd(secret, 0);
        }
    }

    public static void Unregister(string? secret)
    {
        if (!string.IsNullOrEmpty(secret))
        {
            KnownSecrets.TryRemove(secret, out _);
        }
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = text;
        foreach (var secret in KnownSecrets.Keys)
        {
            if (result.Contains(secret, StringComparison.Ordinal))
            {
                result = result.Replace(secret, Mask, StringComparison.Ordinal);
            }
        }

        result = KeyValueRegex().Replace(result, m => m.Groups[1].Value + Mask);
        result = AuthHeaderRegex().Replace(result, m => m.Groups[1].Value + " " + Mask);
        result = RcPassRegex().Replace(result, m => m.Groups[1].Value + Mask);
        return result;
    }

    /// <summary>Shows only the first and last characters of an identifier (e.g. an email).</summary>
    public static string MaskIdentity(string? identity)
    {
        if (string.IsNullOrEmpty(identity))
        {
            return string.Empty;
        }

        var at = identity.IndexOf('@');
        if (at > 1)
        {
            return identity[0] + new string('*', Math.Min(6, at - 1)) + identity[at..];
        }

        return identity.Length <= 4 ? new string('*', identity.Length) : identity[..2] + "****" + identity[^2..];
    }
}
