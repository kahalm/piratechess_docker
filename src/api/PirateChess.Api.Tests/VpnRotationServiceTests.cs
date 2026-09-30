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
                // Der reguläre Start scheitert in beiden Versuchen (5xx wird einmal wiederholt, S2-011)
                // → das finally muss erneut starten.
                if (Interlocked.Increment(ref runningCount) <= 2)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var svc = BuildService(handler);
        await svc.RotateNowAsync(CancellationToken.None);

        // 2× regulärer Start (fehlgeschlagen) + 1× Recovery-Start im finally.
        Assert.True(runningCount >= 3, $"expected recovery restart, running PUTs={runningCount}");
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

    [Fact]
    public async Task Rotation_ReadsAndReturnsPublicIp()
    {
        // Nach der Rotation (und dem Proxy-Ready-Warten) wird die neue Public-IP von gluetun gelesen
        // und zurückgegeben → _currentIp ist gesetzt, VpnIpHealth kann den Stint der IP zuordnen.
        var handler = new StubHandler(async req =>
        {
            await Task.CompletedTask;
            if (req.RequestUri!.AbsolutePath.Contains("publicip"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"public_ip":"9.8.7.6"}""") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var svc = BuildService(handler);

        var ip = await svc.RotateNowAsync(CancellationToken.None);

        Assert.Equal("9.8.7.6", ip);
    }

    [Fact]
    public async Task Rotation_StartHitsStalePooledConnection_RetriesWithoutForcingRecovery()
    {
        // Regression: Nach der stop→Pause→start-Sequenz schließt gluetun die
        // serverseitige Keep-Alive-Verbindung. .NET griff die tote gepoolte
        // Verbindung beim start-PUT wieder auf → "Connection reset by peer", was als
        // Warn-Paar (rotation failed + forcing restart) geloggt wurde. Der interne
        // Retry muss den Reset abfangen und den Start frisch wiederholen, OHNE den
        // catch/Recovery-finally-Pfad auszulösen.
        var runningCount = 0;
        var publicIpQueried = false;

        var handler = new StubHandler(async req =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();

            // Der publicip-Poll wird NUR auf dem Erfolgspfad erreicht (nach bestätigtem Start).
            if (req.RequestUri!.AbsolutePath.Contains("publicip"))
            {
                publicIpQueried = true;
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"public_ip":"1.2.3.4"}""") };
            }

            if (body.Contains("running"))
            {
                // Erster Start trifft die tote gepoolte Verbindung: Transport-Reset
                // (HttpRequestException OHNE StatusCode), KEIN HTTP-Fehlerstatus.
                if (Interlocked.Increment(ref runningCount) == 1)
                    throw new HttpRequestException("Connection reset by peer");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var svc = BuildService(handler);
        await svc.RotateNowAsync(CancellationToken.None);

        // Genau 2 running-PUTs: 1× Reset + 1× frischer Retry — kein dritter aus dem finally.
        Assert.Equal(2, runningCount);
        // Rotation lief auf dem Erfolgspfad weiter (publicip gepollt) → der Reset wurde
        // intern aufgefangen, nicht bis ins catch/Recovery-finally durchgereicht.
        Assert.True(publicIpQueried,
            "publicip should be polled → rotation stayed on success path via retry");
    }

    [Fact]
    public async Task Rotation_StartReturnsErrorStatus_StillTriggersRecoveryRestart_NotSwallowedByRetry()
    {
        // Abgrenzung zum Retry: Ein 4xx (z. B. falscher X-API-Key nach Aktivierung der gluetun-Auth)
        // ist KEIN vorübergehender Fehler und darf NICHT vom Retry geschluckt werden — er muss wie
        // bisher ins Recovery-finally durchschlagen. (5xx wird seit S2-011 einmal wiederholt.)
        var runningCount = 0;

        var handler = new StubHandler(async req =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
            await Task.CompletedTask;
            if (body.Contains("running") && Interlocked.Increment(ref runningCount) == 1)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized); // 401, kein Reset
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var svc = BuildService(handler);
        await svc.RotateNowAsync(CancellationToken.None);

        // 1× regulärer Start (401, propagiert, nicht wiederholt) + 1× Recovery-Start im finally.
        Assert.Equal(2, runningCount);
    }

    // --- S2-011: hängender / kurz gestörter gluetun-Control-Server ------------------------------------

    [Fact]
    public async Task Rotation_StopPutTimesOut_IsRetried_AndRotationCompletes()
    {
        // Der Control-Server nimmt das erste stop-PUT an, antwortet aber nicht (z. B. während seines eigenen
        // Reconnects). Das Client-Timeout bricht ab; vorher war das eine TaskCanceledException, die NICHT
        // wiederholt wurde → Rotation gescheitert, Recovery-PUT, alte IP. Jetzt: ein zweiter Versuch.
        int stops = 0, runnings = 0;
        var handler = new StubHandler(async (req, ct) =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
            if (body.Contains("stopped") && Interlocked.Increment(ref stops) == 1)
                await Task.Delay(Timeout.Infinite, ct);   // hängt; nur das Client-Timeout beendet den Aufruf
            if (body.Contains("running")) Interlocked.Increment(ref runnings);
            if (req.RequestUri!.AbsolutePath.Contains("publicip"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"public_ip":"5.6.7.8"}""") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var svc = BuildService(handler, clientTimeout: TimeSpan.FromMilliseconds(200));

        var ip = await svc.RotateNowAsync(CancellationToken.None);

        Assert.Equal("5.6.7.8", ip);
        Assert.Equal(2, stops);
        Assert.Equal(1, runnings);   // kein Recovery-PUT
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Rotation_StopPutReturns5xx_IsRetried_AndRotationCompletes(HttpStatusCode status)
    {
        int stops = 0, runnings = 0;
        var handler = new StubHandler(async req =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
            if (body.Contains("stopped") && Interlocked.Increment(ref stops) == 1)
                return new HttpResponseMessage(status);
            if (body.Contains("running")) Interlocked.Increment(ref runnings);
            if (req.RequestUri!.AbsolutePath.Contains("publicip"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"public_ip":"5.6.7.8"}""") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var svc = BuildService(handler);

        var ip = await svc.RotateNowAsync(CancellationToken.None);

        Assert.Equal("5.6.7.8", ip);
        Assert.Equal(2, stops);
        Assert.Equal(1, runnings);
    }

    [Fact]
    public async Task Rotation_ControlServerHangsCompletely_EndsAfterTimeouts_AndReleasesTunnel()
    {
        // Worst Case: jeder Aufruf hängt. Die Rotation muss nach 2× stop + 2× Recovery-running (je ein
        // Client-Timeout) enden und den Tunnel wieder freigeben, statt ihn minutenlang auf „rotating" zu halten.
        var calls = 0;
        var handler = new StubHandler(async (req, ct) =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var svc = BuildService(handler, clientTimeout: TimeSpan.FromMilliseconds(200));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ip = await svc.RotateNowAsync(CancellationToken.None);
        sw.Stop();

        Assert.Null(ip);
        Assert.Equal(4, calls);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"rotation took {sw.Elapsed}");
        Assert.False(svc.DescribeTunnels()[0].Rotating);
    }

    [Fact]
    public async Task Rotation_CallerCancels_IsNotRetried()
    {
        // Abgrenzung: bricht der Aufrufer selbst ab, ist das kein Timeout → kein zweiter stop-Versuch.
        var stops = 0;
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(async req =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
            if (body.Contains("stopped"))
            {
                Interlocked.Increment(ref stops);
                cts.Cancel();
                throw new TaskCanceledException();
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var svc = BuildService(handler);

        await svc.RotateNowAsync(cts.Token);

        Assert.Equal(1, stops);
    }

    [Theory]
    [InlineData(null, true)]                                  // Transportfehler (Reset)
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]          // falscher X-API-Key
    [InlineData(HttpStatusCode.NotFound, false)]
    public void IsTransientControlFailure_HttpStatus(HttpStatusCode? status, bool expected)
    {
        var ex = new HttpRequestException("x", null, status);
        Assert.Equal(expected, VpnTunnel.IsTransientControlFailure(ex, CancellationToken.None));
    }

    [Fact]
    public void IsTransientControlFailure_TimeoutVsCallerCancel()
    {
        Assert.True(VpnTunnel.IsTransientControlFailure(
            new TaskCanceledException("timeout", new TimeoutException()), CancellationToken.None));
        Assert.False(VpnTunnel.IsTransientControlFailure(
            new TaskCanceledException(), new CancellationToken(canceled: true)));
        Assert.False(VpnTunnel.IsTransientControlFailure(new InvalidOperationException(), CancellationToken.None));
    }

    [Fact]
    public async Task ControlUrl_FallsBackTo_CrawlerName_GluetunApiUrl()
    {
        // Der Crawler konfiguriert denselben Control-Server als Gluetun:ApiUrl. piratechess nimmt den Namen
        // als letzten Fallback an (ControlUrls > ControlUrl > ApiUrl).
        string? host = null;
        var handler = new StubHandler(req =>
        {
            host = req.RequestUri!.Host;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"public_ip":"4.4.4.4"}""") });
        });
        var svc = new VpnRotationService(new StubHttpClientFactory(handler),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gluetun:ControlUrl"] = "",               // wie appsettings.json
                ["Gluetun:ApiUrl"] = "http://gluetun-api:8000",
            }).Build(),
            NullLogger<VpnRotationService>.Instance);

        Assert.Equal("4.4.4.4", await svc.GetTunnelPublicIpAsync(0));
        Assert.Equal("gluetun-api", host);
    }

    [Fact]
    public async Task ControlUrl_WinsOver_GluetunApiUrl()
    {
        string? host = null;
        var handler = new StubHandler(req =>
        {
            host = req.RequestUri!.Host;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"public_ip":"4.4.4.4"}""") });
        });
        var svc = new VpnRotationService(new StubHttpClientFactory(handler),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gluetun:ControlUrl"] = "http://gluetun-control:8000",
                ["Gluetun:ApiUrl"] = "http://gluetun-api:8000",
            }).Build(),
            NullLogger<VpnRotationService>.Instance);

        await svc.GetTunnelPublicIpAsync(0);
        Assert.Equal("gluetun-control", host);
    }

    // --- Token-gekoppelte Rotation: Ping-Pong dämpfen -----------------------
    // Verschränken sich ein langer Import (uid A) und Einzel-Requests eines anderen Bearers (uid B),
    // erzwang JEDER uid-Wechsel eine volle, synchron abgewartete Rotation (~10–20 s) — hin und zurück.
    // Solange die aktuelle IP ihre Mindest-Haltedauer nicht erfüllt hat, wird gar nicht rotiert.
    [Fact]
    public async Task TokenRotation_UidPingPongWithinMinHold_DoesNotRotate()
    {
        var stops = 0;
        var handler = new StubHandler(async req =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
            if (body.Contains("stopped")) Interlocked.Increment(ref stops);
            if (req.RequestUri!.AbsolutePath.Contains("publicip"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"public_ip":"1.2.3.4"}""") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var svc = BuildService(handler, minHoldSec: 600, rotateAfter: 1000);

        (await svc.AcquireAsync("uidA", CancellationToken.None)).Dispose();  // erste Nutzung
        (await svc.AcquireAsync("uidB", CancellationToken.None)).Dispose();  // Fremd-Request …
        (await svc.AcquireAsync("uidA", CancellationToken.None)).Dispose();  // … und zurück zum Import
        (await svc.AcquireAsync("uidB", CancellationToken.None)).Dispose();

        // Die IP ist noch innerhalb ihrer Haltedauer → kein einziger stop/start-Zyklus.
        Assert.Equal(0, stops);
    }

    [Fact]
    public async Task TokenRotation_MinHoldZero_RotatesOnEveryUidChange()
    {
        // Abgrenzung: ohne Haltedauer (Vpn:TokenMinHoldSec=0) bleibt das bisherige Verhalten —
        // jeder Token-Wechsel bekommt seine eigene Exit-IP.
        var stops = 0;
        var handler = new StubHandler(async req =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
            if (body.Contains("stopped")) Interlocked.Increment(ref stops);
            if (req.RequestUri!.AbsolutePath.Contains("publicip"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"public_ip":"1.2.3.4"}""") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var svc = BuildService(handler, minHoldSec: 0, rotateAfter: 1000);

        (await svc.AcquireAsync("uidA", CancellationToken.None)).Dispose();
        (await svc.AcquireAsync("uidB", CancellationToken.None)).Dispose();
        (await svc.AcquireAsync("uidA", CancellationToken.None)).Dispose();

        Assert.Equal(2, stops);
    }

    [Fact]
    public async Task TokenRotation_SameUid_NeverRotates()
    {
        var stops = 0;
        var handler = new StubHandler(async req =>
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
            await Task.CompletedTask;
            if (body.Contains("stopped")) Interlocked.Increment(ref stops);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var svc = BuildService(handler, minHoldSec: 0, rotateAfter: 1000);

        (await svc.AcquireAsync("uidA", CancellationToken.None)).Dispose();
        (await svc.AcquireAsync("uidA", CancellationToken.None)).Dispose();

        Assert.Equal(0, stops);
    }

    private static VpnRotationService BuildService(HttpMessageHandler handler, int minHoldSec = 0, int rotateAfter = 1,
        TimeSpan? clientTimeout = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gluetun:ControlUrl"] = "http://gluetun:8000",
                ["Vpn:Enabled"] = "true",
                ["Vpn:RotateAfterRequests"] = rotateAfter.ToString(),
                ["Vpn:RestartPauseMs"] = "0",   // keine 3s-Pause im Test
                ["Vpn:ProxyProbeUrl"] = "",     // Proxy-Readiness-Probe überspringen
                ["Vpn:TokenMinHoldSec"] = minHoldSec.ToString(),
            })
            .Build();

        return new VpnRotationService(
            new StubHttpClientFactory(handler, clientTimeout), config, NullLogger<VpnRotationService>.Instance);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler, TimeSpan? timeout = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false);
            if (timeout is { } t) client.Timeout = t;
            return client;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
            : this((req, _) => responder(req)) { }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
