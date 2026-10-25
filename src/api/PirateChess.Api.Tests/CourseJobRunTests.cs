using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using piratechess_lib;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

/// <summary>
/// Charakterisierung vor der Zerlegung Z47 (Job-Lauf aus dem Controller in einen Dienst): der course/start-Job liefert
/// dasselbe PGN und dieselben Zahlen wie der synchrone course-Endpunkt, Fortschrittszähler am Ende vollständig, und
/// ein Fehler des Chessable-Abrufs kommt als dieselbe Meldung an (synchron 400, Job „failed").
/// </summary>
public class CourseJobRunTests : IClassFixture<TestWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly TestWebApplicationFactory _factory;

    public CourseJobRunTests(TestWebApplicationFactory factory) => _factory = factory;

    private static HttpClient Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Service-Key", "test-service-key");
        return client;
    }

    private static async Task SeedAsync(IServiceProvider services, string bid)
    {
        var course = new RestResponseCourse
        {
            CourseJsonContent = "{\"course\":{\"data\":[{\"id\":1},{\"id\":2}]}}",
            ChapterList =
            [
                new RestResponseChapter
                {
                    ChapterJsonContent = "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"L1\"},{\"id\":11,\"name\":\"L2\"}]}}",
                    ResponseLineList =
                    [
                        new RestResponseLine { Oid = 10, LineJsonContent = "{\"game\":{\"color\":\"white\",\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"san\":\"e4\"},{\"id\":1,\"move\":1,\"san\":\"e5\"}]}}" },
                        new RestResponseLine { Oid = 11, LineJsonContent = "{\"game\":{\"color\":\"white\",\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"san\":\"d4\"}]}}" },
                    ]
                },
                new RestResponseChapter
                {
                    ChapterJsonContent = "{\"list\":{\"name\":\"Ch2\",\"title\":\"T2\",\"data\":[{\"id\":20,\"name\":\"L3\"}]}}",
                    ResponseLineList =
                    [
                        new RestResponseLine { Oid = 20, LineJsonContent = "{\"game\":{\"color\":\"black\",\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"san\":\"c4\"}]}}" },
                    ]
                },
            ]
        };
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CachedRawCourses.Add(new CachedRawCourse
        {
            Bid = bid,
            RestResponseJson = GzipText.Compress(JsonSerializer.Serialize(course)),
            CachedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task<ProgressResp> RunJobAsync(HttpClient client, object request)
    {
        var start = await client.PostAsJsonAsync("/api/chessable/direct/course/start", request);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var jobId = (await start.Content.ReadFromJsonAsync<JobStartResp>(JsonOpts))!.JobId;
        for (var i = 0; i < 200; i++)
        {
            var poll = await client.GetFromJsonAsync<ProgressResp>($"/api/chessable/direct/course/{jobId}", JsonOpts);
            if (poll!.Status != "running") return poll;
            await Task.Delay(50);
        }
        throw new TimeoutException("Job nicht fertig geworden");
    }

    [Theory]
    [InlineData("None")]
    [InlineData("FirstKeyMove")]
    [InlineData("AllKeyMoves")]
    public async Task Job_And_SyncCourse_GiveTheSamePgnAndCounts(string mode)
    {
        var bid = "58" + Random.Shared.Next(1000, 9999);
        await SeedAsync(_factory.Services, bid);
        var client = Client(_factory);

        var sync = await client.PostAsJsonAsync("/api/chessable/direct/course", new { Bearer = "", Bid = bid, Mode = mode });
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        var course = (await sync.Content.ReadFromJsonAsync<CourseResp>(JsonOpts))!;

        var job = await RunJobAsync(client, new { Bearer = "", Bid = bid, Mode = mode });

        Assert.Equal("completed", job.Status);
        Assert.Null(job.Error);
        Assert.Equal(course.Pgn, job.Pgn);
        Assert.Equal(course.Name, job.CourseName);
        Assert.Equal(mode, course.Mode);
        Assert.Equal((2, 3), (course.ChapterCount, course.LineCount));
        Assert.Equal((2, 3), (job.ChapterCount, job.LineCount));
        Assert.Equal((2, 2, 3, 3), (job.ChaptersDone, job.ChaptersTotal, job.LinesDone, job.LinesTotal));
        Assert.Contains("c4", job.Pgn);
    }

    /// <summary>Fake, dessen Kursabruf mit einer festen Fehlermeldung scheitert; alles andere wie der Standard-Fake.</summary>
    private sealed class FailingFetch(string error) : IChessableHttpService
    {
        private readonly FakeChessableHttpService _inner = new();
        public Task<(string? jwt, string? error)> LoginAsync(string email, string password, CancellationToken ct = default)
            => _inner.LoginAsync(email, password, ct);
        public (string uid, string? error) ExtractUidFromBearer(string jwt) => _inner.ExtractUidFromBearer(jwt);
        public Task<(Dictionary<string, string>? courses, string? error)> GetCoursesAsync(
            string bearer, string uid, CancellationToken ct = default, int? pinnedTunnel = null)
            => _inner.GetCoursesAsync(bearer, uid, ct, pinnedTunnel);
        public Task<(int? totalLines, string? error)> GetCourseLineCountAsync(string bearer, string uid, string bid, CancellationToken ct = default)
            => _inner.GetCourseLineCountAsync(bearer, uid, bid, ct);
        public Task<(bool ok, int bytes, long ms, string? error, string snippet)> DebugFetchLineAsync(
            string bearer, string uid, int oid, CancellationToken ct = default)
            => _inner.DebugFetchLineAsync(bearer, uid, oid, ct);
        public Task<(RestResponseCourse? data, string? error)> FetchCourseDataAsync(
            string bearer, string uid, string bid, CourseFetchOptions? options = null, CancellationToken ct = default)
            => Task.FromResult<(RestResponseCourse?, string?)>((null, error));
    }

    [Theory]
    [InlineData("Chessable: You do not own this course", "Chessable: You do not own this course")]
    [InlineData(" {} ", "Invalid bearer")]
    public async Task FetchError_SyncReturns400_JobFails_WithTheSameMessage(string fetchError, string expected)
    {
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IChessableHttpService>();
            s.AddSingleton<IChessableHttpService>(new FailingFetch(fetchError));
        }));
        var client = Client(host);
        var request = new { Bearer = "some-valid-jwt", Bid = "59" + Random.Shared.Next(1000, 9999), Mode = "None" };

        var sync = await client.PostAsJsonAsync("/api/chessable/direct/course", request);
        Assert.Equal(HttpStatusCode.BadRequest, sync.StatusCode);
        Assert.Equal(expected, (await sync.Content.ReadFromJsonAsync<MessageResp>(JsonOpts))!.Message);

        var job = await RunJobAsync(client, request);
        Assert.Equal("failed", job.Status);
        Assert.Equal(expected, job.Error);
        Assert.Null(job.Pgn);
    }

    private record JobStartResp(string JobId);
    private record MessageResp(string Message);
    private record CourseResp(string Bid, string Name, string Mode, int ChapterCount, int LineCount, string Pgn);
    private record ProgressResp(string Status, int ChaptersDone, int ChaptersTotal, int LinesDone, int LinesTotal,
        int ChapterCount, int LineCount, string? CourseName, string? Pgn, string? Error);
}
