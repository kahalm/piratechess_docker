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
            ChessGame? game = TryNewGame(string.IsNullOrWhiteSpace(initial) ? StartFen : initial);
            for (int i = 0; i < moves.Count; i++)
            {
                string fromJson = afterByMoveId.TryGetValue(moves.Keys[i], out var r) ? (r.Before ?? "") : "";
                fens.Add(fromJson != "" ? fromJson : (game?.GetFen() ?? ""));

                var move = moves.Values[i];
                if (game == null) continue;
                if (IsNullSan(move.San)) { game = null; continue; }   // ab hier ist die Stellung nicht mehr sicher
                var candidates = SanCandidates(game, (move.San ?? "").Trim());
                if (candidates.Count != 1) { game = null; continue; }
                try { game.MakeMove(candidates[0], false); }
                catch { game = null; }
            }
            return fens;
        }

        /// <summary>Ausgangsstellung einer Partie — Rückfall, wenn die Linie keine eigene FEN nennt.</summary>
        private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

        /// <summary>Brett aus einer FEN, ohne zu werfen (Chessable liefert auch Muster-Diagramme ohne König).</summary>
        private static ChessGame? TryNewGame(string? fen)
        {
            try { return string.IsNullOrWhiteSpace(fen) ? new ChessGame() : new ChessGame(fen); }
            catch { return null; }
        }

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
                    var move = SanToMove(game, m.San);
                    if (move == null) { result.Add(null); break; }

                    char ff = char.ToLower(move.OriginalPosition.File.ToString()[0]);
                    int fr = move.OriginalPosition.Rank;
                    char tf = char.ToLower(move.NewPosition.File.ToString()[0]);
                    int tr = move.NewPosition.Rank;
                    string uciStr = $"{ff}{fr}{tf}{tr}";
                    int eqIdx = m.San.IndexOf('=');
                    if (eqIdx >= 0 && eqIdx + 1 < m.San.Length)
                        uciStr += char.ToLower(m.San[eqIdx + 1]);
                    result.Add(uciStr);

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
                            var move = SanToMove(game, allMoves[i].San);
                            if (move == null) return null;
                            char ff = char.ToLower(move.OriginalPosition.File.ToString()[0]);
                            int fr = move.OriginalPosition.Rank;
                            char tf = char.ToLower(move.NewPosition.File.ToString()[0]);
                            int tr = move.NewPosition.Rank;
                            string uciStr = $"{ff}{fr}{tf}{tr}";
                            int eqIdx = allMoves[i].San.IndexOf('=');
                            if (eqIdx >= 0 && eqIdx + 1 < allMoves[i].San.Length)
                                uciStr += char.ToLower(allMoves[i].San[eqIdx + 1]);
                            return uciStr;
                        }
                    }

                    // Advance the game position for all moves before the target
                    var applyMove = SanToMove(game, allMoves[i].San);
                    if (applyMove == null) return null;
                    game.MakeMove(applyMove, false);
                }
            }
            catch { }
            return null;
        }

        internal static Move? SanToMove(ChessGame game, string san)
        {
            var candidates = SanCandidates(game, san);
            return candidates.Count > 0 ? candidates[0] : null;
        }

        /// <summary>
        /// ALLE legalen Züge, die <paramref name="san"/> in dieser Stellung meinen kann. Mehr als einer heißt:
        /// die Notation ist mehrdeutig („Ne4", wenn zwei Springer nach e4 können) — <see cref="SanToMove"/>
        /// nimmt dann still den ersten, und das ist für die Hauptlinie so geblieben (Chessable erzeugt deren
        /// SAN selbst). In Autoren-Varianten steht dagegen, was der Autor tippt (gemeldet 2026-09-23).
        /// Bauernzüge und Rochaden liefern höchstens einen.
        /// </summary>
        internal static List<Move> SanCandidates(ChessGame game, string san)
        {
            string s = (san ?? string.Empty).TrimEnd('+', '#', '!', '?');
            int backRank = game.WhoseTurn == Player.White ? 1 : 8;

            if (s is "O-O" or "0-0")
                return [new Move(new Position(ChessDotNet.File.E, backRank), new Position(ChessDotNet.File.G, backRank), game.WhoseTurn)];
            if (s is "O-O-O" or "0-0-0")
                return [new Move(new Position(ChessDotNet.File.E, backRank), new Position(ChessDotNet.File.C, backRank), game.WhoseTurn)];

            char? promo = null;
            int eqIdx = s.IndexOf('=');
            if (eqIdx >= 0) { promo = eqIdx + 1 < s.Length ? s[eqIdx + 1] : (char?)null; s = s[..eqIdx]; }

            // Unzureichende/leere Notation (z. B. nach StripMoveNumber bleibt nur eine Zugnummer übrig):
            // kein gültiger Zug → null statt s[^2]-IndexOutOfRange, das sonst den ganzen Kurs-Abruf abriss.
            if (s.Length < 2) return [];

            var destFile = (ChessDotNet.File)(char.ToLower(s[^2]) - 'a');
            int destRank = s[^1] - '0';
            var validMoves = game.GetValidMoves(game.WhoseTurn);

            bool isPawn = !char.IsUpper(s[0]);
            if (isPawn)
            {
                char? srcFile = s.Length >= 4 ? s[0] : (char?)null;
                foreach (var vm in validMoves)
                {
                    if (vm.NewPosition.File != destFile || vm.NewPosition.Rank != destRank) continue;
                    if (game.GetPieceAt(vm.OriginalPosition) is not Pawn) continue;
                    if (srcFile.HasValue && char.ToLower(vm.OriginalPosition.File.ToString()[0]) != srcFile.Value) continue;
                    return [promo.HasValue
                        ? new Move(vm.OriginalPosition, vm.NewPosition, game.WhoseTurn, promo.Value)
                        : vm];
                }
                // ChessDotNet 1.0.0 listet GERADE Bauern-Push-Umwandlungen (z. B. "e8=Q") nicht in
                // GetValidMoves (Schlag-Umwandlungen schon) → der gefilterte Zug zur Zielfeld-Reihe wird
                // nicht gefunden und der ganze (Schlüssel-)Zug bliebe ohne UCI. Für die Push-Umwandlung
                // den Zug daher direkt konstruieren: Ursprung = dieselbe Datei, eine Reihe hinter dem Ziel.
                // WICHTIG: die Umwandlungsreihe muss zur ziehenden Seite passen (Weiß→8, Schwarz→1). Eine
                // farbblinde Prüfung (destRank is 1 or 8) errechnete bei Weiß+Reihe 1 / Schwarz+Reihe 8
                // eine originRank von 0 bzw. 9 → new Position(...,0/9) → GetPieceAt wirft
                // IndexOutOfRangeException und riss den ganzen Kurs-Abruf ab (kaputte Varianten-Daten).
                bool promRank = game.WhoseTurn == Player.White ? destRank == 8 : destRank == 1;
                if (promo.HasValue && !srcFile.HasValue && promRank)
                {
                    int originRank = game.WhoseTurn == Player.White ? destRank - 1 : destRank + 1;
                    var origin = new Position(destFile, originRank);
                    if (game.GetPieceAt(origin) is Pawn)
                        return [new Move(origin, new Position(destFile, destRank), game.WhoseTurn, promo.Value)];
                }
                return [];
            }

            char pieceChar = s[0];
            string mid = s.Length > 3 ? s[1..^2].Replace("x", "") : "";
            char? disambigFile = mid.Length > 0 && char.IsLetter(mid[0]) ? mid[0] : (char?)null;
            int? disambigRank = mid.Length > 0 && char.IsDigit(mid[^1]) ? mid[^1] - '0' : (int?)null;

            var found = new List<Move>();
            foreach (var vm in validMoves)
            {
                if (vm.NewPosition.File != destFile || vm.NewPosition.Rank != destRank) continue;
                var piece = game.GetPieceAt(vm.OriginalPosition);
                if (piece == null || SanPieceChar(piece) != pieceChar) continue;
                if (disambigFile.HasValue && char.ToLower(vm.OriginalPosition.File.ToString()[0]) != disambigFile.Value) continue;
                if (disambigRank.HasValue && vm.OriginalPosition.Rank != disambigRank.Value) continue;
                found.Add(vm);
            }
            return found;
        }

        private static char SanPieceChar(Piece piece) => piece switch
        {
            King => 'K',
            Queen => 'Q',
            Rook => 'R',
            Bishop => 'B',
            Knight => 'N',
            _ => 'P'
        };
    }

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

                return ReplaceCommentStuff(comment);
            }
        }

        private static string ReplaceCommentStuff(string comment)
        {
            comment = comment.Replace("@@StartBracket@@", "(").Replace("@@EndBracket@@", ")");
            comment = findFenTags().Replace(comment, "");
            comment = comment.Replace("@@StartBlockQuote@@", "").Replace("@@EndBlockQuote@@", "");
            comment = comment.Replace("@@LinkStart@@", "").Replace("@@LinkEnd@@", "");
            comment = comment.Replace("@@SANStart@@", "").Replace("@@SANEnd@@", "");
            comment = comment.Replace("@@HeaderStart@@", "").Replace("@@HeaderEnd@@", "");
            comment = comment.Replace("<br/>", "").Replace("<br>", "");
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

                return ReplaceCommentStuff(comment);
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
        {
            var result = new List<(int Anchor, string Pgn)>();
            if (Key != "V" || Val == null || Val.Value.ValueKind != JsonValueKind.Array)
                return result;

            var innerList = JsonSerializer.Deserialize<List<JsonMoveItemList>>(Val.Value, options: Options.GetOptions()) ?? [];

            // ---- Phase 1: in Cluster (Alternativlinien) zerlegen ----
            var clusters = new List<List<JsonMoveItemList>>();
            var cur = new List<JsonMoveItemList>();
            int lastOrder = int.MinValue;
            foreach (var item in innerList)
            {
                if (item.Key == "S")
                {
                    string raw = (item.Val?.ValueKind == JsonValueKind.String ? item.Val.Value.GetString() : "") ?? "";
                    int? ord = MoveOrder(raw);
                    if (ord.HasValue)
                    {
                        if (ord.Value <= lastOrder && cur.Count > 0)
                        {
                            clusters.Add(cur);
                            cur = [];
                            lastOrder = int.MinValue;
                        }
                        lastOrder = ord.Value;
                    }
                    else if (lastOrder != int.MinValue)
                    {
                        // Ein Zug OHNE Nummer setzt die Folge fort und belegt damit den nächsten Halbzug.
                        // Ohne dieses Mitzählen sah „… 3.Nc3 a6 … 3...h6" wie eine Fortsetzung aus (7 > 6),
                        // obwohl „a6" den Halbzug 7 schon belegt — der ganze Block wurde ein Kommentar.
                        lastOrder++;
                    }
                }
                cur.Add(item);
            }
            if (cur.Count > 0) clusters.Add(cur);

            // ---- Phase 2: jeden Cluster nachspielen → Variante oder Kommentar ----
            foreach (var cluster in clusters)
            {
                // Erst die Züge: eine Variante wird der Cluster nur, wenn JEDER Zug eindeutig spielbar ist.
                var raws = cluster.Where(it => it.Key == "S").Select(SanTextOf).Where(r => r != "").ToList();
                bool hasNull = raws.Any(r => r.Contains("--"));
                List<string>? moves = raws.Count > 0 && !hasNull ? ResolveLine(branchFen, raws, 0) : null;
                int anchor = -1;

                // Nicht von hier spielbar? Dann die Stellung suchen, zu der die ZUGNUMMER des ersten Zuges
                // passt (Vollzugzahl + Farbe müssen stimmen — ohne diese Bedingung würde ein zufällig
                // legaler Zug irgendwo anders angehängt).
                if (moves == null && !hasNull && raws.Count > 0 && anchorFens.Count > 0)
                {
                    int? ord = MoveOrder(raws[0]);
                    if (ord.HasValue)
                    {
                        for (int a = 0; a < anchorFens.Count; a++)
                        {
                            if (!FenHasOrder(anchorFens[a], ord.Value)) continue;
                            var tryMoves = ResolveLine(anchorFens[a], raws, 0);
                            if (tryMoves == null) continue;
                            moves = tryMoves;
                            anchor = a;
                            break;
                        }
                    }
                }

                var body = new StringBuilder();         // gültige Varianten-Notation
                var rawText = new StringBuilder();       // Fallback-Klartext (Kommentar)
                int k = 0;

                foreach (var item in cluster)
                {
                    if (item.Key == "C")
                    {
                        string c = item.CommentAfter;
                        if (c != "") { body.Append($"{{{c}}} "); AppendText(rawText, c); }
                    }
                    else if (item.Key == "V")
                    {
                        // Verschachtelte Variante → als Klartext einbetten (gültig + einfach).
                        string nested = item.FlattenToText();
                        if (nested != "") { body.Append($"{{{nested}}} "); AppendText(rawText, nested); }
                    }
                    else if (item.Key == "S")
                    {
                        string raw = SanTextOf(item);
                        if (raw == "") continue;
                        AppendText(rawText, raw);
                        if (moves != null) body.Append(moves[k] + " ");
                        k++;
                    }
                }

                if (moves != null)
                {
                    string b = body.ToString().Trim();
                    if (b != "") result.Add((anchor, $"({b})"));
                }
                else
                {
                    string t = rawText.ToString().Trim();
                    if (t != "") result.Add((-1, $"{{{t}}}"));
                }
            }
            return result;
        }

        /// <summary>Passt die Stellung zur Zugnummer eines Tokens (<see cref="MoveOrder"/>: weiß = N·2,
        /// schwarz = N·2+1)? Gelesen wird nur die FEN selbst — Vollzugzahl und Seite am Zug.</summary>
        private static bool FenHasOrder(string fen, int order)
        {
            var parts = (fen ?? "").Split(' ');
            if (parts.Length < 6) return false;
            if (!int.TryParse(parts[5], out int fullmove)) return false;
            return fullmove * 2 + (parts[1] == "b" ? 1 : 0) == order;
        }

        /// <summary>Höchstzahl Halbzüge, die <see cref="ResolveLine"/> für EINEN Varianten-Cluster nachspielt (= Rekursionstiefe).
        /// 256 Vollzüge reichen für jede Seitenlinie eines Kurses; 512 Rahmen belegen nur einen Bruchteil des
        /// Threadpool-Stacks (1,5 MB unter Linux). Längere Cluster bleiben als Kommentar vollständig erhalten.</summary>
        private const int MaxVariationPlies = 512;

        /// <summary>Zugtext eines „S"-Eintrags. Mit Zeilenumbruch oder Klammer ist er kein Zug, sondern ein Versuch,
        /// Variante/Kommentar/Partie vorzeitig zu beenden → verworfen, wie ein leerer Eintrag (S2-017).</summary>
        private static string SanTextOf(JsonMoveItemList item)
        {
            string raw = ((item.Val?.ValueKind == JsonValueKind.String ? item.Val.Value.GetString() : "") ?? "").Trim();
            return PgnTokenGuard.HasLineBreakOrBracket(raw) ? "" : raw;
        }

        /// <summary>
        /// Spielt die Züge <paramref name="raws"/>[<paramref name="i"/>..] ab <paramref name="fen"/> und liefert sie
        /// so, wie sie ins PGN gehören — oder null, wenn die Folge nicht EINDEUTIG spielbar ist.
        ///
        /// <para>Autoren schreiben in ihren Seitenlinien auch mehrdeutige Züge („16.Ne4", wenn Springer auf c3 UND
        /// c5 nach e4 können; gemeldet 2026-09-23 an „Lifetime Repertoires: King's Indian Defense - Part 2").
        /// <see cref="Game.SanToMove"/> nahm dann still den ersten Springer, und ins PGN kam ein Zug, den kein
        /// PGN-Leser spielen kann. Jetzt wird jede passende Lesart durchgespielt: trägt genau EINE die ganze Folge
        /// (nur nach 16.S3e4 geht 17.Sxb7), wird sie genommen und eindeutig geschrieben („16.N3e4"); tragen
        /// mehrere oder keine, wird der Cluster Kommentar — geraten wird nicht.</para>
        ///
        /// <para>Rekursiv je Zug: die Tiefe ist die Clusterlänge, und die kommt ungedeckelt aus den Daten (ein
        /// Cluster endet nur an einem Zugnummer-Rücksprung). Ein StackOverflow ist in .NET nicht fangbar und
        /// beendet den ganzen Prozess samt laufender Abrufe — deshalb wird ein Cluster mit mehr als
        /// <see cref="MaxVariationPlies"/> Zügen nicht nachgespielt, sondern Kommentar (Review 2026-09-29, N3-001).</para>
        /// </summary>
        private static List<string>? ResolveLine(string? fen, List<string> raws, int i)
        {
            if (raws.Count > MaxVariationPlies) return null;
            if (i >= raws.Count) return [];
            ChessGame? game = TryNewGame(fen);
            if (game == null) return null;

            string raw = raws[i];
            // Nur echte SAN wird Varianten-Zug: SanCandidates liest „N;f3" als Springerzug nach f3, im Movetext
            // beginnt „;" aber einen Zeilenkommentar. Alles andere bleibt als Kommentar erhalten (S2-017).
            string san = StripMoveNumber(raw);
            if (!PgnTokenGuard.IsSan(san)) return null;
            var candidates = Game.SanCandidates(game, san);
            List<string>? rest = null;
            Move? chosen = null;
            foreach (var candidate in candidates)
            {
                ChessGame? next = TryNewGame(game.GetFen());
                if (next == null) continue;
                try { next.MakeMove(candidate, false); }
                catch { continue; }
                var tail = ResolveLine(next.GetFen(), raws, i + 1);
                if (tail == null) continue;
                if (rest != null) return null;   // zwei Lesarten tragen die ganze Folge → mehrdeutig
                rest = tail;
                chosen = candidate;
            }
            if (rest == null || chosen == null) return null;
            return [candidates.Count > 1 ? Disambiguate(raw, chosen, candidates) : raw, .. rest];
        }

        /// <summary>
        /// Schreibt einen mehrdeutigen Figurenzug eindeutig (SAN-Regel: erst die Linie, dann die Reihe, sonst
        /// beides): „16.Ne4" mit Springern auf c3/c5 → „16.N3e4". Zugnummer, Schlagzeichen und Suffix bleiben.
        /// </summary>
        private static string Disambiguate(string raw, Move chosen, List<Move> candidates)
        {
            var number = findLeadingMoveNumber().Match(raw);
            string prefix = number.Success ? raw[..number.Length] : "";
            string san = raw[prefix.Length..];
            var target = findTargetSquare().Match(san);
            if (san.Length == 0 || !char.IsUpper(san[0]) || !target.Success) return raw;

            char file = FileOf(chosen.OriginalPosition);
            int rank = chosen.OriginalPosition.Rank;
            var others = candidates
                .Where(c => FileOf(c.OriginalPosition) != file || c.OriginalPosition.Rank != rank)
                .ToList();
            string which = others.All(o => FileOf(o.OriginalPosition) != file) ? file.ToString()
                : others.All(o => o.OriginalPosition.Rank != rank) ? rank.ToString()
                : $"{file}{rank}";
            string capture = san[..target.Index].Contains('x') ? "x" : "";
            return prefix + san[0] + which + capture + san[target.Index..];
        }

        private static char FileOf(Position p) => char.ToLower(p.File.ToString()[0]);

        /// <summary>Flacht eine (verschachtelte) „V"-Struktur rein zu Text ab (Züge + Kommentare, ohne Klammern/FEN-Bezug).</summary>
        private string FlattenToText()
        {
            if (Val == null || Val.Value.ValueKind != JsonValueKind.Array) return "";
            var list = JsonSerializer.Deserialize<List<JsonMoveItemList>>(Val.Value, options: Options.GetOptions()) ?? [];
            var sb = new StringBuilder();
            foreach (var it in list)
            {
                if (it.Key == "S")
                {
                    string s = SanTextOf(it);
                    if (s != "") AppendText(sb, s);
                }
                else if (it.Key == "C") { string c = it.CommentAfter; if (c != "") AppendText(sb, c); }
                else if (it.Key == "V") { string n = it.FlattenToText(); if (n != "") AppendText(sb, n); }
            }
            return sb.ToString().Trim();
        }

        private static ChessGame? TryNewGame(string? fen)
        {
            try { return string.IsNullOrWhiteSpace(fen) ? new ChessGame() : new ChessGame(fen); }
            catch { return null; }
        }

        /// <summary>Sortierschlüssel eines „N." / „N..."-Zugtokens (weiß = N·2, schwarz = N·2+1); null bei bloßer SAN.</summary>
        private static int? MoveOrder(string raw)
        {
            var mw = findWhiteMoveNumber().Match(raw);
            if (mw.Success) return int.Parse(mw.Groups[1].Value) * 2;
            var mb = findBlackMoveNumber().Match(raw);
            if (mb.Success) return int.Parse(mb.Groups[1].Value) * 2 + 1;
            return null;
        }

        /// <summary>Entfernt die führende Zugnummer („12." / „12...") aus einem Token; lässt bloße SAN unberührt.</summary>
        private static string StripMoveNumber(string raw)
        {
            var m = findLeadingMoveNumber().Match(raw);
            return m.Success ? raw[m.Length..].Trim() : raw.Trim();
        }

        private static void AppendText(StringBuilder sb, string s)
        {
            // Kein Leerzeichen vor einem Satzzeichen: der Klartext eines Clusters entsteht aus Zügen und
            // Textstücken, und ein Stück, das mit „." oder „," beginnt, gehört an das Wort davor
            // („1.d4 . And maybe this is true." → „1.d4. And maybe this is true.").
            if (sb.Length > 0 && !(s.Length > 0 && ",.;:!?)".Contains(s[0]))) sb.Append(' ');
            sb.Append(s);
        }

        [GeneratedRegex(@"^(\d+)\.(?!\.)")]
        private static partial Regex findWhiteMoveNumber();

        [GeneratedRegex(@"^(\d+)\.\.\.")]
        private static partial Regex findBlackMoveNumber();

        [GeneratedRegex(@"^\d+\.(\.\.)?\s*")]
        private static partial Regex findLeadingMoveNumber();

        /// <summary>Das ZIELfeld eines SAN-Zugs = das letzte Feld darin („N3xe4+" → e4).</summary>
        [GeneratedRegex(@"[a-h][1-8]", RegexOptions.RightToLeft)]
        private static partial Regex findTargetSquare();

        [GeneratedRegex("<[^>]*>")]
        private static partial Regex findHtmltags();

        [GeneratedRegex(@"\s+")]
        private static partial Regex findWhitespace();

        [GeneratedRegex(@"@@StartFEN@@(.+?)@@EndFEN@@")]
        private static partial Regex findFenTags();

    }
}
