using piratechess_lib;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class RawCourseCacheTests
{
    [Fact]
    public void SetThenGet_ReturnsSameInstance_PerUidBid()
    {
        var cache = new RawCourseCache();
        var course = new RestResponseCourse { CourseJsonContent = "{}" };

        cache.Set("uid1", "bid1", course);

        Assert.Same(course, cache.Get("uid1", "bid1"));
        Assert.Null(cache.Get("uid1", "bid2")); // andere bid
        Assert.Null(cache.Get("uid2", "bid1")); // andere uid
    }
}
