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
    /// <summary>Zeitpunkt des Übergangs auf completed/failed/cancelled (für die kürzere Terminal-TTL). internal set nur für Tests.</summary>
    public DateTime? TerminalAt { get; internal set; }

    public string Status { get; set; } = "running"; // running | completed | failed | cancelled
    public int ChaptersDone { get; set; }
    public int ChaptersTotal { get; set; }
    public int LinesDone { get; set; }
    /// <summary>Gesamt-Linienzahl (aus getCourse?includeVariations), bekannt schon zu Beginn des
    /// Abrufs → Fortschritts-Nenner + ETA. 0 solange unbekannt.</summary>
    public int LinesTotal { get; set; }
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

    // Abbruch (S2-008): DELETE course/{jobId} bricht den Abruf ab; der Fetch-Worker verknüpft dieses Token mit
    // ApplicationStopping. Eigene CTS ohne Registrierungen und ohne Timer → braucht kein Dispose.
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Wird mit <see cref="Cancel"/> ausgelöst; der Fetch-Worker reicht es bis zum curl-Aufruf durch.</summary>
    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>Atomar: einen laufenden Job auf "cancelled" schalten und seinen Abruf abbrechen. Liefert false,
    /// wenn der Job schon terminal war (dann bleibt sein Status stehen).</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            if (Status != "running") return false;
            Error = "Abgebrochen";
            Status = "cancelled";
            TerminalAt = DateTime.UtcNow;
        }
        // Außerhalb des Gates: Cancel führt die Registrierungen (curl-Kill) synchron aus.
        _cts.Cancel();
        return true;
    }

    /// <summary>Atomar: Ergebnis setzen + auf "completed" schalten (alle Felder unter einer Barriere).
    /// Ein abgebrochener Job bleibt abgebrochen.</summary>
    public void Complete(string pgn, string courseName, int chapterCount, int lineCount)
    {
        lock (_gate)
        {
            if (Status == "cancelled") return;
            Pgn = pgn;
            CourseName = courseName;
            ChapterCount = chapterCount;
            LineCount = lineCount;
            Status = "completed";
            TerminalAt = DateTime.UtcNow;
        }
    }

    /// <summary>Atomar: Fehler setzen + auf "failed" schalten. Ein abgebrochener Job bleibt abgebrochen.</summary>
    public void Fail(string error)
    {
        lock (_gate)
        {
            if (Status == "cancelled") return;
            Error = error;
            Status = "failed";
            TerminalAt = DateTime.UtcNow;
        }
    }

    /// <summary>Konsistenter Schnappschuss für den Poll-Read (Status + zugehörige Felder zusammenhängend).</summary>
    public (string Status, int ChaptersDone, int ChaptersTotal, int LinesDone, int LinesTotal, int ChapterCount, int LineCount, string CourseName, string? Pgn, string? Error) Snapshot()
    {
        lock (_gate)
            return (Status, ChaptersDone, ChaptersTotal, LinesDone, LinesTotal, ChapterCount, LineCount, CourseName, Pgn, Error);
    }
}

/// <summary>Hält laufende/fertige Kurs-Abruf-Jobs im Speicher, je per Job-Id.</summary>
public class CourseFetchJobStore
{
    /// <summary>Terminale (completed/failed/cancelled) Jobs werden so lange aufbewahrt, dass ein normaler
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
            var terminal = snap.Status is "completed" or "failed" or "cancelled";
            var tooOld = nowUtc - job.CreatedAt > MaxJobAge;
            var terminalExpired = terminal && job.TerminalAt is { } t && nowUtc - t > TerminalTtl;
            if ((tooOld || terminalExpired) && _jobs.TryRemove(id, out _)) removed++;
        }

        if (_jobs.Count > MaxJobs)
        {
            // Ältester zuerst, terminale vor laufenden (laufende möglichst nicht abwürgen).
            var overflow = _jobs
                .OrderByDescending(kv => kv.Value.Snapshot().Status is "completed" or "failed" or "cancelled")
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
