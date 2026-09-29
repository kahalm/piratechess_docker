using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

/// <summary>
/// Platzhalter-Geheimnisse aus .env.example gelten nicht als Schlüssel (I2-001): Jwt:Secret/Encryption:Key brechen den
/// Start ab, ein Platzhalter-Service-Key schaltet die Service-Endpunkte ab (503) statt ihn zu akzeptieren.
/// </summary>
public class SecretPlaceholderTests
{
    private sealed class OverrideFactory(string key, string value) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }));
        }
    }

    [Theory]
    [InlineData("change_me_long_random_string", true)]
    [InlineData("change_me_min_32_characters_long_secret_key", true)]
    [InlineData("CHANGE_ME", true)]
    [InlineData("  your_secret_here", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("k3Jq9x-real-random-value", false)]
    [InlineData("please_change_me", false)]
    public void IsPlaceholder_RecognisesTheExamplePrefixes(string? value, bool expected)
        => Assert.Equal(expected, SecretPlaceholder.IsPlaceholder(value));

    [Theory]
    [InlineData("Jwt:Secret", "change_me_min_32_characters_long_secret_key")]
    [InlineData("Encryption:Key", "change_me_exactly_32_chars_long!")]
    public void Startup_WithPlaceholderSecret_Aborts(string key, string placeholder)
    {
        using var factory = new OverrideFactory(key, placeholder);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var message = ex.ToString();
        Assert.Contains($"{key} is still a placeholder", message);
        Assert.DoesNotContain(placeholder, message);
    }

    [Fact]
    public async Task ServiceKeyPlaceholder_IsTreatedAsNotConfigured_Returns503_EvenWithMatchingHeader()
    {
        const string placeholder = "change_me_long_random_string";
        using var factory = new OverrideFactory("Service:ApiKey", placeholder);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Service-Key", placeholder);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/chessable/direct/build-info")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/vpn/status")).StatusCode);
    }

    [Fact]
    public void EnvExample_ApiSecretsAreEmpty_AndNoTwoSecretsShareAPlaceholder()
    {
        var values = File.ReadAllLines(Path.Combine(BuildHardeningTests.RepoRoot(), ".env.example"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
            .ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);

        // Fail-closed-Schlüssel ohne Beispielwert: wer die Datei kopiert, muss sie selbst erzeugen.
        foreach (var key in new[] { "JWT_SECRET", "ENCRYPTION_KEY", "SERVICE_API_KEY" })
            Assert.Equal("", values[key]);

        var shared = values.Where(v => SecretPlaceholder.IsPlaceholder(v.Value))
            .GroupBy(v => v.Value)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(",", g.Select(v => v.Key)));
        Assert.Empty(shared);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("change_me_exactly_32_chars_long!")]
    public void EncryptionService_RejectsEmptyOrPlaceholderKey(string key)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Encryption:Key"] = key })
            .Build();

        Assert.Throws<InvalidOperationException>(() => new EncryptionService(config));
    }
}
