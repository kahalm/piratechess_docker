using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

/// <summary>
/// Multi-Tunnel-Pool: AcquireAsync verteilt round-robin über die konfigurierten Proxys; ohne
/// Liste fällt es auf den Einzelwert zurück (= 1 Tunnel, bisheriges Verhalten). Ohne ControlUrl
/// ist die Rotation deaktiviert → AcquireAsync macht keinen HTTP-Call (rein In-Memory testbar).
/// </summary>
public class VpnTunnelPoolTests
{
    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static VpnRotationService Build(Dictionary<string, string?> settings)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new VpnRotationService(new FakeHttpClientFactory(), cfg, NullLogger<VpnRotationService>.Instance);
    }

    [Fact]
    public async Task Acquire_TwoProxies_RoundRobins()
    {
        var svc = Build(new() { ["Chessable:ProxyUrls"] = "http://a:8888,http://b:8888" });
        var p1 = (await svc.AcquireAsync()).ProxyUrl;
        var p2 = (await svc.AcquireAsync()).ProxyUrl;
        var p3 = (await svc.AcquireAsync()).ProxyUrl;
        var p4 = (await svc.AcquireAsync()).ProxyUrl;

        Assert.Equal("http://a:8888", p1);
        Assert.Equal("http://b:8888", p2);
        Assert.Equal("http://a:8888", p3);
        Assert.Equal("http://b:8888", p4);
    }

    [Fact]
    public async Task Acquire_SingleProxyFallback_AlwaysSameProxy()
    {
        var svc = Build(new() { ["Chessable:ProxyUrl"] = "http://only:8888" });
        Assert.Equal("http://only:8888", (await svc.AcquireAsync()).ProxyUrl);
        Assert.Equal("http://only:8888", (await svc.AcquireAsync()).ProxyUrl);
    }

    [Fact]
    public async Task Acquire_NoProxyConfigured_ReturnsNullProxyLease()
    {
        var svc = Build(new());   // weder ProxyUrls noch ProxyUrl → 1 Tunnel ohne Proxy (direkt)
        var lease = await svc.AcquireAsync();
        Assert.Null(lease.ProxyUrl);
        lease.Dispose();   // darf nicht werfen
    }

    [Fact]
    public async Task Acquire_ProxyUrlsList_OverridesSingleValue()
    {
        var svc = Build(new()
        {
            ["Chessable:ProxyUrl"] = "http://single:8888",
            ["Chessable:ProxyUrls"] = "http://x:8888,http://y:8888",
        });
        var got = new[] { (await svc.AcquireAsync()).ProxyUrl, (await svc.AcquireAsync()).ProxyUrl };
        Assert.Equal(new[] { "http://x:8888", "http://y:8888" }, got);
    }
}
