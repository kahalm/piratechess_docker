using ChessDotNet;
using ChessDotNet.Pieces;
using RestSharp;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace piratechess_lib
{

    public class Game
    {
        public bool Owned { get; set; }
        public List<JsonMove> Data { get; set; } = [];
        public string Initial { get; set; } = string.Empty;
        public string Color { get; set; } = string.Empty;
        public int IsInfo { get; set; }
        /// <summary>Chessable „softFail" — geduldete Alternativzüge je Vollzug/Seite (siehe SoftFailEntry).</summary>
        public List<SoftFailEntry>? SoftFail { get; set; }
        /// <summary>Anzahl kollidierter Move-Ids beim letzten <see cref="GeneratePGN"/>-Lauf (korrupte
        /// Chessable-Daten; letzter Zug je Id gewinnt). >0 ⇒ dem PGN können echte Züge fehlen — der
        /// Aufrufer (PirateChessLib.GetLine) meldet das via RecordError/Diag, damit die Korruption
        /// nicht als sauberer Export durchrutscht.</summary>
        public int DuplicateMoveIds { get; private set; }
        /// <summary>
        /// Stellung VOR jedem Zug der Linie — die Ankerpunkte für Varianten (siehe
        /// <see cref="JsonMoveItemList.GetVariationParts"/>). Chessable liefert sie im „before" des Zuges;
        /// fehlt sie (erster Zug einer Linie hat oft gar kein „after"-Objekt), wird sie aus der
        /// Ausgangsstellung nachgespielt. Was sich nicht nachspielen lässt, bleibt leer — dann gibt es für
        /// diesen Zug eben keinen Ersatz-Anker.
        /// </summary>
        private static List<string> MainlineFens(SortedList<int, JsonMove> moves, Dictionary<int, ResponseMove> afterByMoveId, string? initial)
        {
            var fens = new List<string>(moves.Count);
            ChessGame? game = SanResolver.TryNewGame(string.IsNullOrWhiteSpace(initial) ? StartFen : initial);
            for (int i = 0; i < moves.Count; i++)
            {
                string fromJson = afterByMoveId.TryGetValue(moves.Keys[i], out var r) ? (r.Before ?? "") : "";
                fens.Add(fromJson != "" ? fromJson : (game?.GetFen() ?? ""));

                var move = moves.Values[i];
                if (game == null) continue;
                if (IsNullSan(move.San)) { game = null; continue; }   // ab hier ist die Stellung nicht mehr sicher
                var candidates = SanResolver.SanCandidates(game, (move.San ?? "").Trim());
                if (candidates.Count != 1) { game = null; continue; }
                try { game.MakeMove(candidates[0], false); }
                catch { game = null; }
            }
            return fens;
        }

        /// <summary>Ausgangsstellung einer Partie — Rückfall, wenn die Linie keine eigene FEN nennt.</summary>
        private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

        /// <summary>Chessables Platzhalter für „kein Zug" (Einleitungs-/Erklärlinien) — auch ein leeres San.</summary>
        private static bool IsNullSan(string? san) => (san ?? "").Trim() is "" or "--";

        /// <summary>Zwei Kommentare ohne Zug dazwischen („} {") — siehe <see cref="GeneratePGN"/>.</summary>
        private static readonly Regex CommentGap = new(@"\}\s*\{", RegexOptions.Compiled);

        public string GeneratePGN(bool allKeyMovesTraining = false, bool noTrainingMove = false)
        {
            string pgn = "";
            SortedList<int, JsonMove> sortedMoves = [];
            var afterByMoveId = new Dictionary<int, ResponseMove>();
            Data ??= [];
            DuplicateMoveIds = 0;
            foreach (JsonMove move in Data)
            {
                // Indexer statt Add: doppelte Move-Ids (korrupte Chessable-Daten) überschreiben statt
                // eine ArgumentException zu werfen, die den ganzen Kurs-Export abriss — aber zählen,
                // damit der stille Zugverlust beim Aufrufer sichtbar wird (DuplicateMoveIds).
                if (sortedMoves.ContainsKey(move.Id)) DuplicateMoveIds++;
                sortedMoves[move.Id] = move;

                if (move.After is not null and not "")
                {
                    ResponseMove? responseMoveAfter = JsonSerializer.Deserialize<ResponseMove>(move.After, options: Options.GetOptions());
                    if (responseMoveAfter != null && responseMoveAfter.Data != null)
                    {
                        afterByMoveId[move.Id] = responseMoveAfter;
                        move.CommentAfter = string.Join(" ", responseMoveAfter.Data
                            .Where(d => d.Key == "C")
                            .Select(d => d.CommentAfter)
                            .Where(c => c != ""));
                    }
                }

                if (move.Before is not null and not "")
                {
                    ResponseMove? responseMoveBefore = JsonSerializer.Deserialize<ResponseMove>(move.Before, options: Options.GetOptions());
                    if (responseMoveBefore != null && responseMoveBefore.Data != null)
                    {
                        move.CommentBefore = string.Join(Environment.NewLine, responseMoveBefore.Data.Select(x => x.CommentBefore).ToList());
                    }
                }
            }
            if (IsInfo == 1) noTrainingMove = true;
            if (!noTrainingMove && allKeyMovesTraining)
            {
                var allUcis = GetAllTrainingUcis(sortedMoves);
                bool prevKey = false;
                bool pastFirstKey = false;
                int moveIdx = 0;
                var fenParts = (Initial ?? "").Split(' ');
                bool currentIsWhite = fenParts.Length <= 1 || fenParts[1] != "b";
                bool? solverIsWhite = !string.IsNullOrEmpty(Color)
                    ? Color.Equals("white", StringComparison.OrdinalIgnoreCase)
                    : null;
                foreach (JsonMove move in sortedMoves.Values)
                {
                    if (move.IsKey && !prevKey)
                    {
                        pastFirstKey = true;
                        solverIsWhite ??= currentIsWhite;
                    }
                    if (pastFirstKey && move.IsKey && solverIsWhite == currentIsWhite)
                    {
                        string uci = moveIdx < allUcis.Count ? (allUcis[moveIdx] ?? "") : "";
                        string trainingComment = $"[%tqu \"En\",\"find the move\",\"\",\"\",\"{uci}\",\"\",10]";
                        move.CommentBefore = move.CommentBefore == ""
                            ? trainingComment
                            : trainingComment + "\n" + move.CommentBefore;
                    }
                    prevKey = move.IsKey;
                    currentIsWhite = !currentIsWhite;
                    moveIdx++;
                }
            }
            else if (!noTrainingMove)
            {
                bool? solverIsWhite = !string.IsNullOrEmpty(Color)
                    ? Color.Equals("white", StringComparison.OrdinalIgnoreCase)
                    : null;
                string? uci = GetFirstKeyMoveUci(sortedMoves, solverIsWhite);
                var fenParts = (Initial ?? "").Split(' ');
                bool currentIsWhite = fenParts.Length <= 1 || fenParts[1] != "b";
                bool foundKeyBlock = false;
                foreach (JsonMove move in sortedMoves.Values)
                {
                    if (move.IsKey && !foundKeyBlock)
                        foundKeyBlock = true;
                    if (foundKeyBlock && move.IsKey && (solverIsWhite == null || solverIsWhite.Value == currentIsWhite))
                    {
                        string trainingComment = $"[%tqu \"En\",\"find the move\",\"\",\"\",\"{uci ?? ""}\",\"\",10]";
                        move.CommentBefore = move.CommentBefore == ""
                            ? trainingComment
                            : trainingComment + "\n" + move.CommentBefore;
                        break;
                    }
                    currentIsWhite = !currentIsWhite;
                }
            }

            // Info-/Erklärlinie (Chessable IsInfo==1): expliziten [%info]-Marker in den Kommentar
            // VOR dem ersten Zug setzen, damit der rookhub-Import diese Linie als „IsInfoOnly" erkennt
            // (kein Quiz; aus Random-/Tagespuzzle-Töpfen ausgeblendet; sequenziell nur zum Durchklicken).
            // Der rookhub-Parser scannt den Movetext nach "[%info" (analog zu [%tqu]); die [%…]-Annotation
            // wird bei der Kommentaranzeige ohnehin herausgefiltert, verfälscht den Text also nicht.
            if (IsInfo == 1 && sortedMoves.Count > 0)
            {
                var firstMove = sortedMoves.Values.First();
                firstMove.CommentBefore = firstMove.CommentBefore == ""
                    ? "[%info]"
                    : "[%info]\n" + firstMove.CommentBefore;
            }

            // Varianten ERST JETZT, wenn alle Stellungen der Linie bekannt sind: ein Cluster, der von
            // seinem Elternzug aus nicht spielbar ist, hängt sich an die Stellung, zu der seine Zugnummer
            // passt (siehe GetVariationParts). Chessable hängt die Verweis-Linien einer Einleitung an den
            // Null-Zug am Ende — von dort unspielbar, ab Zug 1 aber ganz normale Varianten.
            var anchorFens = MainlineFens(sortedMoves, afterByMoveId, Initial);
            var variationsAt = anchorFens.Select(_ => new List<string>()).ToList();
            for (int i = 0; i < sortedMoves.Count; i++)
            {
                if (!afterByMoveId.TryGetValue(sortedMoves.Keys[i], out var resp) || resp.Data == null) continue;
                foreach (var data in resp.Data.Where(d => d.Key == "V"))
                {
                    foreach (var (anchor, part) in data.GetVariationParts(resp.Before ?? "", anchorFens))
                    {
                        if (part == "") continue;
                        variationsAt[anchor >= 0 && anchor < variationsAt.Count ? anchor : i].Add(part);
                    }
                }
            }
            for (int i = 0; i < sortedMoves.Count; i++)
                sortedMoves.Values[i].CommentVariations = string.Join(" ", variationsAt[i]);

            int lastMove = 0;
            // Vollzugnummer des Linienbeginns — softFail ist ab da 0-basiert indiziert.
            int firstMoveNum = sortedMoves.Count > 0 ? sortedMoves.Values.First().Move : 1;
            // Zugnummern wie im Chessable-Export: „N." vor Weiß, „N..." vor Schwarz, wenn Schwarz die Linie
            // beginnt oder direkt nach Varianten zieht (sonst ordnet ein strenger PGN-Leser den Zug nicht zu).
            var initialParts = (Initial ?? "").Split(' ');
            bool blackStarts = initialParts.Length > 1 && initialParts[1] == "b";
            // Chessables NULL-ZUG („--") steht am ENDE von Einleitungs-/Erklärlinien, wo kein Zug mehr folgt.
            // Er wird nicht ausgegeben: chess.js — der PGN-Leser von RookHubs Viewer, Zugliste und
            // Repertoire-Ansicht — kennt ihn nicht und verwirft damit die GANZE Partie stillschweigend
            // (auf Dev 85 von 1715 Linien). Seine Kommentare bleiben erhalten und hängen am Zug davor.
            // Nur am Ende: folgt noch ein echter Zug, wäre die Zugfolge ohne Platzhalter falsch, dann bleibt
            // es beim „--" (in echten Daten nie vorgekommen).
            var trailingNullIds = new HashSet<int>();
            for (int i = sortedMoves.Count - 1; i >= 0; i--)
            {
                if (!IsNullSan(sortedMoves.Values[i].San)) break;
                trailingNullIds.Add(sortedMoves.Keys[i]);
            }

            bool afterVariations = false;
            foreach (var moveEntry in sortedMoves)
            {
                JsonMove move = moveEntry.Value;
                bool nullMove = trailingNullIds.Contains(moveEntry.Key);

                if (move.CommentBefore != "")
                {
                    pgn += $"{{{move.CommentBefore}}} ";
                }

                if (!nullMove)
                {
                    if (lastMove < move.Move)
                    {
                        pgn += lastMove == 0 && blackStarts ? $"{move.Move}... " : $"{move.Move}. ";
                    }
                    else if (afterVariations)
                    {
                        pgn += $"{move.Move}... ";
                    }
                    // San geht roh in den Movetext: was kein SAN-Zug ist (Zeilenumbruch + „[Event …]" aus einer
                    // vergifteten Cache-Linie), verwirft die ganze Linie — GetLine überspringt und meldet sie (S2-017).
                    if (!string.IsNullOrEmpty(move.San) && !PgnTokenGuard.IsSan(move.San))
                        throw new FormatException($"Zug-Id {move.Id}: San ist kein SAN-Zug — Linie verworfen (korrupte oder manipulierte Daten)");
                    pgn += move.San + " ";
                }

                // Chessable kann "draws": null bzw. einzelne null-Eintraege liefern; das Property-Pattern
                // filtert null-Elemente mit aus (NullRef in GeneratePGN, bid 282212). Farbe/Felder gehen roh in
                // [%cal]/[%csl] — nur Farbbuchstaben und echte Felder, sonst entfällt der Eintrag (S2-017).
                var arrowList = move.Draws?.Where(x => x is { Object: "arrow" } && PgnTokenGuard.IsDrawColor(x.Color)
                    && PgnTokenGuard.IsSquare(x.Start) && PgnTokenGuard.IsSquare(x.End)).ToList() ?? [];
                var circleList = move.Draws?.Where(x => x is { Object: "circle" } && PgnTokenGuard.IsDrawColor(x.Color)
                    && PgnTokenGuard.IsSquare(x.Start)).ToList() ?? [];

                string annotation = "";

                if (arrowList.Count > 0)
                {
                    annotation += "[%cal ";
                    var firstrun = true;
                    foreach (JsonDraw draw in arrowList)
                    {
                        annotation += $"{(firstrun ? "" : ",")}{(draw.Color ?? "").ToUpper()}{draw.Start}{draw.End}";
                        firstrun = false;
                    }
                    annotation += "]";
                }

                if (circleList.Count > 0)
                {
                    annotation += "[%csl ";
                    var firstrun = true;
                    foreach (JsonDraw draw in circleList)
                    {
                        annotation += $"{(firstrun ? "" : ",")}{(draw.Color ?? "").ToUpper()}{draw.Start}";
                        firstrun = false;
                    }
                    annotation += "]";
                }

                // Geduldete Alternativzüge (Chessable softFail) als [%alt …] — der Repertoire-Trainer
                // akzeptiert sie, verlangt aber trotzdem den Hauptzug. softFail listet Hauptzug +
                // Alternativen; den gespielten Zug selbst ziehen wir ab.
                if (SoftFail != null)
                {
                    int sfIdx = move.Move - firstMoveNum;
                    if (sfIdx >= 0 && sfIdx < SoftFail.Count && SoftFail[sfIdx] != null)
                    {
                        var accepted = move.Col == "w" ? SoftFail[sfIdx].W : SoftFail[sfIdx].B;
                        if (accepted != null)
                        {
                            // Nur SAN-Züge — ein fremder Eintrag entfällt, die Linie bleibt (S2-017).
                            var alts = accepted
                                .Where(a => PgnTokenGuard.IsSan(a) && a != move.San)
                                .Distinct()
                                .ToList();
                            if (alts.Count > 0)
                                annotation += $"[%alt {string.Join(" ", alts)}]";
                        }
                    }
                }

                if (move.CommentAfter != "")
                {
                    annotation += move.CommentAfter;
                }

                if (annotation != "")
                {
                    pgn += $"{{{annotation}}} ";
                }

                // Varianten direkt NACH ihrem eigenen Zug ausgeben (sie sind Alternativen zu IHM),
                // nicht verzögert nach dem Folgezug — sonst hängt der PGN-Leser sie an den falschen Zug.
                if (move.CommentVariations != "")
                {
                    pgn += move.CommentVariations + " ";
                }
                afterVariations = move.CommentVariations != "";

                if (!nullMove) lastMove = move.Move;
            }

            // Zwei Kommentare direkt hintereinander zu EINEM zusammenfassen: chess.js lehnt „{a} {b}" ab und
            // verwirft die Partie. Sie entstehen, wenn aus einer nicht spielbaren Variante ein Kommentar wird
            // (GetVariationPgn) oder wenn hinter dem letzten Zug mehrere Blöcke zusammenkommen. Ein „}" kann
            // nicht aus dem Chessable-Text stammen (ReplaceCommentStuff ersetzt geschweifte Klammern), die
            // Fundstelle ist also eindeutig.
            pgn = CommentGap.Replace(pgn, " ");
            return pgn;
        }

        private List<string?> GetAllTrainingUcis(SortedList<int, JsonMove> sortedMoves)
        {
            var result = new List<string?>(sortedMoves.Count);
            try
            {
                ChessGame game = string.IsNullOrEmpty(Initial)
                    ? new ChessGame()
                    : new ChessGame(Initial);

                foreach (var m in sortedMoves.Values)
                {
                    var move = SanResolver.SanToMove(game, m.San);
                    if (move == null) { result.Add(null); break; }

                    result.Add(SanResolver.ToUci(move, m.San));

                    game.MakeMove(move, false);
                }
            }
            catch { }
            while (result.Count < sortedMoves.Count)
                result.Add(null);
            return result;
        }

        private string? GetFirstKeyMoveUci(SortedList<int, JsonMove> sortedMoves, bool? solverIsWhite)
        {
            try
            {
                ChessGame game = string.IsNullOrEmpty(Initial)
                    ? new ChessGame()
                    : new ChessGame(Initial);

                var allMoves = sortedMoves.Values.ToList();
                bool foundKeyBlock = false;

                for (int i = 0; i < allMoves.Count; i++)
                {
                    if (allMoves[i].IsKey && !foundKeyBlock)
                        foundKeyBlock = true;

                    if (foundKeyBlock && allMoves[i].IsKey)
                    {
                        bool isWhiteTurn = game.WhoseTurn == Player.White;
                        if (solverIsWhite == null || solverIsWhite.Value == isWhiteTurn)
                        {
                            var move = SanResolver.SanToMove(game, allMoves[i].San);
                            if (move == null) return null;
                            return SanResolver.ToUci(move, allMoves[i].San);
                        }
                    }

                    // Advance the game position for all moves before the target
                    var applyMove = SanResolver.SanToMove(game, allMoves[i].San);
                    if (applyMove == null) return null;
                    game.MakeMove(applyMove, false);
                }
            }
            catch { }
            return null;
        }
    }
}
