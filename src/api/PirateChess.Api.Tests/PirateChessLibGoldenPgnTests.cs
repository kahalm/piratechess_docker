using System.Runtime.CompilerServices;
using System.Text.Json;
using piratechess_lib;

namespace PirateChess.Api.Tests;

/// <summary>
/// Golden-Test der PGN-Erzeugung (Zerlegung Z48): ein Kurs mit zwei Kapiteln und Linien, die die heiklen Fälle
/// tragen — Chessable-Exportform mit Verweis-Varianten (1.e4 e6 … 3.e5), mehrdeutiger Springerzug in einer
/// Autoren-Variante (v1.0.47: eindeutig auflösbar / nicht auflösbar / ausdrücklich), softFail-Alternativen,
/// Pfeile/Kreise, Key-Züge, Null-Zug am Ende, Linie mit Schwarz am Zug und Info-Linie. Die Ausgabe aller drei
/// Trainingsmodi muss byte-gleich zu <c>Golden/pgn-*.txt</c> bleiben (mit <c>PGN_GOLDEN_UPDATE=1</c> neu schreiben —
/// nur bei gewollter Formatänderung).
/// </summary>
public class PirateChessLibGoldenPgnTests
{
    private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string KidBefore16 = "r2q2k1/pp2rpb1/2np2pp/2N5/2P5/2NQ2Pb/PP2PP1P/R1BR2K1 w - - 3 16";

    private static string Json(object o) => JsonSerializer.Serialize(o);

    private static object V(params (string key, string val)[] items)
        => new { key = "V", val = items.Select(i => new { key = i.key, state = "", val = i.val }).ToArray() };

    private static object Move(int id, int move, string col, string san, bool isKey = false, string before = "", string after = "",
        object[]? draws = null)
        => new { id, move, col, san, isKey, before, after, draws = draws ?? [] };

    private static string Line(string initial, string color, int isInfo, object[] moves, object? softFail = null)
        => Json(new { game = new { initial, color, isInfo, data = moves, softFail } });

    private static string ExportFormLine()
    {
        const string fenBeforeE5 = "rnbqkbnr/ppp2ppp/4p3/3p4/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq d6 0 3";
        var introBefore = Json(new
        {
            before = StartFen, after = "",
            data = new object[] { V(("C", "willkommen zum ersten Kapitel. <br/><br/> Es gibt nichts, wenn er @@StartFEN@@x@@EndFEN@@"),
                ("S", "2.d4"), ("C", "spielen kann. Alles außer <strong>2.d4</strong>.")) },
        });
        var afterE5 = Json(new
        {
            before = fenBeforeE5, after = "",
            data = new object[]
            {
                V(("S", "3.Nc3"), ("S", "Nf6"), ("C", "werden wir <a class=\"commentMoveSmall\" href=\"/course/128648/3\">später</a> sehen.")),
                V(("S", "3.d3"), ("C", "ist <a href=\"/variation/1\">hier</a> in der Zugfolge @@StartFEN@@x@@EndFEN@@"),
                    ("S", "2.d3"), ("S", "d5"), ("S", "3.Nf3"), ("C", "analysiert.")),
            },
        });
        return Line(StartFen, "white", 0,
        [
            Move(0, 1, "w", "e4", isKey: true, before: introBefore),
            Move(1, 1, "b", "e6"),
            Move(2, 2, "w", "Nf3", isKey: true, draws: [ new { @object = "arrow", start = "f3", end = "e5", color = "g", move = "", index = "" } ]),
            Move(3, 2, "b", "d5"),
            Move(4, 3, "w", "e5", isKey: true, after: afterE5),
            Move(5, 3, "b", "c5"),
            Move(6, 4, "w", "b4", isKey: true, after: Json(new { data = new object[] { new { key = "C", val = "Flügelgambit" } } })),
        ], softFail: new object[] { new { W = (string[]?)null, B = new[] { "e6", "e5", "c5" } } });
    }

    private static string KidLine()
    {
        string After(params (string, string)[] items) => Json(new { before = KidBefore16, after = "", data = new object[] { V(items) } });
        return Line(KidBefore16, "white", 0,
        [
            Move(0, 16, "w", "Qxd6", isKey: true, after: After(("S", "16.Ne4"), ("S", "Bf5"), ("S", "17.Nxb7"))),
            Move(1, 16, "b", "Qxd6", after: Json(new
            {
                before = "", after = "",
                data = new object[]
                {
                    V(("S", "16.Ne4"), ("C", "is a better try, although we have full compensation after Bf5.")),
                    V(("S", "16.N5e4"), ("S", "Bf5")),
                },
            })),
            Move(2, 17, "w", "Rxd6", isKey: true, draws: [ new { @object = "circle", start = "d6", end = "", color = "r", move = "", index = "" } ]),
        ]);
    }

