using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace PirateChess.Api.Tests;

public class ChessableDirectControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private const string ServiceKeyHeader = "X-Service-Key";
    private const string ValidServiceKey = "test-service-key";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public ChessableDirectControllerTests(TestWebApplicationFactory factory) => _factory = factory;

    private HttpClient ClientWithServiceKey(string? key = ValidServiceKey)
    {
        var client = _factory.CreateClient();
        if (key is not null)
            client.DefaultRequestHeaders.Add(ServiceKeyHeader, key);
        return client;
    }

    [Fact]
    public async Task Test_MissingServiceKey_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/test", new { Bearer = "some-jwt" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Test_WrongServiceKey_Returns401()
    {
        var client = ClientWithServiceKey("nope");

        var response = await client.PostAsJsonAsync("/api/chessable/direct/test", new { Bearer = "some-jwt" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Test_MultipleServiceKeyHeaders_Returns401()
    {
        var client = _factory.CreateClient();
        // Zwei X-Service-Key-Header (einer gültig) → abgelehnt (Count != 1).
        client.DefaultRequestHeaders.Add(ServiceKeyHeader, ValidServiceKey);
        client.DefaultRequestHeaders.Add(ServiceKeyHeader, "extra");

        var response = await client.PostAsJsonAsync("/api/chessable/direct/test", new { Bearer = "some-jwt" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Test_ValidBearer_ReturnsUidAndCourseCount()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/test", new { Bearer = "some-valid-jwt" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DirectTestResp>(JsonOpts);
        Assert.Equal("12345", body!.Uid);
        Assert.Equal(2, body.CourseCount);
    }

    [Fact]
    public async Task Test_InvalidBearer_Returns400()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/test", new { Bearer = "not-a-real-jwt" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Test_EmptyBearer_Returns400()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/test", new { Bearer = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Courses_MissingServiceKey_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/courses", new { Bearer = "some-jwt" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Courses_ValidBearer_ReturnsCourseList()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/courses", new { Bearer = "some-valid-jwt" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<CourseItem>>(JsonOpts);
        Assert.Equal(2, body!.Count);
        Assert.Contains(body, c => c.Bid == "1001" && c.Name == "Test Course 1");
        Assert.Contains(body, c => c.Bid == "1002" && c.Name == "Test Course 2");
    }

    [Fact]
    public async Task Courses_InvalidBearer_Returns400()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/courses", new { Bearer = "not-a-real-jwt" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- /api/chessable/direct/course (tiefer Kurs-Abruf für rookhub-Import) ---

    [Fact]
    public async Task Course_MissingServiceKey_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/course",
            new { Bearer = "some-valid-jwt", Bid = "1001", Mode = "None" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Course_InvalidMode_Returns400()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/course",
            new { Bearer = "some-valid-jwt", Bid = "1001", Mode = "Nonsense" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Course_MissingBid_Returns400()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/course",
            new { Bearer = "some-valid-jwt", Bid = "", Mode = "None" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Course_InvalidBearer_Returns400()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/course",
            new { Bearer = "not-a-real-jwt", Bid = "1001", Mode = "None" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Course_ValidRequest_ReturnsCourseEnvelope()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/course",
            new { Bearer = "some-valid-jwt", Bid = "1001", Mode = "None" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CourseResp>(JsonOpts);
        Assert.Equal("1001", body!.Bid);
        Assert.Equal("None", body.Mode);
        Assert.NotNull(body.Pgn);
    }

    // ---- /course/start + /course/{jobId} (async mit Fortschritt) ----

    [Fact]
    public async Task CourseStart_MissingServiceKey_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/start",
            new { Bearer = "some-valid-jwt", Bid = "1001", Mode = "None" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CourseStart_Valid_ReturnsJobId()
    {
        var client = ClientWithServiceKey();
        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/start",
            new { Bearer = "some-valid-jwt", Bid = "1001", Mode = "None" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JobStartResp>(JsonOpts);
        Assert.False(string.IsNullOrWhiteSpace(body!.JobId));
    }

    [Fact]
    public async Task CourseStart_InvalidBearer_Returns400()
    {
        var client = ClientWithServiceKey();
        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/start",
            new { Bearer = "not-a-real-jwt", Bid = "1001", Mode = "None" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CourseProgress_UnknownJob_Returns404()
    {
        var client = ClientWithServiceKey();
        var response = await client.GetAsync("/api/chessable/direct/course/doesnotexist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CourseCached_MissingServiceKey_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/chessable/direct/course/123/cached");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CourseCached_UnknownBid_ReturnsFalse()
    {
        var client = ClientWithServiceKey();
        var response = await client.GetAsync("/api/chessable/direct/course/nope/cached");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CachedResp>(JsonOpts);
        Assert.False(body!.Cached);
    }

    [Fact]
    public async Task CachedBids_MissingServiceKey_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/chessable/direct/courses/cached");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CachedBids_ReturnsSeededBids()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PirateChess.Api.Data.AppDbContext>();
            db.CachedRawCourses.Add(new PirateChess.Api.Models.Entities.CachedRawCourse
            {
                Bid = "bulk-cached-1", RestResponseJson = "x", CachedAt = System.DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = ClientWithServiceKey();
        var response = await client.GetAsync("/api/chessable/direct/courses/cached");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CachedBidsResp>(JsonOpts);
        Assert.Contains("bulk-cached-1", body!.Bids);
    }

    private record CachedBidsResp(List<string> Bids);
    private record CachedResp(bool Cached);
    private record DirectTestResp(string Uid, int CourseCount);
    private record CourseItem(string Bid, string Name);
    private record CourseResp(string Bid, string Name, string Mode, int ChapterCount, int LineCount, string Pgn);
    private record JobStartResp(string JobId);
}
