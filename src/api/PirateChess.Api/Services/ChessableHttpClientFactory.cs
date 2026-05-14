namespace PirateChess.Api.Services;

public static class ChessableHttpClientFactory
{
    public const string ClientName = "Chessable";

    public static IServiceCollection AddChessableHttpClient(
        this IServiceCollection services, IConfiguration configuration)
    {
        var proxyUrl = configuration["Chessable:ProxyUrl"];

        services.AddHttpClient(ClientName, client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "PirateChess/1.0");
        })
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new HttpClientHandler();
            if (!string.IsNullOrEmpty(proxyUrl))
            {
                handler.Proxy = new System.Net.WebProxy(proxyUrl);
                handler.UseProxy = true;
            }
            return handler;
        });

        return services;
    }
}
