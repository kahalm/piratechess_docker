using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PirateChess.Api.Data;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class RawLineCacheTests
{
    private static RawLineCache BuildCache()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString(); // einmal festlegen → alle Scopes teilen denselben Store
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        return new RawLineCache(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RawLineCache>.Instance);
    }

    [Fact]
    public async Task SetThenGet_ByOid_RoundTrips()
    {
        var cache = BuildCache();

        await cache.SetAsync(12345, """{"game":{"data":[]}}""");

        Assert.Equal("""{"game":{"data":[]}}""", await cache.GetAsync(12345));
        Assert.Null(await cache.GetAsync(99999)); // andere Linie → kein Treffer
    }

    [Fact]
    public async Task Set_Twice_UpdatesSameOid()
    {
        var cache = BuildCache();
        await cache.SetAsync(7, """{"v":1}""");
        await cache.SetAsync(7, """{"v":2}""");

        Assert.Equal("""{"v":2}""", await cache.GetAsync(7));
    }

    [Fact]
    public async Task Get_LargeContent_RoundTripsViaGzip()
    {
        var cache = BuildCache();
        var big = "{\"pgn\":\"" + new string('a', 200_000) + "\"}"; // einzelne Linien können groß sein
        await cache.SetAsync(42, big);

        Assert.Equal(big, await cache.GetAsync(42));
    }

    // --- Cache-Härtung: leere / {}-Antworten NIE cachen (kein vergifteter Resume) ---

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public void IsComplete_EmptyOrBlock_False(string content)
        => Assert.False(RawLineCache.IsComplete(content));

    [Fact]
    public void IsComplete_RealContent_True()
        => Assert.True(RawLineCache.IsComplete("""{"game":{}}"""));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public async Task Set_EmptyOrBlock_NotCached(string content)
    {
        var cache = BuildCache();
        await cache.SetAsync(555, content);
        Assert.Null(await cache.GetAsync(555)); // wurde NICHT gecacht
    }
}
