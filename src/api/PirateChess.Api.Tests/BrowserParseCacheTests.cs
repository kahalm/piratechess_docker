using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PirateChess.Api.Data;
using PirateChess.Api.Models.Entities;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

/// <summary>
/// Browser-Import (<c>course/parse</c>) und der geteilte Rohdaten-Cache. Der Parser liest Kapitel und
/// Linien POSITIONSBASIERT: der n-te Eintrag der getList-Antwort bekommt die n-te mitgeschickte Linie.
/// Schickt der Browser nur einen Teil der Linien eines Kapitels, müssen sie über ihre oid zugeordnet werden.
/// </summary>
public class BrowserParseCacheTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public BrowserParseCacheTests(TestWebApplicationFactory factory) => _factory = factory;

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Service-Key", "test-service-key");
        return client;
    }

    private const string TwoLineChapter =
        "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"L1\"},{\"id\":11,\"name\":\"L2\"}]}}";

    private static string LineJson(string san)
        => "{\"game\":{\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"col\":\"w\",\"san\":\"" + san + "\"}]}}";

    private record CourseResp(string Bid, string Name, string Mode, int ChapterCount, int LineCount, string Pgn);

    [Fact]
    public async Task Parse_PartialLinesWithOids_AssignsLineToItsOwnOid()
    {
        // Regression: nur die ZWEITE Linie des Kapitels (oid 11) wird mitgeschickt — z. B. beim
        // inkrementellen „Kurs holen", der schon importierte Linien auslässt. Positionsbasiert landete
        // sie unter oid 10 und dem Namen der ersten Linie.
        var response = await Client().PostAsJsonAsync("/api/chessable/direct/course/parse", new
        {
            Bid = "2001",
            Mode = "None",
            Chapters = new[] { new { ChapterJson = TwoLineChapter, Lines = new[] { LineJson("d4") }, LineOids = new[] { "11" } } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CourseResp>(JsonOpts);
        Assert.Contains("d4", body!.Pgn);
        Assert.Contains("[ChessableOid \"11\"]", body.Pgn);
        Assert.DoesNotContain("[ChessableOid \"10\"]", body.Pgn);
        Assert.Contains("[White \"L2\"]", body.Pgn);
        Assert.Equal(1, body.LineCount);
    }

    // Jeder Test nutzt eigene bids/oids: die Fixture teilt sich eine In-Memory-DB über alle Tests der Klasse.

    private static string ChapterJson(params (int Id, string Name)[] entries)
        => "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[" +
           string.Join(",", entries.Select(e => "{\"id\":" + e.Id + ",\"name\":\"" + e.Name + "\"}")) + "]}}";

    private static object Chapter(string chapterJson, string?[] lines, string[]? oids)
        => new { ChapterJson = chapterJson, Lines = lines, LineOids = oids };

    private async Task<CourseResp> ParseOkAsync(object payload)
    {
        var response = await Client().PostAsJsonAsync("/api/chessable/direct/course/parse", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CourseResp>(JsonOpts))!;
    }

    private async Task SeedLineAsync(int oid, string json)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CachedRawLines.Add(new CachedRawLine { Oid = oid, LineJsonContent = GzipText.Compress(json), CachedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private async Task<string?> CachedLineAsync(int oid)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = db.CachedRawLines.FirstOrDefault(c => c.Oid == oid);
        return row is null ? null : GzipText.Decompress(row.LineJsonContent);
    }

    private async Task<bool> CourseCachedAsync(string bid)
    {
        var body = await Client().GetFromJsonAsync<JsonElement>($"/api/chessable/direct/course/{bid}/cached", JsonOpts);
        return body.GetProperty("cached").GetBoolean();
    }

    [Fact]
    public async Task Parse_LineWithoutContent_IsFilledFromSharedCache()
    {
        await SeedLineAsync(3101, LineJson("c4"));
        var body = await ParseOkAsync(new
        {
            Bid = "3100", Mode = "None",
            Chapters = new[] { Chapter(ChapterJson((3101, "English")), new string?[] { null }, new[] { "3101" }) }
        });
        Assert.Contains("c4", body.Pgn);
        Assert.Contains("[ChessableOid \"3101\"]", body.Pgn);
        Assert.Equal(1, body.LineCount);
    }

    [Fact]
    public async Task Parse_LineMissingEverywhere_IsLeftOutWithoutShiftingTheOthers()
    {
        var body = await ParseOkAsync(new
        {
            Bid = "3200", Mode = "None",
            Chapters = new[] { Chapter(ChapterJson((3201, "A"), (3202, "B")), new[] { null, LineJson("d4") }, new[] { "3201", "3202" }) }
        });
        Assert.Contains("[ChessableOid \"3202\"]", body.Pgn);
        Assert.Contains("[White \"B\"]", body.Pgn);
        Assert.DoesNotContain("3201", body.Pgn);
        Assert.Equal(1, body.LineCount);
    }

    [Fact]
    public async Task Parse_BrowserLines_LandInSharedCache()
    {
        await ParseOkAsync(new
        {
            Bid = "3300", Mode = "None",
            Chapters = new[] { Chapter(ChapterJson((3301, "A")), new[] { LineJson("e4") }, new[] { "3301" }) }
        });
        Assert.Equal(LineJson("e4"), await CachedLineAsync(3301));
    }

    [Fact]
    public async Task Parse_ExistingCachedLine_IsNeverOverwritten_ButTheSentLineIsUsed()
    {
        await SeedLineAsync(3401, LineJson("c4"));
        var body = await ParseOkAsync(new
        {
            Bid = "3400", Mode = "None",
            Chapters = new[] { Chapter(ChapterJson((3401, "A")), new[] { LineJson("g3") }, new[] { "3401" }) }
        });
        Assert.Contains("g3", body.Pgn);
        Assert.Equal(LineJson("c4"), await CachedLineAsync(3401));
    }

    [Fact]
    public async Task Parse_JsonWithoutGame_IsNotCached()
    {
        await ParseOkAsync(new
        {
            Bid = "3500", Mode = "None",
            Chapters = new[] { Chapter(ChapterJson((3501, "A"), (3502, "B")), new[] { "{\"x\":1}", LineJson("e4") }, new[] { "3501", "3502" }) }
        });
        Assert.Null(await CachedLineAsync(3501));
        Assert.NotNull(await CachedLineAsync(3502));
    }

    private const string OneChapterCourse = "{\"course\":{\"data\":[{\"id\":1}]}}";

    [Fact]
    public async Task Parse_CompleteCourse_BecomesCourseCache()
    {
        Assert.False(await CourseCachedAsync("3600"));
        await ParseOkAsync(new
        {
            Bid = "3600", Mode = "None", CourseJson = OneChapterCourse, Complete = true,
            Chapters = new[] { Chapter(ChapterJson((3601, "A"), (3602, "B")), new[] { LineJson("e4"), LineJson("d4") }, new[] { "3601", "3602" }) }
        });
        Assert.True(await CourseCachedAsync("3600"));
    }

    [Fact]
    public async Task Parse_NotMarkedComplete_WritesNoCourseCache()
    {
        await ParseOkAsync(new
        {
            Bid = "3700", Mode = "None", CourseJson = OneChapterCourse, Complete = false,
            Chapters = new[] { Chapter(ChapterJson((3701, "A"), (3702, "B")), new[] { LineJson("e4"), LineJson("d4") }, new[] { "3701", "3702" }) }
        });
        Assert.False(await CourseCachedAsync("3700"));
    }

    [Fact]
    public async Task Parse_CompleteButALineHasNoContent_WritesNoCourseCache()
    {
        await ParseOkAsync(new
        {
            Bid = "3800", Mode = "None", CourseJson = OneChapterCourse, Complete = true,
            Chapters = new[] { Chapter(ChapterJson((3801, "A"), (3802, "B")), new[] { LineJson("e4"), null }, new[] { "3801", "3802" }) }
        });
        Assert.False(await CourseCachedAsync("3800"));
    }

    [Fact]
    public async Task Parse_CompleteButChapterCountDiffers_WritesNoCourseCache()
    {
        await ParseOkAsync(new
        {
            Bid = "3900", Mode = "None", CourseJson = "{\"course\":{\"data\":[{\"id\":1},{\"id\":2}]}}", Complete = true,
            Chapters = new[] { Chapter(ChapterJson((3901, "A"), (3902, "B")), new[] { LineJson("e4"), LineJson("d4") }, new[] { "3901", "3902" }) }
        });
        Assert.False(await CourseCachedAsync("3900"));
    }

    [Theory]
    [InlineData("null-ohne-oids")]
    [InlineData("anzahl")]
    [InlineData("oid-ungueltig")]
    public async Task Parse_MalformedLineOids_Returns400(string fall)
    {
        var chapter = fall switch
        {
            "null-ohne-oids" => Chapter(ChapterJson((4101, "A")), new string?[] { null }, null),
            "anzahl" => Chapter(ChapterJson((4101, "A")), new[] { LineJson("e4") }, new[] { "4101", "4102" }),
            _ => Chapter(ChapterJson((4101, "A")), new[] { LineJson("e4") }, new[] { "abc" }),
        };
        var response = await Client().PostAsJsonAsync("/api/chessable/direct/course/parse",
            new { Bid = "4100", Mode = "None", Chapters = new[] { chapter } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private record CachedLinesResp(List<string> Oids);

    [Fact]
    public async Task LinesCached_ReturnsOnlyTheCachedOids()
    {
        await SeedLineAsync(4201, LineJson("e4"));
        var response = await Client().PostAsJsonAsync("/api/chessable/direct/lines/cached", new { Oids = new[] { "4201", "4202" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CachedLinesResp>(JsonOpts);
        Assert.Equal(new[] { "4201" }, body!.Oids);
    }

    [Fact]
    public async Task LinesCached_InvalidOid_Returns400()
    {
        var response = await Client().PostAsJsonAsync("/api/chessable/direct/lines/cached", new { Oids = new[] { "12x" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LinesCached_TooManyOids_Returns400()
    {
        var oids = Enumerable.Range(1, BrowserCourseAssembler.MaxOidsPerLookup + 1).Select(i => i.ToString()).ToArray();
        var response = await Client().PostAsJsonAsync("/api/chessable/direct/lines/cached", new { Oids = oids });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LinesCached_WithoutServiceKey_Returns401()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/chessable/direct/lines/cached", new { Oids = new[] { "1" } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
