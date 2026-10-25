using System.Text.Json;

namespace piratechess_lib
{
    // Chessable-Kommentar-/Varianten-Eintrag (Key C/S/V); Varianten-Auflösung in Pgn/VariationResolver, Kommentar-
    // Bereinigung in Pgn/CommentText (aus Models.cs herausgelöst, Zerlegung Z48).
    public partial class JsonMoveItemList
    {
        public string State { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public JsonElement? Val { get; set; } //entweder eine Liste von itemList oder ein string.
        public string CommentAfter
        {
            get
            {
                string comment = "";
                if (Val == null)
                {
                    return "";
                }
                if (Val.Value.ValueKind == JsonValueKind.String)
                {
                    comment = Val.ToString() ?? "";
                }
                else
                if (Val.Value.ValueKind == JsonValueKind.Array)
                {
                    List<JsonMoveItemList> innerList = JsonSerializer.Deserialize<List<JsonMoveItemList>>(Val.Value, options: Options.GetOptions())?.ToList() ?? new List<JsonMoveItemList>() ;

                    // Teile EINES Eintrags (Text, Zugverweis, Text …) sind fortlaufender Text → Leerzeichen,
                    // kein Zeilenumbruch (sonst stand z. B. „… dass er\n2.d4\nspielen kann" im Kommentar).
                    comment = string.Join(" ", innerList.Select(x => x.CommentAfter) ?? [""]);
                }
                else
                {
                    return "";
                }

                return CommentText.Clean(comment);
            }
        }

        public string CommentBefore
        {
            get
            {
                string comment = "";
                if (Val == null)
                {
                    return "";
                }
                if (Val.Value.ValueKind == JsonValueKind.String)
                {
                    comment = Val.ToString() ?? "";
                }
                else
                if (Val.Value.ValueKind == JsonValueKind.Array)
                {
                    List<string>? innerList = JsonSerializer.Deserialize<List<JsonMoveItemList>>(Val.Value, options: Options.GetOptions())?.Select(x => x.CommentAfter).ToList();

                    comment = string.Join(" ", innerList ?? [""]);
                }
                else
                {
                    return "";
                }

                return CommentText.Clean(comment);
            }
        }

        /// <summary>
        /// Wandelt die Chessable-„V"-Daten eines Zuges in PGN um. Chessables „V" enthält ZWEI Sorten:
        /// (a) echte Seitenlinien, die am Elternzug abzweigen und legal nachspielbar sind, und
        /// (b) Transpositions-/Verweis-Notizen mit absoluten Zugnummern ab Zug 1, die NICHT von hier
        /// fortsetzen. Früher wurden beide blind als <c>(…)</c> ausgegeben → ungültiges, nicht
        /// nachspielbares PGN (Duplikate, fremde Zugnummern, Nullzüge „--").
        ///
        /// Jetzt zweistufig: die Items werden an Zugnummern-Rücksprüngen in Cluster (einzelne
        /// Alternativlinien) zerlegt, und JEDER Cluster wird ab der Elternstellung (<paramref name="branchFen"/>
        /// = Stellung VOR dem Elternzug) mit der Engine nachgespielt. Spielt er legal durch → echte
        /// <c>(…)</c>-Variante; sonst (illegaler Zug / Nullzug / unbekannte FEN) → als <c>{Kommentar}</c>
        /// ausgegeben, damit das PGN gültig bleibt und der Inhalt erhalten bleibt.
        /// </summary>
        public string GetVariationPgn(string branchFen) =>
            string.Join(" ", GetVariationParts(branchFen, []).Select(p => p.Pgn));

        /// <summary>
        /// Wie <see cref="GetVariationPgn(string)"/>, erlaubt aber ERSATZ-ANKER: Ist ein Cluster von der
        /// Elternstellung aus nicht spielbar, wird die Stellung gesucht, zu der seine ZUGNUMMER passt
        /// (<paramref name="anchorFens"/>[i] = Stellung vor Zug i der Linie). Das sind die
        /// Transpositions-/Verweis-Notizen, die Chessable an den Null-Zug am Ende einer Einleitungslinie
        /// hängt: von dort aus unspielbar, ab der Stellung ihrer Zugnummer aber ganz normale Varianten.
        /// Ohne das landete der halbe Einleitungstext als Kommentar im PGN — in ChessBase nicht anklickbar.
        /// <para>Rückgabe je Cluster: <c>Anchor</c> = Index in <paramref name="anchorFens"/>, an dem die
        /// Variante hängen MUSS, oder -1 für die Elternstellung (auch bei der Kommentar-Notlösung).</para>
        /// </summary>
        public List<(int Anchor, string Pgn)> GetVariationParts(string branchFen, IReadOnlyList<string> anchorFens)
            => VariationResolver.GetParts(this, branchFen, anchorFens);
    }
}
