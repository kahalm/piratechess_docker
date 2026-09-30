using System.Text.RegularExpressions;

namespace piratechess_lib
{
    /// <summary>
    /// Eingangsprüfung für Chessable-Felder, die ROH ins PGN geschrieben werden (Review 2026-09-29, S2-017).
    /// Kommentare werden in <c>ReplaceCommentStuff</c> entschärft; Züge (San, Varianten-Züge), Pfeil-/Kreisfelder
    /// und softFail-Alternativen stehen dagegen unverändert im Movetext bzw. in <c>{[%cal]}</c>/<c>{[%csl]}</c>/
    /// <c>{[%alt]}</c>. Eine vergiftete Linie aus dem geteilten Cache (Browser-Upload) konnte darüber mit
    /// Zeilenumbruch + <c>[Event …]</c> eine zweite Partie samt fremdem <c>[ChessableOid]</c> einschleusen.
    /// Was dort landet, muss deshalb wie ein SAN-Zug, ein Feld oder ein Farbbuchstabe aussehen — Leerraum,
    /// Klammern, Anführungszeichen, „;" oder „%" kommen so nie hinein.
    /// </summary>
    internal static partial class PgnTokenGuard
    {
        /// <summary>SAN-Zug: Figurenzug mit optionaler Disambiguierung, Bauernzug/-schlag mit optionaler
        /// Umwandlung (mit oder ohne „="), Rochade in O- und 0-Schreibweise, Nullzug „--"; dahinter
        /// Schach/Matt und Bewertungszeichen in beliebiger Reihenfolge (wie <c>SanCandidates</c> sie abschneidet).</summary>
        internal static bool IsSan(string? san) => san is not null && SanPattern().IsMatch(san);

        /// <summary>Brettfeld „a1" … „h8".</summary>
        internal static bool IsSquare(string? square) => square is not null && SquarePattern().IsMatch(square);

        /// <summary>Farbe eines Pfeils/Kreises: nur Buchstaben (Chessable liefert „g", „r" …). Leer bleibt erlaubt —
        /// das ergab schon bisher einen Eintrag ohne Farbbuchstaben.</summary>
        internal static bool IsDrawColor(string? color) => string.IsNullOrEmpty(color) || ColorPattern().IsMatch(color);

        /// <summary>Zeilenumbruch oder Klammer (rund, eckig, geschweift): in einem Zug-Token nie echt, im PGN aber
        /// der Hebel, um Kommentar, Variante oder Partie vorzeitig zu beenden.</summary>
        internal static bool HasLineBreakOrBracket(string token) => token.AsSpan().IndexOfAny("\r\n{}[]()") >= 0;

        // \A…\z statt ^…$: „$" passt in .NET auch vor einem abschließenden „\n".
        [GeneratedRegex(@"\A(?:--|[KQRBN][a-h]?[1-8]?x?[a-h][1-8]|[a-h](?:x?[a-h])?[1-8](?:=?[QRBNqrbn])?|O-O(?:-O)?|0-0(?:-0)?)[+#!?]{0,3}\z")]
        private static partial Regex SanPattern();

        [GeneratedRegex(@"\A[a-h][1-8]\z")]
        private static partial Regex SquarePattern();

        [GeneratedRegex(@"\A[A-Za-z]{1,16}\z")]
        private static partial Regex ColorPattern();
    }
}
