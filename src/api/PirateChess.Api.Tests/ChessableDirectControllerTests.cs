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
    public async Task BuildInfo_ReflectsEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("BUILD_GIT_SHA", "deadbeef");
        Environment.SetEnvironmentVariable("BUILD_GIT_REF", "v1.0.30");
        try
        {
            var client = ClientWithServiceKey();
            var response = await client.GetAsync("/api/chessable/direct/build-info");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
            Assert.Equal("deadbeef", body.GetProperty("sha").GetString());
            Assert.Equal("v1.0.30", body.GetProperty("ref").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BUILD_GIT_SHA", null);
            Environment.SetEnvironmentVariable("BUILD_GIT_REF", null);
        }
    }

    [Fact]
    public async Task BuildInfo_MissingServiceKey_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/chessable/direct/build-info");
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
    public async Task Test_WithValidTunnelIndex_PinsAndEchoesIndex()
    {
        var client = ClientWithServiceKey();

        // Im Test-Config gibt es genau 1 Tunnel (Index 0, kein Proxy/Control) → Pin 0 ist gültig.
        var response = await client.PostAsJsonAsync("/api/chessable/direct/test",
            new { Bearer = "some-valid-jwt", TunnelIndex = 0 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DirectTestResp>(JsonOpts);
        Assert.Equal("12345", body!.Uid);
        Assert.Equal(2, body.CourseCount);
        Assert.Equal(0, body.TunnelIndex);   // der gewählte Tunnel wird zurückgemeldet
    }

    [Fact]
    public async Task Test_WithOutOfRangeTunnelIndex_Returns400()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/test",
            new { Bearer = "some-valid-jwt", TunnelIndex = 99 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Test_WithoutTunnelIndex_DoesNotPin()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/test", new { Bearer = "some-valid-jwt" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DirectTestResp>(JsonOpts);
        Assert.Null(body!.TunnelIndex);   // ohne Pin: round-robin, kein Tunnel zurückgemeldet
    }

    [Fact]
    public async Task Tunnels_ReturnsTunnelList()
    {
        var client = ClientWithServiceKey();

        var response = await client.GetAsync("/api/chessable/direct/vpn/tunnels");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tunnels = await response.Content.ReadFromJsonAsync<List<TunnelStatusResp>>(JsonOpts);
        Assert.NotNull(tunnels);
        Assert.NotEmpty(tunnels);
        Assert.Equal(0, tunnels![0].Index);   // 0-basiert, passt zum Pin-Wert
    }

    [Fact]
    public async Task Tunnels_MissingServiceKey_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/chessable/direct/vpn/tunnels");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
    public async Task CourseStart_NoBearer_NotCached_Returns400()
    {
        // Ohne Bearer ist ein Start nur zulässig, wenn der Kurs gecacht ist; ein nicht gecachter Kurs
        // ohne Bearer bleibt „Bearer is required".
        var client = ClientWithServiceKey();
        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/start",
            new { Bearer = "", Bid = "1001", Mode = "None" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Seedet einen VOLLSTÄNDIGEN gecachten Kurs (altes Voll-Blob-Format: Linieninhalt inline,
    /// Oid 0) — besteht RawCourseCache.IsComplete, damit GetAsync ihn ausliefert.</summary>
    private async Task SeedCompleteCachedCourseAsync(string bid)
    {
        var course = new piratechess_lib.RestResponseCourse
        {
            CourseJsonContent = "{\"course\":{\"data\":[{\"id\":1}]}}",
            ChapterList =
            [
                new piratechess_lib.RestResponseChapter
                {
                    ChapterJsonContent = "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"L1\"}]}}",
                    ResponseLineList =
                    [
                        new piratechess_lib.RestResponseLine
                        {
                            Oid = 0,
                            LineJsonContent = "{\"game\":{\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"san\":\"e4\"}]}}"
                        }
                    ]
                }
            ]
        };
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PirateChess.Api.Data.AppDbContext>();
        db.CachedRawCourses.Add(new PirateChess.Api.Models.Entities.CachedRawCourse
        {
            Bid = bid,
            RestResponseJson = PirateChess.Api.Services.GzipText.Compress(JsonSerializer.Serialize(course)),
            CachedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CourseStart_NoBearer_Cached_ReturnsJobId()
    {
        // Der Kern des Features: liegt der Kurs im Rohdaten-Cache, startet der Job auch ohne Bearer.
        await SeedCompleteCachedCourseAsync("424242");
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/start",
            new { Bearer = "", Bid = "424242", Mode = "None" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JobStartResp>(JsonOpts);
        Assert.False(string.IsNullOrWhiteSpace(body!.JobId));
    }

    [Fact]
    public async Task Course_NoBearer_Cached_ReturnsCourse()
    {
        // Dieselbe Regel wie bei course/start: gecacht → Bearer optional (synchroner Endpoint).
        await SeedCompleteCachedCourseAsync("424243");
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/course",
            new { Bearer = "", Bid = "424243", Mode = "None" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CourseResp>(JsonOpts);
        Assert.Equal("424243", body!.Bid);
        Assert.Contains("e4", body.Pgn);
    }

    [Fact]
    public async Task Course_NoBearer_NotCached_Returns400()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/course",
            new { Bearer = "", Bid = "999998", Mode = "None" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CourseInfo_NoBearer_Cached_ReturnsTotal()
    {
        await SeedCompleteCachedCourseAsync("424244");
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/info",
            new { Bearer = "", Bid = "424244" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CourseInfoResp>(JsonOpts);
        Assert.Equal(1, body!.TotalLines);
        Assert.True(body.Cached);
    }

    [Fact]
    public async Task CourseInfo_NoBearer_NotCached_Returns400()
    {
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/info",
            new { Bearer = "", Bid = "999997" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    // --- Force-Refresh: gecachte Rohdaten verwerfen statt ewig den Erst-Import auszuliefern ----

    [Fact]
    public async Task DeleteCourseCache_Cached_RemovesEntry()
    {
        await SeedCompleteCachedCourseAsync("424250");
        var client = ClientWithServiceKey();

        var del = await client.DeleteAsync("/api/chessable/direct/course/424250/cache");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var cached = await client.GetAsync("/api/chessable/direct/course/424250/cached");
        var body = await cached.Content.ReadFromJsonAsync<CachedResp>(JsonOpts);
        Assert.False(body!.Cached);   // nächster Abruf holt wirklich frisch von Chessable
    }

    [Fact]
    public async Task DeleteCourseCache_NonNumericBid_Returns400()
    {
        var client = ClientWithServiceKey();
        var response = await client.DeleteAsync("/api/chessable/direct/course/nope/cache");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Course_ForceRefresh_WithoutBearer_Returns400_EvenIfCached()
    {
        // Force-Refresh heißt echter Chessable-Abruf → der „gecacht ⇒ Bearer optional"-Pfad greift nicht.
        await SeedCompleteCachedCourseAsync("424251");
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/course",
            new { Bearer = "", Bid = "424251", Mode = "None", ForceRefresh = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Course_ForceRefresh_DropsCachedRawDataAndRefetches()
    {
        await SeedCompleteCachedCourseAsync("424252");
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/course",
            new { Bearer = "some-valid-jwt", Bid = "424252", Mode = "None", ForceRefresh = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CourseResp>(JsonOpts);
        // Der gecachte Stand hatte 1 Kapitel/1 Linie („e4"); die Fake-Neuabfrage liefert einen
        // leeren Kurs → der alte Cache wurde tatsächlich verworfen und nicht wieder bedient.
        Assert.Equal(0, body!.ChapterCount);
        Assert.DoesNotContain("e4", body.Pgn);
    }

    [Fact]
    public async Task CourseStart_ForceRefresh_WithoutBearer_Returns400_EvenIfCached()
    {
        await SeedCompleteCachedCourseAsync("424253");
        var client = ClientWithServiceKey();

        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/start",
            new { Bearer = "", Bid = "424253", Mode = "None", ForceRefresh = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
        // Gültiges (numerisches), aber nicht gecachtes bid → 200 mit cached=false.
        var response = await client.GetAsync("/api/chessable/direct/course/999999/cached");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CachedResp>(JsonOpts);
        Assert.False(body!.Cached);
    }

    [Fact]
    public async Task CourseCached_NonNumericBid_Returns400()
    {
        var client = ClientWithServiceKey();
        // Nicht-numerisches bid wird abgelehnt (verhindert u. a. den unbegrenzten Per-bid-Lock).
        var response = await client.GetAsync("/api/chessable/direct/course/nope/cached");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    // --- /api/chessable/direct/course/parse (fetch-freier Parse von browser-erfasstem Roh-JSON) ---

    private const string ParseChapterJson = "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"L1\"}]}}";
    private const string ParseLineJson = "{\"game\":{\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"col\":\"w\",\"san\":\"e4\"}]}}";
    private const string ParseKeyLineJson = "{\"game\":{\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"col\":\"w\",\"san\":\"e4\",\"isKey\":true}]}}";

    [Fact]
    public async Task Parse_MissingServiceKey_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/parse",
            new { Bid = "1001", Mode = "None", Chapters = new[] { new { ChapterJson = ParseChapterJson, Lines = new[] { ParseLineJson } } } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Parse_InvalidBid_Returns400()
    {
        var client = ClientWithServiceKey();
        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/parse",
            new { Bid = "nope", Mode = "None", Chapters = new[] { new { ChapterJson = ParseChapterJson, Lines = new[] { ParseLineJson } } } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Parse_InvalidMode_Returns400()
    {
        var client = ClientWithServiceKey();
        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/parse",
            new { Bid = "1001", Mode = "Nonsense", Chapters = new[] { new { ChapterJson = ParseChapterJson, Lines = new[] { ParseLineJson } } } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Parse_NoChapters_Returns400()
    {
        var client = ClientWithServiceKey();
        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/parse",
            new { Bid = "1001", Mode = "None", Chapters = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Parse_ValidChapters_ReturnsPgnWithMove_NoTraining()
    {
        var client = ClientWithServiceKey();
        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/parse",
            new { Bid = "1001", Mode = "None", Chapters = new[] { new { ChapterJson = ParseChapterJson, Lines = new[] { ParseLineJson } } } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CourseResp>(JsonOpts);
        Assert.Equal("1001", body!.Bid);
        Assert.Equal("None", body.Mode);
        Assert.Equal(1, body.ChapterCount);
        Assert.Equal(1, body.LineCount);
        Assert.Contains("e4", body.Pgn);
        Assert.DoesNotContain("%tqu", body.Pgn);   // None-Mode → kein Trainingsmarker
        Assert.Contains("[ChessableOid \"10\"]", body.Pgn);   // oid = getList data[].id → für Fortschritts-Zuordnung
    }

    [Fact]
    public async Task Parse_FirstKeyMove_EmitsTrainingMarker()
    {
        var client = ClientWithServiceKey();
        var response = await client.PostAsJsonAsync("/api/chessable/direct/course/parse",
            new { Bid = "1001", Mode = "FirstKeyMove", Chapters = new[] { new { ChapterJson = ParseChapterJson, Lines = new[] { ParseKeyLineJson } } } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CourseResp>(JsonOpts);
        Assert.Equal("FirstKeyMove", body!.Mode);
        Assert.Contains("%tqu", body.Pgn);   // FirstKeyMove + isKey → Trainingsmarker
    }

    private record CachedBidsResp(List<string> Bids);
    private record CachedResp(bool Cached);
    private record DirectTestResp(string Uid, int CourseCount, int? TunnelIndex = null, string? TunnelProxy = null, string? ExitIp = null);
    private record TunnelStatusResp(int Index, string? ProxyUrl, string Label, bool Active, bool Rotating, bool CoolingDown);
    private record CourseItem(string Bid, string Name);
    private record CourseResp(string Bid, string Name, string Mode, int ChapterCount, int LineCount, string Pgn);
    private record CourseInfoResp(string Bid, int TotalLines, bool Cached);
    private record JobStartResp(string JobId);
}
