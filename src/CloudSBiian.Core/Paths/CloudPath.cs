using System.Text;

namespace CloudSBiian.Core.Paths;

public sealed class PathValidationException : ArgumentException
{
    public PathValidationException(string message) : base(message) { }
}

/// <summary>
/// Cloud path helpers. A normalized cloud path is "/" rooted, uses "/" as separator,
/// never ends with "/" (except the root) and never contains "." or ".." segments.
/// </summary>
public static class CloudPath
{
    public const string Root = "/";
    public const int MaxNameLength = 255;

    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Normalizes a path coming from Windows ("\a\b") or a backend ("a/b/").
    /// Throws <see cref="PathValidationException"/> on traversal attempts.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Root;
        }

        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder(path.Length + 1);
        foreach (var part in parts)
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                throw new PathValidationException($"Path traversal ('..') is not allowed: {path}");
            }

            if (part.IndexOf('\0') >= 0)
            {
                throw new PathValidationException("Path contains a NUL character.");
            }

            sb.Append('/').Append(part);
        }

        return sb.Length == 0 ? Root : sb.ToString();
    }

    public static bool IsRoot(string path) => Normalize(path) == Root;

    public static string Combine(string directory, string name)
    {
        ValidateName(name);
        var dir = Normalize(directory);
        return dir == Root ? Root + name : dir + "/" + name;
    }

    /// <summary>Combines a directory with a relative path that may contain several segments.</summary>
    public static string CombineRelative(string directory, string relative)
    {
        var rel = Normalize(relative);
        if (rel == Root)
        {
            return Normalize(directory);
        }

        foreach (var segment in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            ValidateName(segment);
        }

        var dir = Normalize(directory);
        return dir == Root ? rel : dir + rel;
    }

    public static string GetParent(string path)
    {
        var p = Normalize(path);
        if (p == Root)
        {
            return Root;
        }

        var idx = p.LastIndexOf('/');
        return idx <= 0 ? Root : p[..idx];
    }

    public static string GetName(string path)
    {
        var p = Normalize(path);
        if (p == Root)
        {
            return string.Empty;
        }

        return p[(p.LastIndexOf('/') + 1)..];
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="ancestor"/> or is below it.</summary>
    public static bool IsSameOrDescendant(string ancestor, string path, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var a = Normalize(ancestor);
        var p = Normalize(path);
        if (a == Root)
        {
            return true;
        }

        return string.Equals(a, p, comparison) || p.StartsWith(a + "/", comparison);
    }

    /// <summary>Re-bases <paramref name="path"/> from <paramref name="oldPrefix"/> to <paramref name="newPrefix"/>.</summary>
    public static string Rebase(string path, string oldPrefix, string newPrefix)
    {
        var p = Normalize(path);
        var o = Normalize(oldPrefix);
        var n = Normalize(newPrefix);
        if (!IsSameOrDescendant(o, p))
        {
            throw new ArgumentException($"'{p}' is not below '{o}'.");
        }

        var rest = o == Root ? p : p[o.Length..];
        if (rest.Length == 0)
        {
            return n;
        }

        return n == Root ? rest : n + rest;
    }

    public static bool TryValidateName(string? name, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(name))
        {
            error = "Name is empty.";
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            error = $"Name is longer than {MaxNameLength} characters.";
            return false;
        }

        if (name is "." or "..")
        {
            error = "'.' and '..' are not valid names.";
            return false;
        }

        foreach (var c in name)
        {
            if (c < 32 || Array.IndexOf(InvalidNameChars, c) >= 0)
            {
                error = $"Name contains an invalid character: '{(c < 32 ? $"0x{(int)c:X2}" : c.ToString())}'.";
                return false;
            }
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            error = "Name must not end with a space or a dot.";
            return false;
        }

        var stem = name.Split('.')[0];
        if (ReservedNames.Contains(stem))
        {
            error = $"'{stem}' is a reserved Windows device name.";
            return false;
        }

        return true;
    }

    public static void ValidateName(string name)
    {
        if (!TryValidateName(name, out var error))
        {
            throw new PathValidationException(error!);
        }
    }

    /// <summary>Converts a cloud path to a Windows-style relative path ("\a\b").</summary>
    public static string ToWindows(string path) => Normalize(path).Replace('/', '\\');

    /// <summary>
    /// Resolves a cloud path below a local root directory and guarantees the result stays inside it.
    /// </summary>
    public static string ToLocalPath(string localRoot, string cloudPath)
    {
        var root = System.IO.Path.GetFullPath(localRoot);
        var rel = Normalize(cloudPath).TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel));
        var rootWithSep = root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? root : root + System.IO.Path.DirectorySeparatorChar;
        if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new PathValidationException($"Path escapes the storage root: {cloudPath}");
        }

        return full;
    }
}
