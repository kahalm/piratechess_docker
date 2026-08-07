using piratechess_lib;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

public class FakeChessableHttpService : IChessableHttpService
{
    public Task<(string? jwt, string? error)> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        if (email == "valid@chessable.com" && password == "valid")
            return Task.FromResult<(string? jwt, string? error)>(("fake-jwt-token", null));

        return Task.FromResult<(string? jwt, string? error)>((null, "Invalid credentials"));
    }

    public (string uid, string? error) ExtractUidFromBearer(string jwt)
    {
        if (jwt == "not-a-real-jwt")
            return ("", "Ungültiges JWT-Format.");

        return ("12345", null);
    }

    /// <summary>Letzter an GetCoursesAsync übergebener Pin-Index (für Assertions im Pin-Test).</summary>
    public int? LastPinnedTunnel { get; private set; }

    public Task<(Dictionary<string, string>? courses, string? error)> GetCoursesAsync(
        string bearer, string uid, CancellationToken ct = default, int? pinnedTunnel = null)
    {
        LastPinnedTunnel = pinnedTunnel;
        var courses = new Dictionary<string, string>
        {
            ["1001"] = "Test Course 1",
            ["1002"] = "Test Course 2"
        };
        return Task.FromResult<(Dictionary<string, string>? courses, string? error)>((courses, null));
    }

    public Task<(RestResponseCourse? data, string? error)> FetchCourseDataAsync(
        string bearer, string uid, string bid,
        Action<string>? onChapterProgress = null,
        Action<string>? onLineProgress = null,
        Action<string>? onCumulativeLines = null,
        Action<string>? onRetry = null,
        Action<int>? onTotalLines = null,
        bool bypassLineCache = false,
        CancellationToken ct = default)
    {
        var course = new RestResponseCourse
        {
            CourseJsonContent = "{\"course\":{\"data\":[]}}"
        };
        return Task.FromResult<(RestResponseCourse? data, string? error)>((course, null));
    }

    public Task<(int? totalLines, string? error)> GetCourseLineCountAsync(
        string bearer, string uid, string bid, CancellationToken ct = default)
        => Task.FromResult<(int?, string?)>((0, null));

    public Task<(bool ok, int bytes, long ms, string? error, string snippet)> DebugFetchLineAsync(
        string bearer, string uid, int oid, CancellationToken ct = default)
        => Task.FromResult<(bool, int, long, string?, string)>((true, 0, 0, null, ""));
}
