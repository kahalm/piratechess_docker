using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class VpnIpHealthTests
{
    private static VpnIpHealth Build(double badRate = 0.4)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Vpn:BadStintRate"] = badRate.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            .Build();
        return new VpnIpHealth(cfg, NullLogger<VpnIpHealth>.Instance);
    }

    [Fact]
    public void AccumulatesPerIp_AndCountsBadStints()
    {
        var h = Build(badRate: 0.4);
        h.RecordStint("1.1.1.1", requests: 10, blocks: 6);  // 60% → schlechte Phase
        h.RecordStint("1.1.1.1", requests: 10, blocks: 5);  // 50% → schlechte Phase (2.)
        h.RecordStint("2.2.2.2", requests: 10, blocks: 0);  // sauber

        var snap = h.Snapshot();
        var bad = snap.Single(s => s.Ip == "1.1.1.1");
        Assert.Equal(20, bad.Requests);
        Assert.Equal(11, bad.Blocks);
        Assert.Equal(2, bad.Stints);
        Assert.Equal(2, bad.BadStints);   // beide Phasen über Schwelle
        Assert.Equal(0.55, bad.BlockRate, 2);

        var good = snap.Single(s => s.Ip == "2.2.2.2");
        Assert.Equal(0, good.BadStints);

        Assert.Equal("1.1.1.1", snap[0].Ip);   // schlechteste zuerst
    }

    [Fact]
    public void GoodStints_DoNotCountAsBad()
    {
        var h = Build(badRate: 0.4);
        h.RecordStint("3.3.3.3", 10, 3);   // 30% < Schwelle → nicht schlecht
        var s = h.Snapshot().Single();
        Assert.Equal(1, s.Stints);
        Assert.Equal(0, s.BadStints);
    }

    [Fact]
    public void IgnoresEmptyIpOrZeroRequests()
    {
        var h = Build();
        h.RecordStint(null, 5, 5);
        h.RecordStint("", 5, 5);
        h.RecordStint("4.4.4.4", 0, 0);   // keine Requests → nichts zu werten
        Assert.Empty(h.Snapshot());
    }
}
