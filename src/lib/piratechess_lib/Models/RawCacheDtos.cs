namespace piratechess_lib
{
    // Roh-JSON eines Kurses für den Cache-Pfad (reine DTOs; aus Models.cs verschoben, Review 2026-09-29 S2-016).
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
