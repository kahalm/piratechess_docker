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

    // Minimal vollständiger Kurs (1 Kapitel, 1 Linie mit Content) — sonst lehnt SetAsync das Cachen ab.
    private static RestResponseCourse Complete(string courseJson)
    {
        var c = new RestResponseCourse { CourseJsonContent = courseJson };
        var ch = new RestResponseChapter { ChapterJsonContent = "{\"list\":{}}" };
        ch.ResponseLineList.Add(new RestResponseLine { LineJsonContent = "{\"game\":{}}" });
        c.ChapterList.Add(ch);
        return c;
    }

    [Fact]
    public async Task SetThenGet_ByBid_RoundTrips()
    {
        var cache = BuildCache();

        await cache.SetAsync("bid1", Complete("{\"course\":{\"data\":[]}}"));

        var got = await cache.GetAsync("bid1");
        Assert.NotNull(got);
        Assert.Equal("{\"course\":{\"data\":[]}}", got!.CourseJsonContent);
        Assert.Null(await cache.GetAsync("other-bid")); // anderer Kurs → kein Treffer
    }

    [Fact]
    public async Task Set_Twice_UpdatesSameBid()
    {
        var cache = BuildCache();
        await cache.SetAsync("bid1", Complete("v1"));
        await cache.SetAsync("bid1", Complete("v2"));

        var got = await cache.GetAsync("bid1");
        Assert.Equal("v2", got!.CourseJsonContent);
    }

    // --- Cache-Härtung: unvollständige Kurse NICHT cachen (Regression bid 116242) ---

    [Fact]
    public void IsComplete_FullCourse_True()
    {
        Assert.True(RawCourseCache.IsComplete(Complete("{}")));
    }

    [Fact]
    public void IsComplete_NoChapters_False()
    {
        Assert.False(RawCourseCache.IsComplete(new RestResponseCourse { CourseJsonContent = "{}" }));
        Assert.False(RawCourseCache.IsComplete(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public void IsComplete_EmptyLineContent_False(string lineContent)
    {
        var c = Complete("{}");
        c.ChapterList[0].ResponseLineList.Add(new RestResponseLine { LineJsonContent = lineContent });
        Assert.False(RawCourseCache.IsComplete(c));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    public void IsComplete_EmptyChapterContent_False(string chapterContent)
    {
        var c = Complete("{}");
        c.ChapterList[0].ChapterJsonContent = chapterContent;
        Assert.False(RawCourseCache.IsComplete(c));
    }

    [Fact]
    public async Task Set_IncompleteCourse_NotCached()
    {
        var cache = BuildCache();
        var poisoned = Complete("{\"course\":{}}");
        poisoned.ChapterList[0].ResponseLineList.Add(new RestResponseLine { LineJsonContent = "" }); // leere Linie

        await cache.SetAsync("bidX", poisoned);

        Assert.Null(await cache.GetAsync("bidX")); // wurde NICHT gecacht
    }
}
