using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PirateChess.Api.Data;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

/// <summary>
/// I2-012: piratechess hatte als einziger .NET-Dienst keinen Heartbeat — ein hängender Dienst fiel erst beim
/// nächsten Nutzer-Import auf. Spiegel der HeartbeatServiceTests in rookhub und Crawler (Datei-Kopie).
/// </summary>
public class HeartbeatServiceTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public HeartbeatServiceTests(TestWebApplicationFactory factory) => _factory = factory;

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Dictionary<string, object?> Props)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Noop();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, ex),
                state is IEnumerable<KeyValuePair<string, object?>> kv
                    ? kv.ToDictionary(p => p.Key, p => p.Value)
                    : new Dictionary<string, object?>()));
        }
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private static async Task<CapturingLogger<HeartbeatService>> EmitOnceAsync()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase("hb-" + Guid.NewGuid()));
        using var provider = services.BuildServiceProvider();
        var logger = new CapturingLogger<HeartbeatService>();
        await new HeartbeatService(provider.GetRequiredService<IServiceScopeFactory>(), logger,
            new ConfigurationBuilder().Build()).EmitAsync();
        return logger;
    }

    [Fact]
    public async Task EmitAsync_LogsStructuredHealthyHeartbeat_WhenDbReachable()
    {
        var logger = await EmitOnceAsync();

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("healthy", entry.Message);   // InMemory-DB ist erreichbar
    }

    /// <summary>
    /// Vertrag mit dem log-watcher: er erkennt den Dienst am strukturierten Feld labels.HeartbeatService =
    /// "piratechess-api" (config.example.yaml: heartbeat_checks "piratechess-api=piratechess-logs-*") bzw.
    /// in der Altform per match_phrase am Satz "Heartbeat: piratechess-api". Beides wörtlich gepinnt.
    /// </summary>
    [Fact]
    public async Task EmitAsync_RendersTheExactSentenceAndFieldTheLogWatcherLooksFor()
    {
        var logger = await EmitOnceAsync();

        var entry = Assert.Single(logger.Entries);
        Assert.StartsWith("Heartbeat: piratechess-api ", entry.Message);
        Assert.Equal("piratechess-api", entry.Props["HeartbeatService"]);
        Assert.StartsWith("Heartbeat: {HeartbeatService} ", (string?)entry.Props["{OriginalFormat}"]);
    }

    [Fact]
    public void HeartbeatService_is_registered_as_hosted_service()
    {
        Assert.Contains(_factory.Services.GetServices<IHostedService>(), s => s is HeartbeatService);
    }
}
