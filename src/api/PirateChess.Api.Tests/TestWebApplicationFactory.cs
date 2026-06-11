using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PirateChess.Api.Data;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = "TestDb_" + Guid.NewGuid();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "TestSecretKeyThatIsAtLeast32CharsLong!!",
                ["Jwt:Issuer"] = "TestIssuer",
                ["Jwt:Audience"] = "TestAudience",
                ["Encryption:Key"] = "TestEncryptionKey32CharsLong!!!!",
                ["Service:ApiKey"] = "test-service-key",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove ALL EF Core / DB related registrations
            var toRemove = services.Where(d =>
                d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                || d.ServiceType == typeof(DbContextOptions)
                || (d.ServiceType.IsGenericType &&
                    d.ServiceType.GetGenericTypeDefinition() == typeof(DbContextOptions<>))
                || d.ImplementationType?.FullName?.Contains("MySql") == true
                || d.ImplementationType?.FullName?.Contains("Pomelo") == true
            ).ToList();

            foreach (var d in toRemove)
                services.Remove(d);

            // Re-add with InMemory
            services.AddDbContext<AppDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));

            // Replace ChessableHttpService with fake for tests
            var chessableDescriptor = services.FirstOrDefault(d =>
                d.ServiceType == typeof(IChessableHttpService));
            if (chessableDescriptor is not null)
                services.Remove(chessableDescriptor);

            services.AddSingleton<IChessableHttpService, FakeChessableHttpService>();
        });

        builder.UseEnvironment("Development");
    }
}
