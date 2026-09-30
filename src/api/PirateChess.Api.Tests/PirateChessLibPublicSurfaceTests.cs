using piratechess_lib;

namespace PirateChess.Api.Tests;

/// <summary>
/// Die Lib wird von Hand in die Desktop-App (kahalm/piratechess) portiert. Die Zerlegung von Models.cs
/// (Review 2026-09-29, S2-016) verschiebt Klassen in eigene Dateien, darf aber keinen öffentlichen Typ
/// umbenennen, verstecken oder in einen anderen Namespace legen: sonst bricht der Port und jeder Aufrufer.
/// Neue öffentliche Typen gehören bewusst in diese Liste.
/// </summary>
public class PirateChessLibPublicSurfaceTests
{
    private static readonly string[] ExpectedPublicTypes =
    [
        "piratechess_lib.Chapter",
        "piratechess_lib.ChapterVariation",
        "piratechess_lib.Course",
        "piratechess_lib.Game",
        "piratechess_lib.JsonBook",
        "piratechess_lib.JsonDraw",
        "piratechess_lib.JsonHomeData",
        "piratechess_lib.JsonMove",
        "piratechess_lib.JsonMoveItem",
        "piratechess_lib.JsonMoveItemList",
        "piratechess_lib.JwtHelper",
        "piratechess_lib.Line",
        "piratechess_lib.PirateChessLib",
        "piratechess_lib.ResponseChapter",
        "piratechess_lib.ResponseChapterList",
        "piratechess_lib.ResponseCourse",
        "piratechess_lib.ResponseLine",
        "piratechess_lib.ResponseList",
        "piratechess_lib.ResponseLogin",
        "piratechess_lib.ResponseMove",
        "piratechess_lib.RestResponseChapter",
        "piratechess_lib.RestResponseCourse",
        "piratechess_lib.RestResponseLine",
        "piratechess_lib.SoftFailEntry",
    ];

    [Fact]
    public void PublicTypes_CompleteAndInLibNamespace()
    {
        var actual = typeof(Game).Assembly.GetExportedTypes()
            .Select(t => t.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedPublicTypes.Order(StringComparer.Ordinal).ToArray(), actual);
    }
}