    private static string BlackStartLine()
        => Line("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1", "black", 0,
        [
            Move(0, 1, "b", "c5", isKey: true),
            Move(1, 2, "w", "Nf3"),
            Move(2, 2, "b", "d6", isKey: true),
            Move(3, 3, "w", "--", after: Json(new { data = new object[] { new { key = "C", val = "Weiter im nächsten Kapitel." } } })),
        ]);

    private static string InfoLine()
        => Line(StartFen, "white", 1, [ Move(0, 1, "w", "d4", isKey: true), Move(1, 1, "b", "Nf6") ]);

    private static RestResponseCourse Course()
    {
        var course = new RestResponseCourse { CourseJsonContent = "{\"course\":{\"data\":[{\"id\":1},{\"id\":2}]}}" };
        var ch1 = new RestResponseChapter
        {
            ChapterJsonContent = "{\"list\":{\"name\":\"Französisch \\\"Vorstoß\\\"\",\"title\":\"Golden\",\"data\":[{\"id\":20733162,\"name\":\"3.e5 c5\"},{\"id\":4711,\"name\":\"KID 16.Dxd6\"}]}}",
        };
        ch1.ResponseLineList.Add(new RestResponseLine { Oid = 20733162, LineJsonContent = ExportFormLine() });
        ch1.ResponseLineList.Add(new RestResponseLine { Oid = 4711, LineJsonContent = KidLine() });
        var ch2 = new RestResponseChapter
        {
            ChapterJsonContent = "{\"list\":{\"name\":\"Sizilianisch\",\"title\":\"Golden\",\"data\":[{\"id\":815,\"name\":\"1...c5\"},{\"id\":816,\"name\":\"Info\"}]}}",
        };
        ch2.ResponseLineList.Add(new RestResponseLine { Oid = 815, LineJsonContent = BlackStartLine() });
        ch2.ResponseLineList.Add(new RestResponseLine { Oid = 816, LineJsonContent = InfoLine() });
        course.ChapterList.Add(ch1);
        course.ChapterList.Add(ch2);
        return course;
    }

    private static string GoldenPath(string mode, [CallerFilePath] string here = "")
        => Path.Combine(Path.GetDirectoryName(here)!, "Golden", $"pgn-{mode}.txt");

    [Theory]
    [InlineData("first-key-move", false, false)]
    [InlineData("all-key-moves", true, false)]
    [InlineData("none", false, true)]
    public void Course_PgnIsByteIdenticalToGolden(string mode, bool allKeyMoves, bool noTrainingMove)
    {
        var lib = new PirateChessLib { restResponseCourse = Course(), AllKeyMovesTraining = allKeyMoves, NoTrainingMove = noTrainingMove };
        var (pgn, name) = lib.GetCourse("424711", useLocalData: true);
        var actual = $"name={name}\nerrors={lib.ErrorCount}\n{pgn}";

        var path = GoldenPath(mode);
        if (Environment.GetEnvironmentVariable("PGN_GOLDEN_UPDATE") == "1")
            File.WriteAllText(path, actual);

        Assert.Equal(0, lib.ErrorCount);
        Assert.Equal(File.ReadAllText(path), actual);
    }

    [Fact]
    public void Golden_CoversTheTrickyCases()
    {
        var none = File.ReadAllText(GoldenPath("none"));
        Assert.Contains("(16.N3e4 Bf5 17.Nxb7)", none);      // mehrdeutig, durch Fortsetzung eindeutig (v1.0.47)
        Assert.Contains("(16.N5e4 Bf5)", none);               // ausdrücklich eindeutig
        Assert.DoesNotContain("(16.Ne4", none);               // nicht auflösbar → Kommentar
        Assert.Contains("3. e5 (3.Nc3 Nf6 {werden wir später sehen.})", none);
        Assert.Contains("[%alt e5 c5]", none);
        Assert.Contains("1... c5", none);
        Assert.Contains("[%tqu", File.ReadAllText(GoldenPath("first-key-move")));
    }
}
