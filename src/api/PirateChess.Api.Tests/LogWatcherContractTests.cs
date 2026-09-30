using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

/// <summary>
/// I2-012: Der log-watcher rechnet by-design-Warnungen von piratechess per Teilstring aus der Warnungs-Spitze
/// heraus (warn_spike_ignore im Target piratechess, log-watcher/config.example.yaml; Prod-Konfig gleich).
/// Die Satzteile stehen hier wörtlich — eine Umformulierung fällt hier auf statt als Fehlalarm auf Prod.
/// </summary>
public class LogWatcherContractTests
{
    [Fact]
    public void WarnSpikeIgnore_matches_the_log_watcher_config_literally()
    {
        Assert.Equal(
            ["curl exited with code", "WIEDERHOLT SCHLECHT", "übersprang eine Linie/Kapitel"],
            LogWatcherContract.WarnSpikeIgnore);
    }

    [Fact]
    public void VpnIpHealth_repeatedly_bad_warning_contains_the_contract_phrase()
    {
        var logger = new HeartbeatServiceTests.CapturingLogger<VpnIpHealth>();
        var health = new VpnIpHealth(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vpn:BadIpMinRequests"] = "10",
            ["Vpn:BadIpBlockRate"] = "0.15",
        }).Build(), logger);

        health.RecordStint("9.9.9.9", requests: 20, blocks: 10);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(LogWatcherContract.IpRepeatedlyBad, warning.Message);
        Assert.Contains(LogWatcherContract.IpRepeatedlyBad, (string?)warning.Props["{OriginalFormat}"]);
    }

    /// <summary>Die übrigen Stellen (curl-Exit, Parser-Skip in vier Pfaden) sind nur mit viel Aufbau auszulösen.
    /// Stattdessen: der Satzteil steht im Quelltext nur in LogWatcherContract.cs, und jede bekannte Stelle baut ihr
    /// Template aus der Konstante. Wer ein Template frei neu formuliert, lässt hier einen Zähler fallen.</summary>
    [Theory]
    [InlineData(nameof(LogWatcherContract.CurlExited), 1)]
    [InlineData(nameof(LogWatcherContract.IpRepeatedlyBad), 1)]
    [InlineData(nameof(LogWatcherContract.ParserSkipped), 4)]
    public void Log_templates_are_built_from_the_contract_constants(string constant, int expectedSites)
    {
        var apiDir = Path.Combine(BuildHardeningTests.RepoRoot(), "src", "api", "PirateChess.Api");
        var sources = Directory.GetFiles(apiDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) != "LogWatcherContract.cs")
            .ToDictionary(f => f, File.ReadAllText);

        var literal = (string)typeof(LogWatcherContract).GetField(constant)!.GetRawConstantValue()!;
        Assert.DoesNotContain(sources, kv => kv.Value.Contains(literal, StringComparison.Ordinal));

        var sites = sources.Values.Sum(src => Regex.Matches(src, $@"\bLogWatcherContract\.{constant}\b").Count);
        Assert.Equal(expectedSites, sites);
    }
}
