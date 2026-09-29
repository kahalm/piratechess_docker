using piratechess_lib;
using PirateChess.Api.Services;

namespace PirateChess.Api.Tests;

/// <summary>
/// Die eine Prüfregel für Chessable-Inhalte (<see cref="ChessableContent"/>) und ihr Spiegel an allen Prüfstellen:
/// Linien-Cache, Browser-Cache und Kurs-Cache müssen für denselben Inhalt dasselbe sagen. Vorher galten
/// <c>{"x":1}</c> und ein Fehlerkörper im Kurs-Cache als verwertbar, im Linien-Cache als ungültig.
/// </summary>
public class ChessableContentTests
{
    private const string ValidLine = "{\"game\":{}}";
    private const string ValidChapter = "{\"list\":{\"data\":[]}}";

    public static TheoryData<string, bool> Lines => new()
    {
        { "", false },
        { "   ", false },
        { "{}", false },
        { "null", false },
        { "{\"game\":{\"data\":[", false },                                  // abgeschnitten
        { "{\"x\":1}", false },                                               // kein game-Objekt
        { "{\"error\":{\"message\":\"Expired token\"}}", false },              // Fehlerkörper (HTTP 200)
        { "{\"error\":{\"message\":\"User is banned or deleted\"}}", false },  // Prod 30.06.
        { ValidLine, true },
        { "{\"Game\":{\"data\":[]}}", true },
    };

    [Theory]
    [MemberData(nameof(Lines))]
    public void LineReason_IsMirroredByEveryCheck(string content, bool usable)
    {
        Assert.Equal(usable, ChessableContent.LineReason(content) is null);
        Assert.Equal(usable, RawLineCache.InvalidReason(content) is null);
        Assert.Equal(usable, BrowserCourseAssembler.IsCacheableLine(content));
        if (!ChessableContent.IsEmpty(content))
            // Leere Linien sind im Kurs eine tolerierte Lücke; jeder andere Inhalt muss taugen.
            Assert.Equal(usable, RawCourseCache.IsComplete(Course(ValidChapter, ValidLine, ValidLine, content)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    public void EmptyLine_IsAToleratedGapInTheCourse(string content)
    {
        Assert.Equal("leer", ChessableContent.LineReason(content));
        Assert.True(RawCourseCache.IsComplete(Course(ValidChapter, ValidLine, ValidLine, content)));
    }

    [Fact]
    public void LineReason_NamesTheChessableErrorMessage()
    {
        Assert.Contains("User is banned or deleted",
            ChessableContent.LineReason("{\"error\":{\"message\":\"User is banned or deleted\"}}"));
        Assert.Contains("Expired token", ChessableContent.LineReason("{\"error\":{\"message\":\"Expired token\"}}"));
        Assert.Equal("kein game-Objekt", ChessableContent.LineReason("{\"x\":1}"));
        Assert.StartsWith("JSON:", ChessableContent.LineReason("{\"game\":{\"data\":["));
    }

    public static TheoryData<string, bool> Chapters => new()
    {
        { "", false },
        { "{}", false },
        { "{\"list\":{\"data\":[{\"id\":1},{\"id", false },                                        // abgeschnitten
        { "{\"error\":{\"message\":\"Expired token\"}}", false },                                     // Fehlerkörper
        { "{\"error\":{\"userFriendlyErrorMessage\":\"You do not own this course.\",\"message\":\"\"}}", false },
        { ValidChapter, true },                                                                        // legitim leer
        { "{\"list\":{\"name\":\"Ch1\",\"data\":[{\"id\":10,\"name\":\"L1\"}]}}", true },
    };

    [Theory]
    [MemberData(nameof(Chapters))]
    public void ChapterReason_IsMirroredByTheCourseCache(string content, bool usable)
    {
        Assert.Equal(usable, ChessableContent.ChapterReason(content) is null);
        Assert.Equal(usable, RawCourseCache.IsComplete(Course(content, ValidLine)));
    }

    private static RestResponseCourse Course(string chapterJson, params string[] lines)
    {
        var course = new RestResponseCourse { CourseJsonContent = "{}" };
        var chapter = new RestResponseChapter { ChapterJsonContent = chapterJson };
        for (var i = 0; i < lines.Length; i++)
            chapter.ResponseLineList.Add(new RestResponseLine { Oid = i + 1, LineJsonContent = lines[i] });
        course.ChapterList.Add(chapter);
        return course;
    }
}
