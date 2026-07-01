using System.Text.RegularExpressions;
using piratechess_lib;

namespace PirateChess.Api.Tests;

/// <summary>
/// Fixtures für den SAN→UCI-Pfad (SanToMove + GetFirstKeyMoveUci/GetAllTrainingUcis), getrieben über
/// die öffentliche GeneratePGN-Ausgabe. Ein leerer UCI im `[%tqu …]` bedeutet: SanToMove konnte den
/// (legalen) Schlüsselzug nicht auflösen → der Trainer kennt den geforderten Zug nicht. Diese Tests
/// belegen, dass die realistischen SAN-Formen (Umwandlung, Schlag-Umwandlung, Rochade, Disambiguierung,
/// en passant) korrekt in UCI übersetzt werden.
/// </summary>
public class PirateChessLibSanTests
{
    /// <summary>Baut eine Ein-Zug-Linie ab <paramref name="initialFen"/>, deren erster Zug der
    /// Schlüsselzug ist, und liefert den im `[%tqu]`-Trainingskommentar erzeugten UCI (oder "").</summary>
    private static string FirstKeyUci(string initialFen, string san, string color = "white", int move = 8, string col = "w")
    {
        var game = new Game
        {
            Initial = initialFen,
            Color = color,
            Data = [ new JsonMove { Id = 0, Move = move, Col = col, San = san, IsKey = true } ],
        };
        var pgn = game.GeneratePGN();
        // [%tqu "En","find the move","","","<uci>","",10]
        var m = Regex.Match(pgn, "\\[%tqu \"En\",\"find the move\",\"\",\"\",\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : "(no tqu)";
    }

    [Theory]
    // Umwandlung (mit '=')
    [InlineData("4k3/4P3/8/8/8/8/8/4K3 w - - 0 8", "e8=Q", "e7e8q")]
    // Schlag-Umwandlung
    [InlineData("3rk3/4P3/8/8/8/8/8/4K3 w - - 0 8", "exd8=Q", "e7d8q")]
    // Kurze Rochade
    [InlineData("4k3/8/8/8/8/8/8/4K2R w K - 0 8", "O-O", "e1g1")]
    // Lange Rochade
    [InlineData("4k3/8/8/8/8/8/8/R3K3 w Q - 0 8", "O-O-O", "e1c1")]
    // Springer-Datei-Disambiguierung: Nb1 und Nf3 erreichen beide d2 → Nbd2
    [InlineData("4k3/8/8/8/8/5N2/8/1N2K3 w - - 0 8", "Nbd2", "b1d2")]
    // Turm-Reihen-Disambiguierung: Ra1 und Ra5 erreichen beide a3 → R1a3
    [InlineData("4k3/8/8/R7/8/8/8/R3K3 w - - 0 8", "R1a3", "a1a3")]
    // En passant: weißer Bauer d5 schlägt c6 e.p. (Schwarz zog gerade c7-c5)
    [InlineData("4k3/8/8/2pP4/8/8/8/4K3 w - c6 0 8", "dxc6", "d5c6")]
    // Einfacher Bauernzug
    [InlineData("4k3/8/8/8/8/8/4P3/4K3 w - - 0 8", "e4", "e2e4")]
    public void FirstKeyMoveUci_RealisticSan_ResolvesToUci(string fen, string san, string expectedUci)
    {
        Assert.Equal(expectedUci, FirstKeyUci(fen, san));
    }

    // --- Regression: farbwidrige Push-Umwandlung in einer Variante riss den Kurs-Abruf ab ---

    /// <summary>Baut einen Zug, dessen "after" eine Variante (V) mit einem einzelnen SAN-Zug ab
    /// <paramref name="branchFen"/> enthält, und rendert das PGN über den echten GeneratePGN-Pfad.</summary>
    private static string VariationPgn(string branchFen, string variationSan)
    {
        // GetVariationPgn (Varianten-Pfad) ruft SanToMove OHNE try/catch auf — im Gegensatz zum
        // Haupt-Zug-Pfad (GetFirstKeyMoveUci), der Exceptions schluckt. Eine farbwidrige Umwandlung
        // muss also hier reproduziert werden.
        string after = System.Text.Json.JsonSerializer.Serialize(new
        {
            before = branchFen,
            after = "",
            data = new object[] { new { key = "V", val = new object[] { new { key = "S", val = variationSan } } } },
        });
        var game = new Game
        {
            Initial = branchFen,
            Data = [ new JsonMove { Id = 0, Move = 1, Col = "w", San = "Ke2", After = after } ],
        };
        return game.GeneratePGN(noTrainingMove: true);
    }

    [Theory]
    // branchFen mit Weiß am Zug, Variante wandelt auf Reihe 1 um (nur Schwarz wandelt dort) → originRank 0
    [InlineData("4k3/8/8/8/8/8/8/4K3 w - - 0 1", "1. e1=Q")]
    // branchFen mit Schwarz am Zug, Variante wandelt auf Reihe 8 um (nur Weiß wandelt dort) → originRank 9
    [InlineData("4k3/8/8/8/8/8/8/4K3 b - - 0 1", "1... e8=Q")]
    public void GeneratePgn_VariationWithColorMismatchedPromotion_DoesNotThrow(string branchFen, string variationSan)
    {
        // Vor dem Fix errechnete die farbblinde Bedingung (destRank is 1 or 8) eine originRank von 0 bzw. 9
        // → new Position(destFile, 0/9) → ChessGame.GetPieceAt warf IndexOutOfRangeException, die durch
        // GetVariationPgn/GeneratePGN bis in den Kurs-Abruf durchschlug (ganzer Kurs „failed").
        // Jetzt wird die (illegale) Umwandlung sauber verworfen und die Variante als {Kommentar} gerendert.
        var ex = Record.Exception(() => VariationPgn(branchFen, variationSan));
        Assert.Null(ex);
    }

    // --- softFail-Indizierung (geduldete Alternativzüge, [%alt …]) ---

    [Fact]
    public void SoftFail_IndexedPerFullMove_WhiteAndBlackSplit()
    {
        var game = new Game
        {
            Data =
            [
                new JsonMove { Id = 0, Move = 1, Col = "w", San = "e4" },
                new JsonMove { Id = 1, Move = 1, Col = "b", San = "e5" },
                new JsonMove { Id = 2, Move = 2, Col = "w", San = "Nf3" },
            ],
            SoftFail =
            [
                new SoftFailEntry { W = ["e4", "d4"], B = ["e5", "c5"] },   // Vollzug 1
                new SoftFailEntry { W = ["Nf3", "Nc3"], B = null },         // Vollzug 2
            ],
        };
        var pgn = game.GeneratePGN(noTrainingMove: true);

        // Der gespielte Zug selbst wird aus den Alternativen herausgefiltert.
        Assert.Contains("e4 {[%alt d4]}", pgn);
        Assert.Contains("e5 {[%alt c5]}", pgn);
        Assert.Contains("Nf3 {[%alt Nc3]}", pgn);
    }

    [Fact]
    public void SoftFail_RelativeToLineStart_WhenLineStartsAtHigherMoveNumber()
    {
        // Linie beginnt bei Vollzug 10 (FEN) → sfIdx = move.Move - firstMoveNum ist linien-relativ:
        // SoftFail[0] gehört zu Vollzug 10, nicht zu einem absoluten Index 10.
        var game = new Game
        {
            Initial = "rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq - 0 10",
            Data =
            [
                new JsonMove { Id = 0, Move = 10, Col = "w", San = "Nf3" },
                new JsonMove { Id = 1, Move = 10, Col = "b", San = "Nc6" },
            ],
            SoftFail =
            [
                new SoftFailEntry { W = ["Nf3", "Bc4"], B = ["Nc6", "d6"] },   // gehört zu Vollzug 10
            ],
        };
        var pgn = game.GeneratePGN(noTrainingMove: true);

        Assert.Contains("Nf3 {[%alt Bc4]}", pgn);
        Assert.Contains("Nc6 {[%alt d6]}", pgn);
    }
}
