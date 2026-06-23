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
}
