namespace piratechess_lib
{
    // Chessable getCourse/getList/getHomeData: Kurs-, Kapitel- und Bücherlisten (reine DTOs; aus Models.cs verschoben, Review 2026-09-29 S2-016).
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
    public class Line
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
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
}
