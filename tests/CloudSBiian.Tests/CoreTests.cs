using System.Security.Cryptography;
using CloudSBiian.Core.Configuration;
using CloudSBiian.Core.Paths;
using CloudSBiian.Core.Security;

namespace CloudSBiian.Tests;

public class CloudPathTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("\\", "/")]
    [InlineData("a\\b\\", "/a/b")]
    [InlineData("/a//b/./c", "/a/b/c")]
    public void Normalize_ProducesCanonicalPaths(string? input, string expected) =>
        Assert.Equal(expected, CloudPath.Normalize(input));

    [Theory]
    [InlineData("/a/../b")]
    [InlineData("..\\windows")]
    [InlineData("a/b/..")]
    public void Normalize_RejectsTraversal(string input) =>
        Assert.Throws<PathValidationException>(() => CloudPath.Normalize(input));

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("a:b")]
    [InlineData("x?")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("")]
    public void InvalidWindowsNames_AreRejected(string name) =>
        Assert.False(CloudPath.TryValidateName(name, out _));

    [Fact]
    public void ToLocalPath_StaysInsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "root");
        Assert.StartsWith(root, CloudPath.ToLocalPath(root, "/a/b.txt"), StringComparison.OrdinalIgnoreCase);
        Assert.Throws<PathValidationException>(() => CloudPath.ToLocalPath(root, "/../escape.txt"));
    }

    [Fact]
    public void Rebase_And_Parent_Work()
    {
        Assert.Equal("/new/x/y", CloudPath.Rebase("/old/x/y", "/old", "/new"));
        Assert.Equal("/a", CloudPath.GetParent("/a/b"));
        Assert.Equal("/", CloudPath.GetParent("/a"));
        Assert.Equal("b", CloudPath.GetName("/a/b"));
        Assert.True(CloudPath.IsSameOrDescendant("/Games", "/games/x.exe"));
        Assert.False(CloudPath.IsSameOrDescendant("/Games", "/GamesOld/x.exe"));
    }
}

public class SecretTests
{
    [Fact]
    public void Redactor_MasksRegisteredSecretsAndTokenShapes()
    {
        SecretRedactor.Register("super-secret-value-123");
        var text = SecretRedactor.Redact("value super-secret-value-123 and {\"access_token\":\"abc.def\"} password=hunter2 Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.x");
        Assert.DoesNotContain("super-secret-value-123", text);
        Assert.DoesNotContain("abc.def", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", text);
    }

    [Fact]
    public void Redactor_DoesNotMaskOrdinaryWords()
    {
        Assert.Equal("file_key=abc chunk 3", SecretRedactor.Redact("file_key=abc chunk 3"));
    }

    [Fact]
    public void InMemoryStore_IsolatesAccounts()
    {
        var store = new InMemorySecretStore();
        store.Set("acc_aaa", "token", "A");
        store.Set("acc_bbb", "token", "B");
        Assert.Equal("A", store.Get("acc_aaa", "token"));
        Assert.Equal("B", store.Get("acc_bbb", "token"));
        store.DeleteAll("acc_aaa");
        Assert.Null(store.Get("acc_aaa", "token"));
        Assert.Equal("B", store.Get("acc_bbb", "token"));
    }

    [Fact]
    public void Store_RejectsInvalidAccountIds()
    {
        var store = new InMemorySecretStore();
        Assert.Throws<ArgumentException>(() => store.Set("../evil", "k", "v"));
    }

    [Fact]
    public void Dpapi_BlobOfOneAccountCannotBeReadAsAnother()
    {
        var dir = Path.Combine(Path.GetTempPath(), "csb-dpapi-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DpapiSecretStore(dir);
            store.Set("terabox_aaaaaa", "cookie", "secret-A");
            Assert.Equal("secret-A", store.Get("terabox_aaaaaa", "cookie"));
            Assert.DoesNotContain("secret-A", File.ReadAllText(Path.Combine(dir, "terabox_aaaaaa.secret")));
            File.Copy(Path.Combine(dir, "terabox_aaaaaa.secret"), Path.Combine(dir, "terabox_bbbbbb.secret"));
            Assert.ThrowsAny<CryptographicException>(() => store.Get("terabox_bbbbbb", "cookie"));
            store.DeleteAll("terabox_aaaaaa");
            Assert.False(File.Exists(Path.Combine(dir, "terabox_aaaaaa.secret")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class SettingsTests
{
    [Fact]
    public void Settings_RoundTripAndValidate()
    {
        var file = Path.Combine(Path.GetTempPath(), "csb-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(file);
            var s = store.Current.Clone();
            s.CacheBudgetBytes = 30 * AppSettings.GiB;
            s.Theme = AppTheme.Light;
            store.Save(s);
            var reloaded = new SettingsStore(file).Current;
            Assert.Equal(30 * AppSettings.GiB, reloaded.CacheBudgetBytes);
            Assert.Equal(AppTheme.Light, reloaded.Theme);
            Assert.Empty(reloaded.Validate());
            reloaded.CacheBudgetBytes = 1;
            Assert.NotEmpty(reloaded.Validate());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void CorruptSettings_FallBackToDefaults()
    {
        var file = Path.Combine(Path.GetTempPath(), "csb-settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, "{ not json");
        try
        {
            Assert.Equal(new AppSettings().CacheBudgetBytes, new SettingsStore(file).Current.CacheBudgetBytes);
        }
        finally
        {
            File.Delete(file);
            File.Delete(file + ".corrupt");
        }
    }
}
