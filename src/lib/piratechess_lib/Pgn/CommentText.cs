using System.Text.RegularExpressions;

namespace piratechess_lib
{
    /// <summary>Bereinigt Chessable-Kommentartext für einen PGN-Kommentar <c>{…}</c> (Marker, HTML, Klammern, Leerraum).
    /// Aus <c>JsonMoveItemList</c> herausgelöst (Zerlegung Z48).</summary>
    internal static partial class CommentText
    {
        internal static string Clean(string comment)
        {
            comment = comment.Replace("@@StartBracket@@", "(").Replace("@@EndBracket@@", ")");
            comment = findFenTags().Replace(comment, "");
            // Absatz-Grenzen (Zeilenumbruch, Zitat-/Überschrift-Block, Block-HTML) werden zu einem Leerzeichen, nicht
            // zu nichts: Chessable schreibt Absätze oft ohne Leerraum („…next few moves.<br><br>In comparison…",
            // „…umwandeln.@@EndBlockQuote@@Sollte…"), sonst klebten die Sätze aneinander („moves.In",
            // gemeldet 2026-11-10). Mehrfach-Leerraum fasst findWhitespace unten zusammen.
            comment = comment.Replace("@@StartBlockQuote@@", " ").Replace("@@EndBlockQuote@@", " ");
            comment = comment.Replace("@@LinkStart@@", "").Replace("@@LinkEnd@@", "");
            comment = comment.Replace("@@SANStart@@", "").Replace("@@SANEnd@@", "");
            comment = comment.Replace("@@HeaderStart@@", " ").Replace("@@HeaderEnd@@", " ");
            comment = findBlockTags().Replace(comment, " ");
            comment = comment.Replace("</strong>", "").Replace("<strong>", "");
            comment = comment.Replace("</bold>", "").Replace("<bold>", "");
            comment = findHtmltags().Replace(comment, "");

            // Geschweifte Klammern aus dem Chessable-Text neutralisieren: der Kommentar wird später in
            // PGN als {…} gewrappt — ein „}" im Text beendet den Kommentar vorzeitig und der Rest rutscht
            // als Müll in den Movetext (Linie/Kurs wird beim Import unlesbar). PGN kennt kein Escaping
            // innerhalb von {…}, also durch runde Klammern ersetzen (die kommen dort ohnehin vor,
            // siehe @@StartBracket@@ oben).
            comment = comment.Replace('{', '(').Replace('}', ')');

            // Eckige Klammern ebenso (S2-017): aus Kommentartext dürfen weder PGN-Tags („[Event …]",
            // „[ChessableOid …]" — rookhub zerlegt an „[Event " und liest die oid je Block) noch Kommandos
            // („[%tqu", „[%info", „[%alt", „[%cal", „[%csl") entstehen. Mehrere Teile vor einem Zug werden mit
            // Zeilenumbruch verbunden, „[Event" stünde dann sogar am Zeilenanfang. Die selbst erzeugten Marker
            // kommen erst in GeneratePGN dazu, NACH dieser Ersetzung, und bleiben unberührt.
            comment = comment.Replace('[', '(').Replace(']', ')');

            // Leerraum wie im Chessable-Export: Chessable schreibt Absätze als „ <br/><br/> ", und entfernte
            // FEN-Marker hinterlassen Leerzeichen am Rand — sonst doppelte Leerzeichen und „Zugfolge }".
            comment = findWhitespace().Replace(comment, " ").Trim();

            return comment;
        }

        [GeneratedRegex(@"<\s*/?\s*(br|p|div|li|ul|ol|blockquote|h[1-6]|tr|td|table|hr)\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex findBlockTags();

        [GeneratedRegex("<[^>]*>")]
        private static partial Regex findHtmltags();

        [GeneratedRegex(@"\s+")]
        private static partial Regex findWhitespace();

        [GeneratedRegex(@"@@StartFEN@@(.+?)@@EndFEN@@")]
        private static partial Regex findFenTags();

    }
}
