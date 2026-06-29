using piratechess_lib;

namespace PirateChess.Api.Services;

public interface IChessableHttpService
{
    Task<(string? jwt, string? error)> LoginAsync(string email, string password, CancellationToken ct = default);

    (string uid, string? error) ExtractUidFromBearer(string jwt);

    Task<(Dictionary<string, string>? courses, string? error)> GetCoursesAsync(
        string bearer, string uid, CancellationToken ct = default);

    Task<(RestResponseCourse? data, string? error)> FetchCourseDataAsync(
        string bearer, string uid, string bid,
        Action<string>? onChapterProgress = null,
        Action<string>? onLineProgress = null,
        Action<string>? onCumulativeLines = null,
        Action<string>? onRetry = null,
        Action<int>? onTotalLines = null,
        CancellationToken ct = default);

    Task<(int? totalLines, string? error)> GetCourseLineCountAsync(
        string bearer, string uid, string bid, CancellationToken ct = default);

    Task<(bool ok, int bytes, long ms, string? error, string snippet)> DebugFetchLineAsync(
        string bearer, string uid, int oid, CancellationToken ct = default);
}
