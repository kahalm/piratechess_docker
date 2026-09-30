using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class EncryptionServiceTests
{
    private const string Key = "super-secret-test-key";

    private static EncryptionService Make(string key = Key)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:Key"] = key })
            .Build();
        return new EncryptionService(config);
    }

    /// <summary>Erzeugt einen Ciphertext im ALTEN Format (AES-CBC, PadRight-Key, ohne Präfix).</summary>
    private static string LegacyCbcEncrypt(string plain, string key = Key)
    {
        var legacyKey = Encoding.UTF8.GetBytes(key.PadRight(32, '0')[..32]);
        using var aes = Aes.Create();
        aes.Key = legacyKey;
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipher = enc.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        var result = new byte[aes.IV.Length + cipher.Length];
        aes.IV.CopyTo(result, 0);
        cipher.CopyTo(result, aes.IV.Length);
        return Convert.ToBase64String(result);
    }

    [Fact]
    public void Encrypt_Decrypt_RoundTrip()
    {
        var svc = Make();
        const string secret = "Bearer abc.def.ghi";
        var cipher = svc.Encrypt(secret);

        Assert.StartsWith("v2:", cipher);
        Assert.NotEqual(secret, cipher);
        Assert.Equal(secret, svc.Decrypt(cipher));
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachTime()
    {
        var svc = Make();
        Assert.NotEqual(svc.Encrypt("x"), svc.Encrypt("x"));   // frische Nonce je Aufruf
    }

    [Fact]
    public void Decrypt_ReadsLegacyCbcCiphertext()
    {
        var svc = Make();
        var legacy = LegacyCbcEncrypt("legacy-bearer");
        Assert.Equal("legacy-bearer", svc.Decrypt(legacy));
    }

    [Fact]
    public void TryDecrypt_ReturnsNull_OnGarbageOrNull()
    {
        var svc = Make();
        Assert.Null(svc.TryDecrypt(null));
        Assert.Null(svc.TryDecrypt(""));
        Assert.Null(svc.TryDecrypt("not-base64!!"));
        Assert.Null(svc.TryDecrypt("v2:" + Convert.ToBase64String(new byte[] { 1, 2, 3 })));   // zu kurz
    }

    [Fact]
    public void TryDecrypt_ReturnsNull_OnWrongKey_ForV2()
    {
        var cipher = Make().Encrypt("secret");
        var other = Make("a-completely-different-key");
        Assert.Null(other.TryDecrypt(cipher));   // GCM-Tag schlägt fehl → null, kein Throw
    }

    [Fact]
    public void TryDecrypt_ReturnsValue_OnValidCiphertext()
    {
        var svc = Make();
        Assert.Equal("hello", svc.TryDecrypt(svc.Encrypt("hello")));
    }

    // --- S2-009: Spiegeltest zur Kopie in rookhub (RookHub.Api/Services/EncryptionService.cs) ------------------------
    // Beide Klassen sind Datei-Kopien. Feste Literale statt Round-Trip: dieselben Chiffrate müssen in beiden Repos mit
    // demselben Schlüssel dasselbe ergeben, und beide lehnen einen leeren Schlüssel ab (SHA256("") ist öffentlich).

    /// <summary>v2 (AES-GCM, SHA256-Key) von "Bearer abc.def.ghi" mit Key <see cref="Key"/>, Nonce 01..0C.</summary>
    internal const string MirrorV2Cipher = "v2:AQIDBAUGBwgJCgsMkhHhlvDgUp+S7ppIDBYIQDMlj7qqeXCSPwbFFc2AHNWPbQ==";

    /// <summary>Alt-CBC (PadRight-Key, ohne Präfix) von "legacy-bearer" mit Key <see cref="Key"/>, IV 10..1F.</summary>
    internal const string MirrorCbcCipher = "EBESExQVFhcYGRobHB0eH4WtrE27wGD0LBllN0pLjvQ=";

    [Fact]
    public void Mirror_DecryptsTheFixedV2Ciphertext()
        => Assert.Equal("Bearer abc.def.ghi", Make().Decrypt(MirrorV2Cipher));

    [Fact]
    public void Mirror_DecryptsTheFixedLegacyCbcCiphertext()
        => Assert.Equal("legacy-bearer", Make().Decrypt(MirrorCbcCipher));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Mirror_RejectsEmptyOrWhitespaceKey(string key)
        => Assert.Throws<InvalidOperationException>(() => Make(key));

    private sealed class EncryptionKeyFactory(string value) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:Key"] = value }));
        }
    }

    /// <summary>Fail-Fast: ein leerer Encryption:Key (ungesetztes ${ENCRYPTION_KEY} in der Compose) bricht den Start ab.
    /// Vorher war EncryptionService ein lazy Singleton — der Dienst lief an und scheiterte erst beim ersten Zugriff.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Startup_WithEmptyEncryptionKey_Aborts(string value)
    {
        using var factory = new EncryptionKeyFactory(value);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Encryption:Key not configured", ex.ToString());
    }
}
