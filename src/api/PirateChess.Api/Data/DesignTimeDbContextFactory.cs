using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PirateChess.Api.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        // Dummy connection for migration generation only
        optionsBuilder.UseMySql(
            "Server=localhost;Database=piratechess;User=root;Password=dummy;",
            new MariaDbServerVersion(new Version(11, 0, 0)));

        return new AppDbContext(optionsBuilder.Options);
    }
}
