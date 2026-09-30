using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using PirateChess.Api.Services;
using Xunit;

namespace PirateChess.Api.Tests;

/// <summary>
/// Regression: der benannte "Chessable"-HttpClient (auf den gluetun-Proxy :8888 verdrahtet)
/// wurde in Program.cs nie registriert. <c>CreateClient("Chessable")</c> lieferte daher einen
/// Default-Client OHNE Proxy, sodass <c>VpnRotationService.WaitForProxyReadyAsync</c> (Readiness-
/// Probe nach der Rotation) und der <c>VpnController</c>-IP-Status-Fallback am Tunnel vorbeiliefen
/// (Probe wirkungslos, Status meldete die Host-IP statt der VPN-Exit-IP).
/// </summary>
public class ChessableHttpClientRegistrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ChessableHttpClientRegistrationTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void ChessableNamedHttpClient_is_registered()
    {
        var options = _factory.Services
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(ChessableHttpClientFactory.ClientName);

        // AddChessableHttpClient setzt einen Default-Header (HttpClientActions) und
        // konfiguriert den Primary-Handler mit dem Proxy (HttpMessageHandlerBuilderActions).
        // Für einen NICHT registrierten Namen wären beide Listen leer.
        Assert.NotEmpty(options.HttpClientActions);
        Assert.NotEmpty(options.HttpMessageHandlerBuilderActions);
    }

    // S2-011: ohne explizites Timeout griffe der HttpClient-Standard von 100 s — ein hängender gluetun-Control-Server
    // hielt einen Tunnel so bis ~200 s auf „rotating" (stop-PUT + Recovery-PUT). Der Crawler begrenzt auf 5 s.
    [Fact]
    public void GluetunControlClient_has_explicit_short_timeout()
    {
        var client = _factory.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(VpnRotationService.ClientName);

        Assert.Equal(VpnRotationService.ControlTimeout, client.Timeout);
        Assert.InRange(client.Timeout, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
    }

    // S2-014: der echte ChessableHttpService braucht den ICurlRunner aus Program.cs. Die Test-Factory ersetzt den
    // Dienst durch ein Fake, darum hier aus dem echten Container bauen: fehlt die Registrierung, wirft das.
    [Fact]
    public void ChessableHttpService_resolves_with_the_registered_curl_runner()
    {
        Assert.IsType<CurlRunner>(_factory.Services.GetRequiredService<ICurlRunner>());
        Assert.NotNull(ActivatorUtilities.CreateInstance<ChessableHttpService>(_factory.Services));
    }
}
