using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using piratechess_lib;
using PirateChess.Api.Data;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class RawCourseCacheTests
{
    private static RawCourseCache BuildCache()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString(); // einmal festlegen → alle Scopes teilen denselben Store
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        return new RawCourseCache(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RawCourseCache>.Instance);
    }

    [Fact]
    public async Task SetThenGet_ByBid_RoundTrips()
    {
        var cache = BuildCache();
        var course = new RestResponseCourse { CourseJsonContent = "{\"course\":{\"data\":[]}}" };

        await cache.SetAsync("bid1", course);

        var got = await cache.GetAsync("bid1");
        Assert.NotNull(got);
        Assert.Equal("{\"course\":{\"data\":[]}}", got!.CourseJsonContent);
        Assert.Null(await cache.GetAsync("other-bid")); // anderer Kurs → kein Treffer
    }

    [Fact]
    public async Task Set_Twice_UpdatesSameBid()
    {
        var cache = BuildCache();
        await cache.SetAsync("bid1", new RestResponseCourse { CourseJsonContent = "v1" });
        await cache.SetAsync("bid1", new RestResponseCourse { CourseJsonContent = "v2" });

        var got = await cache.GetAsync("bid1");
        Assert.Equal("v2", got!.CourseJsonContent);
    }
}
