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

    // Wie eine echte getGame-Antwort: sie nennt selbst ihre oid und ihren Kurs (game.oid, game.bid). Nur solche
    // Linien dürfen in den geteilten Cache.
    private static string GameJson(string san, int oid, string bid)
        => "{\"game\":{\"oid\":" + oid + ",\"bid\":" + bid + ",\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"col\":\"w\",\"san\":\"" + san + "\"}]}}";

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

    private async Task SeedLineAsync(int oid, string json, string? bid = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CachedRawLines.Add(new CachedRawLine { Oid = oid, LineJsonContent = GzipText.Compress(json), CachedAt = DateTime.UtcNow, Bid = bid });
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
            Chapters = new[] { Chapter(ChapterJson((3301, "A")), new[] { GameJson("e4", 3301, "3300") }, new[] { "3301" }) }
        });
        Assert.Equal(GameJson("e4", 3301, "3300"), await CachedLineAsync(3301));
        var row = await CachedRowAsync(3301);
        Assert.Equal("3300", row!.Bid);          // gehört zu genau diesem Kurs
        Assert.True(row.FromBrowser);            // vom Client geschickt, nicht bestätigt
    }

    [Fact]
    public async Task Parse_LineClaimedUnderAForeignOid_IsUsedForTheImport_ButNotShared()
    {
        // A3-002: der Client behauptet oid 5001, die getGame-Antwort selbst nennt oid 99. Vorher landete der Inhalt
        // unter 5001 im geteilten Cache, und jeder spätere Import dieser Linie hätte ihn übernommen.
        var body = await ParseOkAsync(new
        {
            Bid = "5000", Mode = "None",
            Chapters = new[] { Chapter(ChapterJson((5001, "A")), new[] { GameJson("e4", 99, "5000") }, new[] { "5001" }) }
        });
        Assert.Contains("[ChessableOid \"5001\"]", body.Pgn);   // der Einsender bekommt, was er geschickt hat
        Assert.Null(await CachedRowAsync(5001));
    }

    [Fact]
    public async Task Parse_LineOfAnotherCourse_OrWithoutOwnIds_IsNotShared()
    {
        await ParseOkAsync(new
        {
            Bid = "5100", Mode = "None",
            Chapters = new[] { Chapter(ChapterJson((5101, "A"), (5102, "B"), (5103, "C")),
                new[] { GameJson("e4", 5101, "9999"), LineJson("d4"), GameJson("c4", 5103, "5100") },
                new[] { "5101", "5102", "5103" }) }
        });
        Assert.Null(await CachedRowAsync(5101));      // nennt einen anderen Kurs
        Assert.Null(await CachedRowAsync(5102));      // nennt weder oid noch Kurs
        Assert.NotNull(await CachedRowAsync(5103));
    }

    [Fact]
    public async Task Parse_FillsOnlyFromLinesOfTheSameCourse_OrLegacyLinesWithoutCourse()
    {
        // Begleitteil A3-001: eine oid ohne Inhalt wird nur aus einer Zeile desselben Kurses gefüllt.
        await SeedLineAsync(5201, LineJson("e4"), bid: "5200");
        await SeedLineAsync(5202, LineJson("d4"));   // Altbestand ohne bid
        object Payload(string bid) => new
        {
            Bid = bid, Mode = "None",
            Chapters = new[] { Chapter(ChapterJson((5201, "A"), (5202, "B")), new string?[] { null, null }, new[] { "5201", "5202" }) }
        };

        var foreign = await ParseOkAsync(Payload("5300"));
        Assert.DoesNotContain("[ChessableOid \"5201\"]", foreign.Pgn);
        Assert.Contains("[ChessableOid \"5202\"]", foreign.Pgn);

        var own = await ParseOkAsync(Payload("5200"));
        Assert.Contains("[ChessableOid \"5201\"]", own.Pgn);
        Assert.Contains("[ChessableOid \"5202\"]", own.Pgn);
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
            Chapters = new[] { Chapter(ChapterJson((3501, "A"), (3502, "B")), new[] { "{\"x\":1}", GameJson("e4", 3502, "3500") }, new[] { "3501", "3502" }) }
        });
        Assert.Null(await CachedLineAsync(3501));
        Assert.NotNull(await CachedLineAsync(3502));
    }

    private async Task<List<string>> CachedOidsAsync(params string[] oids)
    {
        var response = await Client().PostAsJsonAsync("/api/chessable/direct/lines/cached", new { Oids = oids });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return body.GetProperty("oids").EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    private Task<CachedRawLine?> CachedRowAsync(int oid)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return Task.FromResult(db.CachedRawLines.FirstOrDefault(c => c.Oid == oid));
    }

    [Fact]
    public async Task Parse_TruncatedCachedLine_IsMarked_NoLongerReportedCached_AndNotHealedByBrowserContent()
    {
        // Regression 2026-09-15 (Kurs 207313, oid 36114125): die Linie lag seit Juni abgeschnitten im Cache. Die
        // Extension hielt sie für gecacht, schickte nur die oid, und der Parser übersprang sie — bei jedem Import, still.
        const string truncated =
            "{\"game\":{\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"col\":\"w\",\"san\":\"e4\",\"ann1\":\"Developing the kni";
        await SeedLineAsync(3951, truncated);
        Assert.Contains("3951", await CachedOidsAsync("3951"));   // noch unentdeckt: gilt als gecacht

        var first = await ParseOkAsync(new
        {
            Bid = "3950", Mode = "None",
            Chapters = new[] { Chapter(ChapterJson((3951, "Dubov Italian")), new string?[] { null }, new[] { "3951" }) }
        });
        Assert.DoesNotContain("3951", first.Pgn);
        var marked = await CachedRowAsync(3951);
        Assert.NotNull(marked!.InvalidAt);
        Assert.Equal(truncated, GzipText.Decompress(marked.LineJsonContent));   // markiert, nicht gelöscht
        Assert.DoesNotContain("3951", await CachedOidsAsync("3951"));           // → die Extension holt sie selbst

        // A3-002: eine markierte Linie ersetzt nur ein eigener Server-Abruf, nie der Inhalt eines Clients — sonst
        // könnte jeder eine markierte Linie mit erfundenem Inhalt für alle „heilen".
        var second = await ParseOkAsync(new
        {
            Bid = "3950", Mode = "None",
            Chapters = new[] { Chapter(ChapterJson((3951, "Dubov Italian")), new[] { GameJson("e4", 3951, "3950") }, new[] { "3951" }) }
        });
        Assert.Contains("[ChessableOid \"3951\"]", second.Pgn);                // der Einsender bekommt seine Linie
        var stillMarked = await CachedRowAsync(3951);
        Assert.NotNull(stillMarked!.InvalidAt);
        Assert.Equal(truncated, GzipText.Decompress(stillMarked.LineJsonContent));
        Assert.DoesNotContain("3951", await CachedOidsAsync("3951"));

        using var scope = _factory.Services.CreateScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<AppDbContext>().CachedRawLineArchive.Any(a => a.Oid == 3951));
    }

    [Fact]
    public async Task RevalidateLines_DryRunByDefault_ApplyReleasesARepairedLine()
    {
        await SeedLineAsync(3961, LineJson("d4"));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = db.CachedRawLines.Single(c => c.Oid == 3961);
            row.InvalidAt = DateTime.UtcNow;
            row.InvalidReason = "alte Prüfung";
            await db.SaveChangesAsync();
        }

        var dry = await Client().PostAsync("/api/chessable/direct/lines/revalidate", null);
        Assert.Equal(HttpStatusCode.OK, dry.StatusCode);
        var dryBody = await dry.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.False(dryBody.GetProperty("applied").GetBoolean());
        Assert.Contains("3961", dryBody.GetProperty("clearedOids").EnumerateArray().Select(e => e.GetString()));
        Assert.NotNull((await CachedRowAsync(3961))!.InvalidAt);

        var apply = await Client().PostAsync("/api/chessable/direct/lines/revalidate?dryRun=false", null);
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
        Assert.Null((await CachedRowAsync(3961))!.InvalidAt);
    }

    private const string OneChapterCourse = "{\"course\":{\"data\":[{\"id\":1}]}}";

    [Fact]
    public async Task Parse_CompleteCourse_BecomesCourseCache()
    {
        Assert.False(await CourseCachedAsync("3600"));
        await ParseOkAsync(new
        {
            Bid = "3600", Mode = "None", CourseJson = OneChapterCourse, Complete = true,
            Chapters = new[] { Chapter(ChapterJson((3601, "A"), (3602, "B")), new[] { GameJson("e4", 3601, "3600"), GameJson("d4", 3602, "3600") }, new[] { "3601", "3602" }) }
        });
        Assert.True(await CourseCachedAsync("3600"));
    }

    [Fact]
    public async Task Parse_CompleteButLinesNotSharable_WritesNoCourseCache()
    {
        // A3-002: EIN Kapitel, Complete und ein courseJson mit einem Kapitel erfüllten die Kapitelzahl-Prüfung —
        // der ganze Kurs-Cache dieses bids entstand aus Client-Inhalt, den kein Linien-Check gesehen hatte.
        await ParseOkAsync(new
        {
            Bid = "5400", Mode = "None", CourseJson = OneChapterCourse, Complete = true,
            Chapters = new[] { Chapter(ChapterJson((5401, "A"), (5402, "B")), new[] { LineJson("e4"), GameJson("d4", 5402, "5400") }, new[] { "5401", "5402" }) }
        });
        Assert.False(await CourseCachedAsync("5400"));
        Assert.Null(await CachedRowAsync(5401));
    }

    [Fact]
    public async Task Parse_CompleteButALineBelongsToAnotherCourse_WritesNoCourseCache()
    {
        await SeedLineAsync(5501, LineJson("c4"), bid: "7777");
        await ParseOkAsync(new
        {
            Bid = "5500", Mode = "None", CourseJson = OneChapterCourse, Complete = true,
            Chapters = new[] { Chapter(ChapterJson((5501, "A"), (5502, "B")), new[] { GameJson("e4", 5501, "5500"), GameJson("d4", 5502, "5500") }, new[] { "5501", "5502" }) }
        });
        Assert.False(await CourseCachedAsync("5500"));
        Assert.Equal("7777", (await CachedRowAsync(5501))!.Bid);   // vorhandene Zeile unberührt
    }

    [Fact]
    public async Task Parse_NotMarkedComplete_WritesNoCourseCache()
    {
        await ParseOkAsync(new
        {
            Bid = "3700", Mode = "None", CourseJson = OneChapterCourse, Complete = false,
            Chapters = new[] { Chapter(ChapterJson((3701, "A"), (3702, "B")), new[] { GameJson("e4", 3701, "3700"), GameJson("d4", 3702, "3700") }, new[] { "3701", "3702" }) }
        });
        Assert.False(await CourseCachedAsync("3700"));
    }

    [Fact]
    public async Task Parse_CompleteButALineHasNoContent_WritesNoCourseCache()
    {
        await ParseOkAsync(new
        {
            Bid = "3800", Mode = "None", CourseJson = OneChapterCourse, Complete = true,
            Chapters = new[] { Chapter(ChapterJson((3801, "A"), (3802, "B")), new[] { GameJson("e4", 3801, "3800"), null }, new[] { "3801", "3802" }) }
        });
        Assert.False(await CourseCachedAsync("3800"));
    }

    [Fact]
    public async Task Parse_CompleteButChapterCountDiffers_WritesNoCourseCache()
    {
        await ParseOkAsync(new
        {
            Bid = "3900", Mode = "None", CourseJson = "{\"course\":{\"data\":[{\"id\":1},{\"id\":2}]}}", Complete = true,
            Chapters = new[] { Chapter(ChapterJson((3901, "A"), (3902, "B")), new[] { GameJson("e4", 3901, "3900"), GameJson("d4", 3902, "3900") }, new[] { "3901", "3902" }) }
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
    public async Task LinesCached_WithBid_ReportsOnlyLinesThatFillThisCourse()
    {
        await SeedLineAsync(5601, LineJson("e4"), bid: "5600");
        await SeedLineAsync(5602, LineJson("d4"), bid: "5700");
        await SeedLineAsync(5603, LineJson("c4"));   // Altbestand ohne bid
        var oids = new[] { "5601", "5602", "5603" };

        var scoped = await Client().PostAsJsonAsync("/api/chessable/direct/lines/cached", new { Oids = oids, Bid = "5600" });
        Assert.Equal(HttpStatusCode.OK, scoped.StatusCode);
        Assert.Equal(new[] { "5601", "5603" }, (await scoped.Content.ReadFromJsonAsync<CachedLinesResp>(JsonOpts))!.Oids.Order());

        Assert.Equal(oids, (await CachedOidsAsync(oids)).Order());   // ohne Bid wie bisher
    }

    [Fact]
    public async Task LinesCached_InvalidBid_Returns400()
    {
        var response = await Client().PostAsJsonAsync("/api/chessable/direct/lines/cached", new { Oids = new[] { "1" }, Bid = "12x" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
