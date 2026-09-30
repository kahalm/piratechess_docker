namespace piratechess_lib
{
    // Chessable getGame: Linie, Züge, Pfeile/Kreise, Kommentar-Rohdaten, softFail (reine DTOs; aus Models.cs verschoben, Review 2026-09-29 S2-016).
    public class ResponseLine
    {
        public Game Game { get; set; } = new Game();
    }
    public class ResponseMove
    {
        public string Before { get; set; } = string.Empty;
        public string After { get; set; } = string.Empty;
        public List<JsonMoveItemList> Data { get; set; } = [];
    }
    /// <summary>Pro Vollzug (index = Vollzugnummer ab Linienbeginn) die von Chessable geduldeten
    /// Züge je Seite. Enthält den Hauptzug PLUS die akzeptierten Alternativen (gemeinsame Stellung).
    /// W/B können null sein, wenn die Seite an diesem Zug nicht trainiert wird.</summary>
    public class SoftFailEntry
    {
        public List<string>? W { get; set; }
        public List<string>? B { get; set; }
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
}
