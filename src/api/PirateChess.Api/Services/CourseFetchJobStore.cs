using System.Collections.Concurrent;

namespace PirateChess.Api.Services;

/// <summary>
/// Zustand eines asynchronen tiefen Kurs-Abrufs (für den stateless rookhub-Poll). In-Memory:
/// geht bei einem piratechess-Neustart verloren — der Aufrufer (rookhub) startet dann einfach neu.
/// </summary>
public class CourseFetchJob
{
    public string Status { get; set; } = "running"; // running | completed | failed
    public int ChaptersDone { get; set; }
    public int ChaptersTotal { get; set; }
    public int LinesDone { get; set; }
    public int ChapterCount { get; set; }
    public int LineCount { get; set; }
    public string CourseName { get; set; } = string.Empty;
    public string? Pgn { get; set; }
    public string? Error { get; set; }

    // Der Job wird vom Fetch-Worker (ThreadPool) geschrieben und vom Poll-Request (anderer Thread)
    // gelesen. Ohne Synchronisation könnte der Leser Status=="completed" sehen, BEVOR Pgn sichtbar ist
    // (Reordering/keine Memory-Barrier) → er liefert ein null-PGN und entfernt den Job → PGN verloren.
    // Der terminale Übergang + der terminale Read laufen daher unter diesem Gate.
    private readonly object _gate = new();

    /// <summary>Atomar: Ergebnis setzen + auf "completed" schalten (alle Felder unter einer Barriere).</summary>
    public void Complete(string pgn, string courseName, int chapterCount, int lineCount)
    {
        lock (_gate)
        {
            Pgn = pgn;
            CourseName = courseName;
            ChapterCount = chapterCount;
            LineCount = lineCount;
            Status = "completed";
        }
    }

    /// <summary>Atomar: Fehler setzen + auf "failed" schalten.</summary>
    public void Fail(string error)
    {
        lock (_gate)
        {
            Error = error;
            Status = "failed";
        }
    }

    /// <summary>Konsistenter Schnappschuss für den Poll-Read (Status + zugehörige Felder zusammenhängend).</summary>
    public (string Status, int ChaptersDone, int ChaptersTotal, int LinesDone, int ChapterCount, int LineCount, string CourseName, string? Pgn, string? Error) Snapshot()
    {
        lock (_gate)
            return (Status, ChaptersDone, ChaptersTotal, LinesDone, ChapterCount, LineCount, CourseName, Pgn, Error);
    }
}

/// <summary>Hält laufende/fertige Kurs-Abruf-Jobs im Speicher, je per Job-Id.</summary>
public class CourseFetchJobStore
{
    private readonly ConcurrentDictionary<string, CourseFetchJob> _jobs = new();

    public CourseFetchJob Create(string id)
    {
        var job = new CourseFetchJob();
        _jobs[id] = job;
        return job;
    }

    public CourseFetchJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public void Remove(string id) => _jobs.TryRemove(id, out _);
}
