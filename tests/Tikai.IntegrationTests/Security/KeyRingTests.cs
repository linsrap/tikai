using System.Net;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tikai.Api.Auth;
using Tikai.Api.Security;
using Tikai.Core.Security;
using Tikai.IntegrationTests.Auth;

namespace Tikai.IntegrationTests.Security;

public sealed class KeyRingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Purpose = "Tikai.Tests.v1";
    private TikaiApiFactory _factory = null!;

    public async ValueTask InitializeAsync() => _factory = await TikaiApiFactory.CreateAsync(postgres);

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    private string KeysPath => Path.Combine(_factory.DataPath, "keys");

    [Fact]
    public void The_encryptor_round_trips_and_rejects_a_wrong_secret_or_changed_data()
    {
        var element = new XElement("masterKey", new XElement("value", "c2VjcmV0"));
        var encrypted = new SecretXmlEncryptor(KeyRingKey.Derive(TikaiApiFactory.TestSecret)).Encrypt(element);

        Assert.Equal(typeof(SecretXmlDecryptor), encrypted.DecryptorType);
        Assert.DoesNotContain("c2VjcmV0", encrypted.EncryptedElement.ToString(), StringComparison.Ordinal);
        Assert.Equal(element.ToString(), Decryptor(TikaiApiFactory.TestSecret).Decrypt(encrypted.EncryptedElement).ToString());

        var wrong = Assert.Throws<CryptographicException>(() => Decryptor("another-secret-0123456789-abcdefghijk").Decrypt(encrypted.EncryptedElement));
        Assert.Contains("TIKAI_SECRET", wrong.Message, StringComparison.Ordinal);

        var value = encrypted.EncryptedElement.Element("value")!;
        var bytes = Convert.FromBase64String(value.Value);
        bytes[^1] ^= 1;
        value.Value = Convert.ToBase64String(bytes);
        Assert.Throws<CryptographicException>(() => Decryptor(TikaiApiFactory.TestSecret).Decrypt(encrypted.EncryptedElement));
    }

    [Fact]
    public async Task Keys_on_disk_hold_no_plain_key_material()
    {
        using var browser = await NewBrowserAsync(_factory);
        await browser.SignUpAdminAsync();

        var file = Assert.Single(Directory.GetFiles(KeysPath, "key-*.xml"));
        var xml = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        Assert.Contains("encryptedSecret", xml, StringComparison.Ordinal);
        Assert.Contains(nameof(SecretXmlDecryptor), xml, StringComparison.Ordinal);
        Assert.DoesNotContain("masterKey", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("unencrypted form", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restart_with_the_same_secret_keeps_sessions_and_stored_secrets()
    {
        using var browser = await NewBrowserAsync(_factory);
        using var signUp = await browser.SignUpAsync("ada@example.org");
        var cookie = AuthCookie(signUp);
        var stored = _factory.Services.GetRequiredService<ISecretProtector>().Protect(Purpose, "ghp_token");

        await using var restarted = _factory.Restart();
        using var client = restarted.CreateClient();
        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me") { Headers = { { "Cookie", cookie } } };

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(me, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal("ghp_token", restarted.Services.GetRequiredService<ISecretProtector>().Unprotect(Purpose, stored));
    }

    [Fact]
    public async Task A_wrong_secret_stops_the_hub_at_startup_with_a_clear_message()
    {
        using (var browser = await NewBrowserAsync(_factory))
        {
            await browser.SignUpAdminAsync(); // creates and stores a key
        }

        await using var restarted = _factory.Restart(new Dictionary<string, string> { ["Tikai:EncryptionSecret"] = "not-the-secret-0123456789-abcdefghijk" });
        var failure = Record.Exception(() => restarted.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains("cannot be decrypted", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains("TIKAI_SECRET", failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public async Task A_missing_or_short_secret_stops_the_hub_at_startup(string secret)
    {
        await using var restarted = _factory.Restart(new Dictionary<string, string> { ["Tikai:EncryptionSecret"] = secret });

        var failure = Record.Exception(() => restarted.CreateClient());

        Assert.IsType<OptionsValidationException>(failure, exactMatch: false);
        Assert.Contains("must be set to a random value", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keys_from_before_key_ring_encryption_are_encrypted_at_startup_and_still_work()
    {
        // What steps 04 and 05 wrote: an unencrypted key, and a secret protected with it.
        var old = DataProtectionProvider.Create(new DirectoryInfo(KeysPath), b => b.SetApplicationName("Tikai"));
        var stored = old.CreateProtector(Purpose).Protect("ghp_token");
        var file = Assert.Single(Directory.GetFiles(KeysPath, "key-*.xml"));
        Assert.Contains("unencrypted form", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        using var client = _factory.CreateClient();

        var xml = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        Assert.Contains("encryptedSecret", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("masterKey", xml, StringComparison.Ordinal);
        Assert.Equal("ghp_token", _factory.Services.GetRequiredService<ISecretProtector>().Unprotect(Purpose, stored));
        Assert.Single(Directory.GetFiles(KeysPath, "key-*.xml")); // the old key is kept, not replaced
    }

    private static SecretXmlDecryptor Decryptor(string secret)
    {
        var services = new ServiceCollection()
            .AddSingleton(Options.Create(new KeyRingOptions { EncryptionSecret = secret }))
            .BuildServiceProvider();
        return new SecretXmlDecryptor(services);
    }

    private static string AuthCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).Single(c => c.StartsWith(AuthSetup.AuthCookie + "=", StringComparison.Ordinal));

    private static async Task<BrowserClient> NewBrowserAsync(TikaiApiFactory factory)
    {
        var browser = new BrowserClient(factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true }));
        await browser.RefreshAntiforgeryAsync();
        return browser;
    }
}
