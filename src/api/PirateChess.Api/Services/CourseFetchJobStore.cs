using System.Collections.Concurrent;

namespace PirateChess.Api.Services;

/// <summary>
/// Zustand eines asynchronen tiefen Kurs-Abrufs (für den stateless rookhub-Poll). In-Memory:
/// geht bei einem piratechess-Neustart verloren — der Aufrufer (rookhub) startet dann einfach neu.
/// </summary>
public class CourseFetchJob
{
    /// <summary>Anlage-Zeitpunkt (für TTL/Reaping im <see cref="CourseFetchJobStore"/>). internal set nur für Tests.</summary>
    public DateTime CreatedAt { get; internal set; } = DateTime.UtcNow;
    /// <summary>Zeitpunkt des Übergangs auf completed/failed (für die kürzere Terminal-TTL). internal set nur für Tests.</summary>
    public DateTime? TerminalAt { get; internal set; }

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
            TerminalAt = DateTime.UtcNow;
        }
    }

    /// <summary>Atomar: Fehler setzen + auf "failed" schalten.</summary>
    public void Fail(string error)
    {
        lock (_gate)
        {
            Error = error;
            Status = "failed";
            TerminalAt = DateTime.UtcNow;
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
    /// <summary>Terminale (completed/failed) Jobs werden so lange aufbewahrt, dass ein normaler
    /// rookhub-Poll das Ergebnis (PGN) noch abholen kann; danach freigegeben.</summary>
    public static readonly TimeSpan TerminalTtl = TimeSpan.FromMinutes(30);
    /// <summary>Harte Obergrenze für JEDEN Job (auch „running") gegen steckengebliebene/verwaiste Einträge.</summary>
    public static readonly TimeSpan MaxJobAge = TimeSpan.FromHours(6);
    /// <summary>Notbremse: nie mehr als so viele Jobs halten (ältester terminaler zuerst raus).</summary>
    public const int MaxJobs = 500;

    private readonly ConcurrentDictionary<string, CourseFetchJob> _jobs = new();

    public CourseFetchJob Create(string id)
    {
        Prune(DateTime.UtcNow);   // Lazy-Reaping: jeder neue Job räumt verwaiste/alte Einträge ab.
        var job = new CourseFetchJob();
        _jobs[id] = job;
        return job;
    }

    public CourseFetchJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public void Remove(string id) => _jobs.TryRemove(id, out _);

    public int Count => _jobs.Count;

    /// <summary>
    /// Entfernt verwaiste Jobs: terminale älter als <see cref="TerminalTtl"/>, JEDEN älter als
    /// <see cref="MaxJobAge"/>, und — falls weiterhin über <see cref="MaxJobs"/> — die ältesten
    /// (terminale zuerst), bis das Limit eingehalten ist. Gibt die Anzahl entfernter Jobs zurück.
    /// <paramref name="nowUtc"/> ist Parameter (testbar ohne Wall-Clock).
    /// </summary>
    public int Prune(DateTime nowUtc)
    {
        var removed = 0;
        foreach (var (id, job) in _jobs)
        {
            var snap = job.Snapshot();
            var terminal = snap.Status is "completed" or "failed";
            var tooOld = nowUtc - job.CreatedAt > MaxJobAge;
            var terminalExpired = terminal && job.TerminalAt is { } t && nowUtc - t > TerminalTtl;
            if ((tooOld || terminalExpired) && _jobs.TryRemove(id, out _)) removed++;
        }

        if (_jobs.Count > MaxJobs)
        {
            // Ältester zuerst, terminale vor laufenden (laufende möglichst nicht abwürgen).
            var overflow = _jobs
                .OrderByDescending(kv => kv.Value.Snapshot().Status is "completed" or "failed")
                .ThenBy(kv => kv.Value.CreatedAt)
                .Take(_jobs.Count - MaxJobs)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in overflow)
                if (_jobs.TryRemove(id, out _)) removed++;
        }

        return removed;
    }
}
