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
    // Die Startup-Pflichtprüfungen in Program.cs (ConnectionString/Jwt) laufen als FRÜHE Inline-Reads
    // in WebApplication.CreateBuilder — die erreicht ConfigureAppConfiguration der Factory NICHT mehr.
    // Umgebungsvariablen liest CreateBuilder dagegen sofort (Env-Provider, `__` → `:`). Einmal je Prozess.
    static TestWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection",
            "Server=localhost;Database=test;Uid=test;Pwd=test;");
        Environment.SetEnvironmentVariable("Jwt__Secret", "TestSecretKeyThatIsAtLeast32CharsLong!!");
        Environment.SetEnvironmentVariable("Jwt__Issuer", "TestIssuer");
        Environment.SetEnvironmentVariable("Jwt__Audience", "TestAudience");
    }

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
                // Dummy — der DbContext wird unten auf InMemory umgestellt; nur damit die Startup-
                // Pflichtprüfung (Fail-fast) der ConnString erfüllt ist.
                ["ConnectionStrings:DefaultConnection"] = "Server=localhost;Database=test;Uid=test;Pwd=test;",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove ALL EF Core / DB related registrations.
            // EF Core 9 verbietet jetzt HART zwei DB-Provider im selben ServiceProvider (EF 8 tolerierte es):
            // AddDbContext registriert seit EF 9 zusätzlich ein `IDbContextOptionsConfiguration<AppDbContext>`,
            // das die Pomelo/MySQL-Konfiguration trägt — das muss mit raus, sonst koexistiert MySQL neben
            // InMemory und der Host wirft „Only a single database provider can be registered".
            var toRemove = services.Where(d =>
                d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                || d.ServiceType == typeof(DbContextOptions)
                || (d.ServiceType.IsGenericType &&
                    d.ServiceType.GetGenericTypeDefinition() == typeof(DbContextOptions<>))
                || d.ServiceType.FullName?.Contains("IDbContextOptionsConfiguration") == true
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
