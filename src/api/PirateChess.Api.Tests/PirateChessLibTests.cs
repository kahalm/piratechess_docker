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

    // Tracing: eine übersprungene Linie darf nicht mehr spurlos verschwinden — Kontext + voller
    // Stacktrace müssen in ErrorDetails landen UND den Diag-Event feuern, damit der Aufrufer sie
    // nach Elasticsearch loggt.
    [Fact]
    public void GetCourse_SkippedLine_CapturesDiagnosticDetail()
    {
        var course = OneChapterCourse(
            "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"L1\"}]}}",
            "{\"game\":{\"initial\":\"\",\"moves\":[{\"san\":\"e4\""); // korrupt
        var lib = new PirateChessLib { restResponseCourse = course };
        var events = new List<string>();
        lib.SetErrorDiagEvent(events.Add);

        lib.GetCourse("1", useLocalData: true);

        Assert.Equal(1, lib.ErrorCount);
        Assert.Single(lib.ErrorDetails);
        Assert.Single(events);
        Assert.Contains("Linien-JSON übersprungen", lib.ErrorDetails[0]);
        Assert.Contains("JsonException", lib.ErrorDetails[0]); // Exceptiontyp + Stacktrace mitgeschrieben
        Assert.Contains("at ", lib.ErrorDetails[0]);           // Stacktrace-Zeile vorhanden
    }

    // ErrorDetails werden bei jedem GetCourse-Lauf zurückgesetzt (kein Leck über Läufe hinweg).
    [Fact]
    public void GetCourse_CleanCourse_NoDiagnosticDetail()
    {
        var course = OneChapterCourse(
            "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"L1\"}]}}",
            "{\"game\":{\"initial\":\"\",\"moves\":[{\"id\":0,\"move\":1,\"san\":\"e4\"}]}}");
        var lib = new PirateChessLib { restResponseCourse = course };

        lib.GetCourse("1", useLocalData: true);

        Assert.Equal(0, lib.ErrorCount);
        Assert.Empty(lib.ErrorDetails);
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

    // Regression: korrupte Chessable-Daten mit doppelter Move-Id ließen SortedList.Add eine
    // ArgumentException werfen ("Index/Key"-Fehler) → ganzer Kurs-Abruf „failed". Der Indexer
    // überschreibt jetzt tolerant statt zu werfen.
    [Fact]
    public void GeneratePGN_DuplicateMoveIds_DoesNotThrow()
    {
        var game = new Game
        {
            Data =
            [
                new JsonMove { Id = 1, Move = 1, San = "e4" },
                new JsonMove { Id = 1, Move = 1, San = "d4" },   // dieselbe Id → früher Crash
            ],
        };

        var pgn = game.GeneratePGN(noTrainingMove: true);
        Assert.False(string.IsNullOrWhiteSpace(pgn));
        Assert.Equal(1, game.DuplicateMoveIds); // Kollision wird gezählt, nicht verschluckt
    }

    // Der tolerante Overwrite bei doppelten Move-Ids darf den Zugverlust nicht verstecken: die Linie
    // bleibt im Export, aber ErrorCount/ErrorDetails/Diag-Event melden die Korruption (sonst sähe ein
    // Kurs mit stillschweigend fehlenden Zügen wie ein sauberer Export aus).
    [Fact]
    public void GetCourse_DuplicateMoveIds_LineKeptButReported()
    {
        var course = OneChapterCourse(
            "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"L1\"}]}}",
            "{\"game\":{\"initial\":\"\",\"data\":[{\"id\":1,\"move\":1,\"san\":\"e4\"},{\"id\":1,\"move\":1,\"san\":\"d4\"}]}}");
        var lib = new PirateChessLib { restResponseCourse = course };
        var events = new List<string>();
        lib.SetErrorDiagEvent(events.Add);

        var (pgn, _) = lib.GetCourse("1", useLocalData: true);

        Assert.Contains("d4", pgn);                                   // Linie bleibt im Export (letzter gewinnt)
        Assert.Equal(1, lib.ErrorCount);
        Assert.Single(events);
        Assert.Contains("Doppelte Move-Ids", lib.ErrorDetails[0]);
    }

    // Regression für den Skip-Pfad um GeneratePGN in GetLine: wirft GeneratePGN (hier: korruptes
    // move.after-JSON → JsonException beim Deserialize<ResponseMove>), wird NUR diese Linie
    // übersprungen und via RecordError/Diag gemeldet — der Kurs-Export läuft weiter.
    [Fact]
    public void GetCourse_GeneratePgnThrows_LineSkippedAndReported()
    {
        var course = OneChapterCourse(
            "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"L1\"},{\"id\":11,\"name\":\"L2\"}]}}",
            "{\"game\":{\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"san\":\"e4\",\"after\":\"{korrupt\"}]}}", // GeneratePGN wirft
            "{\"game\":{\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"san\":\"d4\"}]}}");                      // saubere Linie
        var lib = new PirateChessLib { restResponseCourse = course };
        var events = new List<string>();
        lib.SetErrorDiagEvent(events.Add);

        var (pgn, _) = lib.GetCourse("1", useLocalData: true);

        Assert.Equal(1, lib.ErrorCount);
        Assert.Single(events);
        Assert.Contains("GeneratePGN übersprungen", lib.ErrorDetails[0]);
        Assert.Contains("JsonException", lib.ErrorDetails[0]);
        Assert.DoesNotContain("e4", pgn);   // kaputte Linie übersprungen …
        Assert.Contains("d4", pgn);         // … die saubere bleibt erhalten
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

    // ---- Chessable-"V"-Varianten → gültiges PGN ----------------------------
    // Chessables "V"-Daten mischen echte (legale) Seitenlinien mit Transpositions-/Verweis-
    // Notizen (absolute Zugnummern ab Zug 1, Nullzüge "--"). Früher wurden alle blind als
    // (…) ausgegeben → ungültiges, nicht nachspielbares PGN. Jetzt: legal nachspielbar ab der
    // Elternstellung ⇒ echte (…)-Variante; sonst ⇒ {Kommentar} (PGN bleibt gültig).
    private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    private static string AfterWithV(string beforeFen, params (string key, string val)[] items)
    {
        var inner = string.Join(",", items.Select(it => $"{{\"key\":\"{it.key}\",\"state\":\"\",\"val\":\"{it.val}\"}}"));
        return $"{{\"before\":\"{beforeFen}\",\"after\":\"\",\"data\":[{{\"key\":\"V\",\"state\":\"\",\"val\":[{inner}]}}]}}";
    }

    private static string PgnForFirstMoveWithV(string after)
    {
        var game = new Game { Initial = "", Data = [ new JsonMove { Id = 0, Move = 1, San = "e4", After = after } ] };
        return game.GeneratePGN(noTrainingMove: true);
    }

    [Fact]
    public void GetVariationPgn_LegalSideline_RenderedAsPlayableVariation()
    {
        // 1.d4 d5 2.c4 ist ab der Grundstellung legal nachspielbar → echte Variante.
        var pgn = PgnForFirstMoveWithV(AfterWithV(StartFen, ("S", "1.d4"), ("S", "d5"), ("S", "2.c4")));
        Assert.Contains("(1.d4 d5 2.c4)", pgn);
    }

    [Fact]
    public void GetVariationPgn_LegalSidelineWithComment_KeepsCommentInsideVariation()
    {
        var pgn = PgnForFirstMoveWithV(AfterWithV(StartFen, ("S", "1.d4"), ("C", "Damengambit"), ("S", "d5")));
        Assert.Contains("(1.d4 {Damengambit} d5)", pgn);
    }

    [Fact]
    public void GetVariationPgn_IllegalTranspositionNote_RenderedAsCommentNotBrokenVariation()
    {
        // 3...Bb7 4.e3 setzt NICHT von der Grundstellung fort → darf keine (…)-Variante werden.
        var pgn = PgnForFirstMoveWithV(AfterWithV(StartFen, ("S", "3...Bb7"), ("S", "4.e3")));
        Assert.DoesNotContain("(3...Bb7", pgn);
        Assert.Contains("{3...Bb7 4.e3}", pgn);
    }

    [Fact]
    public void GetVariationPgn_NullMove_RenderedAsComment()
    {
        // Nullzug "--" ist nicht nachspielbar → Kommentar statt kaputter Variante.
        var pgn = PgnForFirstMoveWithV(AfterWithV(StartFen, ("S", "19...--"), ("S", "20.Rh8+")));
        Assert.DoesNotContain("(19", pgn);
        Assert.Contains("{19...-- 20.Rh8+}", pgn);
    }

    [Fact]
    public void GetVariationPgn_BareMoveNumberToken_DoesNotThrow()
    {
        // Regression: ein Varianten-Token, das nach StripMoveNumber nur eine leere/zu kurze SAN
        // ergibt ("12." -> ""), ließ SanToMove via s[^2] mit IndexOutOfRange den GANZEN Kurs-Abruf
        // scheitern. Jetzt: null -> als Kommentar gerendert, kein Throw.
        var ex = Record.Exception(() =>
        {
            var pgn = PgnForFirstMoveWithV(AfterWithV(StartFen, ("S", "12."), ("S", "Nf3")));
            Assert.DoesNotContain("(12.", pgn);   // keine kaputte Variante
        });
        Assert.Null(ex);
    }

    [Fact]
    public void GetVariationPgn_TwoAlternativesSameNode_RenderedAsSeparateVariations()
    {
        // Zwei Alternativen am selben Knoten (1.d4 / 1.c4) → zwei getrennte (…)-Varianten.
        var pgn = PgnForFirstMoveWithV(AfterWithV(StartFen, ("S", "1.d4"), ("S", "1.c4")));
        Assert.Contains("(1.d4)", pgn);
        Assert.Contains("(1.c4)", pgn);
    }

    // ---- softFail (geduldete Züge) → [%alt …] ------------------------------
    [Fact]
    public void GeneratePGN_SoftFail_EmittedAsAltAnnotationMinusMainMove()
    {
        // 1.e4 e6 mit softFail an Schwarz' 1. Zug: e6 (Hauptzug) + e5/c5 (geduldet).
        var game = new Game
        {
            Initial = "",
            Data =
            [
                new JsonMove { Id = 0, Move = 1, San = "e4", Col = "w" },
                new JsonMove { Id = 1, Move = 1, San = "e6", Col = "b" },
            ],
            SoftFail =
            [
                new SoftFailEntry { W = null, B = ["e6", "e5", "c5"] },
            ],
        };

        var pgn = game.GeneratePGN(noTrainingMove: true);

        // Der gespielte Hauptzug (e6) ist NICHT in der Alt-Liste, die geduldeten schon.
        Assert.Contains("[%alt e5 c5]", pgn);
    }

    [Fact]
    public void GeneratePGN_NoSoftFail_NoAltAnnotation()
    {
        var game = new Game
        {
            Initial = "",
            Data = [ new JsonMove { Id = 0, Move = 1, San = "e4", Col = "w" } ],
        };

        var pgn = game.GeneratePGN(noTrainingMove: true);

        Assert.DoesNotContain("%alt", pgn);
    }

    // ---- Info-/Erklärlinien (Chessable IsInfo) → [%info]-Marker ------------
    // Chessable markiert reine Erklär-/Infovarianten mit IsInfo=1. Diese sollen in rookhub NICHT
    // als Quiz abgefragt werden → piratechess emittiert einen [%info]-Marker (und kein [%tqu]).
    [Fact]
    public void GeneratePGN_IsInfo_EmitsInfoMarkerAndNoTraining()
    {
        var game = new Game
        {
            Initial = "",
            IsInfo = 1,
            Color = "white",
            Data =
            [
                new JsonMove { Id = 0, Move = 1, San = "e4", Col = "w", IsKey = true },
                new JsonMove { Id = 1, Move = 1, San = "e5", Col = "b", IsKey = true },
            ],
        };

        var pgn = game.GeneratePGN();

        Assert.Contains("[%info]", pgn);
        Assert.DoesNotContain("[%tqu", pgn);   // Info-Linie wird nie trainiert
        Assert.Contains("e4", pgn);            // Züge bleiben (zum Durchklicken)
    }

    [Fact]
    public void GeneratePGN_NotInfo_NoInfoMarker()
    {
        var game = new Game
        {
            Initial = "",
            Data = [ new JsonMove { Id = 0, Move = 1, San = "e4", Col = "w" } ],
        };

        var pgn = game.GeneratePGN(noTrainingMove: true);

        Assert.DoesNotContain("%info", pgn);
    }
}
