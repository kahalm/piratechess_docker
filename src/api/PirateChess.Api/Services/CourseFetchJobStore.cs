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
