using ChessDotNet;

namespace piratechess_lib
{
    /// <summary>Trainingsmarker <c>[%tqu …]</c> vor Key-Zügen der lösenden Seite (erster Key-Zug oder alle). Aus
    /// <c>Game.GeneratePGN</c> herausgelöst (Zerlegung Z48).</summary>
    internal static class TrainingMarkers
    {
        /// <summary>Alle Key-Züge der lösenden Seite ab dem ersten Key-Block bekommen einen Marker (mit UCI).</summary>
        internal static void MarkAllKeyMoves(Game game, SortedList<int, JsonMove> sortedMoves)
        {
            var allUcis = GetAllTrainingUcis(game.Initial, sortedMoves);
            bool prevKey = false;
            bool pastFirstKey = false;
            int moveIdx = 0;
            var fenParts = (game.Initial ?? "").Split(' ');
            bool currentIsWhite = fenParts.Length <= 1 || fenParts[1] != "b";
            bool? solverIsWhite = !string.IsNullOrEmpty(game.Color)
                ? game.Color.Equals("white", StringComparison.OrdinalIgnoreCase)
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

        /// <summary>Nur der erste Key-Zug der lösenden Seite bekommt einen Marker (mit UCI).</summary>
        internal static void MarkFirstKeyMove(Game game, SortedList<int, JsonMove> sortedMoves)
        {
            bool? solverIsWhite = !string.IsNullOrEmpty(game.Color)
                ? game.Color.Equals("white", StringComparison.OrdinalIgnoreCase)
                : null;
            string? uci = GetFirstKeyMoveUci(game.Initial, sortedMoves, solverIsWhite);
            var fenParts = (game.Initial ?? "").Split(' ');
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

        private static List<string?> GetAllTrainingUcis(string initial, SortedList<int, JsonMove> sortedMoves)
        {
            var result = new List<string?>(sortedMoves.Count);
            try
            {
                ChessGame game = string.IsNullOrEmpty(initial)
                    ? new ChessGame()
                    : new ChessGame(initial);

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

        private static string? GetFirstKeyMoveUci(string initial, SortedList<int, JsonMove> sortedMoves, bool? solverIsWhite)
        {
            try
            {
                ChessGame game = string.IsNullOrEmpty(initial)
                    ? new ChessGame()
                    : new ChessGame(initial);

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
