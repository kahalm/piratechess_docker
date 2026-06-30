using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class VpnIpHealthTests
{
    private static VpnIpHealth Build(
        double badStintRate = 0.4, int badStintMinRequests = 5,
        int badIpMinRequests = 50, double badIpBlockRate = 0.15, int maxEntries = 1000)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vpn:BadStintRate"] = badStintRate.ToString(ci),
                ["Vpn:BadStintMinRequests"] = badStintMinRequests.ToString(ci),
                ["Vpn:BadIpMinRequests"] = badIpMinRequests.ToString(ci),
                ["Vpn:BadIpBlockRate"] = badIpBlockRate.ToString(ci),
                ["Vpn:IpHealthMaxEntries"] = maxEntries.ToString(ci),
            })
            .Build();
        return new VpnIpHealth(cfg, NullLogger<VpnIpHealth>.Instance);
    }

    [Fact]
    public void EvictsOldestEntries_WhenMaxEntriesExceeded()
    {
        var h = Build(maxEntries: 50);
        // 200 verschiedene IPs melden → die Tabelle darf nicht unbegrenzt wachsen.
        for (int i = 0; i < 200; i++)
            h.RecordStint($"10.0.0.{i}", requests: 1, blocks: 0);

        // Kern-Garantie (deterministisch): die Tabelle bleibt am Cap gedeckelt statt unbegrenzt zu wachsen.
        var snap = h.Snapshot();
        Assert.True(snap.Count <= 50, $"erwartet ≤ 50 Einträge, war {snap.Count}");
    }

    [Fact]
    public void AccumulatesPerIp()
    {
        var h = Build();
        h.RecordStint("1.1.1.1", requests: 10, blocks: 6);
        h.RecordStint("1.1.1.1", requests: 10, blocks: 5);
        h.RecordStint("2.2.2.2", requests: 10, blocks: 0);

        var snap = h.Snapshot();
        var s = snap.Single(x => x.Ip == "1.1.1.1");
        Assert.Equal(20, s.Requests);
        Assert.Equal(11, s.Blocks);
        Assert.Equal(2, s.Stints);
        Assert.Equal(2, s.BadStints);   // beide Phasen >=5 req und >=40 %
        Assert.Equal(0.55, s.BlockRate, 2);

        var good = snap.Single(x => x.Ip == "2.2.2.2");
        Assert.Equal(0, good.BadStints);
        Assert.False(good.RecurringBad);
    }

    [Fact]
    public void SmallStints_DoNotCountAsBad_EvenAboveRate()
    {
        // Viele kurze Stints (2 Requests, 1 Block = 50 %) — frueher haetten die geflaggt,
        // jetzt unter der Mindest-Stint-Groesse => keine schlechte Phase, kein Recurring-Bad.
        var h = Build(badStintMinRequests: 5, badIpMinRequests: 50, badIpBlockRate: 0.15);
        for (var i = 0; i < 40; i++)
            h.RecordStint("3.3.3.3", requests: 2, blocks: 1);

        var s = h.Snapshot().Single();
        Assert.Equal(80, s.Requests);
        Assert.Equal(0, s.BadStints);       // keine Phase qualifiziert sich als „schlecht"
        Assert.True(s.BlockRate > 0.15);    // Gesamt-Rate hoch...
        Assert.True(s.RecurringBad);        // ...also trotzdem als wiederholt schlecht erkannt (volumen-basiert)
    }

    [Fact]
    public void HealthyIp_LowCumulativeRate_NotRecurringBad()
    {
        // Reale Beobachtung aus Prod: ~4 % Gesamt-Block-Rate trotz vereinzelter Block-Spikes.
        var h = Build();
        for (var i = 0; i < 10; i++)
            h.RecordStint("4.4.4.4", requests: 50, blocks: 2);   // je 4 %

        var s = h.Snapshot().Single();
        Assert.Equal(500, s.Requests);
        Assert.Equal(20, s.Blocks);
        Assert.Equal(0.04, s.BlockRate, 2);
        Assert.False(s.RecurringBad);   // 4 % < 15 % Schwelle => KEIN Fehlalarm
    }

    [Fact]
    public void RecurringBad_WhenCumulativeRateHighOverEnoughRequests()
    {
        var h = Build(badIpMinRequests: 50, badIpBlockRate: 0.15);
        h.RecordStint("5.5.5.5", requests: 40, blocks: 12);   // 30 %, aber erst 40 req (< 50)
        var afterFirst = h.Snapshot().Single();
        Assert.False(afterFirst.RecurringBad);                 // Volumen noch zu klein

        h.RecordStint("5.5.5.5", requests: 40, blocks: 12);   // jetzt 80 req, 30 %
        var s = h.Snapshot().Single();
        Assert.True(s.RecurringBad);
        Assert.True(s.BlockRate >= 0.15);
    }

    [Fact]
    public void IgnoresEmptyIpOrZeroRequests()
    {
        var h = Build();
        h.RecordStint(null, 5, 5);
        h.RecordStint("", 5, 5);
        h.RecordStint("6.6.6.6", 0, 0);
        Assert.Empty(h.Snapshot());
    }

    [Fact]
    public void Snapshot_OrdersRecurringBadFirst()
    {
        var h = Build();
        h.RecordStint("good", 100, 1);            // 1 %
        h.RecordStint("bad", 100, 40);            // 40 % => recurring bad
        var snap = h.Snapshot();
        Assert.Equal("bad", snap[0].Ip);
        Assert.True(snap[0].RecurringBad);
        Assert.False(snap[1].RecurringBad);
    }
}
