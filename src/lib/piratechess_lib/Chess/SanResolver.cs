using ChessDotNet;
using ChessDotNet.Pieces;

namespace piratechess_lib
{
    /// <summary>
    /// SAN-Zug → legaler Zug in einer Stellung (ChessDotNet), ohne zu werfen. Aus <c>Game</c> herausgelöst (Zerlegung Z48);
    /// genutzt von der PGN-Erzeugung (Trainings-UCI, Hauptlinien-Stellungen) und der Varianten-Auflösung.
    /// </summary>
    internal static class SanResolver
    {
        /// <summary>Brett aus einer FEN, ohne zu werfen (Chessable liefert auch Muster-Diagramme ohne König).</summary>
        internal static ChessGame? TryNewGame(string? fen)
        {
            try { return string.IsNullOrWhiteSpace(fen) ? new ChessGame() : new ChessGame(fen); }
            catch { return null; }
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

        /// <summary>UCI eines aufgelösten Zugs („e2e4", bei Umwandlung mit Figur aus dem SAN: „e7e8q").</summary>
        internal static string ToUci(Move move, string san)
        {
            char ff = char.ToLower(move.OriginalPosition.File.ToString()[0]);
            int fr = move.OriginalPosition.Rank;
            char tf = char.ToLower(move.NewPosition.File.ToString()[0]);
            int tr = move.NewPosition.Rank;
            string uciStr = $"{ff}{fr}{tf}{tr}";
            int eqIdx = san.IndexOf('=');
            if (eqIdx >= 0 && eqIdx + 1 < san.Length)
                uciStr += char.ToLower(san[eqIdx + 1]);
            return uciStr;
        }
    }
}
