using piratechess_lib;

namespace PirateChess.Api.Tests;

// Regression: bid 116242 — eine leere (nach 10 Retries als "" gecachte) Linie ließ die
// PGN-Generierung aus dem Cache (GetCourse useLocalData) mit einer JsonException
// ("input does not contain any JSON tokens") crashen → ganzer Kurs-Import scheiterte.
public class PirateChessLibTests
{
    private static RestResponseCourse OneChapterCourse(string chapterJson, params string[] lineContents)
    {
        var course = new RestResponseCourse { CourseJsonContent = "{\"course\":{\"data\":[{\"id\":1}]}}" };
        var ch = new RestResponseChapter { ChapterJsonContent = chapterJson };
        foreach (var lc in lineContents)
            ch.ResponseLineList.Add(new RestResponseLine { LineJsonContent = lc });
        course.ChapterList.Add(ch);
        return course;
    }

    [Fact]
    public void GetCourse_EmptyCachedLine_SkippedNotThrow()
    {
        var course = OneChapterCourse(
            "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"L1\"}]}}",
            ""); // vergiftete Linie
        var lib = new PirateChessLib { restResponseCourse = course };

        var ex = Record.Exception(() => lib.GetCourse("1", useLocalData: true));

        Assert.Null(ex);                 // vorher: JsonException
        Assert.Equal(1, lib.ErrorCount); // leere Linie übersprungen statt Crash
    }

    [Fact]
    public void GetCourse_EmptyCachedChapter_SkippedNotThrow()
    {
        var course = OneChapterCourse(""); // vergiftetes Kapitel, keine Linien
        var lib = new PirateChessLib { restResponseCourse = course };

        var ex = Record.Exception(() => lib.GetCourse("1", useLocalData: true));

        Assert.Null(ex);
        Assert.Equal(1, lib.ErrorCount);
    }

    // Regression (prod): ein mitten im Stream abgebrochener Kapitel-Abruf wurde nicht-leer,
    // aber unvollständig gecacht (~8 KB-Truncation durch den VPN-Proxy). Der Body rutschte an
    // der "leer/{}"-Prüfung vorbei und ließ JsonSerializer crashen
    // ("Expected start of a property name or value, but instead reached end of data.
    //  Path: $.list.data[9] ... BytePositionInLine: 8191") → ganzer Kurs-Import scheiterte.
    [Fact]
    public void GetCourse_TruncatedCachedChapter_SkippedNotThrow()
    {
        // Gültiger JSON-Anfang, der mitten im data-Array abbricht.
        var truncated = "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"L1\"},{\"id\":11,\"na";
        var course = OneChapterCourse(truncated);
        var lib = new PirateChessLib { restResponseCourse = course };

        var ex = Record.Exception(() => lib.GetCourse("1", useLocalData: true));

        Assert.Null(ex);                 // vorher: JsonException, Import-Abbruch
        Assert.Equal(1, lib.ErrorCount); // korruptes Kapitel übersprungen
    }

    [Fact]
    public void GetCourse_TruncatedCachedLine_SkippedNotThrow()
    {
        var course = OneChapterCourse(
            "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"L1\"}]}}",
            "{\"game\":{\"initial\":\"\",\"moves\":[{\"san\":\"e4\""); // mitten im Zug abgeschnitten
        var lib = new PirateChessLib { restResponseCourse = course };

        var ex = Record.Exception(() => lib.GetCourse("1", useLocalData: true));

        Assert.Null(ex);
        Assert.Equal(1, lib.ErrorCount); // korrupte Linie übersprungen statt Crash
    }

    // Regression (prod, bid 282212): Chessable lieferte in "draws" einen null-Eintrag; die
    // .Where(x => x.Object == ...)-Lambda dereferenzierte ihn → NullReferenceException in
    // GeneratePGN ließ den ganzen Kurs-Abruf scheitern.
    [Fact]
    public void GeneratePGN_NullDrawEntry_IgnoredNotThrow()
    {
        var game = new Game
        {
            Data =
            [
                new JsonMove
                {
                    Id = 1, Move = 1, San = "e4",
                    Draws = [ null!, new JsonDraw { Object = "arrow", Color = "green", Start = "e2", End = "e4" } ],
                },
            ],
        };

        var pgn = game.GeneratePGN(noTrainingMove: true);

        Assert.Contains("e4", pgn);
        Assert.Contains("%cal", pgn);   // der gültige Pfeil bleibt erhalten
    }

    [Fact]
    public void GeneratePGN_NullDrawsList_IgnoredNotThrow()
    {
        var game = new Game
        {
            Data = [ new JsonMove { Id = 1, Move = 1, San = "e4", Draws = null! } ],
        };

        var ex = Record.Exception(() => game.GeneratePGN(noTrainingMove: true));

        Assert.Null(ex);
    }
}
