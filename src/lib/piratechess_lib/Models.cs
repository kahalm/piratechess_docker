using ChessDotNet;
using ChessDotNet.Pieces;
using RestSharp;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace piratechess_lib
{

    public class ResponseCourse
    {
        public Course Course { get; set; } = new Course();
    }
    public class Course
    {
        public List<Chapter> Data { get; set; } = [];
    }
    public class Chapter
    {
        public int Id { get; set; }
        /// <summary>Anzahl Varianten des Kapitels (Chessable-Feld "total"). Nur gefüllt, wenn der
        /// getCourse-Abruf mit includeVariations=true erfolgte; sonst 0.</summary>
        public int Total { get; set; }
        /// <summary>Varianten des Kapitels (oid/Typ) — nur bei includeVariations=true. Summe der
        /// Counts über alle Kapitel = Gesamt-Linienzahl des Kurses (= Zahl der getGame-Abrufe).</summary>
        public List<ChapterVariation> Variations { get; set; } = [];
    }
    public class ChapterVariation
    {
        public long Oid { get; set; }
        public string Type { get; set; } = string.Empty;
    }
    public class ResponseLine
    {
        public Game Game { get; set; } = new Game();
    }
    public class ResponseChapter
    {
        public ResponseList List { get; set; } = new ResponseList();
    }
    public class ResponseList
    {
        public string Name { get; set; } = string.Empty;
        public List<Line> Data { get; set; } = [];
        public string Title { get; set; } = string.Empty;
    }

    public class ResponseMove
    {
        public string Before { get; set; } = string.Empty;
        public string After { get; set; } = string.Empty;
        public List<JsonMoveItemList> Data { get; set; } = [];
    }
    public class Line
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
    /// <summary>Pro Vollzug (index = Vollzugnummer ab Linienbeginn) die von Chessable geduldeten
    /// Züge je Seite. Enthält den Hauptzug PLUS die akzeptierten Alternativen (gemeinsame Stellung).
    /// W/B können null sein, wenn die Seite an diesem Zug nicht trainiert wird.</summary>
    public class SoftFailEntry
    {
        public List<string>? W { get; set; }
        public List<string>? B { get; set; }
    }
    public class Game
    {
        public bool Owned { get; set; }
        public List<JsonMove> Data { get; set; } = [];
        public string Initial { get; set; } = string.Empty;
        public string Color { get; set; } = string.Empty;
        public int IsInfo { get; set; }
        /// <summary>Chessable „softFail" — geduldete Alternativzüge je Vollzug/Seite (siehe SoftFailEntry).</summary>
        public List<SoftFailEntry>? SoftFail { get; set; }
        public string GeneratePGN(bool allKeyMovesTraining = false, bool noTrainingMove = false)
        {
            string pgn = "";
            SortedList<int, JsonMove> sortedMoves = [];
            Data ??= [];
            foreach (JsonMove move in Data)
            {
                sortedMoves.Add(move.Id, move);

                if (move.After is not null and not "")
                {
                    ResponseMove? responseMoveAfter = JsonSerializer.Deserialize<ResponseMove>(move.After, options: Options.GetOptions());
                    if (responseMoveAfter != null && responseMoveAfter.Data != null)
                    {
                        var comments = new List<string>();
                        var variations = new List<string>();
                        foreach (var data in responseMoveAfter.Data)
                        {
                            if (data.Key == "C")
                            {
                                string c = data.CommentAfter;
                                if (c != "") comments.Add(c);
                            }
                            else if (data.Key == "V")
                            {
                                // Anknüpfpunkt der Variante = Stellung VOR diesem Zug (Alternative zu ihm).
                                string v = data.GetVariationPgn(responseMoveAfter.Before);
                                if (v != "") variations.Add(v);
                            }
                        }
                        move.CommentAfter = string.Join(" ", comments);
                        move.CommentVariations = string.Join(" ", variations);
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

            int lastMove = 0;
            // Vollzugnummer des Linienbeginns — softFail ist ab da 0-basiert indiziert.
            int firstMoveNum = sortedMoves.Count > 0 ? sortedMoves.Values.First().Move : 1;
            foreach (JsonMove move in sortedMoves.Values)
            {
                if (move.CommentBefore != "")
                {
                    pgn += $"{{{move.CommentBefore}}} ";
                }

                if (lastMove < move.Move)
                {
                    pgn += $"{move.Move}. ";
                }
                pgn += move.San + " ";

                // Chessable kann "draws": null bzw. einzelne null-Eintraege liefern; das Property-Pattern
                // filtert null-Elemente mit aus (NullRef in GeneratePGN, bid 282212).
                var arrowList = move.Draws?.Where(x => x is { Object: "arrow" }).ToList() ?? [];
                var circleList = move.Draws?.Where(x => x is { Object: "circle" }).ToList() ?? [];

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
                            var alts = accepted
                                .Where(a => !string.IsNullOrWhiteSpace(a) && a != move.San)
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

                lastMove = move.Move;
            }
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
            string s = (san ?? string.Empty).TrimEnd('+', '#', '!', '?');
            int backRank = game.WhoseTurn == Player.White ? 1 : 8;

            if (s is "O-O" or "0-0")
                return new Move(new Position(ChessDotNet.File.E, backRank), new Position(ChessDotNet.File.G, backRank), game.WhoseTurn);
            if (s is "O-O-O" or "0-0-0")
                return new Move(new Position(ChessDotNet.File.E, backRank), new Position(ChessDotNet.File.C, backRank), game.WhoseTurn);

            char? promo = null;
            int eqIdx = s.IndexOf('=');
            if (eqIdx >= 0) { promo = eqIdx + 1 < s.Length ? s[eqIdx + 1] : (char?)null; s = s[..eqIdx]; }

            // Unzureichende/leere Notation (z. B. nach StripMoveNumber bleibt nur eine Zugnummer übrig):
            // kein gültiger Zug → null statt s[^2]-IndexOutOfRange, das sonst den ganzen Kurs-Abruf abriss.
            if (s.Length < 2) return null;

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
                    return promo.HasValue
                        ? new Move(vm.OriginalPosition, vm.NewPosition, game.WhoseTurn, promo.Value)
                        : vm;
                }
                // ChessDotNet 1.0.0 listet GERADE Bauern-Push-Umwandlungen (z. B. "e8=Q") nicht in
                // GetValidMoves (Schlag-Umwandlungen schon) → der gefilterte Zug zur Zielfeld-Reihe wird
                // nicht gefunden und der ganze (Schlüssel-)Zug bliebe ohne UCI. Für die Push-Umwandlung
                // den Zug daher direkt konstruieren: Ursprung = dieselbe Datei, eine Reihe hinter dem Ziel.
                if (promo.HasValue && !srcFile.HasValue && destRank is 1 or 8)
                {
                    int originRank = game.WhoseTurn == Player.White ? destRank - 1 : destRank + 1;
                    var origin = new Position(destFile, originRank);
                    if (game.GetPieceAt(origin) is Pawn)
                        return new Move(origin, new Position(destFile, destRank), game.WhoseTurn, promo.Value);
                }
                return null;
            }

            char pieceChar = s[0];
            string mid = s.Length > 3 ? s[1..^2].Replace("x", "") : "";
            char? disambigFile = mid.Length > 0 && char.IsLetter(mid[0]) ? mid[0] : (char?)null;
            int? disambigRank = mid.Length > 0 && char.IsDigit(mid[^1]) ? mid[^1] - '0' : (int?)null;

            foreach (var vm in validMoves)
            {
                if (vm.NewPosition.File != destFile || vm.NewPosition.Rank != destRank) continue;
                var piece = game.GetPieceAt(vm.OriginalPosition);
                if (piece == null || SanPieceChar(piece) != pieceChar) continue;
                if (disambigFile.HasValue && char.ToLower(vm.OriginalPosition.File.ToString()[0]) != disambigFile.Value) continue;
                if (disambigRank.HasValue && vm.OriginalPosition.Rank != disambigRank.Value) continue;
                return vm;
            }
            return null;
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
    public class JsonMove
    {
        public int Id { get; set; }
        public int Move { get; set; }
        /// <summary>Ziehende Seite: "w" oder "b" (Chessable-Feld „col").</summary>
        public string Col { get; set; } = string.Empty;
        public string San { get; set; } = string.Empty;
        public string After { get; set; } = string.Empty;
        public string Before { get; set; } = string.Empty;
        public string CommentAfter { get; internal set; } = string.Empty;
        public string CommentBefore { get; internal set; } = string.Empty;
        public string CommentVariations { get; internal set; } = string.Empty;

        public bool IsKey { get; set; }
        public List<JsonDraw> Draws { get; set; } = [];
    }

    public class JsonDraw
    {
        public string Object { get; set; } = string.Empty;
        public string Start { get; set; } = string.Empty;
        public string End { get; set; } = string.Empty;
        public string Color { get; set; } = string.Empty;
        public string Move { get; set; } = string.Empty;
        public string Index { get; set; } = string.Empty;
    }

    public class JsonMoveItem
    {
        public string State { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public string Val { get; set; } = string.Empty;
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

                    comment = string.Join(Environment.NewLine, innerList.Select(x => x.CommentAfter) ?? [""]);
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

                    comment = string.Join(Environment.NewLine, innerList ?? [""]);
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
        public string GetVariationPgn(string branchFen)
        {
            if (Key != "V" || Val == null || Val.Value.ValueKind != JsonValueKind.Array)
                return "";

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
                }
                cur.Add(item);
            }
            if (cur.Count > 0) clusters.Add(cur);

            // ---- Phase 2: jeden Cluster nachspielen → Variante oder Kommentar ----
            var parts = new List<string>();
            foreach (var cluster in clusters)
            {
                ChessGame? game = TryNewGame(branchFen);
                var body = new StringBuilder();         // gültige Varianten-Notation
                var rawText = new StringBuilder();       // Fallback-Klartext (Kommentar)
                bool anyMove = false, legal = true, hasNull = false;

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
                        string raw = ((item.Val?.ValueKind == JsonValueKind.String ? item.Val.Value.GetString() : "") ?? "").Trim();
                        if (raw == "") continue;
                        anyMove = true;
                        AppendText(rawText, raw);
                        if (raw.Contains("--")) { hasNull = true; continue; }
                        if (game != null && legal && !hasNull)
                        {
                            var mv = Game.SanToMove(game, StripMoveNumber(raw));
                            if (mv != null)
                            {
                                try { game.MakeMove(mv, false); body.Append(raw + " "); }
                                catch { legal = false; }
                            }
                            else legal = false;
                        }
                    }
                }

                if (anyMove && legal && !hasNull)
                {
                    string b = body.ToString().Trim();
                    if (b != "") parts.Add($"({b})");
                }
                else
                {
                    string t = rawText.ToString().Trim();
                    if (t != "") parts.Add($"{{{t}}}");
                }
            }
            return string.Join(" ", parts);
        }

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
                    string s = ((it.Val?.ValueKind == JsonValueKind.String ? it.Val.Value.GetString() : "") ?? "").Trim();
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
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(s);
        }

        [GeneratedRegex(@"^(\d+)\.(?!\.)")]
        private static partial Regex findWhiteMoveNumber();

        [GeneratedRegex(@"^(\d+)\.\.\.")]
        private static partial Regex findBlackMoveNumber();

        [GeneratedRegex(@"^\d+\.(\.\.)?\s*")]
        private static partial Regex findLeadingMoveNumber();

        [GeneratedRegex("<[^>]*>")]
        private static partial Regex findHtmltags();

        [GeneratedRegex(@"@@StartFEN@@(.+?)@@EndFEN@@")]
        private static partial Regex findFenTags();

    }

    public partial class ResponseLogin
    {
        public string Jwt { get; set; } = string.Empty;

        public int Uid
        {
            get
            {
                return JwtHelper.ExtractUidFromToken(Jwt);
            }
        }
    }

    public partial class ResponseChapterList
    {
        public JsonHomeData HomeData { get; set; } = new();
    }

    public class JsonHomeData
    {
        public List<JsonBook> BooksList { get; set; } = [];
    }

    public class JsonBook
    {
        public int Bid { get; set; }
        public string Name { get; set; } = string.Empty;
    }
    public class RestResponseLine
    {
        /// <summary>Globale Chessable-Linien-ID (oid). Schlüssel für den per-Linie-Cache
        /// (CachedRawLines) → erlaubt es, im Kurs-Cache nur die Referenz statt des Inhalts abzulegen.</summary>
        public int Oid { get; set; }
        public string? LineJsonContent { get; set; }
    }
    public class RestResponseChapter
    {
        public string? ChapterJsonContent { get; set; }
        public List<RestResponseLine> ResponseLineList { get; set; } = [];
    } 
    public class RestResponseCourse
    {
        public string? CourseJsonContent { get; set; } 
        public List<RestResponseChapter> ChapterList { get; set; } = [];
    }
}
