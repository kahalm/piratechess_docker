namespace PirateChess.Api.Services;

/// <summary>
/// Rückmeldungen und Schalter für den tiefen Kursabruf (<see cref="IChessableHttpService.FetchCourseDataAsync"/>);
/// fasst die früheren sieben optionalen Parameter zusammen (Zerlegung Z46). Jede Rückmeldung ist optional.
/// </summary>
public sealed record CourseFetchOptions
{
    /// <summary>Ohne Rückmeldungen, mit Linien-Cache.</summary>
    public static readonly CourseFetchOptions Default = new();

    /// <summary>Vor jedem Kapitel: „{n} / {gesamt}" (1-basiert).</summary>
    public Action<string>? OnChapterProgress { get; init; }

    /// <summary>Nach jeder Linie im Kapitel: „{fertig} / {Linien im Kapitel}".</summary>
    public Action<string>? OnLineProgress { get; init; }

    /// <summary>Nach jeder Linie: Zahl aller bisher fertigen Linien des Kurses (als Text).</summary>
    public Action<string>? OnCumulativeLines { get; init; }

    /// <summary>Retry-Hinweis für Kapitel und Linien (Fortschrittstext).</summary>
    public Action<string>? OnRetry { get; init; }

    /// <summary>Gesamt-Linienzahl aus der Kursstruktur, sobald bekannt (nur wenn &gt; 0).</summary>
    public Action<int>? OnTotalLines { get; init; }

    /// <summary>Force-Refresh: den Linien-Resume-Cache übergehen (statt ihn vorher zu löschen —
    /// bei einem gescheiterten Abruf bliebe sonst nichts übrig).</summary>
    public bool BypassLineCache { get; init; }
}
