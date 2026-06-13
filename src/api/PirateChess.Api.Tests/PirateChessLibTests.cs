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
}
