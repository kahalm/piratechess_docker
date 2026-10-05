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

    // Ratio-Guard: scheitern deutlich mehr Linien als ankommen (>10 UND mehr als exportiert), ist das
    // ein systematisches Parser-Problem — GetCourse wirft dann, statt still einen Rumpf-Kurs zu liefern.
    [Fact]
    public void GetCourse_SystematicLineFailure_ThrowsInsteadOfSilentTruncation()
    {
        var corrupt = Enumerable.Repeat("{\"game\":{\"initial\":\"\",\"data\":[{\"san\":\"e4\"", 12).ToArray();
        var lineIds = string.Join(",", Enumerable.Range(10, 13).Select(i => $"{{\"id\":{i},\"name\":\"L{i}\"}}"));
        var course = OneChapterCourse(
            $"{{\"list\":{{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{lineIds}]}}}}",
            [.. corrupt, "{\"game\":{\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"san\":\"d4\"}]}}"]);
        var lib = new PirateChessLib { restResponseCourse = course };

        Assert.Throws<InvalidOperationException>(() => lib.GetCourse("1", useLocalData: true));
    }

    // …aber vereinzelte korrupte Linien in einem überwiegend gesunden Kurs bleiben tolerierter Skip.
    [Fact]
    public void GetCourse_MinorityLineFailure_StillSucceeds()
    {
        var clean = Enumerable.Repeat("{\"game\":{\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"san\":\"d4\"}]}}", 12).ToArray();
        var lineIds = string.Join(",", Enumerable.Range(10, 13).Select(i => $"{{\"id\":{i},\"name\":\"L{i}\"}}"));
        var course = OneChapterCourse(
            $"{{\"list\":{{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{lineIds}]}}}}",
            [.. clean, "{\"game\":{\"initial\":\"\",\"data\":[{\"san\":\"e4\""]);
        var lib = new PirateChessLib { restResponseCourse = course };

        var ex = Record.Exception(() => lib.GetCourse("1", useLocalData: true));

        Assert.Null(ex);
        Assert.Equal(1, lib.ErrorCount);
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

    // ---- Solverfarbe im PGN-Kopf -------------------------------------------
    // Regression (2026-09-18): Chessables Partie-Kurse stellen die Aufgabe oft als „der Gegner hat
    // gerade 10…Sd4 gespielt, widerlege das" — der erste Zug der Linie gehört dann dem GEGNER. Im
    // Repertoire-Modus steht kein [%tqu] im PGN; ohne die Solverfarbe konnte rookhub beim Umwandeln
    // eines Repertoires in einen Kurs nicht wissen, wer am Zug ist, und zeigte die falsche Seite.
    private const string FenBlackToMove = "r1bqk2r/1ppp1ppp/p1n3n1/3Np2Q/2B1P3/3P4/PPP2PP1/R1B1K2R b KQkq - 0 10";

    private static string TwoMoveLine(string color) =>
        "{\"game\":{\"initial\":\"" + FenBlackToMove + "\",\"color\":\"" + color + "\",\"isInfo\":0,\"data\":["
        + "{\"id\":0,\"move\":10,\"col\":\"b\",\"san\":\"Nd4\",\"isKey\":true,\"draws\":[]},"
        + "{\"id\":1,\"move\":11,\"col\":\"w\",\"san\":\"Bg5\",\"isKey\":true,\"draws\":[]}]}}";

    private static string PgnForLine(string lineJson, bool noTrainingMove)
    {
        var course = OneChapterCourse(
            "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":73000253,\"name\":\"L1\"}]}}", lineJson);
        var lib = new PirateChessLib { restResponseCourse = course, NoTrainingMove = noTrainingMove };
        return lib.GetCourse("1", useLocalData: true).Item1;
    }

    [Fact]
    public void GetCourse_RepertoireMode_WritesSolverColorHeader()
    {
        var pgn = PgnForLine(TwoMoveLine("white"), noTrainingMove: true);

        Assert.Contains("[ChessableColor \"white\"]", pgn);
        Assert.Contains("[ChessableOid \"73000253\"]", pgn);
        Assert.DoesNotContain("[%tqu", pgn);   // Repertoire-Modus setzt weiterhin keinen Marker
    }

    [Fact]
    public void GetCourse_LineWithoutColor_WritesNoColorHeader()
    {
        // Kein Farbwert ⇒ kein Header (nichts raten, nichts Fremdes in den Kopf schreiben).
        Assert.DoesNotContain("[ChessableColor", PgnForLine(TwoMoveLine(""), noTrainingMove: true));
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

    // ---- Chessables Null-Zug in der HAUPTlinie + Kommentar-Form ------------
    // Einleitungs-/Erklärlinien enden bei Chessable mit dem Null-Zug „--". Er darf nicht im Movetext
    // stehen: chess.js (RookHub-Viewer, Zugliste, Repertoire-Ansicht) kennt ihn nicht und verwirft die
    // GANZE Partie stillschweigend. Sein Kommentar muss trotzdem erhalten bleiben.
    private static string AfterWithItems(string beforeFen, string itemsJson) =>
        $"{{\"before\":\"{beforeFen}\",\"after\":\"\",\"data\":[{itemsJson}]}}";

    private static string CommentItem(string text) => $"{{\"key\":\"C\",\"state\":\"\",\"val\":\"{text}\"}}";

    private static string VariationItem(params (string key, string val)[] items)
    {
        var inner = string.Join(",", items.Select(it => $"{{\"key\":\"{it.key}\",\"state\":\"\",\"val\":\"{it.val}\"}}"));
        return $"{{\"key\":\"V\",\"state\":\"\",\"val\":[{inner}]}}";
    }

    [Fact]
    public void GetVariationParts_UnplayableHere_AnchoredAtItsOwnMoveNumber()
    {
        // Chessable hängt die Verweis-Linien einer Einleitung an den Null-Zug am Ende: von dort aus
        // (Schwarz am Zug nach 1.e4) ist „1.e4 e5 2.Nf3 …" nicht spielbar. Früher wurde daraus ein
        // Kommentar — in ChessBase bloder Text. Jetzt hängt die Variante an Zug 1.
        const string afterE4 = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1";
        var game = new Game
        {
            Initial = StartFen,
            Data =
            [
                new JsonMove { Id = 0, Move = 1, Col = "w", San = "e4" },
                new JsonMove { Id = 1, Move = 1, Col = "b", San = "--",
                    After = AfterWithItems(afterE4, VariationItem(("C", "• Against"), ("S", "1.e4"), ("S", "e5"),
                        ("C", "we will play"), ("S", "2.Nf3"), ("S", "Nc6"), ("S", "3.Bb5"), ("C", "and the main lines."))) },
            ],
        };

        var pgn = game.GeneratePGN(noTrainingMove: true);

        Assert.Contains("1. e4 ({• Against} 1.e4 e5 {we will play} 2.Nf3 Nc6 3.Bb5 {and the main lines.})", pgn);
    }

    [Fact]
    public void GetVariationParts_NoAnchorWithMatchingMoveNumber_StaysComment()
    {
        // Ersatz-Anker nur, wenn Vollzugzahl UND Farbe passen — sonst hängt eine Notiz an einer Stellung,
        // an der sie zufällig legal ist. „7.Ra2" passt zu keiner Stellung dieser kurzen Linie.
        var game = new Game
        {
            Initial = StartFen,
            Data =
            [
                new JsonMove { Id = 0, Move = 1, Col = "w", San = "e4" },
                new JsonMove { Id = 1, Move = 1, Col = "b", San = "--",
                    After = AfterWithItems("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1",
                        VariationItem(("S", "7.Ra2"))) },
            ],
        };

        var pgn = game.GeneratePGN(noTrainingMove: true);

        Assert.DoesNotContain("(7.Ra2", pgn);
        Assert.Contains("{7.Ra2}", pgn);
    }

    [Fact]
    public void GetVariationPgn_UnnumberedMovesCountTowardsTheSplit()
    {
        // „… 3.Nc3 a6 und sogar 3...h6": „a6" ist schon Schwarz' dritter Zug, „3...h6" ist also eine
        // ALTERNATIVE und kein Folgezug. Ohne Mitzählen der Züge ohne Nummer blieb alles ein Cluster —
        // nicht spielbar, und der ganze Block wurde ein Kommentar.
        var pgn = PgnForFirstMoveWithV(AfterWithV(StartFen,
            ("S", "1.d4"), ("S", "d5"), ("S", "2.c4"), ("S", "e6"), ("S", "3.Nc3"), ("S", "a6"),
            ("C", "und sogar"), ("S", "3...h6")));

        Assert.Contains("(1.d4 d5 2.c4 e6 3.Nc3 a6 {und sogar})", pgn);
        Assert.Contains("{3...h6}", pgn);
    }

    [Fact]
    public void GeneratePGN_TrailingNullMove_OmittedButCommentKept()
    {
        var game = new Game
        {
            Initial = StartFen,
            Data =
            [
                new JsonMove { Id = 0, Move = 1, San = "e4" },
                new JsonMove { Id = 1, Move = 1, San = "--", After = AfterWithItems(StartFen, CommentItem("Hier geht es weiter.")) },
            ],
        };

        var pgn = game.GeneratePGN(noTrainingMove: true);

        Assert.DoesNotContain("--", pgn);          // chess.js würde daran die ganze Partie verwerfen
        Assert.DoesNotContain("1...", pgn);        // auch die Zugnummer des Null-Zugs fällt weg
        Assert.Contains("1. e4", pgn);
        Assert.Contains("Hier geht es weiter.", pgn);
    }

    [Fact]
    public void GeneratePGN_NullMoveFollowedByRealMove_KeepsPlaceholder()
    {
        // Nur der Null-Zug am ENDE fällt weg — mitten in der Linie wäre die Zugfolge ohne Platzhalter falsch.
        var game = new Game
        {
            Initial = StartFen,
            Data =
            [
                new JsonMove { Id = 0, Move = 1, San = "e4" },
                new JsonMove { Id = 1, Move = 1, San = "--" },
                new JsonMove { Id = 2, Move = 2, San = "e5" },
            ],
        };

        Assert.Contains("--", game.GeneratePGN(noTrainingMove: true));
    }

    [Fact]
    public void GeneratePGN_CommentAndCommentedVariation_MergedIntoOneComment()
    {
        // „{a} {b}" lehnt chess.js ab. Hier trifft der Zug-Kommentar auf eine Variante, die als Kommentar
        // gerendert wird (nicht nachspielbar) — beides gehört in EINEN Kommentar.
        var after = AfterWithItems(StartFen, CommentItem("Ein Hinweis.") + "," + VariationItem(("S", "3...Bb7"), ("S", "4.e3")));
        var game = new Game { Initial = StartFen, Data = [ new JsonMove { Id = 0, Move = 1, San = "e4", After = after } ] };

        var pgn = game.GeneratePGN(noTrainingMove: true);

        Assert.DoesNotContain("} {", pgn);
        Assert.Contains("{Ein Hinweis. 3...Bb7 4.e3}", pgn);
    }

    [Fact]
    public void GetVariationPgn_CommentStartingWithPunctuation_NoSpaceBeforeIt()
    {
        // Aus der Einleitung eines echten Kurses: „1.d4 . And maybe this is true." — das Satzzeichen
        // gehört an den Zug davor, nicht hinter ein Leerzeichen.
        var pgn = PgnForFirstMoveWithV(AfterWithV(StartFen, ("S", "3...Bb7"), ("C", ". Soweit die Theorie.")));

        Assert.Contains("{3...Bb7. Soweit die Theorie.}", pgn);
        Assert.DoesNotContain("Bb7 .", pgn);
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

    // ---- Mehrdeutige Züge in "V" (gemeldet 2026-09-23) ----------------------
    // Kurs „Lifetime Repertoires: King's Indian Defense - Part 2", Linie „Fianchetto Variation: 7.d5 e6
    // 8.O-O with 9.Ng5 #6": vor 16.Dxd6 stehen weiße Springer auf c3 UND c5, beide können nach e4. Der
    // Kommentar dort sagt „16.Ne4 is a better try …" — SanToMove nahm still den ersten passenden
    // Springer, und ins PGN kam „(16.Ne4 {…})": ein Zug, den kein PGN-Leser spielen kann.
    private const string KidBefore16 = "r2q2k1/pp2rpb1/2np2pp/2N5/2P5/2NQ2Pb/PP2PP1P/R1BR2K1 w - - 3 16";

    [Fact]
    public void GetVariationPgn_AmbiguousMove_RenderedAsCommentNotAsMove()
    {
        var pgn = PgnForFirstMoveWithV(AfterWithV(KidBefore16,
            ("S", "16.Ne4"), ("C", "is a better try, although we have full compensation after Bf5.")));

        Assert.DoesNotContain("(16.Ne4", pgn);
        Assert.Contains("{16.Ne4 is a better try, although we have full compensation after Bf5.}", pgn);
    }

    [Fact]
    public void GetVariationPgn_AmbiguousMove_ResolvedByContinuation_WritesDisambiguatedSan()
    {
        // 17.Sxb7 geht nur, wenn der Springer auf c5 stehen bleibt → gemeint ist der von c3.
        var pgn = PgnForFirstMoveWithV(AfterWithV(KidBefore16, ("S", "16.Ne4"), ("S", "Bf5"), ("S", "17.Nxb7")));

        Assert.Contains("(16.N3e4 Bf5 17.Nxb7)", pgn);
    }

    [Fact]
    public void GetVariationPgn_AmbiguousMove_BothContinuationsLegal_RenderedAsComment()
    {
        // Nach beiden Springerzügen geht 16…Lf5 → die Folge legt nichts fest, also kein Zug.
        var pgn = PgnForFirstMoveWithV(AfterWithV(KidBefore16, ("S", "16.Ne4"), ("S", "Bf5")));

        Assert.DoesNotContain("(16.", pgn);
        Assert.Contains("{16.Ne4 Bf5}", pgn);
    }

    [Fact]
    public void GetVariationPgn_ExplicitlyDisambiguatedMove_StaysAsWritten()
    {
        var pgn = PgnForFirstMoveWithV(AfterWithV(KidBefore16, ("S", "16.N5e4"), ("S", "Bf5")));

        Assert.Contains("(16.N5e4 Bf5)", pgn);
    }

    // ---- Lange Varianten-Cluster (Review 2026-09-29, N3-001) -----------------
    // ResolveLine rief sich je Zug eines Clusters selbst auf, ohne Tiefengrenze. Ein Cluster wird nur an
    // Zugnummer-Rücksprüngen getrennt, eine lange legale Pendelfolge (Sf3 Sf6 Sg1 Sg8 …) aus einem
    // Browser-Upload war also EIN Cluster mit beliebig vielen Zügen → StackOverflow, den kein catch fängt:
    // der ganze piratechess-Prozess starb samt laufender Abrufe. Jetzt: mehr als 512 Halbzüge
    // (Models.cs MaxVariationPlies) werden nicht nachgespielt, sondern wie jeder nicht auflösbare Cluster
    // als {Kommentar} ausgegeben — der Inhalt bleibt erhalten.
    private const int MaxVariationPlies = 512;

    /// <summary>Legale Springer-Pendelfolge ab der Grundstellung: 1.Nf3 Nf6 2.Ng1 Ng8 3.Nf3 …</summary>
    private static (string key, string val)[] ShuttleLine(int plies) =>
        Enumerable.Range(0, plies).Select(p =>
        {
            int w = p / 2;
            bool there = w % 2 == 0;
            return ("S", p % 2 == 0 ? $"{w + 1}.{(there ? "Nf3" : "Ng1")}" : (there ? "Nf6" : "Ng8"));
        }).ToArray();

    [Fact]
    public void GetVariationPgn_ClusterAtPlyCap_StillRenderedAsVariation()
    {
        // Verhaltensneutral bis zur Grenze: 512 Halbzüge spielen weiter als echte Variante durch.
        var pgn = PgnForFirstMoveWithV(AfterWithV(StartFen, ShuttleLine(MaxVariationPlies)));

        Assert.Contains("(1.Nf3 Nf6 2.Ng1 Ng8 3.Nf3", pgn);
        Assert.Contains("256.Ng1 Ng8)", pgn);
    }

    [Fact]
    public void GetVariationPgn_ClusterOverPlyCap_RenderedAsCommentNotResolved()
    {
        var pgn = PgnForFirstMoveWithV(AfterWithV(StartFen, ShuttleLine(MaxVariationPlies + 1)));

        Assert.DoesNotContain("(1.Nf3", pgn);
        Assert.Contains("{1.Nf3 Nf6 2.Ng1 Ng8 3.Nf3", pgn);
        Assert.Contains("256.Ng1 Ng8 257.Nf3}", pgn);   // nichts abgeschnitten
    }

    [Fact]
    public void GetVariationPgn_HugeCluster_NoStackOverflowOnSmallStack()
    {
        // Der eigentliche Angriff: 60.000 Halbzüge. Auf einem Thread mit 1 MB Stack (kleiner als die
        // 1,5 MB der Threadpool-Threads, auf denen GetCourse läuft) hätte die alte Rekursion den
        // Testprozess beendet; jetzt bleibt die Tiefe unabhängig von der Clusterlänge.
        string after = AfterWithV(StartFen, ShuttleLine(60_000));
        string? pgn = null;
        Exception? error = null;
        var worker = new Thread(() =>
        {
            try { pgn = PgnForFirstMoveWithV(after); }
            catch (Exception ex) { error = ex; }
        }, maxStackSize: 1024 * 1024);
        worker.Start();
        worker.Join();

        Assert.Null(error);
        Assert.NotNull(pgn);
        Assert.DoesNotContain("(1.Nf3", pgn);
        Assert.Contains("{1.Nf3 Nf6 2.Ng1 Ng8", pgn);
    }

    [Fact]
    public void GeneratePGN_DeeplyNestedV_RejectedByJsonDepthLimit()
    {
        // FlattenToText rekursiert je verschachtelter V-Ebene. Gedeckelt ist das durch die JSON-Tiefe
        // (System.Text.Json-Standard 64, Options.cs setzt keine eigene): tiefer Verschachteltes scheitert
        // schon beim Einlesen mit einer fangbaren JsonException. Wer MaxDepth anhebt, muss FlattenToText
        // eine eigene Tiefengrenze geben — dieser Test fällt dann um.
        const int levels = 200;
        string nested = "{\"key\":\"S\",\"state\":\"\",\"val\":\"1.d4\"}";
        for (int i = 0; i < levels; i++)
            nested = $"{{\"key\":\"V\",\"state\":\"\",\"val\":[{nested}]}}";
        string after = $"{{\"before\":\"{StartFen}\",\"after\":\"\",\"data\":[{nested}]}}";

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => PgnForFirstMoveWithV(after));
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

    // ---- PGN-Escaping: Sonderzeichen aus Chessable-Texten ------------------
    // Kapitel-/Linienname mit " zerlegte den Tag-Wert (`[Event "The "Catalan" Setup"]`) → ungültiger
    // Header, den der rookhub-Import falsch bzw. gar nicht liest.
    [Fact]
    public void GetCourse_QuoteInChapterAndLineName_HeadersEscaped()
    {
        var course = OneChapterCourse(
            "{\"list\":{\"name\":\"The \\\"Catalan\\\" Setup\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"Line \\\"A\\\"\"}]}}",
            "{\"game\":{\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"san\":\"e4\"}]}}");
        var lib = new PirateChessLib { restResponseCourse = course };

        var (pgn, _) = lib.GetCourse("1", useLocalData: true);

        Assert.Contains("[Event \"The \\\"Catalan\\\" Setup\"]", pgn);
        Assert.Contains("[White \"Line \\\"A\\\"\"]", pgn);
        // Kein unescapetes " mehr im Wert → jede Header-Zeile endet sauber auf "]
        foreach (var line in pgn.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("[Event") || l.StartsWith("[White")))
            Assert.EndsWith("\"]", line);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a\"b", "a\\\"b")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("a\nb", "a b")]      // Zeilenumbrüche sind in einem Tag-Wert nicht erlaubt
    [InlineData(null, "")]
    public void EscapeHeader_EscapesQuotesAndBackslashes(string? input, string expected)
        => Assert.Equal(expected, PirateChessLib.EscapeHeader(input));

    // Ein „}" im Chessable-Kommentar beendete den PGN-Kommentar vorzeitig — der Resttext landete als
    // Müll im Movetext und die Linie war beim Import unlesbar.
    [Fact]
    public void GeneratePGN_BraceInComment_DoesNotBreakCommentBlock()
    {
        var game = new Game
        {
            Initial = "",
            Data =
            [
                new JsonMove
                {
                    Id = 0, Move = 1, San = "e4", Col = "w",
                    After = "{\"data\":[{\"key\":\"C\",\"val\":\"Schlage } sofort {zurück\"}]}",
                },
            ],
        };

        var pgn = game.GeneratePGN(noTrainingMove: true);

        Assert.Contains("Schlage ) sofort (zurück", pgn);
        // Genau ein Kommentarblock: geschweifte Klammern nur noch als Begrenzer.
        Assert.Equal(1, pgn.Count(c => c == '{'));
        Assert.Equal(1, pgn.Count(c => c == '}'));
    }

    // ---- Nachbau der echten Linie 20733162 (Kurs 128648) gegen Chessables eigenen PGN-Export ----
    // Chessable exportiert: „d5 3. e5 (3. Nc3 …) (3. exd5 …) (3. d3 {ist hier in der Zugfolge})
    // {2.d3 d5 3.Nf3 analysiert.} 3... c5" — ohne doppelte Leerzeichen, ohne Zeilenumbrüche im Text
    // und mit „3..." vor dem schwarzen Zug nach den Varianten.
    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    [Fact]
    public void GeneratePGN_RealLine_MatchesChessableExportFormatting()
    {
        const string fenBeforeE5 = "rnbqkbnr/ppp2ppp/4p3/3p4/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq d6 0 3";
        var introBefore = Json(new
        {
            before = StartFen, after = "",
            data = new object[] { new { key = "V", val = new object[] {
                new { key = "C", val = "willkommen zum ersten Kapitel. <br/><br/> Es gibt nichts, wenn er @@StartFEN@@x@@EndFEN@@" },
                new { key = "S", val = "2.d4" },
                new { key = "C", val = "spielen kann. Alles außer <strong>2.d4</strong>." },
            } } },
        });
        var afterE5 = Json(new
        {
            before = fenBeforeE5, after = "",
            data = new object[]
            {
                new { key = "V", val = new object[] {
                    new { key = "S", val = "3.Nc3" }, new { key = "S", val = "Nf6" },
                    new { key = "C", val = "werden wir <a class=\"commentMoveSmall\" href=\"/course/128648/3\">später</a> sehen." } } },
                new { key = "V", val = new object[] {
                    new { key = "S", val = "3.d3" },
                    new { key = "C", val = "ist <a href=\"/variation/1\">hier</a> in der Zugfolge @@StartFEN@@x@@EndFEN@@" },
                    new { key = "S", val = "2.d3" }, new { key = "S", val = "d5" }, new { key = "S", val = "3.Nf3" },
                    new { key = "C", val = "analysiert." } } },
            },
        });
        var game = new Game
        {
            Initial = StartFen,
            Data =
            [
                new JsonMove { Id = 0, Move = 1, San = "e4", Col = "w", Before = introBefore },
                new JsonMove { Id = 1, Move = 1, San = "e6", Col = "b" },
                new JsonMove { Id = 2, Move = 2, San = "Nf3", Col = "w" },
                new JsonMove { Id = 3, Move = 2, San = "d5", Col = "b" },
                new JsonMove { Id = 4, Move = 3, San = "e5", Col = "w", After = afterE5 },
                new JsonMove { Id = 5, Move = 3, San = "c5", Col = "b" },
                new JsonMove { Id = 6, Move = 4, San = "b4", Col = "w" },
            ],
        };

        var pgn = game.GeneratePGN(noTrainingMove: true);

        Assert.StartsWith("{willkommen zum ersten Kapitel. Es gibt nichts, wenn er 2.d4 spielen kann. Alles außer 2.d4.} 1. e4 ", pgn);
        Assert.Contains("3. e5 (3.Nc3 Nf6 {werden wir später sehen.}) (3.d3 {ist hier in der Zugfolge}) 3... c5 4. b4", pgn);
        // Die Transpositions-Notiz „2.d3 d5 3.Nf3" ist von 3.e5 aus nicht spielbar. Sie landet deshalb
        // nicht mehr als Kommentar im PGN, sondern als echte — und damit anklickbare — Variante an dem
        // Zug, zu dem ihre Zugnummer passt (2.Sf3).
        Assert.Contains("2. Nf3 (2.d3 d5 3.Nf3 {analysiert.}) 2... d5", pgn);
        Assert.DoesNotContain("\n", pgn);
        Assert.DoesNotContain("  ", pgn.TrimEnd());
    }

    [Fact]
    public void GeneratePGN_BlackMoveAfterVariation_GetsMoveNumber_OtherwiseNot()
    {
        var game = new Game
        {
            Initial = StartFen,
            Data =
            [
                new JsonMove { Id = 0, Move = 1, San = "e4", Col = "w", After = AfterWithV(StartFen, ("S", "1.d4")) },
                new JsonMove { Id = 1, Move = 1, San = "e5", Col = "b" },
                new JsonMove { Id = 2, Move = 2, San = "Nf3", Col = "w", After = "{\"data\":[{\"key\":\"C\",\"val\":\"Entwicklung\"}]}" },
                new JsonMove { Id = 3, Move = 2, San = "Nc6", Col = "b" },
            ],
        };

        var pgn = game.GeneratePGN(noTrainingMove: true);

        Assert.Equal("1. e4 (1.d4) 1... e5 2. Nf3 {Entwicklung} Nc6 ", pgn);   // nach Kommentar keine Nummer (wie Chessable)
    }

    [Fact]
    public void GeneratePGN_LineStartingWithBlack_StartsWithEllipsis()
    {
        var game = new Game
        {
            Initial = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1",
            Data =
            [
                new JsonMove { Id = 0, Move = 1, San = "c5", Col = "b" },
                new JsonMove { Id = 1, Move = 2, San = "Nf3", Col = "w" },
            ],
        };

        Assert.Equal("1... c5 2. Nf3 ", game.GeneratePGN(noTrainingMove: true));
    }

    // ---- PGN-Injektion über Nicht-Kommentar-Felder (Review 2026-09-29, S2-017) ----
    // Nur Kommentare wurden entschärft; San, Pfeil-/Kreisfelder, softFail-Alternativen und Varianten-Züge
    // gingen roh ins PGN. Eine vergiftete Linie aus dem geteilten Cache (Browser-Upload) konnte so mit
    // Zeilenumbruch + „[Event …]" eine zweite Partie samt fremdem [ChessableOid] einschleusen — rookhub
    // zerlegt an „[Event " und nimmt die oid je Block als Wahrheit (die spätere überschreibt die echte).
    private const string InjectedGame = "\n\n[Event \"x\"]\n[ChessableOid \"999\"]\n\n1. d4";

    private static int CountOf(string haystack, string needle)
        => (haystack.Length - haystack.Replace(needle, "").Length) / needle.Length;

    [Fact]
    public void GetCourse_SanWithInjectedGame_LineSkippedNoForeignOid()
    {
        string poisoned = Json(new { game = new { initial = "", data = new object[] {
            new { id = 0, move = 1, col = "w", san = "e4" + InjectedGame } } } });
        var course = OneChapterCourse(
            "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"L1\"},{\"id\":11,\"name\":\"L2\"}]}}",
            poisoned,
            "{\"game\":{\"initial\":\"\",\"data\":[{\"id\":0,\"move\":1,\"san\":\"d4\"}]}}");
        var lib = new PirateChessLib { restResponseCourse = course };

        var (pgn, _) = lib.GetCourse("1", useLocalData: true);

        Assert.DoesNotContain("ChessableOid \"999\"", pgn);
        Assert.DoesNotContain("ChessableOid \"10\"", pgn);   // die vergiftete Linie fehlt ganz …
        Assert.Contains("ChessableOid \"11\"", pgn);         // … die saubere bleibt
        Assert.Equal(1, CountOf(pgn, "[Event "));
        Assert.Equal(1, lib.ErrorCount);
        Assert.Contains("GeneratePGN übersprungen", lib.ErrorDetails[0]);
    }

    [Theory]
    [InlineData("e4")]
    [InlineData("exd5")]
    [InlineData("Nbd2")]
    [InlineData("R1a3")]
    [InlineData("Qh4xe1")]
    [InlineData("Kxe2")]
    [InlineData("e8=Q+")]
    [InlineData("exd8=N#")]
    [InlineData("b8Q")]
    [InlineData("O-O")]
    [InlineData("O-O-O+")]
    [InlineData("0-0")]
    [InlineData("Nf3!?")]
    [InlineData("Qxf7#!")]
    public void GeneratePGN_SanForms_Accepted(string san)
    {
        var game = new Game { Initial = "", Data = [ new JsonMove { Id = 0, Move = 1, Col = "w", San = san } ] };

        Assert.Equal($"1. {san} ", game.GeneratePGN(noTrainingMove: true));
    }

    // „--" (Null-Zug) und ein leeres San sind kein Zug: sie stehen am Ende von Einleitungslinien und
    // fallen aus dem Movetext (chess.js verwirft sonst die ganze Partie). Eine Linie, die NUR daraus
    // besteht, hat damit keinen Zugtext mehr — ihre Kommentare bleiben erhalten (eigener Test).
    [Theory]
    [InlineData("--")]
    [InlineData("")]
    public void GeneratePGN_OnlyNullMove_EmptyMovetext(string san)
    {
        var game = new Game { Initial = "", Data = [ new JsonMove { Id = 0, Move = 1, Col = "w", San = san } ] };

        Assert.Equal("", game.GeneratePGN(noTrainingMove: true));
    }

    [Theory]
    [InlineData("e4\n")]
    [InlineData("e4\r\n[Event \"x\"]")]
    [InlineData("e4 d5")]
    [InlineData("e4}")]
    [InlineData("{e4")]
    [InlineData("(e4)")]
    [InlineData("e4;")]
    [InlineData("e4 %")]
    [InlineData("[Event")]
    [InlineData("e9")]
    [InlineData("Nf3 1-0")]
    public void GeneratePGN_NonSan_Throws(string san)
    {
        var game = new Game { Initial = "", Data = [ new JsonMove { Id = 0, Move = 1, Col = "w", San = san } ] };

        Assert.Throws<FormatException>(() => game.GeneratePGN(noTrainingMove: true));
    }

    [Fact]
    public void GeneratePGN_SoftFailEntryNotSan_Dropped()
    {
        var game = new Game
        {
            Initial = "",
            Data =
            [
                new JsonMove { Id = 0, Move = 1, San = "e4", Col = "w" },
                new JsonMove { Id = 1, Move = 1, San = "e6", Col = "b" },
            ],
            SoftFail = [ new SoftFailEntry { B = ["e6", "e5]}" + InjectedGame + " {", "c5"] } ],
        };

        var pgn = game.GeneratePGN(noTrainingMove: true);

        Assert.Contains("[%alt c5]", pgn);
        Assert.DoesNotContain("[Event", pgn);
        Assert.DoesNotContain("\n", pgn);
    }

    [Fact]
    public void GeneratePGN_DrawFieldsNotSquareOrColor_DrawDropped()
    {
        var game = new Game
        {
            Initial = "",
            Data =
            [
                new JsonMove
                {
                    Id = 0, Move = 1, San = "e4", Col = "w",
                    Draws =
                    [
                        new JsonDraw { Object = "arrow", Color = "g", Start = "e2", End = "e4" },
                        new JsonDraw { Object = "arrow", Color = "r]}" + InjectedGame + " {", Start = "d2", End = "d4" },
                        new JsonDraw { Object = "arrow", Color = "r", Start = "d2", End = "d4" + InjectedGame },
                        new JsonDraw { Object = "circle", Color = "r", Start = "d4" },
                        new JsonDraw { Object = "circle", Color = "g", Start = "e4}" + InjectedGame },
                    ],
                },
            ],
        };

        var pgn = game.GeneratePGN(noTrainingMove: true);

        Assert.Equal("1. e4 {[%cal Ge2e4][%csl Rd4]} ", pgn);
    }

    private static string AfterWithVJson(string beforeFen, params (string key, string val)[] items) => Json(new
    {
        before = beforeFen, after = "",
        data = new object[] { new { key = "V", state = "", val = items.Select(it => new { key = it.key, state = "", val = it.val }).ToArray() } },
    });

    [Fact]
    public void GetVariationPgn_TokenWithLineBreakOrBracket_Discarded()
    {
        // „N…f3" löste die alte SAN-Auflösung als Springerzug auf (Mitte ohne Buchstabe/Ziffer = keine
        // Disambiguierung) und schrieb den ganzen Text als Varianten-Zug ins PGN.
        var pgn = PgnForFirstMoveWithV(AfterWithVJson(StartFen, ("S", "N" + InjectedGame + " f3")));

        Assert.DoesNotContain("[Event", pgn);
        Assert.DoesNotContain("ChessableOid", pgn);
        Assert.Equal("1. e4 ", pgn);
    }

    [Fact]
    public void GetVariationPgn_UnresolvableTokenWithBrace_DoesNotBreakOutOfComment()
    {
        // Nicht nachspielbare Cluster werden {Kommentar}; ein „}" im Zugtext beendete ihn vorzeitig.
        var pgn = PgnForFirstMoveWithV(AfterWithVJson(StartFen, ("S", "3...Bb7"), ("S", "4.e3} [Event \"x\"] {")));

        Assert.Contains("{3...Bb7}", pgn);
        Assert.DoesNotContain("[Event", pgn);
        Assert.Equal(1, pgn.Count(c => c == '{'));
        Assert.Equal(1, pgn.Count(c => c == '}'));
    }

    [Fact]
    public void GetVariationPgn_ResolvableButNotSan_RenderedAsCommentNotAsMove()
    {
        // „N;f3" war für die alte Auflösung ein Springerzug nach f3 — im Movetext beginnt „;" aber einen
        // Zeilenkommentar und verschluckt die schließende Klammer samt Rest der Linie.
        var pgn = PgnForFirstMoveWithV(AfterWithVJson(StartFen, ("S", "1.N;f3")));

        Assert.DoesNotContain("(1.N;f3", pgn);
        Assert.Contains("{1.N;f3}", pgn);
    }

    // ---- Nacharbeit S2-017: derselbe Hebel über den KOMMENTARTEXT ----
    // ReplaceCommentStuff ersetzte nur { }; eckige Klammern blieben stehen. Ein „C" mit „[Event …] [ChessableOid …]"
    // landete wörtlich im {Kommentar}, und rookhubs Zerlegung (?=\[Event ) fand darin eine zweite Partie mit fremder
    // oid. Zwei „C" in before werden mit Zeilenumbruch verbunden — dann steht „[Event" sogar am Zeilenanfang.
    private const string ForgedHeaders = "[Event \"x\"] [ChessableOid \"999\"]";

    /// <summary>Nachbau von rookhubs GetCachedLinePgnsAsync: an „[Event " zerlegen, je Block die erste ChessableOid
    /// als Schlüssel (spätere Blöcke überschreiben frühere).</summary>
    private static List<string> RookhubSplitOids(string pgn)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in System.Text.RegularExpressions.Regex.Split(pgn, @"(?=\[Event )"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(block, "\\[ChessableOid \"([^\"]+)\"\\]");
            if (m.Success) result[m.Groups[1].Value] = block.Trim();
        }
        return result.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    private static (string pgn, PirateChessLib lib) CourseWithOneLine(object move)
    {
        var course = OneChapterCourse(
            "{\"list\":{\"name\":\"Ch1\",\"title\":\"T\",\"data\":[{\"id\":10,\"name\":\"L1\"}]}}",
            Json(new { game = new { initial = "", data = new[] { move } } }));
        var lib = new PirateChessLib { restResponseCourse = course };
        var (pgn, _) = lib.GetCourse("1", useLocalData: true);
        return (pgn, lib);
    }

    [Fact]
    public void GetCourse_CommentAfterWithForgedHeaders_NeutralizedNoForeignOid()
    {
        var (pgn, lib) = CourseWithOneLine(new
        {
            id = 0, move = 1, col = "w", san = "e4",
            after = Json(new { data = new object[] { new { key = "C", val = "nice " + ForgedHeaders + " 1. d4 d5 2. c4" } } }),
        });

        Assert.Equal(1, CountOf(pgn, "[Event "));
        Assert.DoesNotContain("[ChessableOid \"999\"]", pgn);   // als entschärfter Text bleibt er stehen, als Tag nicht
        Assert.Equal(new[] { "10" }, RookhubSplitOids(pgn));
        Assert.Contains("1. e4 {nice (Event \"x\") (ChessableOid \"999\") 1. d4 d5 2. c4}", pgn);   // Linie bleibt, Text entschärft
        Assert.Equal(0, lib.ErrorCount);
    }

    [Fact]
    public void GetCourse_SecondCommentBeforeWithForgedHeaders_NeutralizedNoForeignOid()
    {
        var (pgn, lib) = CourseWithOneLine(new
        {
            id = 0, move = 1, col = "w", san = "e4",
            before = Json(new { data = new object[] { new { key = "C", val = "Intro" }, new { key = "C", val = ForgedHeaders } } }),
        });

        Assert.Equal(1, CountOf(pgn, "[Event "));
        Assert.DoesNotContain("[ChessableOid \"999\"]", pgn);   // als entschärfter Text bleibt er stehen, als Tag nicht
        Assert.Equal(new[] { "10" }, RookhubSplitOids(pgn));
        Assert.Contains("{Intro" + Environment.NewLine + "(Event \"x\") (ChessableOid \"999\")} 1. e4", pgn);
        Assert.Equal(0, lib.ErrorCount);
    }

    [Fact]
    public void GeneratePGN_CommentWithForgedMarkers_NeutralizedOwnMarkersKept()
    {
        // rookhub liest [%info]/[%tqu]/[%alt]/[%cal]/[%csl] aus dem Movetext — aus Kommentartext darf keiner entstehen,
        // die selbst erzeugten Marker kommen erst danach dazu und bleiben.
        var game = new Game
        {
            Initial = "",
            IsInfo = 1,
            Data =
            [
                new JsonMove
                {
                    Id = 0, Move = 1, San = "e4", Col = "w",
                    Before = Json(new { data = new object[] { new { key = "C", val = "[%info] [%tqu \"En\",\"find the move\",\"\",\"\",\"d2d4\",\"\",10]" } } }),
                    After = Json(new { data = new object[] { new { key = "C", val = "see [%cal Rd2d4][%csl Rd4] [%alt d4]" } } }),
                    Draws = [ new JsonDraw { Object = "arrow", Color = "g", Start = "e2", End = "e4" } ],
                },
            ],
        };

        var pgn = game.GeneratePGN(noTrainingMove: true);

        Assert.Equal(1, CountOf(pgn, "[%info"));
        Assert.StartsWith("{[%info]\n(%info)", pgn);
        Assert.DoesNotContain("[%tqu", pgn);
        Assert.DoesNotContain("[%alt", pgn);
        Assert.DoesNotContain("[%csl", pgn);
        Assert.Equal(1, CountOf(pgn, "[%cal"));
        Assert.Contains("{[%cal Ge2e4]see (%cal Rd2d4)(%csl Rd4) (%alt d4)}", pgn);
    }
}
