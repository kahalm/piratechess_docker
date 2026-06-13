using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class VpnRotationServiceTests
{
    [Fact]
    public void ParsePublicIp_ValidResponse_ReturnsIp()
    {
        // gluetun /v1/publicip/ip liefert ein Objekt mit public_ip + Geo-Feldern
        var json = """
            {"public_ip":"141.98.102.179","region":"Hesse","country":"Germany","city":"Frankfurt am Main"}
            """;

        Assert.Equal("141.98.102.179", VpnRotationService.ParsePublicIp(json));
    }

    [Fact]
    public void ParsePublicIp_MissingField_ReturnsNull()
    {
        Assert.Null(VpnRotationService.ParsePublicIp("""{"country":"Germany"}"""));
    }

    [Fact]
    public void ParsePublicIp_EmptyIp_ReturnsNull()
    {
        Assert.Null(VpnRotationService.ParsePublicIp("""{"public_ip":""}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"141.98.102.179\"")]
    public void ParsePublicIp_InvalidOrNonObject_ReturnsNull(string json)
    {
        Assert.Null(VpnRotationService.ParsePublicIp(json));
    }

    // --- Proxy-Readiness nach Rotation (Fix: gluetun :8888 liefert beim Reconnect kurz 503) ---

    [Fact]
    public void IsProxyReady_503_ReturnsFalse()
    {
        // gluetun lehnt den CONNECT-Tunnel während des Reconnects mit 503 ab → noch nicht bereit
        Assert.False(VpnRotationService.IsProxyReady(503));
    }

    [Theory]
    [InlineData(0)]    // Probe warf (Tunnel down / Timeout) → kein Statuscode
    [InlineData(-1)]
    public void IsProxyReady_NoResponse_ReturnsFalse(int status)
    {
        Assert.False(VpnRotationService.IsProxyReady(status));
    }

    [Theory]
    [InlineData(200)]  // Origin durch den Tunnel erreicht
    [InlineData(403)]  // Chessable blockt den simplen Probe-Client — Tunnel steht aber
    [InlineData(404)]
    [InlineData(405)]  // HEAD nicht erlaubt — Tunnel steht
    public void IsProxyReady_GotOriginResponse_ReturnsTrue(int status)
    {
        Assert.True(VpnRotationService.IsProxyReady(status));
    }

    // --- Rotations-Atomarität (Regression: VPN blieb nach abgebrochener Rotation 19h "stopped") ---

    [Fact]
    public async Task Rotation_CancelledAfterStop_StillRestartsVpn()
    {
        var bodies = new List<string>();
        var cts = new CancellationTokenSource();

        var handler = new StubHandler(async req =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
            lock (bodies) bodies.Add(body);
            // Simuliert einen abgebrochenen Import: direkt nach dem Stop wird die
            // Rotation gecancelt — genau das Fenster, in dem der Tunnel sonst
            // "stopped" liegen bliebe (der reguläre Start wird nie erreicht).
            if (body.Contains("stopped"))
                cts.Cancel();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var svc = BuildService(handler);

        // RotateNow nutzt denselben internen Pfad wie die Auto-Rotation.
        await svc.RotateNowAsync(cts.Token);

        // Trotz Cancellation nach dem Stop MUSS ein Start (running) erfolgt sein.
        Assert.Contains(bodies, b => b.Contains("stopped"));
        Assert.Contains(bodies, b => b.Contains("running"));
    }

    [Fact]
    public async Task Rotation_StartFails_TriggersRecoveryRestart()
    {
        var runningCount = 0;

        var handler = new StubHandler(async req =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
            await Task.CompletedTask;
            if (body.Contains("running"))
            {
                // Der erste (reguläre) Start scheitert → das finally muss erneut starten.
                if (Interlocked.Increment(ref runningCount) == 1)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var svc = BuildService(handler);
        await svc.RotateNowAsync(CancellationToken.None);

        // 1× regulärer Start (fehlgeschlagen) + 1× Recovery-Start im finally.
        Assert.True(runningCount >= 2, $"expected recovery restart, running PUTs={runningCount}");
    }

    [Fact]
    public async Task Rotation_Success_DoesNotForceRecoveryRestart()
    {
        var runningCount = 0;

        var handler = new StubHandler(async req =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
            await Task.CompletedTask;
            if (body.Contains("running"))
                Interlocked.Increment(ref runningCount);
            // publicip-Poll: gültige IP zurückgeben, damit der Erfolgspfad sauber endet.
            if (req.RequestUri!.AbsolutePath.Contains("publicip"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"public_ip":"1.2.3.4"}""") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var svc = BuildService(handler);
        await svc.RotateNowAsync(CancellationToken.None);

        // Genau ein Start — das finally darf bei Erfolg keinen zweiten auslösen.
        Assert.Equal(1, runningCount);
    }

    private static VpnRotationService BuildService(HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gluetun:ControlUrl"] = "http://gluetun:8000",
                ["Vpn:Enabled"] = "true",
                ["Vpn:RotateAfterRequests"] = "1",
                ["Vpn:RestartPauseMs"] = "0",   // keine 3s-Pause im Test
                ["Vpn:ProxyProbeUrl"] = "",     // Proxy-Readiness-Probe überspringen
            })
            .Build();

        return new VpnRotationService(
            new StubHttpClientFactory(handler), config, NullLogger<VpnRotationService>.Instance);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
