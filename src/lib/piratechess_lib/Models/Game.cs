namespace piratechess_lib
{
    // Chessable getGame: eine Linie (Züge, Ausgangsstellung, Farbe, softFail). Die PGN-Erzeugung steckt in
    // Pgn/GamePgnWriter (aus Models.cs herausgelöst, Zerlegung Z48); GeneratePGN bleibt als Einstieg.
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
        public int DuplicateMoveIds { get; internal set; }

        /// <summary>PGN-Movetext dieser Linie (Kommentare, Trainings-/Info-Marker, Pfeile/Kreise, softFail, Varianten).
        /// Siehe <see cref="GamePgnWriter"/>.</summary>
        public string GeneratePGN(bool allKeyMovesTraining = false, bool noTrainingMove = false)
            => GamePgnWriter.Write(this, allKeyMovesTraining, noTrainingMove);
    }
}
